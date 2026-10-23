using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WpfApp1
{
    public partial class MainWindow
    {
        private async Task ExecuteOptimizedSingleDeviceDeployAsync(List<ImageComposeRow> imageRows)
        {
            var deployList = (imageRows ?? new List<ImageComposeRow>())
                .Where(r => r != null && !string.IsNullOrWhiteSpace(NormalizeRepoTag(r.RepoTag)))
                .GroupBy(r => NormalizeRepoTag(r.RepoTag), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            if (deployList.Count == 0)
            {
                MessageBox.Show(this, "未找到可部署镜像。", "镜像编排", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            DeployTarget target;
            if (!TrySelectOptimizedDeployTarget(out target))
            {
                return;
            }

            var confirmText = new StringBuilder();
            confirmText.AppendLine("目标设备：" + target.DeviceName);
            confirmText.AppendLine("Docker 上下文：" + target.ContextName);
            confirmText.AppendLine("待处理镜像：" + deployList.Count.ToString(CultureInfo.InvariantCulture));
            for (var i = 0; i < deployList.Count; i++)
            {
                confirmText.AppendLine("• " + NormalizeRepoTag(deployList[i].RepoTag));
            }
            confirmText.AppendLine();
            confirmText.AppendLine("部署前会先比较本地和远端镜像 ID；相同镜像将自动跳过或仅补充 Tag。");
            if (MessageBox.Show(this, confirmText.ToString().TrimEnd(), "确认同步镜像", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            await ShowOptimizedDeployProgressAsync(target, deployList);
        }

        private bool TrySelectOptimizedDeployTarget(out DeployTarget target)
        {
            target = null;
            if (_devices == null || _devices.Count == 0)
            {
                MessageBox.Show(this, "没有可用设备，请先读取设备信息。", "镜像编排", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            string contextName;
            string deviceName;
            if (_devices.Count == 1)
            {
                var device = _devices[0];
                deviceName = device.Name;
                if (!_deviceContextMap.TryGetValue(device.Name, out contextName) || string.IsNullOrWhiteSpace(contextName))
                {
                    contextName = device.ContextName;
                }
                contextName = ResolvePreferredDeployContextName(contextName);
            }
            else if (!ShowDeployDeviceDialog(out contextName, out deviceName))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(contextName))
            {
                MessageBox.Show(this, "目标设备没有可用的 Docker/SSH 上下文。", "镜像编排", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            string sshIdentity;
            if (!TryResolveSshIdentityForContext(contextName, out sshIdentity) || string.IsNullOrWhiteSpace(sshIdentity))
            {
                MessageBox.Show(this, "无法解析目标设备的 SSH 地址，请重新读取设备信息。", "镜像编排", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (!_sshPasswordByTarget.ContainsKey(sshIdentity))
            {
                MessageBox.Show(this, "当前会话没有目标设备的 SSH 凭据，请先点击“读取设备信息”并完成连接。", "镜像编排", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            target = new DeployTarget(deviceName, contextName, sshIdentity);
            return true;
        }

        private async Task ShowOptimizedDeployProgressAsync(DeployTarget target, List<ImageComposeRow> deployList)
        {
            var cancellation = new CancellationTokenSource();
            var running = true;
            var closeCompletion = new TaskCompletionSource<bool>();
            var logBuffer = new StringBuilder();

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var targetText = new TextBlock
            {
                Text = "目标设备：" + target.DeviceName,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55))
            };
            root.Children.Add(targetText);
            var stageText = new TextBlock
            {
                Text = "阶段：准备部署",
                Margin = new Thickness(0, 7, 0, 7),
                Foreground = new SolidColorBrush(Color.FromRgb(36, 64, 98))
            };
            Grid.SetRow(stageText, 1);
            root.Children.Add(stageText);
            var progressBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 17, Margin = new Thickness(0, 0, 0, 10) };
            Grid.SetRow(progressBar, 2);
            root.Children.Add(progressBar);
            var logBox = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240))
            };
            Grid.SetRow(logBox, 3);
            root.Children.Add(logBox);
            var cancelButton = new Button { Content = "取消部署", Width = 110, Height = 34, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            Grid.SetRow(cancelButton, 4);
            root.Children.Add(cancelButton);

            var window = new Window
            {
                Title = "同步镜像到设备",
                Width = 920,
                Height = 620,
                MinWidth = 760,
                MinHeight = 480,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = root
            };
            EnsureWindowCenteredOnOwner(window);

            Action<string> appendLog = text =>
            {
                var value = text ?? string.Empty;
                lock (logBuffer)
                {
                    logBuffer.AppendLine(value);
                }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    logBox.AppendText(value + Environment.NewLine);
                    logBox.ScrollToEnd();
                }));
            };
            Action<string, double, bool> report = (stage, percent, indeterminate) => Dispatcher.BeginInvoke(new Action(() =>
            {
                stageText.Text = "阶段：" + stage;
                progressBar.IsIndeterminate = indeterminate;
                if (!indeterminate)
                {
                    progressBar.Value = Math.Max(0, Math.Min(100, percent));
                }
            }));

            cancelButton.Click += (_, __) =>
            {
                if (!running)
                {
                    window.Close();
                    return;
                }
                cancelButton.IsEnabled = false;
                cancelButton.Content = "正在取消...";
                appendLog("[取消] 正在终止当前 docker/ssh/scp 进程...");
                cancellation.Cancel();
            };
            window.Closing += (_, e) =>
            {
                if (running)
                {
                    e.Cancel = true;
                    cancelButton.IsEnabled = false;
                    cancelButton.Content = "正在取消...";
                    cancellation.Cancel();
                }
            };
            window.Closed += (_, __) => closeCompletion.TrySetResult(true);
            window.Show();

            var summary = await PerformOptimizedDeployAsync(target, deployList, cancellation.Token, appendLog, report);
            running = false;
            cancelButton.IsEnabled = true;
            cancelButton.Content = "关闭";
            progressBar.IsIndeterminate = false;
            if (summary.Canceled)
            {
                stageText.Text = "阶段：已取消";
                stageText.Foreground = new SolidColorBrush(Color.FromRgb(180, 83, 9));
                appendLog("[结果] 部署已取消，已执行本地及远端临时文件清理。");
            }
            else if (summary.FailedCount > 0)
            {
                stageText.Text = string.Format("阶段：完成（成功 {0}，跳过 {1}，失败 {2}）", summary.SuccessCount, summary.SkippedCount, summary.FailedCount);
                stageText.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
            }
            else
            {
                progressBar.Value = 100;
                stageText.Text = string.Format("阶段：完成（更新 {0}，快速跳过 {1}）", summary.SuccessCount, summary.SkippedCount);
                stageText.Foreground = new SolidColorBrush(Color.FromRgb(22, 101, 52));
            }
            appendLog(summary.BuildDisplayText());

            await closeCompletion.Task;
            if (!summary.Canceled && summary.FailedCount > 0)
            {
                ShowScrollableErrorDialog("镜像同步完成，但存在失败项。", summary.BuildDisplayText());
            }
        }

        private async Task<OptimizedDeploySummary> PerformOptimizedDeployAsync(
            DeployTarget target,
            List<ImageComposeRow> deployList,
            CancellationToken cancellationToken,
            Action<string> appendLog,
            Action<string, double, bool> report)
        {
            var summary = new OptimizedDeploySummary();
            try
            {
                report("连接设备并读取远端镜像", 3, true);
                appendLog("[预检] 正在连接 " + target.SshIdentity + " 并读取远端镜像清单...");
                var inventoryResult = await RunDeployRemoteDockerCommandAsync(
                    target.SshIdentity,
                    "image ls --no-trunc --format \"{{.ID}}|{{.Repository}}:{{.Tag}}\"",
                    30000,
                    cancellationToken,
                    appendLog);
                if (!inventoryResult.Success)
                {
                    summary.Results.Add(OptimizedDeployItemResult.Failed("设备预检", inventoryResult.Output));
                    return summary;
                }
                var remoteInventory = ParseDeployRemoteInventory(inventoryResult.Output);
                appendLog(string.Format("[预检] 远端当前有 {0} 个可识别镜像标签。", remoteInventory.TagToId.Count));

                for (var i = 0; i < deployList.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var repoTag = NormalizeRepoTag(deployList[i].RepoTag);
                    var basePercent = i * 100d / Math.Max(1, deployList.Count);
                    var span = 100d / Math.Max(1, deployList.Count);
                    Action<string, double, bool> imageReport = (stage, fraction, indeterminate) =>
                        report(string.Format("{0}（{1}/{2}）", stage, i + 1, deployList.Count), basePercent + span * Math.Max(0, Math.Min(1, fraction)), indeterminate);
                    appendLog(string.Empty);
                    appendLog("========== " + repoTag + " ==========");

                    imageReport("读取本地镜像 ID", 0.05, true);
                    var localIdResult = await RunWizardDockerCommandAsync(
                        string.Format("image inspect --format \"{{{{.Id}}}}\" \"{0}\"", repoTag),
                        30000,
                        cancellationToken,
                        line => appendLog("[本地] " + line));
                    if (localIdResult.Canceled)
                    {
                        throw new OperationCanceledException();
                    }
                    var localImageId = NormalizeDeployImageId(localIdResult.Output);
                    if (!localIdResult.Success || string.IsNullOrWhiteSpace(localImageId))
                    {
                        summary.Results.Add(OptimizedDeployItemResult.Failed(repoTag, "本地镜像不存在或无法读取 ID：" + localIdResult.Output));
                        continue;
                    }
                    appendLog("[本地] 镜像 ID：" + localImageId);

                    string remoteExactId;
                    if (remoteInventory.TagToId.TryGetValue(repoTag, out remoteExactId) &&
                        string.Equals(NormalizeDeployImageId(remoteExactId), localImageId, StringComparison.OrdinalIgnoreCase))
                    {
                        appendLog("[快速路径] 远端 Tag 与镜像 ID 均一致，无需传输。");
                        summary.Results.Add(OptimizedDeployItemResult.Skipped(repoTag, "远端已是最新镜像"));
                        imageReport("远端已是最新", 1, false);
                        continue;
                    }

                    string existingRemoteTag;
                    if (remoteInventory.IdToTag.TryGetValue(localImageId, out existingRemoteTag))
                    {
                        imageReport("远端已有相同内容，仅补充 Tag", 0.45, true);
                        appendLog("[快速路径] 远端已有相同镜像 ID，来源标签：" + existingRemoteTag);
                        var tagResult = await RunDeployRemoteDockerCommandAsync(
                            target.SshIdentity,
                            string.Format("image tag \"{0}\" \"{1}\"", existingRemoteTag, repoTag),
                            30000,
                            cancellationToken,
                            appendLog);
                        if (tagResult.Success)
                        {
                            remoteInventory.Add(repoTag, localImageId);
                            summary.Results.Add(OptimizedDeployItemResult.Skipped(repoTag, "未传输，仅在远端补充 Tag"));
                            imageReport("已补充远端 Tag", 1, false);
                        }
                        else
                        {
                            summary.Results.Add(OptimizedDeployItemResult.Failed(repoTag, "远端补 Tag 失败：" + tagResult.Output));
                        }
                        continue;
                    }

                    var archivePath = FindReusableDeployArchive(repoTag, localImageId);
                    if (string.IsNullOrWhiteSpace(archivePath))
                    {
                        imageReport("导出并缓存本地镜像", 0.18, true);
                        archivePath = await ExportDeployArchiveToCacheAsync(repoTag, localImageId, cancellationToken, appendLog);
                        if (string.IsNullOrWhiteSpace(archivePath))
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                throw new OperationCanceledException();
                            }
                            summary.Results.Add(OptimizedDeployItemResult.Failed(repoTag, "本地镜像导出失败，详情见实时日志。"));
                            continue;
                        }
                    }
                    else
                    {
                        appendLog("[缓存] 复用已有镜像包：" + archivePath);
                    }

                    var archiveSize = new FileInfo(archivePath).Length;
                    imageReport("上传镜像包 " + FormatBytes(archiveSize), 0.42, true);
                    var remotePath = "/tmp/deploy_sync_" + Guid.NewGuid().ToString("N") + ".tar";
                    try
                    {
                        var uploadResult = await RunDeployScpWithProgressAsync(
                            target.SshIdentity,
                            archivePath,
                            remotePath,
                            archiveSize,
                            3600000,
                            cancellationToken,
                            appendLog,
                            (uploadedBytes, bytesPerSecond, remaining) =>
                            {
                                var ratio = archiveSize <= 0 ? 0 : Math.Max(0, Math.Min(1, uploadedBytes / (double)archiveSize));
                                var detail = string.Format(
                                    "上传镜像包 {0}/{1}，{2}/s{3}",
                                    FormatBytes(uploadedBytes),
                                    FormatBytes(archiveSize),
                                    FormatBytes((long)Math.Max(0, bytesPerSecond)),
                                    remaining.HasValue ? "，预计剩余 " + FormatDeployDuration(remaining.Value) : string.Empty);
                                imageReport(detail, 0.42 + ratio * 0.28, false);
                            });
                        if (uploadResult.Canceled)
                        {
                            throw new OperationCanceledException();
                        }
                        if (!uploadResult.Success)
                        {
                            summary.Results.Add(OptimizedDeployItemResult.Failed(repoTag, "上传失败：" + uploadResult.Output));
                            continue;
                        }

                        imageReport("远端加载镜像", 0.78, true);
                        var loadResult = await RunDeployRemoteDockerCommandAsync(
                            target.SshIdentity,
                            string.Format("image load -i \"{0}\"", remotePath),
                            3600000,
                            cancellationToken,
                            appendLog);
                        if (loadResult.Canceled)
                        {
                            throw new OperationCanceledException();
                        }
                        if (!loadResult.Success)
                        {
                            summary.Results.Add(OptimizedDeployItemResult.Failed(repoTag, "远端加载失败：" + loadResult.Output));
                            continue;
                        }

                        imageReport("同步远端 Tag 并校验镜像 ID", 0.93, true);
                        var loadedTagResult = await RunDeployRemoteDockerCommandAsync(
                            target.SshIdentity,
                            string.Format("image tag \"{0}\" \"{1}\"", localImageId, repoTag),
                            30000,
                            cancellationToken,
                            appendLog);
                        if (!loadedTagResult.Success)
                        {
                            summary.Results.Add(OptimizedDeployItemResult.Failed(repoTag, "镜像已加载，但设置目标 Tag 失败：" + loadedTagResult.Output));
                            continue;
                        }

                        var verifyResult = await RunDeployRemoteDockerCommandAsync(
                            target.SshIdentity,
                            string.Format("image inspect --format \"{{{{.Id}}}}\" \"{0}\"", repoTag),
                            30000,
                            cancellationToken,
                            appendLog);
                        var remoteImageId = NormalizeDeployImageId(verifyResult.Output);
                        if (!verifyResult.Success || !string.Equals(remoteImageId, localImageId, StringComparison.OrdinalIgnoreCase))
                        {
                            summary.Results.Add(OptimizedDeployItemResult.Failed(repoTag, string.Format("远端校验失败：期望 {0}，实际 {1}", localImageId, string.IsNullOrWhiteSpace(remoteImageId) ? "未找到" : remoteImageId)));
                            continue;
                        }

                        remoteInventory.Add(repoTag, localImageId);
                        summary.Results.Add(OptimizedDeployItemResult.Succeeded(repoTag, string.Format("已上传并加载（{0}）", FormatBytes(archiveSize))));
                        imageReport("同步完成", 1, false);
                    }
                    finally
                    {
                        imageReport("清理远端临时文件", 0.98, true);
                        var cleanup = await RunDeployRemoteShellCommandAsync(
                            target.SshIdentity,
                            "rm -f -- '" + EscapeShellSingleQuoted(remotePath) + "'",
                            20000,
                            CancellationToken.None);
                        appendLog(cleanup.Success ? "[清理] 已删除远端临时文件。" : "[警告] 远端临时文件清理失败：" + cleanup.Output);
                    }
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    report("刷新设备镜像列表", 99, true);
                    await RefreshRuntimeDataBindingsAsync(true);
                }
            }
            catch (OperationCanceledException)
            {
                summary.Canceled = true;
            }
            catch (Exception ex)
            {
                summary.Results.Add(OptimizedDeployItemResult.Failed("部署任务", ex.Message));
            }
            return summary;
        }

        private string FindReusableDeployArchive(string repoTag, string imageId)
        {
            var normalizedId = NormalizeDeployImageId(imageId);
            if (string.IsNullOrWhiteSpace(normalizedId))
            {
                return string.Empty;
            }

            var entries = ReadDeployArchiveIndex();
            for (var i = entries.Count - 1; i >= 0; i--)
            {
                var entry = entries[i];
                if (!string.Equals(NormalizeDeployImageId(entry.ImageId), normalizedId, StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(entry.ArchivePath) ||
                    !File.Exists(entry.ArchivePath))
                {
                    continue;
                }
                try
                {
                    if (new FileInfo(entry.ArchivePath).Length > 0)
                    {
                        return entry.ArchivePath;
                    }
                }
                catch
                {
                }
            }

            var searchDirectories = new[]
            {
                Path.Combine(GetDockerBuildWorkspaceRoot(), "image_outputs"),
                Path.Combine(GetDockerBuildWorkspaceRoot(), "deploy_cache")
            };
            for (var dirIndex = 0; dirIndex < searchDirectories.Length; dirIndex++)
            {
                var directory = searchDirectories[dirIndex];
                if (!Directory.Exists(directory))
                {
                    continue;
                }
                string[] metadataFiles;
                try
                {
                    metadataFiles = Directory.GetFiles(directory, "*.meta.txt", SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    continue;
                }
                for (var fileIndex = 0; fileIndex < metadataFiles.Length; fileIndex++)
                {
                    DeployArchiveEntry entry;
                    if (!TryReadDeployArchiveMetadata(metadataFiles[fileIndex], out entry) ||
                        !string.Equals(NormalizeDeployImageId(entry.ImageId), normalizedId, StringComparison.OrdinalIgnoreCase) ||
                        !File.Exists(entry.ArchivePath))
                    {
                        continue;
                    }
                    return entry.ArchivePath;
                }
            }
            return string.Empty;
        }

        private async Task<string> ExportDeployArchiveToCacheAsync(
            string repoTag,
            string imageId,
            CancellationToken cancellationToken,
            Action<string> appendLog)
        {
            var cacheDirectory = Path.Combine(GetDockerBuildWorkspaceRoot(), "deploy_cache");
            Directory.CreateDirectory(cacheDirectory);
            var safeId = RegexSafeFileName(NormalizeDeployImageId(imageId));
            if (safeId.Length > 48)
            {
                safeId = safeId.Substring(safeId.Length - 48);
            }
            if (string.IsNullOrWhiteSpace(safeId))
            {
                safeId = DateTime.Now.ToString("yyyyMMddHHmmssfff");
            }
            var finalPath = Path.Combine(cacheDirectory, "image_" + safeId + ".tar");
            if (File.Exists(finalPath))
            {
                finalPath = Path.Combine(cacheDirectory, "image_" + safeId + "_" + DateTime.Now.ToString("yyyyMMddHHmmssfff") + ".tar");
            }
            var partialPath = finalPath + ".partial_" + Guid.NewGuid().ToString("N");
            try
            {
                appendLog("[导出] 未找到匹配镜像 ID 的现有镜像包，正在创建部署缓存...");
                var command = string.Format("image save \"{0}\" -o \"{1}\"", repoTag, partialPath);
                var result = await RunWizardDockerCommandAsync(command, 3600000, cancellationToken, line => appendLog("[导出] " + line));
                if (result.Canceled)
                {
                    throw new OperationCanceledException();
                }
                if (!result.Success || !File.Exists(partialPath))
                {
                    appendLog("[错误] docker image save 失败：" + result.Output);
                    return string.Empty;
                }
                File.Move(partialPath, finalPath);
                WriteDeployArchiveMetadata(finalPath, repoTag, imageId);
                appendLog("[缓存] 已保存部署缓存：" + finalPath);
                return finalPath;
            }
            finally
            {
                TryDeleteFileQuietly(partialPath);
            }
        }

        private static string RegexSafeFileName(string value)
        {
            var chars = (value ?? string.Empty).Select(ch => char.IsLetterOrDigit(ch) || ch == '.' || ch == '-' || ch == '_' ? ch : '_').ToArray();
            return new string(chars).Trim('_', '.', '-');
        }

        private void WriteDeployArchiveMetadata(string archivePath, string repoTag, string imageId)
        {
            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            {
                return;
            }
            try
            {
                var entry = new DeployArchiveEntry(repoTag, NormalizeDeployImageId(imageId), Path.GetFullPath(archivePath), DateTime.UtcNow);
                var lines = new[]
                {
                    "version=2",
                    "image_ref_b64=" + EncodeDeployMetadata(entry.RepoTag),
                    "image_id=" + entry.ImageId,
                    "archive_path_b64=" + EncodeDeployMetadata(entry.ArchivePath),
                    "created_utc=" + entry.CreatedUtc.ToString("o", CultureInfo.InvariantCulture)
                };
                File.WriteAllLines(archivePath + ".meta.txt", lines, new UTF8Encoding(false));

                lock (_deployArchiveIndexLock)
                {
                    var entries = ReadDeployArchiveIndexUnsafe();
                    entries.RemoveAll(e =>
                        string.Equals(e.ArchivePath, entry.ArchivePath, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(entry.ImageId) && string.Equals(e.ImageId, entry.ImageId, StringComparison.OrdinalIgnoreCase)));
                    entries.Add(entry);
                    var indexLines = entries.Select(e => string.Join("\t", new[]
                    {
                        "v1",
                        EncodeDeployMetadata(e.RepoTag),
                        EncodeDeployMetadata(e.ImageId),
                        EncodeDeployMetadata(e.ArchivePath),
                        e.CreatedUtc.ToString("o", CultureInfo.InvariantCulture)
                    })).ToArray();
                    File.WriteAllLines(GetDeployArchiveIndexPath(), indexLines, new UTF8Encoding(false));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Write deploy archive metadata failed: " + ex.Message);
            }
        }

        private List<DeployArchiveEntry> ReadDeployArchiveIndex()
        {
            lock (_deployArchiveIndexLock)
            {
                return ReadDeployArchiveIndexUnsafe();
            }
        }

        private List<DeployArchiveEntry> ReadDeployArchiveIndexUnsafe()
        {
            var result = new List<DeployArchiveEntry>();
            var path = GetDeployArchiveIndexPath();
            if (!File.Exists(path))
            {
                return result;
            }
            string[] lines;
            try
            {
                lines = File.ReadAllLines(path, Encoding.UTF8);
            }
            catch
            {
                return result;
            }
            for (var i = 0; i < lines.Length; i++)
            {
                var parts = lines[i].Split('\t');
                if (parts.Length != 5 || parts[0] != "v1")
                {
                    continue;
                }
                DateTime created;
                if (!DateTime.TryParse(parts[4], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out created))
                {
                    created = DateTime.MinValue;
                }
                result.Add(new DeployArchiveEntry(
                    DecodeDeployMetadata(parts[1]),
                    DecodeDeployMetadata(parts[2]),
                    DecodeDeployMetadata(parts[3]),
                    created));
            }
            return result;
        }

        private static bool TryReadDeployArchiveMetadata(string metadataPath, out DeployArchiveEntry entry)
        {
            entry = null;
            try
            {
                var values = File.ReadAllLines(metadataPath, Encoding.UTF8)
                    .Select(line => new { Line = line, Separator = line.IndexOf('=') })
                    .Where(x => x.Separator > 0)
                    .ToDictionary(
                        x => x.Line.Substring(0, x.Separator),
                        x => x.Line.Substring(x.Separator + 1),
                        StringComparer.OrdinalIgnoreCase);
                string version;
                string repoTagEncoded;
                string imageId;
                string archivePathEncoded;
                if (!values.TryGetValue("version", out version) || version != "2" ||
                    !values.TryGetValue("image_ref_b64", out repoTagEncoded) ||
                    !values.TryGetValue("image_id", out imageId) ||
                    !values.TryGetValue("archive_path_b64", out archivePathEncoded))
                {
                    return false;
                }
                DateTime created = DateTime.MinValue;
                string createdText;
                if (values.TryGetValue("created_utc", out createdText))
                {
                    DateTime.TryParse(createdText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out created);
                }
                entry = new DeployArchiveEntry(
                    DecodeDeployMetadata(repoTagEncoded),
                    imageId,
                    DecodeDeployMetadata(archivePathEncoded),
                    created);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string GetDeployArchiveIndexPath()
        {
            return Path.Combine(GetDockerBuildWorkspaceRoot(), "deploy_archive_index.tsv");
        }

        private static string EncodeDeployMetadata(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
        }

        private static string DecodeDeployMetadata(string value)
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(value ?? string.Empty));
            }
            catch
            {
                return string.Empty;
            }
        }

        private async Task<DeployProcessResult> RunDeployRemoteDockerCommandAsync(
            string targetIdentity,
            string commandArguments,
            int timeoutMilliseconds,
            CancellationToken cancellationToken,
            Action<string> appendLog)
        {
            string user;
            string host;
            string password;
            if (!TryResolveSshTarget(targetIdentity, out user, out host, out password))
            {
                return DeployProcessResult.Failed("未找到 SSH 目标或凭据。");
            }
            var escapedPassword = EscapeShellSingleQuoted(password);
            var candidates = new List<string>
            {
                "docker " + commandArguments,
                "/usr/bin/docker " + commandArguments,
                "/usr/local/bin/docker " + commandArguments,
                string.Format("printf '%s\\n' '{0}' | sudo -S -p '' docker {1}", escapedPassword, commandArguments),
                string.Format("printf '%s\\n' '{0}' | sudo -S -p '' /usr/bin/docker {1}", escapedPassword, commandArguments),
                string.Format("printf '%s\\n' '{0}' | sudo -S -p '' /usr/local/bin/docker {1}", escapedPassword, commandArguments)
            };
            var order = new List<int>();
            int preferred;
            var hasPreferred = _preferredSshDockerCandidateIndex.TryGetValue(targetIdentity, out preferred) && preferred >= 0 && preferred < candidates.Count;
            if (hasPreferred)
            {
                order.Add(preferred);
            }
            var isLongRunning = commandArguments.TrimStart().StartsWith("image load ", StringComparison.OrdinalIgnoreCase);
            if (!isLongRunning || !hasPreferred)
            {
                for (var i = 0; i < candidates.Count; i++)
                {
                    if (!order.Contains(i))
                    {
                        order.Add(i);
                    }
                }
            }

            var lastOutput = string.Empty;
            for (var i = 0; i < order.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidateIndex = order[i];
                var result = await RunDeploySshCommandAsync(user, host, password, candidates[candidateIndex], timeoutMilliseconds, cancellationToken);
                if (!string.IsNullOrWhiteSpace(result.Output) && appendLog != null)
                {
                    var lines = result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
                    {
                        appendLog("[远端] " + lines[lineIndex]);
                    }
                }
                if (result.Canceled)
                {
                    return result;
                }
                if (result.Success)
                {
                    _preferredSshDockerCandidateIndex[targetIdentity] = candidateIndex;
                    return result;
                }
                lastOutput = result.Output;
            }
            return DeployProcessResult.Failed(string.IsNullOrWhiteSpace(lastOutput) ? "远端 Docker 命令执行失败。" : lastOutput);
        }

        private async Task<DeployProcessResult> RunDeployRemoteShellCommandAsync(
            string targetIdentity,
            string command,
            int timeoutMilliseconds,
            CancellationToken cancellationToken)
        {
            string user;
            string host;
            string password;
            if (!TryResolveSshTarget(targetIdentity, out user, out host, out password))
            {
                return DeployProcessResult.Failed("未找到 SSH 目标或凭据。");
            }
            return await RunDeploySshCommandAsync(user, host, password, command, timeoutMilliseconds, cancellationToken);
        }

        private async Task<DeployProcessResult> RunDeploySshCommandAsync(
            string user,
            string host,
            string password,
            string command,
            int timeoutMilliseconds,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                string stdOut;
                string stdErr;
                var ok = ExecuteSshRemoteCommand(
                    user,
                    host,
                    password,
                    command,
                    timeoutMilliseconds,
                    out stdOut,
                    out stdErr,
                    "image-deploy",
                    cancellationToken);
                var output = MergeProcessStreams(stdOut, stdErr);
                if (cancellationToken.IsCancellationRequested)
                {
                    return DeployProcessResult.CanceledResult(output);
                }
                return ok ? DeployProcessResult.Succeeded(output) : DeployProcessResult.Failed(output);
            });
        }

        private async Task<DeployProcessResult> RunDeployScpAsync(
            string targetIdentity,
            string localPath,
            string remotePath,
            int timeoutMilliseconds,
            CancellationToken cancellationToken,
            Action<string> appendLog)
        {
            string user;
            string host;
            string password;
            if (!TryResolveSshTarget(targetIdentity, out user, out host, out password))
            {
                return DeployProcessResult.Failed("未找到 SSH 目标或凭据。");
            }
            string scpHost;
            int sshPort;
            if (!TryParseSshHostEndpoint(host, out scpHost, out sshPort))
            {
                return DeployProcessResult.Failed("SSH 主机地址或端口无效。");
            }
            var sshExecutable = ResolveSshExecutablePath();
            var scpExecutable = ResolveScpExecutablePath(sshExecutable);
            if (string.IsNullOrWhiteSpace(scpExecutable))
            {
                return DeployProcessResult.Failed("未找到可用的 scp.exe。");
            }

            return await Task.Run(() =>
            {
                var askPassFile = Path.Combine(Path.GetTempPath(), "deploy_scp_askpass_" + Guid.NewGuid().ToString("N") + ".cmd");
                try
                {
                    File.WriteAllText(askPassFile, "@echo off\r\necho " + password + "\r\n", new UTF8Encoding(false));
                    var arguments = string.Format(
                        "-P {0} -o BatchMode=no -o PreferredAuthentications=password,keyboard-interactive -o PubkeyAuthentication=no -o NumberOfPasswordPrompts=1 -o ConnectTimeout=8 -o ServerAliveInterval=10 -o ServerAliveCountMax=3 -o StrictHostKeyChecking=no -o UserKnownHostsFile=NUL \"{1}\" {2}@{3}:\"{4}\"",
                        sshPort,
                        localPath,
                        user,
                        scpHost,
                        remotePath);
                    var psi = new ProcessStartInfo
                    {
                        FileName = scpExecutable,
                        Arguments = arguments,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    psi.EnvironmentVariables["SSH_ASKPASS"] = askPassFile;
                    psi.EnvironmentVariables["SSH_ASKPASS_REQUIRE"] = "force";
                    psi.EnvironmentVariables["DISPLAY"] = "codex";
                    var buffer = new StringBuilder();
                    using (var process = new Process())
                    {
                        process.StartInfo = psi;
                        process.OutputDataReceived += (_, e) => AppendDeployProcessLine(e.Data, buffer, appendLog);
                        process.ErrorDataReceived += (_, e) => AppendDeployProcessLine(e.Data, buffer, appendLog);
                        if (!process.Start())
                        {
                            return DeployProcessResult.Failed("无法启动 scp 进程。");
                        }
                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();
                        var started = DateTime.UtcNow;
                        using (cancellationToken.Register(() => TryKillDeployProcess(process)))
                        {
                            while (!process.WaitForExit(250))
                            {
                                if (cancellationToken.IsCancellationRequested)
                                {
                                    TryKillDeployProcess(process);
                                    return DeployProcessResult.CanceledResult(buffer.ToString());
                                }
                                if ((DateTime.UtcNow - started).TotalMilliseconds > timeoutMilliseconds)
                                {
                                    TryKillDeployProcess(process);
                                    return DeployProcessResult.Failed("scp 上传超时。\n" + buffer);
                                }
                            }
                        }
                        process.WaitForExit();
                        var output = buffer.ToString().Trim();
                        return process.ExitCode == 0 ? DeployProcessResult.Succeeded(output) : DeployProcessResult.Failed(output);
                    }
                }
                catch (Exception ex)
                {
                    return DeployProcessResult.Failed(ex.Message);
                }
                finally
                {
                    TryDeleteFileQuietly(askPassFile);
                }
            });
        }

        private async Task<DeployProcessResult> RunDeployScpWithProgressAsync(
            string targetIdentity,
            string localPath,
            string remotePath,
            long totalBytes,
            int timeoutMilliseconds,
            CancellationToken cancellationToken,
            Action<string> appendLog,
            Action<long, double, TimeSpan?> progressCallback)
        {
            var started = DateTime.UtcNow;
            var lastSampleTime = started;
            long lastBytes = 0;
            var uploadTask = RunDeployScpAsync(targetIdentity, localPath, remotePath, timeoutMilliseconds, cancellationToken, appendLog);
            while (!uploadTask.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var completed = await Task.WhenAny(uploadTask, Task.Delay(2000, cancellationToken));
                if (completed == uploadTask)
                {
                    break;
                }

                var sizeResult = await RunDeployRemoteShellCommandAsync(
                    targetIdentity,
                    "stat -c %s -- '" + EscapeShellSingleQuoted(remotePath) + "' 2>/dev/null || echo 0",
                    10000,
                    cancellationToken);
                long uploadedBytes;
                if (!TryExtractDeployByteCount(sizeResult.Output, out uploadedBytes))
                {
                    continue;
                }
                uploadedBytes = Math.Max(0, Math.Min(totalBytes, uploadedBytes));
                var now = DateTime.UtcNow;
                var sampleSeconds = Math.Max(0.001, (now - lastSampleTime).TotalSeconds);
                var sampleSpeed = Math.Max(0, (uploadedBytes - lastBytes) / sampleSeconds);
                var averageSeconds = Math.Max(0.001, (now - started).TotalSeconds);
                var averageSpeed = uploadedBytes / averageSeconds;
                var effectiveSpeed = sampleSpeed > 0 ? sampleSpeed : averageSpeed;
                TimeSpan? remaining = null;
                if (effectiveSpeed > 0 && totalBytes > uploadedBytes)
                {
                    remaining = TimeSpan.FromSeconds((totalBytes - uploadedBytes) / effectiveSpeed);
                }
                if (progressCallback != null)
                {
                    progressCallback(uploadedBytes, effectiveSpeed, remaining);
                }
                lastBytes = uploadedBytes;
                lastSampleTime = now;
            }
            var result = await uploadTask;
            if (result.Success && progressCallback != null)
            {
                progressCallback(totalBytes, totalBytes / Math.Max(0.001, (DateTime.UtcNow - started).TotalSeconds), TimeSpan.Zero);
            }
            return result;
        }

        private static bool TryExtractDeployByteCount(string output, out long bytes)
        {
            bytes = 0;
            var matches = System.Text.RegularExpressions.Regex.Matches(output ?? string.Empty, @"(?m)^\s*(\d+)\s*$");
            if (matches.Count == 0)
            {
                return false;
            }
            return long.TryParse(matches[matches.Count - 1].Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out bytes);
        }

        private static string FormatDeployDuration(TimeSpan value)
        {
            if (value.TotalSeconds < 1)
            {
                return "不到 1 秒";
            }
            if (value.TotalMinutes < 1)
            {
                return Math.Ceiling(value.TotalSeconds).ToString("0", CultureInfo.InvariantCulture) + " 秒";
            }
            return Math.Floor(value.TotalMinutes).ToString("0", CultureInfo.InvariantCulture) + " 分 " + value.Seconds.ToString(CultureInfo.InvariantCulture) + " 秒";
        }

        private static void AppendDeployProcessLine(string line, StringBuilder buffer, Action<string> appendLog)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }
            lock (buffer)
            {
                buffer.AppendLine(line);
            }
            if (appendLog != null)
            {
                appendLog("[上传] " + line);
            }
        }

        private static void TryKillDeployProcess(Process process)
        {
            try
            {
                if (process != null && !process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
            }
            catch
            {
            }
        }

        private static DeployRemoteInventory ParseDeployRemoteInventory(string output)
        {
            var inventory = new DeployRemoteInventory();
            var lines = (output ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < lines.Length; i++)
            {
                var parts = lines[i].Split('|');
                if (parts.Length < 2)
                {
                    continue;
                }
                var id = NormalizeDeployImageId(parts[0]);
                var tag = NormalizeRepoTag(parts[1]);
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }
                inventory.IdToTag[id] = string.IsNullOrWhiteSpace(tag) || tag.IndexOf("<none>", StringComparison.OrdinalIgnoreCase) >= 0 ? id : tag;
                if (!string.IsNullOrWhiteSpace(tag) && tag.IndexOf("<none>", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    inventory.TagToId[tag] = id;
                }
            }
            return inventory;
        }

        private static string NormalizeDeployImageId(string value)
        {
            var text = (value ?? string.Empty).Trim();
            var match = System.Text.RegularExpressions.Regex.Match(text, @"sha256:[a-fA-F0-9]{12,}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success ? match.Value.ToLowerInvariant() : string.Empty;
        }

        private static readonly object _deployArchiveIndexLock = new object();

        private sealed class DeployTarget
        {
            public DeployTarget(string deviceName, string contextName, string sshIdentity)
            {
                DeviceName = deviceName ?? string.Empty;
                ContextName = contextName ?? string.Empty;
                SshIdentity = sshIdentity ?? string.Empty;
            }
            public string DeviceName { get; }
            public string ContextName { get; }
            public string SshIdentity { get; }
        }

        private sealed class DeployArchiveEntry
        {
            public DeployArchiveEntry(string repoTag, string imageId, string archivePath, DateTime createdUtc)
            {
                RepoTag = repoTag ?? string.Empty;
                ImageId = imageId ?? string.Empty;
                ArchivePath = archivePath ?? string.Empty;
                CreatedUtc = createdUtc;
            }
            public string RepoTag { get; }
            public string ImageId { get; }
            public string ArchivePath { get; }
            public DateTime CreatedUtc { get; }
        }

        private sealed class DeployRemoteInventory
        {
            public DeployRemoteInventory()
            {
                TagToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                IdToTag = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            public Dictionary<string, string> TagToId { get; }
            public Dictionary<string, string> IdToTag { get; }
            public void Add(string repoTag, string imageId)
            {
                var id = NormalizeDeployImageId(imageId);
                var tag = NormalizeRepoTag(repoTag);
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(tag))
                {
                    TagToId[tag] = id;
                    IdToTag[id] = tag;
                }
            }
        }

        private sealed class DeployProcessResult
        {
            private DeployProcessResult(bool success, bool canceled, string output)
            {
                Success = success;
                Canceled = canceled;
                Output = output ?? string.Empty;
            }
            public bool Success { get; }
            public bool Canceled { get; }
            public string Output { get; }
            public static DeployProcessResult Succeeded(string output) { return new DeployProcessResult(true, false, output); }
            public static DeployProcessResult Failed(string output) { return new DeployProcessResult(false, false, output); }
            public static DeployProcessResult CanceledResult(string output) { return new DeployProcessResult(false, true, output); }
        }

        private sealed class OptimizedDeployItemResult
        {
            private OptimizedDeployItemResult(string repoTag, string status, string detail)
            {
                RepoTag = repoTag ?? string.Empty;
                Status = status ?? string.Empty;
                Detail = detail ?? string.Empty;
            }
            public string RepoTag { get; }
            public string Status { get; }
            public string Detail { get; }
            public static OptimizedDeployItemResult Succeeded(string tag, string detail) { return new OptimizedDeployItemResult(tag, "更新", detail); }
            public static OptimizedDeployItemResult Skipped(string tag, string detail) { return new OptimizedDeployItemResult(tag, "跳过", detail); }
            public static OptimizedDeployItemResult Failed(string tag, string detail) { return new OptimizedDeployItemResult(tag, "失败", detail); }
        }

        private sealed class OptimizedDeploySummary
        {
            public OptimizedDeploySummary()
            {
                Results = new List<OptimizedDeployItemResult>();
            }
            public List<OptimizedDeployItemResult> Results { get; }
            public bool Canceled { get; set; }
            public int SuccessCount => Results.Count(r => r.Status == "更新");
            public int SkippedCount => Results.Count(r => r.Status == "跳过");
            public int FailedCount => Results.Count(r => r.Status == "失败");
            public string BuildDisplayText()
            {
                var lines = new List<string>
                {
                    string.Empty,
                    string.Format("[汇总] 更新 {0}，快速跳过 {1}，失败 {2}", SuccessCount, SkippedCount, FailedCount)
                };
                for (var i = 0; i < Results.Count; i++)
                {
                    lines.Add(string.Format("[{0}] {1}：{2}", Results[i].Status, Results[i].RepoTag, Results[i].Detail));
                }
                return string.Join(Environment.NewLine, lines);
            }
        }
    }
}
