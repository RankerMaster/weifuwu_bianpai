using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace WpfApp1
{
    public partial class MainWindow
    {
        private readonly Random _strategyRandom = new Random();
        private readonly List<StrategyLogRow> _strategyLogRows = new List<StrategyLogRow>();
        private readonly List<StrategyPlanItem> _strategyPlanItems = new List<StrategyPlanItem>();
        private double _strategyDiskMaxMb;
        private bool _isAdjustingStrategyThresholdText;

        private void StrategyButton_OnClick(object sender, RoutedEventArgs e)
        {
            ShowStrategyPanelOnly();
            SetSidebarSelected("Strategy");
            RefreshStrategyDeviceSelector(GetSelectedStrategyDeviceName());
            if (StrategyPercentSlider != null)
            {
                StrategyPercentSlider.Value = 0;
            }
            UpdateStrategyThresholdComputedText();
            _ = RefreshStrategyLocalImageListAsync();
        }

        private async void StrategyRefreshButton_OnClick(object sender, RoutedEventArgs e)
        {
            await RefreshRuntimeDataBindingsAsync(true);
            await RefreshStrategyLocalImageListAsync();
        }

        private void ShowStrategyPanelOnly()
        {
            DashboardTopPanel.Visibility = Visibility.Collapsed;
            DashboardMainPanel.Visibility = Visibility.Collapsed;
            ServiceImagePanel.Visibility = Visibility.Collapsed;
            ServiceContainerPanel.Visibility = Visibility.Collapsed;
            StrategyPanel.Visibility = Visibility.Visible;
        }

        private string GetSelectedStrategyDeviceName()
        {
            var selected = StrategyDeviceSelector == null ? null : StrategyDeviceSelector.SelectedItem as ComboBoxItem;
            var device = selected == null ? null : selected.Tag as DeviceInfo;
            return device == null ? string.Empty : device.Name;
        }

        private DeviceInfo GetSelectedStrategyDevice()
        {
            var selected = StrategyDeviceSelector == null ? null : StrategyDeviceSelector.SelectedItem as ComboBoxItem;
            return selected == null ? null : selected.Tag as DeviceInfo;
        }

        private string GetSelectedStrategyContextName()
        {
            var device = GetSelectedStrategyDevice();
            if (device == null)
            {
                return string.Empty;
            }

            string contextName;
            if (_deviceContextMap.TryGetValue(device.Name, out contextName) && !string.IsNullOrWhiteSpace(contextName))
            {
                return contextName;
            }

            return device.ContextName ?? string.Empty;
        }

        private void RefreshStrategyDeviceSelector(string preferredDeviceName)
        {
            if (StrategyDeviceSelector == null)
            {
                return;
            }

            var existingSelectedName = GetSelectedStrategyDeviceName();
            var targetName = string.IsNullOrWhiteSpace(preferredDeviceName) ? existingSelectedName : preferredDeviceName;
            StrategyDeviceSelector.Items.Clear();

            for (var i = 0; i < _devices.Count; i++)
            {
                var d = _devices[i];
                StrategyDeviceSelector.Items.Add(new ComboBoxItem
                {
                    Content = string.Format("{0} ({1})", d.Name, d.Ip),
                    Tag = d
                });
            }

            if (StrategyDeviceSelector.Items.Count == 0)
            {
                StrategyDeviceSelector.Items.Add(new ComboBoxItem
                {
                    Content = "暂无设备",
                    Tag = null
                });
                StrategyDeviceSelector.SelectedIndex = 0;
                return;
            }

            if (!string.IsNullOrWhiteSpace(targetName))
            {
                for (var i = 0; i < StrategyDeviceSelector.Items.Count; i++)
                {
                    var item = StrategyDeviceSelector.Items[i] as ComboBoxItem;
                    var device = item == null ? null : item.Tag as DeviceInfo;
                    if (device != null && string.Equals(device.Name, targetName, StringComparison.OrdinalIgnoreCase))
                    {
                        StrategyDeviceSelector.SelectedIndex = i;
                        return;
                    }
                }
            }

            if (StrategyDeviceSelector.SelectedIndex < 0 && StrategyDeviceSelector.Items.Count > 0)
            {
                StrategyDeviceSelector.SelectedIndex = 0;
            }
        }

        private async void StrategyDeviceSelector_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            await LoadStrategyDeviceDataAsync();
        }

        private async void StrategyReadDiskButton_OnClick(object sender, RoutedEventArgs e)
        {
            await ReadStrategyDiskMaxBySshAsync();
        }

        private async Task ReadStrategyDiskMaxBySshAsync()
        {
            var device = GetSelectedStrategyDevice();
            if (device == null)
            {
                MessageBox.Show("请先选择设备。", "容器策略优化", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var contextName = GetEffectiveStrategyContextName(device);
            long bytes = 0;
            string detail = string.Empty;
            var ok = await Task.Run(() =>
                TryReadDiskMaxByContextOrSsh(contextName, out bytes, out detail) &&
                bytes > 0);
            if (!ok || bytes <= 0)
            {
                StrategyDiskMaxSpaceText.Text = "磁盘最大空间：读取失败";
                var message = string.Format(
                    "设备：{0} ({1})\n上下文：{2}\n\n{3}",
                    device.Name,
                    device.Ip,
                    string.IsNullOrWhiteSpace(contextName) ? "(未识别)" : contextName,
                    string.IsNullOrWhiteSpace(detail) ? "请确认该设备已配置可用的 SSH Docker Context，并可执行 docker info / ssh。" : detail);
                ShowScrollableErrorDialog("读取磁盘最大空间失败。", message);
                return;
            }

            var maxMemoryMb = bytes / (1024d * 1024d);
            _strategyDiskMaxMb = maxMemoryMb;
            if (EdgeMemoryThresholdTextBox != null)
            {
                var currentThreshold = ParseStrategyThresholdMb(EdgeMemoryThresholdTextBox.Text);
                var clampedThreshold = ClampStrategyThresholdMb(currentThreshold);
                SetStrategyThresholdText(clampedThreshold);
            }

            UpdateStrategyThresholdComputedText();
            StrategyDiskMaxSpaceText.Text = string.Format("磁盘最大空间：{0}", FormatStrategyDiskMaxGb(bytes));
        }

        private string GetEffectiveStrategyContextName(DeviceInfo device)
        {
            if (device == null)
            {
                return string.Empty;
            }

            var contextName = GetSelectedStrategyContextName();
            if (!string.IsNullOrWhiteSpace(contextName))
            {
                return contextName;
            }

            var contexts = ReadDockerContexts()
                .Where(IsSshContext)
                .ToList();

            for (var i = 0; i < contexts.Count; i++)
            {
                var ctx = contexts[i];
                var ip = ResolveContextIp(ctx.DockerHost);
                if (!string.IsNullOrWhiteSpace(ip) &&
                    string.Equals(ip, device.Ip, StringComparison.OrdinalIgnoreCase))
                {
                    _deviceContextMap[device.Name] = ctx.Name;
                    return ctx.Name;
                }
            }

            return string.Empty;
        }

        private bool TryReadDiskMaxByContextOrSsh(string contextName, out long diskBytes, out string detail)
        {
            diskBytes = 0;
            detail = string.Empty;
            if (string.IsNullOrWhiteSpace(contextName))
            {
                detail = "未识别到该设备对应的 SSH Docker Context。";
                return false;
            }

            if (TryReadDockerRootDriveTotalBytes(contextName, out diskBytes) && diskBytes > 0)
            {
                return true;
            }

            string sshUser;
            string sshIp;
            string parseDetail;
            if (!TryResolveSshEndpointFromContext(contextName, out sshUser, out sshIp, out parseDetail))
            {
                detail = parseDetail;
                return false;
            }

            string sshOutput;
            if (TryReadDiskMaxBySsh(sshIp, sshUser, "df -B1 --output=size / | tail -1", out diskBytes, out sshOutput) && diskBytes > 0)
            {
                return true;
            }

            detail = string.Format(
                "已识别 context={0} => {1}@{2}，但 SSH 执行失败。\n输出：\n{3}",
                contextName,
                sshUser,
                sshIp,
                string.IsNullOrWhiteSpace(sshOutput) ? "(无输出)" : sshOutput);
            return false;
        }

        private bool TryResolveSshEndpointFromContext(string contextName, out string sshUser, out string sshIp, out string detail)
        {
            sshUser = string.Empty;
            sshIp = string.Empty;
            detail = string.Empty;

            var dockerHost = (ReadDockerHostByContext(contextName) ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(dockerHost))
            {
                detail = string.Format("未能读取 Docker context `{0}` 的 Host。", contextName);
                return false;
            }

            Uri uri;
            if (!Uri.TryCreate(dockerHost, UriKind.Absolute, out uri) ||
                !string.Equals(uri.Scheme, "ssh", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(uri.Host))
            {
                detail = string.Format("context `{0}` 的 Host 不是 ssh://user@ip 格式：{1}", contextName, dockerHost);
                return false;
            }

            var userInfo = uri.UserInfo ?? string.Empty;
            if (string.IsNullOrWhiteSpace(userInfo))
            {
                detail = string.Format("context `{0}` 缺少 SSH 用户名：{1}", contextName, dockerHost);
                return false;
            }

            var user = userInfo;
            var colonIndex = user.IndexOf(':');
            if (colonIndex >= 0)
            {
                user = user.Substring(0, colonIndex);
            }

            if (string.IsNullOrWhiteSpace(user))
            {
                detail = string.Format("context `{0}` 的 SSH 用户名无效：{1}", contextName, dockerHost);
                return false;
            }

            sshUser = user.Trim();
            sshIp = uri.Host.Trim();
            return true;
        }

        private bool TryReadDiskMaxBySsh(string ip, string user, string command, out long diskBytes, out string output)
        {
            diskBytes = 0;
            output = string.Empty;
            try
            {
                var sshArgs = string.Format("{0}@{1} \"{2}\"", user, ip, command.Replace("\"", "\\\""));
                var sshBins = new[]
                {
                    @"C:\Windows\Sysnative\OpenSSH\ssh.exe",
                    @"C:\Windows\System32\OpenSSH\ssh.exe",
                    @"C:\Windows\SysWOW64\OpenSSH\ssh.exe",
                    "ssh"
                };

                var allOutputs = new List<string>();
                for (var i = 0; i < sshBins.Length; i++)
                {
                    var sshBin = sshBins[i];
                    if (!TryExecSshAndReadBytes(sshBin, sshArgs, out diskBytes, out output))
                    {
                        allOutputs.Add(string.Format("[{0}] {1}", sshBin, output));
                        continue;
                    }

                    return true;
                }

                string whereSshOut;
                var whereOk = TryRunCmdCommand("where ssh", out whereSshOut);
                if (whereOk)
                {
                    var whereLines = (whereSshOut ?? string.Empty)
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => (s ?? string.Empty).Trim())
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    for (var i = 0; i < whereLines.Count; i++)
                    {
                        var sshBin = whereLines[i];
                        if (!TryExecSshAndReadBytes(sshBin, sshArgs, out diskBytes, out output))
                        {
                            allOutputs.Add(string.Format("[{0}] {1}", sshBin, output));
                            continue;
                        }

                        return true;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(whereSshOut))
                {
                    allOutputs.Add("[where ssh] " + whereSshOut);
                }

                long cmdBytes;
                string cmdOut;
                if (TryExecSshAndReadBytes("cmd.exe", "/c ssh " + sshArgs, out cmdBytes, out cmdOut))
                {
                    diskBytes = cmdBytes;
                    output = cmdOut;
                    return true;
                }
                allOutputs.Add("[cmd /c ssh] " + cmdOut);

                output = string.Join(Environment.NewLine, allOutputs.Where(s => !string.IsNullOrWhiteSpace(s)));
                return false;
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return false;
            }
        }

        private static bool TryRunCmdCommand(string cmdArgs, out string output)
        {
            output = string.Empty;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c " + cmdArgs,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psi))
                {
                    if (process == null)
                    {
                        output = "无法启动 cmd 进程。";
                        return false;
                    }

                    var stdOutTask = process.StandardOutput.ReadToEndAsync();
                    var stdErrTask = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(10000))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                        }

                        output = "cmd 执行超时。";
                        return false;
                    }

                    Task.WaitAll(stdOutTask, stdErrTask);
                    var stdOut = (stdOutTask.Result ?? string.Empty).Trim();
                    var stdErr = (stdErrTask.Result ?? string.Empty).Trim();
                    output = string.IsNullOrWhiteSpace(stdErr) ? stdOut : (stdOut + Environment.NewLine + stdErr).Trim();
                    return process.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return false;
            }
        }

        private static bool TryExecSshAndReadBytes(string sshFileName, string sshArgs, out long diskBytes, out string output)
        {
            diskBytes = 0;
            output = string.Empty;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = sshFileName,
                    Arguments = sshArgs,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psi))
                {
                    if (process == null)
                    {
                        output = "无法启动 ssh 进程。";
                        return false;
                    }

                    var stdOutTask = process.StandardOutput.ReadToEndAsync();
                    var stdErrTask = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(12000))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                        }

                        output = "ssh 执行超时。";
                        return false;
                    }

                    Task.WaitAll(stdOutTask, stdErrTask);
                    var stdOut = (stdOutTask.Result ?? string.Empty).Trim();
                    var stdErr = (stdErrTask.Result ?? string.Empty).Trim();
                    output = string.IsNullOrWhiteSpace(stdErr) ? stdOut : (stdOut + Environment.NewLine + stdErr).Trim();

                    if (process.ExitCode != 0)
                    {
                        return false;
                    }

                    var lines = stdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    var lastLine = lines.Length == 0 ? stdOut : lines[lines.Length - 1];
                    long value;
                    if (long.TryParse(lastLine.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                    {
                        diskBytes = value;
                        return true;
                    }

                    return false;
                }
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return false;
            }
        }

        private void StrategyThresholdInput_OnChanged(object sender, RoutedEventArgs e)
        {
            if (_isAdjustingStrategyThresholdText)
            {
                return;
            }

            UpdateStrategyThresholdComputedText();
        }

        private void EdgeMemoryThresholdTextBox_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (CanEditStrategyThreshold())
            {
                return;
            }

            e.Handled = true;
        }

        private void EdgeMemoryThresholdTextBox_OnPreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (CanEditStrategyThreshold())
            {
                return;
            }

            e.Handled = true;
        }

        private bool CanEditStrategyThreshold()
        {
            if (_strategyDiskMaxMb > 0)
            {
                return true;
            }

            MessageBox.Show(
                "请先点击“读取磁盘上限”，读取成功后再输入边缘终端最大内存阈值。",
                "容器策略优化",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            if (StrategyReadDiskButton != null)
            {
                StrategyReadDiskButton.Focus();
            }

            return false;
        }

        private void UpdateStrategyThresholdComputedText()
        {
            if (StrategyPercentSlider == null || StrategyPercentValueText == null || StrategyComputedThresholdText == null)
            {
                return;
            }

            var percent = StrategyPercentSlider.Value;
            StrategyPercentValueText.Text = percent.ToString("0", CultureInfo.InvariantCulture) + "%";

            var thresholdMb = ParseStrategyThresholdMb(EdgeMemoryThresholdTextBox == null ? "0" : EdgeMemoryThresholdTextBox.Text);
            var clampedThreshold = ClampStrategyThresholdMb(thresholdMb);
            if (Math.Abs(clampedThreshold - thresholdMb) > 0.0001d)
            {
                SetStrategyThresholdText(clampedThreshold);
                thresholdMb = clampedThreshold;
            }

            var resultMb = thresholdMb * percent / 100d;
            StrategyComputedThresholdText.Text = string.Format("策略阈值：{0} MB", resultMb.ToString("0.##", CultureInfo.InvariantCulture));
        }

        private async Task LoadStrategyDeviceDataAsync()
        {
            var device = GetSelectedStrategyDevice();
            if (device == null)
            {
                _strategyDiskMaxMb = 0;
                StrategyDiskMaxSpaceText.Text = "磁盘最大空间：未读取磁盘上限";
                SetStrategyThresholdText(0);
                if (StrategyPercentSlider != null)
                {
                    StrategyPercentSlider.Value = 0;
                }
                StrategyLogDataGrid.ItemsSource = new List<StrategyLogRow>();
                _strategyLogRows.Clear();
                return;
            }

            _strategyDiskMaxMb = 0;
            StrategyDiskMaxSpaceText.Text = "磁盘最大空间：未读取磁盘上限";
            SetStrategyThresholdText(0);
            if (StrategyPercentSlider != null)
            {
                StrategyPercentSlider.Value = 0;
            }
            await EnsureAndLoadStrategyCsvAsync(device);
        }

        private async Task TryReadDiskByDockerContextAsync()
        {
            var contextName = GetEffectiveStrategyContextName(GetSelectedStrategyDevice());
            if (string.IsNullOrWhiteSpace(contextName))
            {
                StrategyDiskMaxSpaceText.Text = "磁盘最大空间：未知";
                return;
            }

            long totalBytes = 0;
            string detail;
            var ok = await Task.Run(() => TryReadDiskMaxByContextOrSsh(contextName, out totalBytes, out detail));
            StrategyDiskMaxSpaceText.Text = ok && totalBytes > 0
                ? string.Format("磁盘最大空间：{0}", FormatStrategyDiskMaxGb(totalBytes))
                : "磁盘最大空间：未知";
        }

        private static string FormatStrategyDiskMaxGb(long bytes)
        {
            if (bytes <= 0)
            {
                return "0GB";
            }

            var gb = bytes / (1024d * 1024d * 1024d);
            return gb.ToString("0.##", CultureInfo.InvariantCulture) + "GB";
        }

        private double ParseStrategyThresholdMb(string rawText)
        {
            double value;
            var text = (rawText ?? string.Empty).Trim();
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                !double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            {
                value = 0;
            }

            if (value < 0)
            {
                value = 0;
            }

            return value;
        }

        private double ClampStrategyThresholdMb(double thresholdMb)
        {
            var value = thresholdMb < 0 ? 0 : thresholdMb;
            if (_strategyDiskMaxMb > 0 && value > _strategyDiskMaxMb)
            {
                value = _strategyDiskMaxMb;
            }

            return value;
        }

        private void SetStrategyThresholdText(double thresholdMb)
        {
            if (EdgeMemoryThresholdTextBox == null)
            {
                return;
            }

            var text = thresholdMb.ToString("0.##", CultureInfo.InvariantCulture);
            var current = (EdgeMemoryThresholdTextBox.Text ?? string.Empty).Trim();
            if (string.Equals(current, text, StringComparison.Ordinal))
            {
                return;
            }

            _isAdjustingStrategyThresholdText = true;
            try
            {
                EdgeMemoryThresholdTextBox.Text = text;
            }
            finally
            {
                _isAdjustingStrategyThresholdText = false;
            }
        }

        private async Task EnsureAndLoadStrategyCsvAsync(DeviceInfo device)
        {
            var path = GetStrategyCsvPath(device.Ip);
            if (!File.Exists(path))
            {
                await GenerateStrategyCsvForDeviceAsync(path);
            }

            _strategyLogRows.Clear();
            _strategyLogRows.AddRange(ReadStrategyCsv(path));
            StrategyLogDataGrid.ItemsSource = null;
            StrategyLogDataGrid.ItemsSource = _strategyLogRows.ToList();
        }

        private async Task RefreshStrategyLocalImageListAsync()
        {
            var localImages = await Task.Run(() => ReadDockerImages(null));
            var cards = BuildStrategyCardsFromDockerImages(localImages);
            _strategyImages.Clear();
            _strategyImages.AddRange(cards);
            StrategyImageItemsControl.ItemsSource = null;
            StrategyImageItemsControl.ItemsSource = _strategyImages.ToList();
        }

        private List<DeployImageCard> BuildStrategyCardsFromDockerImages(List<DockerImageInfo> images)
        {
            var cards = new List<DeployImageCard>();
            if (images == null)
            {
                return cards;
            }

            for (var i = 0; i < images.Count; i++)
            {
                var img = images[i];
                if (img == null || string.IsNullOrWhiteSpace(img.Id))
                {
                    continue;
                }

                var repository = (img.Repository ?? string.Empty).Trim();
                var tag = (img.Tag ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(repository) ||
                    string.IsNullOrWhiteSpace(tag) ||
                    string.Equals(repository, "<none>", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tag, "<none>", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                cards.Add(new DeployImageCard(
                    repository + ":" + tag,
                    ShortId(img.Id, 16),
                    img.CreatedAt,
                    img.Size));
            }

            return cards
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Tag, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void WriteImagePlanCsvFromLocalImages(string csvPath, List<DockerImageInfo> localImages)
        {
            var directory = Path.GetDirectoryName(csvPath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var sb = new StringBuilder();
            sb.AppendLine("镜像ID,镜像大小,镜像使用次数,镜像使用间隔(随机生成，没有单位)");
            for (var i = 0; i < localImages.Count; i++)
            {
                var img = localImages[i];
                if (img == null || string.IsNullOrWhiteSpace(img.Id))
                {
                    continue;
                }

                var repository = (img.Repository ?? string.Empty).Trim();
                var tag = (img.Tag ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(repository) ||
                    string.IsNullOrWhiteSpace(tag) ||
                    string.Equals(repository, "<none>", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tag, "<none>", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var usageCount = 1 + _strategyRandom.Next(1000);
                var usageInterval = Math.Round(0.1 + _strategyRandom.NextDouble() * 499.9, 2);
                var shortImageId = NormalizeImageId(img.Id);
                if (shortImageId.Length > 16)
                {
                    shortImageId = shortImageId.Substring(0, 16);
                }
                sb.AppendLine(string.Format(
                    "{0},{1},{2},{3}",
                    EscapeCsv(shortImageId),
                    EscapeCsv(img.Size),
                    usageCount.ToString(CultureInfo.InvariantCulture),
                    usageInterval.ToString("0.##", CultureInfo.InvariantCulture)));
            }

            File.WriteAllText(csvPath, sb.ToString(), Encoding.UTF8);
        }

        private async Task GenerateStrategyCsvForDeviceAsync(string csvPath)
        {
            var contextName = GetSelectedStrategyContextName();
            var images = await Task.Run(() => ReadDockerImages(contextName));

            var directory = Path.GetDirectoryName(csvPath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var sb = new StringBuilder();
            sb.AppendLine("镜像ID,镜像大小,镜像使用次数,镜像使用间隔");
            for (var i = 0; i < images.Count; i++)
            {
                var img = images[i];
                if (string.IsNullOrWhiteSpace(img.Id))
                {
                    continue;
                }

                var usageCount = 1 + _strategyRandom.Next(30);
                var usageInterval = 1 + _strategyRandom.Next(100);
                sb.AppendLine(string.Format(
                    "{0},{1},{2},{3}",
                    EscapeCsv(ShortId(img.Id, 16)),
                    EscapeCsv(img.Size),
                    usageCount.ToString(CultureInfo.InvariantCulture),
                    usageInterval.ToString(CultureInfo.InvariantCulture)));
            }

            File.WriteAllText(csvPath, sb.ToString(), Encoding.UTF8);
        }

        private static string EscapeCsv(string value)
        {
            var text = (value ?? string.Empty).Replace("\"", "\"\"");
            if (text.IndexOf(',') >= 0 || text.IndexOf('"') >= 0 || text.IndexOf('\n') >= 0 || text.IndexOf('\r') >= 0)
            {
                return "\"" + text + "\"";
            }

            return text;
        }

        private static string GetStrategyCsvDirectory()
        {
            return Path.Combine(GetDockerBuildWorkspaceRoot(), "strategy_logs");
        }

        private static string GetStrategyCsvPath(string deviceIp)
        {
            var safe = (deviceIp ?? "unknown").Replace(":", "_").Replace("/", "_").Replace("\\", "_");
            return Path.Combine(GetStrategyCsvDirectory(), safe + ".csv");
        }

        private static List<StrategyLogRow> ReadStrategyCsv(string path)
        {
            var result = new List<StrategyLogRow>();
            if (!File.Exists(path))
            {
                return result;
            }

            var lines = File.ReadAllLines(path, Encoding.UTF8);
            for (var i = 1; i < lines.Length; i++)
            {
                var line = (lines[i] ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var parts = line.Split(',');
                if (parts.Length < 4)
                {
                    continue;
                }

                int usageCount;
                int usageInterval;
                if (!int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out usageCount))
                {
                    usageCount = 0;
                }

                if (!int.TryParse(parts[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out usageInterval))
                {
                    usageInterval = 0;
                }

                result.Add(new StrategyLogRow(
                    parts[0].Trim().Trim('"'),
                    parts[1].Trim().Trim('"'),
                    usageCount,
                    usageInterval));
            }

            return result;
        }

        private static string GetProjectRootDirectory()
        {
            var workspace = GetDockerBuildWorkspaceRoot();
            if (string.IsNullOrWhiteSpace(workspace))
            {
                return AppDomain.CurrentDomain.BaseDirectory;
            }

            var parent = Directory.GetParent(workspace);
            return parent == null ? workspace : parent.FullName;
        }

        private static string GetStrategyImagePlanCsvPath()
        {
            return Path.Combine(GetProjectRootDirectory(), "image_plan_data.csv");
        }

        private static bool TryResolveCuckooTrainScriptPath(out string scriptPath, out List<string> searchedPaths)
        {
            scriptPath = string.Empty;
            searchedPaths = new List<string>();

            const string fixedScriptPath = @"C:\Users\public.DESKTOP-SQDC8E0\Desktop\CASS-科技部验收\bin\Debug\cuckoo_Train1.py";
            searchedPaths.Add(fixedScriptPath);
            if (File.Exists(fixedScriptPath))
            {
                scriptPath = fixedScriptPath;
                return true;
            }

            var candidates = new List<string>();
            void addCandidate(string path)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                var normalized = path.Trim();
                if (!candidates.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                {
                    candidates.Add(normalized);
                }
            }

            void addParentCandidates(string startPath, int maxDepth)
            {
                if (string.IsNullOrWhiteSpace(startPath))
                {
                    return;
                }

                DirectoryInfo dir = null;
                try
                {
                    dir = new DirectoryInfo(startPath);
                    if (!dir.Exists && File.Exists(startPath))
                    {
                        var parentOfFile = Path.GetDirectoryName(startPath);
                        dir = string.IsNullOrWhiteSpace(parentOfFile) ? null : new DirectoryInfo(parentOfFile);
                    }
                }
                catch
                {
                    return;
                }

                var depth = 0;
                while (dir != null && depth++ < maxDepth)
                {
                    addCandidate(Path.Combine(dir.FullName, "cuckoo_Train1.py"));
                    dir = dir.Parent;
                }
            }

            var projectRoot = GetProjectRootDirectory();
            addCandidate(Path.Combine(projectRoot, "cuckoo_Train1.py"));

            var workspaceRoot = GetDockerBuildWorkspaceRoot();
            addCandidate(Path.Combine(workspaceRoot, "cuckoo_Train1.py"));

            addCandidate(Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? string.Empty, "cuckoo_Train1.py"));
            addCandidate(Path.Combine(Environment.CurrentDirectory ?? string.Empty, "cuckoo_Train1.py"));
            addParentCandidates(AppDomain.CurrentDomain.BaseDirectory, 12);
            addParentCandidates(Environment.CurrentDirectory, 12);

            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                searchedPaths.Add(candidate);
                if (File.Exists(candidate))
                {
                    scriptPath = candidate;
                    return true;
                }
            }

            return false;
        }

        private static bool TryRunProcess(string fileName, string arguments, int timeoutMs, out string output)
        {
            output = string.Empty;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psi))
                {
                    if (process == null)
                    {
                        output = "无法启动进程。";
                        return false;
                    }

                    var stdOutTask = process.StandardOutput.ReadToEndAsync();
                    var stdErrTask = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(timeoutMs))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                        }

                        output = "执行超时。";
                        return false;
                    }

                    Task.WaitAll(stdOutTask, stdErrTask);
                    var stdOut = (stdOutTask.Result ?? string.Empty).Trim();
                    var stdErr = (stdErrTask.Result ?? string.Empty).Trim();
                    output = (stdOut + Environment.NewLine + stdErr).Trim();
                    return process.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return false;
            }
        }

        private static string BuildPythonRuntimeStatusText()
        {
            string pythonOutput;
            var pythonOk = TryRunProcess("python", "--version", 8000, out pythonOutput);
            var pythonDetail = string.IsNullOrWhiteSpace(pythonOutput) ? "(无输出)" : pythonOutput;

            string pyOutput;
            var pyOk = TryRunProcess("py", "-3 --version", 8000, out pyOutput);
            var pyDetail = string.IsNullOrWhiteSpace(pyOutput) ? "(无输出)" : pyOutput;

            return string.Join(
                Environment.NewLine,
                "python: " + (pythonOk ? "可用" : "不可用"),
                "python 输出: " + pythonDetail,
                "py: " + (pyOk ? "可用" : "不可用"),
                "py 输出: " + pyDetail);
        }

        private bool TryRunStrategyPython(string csvPath, double budgetMb, out List<string> selectedIds, out string detail)
        {
            selectedIds = new List<string>();
            detail = string.Empty;

            string scriptPath;
            List<string> searchedPaths;
            var pythonStatus = BuildPythonRuntimeStatusText();
            if (!TryResolveCuckooTrainScriptPath(out scriptPath, out searchedPaths))
            {
                var searched = searchedPaths == null || searchedPaths.Count == 0
                    ? "(无候选路径)"
                    : string.Join(Environment.NewLine, searchedPaths.Select(p => " - " + p));
                detail = string.Join(
                    Environment.NewLine,
                    "未找到算法脚本：cuckoo_Train1.py",
                    "已搜索脚本路径：",
                    searched,
                    "python/py 是否可用：",
                    pythonStatus);
                return false;
            }

            var searchedScriptPaths = searchedPaths == null || searchedPaths.Count == 0
                ? "(无候选路径)"
                : string.Join(Environment.NewLine, searchedPaths.Select(p => " - " + p));

            if (!File.Exists(csvPath))
            {
                detail = "未找到输入 CSV：" + csvPath;
                return false;
            }

            Directory.CreateDirectory(GetStrategyCsvDirectory());
            var outputIdsPath = Path.Combine(
                GetStrategyCsvDirectory(),
                "selected_image_ids_" + DateTime.Now.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + ".txt");

            try
            {
                var args = string.Format(
                    "\"{0}\" --mode strategy --input-csv \"{1}\" --budget-mb {2} --output-ids \"{3}\"",
                    scriptPath,
                    csvPath,
                    budgetMb.ToString("0.##", CultureInfo.InvariantCulture),
                    outputIdsPath);

                string output;
                var ok = TryRunProcess("python", args, 120000, out output);
                if (!ok)
                {
                    ok = TryRunProcess("py", "-3 " + args, 120000, out output);
                }

                detail = string.Join(
                    Environment.NewLine,
                    "已搜索脚本路径：",
                    searchedScriptPaths,
                    "python/py 是否可用：",
                    pythonStatus,
                    "执行输出：",
                    string.IsNullOrWhiteSpace(output) ? "(无输出)" : output);
                if (!ok)
                {
                    return false;
                }

                if (!File.Exists(outputIdsPath))
                {
                    detail = (detail + Environment.NewLine + "算法未产出 ID 文件。").Trim();
                    return false;
                }

                selectedIds = File.ReadAllLines(outputIdsPath, Encoding.UTF8)
                    .Select(x => (x ?? string.Empty).Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return selectedIds.Count > 0;
            }
            finally
            {
                TryDeleteFileQuietly(outputIdsPath);
            }
        }

        private static bool IsDockerImageIdMatched(string dockerImageId, string selectedId)
        {
            var a = (dockerImageId ?? string.Empty).Trim();
            var b = (selectedId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            {
                return false;
            }

            var aNoPrefix = a.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? a.Substring("sha256:".Length) : a;
            var bNoPrefix = b.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? b.Substring("sha256:".Length) : b;

            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(aNoPrefix, bNoPrefix, StringComparison.OrdinalIgnoreCase) ||
                   aNoPrefix.StartsWith(bNoPrefix, StringComparison.OrdinalIgnoreCase) ||
                   bNoPrefix.StartsWith(aNoPrefix, StringComparison.OrdinalIgnoreCase);
        }

        private async void StrategyGenerateButton_OnClick(object sender, RoutedEventArgs e)
        {
            var device = GetSelectedStrategyDevice();
            if (device == null)
            {
                MessageBox.Show("请先选择设备。", "容器策略优化", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            double thresholdMb;
            var thresholdText = (EdgeMemoryThresholdTextBox.Text ?? string.Empty).Trim();
            if (!double.TryParse(thresholdText, NumberStyles.Float, CultureInfo.InvariantCulture, out thresholdMb) &&
                !double.TryParse(thresholdText, NumberStyles.Float, CultureInfo.CurrentCulture, out thresholdMb))
            {
                MessageBox.Show("请输入有效的最大内存阈值(MB)。", "容器策略优化", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            thresholdMb = ClampStrategyThresholdMb(thresholdMb);
            SetStrategyThresholdText(thresholdMb);

            var budgetMb = thresholdMb * StrategyPercentSlider.Value / 100d;
            if (budgetMb <= 0)
            {
                MessageBox.Show("策略预算为 0，请先设置阈值和百分比。", "容器策略优化", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var localImages = await Task.Run(() => ReadDockerImages(null));
            if (localImages.Count == 0)
            {
                MessageBox.Show("未读取到本地 Docker 镜像信息。", "容器策略优化", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var csvPath = GetStrategyImagePlanCsvPath();
            WriteImagePlanCsvFromLocalImages(csvPath, localImages);

            var selectedIds = new List<string>();
            var pythonOutput = string.Empty;
            var pyOk = await Task.Run(() => TryRunStrategyPython(csvPath, budgetMb, out selectedIds, out pythonOutput));
            if (!pyOk)
            {
                ShowScrollableErrorDialog("执行 cuckoo_Train1.py 失败。", string.IsNullOrWhiteSpace(pythonOutput) ? "(无输出)" : pythonOutput);
                return;
            }

            _strategyPlanItems.Clear();
            _strategyImages.Clear();
            var usedMb = 0d;

            for (var i = 0; i < selectedIds.Count; i++)
            {
                var selectedId = selectedIds[i];
                var info = localImages.FirstOrDefault(img => IsDockerImageIdMatched(img.Id, selectedId));
                if (info == null)
                {
                    continue;
                }

                var sizeMb = ParseSizeToBytes(info.Size) / (1024d * 1024d);
                if (sizeMb <= 0)
                {
                    continue;
                }

                var repoTag = string.Format("{0}:{1}", info.Repository, info.Tag);
                if (_strategyPlanItems.Any(p => string.Equals(p.RepoTag, repoTag, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var shortId = ShortId(info.Id, 16);
                _strategyPlanItems.Add(new StrategyPlanItem(repoTag, shortId, info.CreatedAt, info.Size));
                _strategyImages.Add(new DeployImageCard(repoTag, shortId, info.CreatedAt, info.Size));
                usedMb += sizeMb;
            }

            StrategyImageItemsControl.ItemsSource = null;
            StrategyImageItemsControl.ItemsSource = _strategyImages;

            MessageBox.Show(
                string.Format("预部署镜像方案已生成：{0} 个镜像。\n阈值预算：{1} MB\n已占用：{2} MB", _strategyImages.Count, budgetMb.ToString("0.##"), usedMb.ToString("0.##")),
                "容器策略优化",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private async void StrategyDeployButton_OnClick(object sender, RoutedEventArgs e)
        {
            var device = GetSelectedStrategyDevice();
            if (device == null)
            {
                MessageBox.Show("请先选择设备。", "容器策略优化", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var contextName = GetEffectiveStrategyContextName(device);
            if (string.IsNullOrWhiteSpace(contextName))
            {
                MessageBox.Show("当前设备无可用 Docker 上下文。", "容器策略优化", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var deployContextName = ResolveFastOperationContext(contextName);

            var currentCards = (_strategyImages ?? new List<DeployImageCard>())
                .Where(x => x != null)
                .Where(x => !string.IsNullOrWhiteSpace(NormalizeRepoTag(x.RepoTag)))
                .GroupBy(x => NormalizeRepoTag(x.RepoTag), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (currentCards.Count == 0)
            {
                MessageBox.Show("当前预部署镜像列表为空，无法执行一键部署。", "容器策略优化", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            List<DeployImageCard> selectedCards;
            if (!TryShowStrategyDeployConfirmDialog(currentCards, out selectedCards))
            {
                return;
            }

            var deployRepoTags = selectedCards
                .Select(x => NormalizeRepoTag(x.RepoTag))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (deployRepoTags.Count == 0)
            {
                MessageBox.Show("未选择需要部署的镜像。", "容器策略优化", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var skipped = 0;
            var deployed = 0;
            var failed = new List<string>();
            Window progressWindow = null;
            TextBlock progressText = null;
            TextBlock progressStageText = null;
            ProgressBar progressBar = null;
            var operationCanceled = false;
            var progressClosedByCode = false;
            try
            {
                progressStageText = new TextBlock
                {
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 0, 8),
                    Text = "阶段：准备中..."
                };
                progressBar = new ProgressBar
                {
                    Minimum = 0,
                    Maximum = 100,
                    Height = 16,
                    Margin = new Thickness(0, 0, 0, 10),
                    Value = 0
                };
                progressText = new TextBlock
                {
                    Text = "正在准备一键部署任务...",
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap
                };
                var panel = new StackPanel
                {
                    Margin = new Thickness(18, 14, 18, 14)
                };
                panel.Children.Add(progressStageText);
                panel.Children.Add(progressBar);
                panel.Children.Add(progressText);
                progressWindow = new Window
                {
                    Title = "正在部署中",
                    Width = 640,
                    Height = 250,
                    ResizeMode = ResizeMode.NoResize,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this,
                    Content = panel
                };
                progressWindow.Closing += (_, __) =>
                {
                    if (!progressClosedByCode)
                    {
                        operationCanceled = true;
                    }
                };
                progressWindow.Show();

                for (var i = 0; i < deployRepoTags.Count; i++)
                {
                    var repoTag = deployRepoTags[i];
                    var itemIndex = i;
                    Action<string, double> updateProgress = (stage, stagePercent) =>
                    {
                        var stageRatio = Math.Max(0, Math.Min(1, stagePercent));
                        var overallPercent = ((itemIndex + stageRatio) * 100d) / Math.Max(1, deployRepoTags.Count);
                        var stageText = string.IsNullOrWhiteSpace(stage) ? "处理中" : stage.Trim();
                        if (progressStageText != null)
                        {
                            progressStageText.Text = string.Format("阶段：{0}（总进度 {1:F0}%）", stageText, overallPercent);
                        }

                        if (progressBar != null)
                        {
                            progressBar.Value = Math.Max(0, Math.Min(100, overallPercent));
                        }

                        SetProgressText(
                            progressText,
                            string.Format(
                                "正在部署中（{0}/{1}）\n当前镜像：{2}\n目标设备：{3}\n部署上下文：{4}\n当前步骤：{5}",
                                itemIndex + 1,
                                deployRepoTags.Count,
                                repoTag,
                                device.Name,
                                deployContextName,
                                stageText));
                    };

                    if (operationCanceled)
                    {
                        return;
                    }

                    updateProgress("开始部署", 0.05);
                    var deployTask = EnsureImageExistsOnRemoteWithDetailAsync(deployContextName, repoTag, updateProgress);
                    var deployCompleted = await Task.WhenAny(deployTask, Task.Delay(90000));
                    var deployError = deployCompleted == deployTask
                        ? await deployTask
                        : "部署失败：执行超时（超过 1 分半，90 秒）。";
                    if (operationCanceled)
                    {
                        return;
                    }
                    if (string.IsNullOrWhiteSpace(deployError))
                    {
                        updateProgress("部署成功", 1);
                        deployed++;
                    }
                    else
                    {
                        updateProgress("部署失败", 1);
                        failed.Add(repoTag + " => " + deployError);
                    }
                }

                // 刷新失败不影响部署结果弹窗
                try
                {
                    await RefreshRuntimeDataBindingsAsync(true);
                    ShowStrategyPanelOnly();
                    SetSidebarSelected("Strategy");
                }
                catch
                {
                }

                if (failed.Count == 0)
                {
                    if (progressStageText != null)
                    {
                        progressStageText.Text = "阶段：部署完成（总进度 100%）";
                    }

                    if (progressBar != null)
                    {
                        progressBar.Value = 100;
                    }

                    SetProgressText(
                        progressText,
                        string.Format(
                            "镜像已推送到目标 Docker。\n设备：{0} ({1})\n上下文：{2}\n\n推送成功：{3}\n已存在跳过：{4}",
                            device.Name,
                            device.Ip,
                            deployContextName,
                            deployed,
                            skipped));
                    await Task.Delay(1500);

                    progressClosedByCode = true;
                    CloseWindowQuietly(ref progressWindow);
                    return;
                }

                progressClosedByCode = true;
                CloseWindowQuietly(ref progressWindow);
                ShowScrollableErrorDialog(
                    string.Format(
                        "镜像推送结果：设备 {0} ({1})，上下文 {2}\n推送成功 {3}，已存在跳过 {4}，失败 {5}",
                        device.Name,
                        device.Ip,
                        deployContextName,
                        deployed,
                        skipped,
                        failed.Count),
                    string.Join("\n", failed));
            }
            catch (Exception ex)
            {
                progressClosedByCode = true;
                CloseWindowQuietly(ref progressWindow);
                ShowScrollableErrorDialog(
                    "一键部署执行异常。",
                    string.Format(
                        "设备：{0} ({1})\n上下文：{2}\n\n异常信息：\n{3}",
                        device.Name,
                        device.Ip,
                        deployContextName,
                        ex.ToString()));
            }
            finally
            {
                if (progressWindow != null)
                {
                    try
                    {
                        progressClosedByCode = true;
                        progressWindow.Close();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private bool TryShowStrategyDeployConfirmDialog(List<DeployImageCard> cards, out List<DeployImageCard> selectedCards)
        {
            selectedCards = new List<DeployImageCard>();
            var confirmedCards = new List<DeployImageCard>();
            var rows = (cards ?? new List<DeployImageCard>())
                .Where(c => c != null)
                .Select(c => new StrategyDeploySelectionRow(c))
                .ToList();
            if (rows.Count == 0)
            {
                return false;
            }

            var root = new Grid
            {
                Margin = new Thickness(12)
            };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var headerPanel = new DockPanel
            {
                LastChildFill = false,
                Margin = new Thickness(0, 0, 0, 8)
            };
            var title = new TextBlock
            {
                Text = "请选择要部署的预部署镜像：",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(title, Dock.Left);
            headerPanel.Children.Add(title);

            var selectAll = new CheckBox
            {
                Content = "全选",
                IsChecked = true,
                VerticalAlignment = VerticalAlignment.Center
            };
            selectAll.Checked += (_, __) =>
            {
                for (var i = 0; i < rows.Count; i++)
                {
                    rows[i].IsSelected = true;
                }
            };
            selectAll.Unchecked += (_, __) =>
            {
                for (var i = 0; i < rows.Count; i++)
                {
                    rows[i].IsSelected = false;
                }
            };
            DockPanel.SetDock(selectAll, Dock.Right);
            headerPanel.Children.Add(selectAll);
            Grid.SetRow(headerPanel, 0);
            root.Children.Add(headerPanel);

            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                IsReadOnly = false,
                SelectionMode = DataGridSelectionMode.Extended,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                RowBackground = System.Windows.Media.Brushes.White,
                AlternatingRowBackground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 250, 252)),
                ItemsSource = rows
            };
            grid.Columns.Add(new DataGridCheckBoxColumn
            {
                Header = "√",
                Width = 52,
                Binding = new Binding("IsSelected")
                {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                }
            });
            grid.Columns.Add(new DataGridTextColumn { Header = "Name", Width = new DataGridLength(1, DataGridLengthUnitType.Star), Binding = new Binding("Name") });
            grid.Columns.Add(new DataGridTextColumn { Header = "Tag", Width = new DataGridLength(1, DataGridLengthUnitType.Star), Binding = new Binding("Tag") });
            grid.Columns.Add(new DataGridTextColumn { Header = "Image ID", Width = new DataGridLength(1, DataGridLengthUnitType.Star), Binding = new Binding("ImageId") });
            grid.Columns.Add(new DataGridTextColumn { Header = "Created", Width = new DataGridLength(1, DataGridLengthUnitType.Star), Binding = new Binding("Created") });
            grid.Columns.Add(new DataGridTextColumn { Header = "Size", Width = new DataGridLength(1, DataGridLengthUnitType.Star), Binding = new Binding("Size") });
            Grid.SetRow(grid, 1);
            root.Children.Add(grid);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            var confirmButton = new Button
            {
                Content = "确认",
                MinWidth = 88,
                Height = 32,
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true
            };
            var cancelButton = new Button
            {
                Content = "取消",
                MinWidth = 88,
                Height = 32,
                IsCancel = true
            };
            buttonPanel.Children.Add(confirmButton);
            buttonPanel.Children.Add(cancelButton);
            Grid.SetRow(buttonPanel, 2);
            root.Children.Add(buttonPanel);

            var dialog = new Window
            {
                Title = "容器策略优化",
                Owner = this,
                Width = 980,
                Height = 560,
                MinWidth = 820,
                MinHeight = 420,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = root
            };

            confirmButton.Click += (_, __) =>
            {
                var selected = rows
                    .Where(r => r.IsSelected)
                    .Select(r => r.Card)
                    .Where(c => c != null)
                    .ToList();
                if (selected.Count == 0)
                {
                    MessageBox.Show("请至少勾选一个镜像。", "容器策略优化", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                confirmedCards = selected;
                dialog.DialogResult = true;
                dialog.Close();
            };
            cancelButton.Click += (_, __) =>
            {
                dialog.DialogResult = false;
                dialog.Close();
            };

            var ok = dialog.ShowDialog() == true;
            if (ok)
            {
                selectedCards = confirmedCards;
            }

            return ok;
        }

        private sealed class StrategyLogRow
        {
            public StrategyLogRow(string imageId, string imageSize, int usageCount, int usageInterval)
            {
                ImageId = imageId;
                ImageSize = imageSize;
                UsageCount = usageCount;
                UsageInterval = usageInterval;
            }

            public string ImageId { get; }
            public string ImageSize { get; }
            public int UsageCount { get; }
            public int UsageInterval { get; }
        }

        private sealed class StrategyPlanItem
        {
            public StrategyPlanItem(string repoTag, string imageId, string createdAt, string size)
            {
                RepoTag = repoTag;
                ImageId = imageId;
                CreatedAt = createdAt;
                Size = size;
            }

            public string RepoTag { get; }
            public string ImageId { get; }
            public string CreatedAt { get; }
            public string Size { get; }
        }

        private sealed class StrategyDeploySelectionRow
        {
            public StrategyDeploySelectionRow(DeployImageCard card)
            {
                Card = card;
                IsSelected = true;
            }

            public DeployImageCard Card { get; }
            public bool IsSelected { get; set; }
            public string Name => Card == null ? string.Empty : Card.Name;
            public string Tag => Card == null ? string.Empty : Card.Tag;
            public string ImageId => Card == null ? string.Empty : Card.ImageId;
            public string Created => Card == null ? string.Empty : Card.Created;
            public string Size => Card == null ? string.Empty : Card.Size;
        }
    }
}
