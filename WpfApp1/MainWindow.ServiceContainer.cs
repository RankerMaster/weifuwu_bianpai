using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace WpfApp1
{
    public partial class MainWindow
    {
        private readonly object _containerRefreshStateLock = new object();
        private readonly Dictionary<string, int> _containerRefreshVersions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CancellationTokenSource> _containerRefreshCancellations = new Dictionary<string, CancellationTokenSource>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _containerDeviceOnline = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _containerIdsInOperation = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private async void ServiceContainerButton_OnClick(object sender, RoutedEventArgs e)
        {
            ShowServiceContainerPanel();
            SetSidebarSelected("ServiceContainer");

            if (!_hasReadDeviceInfo)
            {
                SetContainerPageState("尚未读取设备信息", false, false);
                BindContainerRows(GetSelectedContainerDeviceName());
                return;
            }

            if (_devices.Count == 0 || ContainerDeviceSelector.Items.Count == 0)
            {
                _suppressContainerDeviceSelectionRefresh = true;
                try
                {
                    await RefreshRuntimeDataBindingsAsync(true);
                }
                finally
                {
                    _suppressContainerDeviceSelectionRefresh = false;
                }
            }

            await RefreshSelectedContainerDeviceAsync();
        }

        private async void ContainerRetryButton_OnClick(object sender, RoutedEventArgs e)
        {
            await RefreshSelectedContainerDeviceAsync();
        }

        private async Task RefreshSelectedContainerDeviceAsync()
        {
            var deviceName = GetSelectedContainerDeviceName();
            var contextName = GetSelectedContainerContextName();

            BindContainerRows(deviceName);
            if (string.IsNullOrWhiteSpace(deviceName) || string.IsNullOrWhiteSpace(contextName))
            {
                SetContainerPageState("未找到可验证的设备", false, false);
                return;
            }

            SetContainerPageState("缓存数据，正在验证设备", false, false);
            var summaryVersion = InvalidateContainerRefresh(contextName);
            var query = await Task.Run(() => QueryContainerSummary(contextName));

            if (!string.Equals(deviceName, GetSelectedContainerDeviceName(), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!IsContainerRefreshVersionCurrent(contextName, summaryVersion))
            {
                return;
            }

            if (!query.Success)
            {
                InvalidateContainerRefresh(contextName);
                SetContainerDeviceOnline(contextName, false);
                SetContainerPageState("设备离线，正在显示只读缓存数据", false, true);
                return;
            }

            ApplyContainerSummary(contextName, deviceName, query.Containers);
            SetContainerDeviceOnline(contextName, true);
            SetContainerPageState(query.Containers.Count == 0 ? "暂无容器" : "设备在线，概要已同步", true, false);

            var refreshState = BeginContainerDetailRefresh(contextName);
            StartContainerDetailsRefresh(contextName, deviceName, query.Containers, refreshState.Item1, refreshState.Item2);
        }

        private ContainerSummaryQueryResult QueryContainerSummary(string contextName)
        {
            string output;
            const string command = "ps -a --no-trunc --format \"{{.ID}}|{{.Names}}|{{.Image}}|{{.Status}}\"";
            if (!TryRunDockerCommand(command, contextName, out output, 10000))
            {
                return ContainerSummaryQueryResult.Failed(output);
            }

            var containers = new List<DockerContainerInfo>();
            var lines = (output ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < lines.Length; i++)
            {
                var parts = lines[i].Split('|');
                if (parts.Length < 4 || string.IsNullOrWhiteSpace(parts[0]))
                {
                    return ContainerSummaryQueryResult.Failed("容器概要查询返回格式无效。");
                }

                var status = parts[3].Trim();
                containers.Add(new DockerContainerInfo(
                    parts[0].Trim(),
                    parts[1].Trim(),
                    parts[2].Trim(),
                    string.Empty,
                    status,
                    InferContainerStateFromStatus(status),
                    "-"));
            }

            return ContainerSummaryQueryResult.Succeeded(containers);
        }

        private void ApplyContainerSummary(string contextName, string deviceName, List<DockerContainerInfo> containers)
        {
            InvalidateContainerRefresh(contextName);
            var remoteRows = containers ?? new List<DockerContainerInfo>();
            var remoteIds = new HashSet<string>(remoteRows.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);

            _containerRows.RemoveAll(row =>
                string.Equals(row.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase) &&
                !remoteIds.Contains(row.FullId ?? string.Empty));

            for (var i = 0; i < remoteRows.Count; i++)
            {
                var container = remoteRows[i];
                var index = _containerRows.FindIndex(row =>
                    string.Equals(row.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(row.FullId, container.Id, StringComparison.OrdinalIgnoreCase));
                var normalizedName = container.Name.StartsWith("/", StringComparison.Ordinal) ? container.Name : "/" + container.Name;

                if (index < 0)
                {
                    _containerRows.Add(new ContainerComposeRow(
                        deviceName,
                        ShortContainerId(container.Id),
                        normalizedName,
                        BuildChineseContainerName(container.Name),
                        NormalizeImageNameWithTag(container.Image),
                        container.State,
                        "-", "-", "-", "-", "-", "-",
                        container.Status,
                        container.Id,
                        "-"));
                    continue;
                }

                var current = _containerRows[index];
                if (!string.Equals(current.Name, normalizedName, StringComparison.Ordinal) ||
                    !string.Equals(current.Image, NormalizeImageNameWithTag(container.Image), StringComparison.Ordinal) ||
                    !string.Equals(current.Status, container.State, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(current.Detail, container.Status, StringComparison.Ordinal))
                {
                    _containerRows[index] = new ContainerComposeRow(
                        current.DeviceName,
                        current.Id,
                        normalizedName,
                        BuildChineseContainerName(container.Name),
                        NormalizeImageNameWithTag(container.Image),
                        container.State,
                        current.Ports,
                        current.CpuCores,
                        current.CpuPercent,
                        current.MemoryUsage,
                        current.MemoryPercent,
                        current.DiskReadWrite,
                        container.Status,
                        current.FullId,
                        current.Size);
                }
            }

            BindContainerRows(GetSelectedContainerDeviceName());
        }

        private Tuple<int, CancellationToken> BeginContainerDetailRefresh(string contextName)
        {
            lock (_containerRefreshStateLock)
            {
                CancellationTokenSource previous;
                if (_containerRefreshCancellations.TryGetValue(contextName, out previous))
                {
                    previous.Cancel();
                    previous.Dispose();
                }

                int currentVersion;
                _containerRefreshVersions.TryGetValue(contextName, out currentVersion);
                var nextVersion = currentVersion + 1;
                var source = new CancellationTokenSource();
                _containerRefreshVersions[contextName] = nextVersion;
                _containerRefreshCancellations[contextName] = source;
                return Tuple.Create(nextVersion, source.Token);
            }
        }

        private int InvalidateContainerRefresh(string contextName)
        {
            lock (_containerRefreshStateLock)
            {
                CancellationTokenSource source;
                if (_containerRefreshCancellations.TryGetValue(contextName, out source))
                {
                    source.Cancel();
                    source.Dispose();
                    _containerRefreshCancellations.Remove(contextName);
                }

                int version;
                _containerRefreshVersions.TryGetValue(contextName, out version);
                var nextVersion = version + 1;
                _containerRefreshVersions[contextName] = nextVersion;
                return nextVersion;
            }
        }

        private bool IsContainerRefreshVersionCurrent(string contextName, int version)
        {
            lock (_containerRefreshStateLock)
            {
                int currentVersion;
                return _containerRefreshVersions.TryGetValue(contextName, out currentVersion) && currentVersion == version;
            }
        }

        private bool IsContainerRefreshCurrent(string contextName, int version, CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                return false;
            }

            lock (_containerRefreshStateLock)
            {
                int currentVersion;
                return _containerRefreshVersions.TryGetValue(contextName, out currentVersion) && currentVersion == version;
            }
        }

        private async void StartContainerDetailsRefresh(
            string contextName,
            string deviceName,
            List<DockerContainerInfo> containers,
            int version,
            CancellationToken token)
        {
            try
            {
                var statsById = await Task.Run(() => ReadDockerContainerStats(contextName));
                if (!IsContainerRefreshCurrent(contextName, version, token))
                {
                    return;
                }

                var hostCpuLimitText = await Task.Run(() => ReadHostCpuLimitText(contextName));
                for (var i = 0; i < containers.Count; i++)
                {
                    if (!IsContainerRefreshCurrent(contextName, version, token))
                    {
                        return;
                    }

                    var container = containers[i];
                    var detail = await Task.Run(() => ReadContainerDetail(contextName, container, statsById, hostCpuLimitText));
                    if (!IsContainerRefreshCurrent(contextName, version, token))
                    {
                        return;
                    }

                    await Dispatcher.InvokeAsync(() => ApplyContainerDetailRow(contextName, deviceName, container.Id, detail, version, token));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Container details refresh failed: " + ex.Message);
            }
        }

        private ContainerDetailResult ReadContainerDetail(
            string contextName,
            DockerContainerInfo container,
            Dictionary<string, DockerContainerStatsInfo> statsById,
            string hostCpuLimitText)
        {
            DockerContainerStatsInfo stats;
            var hasStats = TryGetContainerStats(statsById, container.Id, out stats);
            var limits = ReadContainerLimitInfo(contextName, container.Id);
            var ports = ReadContainerPorts(contextName, container.Id);
            string sizeOutput;
            var size = "-";
            var sizeCommand = string.Format("container inspect --size \"{0}\" --format \"{{{{.SizeRw}}}}\"", container.Id);
            if (TryRunDockerCommand(sizeCommand, contextName, out sizeOutput, 10000))
            {
                long sizeBytes;
                if (long.TryParse((sizeOutput ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out sizeBytes) && sizeBytes >= 0)
                {
                    size = FormatBytes(sizeBytes);
                }
            }

            var memoryUsage = hasStats ? stats.MemoryUsageText : "-";
            return new ContainerDetailResult(
                string.IsNullOrWhiteSpace(ports) ? "-" : ports,
                BuildCpuCoresDisplayText(limits.CpuCoresText, hostCpuLimitText),
                hasStats ? stats.CpuPercentText : "-",
                MergeMemoryUsageWithLimit(memoryUsage, limits.MemoryLimitText),
                hasStats ? stats.MemoryPercentText : "-",
                hasStats ? stats.BlockIoText : "-",
                size);
        }

        private void ApplyContainerDetailRow(
            string contextName,
            string deviceName,
            string fullId,
            ContainerDetailResult detail,
            int version,
            CancellationToken token)
        {
            if (!IsContainerRefreshCurrent(contextName, version, token))
            {
                return;
            }

            var index = _containerRows.FindIndex(row =>
                string.Equals(row.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(row.FullId, fullId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return;
            }

            var current = _containerRows[index];
            _containerRows[index] = new ContainerComposeRow(
                current.DeviceName, current.Id, current.Name, current.ChineseName, current.Image, current.Status,
                detail.Ports, detail.CpuCores, detail.CpuPercent, detail.MemoryUsage, detail.MemoryPercent,
                detail.BlockIo, current.Detail, current.FullId, detail.Size);

            if (string.Equals(GetSelectedContainerDeviceName(), deviceName, StringComparison.OrdinalIgnoreCase))
            {
                BindContainerRows(deviceName);
            }
        }

        private async Task<bool> ConfirmContainerOperationAsync(string contextName, IList<ContainerComposeRow> targetRows)
        {
            var deviceName = GetDeviceNameByContextName(contextName);
            var summaryVersion = InvalidateContainerRefresh(contextName);
            var query = await Task.Run(() => QueryContainerSummary(contextName));
            if (!IsContainerRefreshVersionCurrent(contextName, summaryVersion))
            {
                return false;
            }

            if (!query.Success)
            {
                InvalidateContainerRefresh(contextName);
                SetContainerDeviceOnline(contextName, false);
                SetContainerPageState("设备离线，操作已取消；当前显示只读缓存数据", false, true);
                return false;
            }

            SetContainerDeviceOnline(contextName, true);
            ApplyContainerSummary(contextName, deviceName, query.Containers);
            SetContainerPageState(query.Containers.Count == 0 ? "暂无容器" : "设备在线，概要已同步", true, false);

            if (targetRows == null || targetRows.Count == 0)
            {
                return true;
            }

            var existingIds = new HashSet<string>(query.Containers.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
            if (targetRows.Any(row => string.IsNullOrWhiteSpace(row.FullId) || !existingIds.Contains(row.FullId)))
            {
                MessageBox.Show(this, "所选容器已不存在或缺少完整 ID，请刷新后重试。", "容器编排", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            return true;
        }

        private bool TryLockContainerRows(IEnumerable<ContainerComposeRow> rows, out List<string> lockedIds)
        {
            lockedIds = (rows ?? Enumerable.Empty<ContainerComposeRow>())
                .Select(row => row.FullId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            lock (_containerIdsInOperation)
            {
                if (lockedIds.Any(id => _containerIdsInOperation.Contains(id)))
                {
                    return false;
                }

                foreach (var id in lockedIds)
                {
                    _containerIdsInOperation.Add(id);
                }
            }

            return true;
        }

        private void UnlockContainerRows(IEnumerable<string> ids)
        {
            lock (_containerIdsInOperation)
            {
                foreach (var id in ids ?? Enumerable.Empty<string>())
                {
                    _containerIdsInOperation.Remove(id);
                }
            }
        }

        private void SetContainerDeviceOnline(string contextName, bool online)
        {
            if (!string.IsNullOrWhiteSpace(contextName))
            {
                _containerDeviceOnline[contextName] = online;
            }
        }

        private void SetContainerPageState(string text, bool actionsEnabled, bool retryVisible)
        {
            if (ContainerRefreshStatusText != null)
            {
                ContainerRefreshStatusText.Text = text;
                ContainerRefreshStatusText.Foreground = new SolidColorBrush(
                    actionsEnabled ? Color.FromRgb(39, 114, 66) : Color.FromRgb(180, 83, 9));
            }

            if (ContainerRetryButton != null)
            {
                ContainerRetryButton.Visibility = retryVisible ? Visibility.Visible : Visibility.Collapsed;
            }

            SetContainerActionButtonsEnabled(actionsEnabled);
            if (ContainerComposeGrid != null)
            {
                ContainerComposeGrid.IsReadOnly = true;
                if (ContainerComposeGrid.ContextMenu != null)
                {
                    ContainerComposeGrid.ContextMenu.IsEnabled = actionsEnabled;
                }
            }
        }

        private void UpdateContainerActionAvailability()
        {
            var contextName = GetSelectedContainerContextName();
            bool online;
            var enabled = !string.IsNullOrWhiteSpace(contextName) &&
                          _containerDeviceOnline.TryGetValue(contextName, out online) &&
                          online;
            SetContainerActionButtonsEnabled(enabled);
            if (ContainerComposeGrid != null && ContainerComposeGrid.ContextMenu != null)
            {
                ContainerComposeGrid.ContextMenu.IsEnabled = enabled;
            }
        }

        private void ShowServiceContainerPanel()
        {
            DashboardTopPanel.Visibility = Visibility.Collapsed;
            DashboardMainPanel.Visibility = Visibility.Collapsed;
            StrategyPanel.Visibility = Visibility.Collapsed;
            ServiceImagePanel.Visibility = Visibility.Collapsed;
            ServiceContainerPanel.Visibility = Visibility.Visible;
        }

        private sealed class ContainerSummaryQueryResult
        {
            private ContainerSummaryQueryResult(bool success, List<DockerContainerInfo> containers, string error)
            {
                Success = success;
                Containers = containers ?? new List<DockerContainerInfo>();
                Error = error ?? string.Empty;
            }

            public bool Success { get; }
            public List<DockerContainerInfo> Containers { get; }
            public string Error { get; }

            public static ContainerSummaryQueryResult Succeeded(List<DockerContainerInfo> containers)
            {
                return new ContainerSummaryQueryResult(true, containers, string.Empty);
            }

            public static ContainerSummaryQueryResult Failed(string error)
            {
                return new ContainerSummaryQueryResult(false, null, error);
            }
        }

        private sealed class ContainerDetailResult
        {
            public ContainerDetailResult(string ports, string cpuCores, string cpuPercent, string memoryUsage, string memoryPercent, string blockIo, string size)
            {
                Ports = ports;
                CpuCores = cpuCores;
                CpuPercent = cpuPercent;
                MemoryUsage = memoryUsage;
                MemoryPercent = memoryPercent;
                BlockIo = blockIo;
                Size = size;
            }

            public string Ports { get; }
            public string CpuCores { get; }
            public string CpuPercent { get; }
            public string MemoryUsage { get; }
            public string MemoryPercent { get; }
            public string BlockIo { get; }
            public string Size { get; }
        }
    }
}
