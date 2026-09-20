using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
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
            var containerIdSnapshot = BuildContainerIdSnapshot(query.Containers);
            StartContainerDetailsRefresh(contextName, deviceName, query.Containers, containerIdSnapshot, refreshState.Item1, refreshState.Item2);
        }

        private ContainerSummaryQueryResult QueryContainerSummary(string contextName)
        {
            string output;
            const string command = "ps -a --no-trunc --format \"{{.ID}}|{{.Names}}|{{.Image}}|{{.Status}}|{{.Ports}}\"";
            if (!TryRunDockerCommand(command, contextName, out output, 10000))
            {
                return ContainerSummaryQueryResult.Failed(output);
            }

            var containers = new List<DockerContainerInfo>();
            var lines = (output ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < lines.Length; i++)
            {
                var parts = lines[i].Split('|');
                if (parts.Length < 5 || string.IsNullOrWhiteSpace(parts[0]))
                {
                    return ContainerSummaryQueryResult.Failed("容器概要查询返回格式无效。");
                }

                var status = parts[3].Trim();
                containers.Add(new DockerContainerInfo(
                    parts[0].Trim(),
                    parts[1].Trim(),
                    parts[2].Trim(),
                    parts[4].Trim(),
                    status,
                    InferContainerStateFromStatus(status),
                    "-"));
            }

            return ContainerSummaryQueryResult.Succeeded(containers);
        }

        private static List<string> BuildContainerIdSnapshot(IEnumerable<DockerContainerInfo> containers)
        {
            return (containers ?? Enumerable.Empty<DockerContainerInfo>())
                .Select(container => (container == null ? string.Empty : container.Id) ?? string.Empty)
                .Select(id => id.Trim())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
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
                        string.IsNullOrWhiteSpace(container.Ports) ? "-" : container.Ports,
                        "-", "-", "-", "-", "-",
                        container.Status,
                        container.Id,
                        "-"));
                    continue;
                }

                var current = _containerRows[index];
                var summaryPorts = string.IsNullOrWhiteSpace(container.Ports) ? current.Ports : container.Ports;
                if (!string.Equals(current.Name, normalizedName, StringComparison.Ordinal) ||
                    !string.Equals(current.Image, NormalizeImageNameWithTag(container.Image), StringComparison.Ordinal) ||
                    !string.Equals(current.Status, container.State, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(current.Ports, summaryPorts, StringComparison.Ordinal) ||
                    !string.Equals(current.Detail, container.Status, StringComparison.Ordinal))
                {
                    _containerRows[index] = new ContainerComposeRow(
                        current.DeviceName,
                        current.Id,
                        normalizedName,
                        BuildChineseContainerName(container.Name),
                        NormalizeImageNameWithTag(container.Image),
                        container.State,
                        summaryPorts,
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
            List<string> containerIdSnapshot,
            int version,
            CancellationToken token)
        {
            try
            {
                var fixedContainerIds = (containerIdSnapshot ?? new List<string>()).ToList();
                if (fixedContainerIds.Count == 0)
                {
                    return;
                }

                var allowedIds = new HashSet<string>(fixedContainerIds, StringComparer.OrdinalIgnoreCase);
                var summaryById = (containers ?? new List<DockerContainerInfo>())
                    .Where(container => container != null && allowedIds.Contains(container.Id))
                    .GroupBy(container => container.Id, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                var statsById = await Task.Run(() => ReadDockerContainerStats(contextName));
                if (!IsContainerRefreshCurrent(contextName, version, token))
                {
                    return;
                }

                var hostCpuLimitText = await Task.Run(() => ReadHostCpuLimitText(contextName));
                var appliedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < fixedContainerIds.Count; i += 15)
                {
                    if (!IsContainerRefreshCurrent(contextName, version, token))
                    {
                        return;
                    }

                    var batchIds = fixedContainerIds.Skip(i).Take(15).ToList();
                    var appliedSnapshot = new HashSet<string>(appliedIds, StringComparer.OrdinalIgnoreCase);
                    var batch = await Task.Run(() => ReadContainerDetailBatch(contextName, batchIds, allowedIds, appliedSnapshot, summaryById, hostCpuLimitText));
                    if (!IsContainerRefreshCurrent(contextName, version, token))
                    {
                        return;
                    }

                    var accepted = batch.Details.Where(detail => appliedIds.Add(detail.FullId)).ToList();
                    if (accepted.Count > 0)
                    {
                        await Dispatcher.InvokeAsync(() => ApplyContainerDetailBatch(contextName, deviceName, accepted, statsById, version, token));
                    }
                }

                var missingIds = fixedContainerIds.Where(id => !appliedIds.Contains(id)).ToList();
                for (var i = 0; i < missingIds.Count; i += 15)
                {
                    if (!IsContainerRefreshCurrent(contextName, version, token))
                    {
                        return;
                    }

                    var retryIds = missingIds.Skip(i).Take(15).ToList();
                    var appliedSnapshot = new HashSet<string>(appliedIds, StringComparer.OrdinalIgnoreCase);
                    var retryBatch = await Task.Run(() => ReadContainerDetailBatch(contextName, retryIds, allowedIds, appliedSnapshot, summaryById, hostCpuLimitText));
                    if (!IsContainerRefreshCurrent(contextName, version, token))
                    {
                        return;
                    }

                    var accepted = retryBatch.Details.Where(detail => appliedIds.Add(detail.FullId)).ToList();
                    if (accepted.Count > 0)
                    {
                        await Dispatcher.InvokeAsync(() => ApplyContainerDetailBatch(contextName, deviceName, accepted, statsById, version, token));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Container details refresh failed: " + ex.Message);
            }
        }

        private ContainerDetailBatchResult ReadContainerDetailBatch(
            string contextName,
            List<string> containerIds,
            HashSet<string> allowedIds,
            HashSet<string> alreadyAppliedIds,
            Dictionary<string, DockerContainerInfo> summaryById,
            string hostCpuLimitText)
        {
            var ids = (containerIds ?? new List<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList();
            if (ids.Count == 0)
            {
                return new ContainerDetailBatchResult(DockerCommandExecutionResult.NotStarted, new List<ContainerDetailResult>());
            }

            const string format = "{{.Id}}|{{.HostConfig.NanoCpus}}|{{.HostConfig.Memory}}|{{.HostConfig.CpuQuota}}|{{.HostConfig.CpuPeriod}}|{{.HostConfig.CpusetCpus}}|{{json .HostConfig.PortBindings}}|{{json .Config.ExposedPorts}}|{{.SizeRw}}";
            var command = "container inspect --size --format \"" + format + "\" " +
                          string.Join(" ", ids.Select(id => string.Format("\"{0}\"", id)));
            var execution = ExecuteDockerCommandDetailed(command, contextName, 30000);
            var details = ParseContainerInspectBatch(
                execution.StandardOutput,
                allowedIds,
                alreadyAppliedIds,
                summaryById,
                hostCpuLimitText);

            if (!execution.Succeeded)
            {
                Debug.WriteLine(
                    "Container detail batch inspect failed; valid stdout rows are retained. Exit=" +
                    execution.ExitCode.ToString(CultureInfo.InvariantCulture) + " | stderr=" + execution.StandardError);
            }

            return new ContainerDetailBatchResult(execution, details);
        }

        private List<ContainerDetailResult> ParseContainerInspectBatch(
            string standardOutput,
            HashSet<string> allowedIds,
            HashSet<string> alreadyAppliedIds,
            Dictionary<string, DockerContainerInfo> summaryById,
            string hostCpuLimitText)
        {
            var result = new List<ContainerDetailResult>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lines = (standardOutput ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < lines.Length; i++)
            {
                var parts = lines[i].Split('|');
                if (parts.Length < 9)
                {
                    continue;
                }

                var fullId = (parts[0] ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(fullId) ||
                    allowedIds == null ||
                    !allowedIds.Contains(fullId) ||
                    (alreadyAppliedIds != null && alreadyAppliedIds.Contains(fullId)) ||
                    !seenIds.Add(fullId))
                {
                    continue;
                }

                DockerContainerLimitInfo limitInfo;
                var hasLimits = TryParseContainerLimitInfo(parts, out limitInfo);
                DockerContainerInfo summary;
                summaryById.TryGetValue(fullId, out summary);
                string ports;
                var hasPorts = TryResolveContainerPorts(
                    parts[6],
                    summary == null ? string.Empty : summary.Ports,
                    parts[7],
                    out ports);
                long sizeBytes;
                var hasSize = long.TryParse((parts[8] ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out sizeBytes) && sizeBytes >= 0;
                if (!hasLimits && !hasPorts && !hasSize)
                {
                    continue;
                }

                result.Add(new ContainerDetailResult(
                    fullId,
                    hasPorts,
                    ports,
                    hasLimits,
                    hasLimits ? BuildCpuCoresDisplayText(limitInfo.CpuCoresText, hostCpuLimitText) : string.Empty,
                    hasLimits ? limitInfo.MemoryLimitText : string.Empty,
                    hasSize,
                    hasSize ? FormatBytes(sizeBytes) : string.Empty));
            }

            return result;
        }

        private static bool TryParseContainerLimitInfo(string[] parts, out DockerContainerLimitInfo limitInfo)
        {
            limitInfo = DockerContainerLimitInfo.Empty;
            if (parts == null || parts.Length < 6)
            {
                return false;
            }

            long nanoCpus;
            long memoryBytes;
            long cpuQuota;
            long cpuPeriod;
            if (!long.TryParse((parts[1] ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out nanoCpus) ||
                !long.TryParse((parts[2] ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out memoryBytes) ||
                !long.TryParse((parts[3] ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out cpuQuota) ||
                !long.TryParse((parts[4] ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out cpuPeriod))
            {
                return false;
            }

            var cpuCoresText = "-";
            if (nanoCpus > 0)
            {
                cpuCoresText = (nanoCpus / 1000000000d).ToString("0.##", CultureInfo.InvariantCulture);
            }
            else if (cpuQuota > 0 && cpuPeriod > 0)
            {
                cpuCoresText = (cpuQuota / (double)cpuPeriod).ToString("0.##", CultureInfo.InvariantCulture);
            }
            else
            {
                var cpusetCount = CountCpuSetCores((parts[5] ?? string.Empty).Trim());
                if (cpusetCount > 0)
                {
                    cpuCoresText = cpusetCount.ToString(CultureInfo.InvariantCulture);
                }
            }

            limitInfo = new DockerContainerLimitInfo(
                cpuCoresText,
                memoryBytes <= 0 ? "-" : FormatBytes(memoryBytes));
            return true;
        }

        private static bool TryResolveContainerPorts(
            string portBindingsJson,
            string summaryPorts,
            string exposedPortsJson,
            out string portsText)
        {
            portsText = string.Empty;
            List<string> bindings;
            var bindingsValid = TryParsePortBindings(portBindingsJson, out bindings);
            if (bindingsValid && bindings.Count > 0)
            {
                portsText = string.Join(", ", bindings.Distinct(StringComparer.OrdinalIgnoreCase));
                return true;
            }

            var summary = (summaryPorts ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(summary))
            {
                portsText = summary;
                return true;
            }

            List<string> exposed;
            var exposedValid = TryParseExposedPorts(exposedPortsJson, out exposed);
            if (exposedValid && exposed.Count > 0)
            {
                portsText = string.Join(", ", exposed.Distinct(StringComparer.OrdinalIgnoreCase));
                return true;
            }

            if (bindingsValid && exposedValid)
            {
                portsText = "-";
                return true;
            }

            return false;
        }

        private static bool TryParsePortBindings(string json, out List<string> ports)
        {
            ports = new List<string>();
            var text = (json ?? string.Empty).Trim();
            if (string.Equals(text, "null", StringComparison.OrdinalIgnoreCase) || text == "{}")
            {
                return true;
            }

            if (!text.StartsWith("{", StringComparison.Ordinal) || !text.EndsWith("}", StringComparison.Ordinal))
            {
                return false;
            }

            var keyMatches = Regex.Matches(text, "\"([0-9]+/(tcp|udp))\"\\s*:\\s*\\[(.*?)\\]");
            if (keyMatches.Count == 0)
            {
                return false;
            }

            for (var i = 0; i < keyMatches.Count; i++)
            {
                var containerPort = keyMatches[i].Groups[1].Value;
                var block = keyMatches[i].Groups[3].Value;
                var hostPortMatches = Regex.Matches(block, "\"HostPort\"\\s*:\\s*\"([0-9]*)\"");
                if (hostPortMatches.Count == 0)
                {
                    ports.Add(":" + containerPort);
                    continue;
                }

                for (var j = 0; j < hostPortMatches.Count; j++)
                {
                    var hostPort = hostPortMatches[j].Groups[1].Value;
                    ports.Add((string.IsNullOrWhiteSpace(hostPort) ? "0" : hostPort) + ":" + containerPort);
                }
            }

            return true;
        }

        private static bool TryParseExposedPorts(string json, out List<string> ports)
        {
            ports = new List<string>();
            var text = (json ?? string.Empty).Trim();
            if (string.Equals(text, "null", StringComparison.OrdinalIgnoreCase) || text == "{}")
            {
                return true;
            }

            if (!text.StartsWith("{", StringComparison.Ordinal) || !text.EndsWith("}", StringComparison.Ordinal))
            {
                return false;
            }

            var matches = Regex.Matches(text, "\"([0-9]+/(tcp|udp))\"");
            if (matches.Count == 0)
            {
                return false;
            }

            for (var i = 0; i < matches.Count; i++)
            {
                ports.Add(":" + matches[i].Groups[1].Value);
            }

            return true;
        }

        private void ApplyContainerDetailBatch(
            string contextName,
            string deviceName,
            List<ContainerDetailResult> details,
            Dictionary<string, DockerContainerStatsInfo> statsById,
            int version,
            CancellationToken token)
        {
            if (!IsContainerRefreshCurrent(contextName, version, token))
            {
                return;
            }

            var changed = false;
            for (var i = 0; i < details.Count; i++)
            {
                var detail = details[i];
                var index = _containerRows.FindIndex(row =>
                    string.Equals(row.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(row.FullId, detail.FullId, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    continue;
                }

                var current = _containerRows[index];
                DockerContainerStatsInfo stats;
                var hasStats = TryGetContainerStats(statsById, detail.FullId, out stats);
                var cpuCores = detail.HasLimits ? detail.CpuCores : current.CpuCores;
                var cpuPercent = hasStats ? stats.CpuPercentText : current.CpuPercent;
                var memoryUsage = hasStats ? stats.MemoryUsageText : current.MemoryUsage;
                if (detail.HasLimits)
                {
                    memoryUsage = MergeMemoryUsageWithLimit(memoryUsage, detail.MemoryLimit);
                }

                var memoryPercent = hasStats ? stats.MemoryPercentText : current.MemoryPercent;
                var blockIo = hasStats ? stats.BlockIoText : current.DiskReadWrite;
                var ports = detail.HasPorts ? detail.Ports : current.Ports;
                var size = detail.HasSize ? detail.Size : current.Size;

                if (string.Equals(current.CpuCores, cpuCores, StringComparison.Ordinal) &&
                    string.Equals(current.CpuPercent, cpuPercent, StringComparison.Ordinal) &&
                    string.Equals(current.MemoryUsage, memoryUsage, StringComparison.Ordinal) &&
                    string.Equals(current.MemoryPercent, memoryPercent, StringComparison.Ordinal) &&
                    string.Equals(current.DiskReadWrite, blockIo, StringComparison.Ordinal) &&
                    string.Equals(current.Ports, ports, StringComparison.Ordinal) &&
                    string.Equals(current.Size, size, StringComparison.Ordinal))
                {
                    continue;
                }

                _containerRows[index] = new ContainerComposeRow(
                    current.DeviceName, current.Id, current.Name, current.ChineseName, current.Image, current.Status,
                    ports, cpuCores, cpuPercent, memoryUsage, memoryPercent,
                    blockIo, current.Detail, current.FullId, size);
                changed = true;
            }

            if (changed && string.Equals(GetSelectedContainerDeviceName(), deviceName, StringComparison.OrdinalIgnoreCase))
            {
                BindContainerRows(deviceName);
            }
        }

        private DockerCommandExecutionResult ExecuteDockerCommandDetailed(string commandArgs, string contextName, int timeoutMs)
        {
            if (!string.IsNullOrWhiteSpace(contextName) && contextName.StartsWith(SshContextPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var identity = contextName.Substring(SshContextPrefix.Length).Trim();
                return ExecuteDockerCommandDetailedOverSsh(identity, commandArgs, timeoutMs);
            }

            var fallbackSshIdentity = string.Empty;
            if (!string.IsNullOrWhiteSpace(contextName))
            {
                string resolvedIdentity;
                if (TryResolveSshIdentityForContext(contextName, out resolvedIdentity) &&
                    _sshPasswordByTarget.ContainsKey(resolvedIdentity))
                {
                    fallbackSshIdentity = resolvedIdentity;
                }
            }

            var args = string.IsNullOrWhiteSpace(contextName)
                ? commandArgs
                : string.Format("--context \"{0}\" {1}", contextName, commandArgs);
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            try
            {
                using (var process = Process.Start(psi))
                {
                    if (process == null)
                    {
                        return DockerCommandExecutionResult.NotStartedWithError("无法启动 docker 进程。");
                    }

                    var stdOutTask = process.StandardOutput.ReadToEndAsync();
                    var stdErrTask = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(timeoutMs))
                    {
                        try { process.Kill(); } catch { }
                        string timedOutStdOut;
                        string timedOutStdErr;
                        CollectProcessStreams(stdOutTask, stdErrTask, 3000, out timedOutStdOut, out timedOutStdErr);
                        if (!string.IsNullOrWhiteSpace(fallbackSshIdentity) && string.IsNullOrWhiteSpace(timedOutStdOut))
                        {
                            return ExecuteDockerCommandDetailedOverSsh(fallbackSshIdentity, commandArgs, timeoutMs);
                        }

                        return new DockerCommandExecutionResult(true, -1, timedOutStdOut, timedOutStdErr, true);
                    }

                    string stdOut;
                    string stdErr;
                    CollectProcessStreams(stdOutTask, stdErrTask, 3000, out stdOut, out stdErr);
                    var localResult = new DockerCommandExecutionResult(true, process.ExitCode, stdOut, stdErr, false);
                    if (!localResult.Succeeded &&
                        !string.IsNullOrWhiteSpace(fallbackSshIdentity) &&
                        string.IsNullOrWhiteSpace(localResult.StandardOutput))
                    {
                        return ExecuteDockerCommandDetailedOverSsh(fallbackSshIdentity, commandArgs, timeoutMs);
                    }

                    return localResult;
                }
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrWhiteSpace(fallbackSshIdentity))
                {
                    return ExecuteDockerCommandDetailedOverSsh(fallbackSshIdentity, commandArgs, timeoutMs);
                }

                return DockerCommandExecutionResult.NotStartedWithError(ex.Message);
            }
        }

        private DockerCommandExecutionResult ExecuteDockerCommandDetailedOverSsh(string targetIdentity, string commandArgs, int timeoutMs)
        {
            string user;
            string host;
            string password;
            if (!TryResolveSshTarget(targetIdentity, out user, out host, out password))
            {
                return DockerCommandExecutionResult.NotStartedWithError("无法解析 SSH 目标。");
            }

            var effectiveTimeoutMs = Math.Max(timeoutMs, 15000);
            var escapedPassword = EscapeShellSingleQuoted(password);
            var candidates = new List<string>
            {
                "docker " + commandArgs,
                "/usr/bin/docker " + commandArgs,
                "/usr/local/bin/docker " + commandArgs,
                string.Format("printf '%s\\n' '{0}' | sudo -S -p '' docker {1}", escapedPassword, commandArgs),
                string.Format("printf '%s\\n' '{0}' | sudo -S -p '' /usr/bin/docker {1}", escapedPassword, commandArgs),
                string.Format("printf '%s\\n' '{0}' | sudo -S -p '' /usr/local/bin/docker {1}", escapedPassword, commandArgs)
            };
            var identity = BuildSshTargetIdentity(user, host);
            var candidateIndexes = new List<int>();
            int preferredIndex;
            var hasPreferred = _preferredSshDockerCandidateIndex.TryGetValue(identity, out preferredIndex) &&
                               preferredIndex >= 0 &&
                               preferredIndex < candidates.Count;
            if (hasPreferred)
            {
                candidateIndexes.Add(preferredIndex);
            }

            for (var i = 0; i < candidates.Count; i++)
            {
                if (!hasPreferred || i != preferredIndex)
                {
                    candidateIndexes.Add(i);
                }
            }

            var lastResult = DockerCommandExecutionResult.NotStartedWithError("远端 Docker 命令执行失败。");
            for (var i = 0; i < candidateIndexes.Count; i++)
            {
                var candidateIndex = candidateIndexes[i];
                string stdOut;
                string stdErr;
                var ok = ExecuteSshRemoteCommand(
                    user,
                    host,
                    password,
                    candidates[candidateIndex],
                    effectiveTimeoutMs,
                    out stdOut,
                    out stdErr,
                    "ssh-docker-detail-batch[" + candidateIndex.ToString(CultureInfo.InvariantCulture) + "]");
                lastResult = new DockerCommandExecutionResult(true, ok ? 0 : 1, stdOut, stdErr, false);
                if (ok)
                {
                    _lastSshErrorByTarget.Remove(identity);
                    _preferredSshDockerCandidateIndex[identity] = candidateIndex;
                    return lastResult;
                }

                if (!string.IsNullOrWhiteSpace(stdOut))
                {
                    return lastResult;
                }
            }

            _lastSshErrorByTarget[identity] = string.IsNullOrWhiteSpace(lastResult.StandardError)
                ? "远端命令执行失败（无 stdout/stderr 输出）。"
                : lastResult.StandardError.Trim();
            return lastResult;
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
            public ContainerDetailResult(
                string fullId,
                bool hasPorts,
                string ports,
                bool hasLimits,
                string cpuCores,
                string memoryLimit,
                bool hasSize,
                string size)
            {
                FullId = fullId;
                HasPorts = hasPorts;
                Ports = ports;
                HasLimits = hasLimits;
                CpuCores = cpuCores;
                MemoryLimit = memoryLimit;
                HasSize = hasSize;
                Size = size;
            }

            public string FullId { get; }
            public bool HasPorts { get; }
            public string Ports { get; }
            public bool HasLimits { get; }
            public string CpuCores { get; }
            public string MemoryLimit { get; }
            public bool HasSize { get; }
            public string Size { get; }
        }

        private sealed class ContainerDetailBatchResult
        {
            public ContainerDetailBatchResult(DockerCommandExecutionResult execution, List<ContainerDetailResult> details)
            {
                Execution = execution;
                Details = details ?? new List<ContainerDetailResult>();
            }

            public DockerCommandExecutionResult Execution { get; }
            public List<ContainerDetailResult> Details { get; }
        }

        private sealed class DockerCommandExecutionResult
        {
            public static readonly DockerCommandExecutionResult NotStarted =
                new DockerCommandExecutionResult(false, -1, string.Empty, string.Empty, false);

            public DockerCommandExecutionResult(bool started, int exitCode, string standardOutput, string standardError, bool timedOut)
            {
                Started = started;
                ExitCode = exitCode;
                StandardOutput = standardOutput ?? string.Empty;
                StandardError = standardError ?? string.Empty;
                TimedOut = timedOut;
            }

            public bool Started { get; }
            public int ExitCode { get; }
            public string StandardOutput { get; }
            public string StandardError { get; }
            public bool TimedOut { get; }
            public bool Succeeded => Started && !TimedOut && ExitCode == 0;

            public static DockerCommandExecutionResult NotStartedWithError(string error)
            {
                return new DockerCommandExecutionResult(false, -1, string.Empty, error, false);
            }
        }
    }
}
