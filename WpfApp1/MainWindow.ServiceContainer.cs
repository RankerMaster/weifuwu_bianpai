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
        private readonly object _remoteDockerRouteLock = new object();
        private readonly Dictionary<string, RemoteDockerRoute> _remoteDockerRoutes = new Dictionary<string, RemoteDockerRoute>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lightweightDetailCacheLock = new object();
        private readonly Dictionary<string, LightweightDetailSnapshot> _lightweightDetailCache = new Dictionary<string, LightweightDetailSnapshot>(StringComparer.OrdinalIgnoreCase);

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
            var refreshState = BeginContainerDetailRefresh(contextName);
            var summaryVersion = refreshState.Item1;
            var query = await Task.Run(() => QueryContainerSummary(contextName, refreshState.Item2));

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

            var containerIdSnapshot = BuildContainerIdSnapshot(query.Containers);
            StartContainerDetailsRefresh(contextName, deviceName, query.Containers, containerIdSnapshot, refreshState.Item1, refreshState.Item2);
        }

        private ContainerSummaryQueryResult QueryContainerSummary(
            string contextName,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var stopwatch = Stopwatch.StartNew();
            const string command = "ps -a --no-trunc --format \"{{.ID}}|{{.Names}}|{{.Image}}|{{.Status}}|{{.Ports}}|{{.Command}}\"";
            var execution = ExecuteDockerCommandDetailed(command, contextName, 10000, cancellationToken);
            stopwatch.Stop();
            RecordContainerRefreshTiming("container-summary", contextName, command, stopwatch.ElapsedMilliseconds, execution.Succeeded);
            if (!execution.Succeeded)
            {
                var error = string.IsNullOrWhiteSpace(execution.StandardError)
                    ? execution.StandardOutput
                    : execution.StandardError;
                return ContainerSummaryQueryResult.Failed(error);
            }

            var containers = new List<DockerContainerInfo>();
            var lines = execution.StandardOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < lines.Length; i++)
            {
                var parts = lines[i].Split(new[] { '|' }, 6);
                if (parts.Length < 6 || string.IsNullOrWhiteSpace(parts[0]))
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
                    "-",
                    parts[5].Trim()));
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
            var remoteRows = containers ?? new List<DockerContainerInfo>();
            var remoteIds = new HashSet<string>(remoteRows.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);

            _containerRows.RemoveAll(row =>
                string.Equals(row.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase) &&
                !remoteIds.Contains(row.FullId ?? string.Empty));

            var rowIndexes = _containerRows
                .Select((row, index) => new { Row = row, Index = index })
                .Where(item => string.Equals(item.Row.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase) &&
                               !string.IsNullOrWhiteSpace(item.Row.FullId))
                .GroupBy(item => item.Row.FullId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Index, StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < remoteRows.Count; i++)
            {
                var container = remoteRows[i];
                int index;
                if (!rowIndexes.TryGetValue(container.Id, out index))
                {
                    index = -1;
                }
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
                        "-",
                        container.Command));
                    rowIndexes[container.Id] = _containerRows.Count - 1;
                    continue;
                }

                var current = _containerRows[index];
                var summaryPorts = string.IsNullOrWhiteSpace(container.Ports) ? current.Ports : container.Ports;
                var isRunning = string.Equals(container.State, "running", StringComparison.OrdinalIgnoreCase);
                var cpuPercent = isRunning ? current.CpuPercent : "-";
                var memoryUsage = isRunning ? current.MemoryUsage : "-";
                var memoryPercent = isRunning ? current.MemoryPercent : "-";
                var diskReadWrite = isRunning ? current.DiskReadWrite : "-";
                if (!string.Equals(current.Name, normalizedName, StringComparison.Ordinal) ||
                    !string.Equals(current.Image, NormalizeImageNameWithTag(container.Image), StringComparison.Ordinal) ||
                    !string.Equals(current.Status, container.State, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(current.Ports, summaryPorts, StringComparison.Ordinal) ||
                    !string.Equals(current.CpuPercent, cpuPercent, StringComparison.Ordinal) ||
                    !string.Equals(current.MemoryUsage, memoryUsage, StringComparison.Ordinal) ||
                    !string.Equals(current.MemoryPercent, memoryPercent, StringComparison.Ordinal) ||
                    !string.Equals(current.DiskReadWrite, diskReadWrite, StringComparison.Ordinal) ||
                    !string.Equals(current.Detail, container.Status, StringComparison.Ordinal) ||
                    !string.Equals(current.Command, container.Command, StringComparison.Ordinal))
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
                        cpuPercent,
                        memoryUsage,
                        memoryPercent,
                        diskReadWrite,
                        container.Status,
                        current.FullId,
                        current.Size,
                        container.Command);
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

        private void CancelAllContainerBackgroundRefreshes()
        {
            lock (_containerRefreshStateLock)
            {
                foreach (var source in _containerRefreshCancellations.Values.ToList())
                {
                    source.Cancel();
                    source.Dispose();
                }

                _containerRefreshCancellations.Clear();
                var keys = _containerRefreshVersions.Keys.ToList();
                for (var i = 0; i < keys.Count; i++)
                {
                    _containerRefreshVersions[keys[i]] = _containerRefreshVersions[keys[i]] + 1;
                }
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
                var fixedContainerIds = (containerIdSnapshot ?? new List<string>())
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (fixedContainerIds.Count == 0 || !IsContainerRefreshCurrent(contextName, version, token))
                {
                    return;
                }

                var allowedIds = new HashSet<string>(fixedContainerIds, StringComparer.OrdinalIgnoreCase);
                var summaryById = (containers ?? new List<DockerContainerInfo>())
                    .Where(container => container != null && allowedIds.Contains(container.Id))
                    .GroupBy(container => container.Id, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                var batches = BuildContainerIdBatches(fixedContainerIds, 40, 6000);
                LightweightDetailSnapshot cachedSnapshot;
                var hasFreshSnapshot = TryGetFreshLightweightDetailSnapshot(contextName, out cachedSnapshot);
                var statsById = hasFreshSnapshot
                    ? new Dictionary<string, DockerContainerStatsInfo>(cachedSnapshot.StatsById, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, DockerContainerStatsInfo>(StringComparer.OrdinalIgnoreCase);
                var hostCpuLimitText = hasFreshSnapshot ? cachedSnapshot.HostCpuLimitText : string.Empty;
                var batchResults = new List<ContainerDetailBatchResult>();
                DockerCommandExecutionResult aggregateExecution;

                if (contextName.StartsWith(SshContextPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var combined = await Task.Run(() => ReadCombinedRemoteContainerDetails(
                        contextName, batches, allowedIds, summaryById, statsById, hostCpuLimitText, hasFreshSnapshot, token));
                    if (!IsContainerRefreshCurrent(contextName, version, token) || combined.Execution.Cancelled)
                    {
                        return;
                    }

                    statsById = combined.StatsById;
                    hostCpuLimitText = combined.HostCpuLimitText;
                    batchResults.AddRange(combined.Batches);
                    aggregateExecution = combined.Execution;
                }
                else
                {
                    var statsWatch = Stopwatch.StartNew();
                    if (!hasFreshSnapshot)
                    {
                        var statsExecution = await Task.Run(() => ExecuteDockerCommandDetailed(
                            "stats --no-stream --no-trunc --format \"{{.ID}}|{{.CPUPerc}}|{{.MemUsage}}|{{.MemPerc}}|{{.BlockIO}}\"",
                            contextName, 30000, token));
                        statsById = ParseDockerContainerStatsOutput(statsExecution.StandardOutput);
                        var infoExecution = await Task.Run(() => ExecuteDockerCommandDetailed(
                            "info --format \"{{.NCPU}}\"", contextName, 15000, token));
                        hostCpuLimitText = ParseHostCpuLimitText(infoExecution.StandardOutput);
                    }
                    statsWatch.Stop();
                    RecordContainerRefreshTiming("container-stats-info", contextName, "stats/info", statsWatch.ElapsedMilliseconds, !token.IsCancellationRequested);

                    aggregateExecution = DockerCommandExecutionResult.SuccessfulEmpty;
                    for (var i = 0; i < batches.Count; i++)
                    {
                        if (!IsContainerRefreshCurrent(contextName, version, token))
                        {
                            return;
                        }
                        var batch = await Task.Run(() => ReadContainerDetailBatch(
                            contextName, batches[i], allowedIds, new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                            summaryById, hostCpuLimitText, token));
                        batchResults.Add(batch);
                        if (!batch.Execution.Succeeded)
                        {
                            aggregateExecution = batch.Execution;
                        }
                    }
                }

                if (statsById.Count > 0 && IsContainerRefreshCurrent(contextName, version, token))
                {
                    var statsUiWatch = Stopwatch.StartNew();
                    await Dispatcher.InvokeAsync(() => ApplyContainerStatsBatch(
                        contextName, deviceName, statsById, version, token));
                    statsUiWatch.Stop();
                    RecordContainerRefreshTiming("container-ui-stats", contextName, "rows=" + statsById.Count, statsUiWatch.ElapsedMilliseconds, true);
                }

                var appliedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < batchResults.Count; i++)
                {
                    if (!IsContainerRefreshCurrent(contextName, version, token))
                    {
                        return;
                    }

                    var accepted = batchResults[i].Details.Where(detail => appliedIds.Add(detail.FullId)).ToList();
                    if (accepted.Count == 0)
                    {
                        continue;
                    }

                    var uiWatch = Stopwatch.StartNew();
                    await Dispatcher.InvokeAsync(() => ApplyContainerDetailBatch(
                        contextName, deviceName, accepted, statsById, version, token));
                    uiWatch.Stop();
                    RecordContainerRefreshTiming("container-ui-batch", contextName, "rows=" + accepted.Count, uiWatch.ElapsedMilliseconds, true);
                }

                var missingIds = fixedContainerIds.Where(id => !appliedIds.Contains(id)).ToList();
                if (missingIds.Count > 0 && IsRecoverableDetailFailure(aggregateExecution))
                {
                    var retryBatches = BuildContainerIdBatches(missingIds, 40, 6000);
                    for (var i = 0; i < retryBatches.Count; i++)
                    {
                        if (!IsContainerRefreshCurrent(contextName, version, token))
                        {
                            return;
                        }

                        var retry = await Task.Run(() => ReadContainerDetailBatch(
                            contextName, retryBatches[i], allowedIds, appliedIds,
                            summaryById, hostCpuLimitText, token));
                        if (retry.Execution.Cancelled)
                        {
                            return;
                        }

                        var accepted = retry.Details.Where(detail => appliedIds.Add(detail.FullId)).ToList();
                        if (accepted.Count > 0)
                        {
                            await Dispatcher.InvokeAsync(() => ApplyContainerDetailBatch(
                                contextName, deviceName, accepted, statsById, version, token));
                        }
                    }
                }

            }
            catch (OperationCanceledException)
            {
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
            string hostCpuLimitText,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var ids = (containerIds ?? new List<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList();
            if (ids.Count == 0)
            {
                return new ContainerDetailBatchResult(DockerCommandExecutionResult.NotStarted, new List<ContainerDetailResult>());
            }

            const string format = "{{.Id}}|{{.HostConfig.NanoCpus}}|{{.HostConfig.Memory}}|{{.HostConfig.CpuQuota}}|{{.HostConfig.CpuPeriod}}|{{.HostConfig.CpusetCpus}}|{{json .HostConfig.PortBindings}}|{{json .Config.ExposedPorts}}";
            var command = "container inspect --format \"" + format + "\" " +
                          string.Join(" ", ids.Select(id => string.Format("\"{0}\"", id)));
            var stopwatch = Stopwatch.StartNew();
            var execution = ExecuteDockerCommandDetailed(command, contextName, 30000, cancellationToken);
            stopwatch.Stop();
            RecordContainerRefreshTiming("container-inspect-batch", contextName, "ids=" + ids.Count, stopwatch.ElapsedMilliseconds, execution.Succeeded);
            var parseWatch = Stopwatch.StartNew();
            var details = ParseContainerInspectBatch(
                execution.StandardOutput, allowedIds, alreadyAppliedIds, summaryById, hostCpuLimitText);
            parseWatch.Stop();
            RecordContainerRefreshTiming("container-parse-batch", contextName, "ids=" + ids.Count, parseWatch.ElapsedMilliseconds, true);
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
                if (parts.Length < 8)
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
                long sizeBytes = 0;
                var hasSize = parts.Length > 8 &&
                              long.TryParse((parts[8] ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out sizeBytes) &&
                              sizeBytes >= 0;
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
            var rowIndexes = _containerRows
                .Select((row, index) => new { Row = row, Index = index })
                .Where(item => string.Equals(item.Row.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase) &&
                               !string.IsNullOrWhiteSpace(item.Row.FullId))
                .GroupBy(item => item.Row.FullId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Index, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < details.Count; i++)
            {
                var detail = details[i];
                int index;
                if (!rowIndexes.TryGetValue(detail.FullId, out index))
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
                    blockIo, current.Detail, current.FullId, size, current.Command);
                changed = true;
            }

            if (changed && string.Equals(GetSelectedContainerDeviceName(), deviceName, StringComparison.OrdinalIgnoreCase))
            {
                BindContainerRows(deviceName);
            }
        }

        private void ApplyContainerStatsBatch(
            string contextName,
            string deviceName,
            Dictionary<string, DockerContainerStatsInfo> statsById,
            int version,
            CancellationToken token)
        {
            if (!IsContainerRefreshCurrent(contextName, version, token) || statsById == null || statsById.Count == 0)
            {
                return;
            }

            var indexes = _containerRows
                .Select((row, index) => new { Row = row, Index = index })
                .Where(item => string.Equals(item.Row.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase) &&
                               !string.IsNullOrWhiteSpace(item.Row.FullId))
                .GroupBy(item => item.Row.FullId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Index, StringComparer.OrdinalIgnoreCase);
            var changed = false;
            foreach (var pair in indexes)
            {
                DockerContainerStatsInfo stats;
                if (!TryGetContainerStats(statsById, pair.Key, out stats) || stats == null)
                {
                    continue;
                }

                var index = pair.Value;
                var current = _containerRows[index];
                if (string.Equals(current.CpuPercent, stats.CpuPercentText, StringComparison.Ordinal) &&
                    string.Equals(current.MemoryUsage, stats.MemoryUsageText, StringComparison.Ordinal) &&
                    string.Equals(current.MemoryPercent, stats.MemoryPercentText, StringComparison.Ordinal) &&
                    string.Equals(current.DiskReadWrite, stats.BlockIoText, StringComparison.Ordinal))
                {
                    continue;
                }

                _containerRows[index] = new ContainerComposeRow(
                    current.DeviceName, current.Id, current.Name, current.ChineseName, current.Image, current.Status,
                    current.Ports, current.CpuCores, stats.CpuPercentText, stats.MemoryUsageText,
                    stats.MemoryPercentText, stats.BlockIoText, current.Detail, current.FullId, current.Size, current.Command);
                changed = true;
            }

            if (changed && string.Equals(GetSelectedContainerDeviceName(), deviceName, StringComparison.OrdinalIgnoreCase))
            {
                BindContainerRows(deviceName);
            }
        }

        private static List<List<string>> BuildContainerIdBatches(IList<string> containerIds, int maximumCount, int maximumCommandLength)
        {
            var result = new List<List<string>>();
            var current = new List<string>();
            var currentLength = 0;
            foreach (var rawId in containerIds ?? new List<string>())
            {
                var id = (rawId ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var addedLength = id.Length + 3;
                if (current.Count > 0 && (current.Count >= maximumCount || currentLength + addedLength > maximumCommandLength))
                {
                    result.Add(current);
                    current = new List<string>();
                    currentLength = 0;
                }

                current.Add(id);
                currentLength += addedLength;
            }

            if (current.Count > 0)
            {
                result.Add(current);
            }
            return result;
        }

        private void RecordContainerRefreshTiming(
            string stage,
            string contextName,
            string operation,
            long elapsedMilliseconds,
            bool succeeded)
        {
            Debug.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "[ContainerRefreshTiming] {0} | {1} | {2} | {3} ms | {4}",
                stage,
                contextName,
                operation,
                elapsedMilliseconds,
                succeeded ? "OK" : "FAIL"));
            AppendReadTiming(stage, contextName, operation, elapsedMilliseconds, succeeded);
        }

        private bool TryGetFreshLightweightDetailSnapshot(string contextName, out LightweightDetailSnapshot snapshot)
        {
            lock (_lightweightDetailCacheLock)
            {
                if (_lightweightDetailCache.TryGetValue((contextName ?? string.Empty).Trim(), out snapshot) &&
                    DateTime.UtcNow - snapshot.CapturedUtc <= TimeSpan.FromSeconds(30))
                {
                    return true;
                }
            }

            snapshot = null;
            return false;
        }

        private static string ParseHostCpuLimitText(string output)
        {
            double value;
            var line = (output ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault();
            return double.TryParse((line ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0
                ? value.ToString("0.##", CultureInfo.InvariantCulture)
                : "-";
        }

        private static bool IsRecoverableDetailFailure(DockerCommandExecutionResult execution)
        {
            if (execution == null || execution.Cancelled || execution.TimedOut)
            {
                return false;
            }

            if (execution.FailureKind == DockerCommandFailureKind.PartialOutput)
            {
                return true;
            }

            var error = (execution.StandardError ?? string.Empty).ToLowerInvariant();
            return error.Contains("no such object") ||
                   error.Contains("no such container") ||
                   error.Contains("has disappeared");
        }

        private CombinedContainerRefreshResult ReadCombinedRemoteContainerDetails(
            string contextName,
            List<List<string>> batches,
            HashSet<string> allowedIds,
            Dictionary<string, DockerContainerInfo> summaryById,
            Dictionary<string, DockerContainerStatsInfo> cachedStats,
            string cachedHostCpu,
            bool reuseLightweightSnapshot,
            CancellationToken cancellationToken)
        {
            var identity = contextName.Substring(SshContextPrefix.Length).Trim();
            var markerRoot = "__WPFAPP1_REFRESH_" + Guid.NewGuid().ToString("N") + "_";
            RemoteDockerRoute route;
            DockerCommandExecutionResult probeFailure;
            if (!TryEnsureRemoteDockerRoute(identity, cancellationToken, false, out route, out probeFailure))
            {
                return CombinedContainerRefreshResult.Failed(probeFailure, cachedStats, cachedHostCpu);
            }

            string user;
            string host;
            string password;
            if (!TryResolveSshTarget(identity, out user, out host, out password))
            {
                return CombinedContainerRefreshResult.Failed(
                    DockerCommandExecutionResult.NotStartedWithError("Unable to resolve SSH target."), cachedStats, cachedHostCpu);
            }

            var script = BuildCombinedContainerRefreshScript(route, password, batches, markerRoot, !reuseLightweightSnapshot);
            var queryWatch = Stopwatch.StartNew();
            var execution = ExecuteRemoteReadOnlyScript(user, host, password, script, cancellationToken);
            if ((execution.FailureKind == DockerCommandFailureKind.CommandNotFound ||
                 execution.FailureKind == DockerCommandFailureKind.DockerPermission) &&
                !execution.Cancelled)
            {
                lock (_remoteDockerRouteLock)
                {
                    _remoteDockerRoutes.Remove(NormalizeSshIdentity(identity));
                }
                if (TryEnsureRemoteDockerRoute(identity, cancellationToken, true, out route, out probeFailure))
                {
                    script = BuildCombinedContainerRefreshScript(route, password, batches, markerRoot, !reuseLightweightSnapshot);
                    execution = ExecuteRemoteReadOnlyScript(user, host, password, script, cancellationToken);
                }
                else
                {
                    execution = probeFailure;
                }
            }
            queryWatch.Stop();
            RecordContainerRefreshTiming("container-combined-query", contextName, "stats/info/inspect", queryWatch.ElapsedMilliseconds, execution.Succeeded);
            if (execution.Cancelled)
            {
                return CombinedContainerRefreshResult.Failed(execution, cachedStats, cachedHostCpu);
            }

            var parseWatch = Stopwatch.StartNew();
            var stats = cachedStats ?? new Dictionary<string, DockerContainerStatsInfo>(StringComparer.OrdinalIgnoreCase);
            var hostCpu = cachedHostCpu;
            if (!reuseLightweightSnapshot)
            {
                string statsSection;
                if (TryExtractMarkedSection(execution.StandardOutput, markerRoot + "STATS_BEGIN", markerRoot + "STATS_END", out statsSection))
                {
                    stats = ParseDockerContainerStatsOutput(statsSection);
                }

                string infoSection;
                if (TryExtractMarkedSection(execution.StandardOutput, markerRoot + "INFO_BEGIN", markerRoot + "INFO_END", out infoSection))
                {
                    hostCpu = ParseHostCpuLimitText(infoSection);
                }
            }

            var detailBatches = new List<ContainerDetailBatchResult>();
            for (var i = 0; i < batches.Count; i++)
            {
                string inspectSection;
                var parsed = new List<ContainerDetailResult>();
                if (TryExtractMarkedSection(
                    execution.StandardOutput,
                    markerRoot + "INSPECT_" + i.ToString(CultureInfo.InvariantCulture) + "_BEGIN",
                    markerRoot + "INSPECT_" + i.ToString(CultureInfo.InvariantCulture) + "_END",
                    out inspectSection))
                {
                    parsed = ParseContainerInspectBatch(
                        inspectSection, allowedIds, new HashSet<string>(StringComparer.OrdinalIgnoreCase), summaryById, hostCpu);
                }
                detailBatches.Add(new ContainerDetailBatchResult(execution, parsed));
            }
            parseWatch.Stop();
            RecordContainerRefreshTiming("container-combined-parse", contextName, "batches=" + batches.Count, parseWatch.ElapsedMilliseconds, true);
            return new CombinedContainerRefreshResult(execution, stats, hostCpu, detailBatches);
        }

        private static string BuildCombinedContainerRefreshScript(
            RemoteDockerRoute route,
            string password,
            List<List<string>> batches,
            string markerRoot,
            bool includeStatsAndInfo)
        {
            var dockerFunction = route.UseSudo
                ? string.Format("docker_cmd() {{ printf '%s\\n' '{0}' | sudo -S -p '' {1} \"$@\"; }}; ", EscapeShellSingleQuoted(password), route.ExecutablePath)
                : string.Format("docker_cmd() {{ {0} \"$@\"; }}; ", route.ExecutablePath);
            var script = "set +e; export LC_ALL=C; " + dockerFunction +
                         "docker_cmd version --format '{{.Server.Version}}' >/dev/null || exit $?; ";
            if (includeStatsAndInfo)
            {
                script += "printf '%s\\n' '" + markerRoot + "STATS_BEGIN'; " +
                          "docker_cmd stats --no-stream --no-trunc --format '{{.ID}}|{{.CPUPerc}}|{{.MemUsage}}|{{.MemPerc}}|{{.BlockIO}}'; " +
                          "printf '%s\\n' '" + markerRoot + "STATS_END' '" + markerRoot + "INFO_BEGIN'; " +
                          "docker_cmd info --format '{{.NCPU}}'; " +
                          "printf '%s\\n' '" + markerRoot + "INFO_END'; ";
            }

            const string inspectFormat = "{{.Id}}|{{.HostConfig.NanoCpus}}|{{.HostConfig.Memory}}|{{.HostConfig.CpuQuota}}|{{.HostConfig.CpuPeriod}}|{{.HostConfig.CpusetCpus}}|{{json .HostConfig.PortBindings}}|{{json .Config.ExposedPorts}}";
            for (var i = 0; i < batches.Count; i++)
            {
                var number = i.ToString(CultureInfo.InvariantCulture);
                script += "printf '%s\\n' '" + markerRoot + "INSPECT_" + number + "_BEGIN'; " +
                          "docker_cmd container inspect --format '" + inspectFormat + "' " + string.Join(" ", batches[i]) + "; " +
                          "printf '%s\\n' '" + markerRoot + "INSPECT_" + number + "_END'; ";
            }
            return script + "printf '%s\\n' '" + markerRoot + "COMPLETE'";
        }

        private DockerCommandExecutionResult ExecuteRemoteReadOnlyScript(
            string user,
            string host,
            string password,
            string script,
            CancellationToken cancellationToken)
        {
            string stdOut;
            string stdErr;
            var ok = ExecuteSshRemoteCommand(
                user, host, password, script, 60000, out stdOut, out stdErr,
                "ssh-container-combined", cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return DockerCommandExecutionResult.CancelledResult(stdOut, stdErr);
            }
            return CreateDockerCommandResult(true, ok ? 0 : 1, stdOut, stdErr, false, false);
        }

        private DockerCommandExecutionResult ExecuteDockerCommandDetailed(
            string commandArgs,
            string contextName,
            int timeoutMs,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(contextName) && contextName.StartsWith(SshContextPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return ExecuteDockerCommandDetailedOverSsh(
                    contextName.Substring(SshContextPrefix.Length).Trim(),
                    commandArgs,
                    timeoutMs,
                    cancellationToken);
            }

            string fallbackSshIdentity;
            var hasSshFallback = TryResolveSshIdentityForContext(contextName, out fallbackSshIdentity) &&
                                 _sshPasswordByTarget.ContainsKey(fallbackSshIdentity);
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
                        return DockerCommandExecutionResult.NotStartedWithError("Unable to start docker process.");
                    }

                    var stdOutTask = process.StandardOutput.ReadToEndAsync();
                    var stdErrTask = process.StandardError.ReadToEndAsync();
                    using (cancellationToken.Register(() =>
                    {
                        try { if (!process.HasExited) process.Kill(); } catch { }
                    }))
                    {
                        if (!process.WaitForExit(timeoutMs))
                        {
                            try { process.Kill(); } catch { }
                            string timeoutOutput;
                            string timeoutError;
                            CollectProcessStreams(stdOutTask, stdErrTask, 3000, out timeoutOutput, out timeoutError);
                            return new DockerCommandExecutionResult(
                                true, -1, timeoutOutput, timeoutError, true, false, DockerCommandFailureKind.Timeout);
                        }
                    }

                    string stdOut;
                    string stdErr;
                    CollectProcessStreams(stdOutTask, stdErrTask, 3000, out stdOut, out stdErr);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return DockerCommandExecutionResult.CancelledResult(stdOut, stdErr);
                    }

                    var localResult = CreateDockerCommandResult(true, process.ExitCode, stdOut, stdErr, false, false);
                    if (!localResult.Succeeded && hasSshFallback && string.IsNullOrWhiteSpace(stdOut))
                    {
                        return ExecuteDockerCommandDetailedOverSsh(fallbackSshIdentity, commandArgs, timeoutMs, cancellationToken);
                    }

                    return localResult;
                }
            }
            catch (Exception ex)
            {
                return hasSshFallback
                    ? ExecuteDockerCommandDetailedOverSsh(fallbackSshIdentity, commandArgs, timeoutMs, cancellationToken)
                    : DockerCommandExecutionResult.NotStartedWithError(ex.Message);
            }
        }

        private DockerCommandExecutionResult ExecuteDockerCommandDetailedOverSsh(
            string targetIdentity,
            string commandArgs,
            int timeoutMs,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            RemoteDockerRoute route;
            DockerCommandExecutionResult probeFailure;
            if (!TryEnsureRemoteDockerRoute(targetIdentity, cancellationToken, false, out route, out probeFailure))
            {
                return probeFailure;
            }

            var result = ExecuteDockerCommandOnRemoteRoute(targetIdentity, route, commandArgs, timeoutMs, cancellationToken);
            if (result.Cancelled || result.TimedOut ||
                (result.FailureKind != DockerCommandFailureKind.CommandNotFound &&
                 result.FailureKind != DockerCommandFailureKind.DockerPermission))
            {
                return result;
            }

            lock (_remoteDockerRouteLock)
            {
                _remoteDockerRoutes.Remove(NormalizeSshIdentity(targetIdentity));
            }

            if (!TryEnsureRemoteDockerRoute(targetIdentity, cancellationToken, true, out route, out probeFailure))
            {
                return probeFailure;
            }

            return ExecuteDockerCommandOnRemoteRoute(targetIdentity, route, commandArgs, timeoutMs, cancellationToken);
        }

        private bool TryEnsureRemoteDockerRoute(
            string targetIdentity,
            CancellationToken cancellationToken,
            bool forceProbe,
            out RemoteDockerRoute route,
            out DockerCommandExecutionResult failure)
        {
            route = null;
            failure = DockerCommandExecutionResult.NotStarted;
            var normalizedIdentity = NormalizeSshIdentity(targetIdentity);
            if (!forceProbe)
            {
                lock (_remoteDockerRouteLock)
                {
                    if (_remoteDockerRoutes.TryGetValue(normalizedIdentity, out route))
                    {
                        return true;
                    }
                }
            }

            string user;
            string host;
            string password;
            if (!TryResolveSshTarget(normalizedIdentity, out user, out host, out password))
            {
                failure = DockerCommandExecutionResult.NotStartedWithError("Unable to resolve SSH target.");
                return false;
            }

            const string marker = "__WPFAPP1_DOCKER_ROUTE__";
            var escapedPassword = EscapeShellSingleQuoted(password);
            var probeScript =
                "set +e; export LC_ALL=C; " +
                "for p in docker /usr/bin/docker /usr/local/bin/docker; do " +
                "command -v \"$p\" >/dev/null 2>&1 || test -x \"$p\" || continue; " +
                "\"$p\" version --format '{{.Server.Version}}' >/dev/null 2>&1 && { printf '%s|%s|0\\n' '" + marker + "' \"$p\"; exit 0; }; " +
                "done; " +
                "for p in docker /usr/bin/docker /usr/local/bin/docker; do " +
                "command -v \"$p\" >/dev/null 2>&1 || test -x \"$p\" || continue; " +
                "printf '%s\\n' '" + escapedPassword + "' | sudo -S -p '' \"$p\" version --format '{{.Server.Version}}' >/dev/null 2>&1 && { printf '%s|%s|1\\n' '" + marker + "' \"$p\"; exit 0; }; " +
                "done; printf '%s\\n' '__WPFAPP1_DOCKER_ROUTE_FAILED__' >&2; exit 127";

            string stdOut;
            string stdErr;
            var ok = ExecuteSshRemoteCommand(
                user, host, password, probeScript, 12000, out stdOut, out stdErr,
                "ssh-docker-route-probe", cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                failure = DockerCommandExecutionResult.CancelledResult(stdOut, stdErr);
                return false;
            }

            var routeLine = (stdOut ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => line.StartsWith(marker + "|", StringComparison.Ordinal));
            var parts = (routeLine ?? string.Empty).Split('|');
            if (!ok || parts.Length != 3 || string.IsNullOrWhiteSpace(parts[1]))
            {
                failure = CreateDockerCommandResult(true, ok ? 0 : 1, stdOut, stdErr, false, false);
                return false;
            }

            route = new RemoteDockerRoute(parts[1].Trim(), string.Equals(parts[2].Trim(), "1", StringComparison.Ordinal));
            lock (_remoteDockerRouteLock)
            {
                _remoteDockerRoutes[normalizedIdentity] = route;
            }
            return true;
        }

        private DockerCommandExecutionResult ExecuteDockerCommandOnRemoteRoute(
            string targetIdentity,
            RemoteDockerRoute route,
            string commandArgs,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            string user;
            string host;
            string password;
            if (!TryResolveSshTarget(targetIdentity, out user, out host, out password))
            {
                return DockerCommandExecutionResult.NotStartedWithError("Unable to resolve SSH target.");
            }

            var remoteCommand = BuildRemoteDockerCommand(route, password, commandArgs);
            string stdOut;
            string stdErr;
            var ok = ExecuteSshRemoteCommand(
                user, host, password, remoteCommand, Math.Max(timeoutMs, 15000), out stdOut, out stdErr,
                "ssh-docker-cached-route", cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return DockerCommandExecutionResult.CancelledResult(stdOut, stdErr);
            }

            var result = CreateDockerCommandResult(true, ok ? 0 : 1, stdOut, stdErr, false, false);
            if (result.Succeeded)
            {
                _lastSshErrorByTarget.Remove(NormalizeSshIdentity(targetIdentity));
            }
            else
            {
                _lastSshErrorByTarget[NormalizeSshIdentity(targetIdentity)] =
                    string.IsNullOrWhiteSpace(stdErr) ? stdOut : stdErr;
            }
            return result;
        }

        private static string BuildRemoteDockerCommand(RemoteDockerRoute route, string password, string commandArgs)
        {
            if (route.UseSudo)
            {
                return string.Format(
                    "printf '%s\\n' '{0}' | sudo -S -p '' {1} {2}",
                    EscapeShellSingleQuoted(password), route.ExecutablePath, commandArgs);
            }

            return route.ExecutablePath + " " + commandArgs;
        }

        private static string NormalizeSshIdentity(string targetIdentity)
        {
            return (targetIdentity ?? string.Empty).Trim();
        }

        private static DockerCommandExecutionResult CreateDockerCommandResult(
            bool started,
            int exitCode,
            string stdOut,
            string stdErr,
            bool timedOut,
            bool cancelled)
        {
            var merged = ((stdErr ?? string.Empty) + "\n" + (stdOut ?? string.Empty)).ToLowerInvariant();
            var kind = DockerCommandFailureKind.Other;
            if (cancelled)
            {
                kind = DockerCommandFailureKind.Cancelled;
            }
            else if (timedOut || merged.Contains("timed out") || merged.Contains("timeout") || merged.Contains("超时"))
            {
                kind = DockerCommandFailureKind.Timeout;
            }
            else if (merged.Contains("authentication failed") ||
                     merged.Contains("permission denied (publickey,password") ||
                     merged.Contains("permission denied, please try again") ||
                     merged.Contains("access denied"))
            {
                kind = DockerCommandFailureKind.Authentication;
            }
            else if (merged.Contains("permission denied") || merged.Contains("got permission denied") || merged.Contains("sudo:"))
            {
                kind = DockerCommandFailureKind.DockerPermission;
            }
            else if (merged.Contains("not found") || merged.Contains("no such file or directory") || exitCode == 127)
            {
                kind = DockerCommandFailureKind.CommandNotFound;
            }
            else if (merged.Contains("no route to host") || merged.Contains("connection refused") || merged.Contains("network is unreachable") || merged.Contains("could not resolve hostname"))
            {
                kind = DockerCommandFailureKind.Offline;
            }
            else if (!string.IsNullOrWhiteSpace(stdOut) && exitCode != 0)
            {
                kind = DockerCommandFailureKind.PartialOutput;
            }
            else if (exitCode == 0)
            {
                kind = DockerCommandFailureKind.None;
            }

            return new DockerCommandExecutionResult(started, exitCode, stdOut, stdErr, timedOut, cancelled, kind);
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

        private sealed class CombinedContainerRefreshResult
        {
            public CombinedContainerRefreshResult(
                DockerCommandExecutionResult execution,
                Dictionary<string, DockerContainerStatsInfo> statsById,
                string hostCpuLimitText,
                List<ContainerDetailBatchResult> batches)
            {
                Execution = execution ?? DockerCommandExecutionResult.NotStarted;
                StatsById = statsById ?? new Dictionary<string, DockerContainerStatsInfo>(StringComparer.OrdinalIgnoreCase);
                HostCpuLimitText = string.IsNullOrWhiteSpace(hostCpuLimitText) ? "-" : hostCpuLimitText;
                Batches = batches ?? new List<ContainerDetailBatchResult>();
            }

            public DockerCommandExecutionResult Execution { get; }
            public Dictionary<string, DockerContainerStatsInfo> StatsById { get; }
            public string HostCpuLimitText { get; }
            public List<ContainerDetailBatchResult> Batches { get; }

            public static CombinedContainerRefreshResult Failed(
                DockerCommandExecutionResult execution,
                Dictionary<string, DockerContainerStatsInfo> statsById,
                string hostCpuLimitText)
            {
                return new CombinedContainerRefreshResult(execution, statsById, hostCpuLimitText, null);
            }
        }

        private sealed class LightweightDetailSnapshot
        {
            public LightweightDetailSnapshot(
                Dictionary<string, DockerContainerStatsInfo> statsById,
                string hostCpuLimitText,
                DateTime capturedUtc)
            {
                StatsById = statsById ?? new Dictionary<string, DockerContainerStatsInfo>(StringComparer.OrdinalIgnoreCase);
                HostCpuLimitText = string.IsNullOrWhiteSpace(hostCpuLimitText) ? "-" : hostCpuLimitText;
                CapturedUtc = capturedUtc;
            }

            public Dictionary<string, DockerContainerStatsInfo> StatsById { get; }
            public string HostCpuLimitText { get; }
            public DateTime CapturedUtc { get; }
        }

        private sealed class DockerCommandExecutionResult
        {
            public static readonly DockerCommandExecutionResult NotStarted =
                new DockerCommandExecutionResult(false, -1, string.Empty, string.Empty, false, false, DockerCommandFailureKind.Other);
            public static readonly DockerCommandExecutionResult SuccessfulEmpty =
                new DockerCommandExecutionResult(true, 0, string.Empty, string.Empty, false, false, DockerCommandFailureKind.None);

            public DockerCommandExecutionResult(bool started, int exitCode, string standardOutput, string standardError, bool timedOut)
                : this(started, exitCode, standardOutput, standardError, timedOut, false,
                    timedOut ? DockerCommandFailureKind.Timeout : (exitCode == 0 ? DockerCommandFailureKind.None : DockerCommandFailureKind.Other))
            {
            }

            public DockerCommandExecutionResult(
                bool started,
                int exitCode,
                string standardOutput,
                string standardError,
                bool timedOut,
                bool cancelled,
                DockerCommandFailureKind failureKind)
            {
                Started = started;
                ExitCode = exitCode;
                StandardOutput = standardOutput ?? string.Empty;
                StandardError = standardError ?? string.Empty;
                TimedOut = timedOut;
                Cancelled = cancelled;
                FailureKind = failureKind;
            }

            public bool Started { get; }
            public int ExitCode { get; }
            public string StandardOutput { get; }
            public string StandardError { get; }
            public bool TimedOut { get; }
            public bool Cancelled { get; }
            public DockerCommandFailureKind FailureKind { get; }
            public bool Succeeded => Started && !TimedOut && !Cancelled && ExitCode == 0;

            public static DockerCommandExecutionResult NotStartedWithError(string error)
            {
                return new DockerCommandExecutionResult(false, -1, string.Empty, error, false, false, DockerCommandFailureKind.Other);
            }

            public static DockerCommandExecutionResult CancelledResult(string standardOutput, string standardError)
            {
                return new DockerCommandExecutionResult(
                    true, -1, standardOutput, standardError, false, true, DockerCommandFailureKind.Cancelled);
            }
        }

        private sealed class RemoteDockerRoute
        {
            public RemoteDockerRoute(string executablePath, bool useSudo)
            {
                ExecutablePath = executablePath ?? string.Empty;
                UseSudo = useSudo;
            }

            public string ExecutablePath { get; }
            public bool UseSudo { get; }
        }

        private enum DockerCommandFailureKind
        {
            None,
            Cancelled,
            Timeout,
            Offline,
            Authentication,
            CommandNotFound,
            DockerPermission,
            PartialOutput,
            PermanentFormat,
            Other
        }
    }
}
