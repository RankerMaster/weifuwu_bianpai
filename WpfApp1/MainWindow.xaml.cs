using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace WpfApp1
{
    public partial class MainWindow : Window
    {
        private DeviceInfo _currentDevice;
        private readonly Dictionary<string, string> _deviceContextMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastSnapshotAt = DateTime.MinValue;
        private bool _isRefreshing;
        private string _lastOperatedContainerId = string.Empty;
        private string _lastOperatedContainerName = string.Empty;
        private string _lastOperatedContextName = string.Empty;
        private readonly List<ContainerOperationLog> _containerOperationLogs = new List<ContainerOperationLog>();
        private string _readLocalIp = string.Empty;
        private string _readTargetUser = "root";
        private string _readTargetIp = "192.168.118.88";
        private readonly HashSet<string> _readTargetIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _sshPasswordByTarget = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _lastSshErrorByTarget = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _preferredSshDockerCandidateIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private Window _containerLoadingWindow;
        private bool _hasReadDeviceInfo;
        private const string SshContextPrefix = "SSH:";
        private const string DashboardAllDevicesSelectionKey = "__DASHBOARD_ALL_DEVICES__";
        private readonly Dictionary<string, SortDescription> _gridSortStates = new Dictionary<string, SortDescription>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _hostCpuLimitTextByContext = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _hostMemoryLimitMbByContext = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _containerSizeBackfillRunningContexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _suppressContainerDeviceSelectionRefresh;
        private bool _isCollectingReadTimings;
        private readonly object _readTimingLock = new object();
        private readonly List<ReadTimingRecord> _readTimingRecords = new List<ReadTimingRecord>();
        private Stopwatch _readWallClock;
        private readonly Dictionary<string, string> _imageChineseNameByImageId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _imageChineseNameByRepoTag = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private readonly List<DeviceInfo> _devices = new List<DeviceInfo>();

        // 汇总从各设备读取到的 Docker 镜像信息，作为设备镜像列表的数据源。
        private readonly List<DeviceImageRow> _allImageRows = new List<DeviceImageRow>();

        // 保存策略页面使用的镜像卡片数据，包含镜像仓库标签、ID、创建时间和大小。
        private readonly List<DeployImageCard> _strategyImages = new List<DeployImageCard>();

        // 保存镜像编排流程中的源镜像，即可作为后续制作或组合基础的镜像。
        private readonly List<ImageComposeRow> _sourceImages = new List<ImageComposeRow>();

        // 保存已经完成制作、可供后续部署使用的预部署镜像。
        private readonly List<ImageComposeRow> _preparedImages = new List<ImageComposeRow>();

        // 保存镜像制作或容器编排时可选择的程序包文件及其文件信息。
        private readonly List<ProgramFileRow> _programFiles = new List<ProgramFileRow>();

        // 保存从设备读取到的容器运行记录，供容器列表展示和操作使用。
        private readonly List<ContainerComposeRow> _containerRows = new List<ContainerComposeRow>();

        /// <summary>
        /// 创建主窗口并完成本地初始化：加载 XAML 控件、恢复镜像中文名称、
        /// 重置运行时界面，并注册窗口加载和关闭事件。
        /// 构造阶段不会连接远程设备，设备数据由用户执行“读取设备信息”后获取。
        /// </summary>
        public MainWindow()
        {
            // 创建 XAML 中声明的控件并初始化字段引用。后续初始化代码依赖这些控件，
            // 因此 InitializeComponent 必须在所有界面访问操作之前执行。
            InitializeComponent();

            // 从持久化文件读取“镜像 ID/仓库标签 -> 中文名称”的映射，
            // 再为各 DataGrid 注册统一的列排序逻辑。
            LoadImageChineseNameStore();
            RegisterGridSortingBehavior();

            // 删除上次运行生成的设备容器 CSV 日志，避免旧日志混入本次会话。
            CleanupContainerLogCsvFiles();

            // 清空设备、镜像、容器和策略等运行时集合，同时重置仪表盘指标、
            // 设备选择器及各列表的数据源，使界面以“尚未读取设备”的状态启动。
            ClearRuntimeDataForColdStart();

            // 等窗口及其控件全部加载完成后，将当前运行时集合绑定到界面，
            // 并把侧边栏默认切换到“感知”页面，同时应用对应的选中样式。
            Loaded += (_, __) =>
            {
                ApplyRuntimeDataBindings(string.Empty, string.Empty, string.Empty);
                SetSidebarSelected("Awareness");
            };

            // 窗口关闭时删除本次运行生成的设备容器 CSV 日志，避免留下临时数据。
            Closed += (_, __) => CleanupContainerLogCsvFiles();

        }

        private void RegisterGridSortingBehavior()
        {
            RegisterDataGridSorting(DeviceImageGrid);
            RegisterDataGridSorting(SourceImageGrid);
            RegisterDataGridSorting(PreparedImageGrid);
            RegisterDataGridSorting(ProgramFileGrid);
            RegisterDataGridSorting(ContainerComposeGrid);
            RegisterDataGridSorting(StrategyLogDataGrid);
            RegisterDataGridSorting(StrategyImageItemsControl);
        }

        private void RegisterDataGridSorting(DataGrid grid)
        {
            if (grid == null)
            {
                return;
            }

            grid.CanUserSortColumns = true;
            grid.Sorting += SharedDataGrid_OnSorting;

            var descriptor = DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(DataGrid));
            if (descriptor != null)
            {
                descriptor.AddValueChanged(grid, (_, __) => ApplySavedSortForGrid(grid));
            }
        }

        private void SharedDataGrid_OnSorting(object sender, DataGridSortingEventArgs e)
        {
            var grid = sender as DataGrid;
            if (grid == null || e == null || e.Column == null)
            {
                return;
            }

            var memberPath = ResolveSortMemberPath(e.Column);
            if (string.IsNullOrWhiteSpace(memberPath))
            {
                return;
            }

            var nextDirection = e.Column.SortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;

            ApplySortForGrid(grid, memberPath, nextDirection);
            e.Handled = true;
        }

        private static string ResolveSortMemberPath(DataGridColumn column)
        {
            if (column == null)
            {
                return string.Empty;
            }

            var memberPath = column.SortMemberPath ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(memberPath))
            {
                return memberPath.Trim();
            }

            var boundColumn = column as DataGridBoundColumn;
            var binding = boundColumn == null ? null : boundColumn.Binding as Binding;
            if (binding == null || binding.Path == null)
            {
                return string.Empty;
            }

            return binding.Path.Path ?? string.Empty;
        }

        private void ApplySavedSortForGrid(DataGrid grid)
        {
            if (grid == null || string.IsNullOrWhiteSpace(grid.Name))
            {
                return;
            }

            SortDescription sort;
            if (!_gridSortStates.TryGetValue(grid.Name, out sort))
            {
                return;
            }

            ApplySortForGrid(grid, sort.PropertyName, sort.Direction);
        }

        private void ApplySortForGrid(DataGrid grid, string memberPath, ListSortDirection direction)
        {
            if (grid == null || string.IsNullOrWhiteSpace(memberPath))
            {
                return;
            }

            var view = CollectionViewSource.GetDefaultView(grid.ItemsSource);
            if (view == null)
            {
                return;
            }

            var sortedWithCustomComparer = false;
            var listView = view as ListCollectionView;
            if (listView != null)
            {
                var column = grid.Columns.FirstOrDefault(c => string.Equals(
                    ResolveSortMemberPath(c),
                    memberPath,
                    StringComparison.OrdinalIgnoreCase));
                var headerText = column == null ? string.Empty : (column.Header == null ? string.Empty : column.Header.ToString());
                listView.CustomSort = new GridColumnComparer(memberPath, headerText, direction);
                sortedWithCustomComparer = true;
            }

            using (view.DeferRefresh())
            {
                view.SortDescriptions.Clear();
                if (!sortedWithCustomComparer)
                {
                    view.SortDescriptions.Add(new SortDescription(memberPath, direction));
                }
            }

            for (var i = 0; i < grid.Columns.Count; i++)
            {
                var column = grid.Columns[i];
                column.SortDirection = string.Equals(
                    ResolveSortMemberPath(column),
                    memberPath,
                    StringComparison.OrdinalIgnoreCase)
                    ? (ListSortDirection?)direction
                    : null;
            }

            if (!string.IsNullOrWhiteSpace(grid.Name))
            {
                _gridSortStates[grid.Name] = new SortDescription(memberPath, direction);
            }
        }

        private sealed class GridColumnComparer : IComparer
        {
            private readonly string _memberPath;
            private readonly string _headerText;
            private readonly ListSortDirection _direction;

            public GridColumnComparer(string memberPath, string headerText, ListSortDirection direction)
            {
                _memberPath = memberPath ?? string.Empty;
                _headerText = headerText ?? string.Empty;
                _direction = direction;
            }

            public int Compare(object x, object y)
            {
                var xv = ReadPropertyValue(x, _memberPath);
                var yv = ReadPropertyValue(y, _memberPath);
                var result = CompareValue(xv, yv, _memberPath, _headerText);
                return _direction == ListSortDirection.Ascending ? result : -result;
            }

            private static object ReadPropertyValue(object instance, string propertyName)
            {
                if (instance == null || string.IsNullOrWhiteSpace(propertyName))
                {
                    return null;
                }

                var prop = instance.GetType().GetProperty(propertyName);
                return prop == null ? null : prop.GetValue(instance, null);
            }

            private static int CompareValue(object left, object right, string memberPath, string headerText)
            {
                var leftText = (left == null ? string.Empty : left.ToString() ?? string.Empty).Trim();
                var rightText = (right == null ? string.Empty : right.ToString() ?? string.Empty).Trim();

                var key = ((memberPath ?? string.Empty) + "|" + (headerText ?? string.Empty)).ToLowerInvariant();
                var isSizeColumn = key.Contains("size") || key.Contains("大小");
                var isDateColumn = key.Contains("created") || key.Contains("修改时间");

                if (isSizeColumn)
                {
                    double leftBytes;
                    double rightBytes;
                    if (TryParseSizeToBytes(leftText, out leftBytes) && TryParseSizeToBytes(rightText, out rightBytes))
                    {
                        return leftBytes.CompareTo(rightBytes);
                    }
                }

                if (isDateColumn)
                {
                    DateTimeOffset leftTime;
                    DateTimeOffset rightTime;
                    if (TryParseDateTimeOffset(leftText, out leftTime) && TryParseDateTimeOffset(rightText, out rightTime))
                    {
                        return leftTime.CompareTo(rightTime);
                    }
                }

                return string.Compare(leftText, rightText, StringComparison.OrdinalIgnoreCase);
            }

            private static bool TryParseSizeToBytes(string raw, out double bytes)
            {
                bytes = 0;
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return false;
                }

                var text = raw.Trim();
                var virtualIndex = text.IndexOf('(');
                if (virtualIndex > 0)
                {
                    // 容器 Size 形如 "10.9MB (virtual 21.6GB)"，排序按可写层(括号前)处理。
                    text = text.Substring(0, virtualIndex).Trim();
                }

                text = text.ToUpperInvariant().Replace(" ", string.Empty);
                var match = Regex.Match(text, @"^(?<num>\d+(?:\.\d+)?)(?<unit>B|KB|MB|GB|TB)?$");
                if (!match.Success)
                {
                    return false;
                }

                double number;
                if (!double.TryParse(match.Groups["num"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                {
                    return false;
                }

                var unit = match.Groups["unit"].Value;
                double factor;
                switch (unit)
                {
                    case "TB":
                        factor = 1024d * 1024d * 1024d * 1024d;
                        break;
                    case "GB":
                        factor = 1024d * 1024d * 1024d;
                        break;
                    case "MB":
                        factor = 1024d * 1024d;
                        break;
                    case "KB":
                        factor = 1024d;
                        break;
                    default:
                        factor = 1d;
                        break;
                }

                bytes = number * factor;
                return true;
            }

            private static bool TryParseDateTimeOffset(string raw, out DateTimeOffset value)
            {
                value = DateTimeOffset.MinValue;
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return false;
                }

                var text = raw.Trim();
                var normalized = Regex.Replace(text, @"([+-]\d{2})(\d{2})", "$1:$2");
                normalized = Regex.Replace(normalized, @"\s+[A-Za-z]{2,5}$", string.Empty);

                return DateTimeOffset.TryParse(
                    normalized,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out value);
            }
        }

        private void ClearRuntimeDataForColdStart()
        {
            _devices.Clear();
            _allImageRows.Clear();
            _sourceImages.Clear();
            _preparedImages.Clear();
            _programFiles.Clear();
            _containerRows.Clear();
            _strategyImages.Clear();
            _deviceContextMap.Clear();

            DeviceImageGrid.ItemsSource = new List<DeviceImageRow>();
            SourceImageGrid.ItemsSource = new List<ImageComposeRow>();
            PreparedImageGrid.ItemsSource = new List<ImageComposeRow>();
            ProgramFileGrid.ItemsSource = new List<ProgramFileRow>();
            ContainerComposeGrid.ItemsSource = new List<ContainerComposeRow>();
            StrategyImageItemsControl.ItemsSource = new List<DeployImageCard>();

            DevicePreviewPanel.Children.Clear();
            RunningDeviceCountText.Text = "0个";
            ImageCountText.Text = "0个";
            ContainerCountText.Text = "0个";
            SelectedDeviceText.Text = "暂无";
            CpuUsageText.Text = "0.00%";
            MemoryUsageText.Text = "0.00%";
            DiskUsageText.Text = "0.00%";
            PieDetailText.Text = "暂无设备数据";
            UpdatePie(0, 0, 0);
            SetPieTooltips(0, 0, 0);

            DeviceSelector.Items.Clear();
            DeviceSelector.Items.Add(new ComboBoxItem
            {
                Content = "全部设备",
                Tag = "ALL"
            });
            DeviceSelector.SelectedIndex = 0;

            ContainerDeviceSelector.Items.Clear();
            ContainerDeviceSelector.Items.Add(new ComboBoxItem
            {
                Content = "暂无设备",
                Tag = "NONE"
            });
            ContainerDeviceSelector.SelectedIndex = 0;
            ServiceImageDeviceSelector.Items.Clear();
            ServiceImageDeviceSelector.Items.Add(new ComboBoxItem
            {
                Content = "全部设备",
                Tag = "ALL"
            });
            ServiceImageDeviceSelector.SelectedIndex = 0;
        }

        /// <summary>
        /// 刷新设备、镜像和资源数据，并重新绑定相关界面控件。
        /// 此重载使用轻量容器汇总模式，不主动读取容器明细，适用于普通或定时刷新。
        /// </summary>
        /// <param name="force">
        /// 为 <see langword="true"/> 时忽略一秒内的快照缓存并重新读取；
        /// 为 <see langword="false"/> 时允许直接复用最近一次成功读取的数据。
        /// </param>
        private async Task RefreshRuntimeDataBindingsAsync(bool force)
        {
            // 普通刷新只查询运行中容器的汇总指标，现有容器明细由 ApplyDockerSnapshot 保留。
            await RefreshRuntimeDataBindingsAsync(force, ContainerReadMode.LightweightSummary);
        }

        private async Task RefreshRuntimeDataBindingsAsync(bool force, ContainerReadMode containerReadMode)
        {
            await RefreshRuntimeDataBindingsAsync(
                force,
                containerReadMode,
                _readTargetIps.ToList(),
                SnapshotApplyMode.ReplaceAll);
        }

        /// <summary>
        /// 从当前已登记的 SSH 目标读取 Docker 运行时快照，将成功读取的数据写入内存集合，
        /// 然后重新绑定仪表盘、服务镜像和容器页面，同时尽量保留用户当前选择的设备。
        /// </summary>
        /// <param name="force">是否强制绕过一秒快照缓存；该参数不会绕过正在执行的刷新任务。</param>
        /// <param name="containerReadMode">
        /// 容器数据读取模式：轻量模式只读取运行中容器汇总，完整模式读取并替换容器明细。
        /// </param>
        private async Task<bool> RefreshRuntimeDataBindingsAsync(
            bool force,
            ContainerReadMode containerReadMode,
            IEnumerable<string> targetIdentities,
            SnapshotApplyMode applyMode)
        {
            // 数据源重新绑定会重建设备选择器，因此刷新前先记录三个页面当前选中的设备名称。
            var selectedContainerDevice = GetSelectedContainerDeviceName();
            var selectedDashboardDevice = IsAllDashboardDevicesSelected()
                ? DashboardAllDevicesSelectionKey
                : GetSelectedDashboardDeviceName();
            var selectedServiceImageDevice = GetSelectedServiceImageDeviceName();

            if (!_hasReadDeviceInfo)
            {
                // 尚未成功读取过设备时，不执行 Docker/SSH 命令；恢复冷启动数据并刷新空白界面。
                ClearRuntimeDataForColdStart();
                ApplyRuntimeDataBindings(selectedDashboardDevice, selectedContainerDevice, selectedServiceImageDevice);
                return false;
            }

            if (applyMode == SnapshotApplyMode.ReplaceAll &&
                !force &&
                _devices.Count > 0 &&
                DateTime.Now - _lastSnapshotAt < TimeSpan.FromSeconds(1))
            {
                // 最近一秒内已有有效快照时只重新绑定界面，避免短时间内重复访问远程设备。
                ApplyRuntimeDataBindings(selectedDashboardDevice, selectedContainerDevice, selectedServiceImageDevice);
                return true;
            }

            if (_isRefreshing)
            {
                // 同一时刻只允许一个读取任务执行，防止多个快照并发覆盖共享集合。
                return false;
            }

            var targets = (targetIdentities ?? Enumerable.Empty<string>())
                .Select(target => (target ?? string.Empty).Trim())
                .Where(target => !string.IsNullOrWhiteSpace(target))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (targets.Count == 0)
            {
                return false;
            }

            _isRefreshing = true;
            try
            {
                // Docker/SSH 查询属于阻塞操作，放到线程池执行，避免阻塞 WPF UI 线程。
                // 读取失败时返回 null，此时保留现有内存数据，仅继续执行界面绑定。
                var snapshot = await Task.Run(() =>
                {
                    DockerRuntimeSnapshot localSnapshot;
                    return TryReadDockerSnapshot(out localSnapshot, _readLocalIp, targets, containerReadMode) ? localSnapshot : null;
                });

                if (snapshot != null)
                {
                    // 只有获得完整快照后才替换内存数据并更新时间戳，失败结果不会污染缓存时间。
                    ApplyDockerSnapshot(snapshot, applyMode);
                    _lastSnapshotAt = DateTime.Now;
                }

                // 使用刷新前保存的设备名称恢复各页面选择，并更新所有关联的数据源和统计信息。
                ApplyRuntimeDataBindings(selectedDashboardDevice, selectedContainerDevice, selectedServiceImageDevice);
                return snapshot != null;
            }
            catch (Exception ex)
            {
                // 后台刷新失败不打断界面操作；保留原有数据，并将错误写入调试输出供排查。
                Debug.WriteLine("Refresh runtime data failed: " + ex.Message);
                return false;
            }
            finally
            {
                // 无论读取成功、返回空快照还是发生异常，都必须释放刷新占用标记。
                _isRefreshing = false;
            }
        }

        private void ApplyRuntimeDataBindings(string preferredDashboardDeviceName, string preferredContainerDeviceName, string preferredServiceImageDeviceName)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => ApplyRuntimeDataBindings(preferredDashboardDeviceName, preferredContainerDeviceName, preferredServiceImageDeviceName));
                return;
            }

            InitializeDevices(preferredDashboardDeviceName);
            if (_devices.Count == 0)
            {
                UpdateAllDevicesDashboard();
            }

            DeviceImageGrid.ItemsSource = null;
            DeviceImageGrid.ItemsSource = _allImageRows;

            ServiceImageDeviceSelector.Items.Clear();
            ServiceImageDeviceSelector.Items.Add(new ComboBoxItem
            {
                Content = "全部设备",
                Tag = "ALL"
            });
            for (var i = 0; i < _devices.Count; i++)
            {
                var d = _devices[i];
                ServiceImageDeviceSelector.Items.Add(new ComboBoxItem
                {
                    Content = string.Format("{0} ({1})", d.Name, d.Ip),
                    Tag = d
                });
            }

            if (!string.IsNullOrWhiteSpace(preferredServiceImageDeviceName))
            {
                for (var i = 0; i < ServiceImageDeviceSelector.Items.Count; i++)
                {
                    var item = ServiceImageDeviceSelector.Items[i] as ComboBoxItem;
                    var device = item != null ? item.Tag as DeviceInfo : null;
                    if (device != null && string.Equals(device.Name, preferredServiceImageDeviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        ServiceImageDeviceSelector.SelectedIndex = i;
                        break;
                    }
                }
            }

            if (ServiceImageDeviceSelector.SelectedIndex < 0 && ServiceImageDeviceSelector.Items.Count > 0)
            {
                ServiceImageDeviceSelector.SelectedIndex = 0;
            }

            BindServiceImageRows(GetSelectedServiceImageDeviceName());

            BindProgramFileGridWithSelection(
                ProgramFileGrid,
                _programFiles
                    .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList());

            StrategyImageItemsControl.ItemsSource = null;
            StrategyImageItemsControl.ItemsSource = _strategyImages;

            ContainerDeviceSelector.Items.Clear();
            for (var i = 0; i < _devices.Count; i++)
            {
                var d = _devices[i];
                ContainerDeviceSelector.Items.Add(new ComboBoxItem
                {
                    Content = string.Format("{0} ({1})", d.Name, d.Ip),
                    Tag = d
                });
            }

            if (ContainerDeviceSelector.Items.Count > 0)
            {
                if (!string.IsNullOrWhiteSpace(preferredContainerDeviceName))
                {
                    for (var i = 0; i < ContainerDeviceSelector.Items.Count; i++)
                    {
                        var item = ContainerDeviceSelector.Items[i] as ComboBoxItem;
                        var device = item != null ? item.Tag as DeviceInfo : null;
                        if (device != null && string.Equals(device.Name, preferredContainerDeviceName, StringComparison.OrdinalIgnoreCase))
                        {
                            ContainerDeviceSelector.SelectedIndex = i;
                            break;
                        }
                    }
                }

                if (ContainerDeviceSelector.SelectedIndex < 0)
                {
                    ContainerDeviceSelector.SelectedIndex = 0;
                }

                BindContainerRows(GetSelectedContainerDeviceName());
            }
            else
            {
                ContainerDeviceSelector.Items.Clear();
                ContainerDeviceSelector.Items.Add(new ComboBoxItem
                {
                    Content = "暂无设备",
                    Tag = "NONE"
                });
                ContainerDeviceSelector.SelectedIndex = 0;
                BindContainerRows(null);
            }
        }

        private void InitializeDevices(string preferredDeviceName)
        {
            DeviceSelector.Items.Clear();
            DevicePreviewPanel.Children.Clear();

            DeviceSelector.Items.Add(new ComboBoxItem
            {
                Content = "全部设备",
                Tag = "ALL"
            });

            var runningDevices = _devices.Where(d => d.ContainerCount > 0).ToList();
            for (var idx = 0; idx < _devices.Count; idx++)
            {
                var device = _devices[idx];
                var item = new ComboBoxItem
                {
                    Content = string.Format("{0} ({1})", device.Name, device.Ip),
                    Tag = device
                };
                DeviceSelector.Items.Add(item);
            }

            for (var i = 0; i < runningDevices.Count; i++)
            {
                DevicePreviewPanel.Children.Add(CreateDevicePreviewText(runningDevices[i]));
            }

            if (runningDevices.Count == 0)
            {
                DevicePreviewPanel.Children.Add(new TextBlock
                {
                    Text = "暂无运行中的设备",
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139))
                });
            }

            RunningDeviceCountText.Text = string.Format("{0}个", runningDevices.Count);
            if (string.Equals(preferredDeviceName, DashboardAllDevicesSelectionKey, StringComparison.Ordinal))
            {
                DeviceSelector.SelectedIndex = 0;
                return;
            }

            if (!string.IsNullOrWhiteSpace(preferredDeviceName))
            {
                for (var i = 0; i < DeviceSelector.Items.Count; i++)
                {
                    var item = DeviceSelector.Items[i] as ComboBoxItem;
                    var deviceTag = item != null ? item.Tag as DeviceInfo : null;
                    if (deviceTag != null && string.Equals(deviceTag.Name, preferredDeviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        DeviceSelector.SelectedIndex = i;
                        return;
                    }
                }
            }

            if (DeviceSelector.Items.Count > 1)
            {
                DeviceSelector.SelectedIndex = 1;
                return;
            }

            DeviceSelector.SelectedIndex = 0;
        }

        private string GetSelectedDashboardDeviceName()
        {
            var selected = DeviceSelector.SelectedItem as ComboBoxItem;
            if (selected == null)
            {
                return string.Empty;
            }

            var tag = selected.Tag as DeviceInfo;
            return tag == null ? string.Empty : tag.Name;
        }

        private bool IsAllDashboardDevicesSelected()
        {
            var selected = DeviceSelector.SelectedItem as ComboBoxItem;
            return selected != null && string.Equals(selected.Tag as string, "ALL", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 响应“读取设备信息”按钮：收集 SSH 登录信息、校验连接凭据、读取目标设备的
        /// Docker 镜像及资源数据，并刷新界面。读取失败或被用户取消时会回滚本次连接状态。
        /// </summary>
        /// <param name="sender">触发事件的“读取设备信息”按钮。</param>
        /// <param name="e">按钮单击事件参数。</param>
        private async void ReadDeviceInfoButton_OnClick(object sender, RoutedEventArgs e)
        {
            BeginReadTimingCollection();
            string targetUser;
            string targetIp;
            string targetPassword;
            // 弹出连接参数对话框；用户取消输入时，不创建进度窗口，也不改变当前设备数据。
            if (!ShowReadDeviceInfoDialog(out targetUser, out targetIp, out targetPassword))
            {
                EndReadTimingCollection();
                return;
            }

            TextBlock progressStageText = null;     // 显示当前所处的读取阶段。
            ProgressBar progressBar = null;         // 显示整体读取进度。
            TextBlock progressText = null;          // 显示当前操作的详细提示。
            Window progressWindow = null;           // 显示读取过程的进度窗口。

            var operationCanceled = false;          // 标记用户是否主动取消了读取操作。
            var progressClosedByCode = false;       // 区分代码关闭窗口与用户手动关闭窗口。
            var targetIdentity = string.Empty;      // 保存由 SSH 用户名和目标 IP 组成的目标标识。
            var targetRegisteredThisAttempt = false;
            var replacedTargetIdentity = string.Empty;
            var exactTargetIdentityExisted = false;
            var hadPreviousTargetPassword = false;
            var previousTargetPassword = string.Empty;


            try
            {
                // 进度窗口由代码动态创建，用于展示连接、校验和数据拉取阶段。
                progressStageText = new TextBlock
                {
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(36, 64, 98)),
                    Margin = new Thickness(0, 0, 0, 8)
                };
                progressBar = new ProgressBar
                {
                    Minimum = 0,
                    Maximum = 100,
                    Height = 16,
                    Margin = new Thickness(0, 0, 0, 10)
                };
                progressText = new TextBlock
                {
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromRgb(42, 53, 68))
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
                    Title = "正在读取设备信息",
                    Width = 620,
                    Height = 240,
                    ResizeMode = ResizeMode.NoResize,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this,
                    Content = panel
                };


                // 统一更新进度条、阶段标题和详细提示，并把百分比限制在有效范围内。
                Action<double, string, string> setReadProgress = (percent, stage, text) =>
                {
                    if (progressBar != null)
                    {
                        progressBar.Value = Math.Max(0, Math.Min(100, percent));
                    }

                    if (progressStageText != null)
                    {
                        progressStageText.Text = string.IsNullOrWhiteSpace(stage) ? "阶段：处理中" : ("阶段：" + stage);
                    }

                    SetProgressText(progressText, text);
                };
                setReadProgress(15, "连接设备", string.Format("正在连接 {0}@{1} ", targetUser, targetIp));
                // 只有用户主动关闭进度窗口才视为取消；正常完成时由代码关闭窗口。
                progressWindow.Closing += (_, __) =>
                {
                    if (!progressClosedByCode)
                    {
                        operationCanceled = true;
                    }
                };
                progressWindow.Show();

                // 将执行权暂时交还给调度器，使进度窗口先完成渲染并响应可能的关闭操作。
                await Dispatcher.Yield(DispatcherPriority.Background);
                if (operationCanceled)
                {
                    return;
                }

                // 保存本次读取上下文并生成目标唯一标识。
                _readLocalIp = GetLocalIpv4();
                _readTargetUser = targetUser;
                _readTargetIp = targetIp;
                targetIdentity = BuildSshTargetIdentity(targetUser, targetIp);

                setReadProgress(30, "密码校验", string.Format("正在以密码模式校验 SSH：{0}@{1} ", targetUser, targetIp));

                var validateOut = string.Empty;
                var validateErr = string.Empty;
                // SSH 调用是阻塞操作，放到后台线程执行以保持界面可响应；远端返回固定标记
                // 才表示命令确实执行成功，避免仅凭进程退出状态误判密码校验结果。
                var validateOk = await Task.Run(() => ExecuteSshRemoteCommand(
                    targetUser,
                    targetIp,
                    targetPassword,
                     "echo __PWD_CHECK_OK__",
                     15000,
                     out validateOut,
                     out validateErr,
                     "ssh-password-check"));
                if (operationCanceled)
                {
                    return;
                }

                if (!validateOk || (validateOut ?? string.Empty).IndexOf("__PWD_CHECK_OK__", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    // 校验失败时先按“代码主动关闭”处理进度窗口，再展示 SSH 标准错误或输出。
                    progressClosedByCode = true;
                    CloseWindowQuietly(ref progressWindow);
                    if (!operationCanceled)
                    {
                        ShowScrollableErrorDialog(
                            "读取设备信息",
                            "密码校验失败，请检查用户名/IP/密码。\n\n输出：\n" +
                            (string.IsNullOrWhiteSpace(validateErr) ? (validateOut ?? string.Empty) : validateErr) +
                            "\n\n耗时摘要：\n" + BuildReadTimingSummary());
                    }

                    return;
                }

                // 校验成功后再登记目标和密码，供后续远程 Docker 命令复用。
                exactTargetIdentityExisted = _readTargetIps.Contains(targetIdentity);
                hadPreviousTargetPassword = _sshPasswordByTarget.TryGetValue(targetIdentity, out previousTargetPassword);
                replacedTargetIdentity = _readTargetIps.FirstOrDefault(identity =>
                {
                    var value = (identity ?? string.Empty).Trim();
                    var at = value.LastIndexOf('@');
                    var host = at >= 0 && at < value.Length - 1 ? value.Substring(at + 1).Trim() : string.Empty;
                    return string.Equals(value, targetIdentity, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(host, targetIp, StringComparison.OrdinalIgnoreCase);
                }) ?? string.Empty;
                _readTargetIps.Add(targetIdentity);
                _sshPasswordByTarget[targetIdentity] = targetPassword ?? string.Empty;
                targetRegisteredThisAttempt = !exactTargetIdentityExisted;
                _hasReadDeviceInfo = true;

                setReadProgress(56, "拉取镜像与资源", "正在拉取远程镜像与资源指标数据...");

                // 首次读取使用轻量容器汇总：保留镜像和 Docker 磁盘信息，不预取容器明细。
                var readSucceeded = await RefreshRuntimeDataBindingsAsync(
                    true,
                    ContainerReadMode.LightweightSummary,
                    new[] { targetIdentity },
                    SnapshotApplyMode.MergeDevices);
                if (operationCanceled)
                {
                    return;
                }

                if (!readSucceeded)
                {
                    // 本次新增的目标未返回 Docker 数据时，撤销刚写入的目标、密码和命令候选缓存；
                    // 已存在的目标保留原缓存，避免一次刷新失败破坏此前的连接配置。
                    if (!exactTargetIdentityExisted)
                    {
                        _readTargetIps.Remove(targetIdentity);
                        _sshPasswordByTarget.Remove(targetIdentity);
                        _preferredSshDockerCandidateIndex.Remove(targetIdentity);
                    }
                    else if (hadPreviousTargetPassword)
                    {
                        _sshPasswordByTarget[targetIdentity] = previousTargetPassword;
                    }

                    if (_readTargetIps.Count == 0)
                    {
                        // 已无任何有效目标时恢复冷启动界面，防止继续显示失败读取留下的数据。
                        _hasReadDeviceInfo = false;
                        ClearRuntimeDataForColdStart();
                    }
                    progressClosedByCode = true;
                    CloseWindowQuietly(ref progressWindow);
                    if (!operationCanceled)
                    {
                        ShowScrollableErrorDialog(
                            "读取设备信息",
                            string.Format(
                                "连接失败：未读取到目标设备 {0} 的 Docker 运行信息。\n\n耗时摘要：\n{1}",
                                targetIp,
                                BuildReadTimingSummary()));
                    }

                    return;
                }

                if (!string.IsNullOrWhiteSpace(replacedTargetIdentity) &&
                    !string.Equals(replacedTargetIdentity, targetIdentity, StringComparison.OrdinalIgnoreCase))
                {
                    _readTargetIps.Remove(replacedTargetIdentity);
                    _sshPasswordByTarget.Remove(replacedTargetIdentity);
                    _preferredSshDockerCandidateIndex.Remove(replacedTargetIdentity);
                    _lastSshErrorByTarget.Remove(replacedTargetIdentity);
                }

                // 为已发现设备准备日志文件后，再向用户报告读取成功。
                EnsureContainerLogCsvFilesForKnownDevices();

                setReadProgress(100, "完成", "设备信息读取完成。");

                progressClosedByCode = true;
                CloseWindowQuietly(ref progressWindow);
                if (!operationCanceled)
                {
                    // 汇总各阶段耗时，并连同当前有效目标数量一起反馈给用户。
                    var timingSummary = BuildReadTimingSummary();
                    MessageBox.Show(
                        this,
                        string.Format(
                            "连接成功：{0}@{1}\n当前已连接设备目标数：{2}\n\n耗时摘要：\n{3}",
                            targetUser,
                            targetIp,
                            _readTargetIps.Count,
                            timingSummary),
                        "读取设备信息",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            finally
            {
                // 无论从哪个分支退出，都结束本次计时采集，避免影响下一次读取的统计结果。
                EndReadTimingCollection();
                // 用户中途取消时清除当前目标状态，避免保留不完整的运行时数据。
                if (operationCanceled && targetRegisteredThisAttempt && !string.IsNullOrWhiteSpace(targetIdentity))
                {
                    _readTargetIps.Remove(targetIdentity);
                    _sshPasswordByTarget.Remove(targetIdentity);
                    _preferredSshDockerCandidateIndex.Remove(targetIdentity);
                    _lastSshErrorByTarget.Remove(targetIdentity);
                    if (_readTargetIps.Count == 0)
                    {
                        _hasReadDeviceInfo = false;
                        ClearRuntimeDataForColdStart();
                    }
                }
                else if (operationCanceled && exactTargetIdentityExisted && hadPreviousTargetPassword)
                {
                    _sshPasswordByTarget[targetIdentity] = previousTargetPassword;
                }

                // 无论成功、失败还是取消，都确保关闭进度窗口并恢复主窗口焦点。
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

                RestoreMainWindowFocus();
            }
        }

        private bool ShowReadDeviceInfoDialog(out string targetUser, out string targetIp, out string targetPassword)
        {
            targetUser = string.Empty;
            targetIp = string.Empty;
            targetPassword = string.Empty;
            var selectedTargetUser = string.Empty;
            var selectedTargetIp = string.Empty;
            var selectedTargetPassword = string.Empty;

            var targetUserBox = new TextBox
            {
                Width = 500,
                Height = 32,
                HorizontalAlignment = HorizontalAlignment.Left,
                Text = _readTargetUser
            };

            var targetIpBox = new TextBox
            {
                Width = 500,
                Height = 32,
                HorizontalAlignment = HorizontalAlignment.Left,
                Text = _readTargetIp
            };

            var targetPasswordBox = new PasswordBox
            {
                Width = 500,
                Height = 32,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            var targetPasswordTextBox = new TextBox
            {
                Width = 500,
                Height = 32,
                HorizontalAlignment = HorizontalAlignment.Left,
                Visibility = Visibility.Collapsed
            };
            var passwordEyeButton = new Button
            {
                Content = "👁",
                Width = 38,
                Height = 32,
                Margin = new Thickness(8, 0, 0, 0),
                ToolTip = "显示/隐藏密码",
                Cursor = Cursors.Hand
            };

            var tips = new TextBlock
            {
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                Text = "将通过 SSH 远程执行 Docker 命令读取设备信息（用户名 + 目标IP + 密码）。"
            };

            var panel = new Grid { Margin = new Thickness(46, 26, 20, 0) };
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddReadDeviceDialogRow(panel, 0, "SSH 用户名", targetUserBox);
            AddReadDeviceDialogRow(panel, 1, "通信目标IP地址", targetIpBox);
            var passwordRow = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
            passwordRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            passwordRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(targetPasswordBox, 0);
            Grid.SetColumn(targetPasswordTextBox, 0);
            Grid.SetColumn(passwordEyeButton, 1);
            passwordRow.Children.Add(targetPasswordBox);
            passwordRow.Children.Add(targetPasswordTextBox);
            passwordRow.Children.Add(passwordEyeButton);
            AddReadDeviceDialogRow(panel, 2, "SSH 密码", passwordRow);
            Grid.SetRow(tips, 3);
            Grid.SetColumn(tips, 1);
            panel.Children.Add(tips);

            var okButton = new Button
            {
                Content = "读取",
                Width = 86,
                Height = 32,
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true
            };
            var cancelButton = new Button
            {
                Content = "取消",
                Width = 86,
                Height = 32,
                Margin = new Thickness(0),
                IsCancel = true
            };

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 0, 0)
            };
            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);

            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                CanContentScroll = true,
                Content = panel
            };

            var root = new DockPanel();
            DockPanel.SetDock(buttonPanel, Dock.Bottom);
            root.Children.Add(buttonPanel);
            root.Children.Add(scrollViewer);

            var dialog = new Window
            {
                Title = "读取设备信息",
                Width = 820,
                Height = 320,
                MinWidth = 760,
                MinHeight = 300,
                ResizeMode = ResizeMode.CanResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Content = root
            };

            okButton.Click += (_, __) =>
            {
                var user = (targetUserBox.Text ?? string.Empty).Trim();
                var target = (targetIpBox.Text ?? string.Empty).Trim();

                if (string.IsNullOrWhiteSpace(user))
                {
                    MessageBox.Show("请输入 SSH 用户名。", "读取设备信息", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (!IsValidIpv4(target))
                {
                    MessageBox.Show("请输入有效的目标IPv4地址。", "读取设备信息", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var password = targetPasswordTextBox.Visibility == Visibility.Visible
                    ? (targetPasswordTextBox.Text ?? string.Empty)
                    : (targetPasswordBox.Password ?? string.Empty);
                if (string.IsNullOrWhiteSpace(password))
                {
                    MessageBox.Show("请输入 SSH 密码。", "读取设备信息", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                selectedTargetUser = user;
                selectedTargetIp = target;
                selectedTargetPassword = password;
                dialog.DialogResult = true;
            };

            passwordEyeButton.Click += (_, __) =>
            {
                if (targetPasswordBox.Visibility == Visibility.Visible)
                {
                    targetPasswordTextBox.Text = targetPasswordBox.Password ?? string.Empty;
                    targetPasswordBox.Visibility = Visibility.Collapsed;
                    targetPasswordTextBox.Visibility = Visibility.Visible;
                    passwordEyeButton.Content = "●";
                }
                else
                {
                    targetPasswordBox.Password = targetPasswordTextBox.Text ?? string.Empty;
                    targetPasswordTextBox.Visibility = Visibility.Collapsed;
                    targetPasswordBox.Visibility = Visibility.Visible;
                    passwordEyeButton.Content = "👁";
                }
            };

            if (dialog.ShowDialog() == true)
            {
                targetUser = selectedTargetUser;
                targetIp = selectedTargetIp;
                targetPassword = selectedTargetPassword;
                return true;
            }

            return false;
        }

        private static void AddReadDeviceDialogRow(Grid panel, int rowIndex, string label, UIElement editor)
        {
            var labelBlock = new TextBlock
            {
                Text = label,
                Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55)),
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 14)
            };
            Grid.SetRow(labelBlock, rowIndex);
            Grid.SetColumn(labelBlock, 0);

            if (editor is FrameworkElement element)
            {
                element.Margin = new Thickness(0, 0, 0, 14);
            }
            Grid.SetRow(editor, rowIndex);
            Grid.SetColumn(editor, 1);

            panel.Children.Add(labelBlock);
            panel.Children.Add(editor);
        }

        private static bool IsValidIpv4(string ipText)
        {
            if (string.IsNullOrWhiteSpace(ipText))
            {
                return false;
            }

            IPAddress address;
            return IPAddress.TryParse(ipText.Trim(), out address) &&
                   address.AddressFamily == AddressFamily.InterNetwork;
        }

        private FrameworkElement CreateDevicePreviewText(DeviceInfo device)
        {
            return new TextBlock
            {
                Text = string.Format("{0}: {1}", device.Name, device.Ip),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55)),
                Margin = new Thickness(0, 0, 0, 8)
            };
        }

        private void DeviceSelector_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selected = DeviceSelector.SelectedItem as ComboBoxItem;
            if (selected == null)
            {
                return;
            }

            if ((selected.Tag as string) == "ALL")
            {
                UpdateAllDevicesDashboard();
                BindImageRows(string.Empty, string.Empty);
                return;
            }

            var device = selected.Tag as DeviceInfo;
            if (device == null)
            {
                return;
            }

            UpdateDeviceDashboard(device);
            BindImageRows(device.Name, device.Ip);
        }

        private void DeviceImageGridRow_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var row = sender as DataGridRow;
            if (row == null)
            {
                return;
            }

            row.IsSelected = true;
            row.Focus();
        }

        private void ContainerComposeGridRow_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var row = sender as DataGridRow;
            if (row == null)
            {
                return;
            }

            row.IsSelected = true;
            row.Focus();
        }

        private async void ContainerModifyNameMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            var selectedRow = ContainerComposeGrid == null ? null : ContainerComposeGrid.SelectedItem as ContainerComposeRow;
            if (selectedRow == null)
            {
                MessageBox.Show("请先选中一行容器。", "容器编排", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var contextName = GetSelectedContainerContextName();
            if (string.IsNullOrWhiteSpace(contextName))
            {
                MessageBox.Show("未找到当前设备对应的 Docker 上下文。", "容器编排", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var oldName = ((selectedRow.Name ?? string.Empty).Trim().TrimStart('/'));
            if (string.IsNullOrWhiteSpace(oldName))
            {
                MessageBox.Show("当前容器名称无效，无法修改。", "容器编排", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string newName;
            if (!ShowSingleInputDialog(
                "修改容器名称",
                "请输入新的容器名称：",
                oldName,
                out newName,
                "Docker 容器名称只能包含[a-z   A-Z   0-9   _   .   -]（字母、数字、下划线、点、横杠）"))
            {
                return;
            }

            var normalizedNewName = (newName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalizedNewName))
            {
                MessageBox.Show("容器名称不能为空。", "容器编排", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!System.Text.RegularExpressions.Regex.IsMatch(normalizedNewName, "^[a-zA-Z0-9_.-]+$"))
            {
                MessageBox.Show(this, "修改容器名称失败，名称未按规定改写", "容器编排", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.Equals(oldName, normalizedNewName, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("新旧容器名称相同，无需修改。", "容器编排", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!ShowRenameContainerConfirmDialog(oldName, normalizedNewName))
            {
                return;
            }

            if (!await ConfirmContainerOperationAsync(contextName, new List<ContainerComposeRow> { selectedRow }))
            {
                return;
            }

            List<string> lockedContainerIds;
            if (!TryLockContainerRows(new[] { selectedRow }, out lockedContainerIds))
            {
                MessageBox.Show(this, "该容器正在执行其他操作，请稍后重试。", "容器编排", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Window progressWindow = null;
            TextBlock progressText = null;
            try
            {
                progressWindow = CreateProgressDialog("正在修改容器名称");
                progressText = progressWindow.Content as TextBlock;
                SetProgressText(progressText, "正在修改容器名称中，请稍候...");
                progressWindow.Show();
                await Dispatcher.Yield(DispatcherPriority.Background);

                var renameSource = selectedRow.FullId;
                var renameCommand = string.Format("rename \"{0}\" \"{1}\"", renameSource, normalizedNewName);
                var renameOut = string.Empty;
                var renameOk = await Task.Run(() => TryRunDockerCommand(renameCommand, contextName, out renameOut));
                if (!renameOk)
                {
                    CloseWindowQuietly(ref progressWindow);
                    MessageBox.Show(this, "修改容器名称失败，名称未按规定改写", "容器编排", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                await WaitForContainerRowsAsync(
                    contextName,
                    rows => rows.Any(r =>
                        string.Equals((r.Name ?? string.Empty).TrimStart('/'), normalizedNewName, StringComparison.OrdinalIgnoreCase)),
                    5000);

                var selectedDeviceName = GetSelectedContainerDeviceName();
                var renamedRow = _containerRows.FirstOrDefault(r =>
                    string.Equals(r.DeviceName, selectedDeviceName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals((r.Name ?? string.Empty).TrimStart('/'), normalizedNewName, StringComparison.OrdinalIgnoreCase));

                var renameChineseName = oldName + "->" + normalizedNewName;
                var logSourceRow = renamedRow ?? selectedRow;
                var renameLogRow = new ContainerComposeRow(
                    logSourceRow.DeviceName,
                    logSourceRow.Id,
                    "/" + normalizedNewName,
                    renameChineseName,
                    logSourceRow.Image,
                    logSourceRow.Status,
                    logSourceRow.Ports,
                    logSourceRow.CpuCores,
                    logSourceRow.CpuPercent,
                    logSourceRow.MemoryUsage,
                    logSourceRow.MemoryPercent,
                    logSourceRow.DiskReadWrite,
                    logSourceRow.Detail,
                    string.IsNullOrWhiteSpace(logSourceRow.FullId) ? selectedRow.FullId : logSourceRow.FullId);

                AddContainerOperationLog(
                    contextName,
                    "修改容器名称",
                    renameLogRow.FullId,
                    normalizedNewName,
                    "成功",
                    string.Format("名称: {0}->{1}", oldName, normalizedNewName));
                AppendContainerOperationCsvByDeviceName(selectedDeviceName, "修改容器名称", renameLogRow);

                CloseWindowQuietly(ref progressWindow);
                MessageBox.Show(
                    this,
                    string.Format("修改容器名称成功：{0} -> {1}", oldName, normalizedNewName),
                    "容器编排",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            finally
            {
                if (progressWindow != null)
                {
                    try
                    {
                        progressWindow.Close();
                    }
                    catch
                    {
                    }
                }

                UnlockContainerRows(lockedContainerIds);
                UpdateContainerActionAvailability();
                RestoreMainWindowFocus();
            }
        }

        private async void DeviceImageModifyNameMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            var selectedRow = DeviceImageGrid == null ? null : DeviceImageGrid.SelectedItem as DeviceImageRow;
            if (selectedRow == null)
            {
                MessageBox.Show("请先选中一行镜像。", "镜像和容器感知", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string newName;
            if (!ShowSingleInputDialog("修改Name", "请输入新的 Name（Repository）：", selectedRow.Repository, out newName))
            {
                return;
            }

            var normalized = (newName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                MessageBox.Show("Name 不能为空。", "镜像和容器感知", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await RetagDeviceImageAsync(selectedRow, normalized, selectedRow.Tag, "修改Name");
        }

        private async void DeviceImageModifyTagMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            var selectedRow = DeviceImageGrid == null ? null : DeviceImageGrid.SelectedItem as DeviceImageRow;
            if (selectedRow == null)
            {
                MessageBox.Show("请先选中一行镜像。", "镜像和容器感知", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string newTag;
            if (!ShowSingleInputDialog("修改Tag", "请输入新的 Tag：", selectedRow.Tag, out newTag))
            {
                return;
            }

            var normalized = (newTag ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                MessageBox.Show("Tag 不能为空。", "镜像和容器感知", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await RetagDeviceImageAsync(selectedRow, selectedRow.Repository, normalized, "修改Tag");
        }

        private async Task RetagDeviceImageAsync(DeviceImageRow row, string newRepository, string newTag, string actionTitle)
        {
            var selectedViewDeviceName = GetSelectedDashboardDeviceName();
            var oldRepository = (row == null ? string.Empty : row.Repository ?? string.Empty).Trim();
            var originalTag = (row == null ? string.Empty : row.Tag ?? string.Empty).Trim();
            var oldTag = originalTag;
            if (string.IsNullOrWhiteSpace(oldTag) || string.Equals(oldTag, "<none>", StringComparison.OrdinalIgnoreCase))
            {
                oldTag = "latest";
            }

            var targetRepo = (newRepository ?? string.Empty).Trim();
            var targetTag = (newTag ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(targetRepo))
            {
                MessageBox.Show("Name 不能为空。", "镜像和容器感知", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(targetTag) || string.Equals(targetTag, "<none>", StringComparison.OrdinalIgnoreCase))
            {
                targetTag = "latest";
            }

            var contextName = (row == null ? string.Empty : row.ContextName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(contextName) && row != null && !string.IsNullOrWhiteSpace(row.DeviceName))
            {
                string mappedContext;
                if (_deviceContextMap.TryGetValue(row.DeviceName, out mappedContext))
                {
                    contextName = (mappedContext ?? string.Empty).Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(contextName))
            {
                MessageBox.Show("未找到该镜像对应的 Docker 上下文，无法执行修改。", "镜像和容器感知", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            contextName = ResolveFastOperationContext(contextName);

            var oldRepoTag = oldRepository + ":" + oldTag;
            var newRepoTag = targetRepo + ":" + targetTag;
            if (string.Equals(oldRepoTag, newRepoTag, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("新旧 Name:Tag 相同，无需修改。", "镜像和容器感知", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(
                string.Format("确定将镜像从\n{0}\n修改为\n{1}\n吗？", oldRepoTag, newRepoTag),
                actionTitle,
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            var useImageIdAsSource =
                string.IsNullOrWhiteSpace(oldRepository) ||
                string.Equals(oldRepository, "<none>", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(originalTag) ||
                string.Equals(originalTag, "<none>", StringComparison.OrdinalIgnoreCase);
            var sourceRef = useImageIdAsSource ? ((row == null ? string.Empty : row.ImageId ?? string.Empty).Trim()) : oldRepoTag;
            if (string.IsNullOrWhiteSpace(sourceRef))
            {
                MessageBox.Show("未找到有效的源镜像标识（Name:Tag 或 Image ID）。", "镜像和容器感知", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Window progressWindow = null;
            TextBlock progressText = null;
            try
            {
                progressWindow = CreateProgressDialog(actionTitle);
                progressText = progressWindow.Content as TextBlock;
                SetProgressText(progressText, string.Format("正在执行 {0}...\n{1} -> {2}", actionTitle, oldRepoTag, newRepoTag));
                progressWindow.Show();
                await Dispatcher.Yield(DispatcherPriority.Background);

                var outText = string.Empty;
                var tagCmd = string.Format("tag \"{0}\" \"{1}\"", sourceRef, newRepoTag);
                var tagOk = await Task.Run(() => TryRunDockerCommand(tagCmd, contextName, out outText, 45000));
                if (!tagOk)
                {
                    ShowScrollableErrorDialog(actionTitle + "失败", string.IsNullOrWhiteSpace(outText) ? "执行 docker tag 失败（无输出）。" : outText);
                    return;
                }

                if (!useImageIdAsSource)
                {
                    SetProgressText(progressText, "正在清理旧标签...");
                    var rmOldOut = string.Empty;
                    var rmOldCmd = string.Format("image rm \"{0}\"", oldRepoTag);
                    await Task.Run(() => TryRunDockerCommand(rmOldCmd, contextName, out rmOldOut, 15000));
                }

                SetProgressText(progressText, "正在刷新镜像列表...");
                await RefreshDeviceImageRowsFastAsync(selectedViewDeviceName);
            }
            finally
            {
                if (progressWindow != null)
                {
                    try
                    {
                        progressWindow.Close();
                    }
                    catch
                    {
                    }
                }
            }

            MessageBox.Show(
                string.Format("{0}成功：\n{1}", actionTitle, newRepoTag),
                "镜像和容器感知",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private bool ShowSingleInputDialog(string title, string label, string initialValue, out string value, string hintText = null)
        {
            value = string.Empty;
            var inputResult = string.Empty;
            var textBox = new TextBox
            {
                Text = initialValue ?? string.Empty,
                Width = 360,
                Height = 30,
                Margin = new Thickness(0, 8, 0, 0)
            };

            var panel = new StackPanel
            {
                Margin = new Thickness(16, 14, 16, 10)
            };
            panel.Children.Add(new TextBlock
            {
                Text = label ?? string.Empty,
                FontWeight = FontWeights.SemiBold
            });
            panel.Children.Add(textBox);
            if (!string.IsNullOrWhiteSpace(hintText))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = hintText,
                    Margin = new Thickness(0, 10, 0, 0),
                    Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                    TextWrapping = TextWrapping.Wrap
                });
            }

            var okButton = new Button { Content = "确定", Width = 86, Height = 32, Margin = new Thickness(0, 10, 8, 0), IsDefault = true };
            var cancelButton = new Button { Content = "取消", Width = 86, Height = 32, Margin = new Thickness(0, 10, 0, 0), IsCancel = true };
            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);

            var root = new DockPanel();
            DockPanel.SetDock(buttonPanel, Dock.Bottom);
            root.Children.Add(buttonPanel);
            root.Children.Add(panel);

            var dialog = new Window
            {
                Title = title ?? "输入",
                Width = 430,
                Height = string.IsNullOrWhiteSpace(hintText) ? 210 : 260,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Content = root
            };

            okButton.Click += (_, __) =>
            {
                inputResult = textBox.Text ?? string.Empty;
                dialog.DialogResult = true;
            };

            var ok = dialog.ShowDialog() == true;
            if (ok)
            {
                value = inputResult;
            }

            return ok;
        }

        private bool ShowRenameContainerConfirmDialog(string oldName, string newName)
        {
            var panel = new StackPanel
            {
                Margin = new Thickness(18, 14, 18, 10)
            };

            panel.Children.Add(new TextBlock
            {
                Text = string.Format(
                    "确定将容器名称从 {0} 修改为 {1} 吗？",
                    (oldName ?? string.Empty).Trim(),
                    (newName ?? string.Empty).Trim()),
                FontSize = 16,
                TextWrapping = TextWrapping.Wrap
            });

            var yesButton = new Button { Content = "是(Y)", Width = 96, Height = 34, Margin = new Thickness(0, 12, 8, 0), IsDefault = true };
            var noButton = new Button { Content = "否(N)", Width = 96, Height = 34, Margin = new Thickness(0, 12, 0, 0), IsCancel = true };
            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttonPanel.Children.Add(yesButton);
            buttonPanel.Children.Add(noButton);

            var root = new DockPanel();
            DockPanel.SetDock(buttonPanel, Dock.Bottom);
            root.Children.Add(buttonPanel);
            root.Children.Add(panel);

            var confirmed = false;
            var dialog = new Window
            {
                Title = "修改容器名称",
                Owner = this,
                Width = 460,
                Height = 210,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = root
            };

            yesButton.Click += (_, __) =>
            {
                confirmed = true;
                dialog.DialogResult = true;
            };
            noButton.Click += (_, __) =>
            {
                dialog.DialogResult = false;
            };

            dialog.ShowDialog();
            return confirmed;
        }

        private async void DeviceImageDeleteMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            var selectedViewDeviceName = GetSelectedDashboardDeviceName();
            var selectedRow = DeviceImageGrid == null ? null : DeviceImageGrid.SelectedItem as DeviceImageRow;
            if (selectedRow == null)
            {
                MessageBox.Show("请先选中一行镜像。", "镜像和容器感知", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var repository = (selectedRow.Repository ?? string.Empty).Trim();
            var tag = (selectedRow.Tag ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(repository) ||
                string.Equals(repository, "<none>", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("当前镜像仓库名无效，无法删除。", "镜像和容器感知", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(tag) || string.Equals(tag, "<none>", StringComparison.OrdinalIgnoreCase))
            {
                tag = "latest";
            }

            var repoTag = repository + ":" + tag;
            var confirm = MessageBox.Show(
                "确定删除该镜像吗？\n" + repoTag,
                "删除镜像",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            Window progressWindow = null;
            TextBlock progressText = null;
            var operationCanceled = false;
            var progressClosedByCode = false;
            try
            {
                progressWindow = CreateProgressDialog("正在删除");
                progressText = progressWindow.Content as TextBlock;
                SetProgressText(progressText, "正在删除镜像，请稍候...\n" + repoTag);
                progressWindow.Closing += (_, __) =>
                {
                    if (!progressClosedByCode)
                    {
                        operationCanceled = true;
                    }
                };
                progressWindow.Show();
                await Dispatcher.Yield(DispatcherPriority.Background);
                if (operationCanceled)
                {
                    return;
                }

                var contextName = (selectedRow.ContextName ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(contextName) && !string.IsNullOrWhiteSpace(selectedRow.DeviceName))
                {
                    string mappedContext;
                    if (_deviceContextMap.TryGetValue(selectedRow.DeviceName, out mappedContext))
                    {
                        contextName = (mappedContext ?? string.Empty).Trim();
                    }
                }

                if (string.IsNullOrWhiteSpace(contextName))
                {
                    ShowScrollableErrorDialog("删除镜像失败", "未找到该镜像对应的设备上下文。");
                    return;
                }
                contextName = ResolveFastOperationContext(contextName);
                var rmOk = false;
                var rmOut = string.Empty;
                var attempts = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("正在删除镜像（普通）...", string.Format("image rm \"{0}\"", repoTag)),
                    new KeyValuePair<string, string>("正在删除镜像（强制）...", string.Format("image rm -f \"{0}\"", repoTag))
                };
                if (!string.IsNullOrWhiteSpace(selectedRow.ImageId))
                {
                    var imageId = selectedRow.ImageId.Trim();
                    attempts.Add(new KeyValuePair<string, string>("正在按 ImageID 删除（普通）...", string.Format("image rm \"{0}\"", imageId)));
                    attempts.Add(new KeyValuePair<string, string>("正在按 ImageID 删除（强制）...", string.Format("image rm -f \"{0}\"", imageId)));
                }

                for (var i = 0; i < attempts.Count; i++)
                {
                    var attempt = attempts[i];
                    SetProgressText(progressText, attempt.Key + "\n" + repoTag);
                    var rmResult = await RunDockerCommandWithHardTimeoutAsync(attempt.Value, contextName, 20000, 25000);
                    rmOk = rmResult.Ok;
                    rmOut = rmResult.Output;
                    if (rmOk)
                    {
                        break;
                    }

                    if (operationCanceled)
                    {
                        return;
                    }
                }
                if (operationCanceled)
                {
                    return;
                }

                if (!rmOk)
                {
                    var errorText = string.IsNullOrWhiteSpace(rmOut) ? "命令执行失败（无输出）。" : rmOut;
                    ShowScrollableErrorDialog("删除镜像失败", errorText);
                    return;
                }

                SetProgressText(progressText, "镜像删除完成，正在刷新界面...");
                await RefreshDeviceImageRowsFastAsync(selectedViewDeviceName);

                MessageBox.Show(
                    this,
                    "删除成功：\n" + repoTag,
                    "镜像和容器感知",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
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

                RestoreMainWindowFocus();
            }
        }

        private async void DashboardRefreshButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (!_hasReadDeviceInfo || _devices.Count == 0)
            {
                return;
            }

            var button = sender as Button;
            if (button != null)
            {
                button.IsEnabled = false;
            }

            try
            {
                if (_isRefreshing)
                {
                    await WaitForRuntimeRefreshIdleAsync(10000);
                }

                var selectedItem = DeviceSelector.SelectedItem as ComboBoxItem;
                var selectedDevice = selectedItem == null ? null : selectedItem.Tag as DeviceInfo;
                if (selectedDevice == null)
                {
                    // “全部设备”只在用户明确选择全部时读取所有已登记目标。
                    await RefreshRuntimeDataBindingsAsync(true, ContainerReadMode.LightweightSummary);
                }
                else
                {
                    var contextName = selectedDevice.ContextName;
                    if (string.IsNullOrWhiteSpace(contextName))
                    {
                        _deviceContextMap.TryGetValue(selectedDevice.Name, out contextName);
                    }

                    string identity;
                    if (!TryResolveSshIdentityForContext(contextName, out identity) ||
                        string.IsNullOrWhiteSpace(identity))
                    {
                        MessageBox.Show(
                            this,
                            "无法确定当前设备对应的 SSH 目标，本次未刷新其他设备。",
                            "刷新设备信息",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        return;
                    }

                    await RefreshRuntimeDataBindingsAsync(
                        true,
                        ContainerReadMode.LightweightSummary,
                        new[] { identity },
                        SnapshotApplyMode.MergeDevices);
                }
            }
            finally
            {
                if (button != null)
                {
                    button.IsEnabled = true;
                }
            }
        }

        private async Task WaitForRuntimeRefreshIdleAsync(int timeoutMs)
        {
            var timeoutAt = DateTime.Now.AddMilliseconds(Math.Max(0, timeoutMs));
            while (_isRefreshing && DateTime.Now < timeoutAt)
            {
                await Task.Delay(100);
            }
        }

        private bool TryResolveSshTargetForImageRow(DeviceImageRow row, string contextName, out string user, out string host, out string password)
        {
            user = string.Empty;
            host = string.Empty;
            password = string.Empty;

            var ctx = (contextName ?? string.Empty).Trim();
            if (ctx.StartsWith(SshContextPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var identity = ctx.Substring(SshContextPrefix.Length).Trim();
                if (TryResolveSshTarget(identity, out user, out host, out password))
                {
                    return true;
                }
            }

            var dockerHost = ReadDockerHostByContext(ctx);
            var dockerHostText = (dockerHost ?? string.Empty).Trim();
            if (dockerHostText.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    Uri uri;
                    if (Uri.TryCreate(dockerHostText, UriKind.Absolute, out uri))
                    {
                        var parsedUser = Uri.UnescapeDataString(uri.UserInfo ?? string.Empty).Trim();
                        var parsedHost = (uri.Host ?? string.Empty).Trim();
                        if (!string.IsNullOrWhiteSpace(parsedUser) && !string.IsNullOrWhiteSpace(parsedHost))
                        {
                            var identity = BuildSshTargetIdentity(parsedUser, parsedHost);
                            if (TryResolveSshTarget(identity, out user, out host, out password))
                            {
                                return true;
                            }
                        }
                    }
                }
                catch
                {
                }
            }

            var ip = ((row == null ? string.Empty : row.DeviceIp) ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(ip))
            {
                ip = ResolveContextIp(dockerHostText);
            }

            if (!string.IsNullOrWhiteSpace(ip))
            {
                foreach (var kv in _sshPasswordByTarget)
                {
                    var identity = (kv.Key ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(identity))
                    {
                        continue;
                    }

                    var at = identity.LastIndexOf('@');
                    if (at <= 0 || at >= identity.Length - 1)
                    {
                        continue;
                    }

                    var hostPart = identity.Substring(at + 1).Trim();
                    if (!string.Equals(hostPart, ip, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (TryResolveSshTarget(identity, out user, out host, out password))
                    {
                        return true;
                    }
                }
            }

            var deviceName = ((row == null ? string.Empty : row.DeviceName) ?? string.Empty).Trim();
            string mappedContext;
            if (!string.IsNullOrWhiteSpace(deviceName) && _deviceContextMap.TryGetValue(deviceName, out mappedContext))
            {
                var mapCtx = (mappedContext ?? string.Empty).Trim();
                if (mapCtx.StartsWith(SshContextPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var mappedIdentity = mapCtx.Substring(SshContextPrefix.Length).Trim();
                    if (TryResolveSshTarget(mappedIdentity, out user, out host, out password))
                    {
                        return true;
                    }
                }
            }

            var lastIdentity = BuildSshTargetIdentity(_readTargetUser, _readTargetIp);
            if (TryResolveSshTarget(lastIdentity, out user, out host, out password))
            {
                return true;
            }

            return false;
        }

        private bool TryResolveSshIdentityForContext(string contextName, out string identity)
        {
            identity = string.Empty;
            var ctx = (contextName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(ctx))
            {
                return false;
            }

            if (ctx.StartsWith(SshContextPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var directIdentity = ctx.Substring(SshContextPrefix.Length).Trim();
                if (!string.IsNullOrWhiteSpace(directIdentity))
                {
                    identity = directIdentity;
                    return true;
                }
            }

            var dockerHost = (ReadDockerHostByContext(ctx) ?? string.Empty).Trim();
            if (!dockerHost.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                Uri uri;
                if (!Uri.TryCreate(dockerHost, UriKind.Absolute, out uri))
                {
                    return false;
                }

                var parsedUser = Uri.UnescapeDataString(uri.UserInfo ?? string.Empty).Trim();
                var parsedHost = (uri.Host ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(parsedUser) || string.IsNullOrWhiteSpace(parsedHost))
                {
                    return false;
                }

                identity = BuildSshTargetIdentity(parsedUser, parsedHost);
                return !string.IsNullOrWhiteSpace(identity);
            }
            catch
            {
                return false;
            }
        }

        private void UpdateDeviceDashboard(DeviceInfo device)
        {
            _currentDevice = device;
            ImageCountText.Text = string.Format("{0}个", device.ImageCount);
            ContainerCountText.Text = string.Format("{0}个", device.ContainerCount);

            SelectedDeviceText.Text = device.Name;
            CpuUsageText.Text = string.Format("{0:F2}%", device.CpuUsage);
            MemoryUsageText.Text = string.Format("{0:F2}%", device.MemoryUsage);
            DiskUsageText.Text = string.Format("{0:F2}%", device.DiskUsage);

            UpdatePie(device.CpuUsage, device.MemoryUsage, device.DiskUsage);
            SetPieTooltips(device.CpuUsage, device.MemoryUsage, device.DiskUsage);
            PieDetailText.Text = "悬浮或点击饼图扇区查看详情";
        }

        private void BindImageRows(string deviceName, string deviceIp)
        {
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                DeviceImageGrid.ItemsSource = _allImageRows.ToList();
                return;
            }

            var byDeviceName = _allImageRows
                .Where(row => string.Equals(row.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (byDeviceName.Count > 0)
            {
                DeviceImageGrid.ItemsSource = byDeviceName;
                return;
            }

            DeviceImageGrid.ItemsSource = _allImageRows
                .Where(row => string.Equals(row.DeviceIp, deviceIp, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private void UpdateAllDevicesDashboard()
        {
            if (_devices.Count == 0)
            {
                _currentDevice = new DeviceInfo("全部设备", "-", 0, 0, 0, 0, 0);
                ImageCountText.Text = "0个";
                ContainerCountText.Text = "0个";
                SelectedDeviceText.Text = "全部设备";
                CpuUsageText.Text = "0.00%";
                MemoryUsageText.Text = "0.00%";
                DiskUsageText.Text = "0.00%";
                UpdatePie(0, 0, 0);
                SetPieTooltips(0, 0, 0);
                PieDetailText.Text = "暂无设备数据";
                DeviceImageGrid.ItemsSource = new List<DeviceImageRow>();
                return;
            }

            var imageTotal = _devices.Sum(d => d.ImageCount);
            var containerTotal = _devices.Sum(d => d.ContainerCount);
            var cpuAvg = _devices.Average(d => d.CpuUsage);
            var memoryAvg = _devices.Average(d => d.MemoryUsage);
            var diskAvg = _devices.Average(d => d.DiskUsage);
            _currentDevice = new DeviceInfo("全部设备", "-", cpuAvg, memoryAvg, diskAvg, imageTotal, containerTotal);

            ImageCountText.Text = string.Format("{0}个", imageTotal);
            ContainerCountText.Text = string.Format("{0}个", containerTotal);
            SelectedDeviceText.Text = "全部设备";

            CpuUsageText.Text = string.Format("{0:F2}%", cpuAvg);
            MemoryUsageText.Text = string.Format("{0:F2}%", memoryAvg);
            DiskUsageText.Text = string.Format("{0:F2}%", diskAvg);

            UpdatePie(cpuAvg, memoryAvg, diskAvg);
            SetPieTooltips(cpuAvg, memoryAvg, diskAvg);
            PieDetailText.Text = "当前显示全部设备平均资源占用";
        }

        private void UpdatePie(double cpu, double memory, double disk)
        {
            var total = cpu + memory + disk;
            if (total <= 0)
            {
                total = 1;
            }

            var cpuSweep = 360d * cpu / total;
            var memorySweep = 360d * memory / total;
            var diskSweep = Math.Max(0, 360d - cpuSweep - memorySweep);

            var pieWidth = PieCanvas != null && PieCanvas.Width > 0 ? PieCanvas.Width : 190d;
            var pieHeight = PieCanvas != null && PieCanvas.Height > 0 ? PieCanvas.Height : 190d;
            var centerX = pieWidth / 2d;
            var centerY = pieHeight / 2d;
            var radius = Math.Max(10d, Math.Min(pieWidth, pieHeight) / 2d - 6d);

            CpuSlicePath.Data = CreatePieSlice(centerX, centerY, radius, 0, cpuSweep);
            MemorySlicePath.Data = CreatePieSlice(centerX, centerY, radius, cpuSweep, memorySweep);
            DiskSlicePath.Data = CreatePieSlice(centerX, centerY, radius, cpuSweep + memorySweep, diskSweep);
        }

        private void SetPieTooltips(double cpu, double memory, double disk)
        {
            CpuSlicePath.ToolTip = string.Format("运行中的容器CPU占用: {0:F2}%", cpu);
            MemorySlicePath.ToolTip = string.Format("运行中的容器内存占用: {0:F2}%", memory);
            DiskSlicePath.ToolTip = string.Format("Docker数据的磁盘占用: {0:F2}%", disk);
        }

        private void PieSlice_OnMouseEnter(object sender, MouseEventArgs e)
        {
            UpdatePieDetailBySlice(sender as System.Windows.Shapes.Path);
        }

        private void PieSlice_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            UpdatePieDetailBySlice(sender as System.Windows.Shapes.Path);
        }

        private void UpdatePieDetailBySlice(System.Windows.Shapes.Path slice)
        {
            if (slice == null || _currentDevice == null)
            {
                return;
            }

            if (slice == CpuSlicePath)
            {
                PieDetailText.Text = string.Format("运行中的容器CPU占用: {0:F2}%", _currentDevice.CpuUsage);
                return;
            }

            if (slice == MemorySlicePath)
            {
                PieDetailText.Text = string.Format("运行中的容器内存占用: {0:F2}%", _currentDevice.MemoryUsage);
                return;
            }

            if (slice == DiskSlicePath)
            {
                PieDetailText.Text = string.Format("Docker数据的磁盘占用: {0:F2}%", _currentDevice.DiskUsage);
            }
        }

        private static Geometry CreatePieSlice(double centerX, double centerY, double radius, double startAngle, double sweepAngle)
        {
            if (sweepAngle <= 0.001)
            {
                return Geometry.Empty;
            }

            if (sweepAngle >= 359.999)
            {
                sweepAngle = 359.999;
            }

            var startRadians = startAngle * Math.PI / 180d;
            var endRadians = (startAngle + sweepAngle) * Math.PI / 180d;

            var startPoint = new Point(centerX + radius * Math.Cos(startRadians), centerY + radius * Math.Sin(startRadians));
            var endPoint = new Point(centerX + radius * Math.Cos(endRadians), centerY + radius * Math.Sin(endRadians));
            var isLargeArc = sweepAngle > 180;

            var figure = new PathFigure
            {
                StartPoint = new Point(centerX, centerY),
                IsClosed = true
            };
            figure.Segments.Add(new LineSegment(startPoint, true));
            figure.Segments.Add(new ArcSegment(endPoint, new Size(radius, radius), 0, isLargeArc, SweepDirection.Clockwise, true));
            figure.Segments.Add(new LineSegment(new Point(centerX, centerY), true));

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            return geometry;
        }

        private void ApplyDockerSnapshot(DockerRuntimeSnapshot snapshot, SnapshotApplyMode applyMode)
        {
            if (applyMode == SnapshotApplyMode.MergeDevices)
            {
                MergeDockerSnapshot(snapshot);
                return;
            }

            var devices = snapshot.Devices;
            _devices.Clear();
            _devices.AddRange(devices);

            _allImageRows.Clear();
            _allImageRows.AddRange(snapshot.DeviceImageRows);

            if (snapshot.ContainerReadMode == ContainerReadMode.FullDetails)
            {
                _containerRows.Clear();
                _containerRows.AddRange(snapshot.ContainerRows);
            }

            // 策略页“预部署镜像”列表固定以本地 Docker 数据/手动操作为准，
            // 不再在运行时快照刷新时用远端上下文数据覆盖，避免界面跳变。

            _deviceContextMap.Clear();
            foreach (var pair in snapshot.DeviceContexts)
            {
                _deviceContextMap[pair.Key] = pair.Value;
            }
        }

        private void MergeDockerSnapshot(DockerRuntimeSnapshot snapshot)
        {
            for (var i = 0; i < snapshot.Devices.Count; i++)
            {
                var incoming = snapshot.Devices[i];
                var existingIndex = _devices.FindIndex(device => IsSameDevice(device, incoming));
                var existing = existingIndex >= 0 ? _devices[existingIndex] : null;
                var deviceName = existing == null ? incoming.Name : existing.Name;
                var mergedDevice = new DeviceInfo(
                    deviceName,
                    incoming.Ip,
                    incoming.CpuUsage,
                    incoming.MemoryUsage,
                    incoming.DiskUsage,
                    incoming.ImageCount,
                    incoming.ContainerCount,
                    incoming.ContextName);

                if (existingIndex >= 0)
                {
                    _devices[existingIndex] = mergedDevice;
                }
                else
                {
                    _devices.Add(mergedDevice);
                }

                _allImageRows.RemoveAll(row =>
                    string.Equals(row.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(incoming.ContextName) &&
                     string.Equals(row.ContextName, incoming.ContextName, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(incoming.Ip) &&
                     string.Equals(row.DeviceIp, incoming.Ip, StringComparison.OrdinalIgnoreCase)));

                var incomingRows = snapshot.DeviceImageRows
                    .Where(row =>
                        string.Equals(row.DeviceName, incoming.Name, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(incoming.ContextName) &&
                         string.Equals(row.ContextName, incoming.ContextName, StringComparison.OrdinalIgnoreCase)))
                    .Select(row => new DeviceImageRow(
                        row.DeviceIp,
                        row.Repository,
                        row.Tag,
                        row.Created,
                        row.Size,
                        row.Labels,
                        row.ImageId,
                        row.ChineseName,
                        deviceName,
                        row.ContextName))
                    .ToList();
                _allImageRows.AddRange(incomingRows);

                var staleContextKeys = _deviceContextMap
                    .Where(pair =>
                        string.Equals(pair.Key, deviceName, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(incoming.ContextName) &&
                         string.Equals(pair.Value, incoming.ContextName, StringComparison.OrdinalIgnoreCase)))
                    .Select(pair => pair.Key)
                    .ToList();
                for (var keyIndex = 0; keyIndex < staleContextKeys.Count; keyIndex++)
                {
                    _deviceContextMap.Remove(staleContextKeys[keyIndex]);
                }
                _deviceContextMap[deviceName] = incoming.ContextName;

                if (snapshot.ContainerReadMode == ContainerReadMode.FullDetails)
                {
                    _containerRows.RemoveAll(row => string.Equals(row.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
                    _containerRows.AddRange(snapshot.ContainerRows.Where(row =>
                        string.Equals(row.DeviceName, incoming.Name, StringComparison.OrdinalIgnoreCase)));
                }
            }
        }

        private static bool IsSameDevice(DeviceInfo left, DeviceInfo right)
        {
            if (left == null || right == null)
            {
                return false;
            }

            return (!string.IsNullOrWhiteSpace(left.ContextName) &&
                    string.Equals(left.ContextName, right.ContextName, StringComparison.OrdinalIgnoreCase)) ||
                   (!string.IsNullOrWhiteSpace(left.Ip) &&
                    string.Equals(left.Ip, right.Ip, StringComparison.OrdinalIgnoreCase));
        }

        private void RefreshLocalServiceImageLists()
        {
            var localImages = ReadDockerImages(null);
            var mergedPrepared = ReadLocalPreparedImagesFromDocker(localImages);

            var preparedRepoTags = new HashSet<string>(
                mergedPrepared
                    .Select(r => NormalizeRepoTag(r.RepoTag))
                    .Where(tag => !string.IsNullOrWhiteSpace(tag)),
                StringComparer.OrdinalIgnoreCase);

            var localBaseImages = ReadLocalBaseImages(localImages, preparedRepoTags);
            _sourceImages.Clear();
            _sourceImages.AddRange(localBaseImages);

            _preparedImages.Clear();
            _preparedImages.AddRange(mergedPrepared
                .OrderByDescending(r => r.Created, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.RepoTag, StringComparer.OrdinalIgnoreCase)
                .ToList());

            _programFiles.Clear();
            _programFiles.AddRange(ReadLocalProgramPackages());
        }

        private List<ImageComposeRow> ReadLocalBaseImages(List<DockerImageInfo> localImages, HashSet<string> excludedRepoTags)
        {
            var rows = new List<ImageComposeRow>();
            localImages = localImages ?? new List<DockerImageInfo>();
            for (var i = 0; i < localImages.Count; i++)
            {
                var img = localImages[i];
                var repository = (img.Repository ?? string.Empty).Trim();
                var tag = (img.Tag ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(repository) ||
                    string.IsNullOrWhiteSpace(tag) ||
                    string.Equals(repository, "<none>", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tag, "<none>", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var repoTag = repository + ":" + tag;
                if (excludedRepoTags != null && excludedRepoTags.Contains(repoTag))
                {
                    continue;
                }

                var imageId = NormalizeImageId(img.Id);
                var created = img.CreatedAt;
                var size = img.Size;
                var chineseName = ResolveImageChineseName("LOCAL", "本地", string.Empty, repository, tag, img.Id);
                rows.Add(new ImageComposeRow(repoTag, imageId, created, size, "本地", "LOCAL", chineseName));
            }

            return rows
                .OrderBy(r => r.RepoTag, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.ImageId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private List<ImageComposeRow> ReadLocalPreparedImagesFromDocker(List<DockerImageInfo> localImages)
        {
            var rows = new List<ImageComposeRow>();
            localImages = localImages ?? new List<DockerImageInfo>();
            for (var i = 0; i < localImages.Count; i++)
            {
                var img = localImages[i];
                var repository = (img.Repository ?? string.Empty).Trim();
                var tag = (img.Tag ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(repository) ||
                    string.IsNullOrWhiteSpace(tag) ||
                    string.Equals(repository, "<none>", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tag, "<none>", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!tag.StartsWith("custom_image", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var repoTag = repository + ":" + tag;
                var chineseName = ResolveImageChineseName("LOCAL", "本地", string.Empty, repository, tag, img.Id);
                rows.Add(new ImageComposeRow(
                    repoTag,
                    NormalizeImageId(img.Id),
                    ResolveCustomImageCreatedTime(tag, img.CreatedAt),
                    img.Size,
                    "本地",
                    "LOCAL",
                    chineseName));
            }

            return rows
                .OrderByDescending(r => r.Created, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.RepoTag, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string ResolveCustomImageCreatedTime(string tag, string fallbackCreatedAt)
        {
            var value = (tag ?? string.Empty).Trim();
            const string prefix = "custom_image_";
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return fallbackCreatedAt ?? string.Empty;
            }

            var stamp = value.Substring(prefix.Length).Trim();
            if (stamp.Length < 14)
            {
                return fallbackCreatedAt ?? string.Empty;
            }

            DateTime parsed;
            if (DateTime.TryParseExact(
                stamp.Substring(0, 14),
                "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out parsed))
            {
                return parsed.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }

            return fallbackCreatedAt ?? string.Empty;
        }

        private string ResolveSnapshotDeviceName(string contextName, string ip, HashSet<string> usedDeviceNames)
        {
            var existing = _devices.FirstOrDefault(device =>
                (!string.IsNullOrWhiteSpace(contextName) &&
                 string.Equals(device.ContextName, contextName, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(ip) &&
                 string.Equals(device.Ip, ip, StringComparison.OrdinalIgnoreCase)));
            if (existing != null &&
                (usedDeviceNames == null || !usedDeviceNames.Contains(existing.Name)))
            {
                return existing.Name;
            }

            var unavailableNames = new HashSet<string>(
                _devices.Where(device => device != null).Select(device => device.Name),
                StringComparer.OrdinalIgnoreCase);
            if (usedDeviceNames != null)
            {
                unavailableNames.UnionWith(usedDeviceNames);
            }

            var deviceIndex = 1;
            while (unavailableNames.Contains(string.Format("设备{0}", deviceIndex)))
            {
                deviceIndex++;
            }
            return string.Format("设备{0}", deviceIndex);
        }

        private bool TryReadDockerSnapshot(out DockerRuntimeSnapshot snapshot, string localIp, IEnumerable<string> targetIps, ContainerReadMode containerReadMode)
        {
            snapshot = null;

            var targetSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (targetIps != null)
            {
                foreach (var ip in targetIps)
                {
                    var trimmed = (ip ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(trimmed))
                    {
                        targetSet.Add(trimmed);
                    }
                }
            }

            var contexts = new List<DockerContextInfo>();
            if (targetSet.Count > 0)
            {
                foreach (var target in targetSet)
                {
                    var identity = (target ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(identity))
                    {
                        continue;
                    }

                    var expectedHost = "ssh://" + identity;
                    if (_sshPasswordByTarget.ContainsKey(identity))
                    {
                        if (!contexts.Any(c => string.Equals(c.Name, SshContextPrefix + identity, StringComparison.OrdinalIgnoreCase)))
                        {
                            contexts.Add(new DockerContextInfo(SshContextPrefix + identity, expectedHost));
                        }
                        continue;
                    }
                }
            }
            else
            {
                var sshContexts = ReadDockerContexts()
                    .Where(IsSshContext)
                    .ToList();
                contexts.AddRange(sshContexts);
            }

            if (contexts.Count == 0)
            {
                return false;
            }

            var devices = new List<DeviceInfo>();
            var imageRows = new List<DeviceImageRow>();
            var containerRows = new List<ContainerComposeRow>();
            var deviceContexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var seenIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var usedDeviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < contexts.Count; i++)
            {
                var context = contexts[i];
                var ip = ResolveContextIp(context.DockerHost);
                if (!string.IsNullOrWhiteSpace(ip) && seenIps.Contains(ip))
                {
                    continue;
                }
                seenIps.Add(ip);
                var deviceName = ResolveSnapshotDeviceName(context.Name, ip, usedDeviceNames);
                usedDeviceNames.Add(deviceName);

                var images = new List<DockerImageInfo>();
                var containers = new List<DockerContainerInfo>();
                var containerStats = new Dictionary<string, DockerContainerStatsInfo>(StringComparer.OrdinalIgnoreCase);
                var hostCpuLimitText = "-";
                var cpuUsage = 0d;
                var memoryUsage = 0d;
                var diskUsage = 0d;
                var runningCount = 0;

                if (containerReadMode == ContainerReadMode.FullDetails)
                {
                    images = ReadDockerImages(context.Name);
                    containers = ReadDockerContainers(context.Name);
                    containerStats = ReadDockerContainerStats(context.Name);
                    hostCpuLimitText = ReadHostCpuLimitText(context.Name);
                    var hostMemoryTotalMb = ReadHostMemoryLimitMb(context.Name);
                    cpuUsage = CalculateContainerCpuUsagePercent(containerStats.Values, hostCpuLimitText);
                    memoryUsage = CalculateContainerMemoryUsagePercent(containerStats.Values, hostMemoryTotalMb);
                    runningCount = containers.Count(c => string.Equals(c.State, "running", StringComparison.OrdinalIgnoreCase));
                    diskUsage = ReadDockerRootDiskUsagePercent(context.Name);
                }
                else
                {
                    LightweightDockerData lightweightData;
                    if (!TryReadLightweightDockerData(context.Name, out lightweightData))
                    {
                        return false;
                    }

                    images = lightweightData.Images;
                    runningCount = lightweightData.RunningContainerCount;
                    cpuUsage = lightweightData.CpuUsagePercent;
                    memoryUsage = lightweightData.MemoryUsagePercent;
                    diskUsage = lightweightData.DiskUsagePercent;
                }

                if (containerReadMode == ContainerReadMode.FullDetails && runningCount == 0)
                {
                    runningCount = containers.Count(c => c.Status.IndexOf("Up", StringComparison.OrdinalIgnoreCase) >= 0);
                }

                devices.Add(new DeviceInfo(deviceName, ip, cpuUsage, memoryUsage, diskUsage, images.Count, runningCount, context.Name));
                deviceContexts[deviceName] = context.Name;

                for (var imgIndex = 0; imgIndex < images.Count; imgIndex++)
                {
                    var img = images[imgIndex];
                    var imageChineseName = ResolveImageChineseName(
                        context.Name,
                        deviceName,
                        ip,
                        img.Repository,
                        img.Tag,
                        img.Id);
                    imageRows.Add(new DeviceImageRow(
                        ip,
                        img.Repository,
                        img.Tag,
                        img.CreatedAt,
                        img.Size,
                        "-",
                        ShortId(img.Id, 24),
                        imageChineseName,
                        deviceName,
                        context.Name));

                }

                for (var cIndex = 0; containerReadMode == ContainerReadMode.FullDetails && cIndex < containers.Count; cIndex++)
                {
                    var c = containers[cIndex];
                    DockerContainerStatsInfo stat;
                    var hasStat = TryGetContainerStats(containerStats, c.Id, out stat);
                    var limitInfo = ReadContainerLimitInfo(context.Name, c.Id);
                    var portsText = ResolveContainerPortsForDisplay(context.Name, c.Id, c.Ports, "-");
                    var normalizedName = c.Name.StartsWith("/") ? c.Name : "/" + c.Name;
                    var memoryUsageText = hasStat && stat != null
                        ? stat.MemoryUsageText
                        : "-";
                    memoryUsageText = MergeMemoryUsageWithLimit(memoryUsageText, limitInfo.MemoryLimitText);
                    containerRows.Add(new ContainerComposeRow(
                        deviceName,
                        c.Id,
                        normalizedName,
                        BuildChineseContainerName(c.Name),
                        NormalizeImageNameWithTag(c.Image),
                        string.IsNullOrWhiteSpace(c.State) ? "unknown" : c.State,
                        string.IsNullOrWhiteSpace(portsText) ? "-" : portsText,
                        BuildCpuCoresDisplayText(limitInfo.CpuCoresText, hostCpuLimitText),
                        hasStat ? stat.CpuPercentText : "0%",
                        memoryUsageText,
                        hasStat ? stat.MemoryPercentText : "0%",
                        hasStat ? stat.BlockIoText : "0B / 0B",
                        c.Status,
                        c.Id,
                        c.Size));
                }
            }

            if (devices.Count == 0)
            {
                return false;
            }

            snapshot = new DockerRuntimeSnapshot(
                devices,
                imageRows,
                containerRows,
                deviceContexts,
                containerReadMode);

            return true;
        }

        private static bool IsSshContext(DockerContextInfo context)
        {
            if (context == null)
            {
                return false;
            }

            var host = (context.DockerHost ?? string.Empty).Trim();
            return host.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetDockerBuildWorkspaceRoot()
        {
            var starts = new List<string>();

            if (!string.IsNullOrWhiteSpace(Environment.CurrentDirectory))
            {
                starts.Add(Environment.CurrentDirectory);
            }

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            if (!string.IsNullOrWhiteSpace(baseDir))
            {
                starts.Add(baseDir);
            }

            var candidates = new List<string>();
            for (var i = 0; i < starts.Count; i++)
            {
                var founds = FindDockerBuildWorkspacesFrom(starts[i]);
                for (var j = 0; j < founds.Count; j++)
                {
                    var path = founds[j];
                    if (!candidates.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        candidates.Add(path);
                    }
                }
            }

            if (candidates.Count > 0)
            {
                var best = candidates
                    .OrderByDescending(GetDockerWorkspaceScore)
                    .ThenByDescending(p => p.Length)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(best))
                {
                    return best;
                }
            }

            // 回退：若未找到现有目录，则按当前目录创建。
            return System.IO.Path.Combine(Environment.CurrentDirectory, "dockerbuild_workspace");
        }

        private static string EnsureDockerComposeFile()
        {
            var workspaceRoot = GetDockerBuildWorkspaceRoot();
            if (!Directory.Exists(workspaceRoot))
            {
                Directory.CreateDirectory(workspaceRoot);
            }

            var composePath = System.IO.Path.Combine(workspaceRoot, "docker-compose.yml");
            if (!File.Exists(composePath))
            {
                var content = string.Join(
                    Environment.NewLine,
                    "services:",
                    "  cass_ai:",
                    "    image: cass_ai:latest",
                    "    container_name: cass_ai",
                    "    privileged: true",
                    "    network_mode: host",
                    "    volumes:",
                    "      - /home/ftpuser:/home/ftpuser",
                    "    stdin_open: true",
                    "    tty: true",
                    "    restart: unless-stopped",
                    string.Empty);
                File.WriteAllText(composePath, content, new UTF8Encoding(false));
            }

            return composePath;
        }

        private static string NormalizeContainerComposeServiceName(string containerName)
        {
            var value = (containerName ?? string.Empty).Trim().TrimStart('/');
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = new StringBuilder(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                var ch = char.ToLowerInvariant(value[i]);
                var isAlphaNum = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9');
                if (isAlphaNum || ch == '-' || ch == '_')
                {
                    normalized.Append(ch);
                }
                else
                {
                    normalized.Append('_');
                }
            }

            return normalized.ToString().Trim('_');
        }

        private static List<string> UpsertComposeServicesForSelectedRows(string composePath, List<ContainerComposeRow> selectedRows)
        {
            var rows = (selectedRows ?? new List<ContainerComposeRow>())
                .Where(r => r != null)
                .ToList();

            var serviceRows = rows
                .Select(r => new
                {
                    ServiceName = NormalizeContainerComposeServiceName(r.Name),
                    ContainerName = NormalizeContainerComposeServiceName(r.Name),
                    Image = NormalizeComposeImageName(r.Image)
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.ServiceName))
                .GroupBy(x => x.ServiceName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (serviceRows.Count == 0)
            {
                return new List<string>();
            }

            var sb = new StringBuilder();
            sb.AppendLine("services:");
            for (var i = 0; i < serviceRows.Count; i++)
            {
                var s = serviceRows[i];
                sb.AppendLine("  " + s.ServiceName + ":");
                sb.AppendLine("    image: " + s.Image);
                sb.AppendLine("    container_name: " + s.ContainerName);
                sb.AppendLine("    privileged: true");
                sb.AppendLine("    network_mode: host");
                sb.AppendLine("    volumes:");
                sb.AppendLine("      - /home/ftpuser:/home/ftpuser");
                sb.AppendLine("    stdin_open: true");
                sb.AppendLine("    tty: true");
                sb.AppendLine("    restart: unless-stopped");
            }

            File.WriteAllText(composePath, sb.ToString(), new UTF8Encoding(false));
            return serviceRows.Select(x => x.ServiceName).ToList();
        }

        private static string NormalizeComposeImageName(string imageName)
        {
            var value = (imageName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value) || value == "-")
            {
                return "cass_ai:latest";
            }

            return value.Contains(":") ? value : value + ":latest";
        }

        private static string NormalizeImageNameWithTag(string imageName)
        {
            var value = (imageName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value) || value == "-")
            {
                return "-";
            }

            var slashIndex = value.LastIndexOf('/');
            var colonIndex = value.LastIndexOf(':');
            var hasTag = colonIndex > slashIndex;
            return hasTag ? value : value + ":latest";
        }

        private static List<string> FindDockerBuildWorkspacesFrom(string startPath)
        {
            var results = new List<string>();
            if (string.IsNullOrWhiteSpace(startPath))
            {
                return results;
            }

            var normalized = startPath;
            if (!Directory.Exists(normalized) && File.Exists(normalized))
            {
                normalized = System.IO.Path.GetDirectoryName(normalized);
            }

            var dir = new DirectoryInfo(normalized ?? string.Empty);
            var maxDepth = 16;
            while (dir != null && maxDepth-- > 0)
            {
                var candidate = System.IO.Path.Combine(dir.FullName, "dockerbuild_workspace");
                if (Directory.Exists(candidate))
                {
                    results.Add(candidate);
                }

                dir = dir.Parent;
            }

            return results;
        }

        private static int GetDockerWorkspaceScore(string workspacePath)
        {
            if (string.IsNullOrWhiteSpace(workspacePath))
            {
                return int.MinValue;
            }

            var score = 0;
            if (workspacePath.IndexOf("\\bin\\", StringComparison.OrdinalIgnoreCase) < 0)
            {
                score += 10;
            }

            var programDir = System.IO.Path.Combine(workspacePath, "program_package");
            var basicDir = System.IO.Path.Combine(workspacePath, "basic_image");
            var outputDir = System.IO.Path.Combine(workspacePath, "tempBuild_image");

            if (Directory.Exists(programDir))
            {
                score += 50;
                try
                {
                    var hasProgramFiles = Directory.GetFiles(programDir).Any(IsProgramPackageFile);
                    if (hasProgramFiles)
                    {
                        score += 200;
                    }
                }
                catch
                {
                }
            }

            if (Directory.Exists(basicDir))
            {
                score += 20;
            }

            if (Directory.Exists(outputDir))
            {
                score += 15;
            }

            return score;
        }

        private static string GetProgramPackageDirectory()
        {
            return System.IO.Path.Combine(GetDockerBuildWorkspaceRoot(), "program_package");
        }

        private static string GetTempBuildImageDirectory()
        {
            return System.IO.Path.Combine(GetDockerBuildWorkspaceRoot(), "tempBuild_image");
        }

        private static string GetBasicImageDirectory()
        {
            return System.IO.Path.Combine(GetDockerBuildWorkspaceRoot(), "basic_image");
        }

        private List<ProgramFileRow> ReadLocalProgramPackages()
        {
            var results = new List<ProgramFileRow>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidateDirs = new List<string>();

            var programPackageDir = GetProgramPackageDirectory();
            if (!string.IsNullOrWhiteSpace(programPackageDir))
            {
                if (!Directory.Exists(programPackageDir))
                {
                    Directory.CreateDirectory(programPackageDir);
                }
                candidateDirs.Add(programPackageDir);
            }

            for (var i = 0; i < candidateDirs.Count; i++)
            {
                var dir = candidateDirs[i];
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    continue;
                }

                string[] files;
                try
                {
                    files = Directory.GetFiles(dir);
                }
                catch
                {
                    continue;
                }

                for (var f = 0; f < files.Length; f++)
                {
                    var filePath = files[f];
                    if (string.IsNullOrWhiteSpace(filePath) || seenPaths.Contains(filePath))
                    {
                        continue;
                    }

                    var fileName = System.IO.Path.GetFileName(filePath);
                    if (!IsProgramPackageFile(fileName))
                    {
                        continue;
                    }

                    FileInfo info;
                    try
                    {
                        info = new FileInfo(filePath);
                    }
                    catch
                    {
                        continue;
                    }

                    seenPaths.Add(filePath);
                    results.Add(new ProgramFileRow(fileName, FormatBytes(info.Length), info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"), filePath));
                }
            }

            return results
                .OrderByDescending(r => r.ModifiedAt, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool IsProgramPackageFile(string fileName)
        {
            var name = (fileName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            return name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith(".sh", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
        }

        private List<DockerContextInfo> ReadDockerContexts()
        {
            string output;
            if (!TryRunDockerCommand("context ls --format \"{{.Name}}|{{.Current}}\"", null, out output))
            {
                return new List<DockerContextInfo>();
            }

            var contexts = new List<DockerContextInfo>();
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < lines.Length; i++)
            {
                var parts = lines[i].Split('|');
                if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
                {
                    continue;
                }

                var name = parts[0].Trim();
                var dockerHost = ReadDockerHostByContext(name);
                contexts.Add(new DockerContextInfo(name, dockerHost));
            }

            return contexts;
        }

        private bool TryEnsureDockerContextForTarget(string targetIdentity, out string ensuredContextName)
        {
            ensuredContextName = string.Empty;
            var identity = (targetIdentity ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(identity))
            {
                return false;
            }

            var expectedHost = "ssh://" + identity;
            var existing = ReadDockerContexts()
                .Where(IsSshContext)
                .FirstOrDefault(c => string.Equals((c.DockerHost ?? string.Empty).Trim(), expectedHost, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                ensuredContextName = existing.Name;
                return true;
            }

            var baseName = BuildAutoSshContextName(identity);
            var existingNames = new HashSet<string>(
                ReadDockerContexts().Select(c => c.Name ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < 20; i++)
            {
                var candidate = i == 0 ? baseName : (baseName + "_" + (i + 1).ToString(CultureInfo.InvariantCulture));
                if (existingNames.Contains(candidate))
                {
                    continue;
                }

                string createOut;
                var createCmd = string.Format("context create \"{0}\" --docker \"host={1}\"", candidate, expectedHost);
                if (TryRunDockerCommand(createCmd, null, out createOut, 20000))
                {
                    ensuredContextName = candidate;
                    return true;
                }
            }

            return false;
        }

        private static string BuildAutoSshContextName(string targetIdentity)
        {
            var identity = (targetIdentity ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(identity))
            {
                return "auto_ssh";
            }

            var sb = new StringBuilder();
            for (var i = 0; i < identity.Length; i++)
            {
                var ch = identity[i];
                if ((ch >= 'a' && ch <= 'z') ||
                    (ch >= 'A' && ch <= 'Z') ||
                    (ch >= '0' && ch <= '9'))
                {
                    sb.Append(char.ToLowerInvariant(ch));
                }
                else
                {
                    sb.Append('_');
                }
            }

            var normalized = sb.ToString().Trim('_');
            if (string.IsNullOrWhiteSpace(normalized))
            {
                normalized = "ssh_target";
            }

            return "auto_" + normalized;
        }

        private string ReadDockerHostByContext(string contextName)
        {
            var normalizedContextName = (contextName ?? string.Empty).Trim();
            if (normalizedContextName.StartsWith(SshContextPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var identity = normalizedContextName.Substring(SshContextPrefix.Length).Trim();
                if (!string.IsNullOrWhiteSpace(identity))
                {
                    return "ssh://" + identity;
                }
            }

            string output;
            var command = string.Format("context inspect \"{0}\" --format \"{{{{(index .Endpoints \\\"docker\\\").Host}}}}\"", normalizedContextName);
            if (TryRunDockerCommand(command, null, out output) && !string.IsNullOrWhiteSpace(output))
            {
                return output.Trim();
            }

            return string.Empty;
        }

        private static bool IsContextMatchTarget(DockerContextInfo context, string targetIp)
        {
            if (context == null || string.IsNullOrWhiteSpace(targetIp))
            {
                return true;
            }

            var targetUser = string.Empty;
            var targetHostIp = targetIp;
            var at = targetIp.IndexOf('@');
            if (at > 0 && at < targetIp.Length - 1)
            {
                targetUser = targetIp.Substring(0, at).Trim();
                targetHostIp = targetIp.Substring(at + 1).Trim();
            }

            var resolvedIp = ResolveContextIp(context.DockerHost);
            if (string.Equals(resolvedIp, targetHostIp, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(targetUser))
                {
                    return true;
                }

                var hostText2 = context.DockerHost ?? string.Empty;
                var userToken = "ssh://" + targetUser + "@";
                return hostText2.IndexOf(userToken, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            var hostText = context.DockerHost ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(targetUser))
            {
                var targetIdentity = targetUser + "@" + targetHostIp;
                return hostText.IndexOf(targetIdentity, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            return hostText.IndexOf(targetHostIp, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string BuildSshTargetIdentity(string user, string ip)
        {
            var u = (user ?? string.Empty).Trim();
            var h = (ip ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(u) ? h : (u + "@" + h);
        }

        private List<DockerImageInfo> ReadDockerImages(string contextName)
        {
            string output;
            if (!TryRunDockerCommand("image ls --no-trunc --format \"{{.ID}}|{{.Repository}}|{{.Tag}}|{{.CreatedAt}}|{{.Size}}\"", contextName, out output))
            {
                return new List<DockerImageInfo>();
            }

            return ParseDockerImages(output);
        }

        private static List<DockerImageInfo> ParseDockerImages(string output)
        {
            var result = new List<DockerImageInfo>();
            var lines = (output ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < lines.Length; i++)
            {
                var parts = lines[i].Split('|');
                if (parts.Length < 5)
                {
                    continue;
                }

                result.Add(new DockerImageInfo(
                    parts[0].Trim(),
                    string.IsNullOrWhiteSpace(parts[1]) ? "<none>" : parts[1].Trim(),
                    string.IsNullOrWhiteSpace(parts[2]) ? "<none>" : parts[2].Trim(),
                    parts[3].Trim(),
                    parts[4].Trim()));
            }

            return result;
        }

        private List<DockerContainerInfo> ReadDockerContainers(string contextName)
        {
            string output;
            if (!TryRunDockerCommand("ps -a --no-trunc --format \"{{.ID}}|{{.Names}}|{{.Image}}|{{.Ports}}|{{.Status}}\"", contextName, out output))
            {
                return new List<DockerContainerInfo>();
            }

            var result = new List<DockerContainerInfo>();
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < lines.Length; i++)
            {
                var parts = lines[i].Split('|');
                if (parts.Length < 5)
                {
                    continue;
                }

                var status = parts[4].Trim();
                result.Add(new DockerContainerInfo(
                    parts[0].Trim(),
                    parts[1].Trim(),
                    parts[2].Trim(),
                    parts[3].Trim(),
                    status,
                    InferContainerStateFromStatus(status),
                    "-"));
            }

            return result;
        }

        private DockerContainerInfo ReadSingleContainerInfo(string contextName, string containerId, string containerName)
        {
            string output;
            if (!string.IsNullOrWhiteSpace(containerId))
            {
                var byIdCmd = string.Format("ps -a --no-trunc --filter \"id={0}\" --format \"{{{{.ID}}}}|{{{{.Names}}}}|{{{{.Image}}}}|{{{{.Ports}}}}|{{{{.Status}}}}\"", containerId);
                if (TryRunDockerCommand(byIdCmd, contextName, out output))
                {
                    var row = ParseSingleContainerInfo(output);
                    if (row != null)
                    {
                        return row;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(containerName))
            {
                var cleanName = containerName.Trim().TrimStart('/');
                var byNameCmd = string.Format("ps -a --no-trunc --filter \"name=^{0}$\" --format \"{{{{.ID}}}}|{{{{.Names}}}}|{{{{.Image}}}}|{{{{.Ports}}}}|{{{{.Status}}}}\"", cleanName);
                if (TryRunDockerCommand(byNameCmd, contextName, out output))
                {
                    return ParseSingleContainerInfo(output);
                }
            }

            return null;
        }

        private static DockerContainerInfo ParseSingleContainerInfo(string output)
        {
            var lines = (output ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
            {
                return null;
            }

            var parts = lines[0].Split('|');
            if (parts.Length < 5)
            {
                return null;
            }

            var status = parts[4].Trim();
            return new DockerContainerInfo(
                parts[0].Trim(),
                parts[1].Trim(),
                parts[2].Trim(),
                parts[3].Trim(),
                status,
                InferContainerStateFromStatus(status),
                "-");
        }

        private static string InferContainerStateFromStatus(string status)
        {
            var text = (status ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return "unknown";
            }

            if (text.StartsWith("Up", StringComparison.OrdinalIgnoreCase))
            {
                return "running";
            }

            if (text.StartsWith("Exited", StringComparison.OrdinalIgnoreCase))
            {
                return "exited";
            }

            if (text.StartsWith("Created", StringComparison.OrdinalIgnoreCase))
            {
                return "created";
            }

            return "unknown";
        }

        private static bool TryGetContainerStats(Dictionary<string, DockerContainerStatsInfo> statsById, string containerId, out DockerContainerStatsInfo stats)
        {
            stats = null;
            if (statsById == null || statsById.Count == 0 || string.IsNullOrWhiteSpace(containerId))
            {
                return false;
            }

            var normalized = containerId.Trim();
            if (statsById.TryGetValue(normalized, out stats))
            {
                return true;
            }

            foreach (var kv in statsById)
            {
                if (normalized.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase) ||
                    kv.Key.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
                {
                    stats = kv.Value;
                    return true;
                }
            }

            return false;
        }

        private const string LightweightImagesBeginMarker = "__WPFAPP1_9F31_IMAGES_BEGIN__";
        private const string LightweightImagesEndMarker = "__WPFAPP1_9F31_IMAGES_END__";
        private const string LightweightStatsBeginMarker = "__WPFAPP1_9F31_STATS_BEGIN__";
        private const string LightweightStatsEndMarker = "__WPFAPP1_9F31_STATS_END__";
        private const string LightweightInfoBeginMarker = "__WPFAPP1_9F31_INFO_BEGIN__";
        private const string LightweightInfoEndMarker = "__WPFAPP1_9F31_INFO_END__";
        private const string LightweightDiskBeginMarker = "__WPFAPP1_9F31_DISK_BEGIN__";
        private const string LightweightDiskEndMarker = "__WPFAPP1_9F31_DISK_END__";
        private const string LightweightCompleteMarker = "__WPFAPP1_9F31_COMPLETE__";

        private bool TryReadLightweightDockerData(string contextName, out LightweightDockerData data)
        {
            data = null;
            string identity;
            if (!TryResolveSshIdentityForContext(contextName, out identity) || string.IsNullOrWhiteSpace(identity))
            {
                return false;
            }

            string user;
            string host;
            string password;
            if (!TryResolveSshTarget(identity, out user, out host, out password))
            {
                return false;
            }

            var dockerExecutables = new[] { "docker", "/usr/bin/docker", "/usr/local/bin/docker" };
            var candidates = new List<Tuple<string, bool>>();
            for (var i = 0; i < dockerExecutables.Length; i++)
            {
                candidates.Add(Tuple.Create(dockerExecutables[i], false));
            }
            for (var i = 0; i < dockerExecutables.Length; i++)
            {
                candidates.Add(Tuple.Create(dockerExecutables[i], true));
            }

            var targetIdentity = BuildSshTargetIdentity(user, host);
            var candidateIndexes = BuildSshCandidateOrder(targetIdentity, candidates.Count);
            var bestError = string.Empty;
            for (var orderIndex = 0; orderIndex < candidateIndexes.Count; orderIndex++)
            {
                var candidateIndex = candidateIndexes[orderIndex];
                var candidate = candidates[candidateIndex];
                var script = BuildLightweightDockerBatchScript(candidate.Item1, candidate.Item2, password);
                string stdOut;
                string stdErr;
                var ok = ExecuteSshRemoteCommand(
                    user,
                    host,
                    password,
                    script,
                    30000,
                    out stdOut,
                    out stdErr,
                    "ssh-batch-candidate[" + candidateIndex.ToString(CultureInfo.InvariantCulture) + "]");

                LightweightDockerData parsed;
                var parseError = string.Empty;
                if (ok && TryParseLightweightDockerBatchOutput(contextName, stdOut, out parsed, out parseError))
                {
                    _preferredSshDockerCandidateIndex[targetIdentity] = candidateIndex;
                    _lastSshErrorByTarget.Remove(targetIdentity);
                    data = parsed;
                    return true;
                }

                bestError = !string.IsNullOrWhiteSpace(parseError)
                    ? parseError
                    : (!string.IsNullOrWhiteSpace(stdErr) ? stdErr.Trim() : (stdOut ?? string.Empty).Trim());
            }

            if (string.IsNullOrWhiteSpace(bestError))
            {
                bestError = "轻量批量读取失败（无可用输出）。";
            }
            _lastSshErrorByTarget[targetIdentity] = bestError;
            Debug.WriteLine("Lightweight SSH batch failed: " + targetIdentity + " | " + bestError);
            return false;
        }

        private List<int> BuildSshCandidateOrder(string targetIdentity, int candidateCount)
        {
            var result = new List<int>();
            int preferredIndex;
            if (_preferredSshDockerCandidateIndex.TryGetValue(targetIdentity, out preferredIndex) &&
                preferredIndex >= 0 &&
                preferredIndex < candidateCount)
            {
                result.Add(preferredIndex);
            }

            for (var i = 0; i < candidateCount; i++)
            {
                if (!result.Contains(i))
                {
                    result.Add(i);
                }
            }
            return result;
        }

        private static string BuildLightweightDockerBatchScript(string dockerExecutable, bool useSudo, string password)
        {
            var dockerFunction = useSudo
                ? string.Format(
                    "docker_cmd() {{ printf '%s\\n' '{0}' | sudo -S -p '' {1} \"$@\"; }}; ",
                    EscapeShellSingleQuoted(password),
                    dockerExecutable)
                : string.Format("docker_cmd() {{ {0} \"$@\"; }}; ", dockerExecutable);

            return "set -e; export LC_ALL=C; " + dockerFunction +
                   "printf '%s\\n' '" + LightweightImagesBeginMarker + "'; " +
                   "docker_cmd image ls --no-trunc --format '{{.ID}}|{{.Repository}}|{{.Tag}}|{{.CreatedAt}}|{{.Size}}'; " +
                   "printf '%s\\n' '" + LightweightImagesEndMarker + "' '" + LightweightStatsBeginMarker + "'; " +
                   "docker_cmd stats --no-stream --no-trunc --format '{{.ID}}|{{.CPUPerc}}|{{.MemUsage}}'; " +
                   "printf '%s\\n' '" + LightweightStatsEndMarker + "' '" + LightweightInfoBeginMarker + "'; " +
                   "info_line=$(docker_cmd info --format '{{.NCPU}}|{{.MemTotal}}|{{.DockerRootDir}}'); " +
                   "printf '%s\\n' \"$info_line\"; " +
                   "printf '%s\\n' '" + LightweightInfoEndMarker + "' '" + LightweightDiskBeginMarker + "'; " +
                   "docker_root=$(printf '%s\\n' \"$info_line\" | awk -F'|' 'NR==1 {print $3}'); " +
                   "test -n \"$docker_root\"; " +
                   "df -P \"$docker_root\" | awk 'NR==2 {gsub(\"%\",\"\",$5); print $5}'; " +
                   "printf '%s\\n' '" + LightweightDiskEndMarker + "' '" + LightweightCompleteMarker + "'";
        }

        private bool TryParseLightweightDockerBatchOutput(
            string contextName,
            string output,
            out LightweightDockerData data,
            out string error)
        {
            data = null;
            error = string.Empty;
            string imagesSection;
            string statsSection;
            string infoSection;
            string diskSection;
            if (!TryExtractMarkedSection(output, LightweightImagesBeginMarker, LightweightImagesEndMarker, out imagesSection) ||
                !TryExtractMarkedSection(output, LightweightStatsBeginMarker, LightweightStatsEndMarker, out statsSection) ||
                !TryExtractMarkedSection(output, LightweightInfoBeginMarker, LightweightInfoEndMarker, out infoSection) ||
                !TryExtractMarkedSection(output, LightweightDiskBeginMarker, LightweightDiskEndMarker, out diskSection) ||
                !ContainsUniqueMarker(output, LightweightCompleteMarker))
            {
                error = "轻量批量读取返回的分段标记不完整。";
                return false;
            }

            var infoLine = infoSection.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            var infoParts = (infoLine ?? string.Empty).Split('|');
            double hostCpuCores;
            long hostMemoryTotalBytes;
            if (infoParts.Length < 3 ||
                !double.TryParse(infoParts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out hostCpuCores) ||
                hostCpuCores <= 0 ||
                !long.TryParse(infoParts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out hostMemoryTotalBytes) ||
                hostMemoryTotalBytes <= 0 ||
                string.IsNullOrWhiteSpace(infoParts[2]))
            {
                error = "轻量批量读取返回的主机容量或 DockerRootDir 无效。";
                return false;
            }

            double diskUsagePercent;
            var diskLine = diskSection.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (!TryParseUsagePercent(diskLine, out diskUsagePercent))
            {
                error = "轻量批量读取返回的 Docker 磁盘使用率无效。";
                return false;
            }

            var statsLines = statsSection.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var totalCpuPercent = 0d;
            var totalMemoryUsedBytes = 0d;
            for (var i = 0; i < statsLines.Length; i++)
            {
                var parts = statsLines[i].Split('|');
                if (parts.Length < 3 || string.IsNullOrWhiteSpace(parts[0]))
                {
                    error = "轻量批量读取返回的容器汇总记录格式无效。";
                    return false;
                }
                totalCpuPercent += ParsePercent(parts[1]);
                totalMemoryUsedBytes += ExtractMemoryUsedBytes(parts[2]);
            }

            var key = (contextName ?? string.Empty).Trim();
            _hostCpuLimitTextByContext[key] = hostCpuCores.ToString("0.##", CultureInfo.InvariantCulture);
            _hostMemoryLimitMbByContext[key] = (int)Math.Max(
                1,
                Math.Min(int.MaxValue, hostMemoryTotalBytes / (1024L * 1024L)));

            data = new LightweightDockerData(
                ParseDockerImages(imagesSection),
                statsLines.Length,
                Math.Max(0, Math.Min(100, totalCpuPercent / hostCpuCores)),
                Math.Max(0, Math.Min(100, totalMemoryUsedBytes * 100d / hostMemoryTotalBytes)),
                diskUsagePercent);
            return true;
        }

        private static bool TryExtractMarkedSection(string output, string beginMarker, string endMarker, out string section)
        {
            section = string.Empty;
            var lines = (output ?? string.Empty).Replace("\r", string.Empty).Split('\n');
            var beginIndexes = Enumerable.Range(0, lines.Length).Where(i => string.Equals(lines[i].Trim(), beginMarker, StringComparison.Ordinal)).ToList();
            var endIndexes = Enumerable.Range(0, lines.Length).Where(i => string.Equals(lines[i].Trim(), endMarker, StringComparison.Ordinal)).ToList();
            if (beginIndexes.Count != 1 || endIndexes.Count != 1 || endIndexes[0] <= beginIndexes[0])
            {
                return false;
            }

            section = string.Join("\n", lines.Skip(beginIndexes[0] + 1).Take(endIndexes[0] - beginIndexes[0] - 1));
            return true;
        }

        private static bool ContainsUniqueMarker(string output, string marker)
        {
            return (output ?? string.Empty)
                .Replace("\r", string.Empty)
                .Split('\n')
                .Count(line => string.Equals(line.Trim(), marker, StringComparison.Ordinal)) == 1;
        }

        private Dictionary<string, DockerContainerStatsInfo> ReadDockerContainerStats(string contextName)
        {
            string output;
            if (!TryRunDockerCommand("stats --no-stream --no-trunc --format \"{{.ID}}|{{.CPUPerc}}|{{.MemUsage}}|{{.MemPerc}}|{{.BlockIO}}\"", contextName, out output))
            {
                return new Dictionary<string, DockerContainerStatsInfo>(StringComparer.OrdinalIgnoreCase);
            }

            var result = new Dictionary<string, DockerContainerStatsInfo>(StringComparer.OrdinalIgnoreCase);
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < lines.Length; i++)
            {
                var parts = lines[i].Split('|');
                if (parts.Length < 5)
                {
                    continue;
                }

                var id = (parts[0] ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                result[id] = new DockerContainerStatsInfo(
                    ParsePercent(parts[1]),
                    NormalizePercentText(parts[1]),
                    string.IsNullOrWhiteSpace(parts[2]) ? "0B / 0B" : parts[2].Trim(),
                    NormalizePercentText(parts[3]),
                    string.IsNullOrWhiteSpace(parts[4]) ? "0B / 0B" : parts[4].Trim());
            }

            return result;
        }

        private DockerContainerStatsInfo ReadSingleContainerStatsInfo(string contextName, string containerId)
        {
            if (string.IsNullOrWhiteSpace(containerId))
            {
                return null;
            }

            string output;
            var cmd = string.Format("stats --no-stream --no-trunc --format \"{{{{.ID}}}}|{{{{.CPUPerc}}}}|{{{{.MemUsage}}}}|{{{{.MemPerc}}}}|{{{{.BlockIO}}}}\" \"{0}\"", containerId);
            if (!TryRunDockerCommand(cmd, contextName, out output))
            {
                return null;
            }

            var lines = (output ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
            {
                return null;
            }

            var parts = lines[0].Split('|');
            if (parts.Length < 5)
            {
                return null;
            }

            return new DockerContainerStatsInfo(
                ParsePercent(parts[1]),
                NormalizePercentText(parts[1]),
                string.IsNullOrWhiteSpace(parts[2]) ? "0B / 0B" : parts[2].Trim(),
                NormalizePercentText(parts[3]),
                string.IsNullOrWhiteSpace(parts[4]) ? "0B / 0B" : parts[4].Trim());
        }

        private string ReadContainerPorts(string contextName, string containerId)
        {
            if (string.IsNullOrWhiteSpace(containerId))
            {
                return string.Empty;
            }

            string bindingsOutput;
            var bindingsCommand = string.Format("container inspect \"{0}\" --format \"{{{{json .HostConfig.PortBindings}}}}\"", containerId);
            TryRunDockerCommand(bindingsCommand, contextName, out bindingsOutput);

            string exposedOutput;
            var exposedCommand = string.Format("container inspect \"{0}\" --format \"{{{{json .Config.ExposedPorts}}}}\"", containerId);
            TryRunDockerCommand(exposedCommand, contextName, out exposedOutput);

            var ports = new List<string>();

            if (!string.IsNullOrWhiteSpace(bindingsOutput) && bindingsOutput.Trim() != "null" && bindingsOutput.Trim() != "{}")
            {
                var keyMatches = System.Text.RegularExpressions.Regex.Matches(bindingsOutput, "\"([0-9]+/(tcp|udp))\"\\s*:\\s*\\[(.*?)\\]");
                for (var i = 0; i < keyMatches.Count; i++)
                {
                    var containerPort = keyMatches[i].Groups[1].Value;
                    var block = keyMatches[i].Groups[3].Value;
                    var hostPortMatches = System.Text.RegularExpressions.Regex.Matches(block, "\"HostPort\"\\s*:\\s*\"([0-9]*)\"");
                    if (hostPortMatches.Count == 0)
                    {
                        ports.Add(":" + containerPort);
                        continue;
                    }

                    for (var j = 0; j < hostPortMatches.Count; j++)
                    {
                        var hp = hostPortMatches[j].Groups[1].Value;
                        if (string.IsNullOrWhiteSpace(hp))
                        {
                            hp = "0";
                        }

                        ports.Add(hp + ":" + containerPort);
                    }
                }
            }

            if (ports.Count == 0 && !string.IsNullOrWhiteSpace(exposedOutput) && exposedOutput.Trim() != "null" && exposedOutput.Trim() != "{}")
            {
                var matches = System.Text.RegularExpressions.Regex.Matches(exposedOutput, "\"([0-9]+/(tcp|udp))\"");
                for (var i = 0; i < matches.Count; i++)
                {
                    ports.Add(":" + matches[i].Groups[1].Value);
                }
            }

            return string.Join(", ", ports.Distinct(StringComparer.OrdinalIgnoreCase));
        }

        private DockerContainerLimitInfo ReadContainerLimitInfo(string contextName, string containerId)
        {
            if (string.IsNullOrWhiteSpace(containerId))
            {
                return DockerContainerLimitInfo.Empty;
            }

            string output;
            var command = string.Format("container inspect \"{0}\" --format \"{{{{.HostConfig.NanoCpus}}}}|{{{{.HostConfig.Memory}}}}|{{{{.HostConfig.CpuQuota}}}}|{{{{.HostConfig.CpuPeriod}}}}|{{{{.HostConfig.CpusetCpus}}}}\"", containerId);
            if (!TryRunDockerCommand(command, contextName, out output))
            {
                return DockerContainerLimitInfo.Empty;
            }

            var parts = (output ?? string.Empty).Trim().Split('|');
            if (parts.Length < 2)
            {
                return DockerContainerLimitInfo.Empty;
            }

            long nanoCpus;
            long memoryBytes;
            if (!long.TryParse(parts[0], out nanoCpus))
            {
                nanoCpus = 0;
            }

            if (!long.TryParse(parts[1], out memoryBytes))
            {
                memoryBytes = 0;
            }

            var cpuCoresText = "-";
            if (nanoCpus > 0)
            {
                cpuCoresText = (nanoCpus / 1000000000d).ToString("0.##", CultureInfo.InvariantCulture);
            }
            else
            {
                long cpuQuota;
                long cpuPeriod;
                if (parts.Length >= 4 &&
                    long.TryParse(parts[2], out cpuQuota) &&
                    long.TryParse(parts[3], out cpuPeriod) &&
                    cpuQuota > 0 &&
                    cpuPeriod > 0)
                {
                    cpuCoresText = (cpuQuota / (double)cpuPeriod).ToString("0.##", CultureInfo.InvariantCulture);
                }
                else if (parts.Length >= 5)
                {
                    var cpuset = (parts[4] ?? string.Empty).Trim();
                    var cpusetCount = CountCpuSetCores(cpuset);
                    if (cpusetCount > 0)
                    {
                        cpuCoresText = cpusetCount.ToString(CultureInfo.InvariantCulture);
                    }
                }
            }
            var memoryLimitText = memoryBytes <= 0 ? "-" : FormatBytes(memoryBytes);
            return new DockerContainerLimitInfo(cpuCoresText, memoryLimitText);
        }

        private string ReadHostCpuLimitText(string contextName)
        {
            var key = (contextName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                return "-";
            }

            string cached;
            if (_hostCpuLimitTextByContext.TryGetValue(key, out cached) && !string.IsNullOrWhiteSpace(cached))
            {
                return cached;
            }

            string output;
            if (!TryRunDockerCommand("info --format \"{{.NCPU}}\"", key, out output, 12000))
            {
                _hostCpuLimitTextByContext[key] = "-";
                return "-";
            }

            var text = (output ?? string.Empty).Trim();
            int ncpu;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ncpu) && ncpu > 0)
            {
                var resolved = ncpu.ToString(CultureInfo.InvariantCulture);
                _hostCpuLimitTextByContext[key] = resolved;
                return resolved;
            }

            _hostCpuLimitTextByContext[key] = "-";
            return "-";
        }

        private int ReadHostMemoryLimitMb(string contextName)
        {
            var key = (contextName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                return 0;
            }

            int cached;
            if (_hostMemoryLimitMbByContext.TryGetValue(key, out cached) && cached > 0)
            {
                return cached;
            }

            string output;
            if (!TryRunDockerCommand("info --format \"{{.MemTotal}}\"", key, out output, 12000))
            {
                return 0;
            }

            var text = (output ?? string.Empty).Trim();
            long memTotalBytes;
            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out memTotalBytes) || memTotalBytes <= 0)
            {
                return 0;
            }

            var totalMb = (int)Math.Max(1, memTotalBytes / (1024L * 1024L));
            _hostMemoryLimitMbByContext[key] = totalMb;
            return totalMb;
        }

        private bool TryReadHostResourceUsage(string contextName, out double cpuUsage, out double memoryUsage, out double diskUsage)
        {
            cpuUsage = 0;
            memoryUsage = 0;
            diskUsage = 0;

            string identity;
            if (!TryResolveSshIdentityForContext(contextName, out identity) || string.IsNullOrWhiteSpace(identity))
            {
                return false;
            }

            string user;
            string host;
            string password;
            if (!TryResolveSshTarget(identity, out user, out host, out password))
            {
                return false;
            }

            var command =
                "cpu=$(vmstat 1 2 | tail -1 | awk '{print 100-$15}'); " +
                "mem=$(free | awk '/Mem:/ {printf \"%.2f\", $3*100/$2}'); " +
                "disk=$(df -P / | awk 'NR==2 {gsub(\"%\",\"\",$5); print $5}'); " +
                "printf '%s|%s|%s\\n' \"$cpu\" \"$mem\" \"$disk\"";

            string stdOut;
            string stdErr;
            if (!ExecuteSshRemoteCommand(user, host, password, command, 8000, out stdOut, out stdErr, "ssh-host-resource"))
            {
                return false;
            }

            var line = (stdOut ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault();
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            var parts = line.Split('|');
            if (parts.Length < 3)
            {
                return false;
            }

            return TryParseUsagePercent(parts[0], out cpuUsage) &&
                   TryParseUsagePercent(parts[1], out memoryUsage) &&
                   TryParseUsagePercent(parts[2], out diskUsage);
        }

        private static bool TryParseUsagePercent(string value, out double percent)
        {
            percent = 0;
            var text = (value ?? string.Empty).Replace("%", string.Empty).Trim();
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out percent) &&
                !double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out percent))
            {
                return false;
            }

            percent = Math.Max(0, Math.Min(100, percent));
            return true;
        }

        private int ReadRunningContainerCount(string contextName)
        {
            string output;
            if (!TryRunDockerCommand("ps -q", contextName, out output, 8000))
            {
                return 0;
            }

            return (output ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Count(line => !string.IsNullOrWhiteSpace(line));
        }

        private static string BuildCpuCoresDisplayText(string containerLimitCoresText, string hostLimitCoresText)
        {
            var host = (hostLimitCoresText ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(host) || string.Equals(host, "-", StringComparison.Ordinal))
            {
                return string.IsNullOrWhiteSpace(containerLimitCoresText) ? "-" : containerLimitCoresText;
            }

            var container = (containerLimitCoresText ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(container) || string.Equals(container, "-", StringComparison.Ordinal))
            {
                return string.Format("不限(宿主机上限{0}核)", host);
            }

            return string.Format("{0}核(宿主机上限{1}核)", container, host);
        }

        private static int CountCpuSetCores(string cpusetText)
        {
            var text = (cpusetText ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }

            var count = 0;
            var parts = text.Split(',');
            for (var i = 0; i < parts.Length; i++)
            {
                var token = (parts[i] ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                var range = token.Split('-');
                int start;
                int end;
                if (range.Length == 2 &&
                    int.TryParse(range[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out start) &&
                    int.TryParse(range[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out end) &&
                    end >= start)
                {
                    count += (end - start + 1);
                }
                else
                {
                    int single;
                    if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out single))
                    {
                        count += 1;
                    }
                }
            }

            return count;
        }

        private static string MergeMemoryUsageWithLimit(string memoryUsageText, string memoryLimitText)
        {
            if (string.IsNullOrWhiteSpace(memoryLimitText) || memoryLimitText == "-")
            {
                return string.IsNullOrWhiteSpace(memoryUsageText) ? "0B / 0B" : memoryUsageText;
            }

            var current = string.IsNullOrWhiteSpace(memoryUsageText) ? "0B / 0B" : memoryUsageText;
            var slashIndex = current.IndexOf('/');
            if (slashIndex <= 0)
            {
                return "0B / " + memoryLimitText;
            }

            var used = current.Substring(0, slashIndex).Trim();
            if (string.IsNullOrWhiteSpace(used))
            {
                used = "0B";
            }

            return used + " / " + memoryLimitText;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0)
            {
                return "0B";
            }

            var units = new[] { "B", "KB", "MB", "GB", "TB" };
            var value = (double)bytes;
            var unitIndex = 0;
            while (value >= 1024 && unitIndex < units.Length - 1)
            {
                value /= 1024;
                unitIndex++;
            }

            return value.ToString(value >= 100 ? "0" : "0.##", CultureInfo.InvariantCulture) + units[unitIndex];
        }

        private double ReadDockerRootDiskUsagePercent(string contextName)
        {
            string output;
            if (!TryRunDockerCommand("info --format \"{{.DockerRootDir}}\"", contextName, out output))
            {
                return 0;
            }

            var dockerRoot = (output ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(dockerRoot))
            {
                return 0;
            }

            double usagePercent;
            if (TryReadRemotePathDiskUsagePercent(contextName, dockerRoot, out usagePercent))
            {
                return usagePercent;
            }

            if (TryReadLocalPathDiskUsagePercent(dockerRoot, out usagePercent))
            {
                return usagePercent;
            }

            return 0;
        }

        private bool TryReadRemotePathDiskUsagePercent(string contextName, string path, out double usagePercent)
        {
            usagePercent = 0;
            if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("/", StringComparison.Ordinal))
            {
                return false;
            }

            string identity;
            if (!TryResolveSshIdentityForContext(contextName, out identity) || string.IsNullOrWhiteSpace(identity))
            {
                return false;
            }

            string user;
            string host;
            string password;
            if (!TryResolveSshTarget(identity, out user, out host, out password))
            {
                return false;
            }

            var command = string.Format(
                "df -P '{0}' | awk 'NR==2 {{gsub(\"%\",\"\",$5); print $5}}'",
                EscapeShellSingleQuoted(path));

            string stdOut;
            string stdErr;
            if (!ExecuteSshRemoteCommand(user, host, password, command, 8000, out stdOut, out stdErr, "ssh-df"))
            {
                return false;
            }

            var line = (stdOut ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault();
            return TryParseUsagePercent(line, out usagePercent);
        }

        private static bool TryReadLocalPathDiskUsagePercent(string path, out double usagePercent)
        {
            usagePercent = 0;
            if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathRooted(path))
            {
                return false;
            }

            try
            {
                var driveRoot = System.IO.Path.GetPathRoot(path);
                if (string.IsNullOrWhiteSpace(driveRoot))
                {
                    return false;
                }

                var drive = new DriveInfo(driveRoot);
                if (!drive.IsReady || drive.TotalSize <= 0)
                {
                    return false;
                }

                usagePercent = Math.Max(0, Math.Min(100, (drive.TotalSize - drive.AvailableFreeSpace) * 100d / drive.TotalSize));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private double ReadRunningContainersDiskUsagePercent(string contextName, List<DockerContainerInfo> containers)
        {
            if (containers == null || containers.Count == 0)
            {
                return 0;
            }

            var runningContainerIds = containers
                .Where(c => string.Equals(c.State, "running", StringComparison.OrdinalIgnoreCase) ||
                            c.Status.IndexOf("Up", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(c => c.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (runningContainerIds.Count == 0)
            {
                return 0;
            }

            long totalWritableBytes = 0;
            for (var i = 0; i < runningContainerIds.Count; i++)
            {
                long writableBytes;
                if (TryReadContainerWritableBytes(contextName, runningContainerIds[i], out writableBytes) && writableBytes > 0)
                {
                    totalWritableBytes += writableBytes;
                }
            }

            if (totalWritableBytes <= 0)
            {
                return 0;
            }

            long driveTotalBytes;
            if (!TryReadDockerRootDriveTotalBytes(contextName, out driveTotalBytes) || driveTotalBytes <= 0)
            {
                return 0;
            }

            return Math.Max(0, Math.Min(100, totalWritableBytes * 100d / driveTotalBytes));
        }

        private static bool TryReadContainerWritableBytes(string contextName, string containerId, out long writableBytes)
        {
            writableBytes = 0;
            if (string.IsNullOrWhiteSpace(containerId))
            {
                return false;
            }

            string output;
            var command = string.Format("container inspect --size \"{0}\" --format \"{{{{.SizeRw}}}}\"", containerId);
            if (!TryRunDockerCommandStatic(command, contextName, out output))
            {
                return false;
            }

            var text = (output ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text) || text == "<nil>")
            {
                return false;
            }

            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out writableBytes);
        }

        private static bool TryReadDockerRootDriveTotalBytes(string contextName, out long driveTotalBytes)
        {
            driveTotalBytes = 0;
            string output;
            if (!TryRunDockerCommandStatic("info --format \"{{.DockerRootDir}}\"", contextName, out output))
            {
                return false;
            }

            var dockerRoot = output.Trim();
            if (string.IsNullOrWhiteSpace(dockerRoot) || !System.IO.Path.IsPathRooted(dockerRoot))
            {
                return false;
            }

            try
            {
                var driveRoot = System.IO.Path.GetPathRoot(dockerRoot);
                if (string.IsNullOrWhiteSpace(driveRoot))
                {
                    return false;
                }

                // 远端 Linux 常见为 "/var/lib/docker"，在 Windows 下会得到 "\"，
                // 不能用于 DriveInfo（会抛 ArgumentException）。
                var normalizedRoot = driveRoot.Trim();
                var isWindowsDriveRoot =
                    (normalizedRoot.Length == 3 &&
                     ((normalizedRoot[0] >= 'A' && normalizedRoot[0] <= 'Z') || (normalizedRoot[0] >= 'a' && normalizedRoot[0] <= 'z')) &&
                     normalizedRoot[1] == ':' &&
                     (normalizedRoot[2] == '\\' || normalizedRoot[2] == '/')) ||
                    (normalizedRoot.Length == 2 &&
                     ((normalizedRoot[0] >= 'A' && normalizedRoot[0] <= 'Z') || (normalizedRoot[0] >= 'a' && normalizedRoot[0] <= 'z')) &&
                     normalizedRoot[1] == ':');
                if (!isWindowsDriveRoot)
                {
                    return false;
                }

                var drive = new DriveInfo(driveRoot);
                if (!drive.IsReady || drive.TotalSize <= 0)
                {
                    return false;
                }

                driveTotalBytes = drive.TotalSize;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryRunDockerCommandStatic(string commandArgs, string contextName, out string output)
        {
            output = string.Empty;
            try
            {
                var arguments = commandArgs;
                if (!string.IsNullOrWhiteSpace(contextName))
                {
                    arguments = string.Format("--context \"{0}\" {1}", contextName, commandArgs);
                }

                var psi = new ProcessStartInfo("docker", arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psi))
                {
                    if (process == null)
                    {
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

                        string timedOutOut;
                        string timedOutErr;
                        CollectProcessStreams(stdOutTask, stdErrTask, 1500, out timedOutOut, out timedOutErr);
                        var timeoutMerged = MergeProcessStreams(timedOutOut, timedOutErr);
                        output = string.IsNullOrWhiteSpace(timeoutMerged) ? "docker 命令执行超时。" : timeoutMerged;
                        return false;
                    }

                    string stdOut;
                    string stdErr;
                    CollectProcessStreams(stdOutTask, stdErrTask, 3000, out stdOut, out stdErr);

                    if (process.ExitCode != 0)
                    {
                        Debug.WriteLine("Docker command failed: " + stdErr);
                        output = MergeProcessStreams(stdOut, stdErr);
                        return false;
                    }

                    output = stdOut;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("TryRunDockerCommandStatic failed: " + ex.Message);
                return false;
            }
        }

        private static double ComputeDiskActivityPercent(Dictionary<string, DockerContainerStatsInfo> containerStats)
        {
            if (containerStats == null || containerStats.Count == 0)
            {
                return 0;
            }

            double totalBytes = 0;
            foreach (var stat in containerStats.Values)
            {
                totalBytes += ParseBlockIoBytes(stat == null ? string.Empty : stat.BlockIoText);
            }

            var mib = totalBytes / (1024d * 1024d);
            var percent = mib * 8d;
            return Math.Max(0, Math.Min(100, percent));
        }

        private static double ParseBlockIoBytes(string blockIoText)
        {
            if (string.IsNullOrWhiteSpace(blockIoText))
            {
                return 0;
            }

            var parts = blockIoText.Split('/');
            double sum = 0;
            for (var i = 0; i < parts.Length; i++)
            {
                sum += ParseSizeToBytes(parts[i]);
            }

            return sum;
        }

        private static double CalculateContainerCpuUsagePercent(IEnumerable<DockerContainerStatsInfo> stats, string hostCpuLimitText)
        {
            if (stats == null)
            {
                return 0;
            }

            var totalContainerCpuPercent = stats.Sum(s => s == null ? 0 : s.CpuPercent);
            if (totalContainerCpuPercent <= 0)
            {
                return 0;
            }

            double hostCpuCores;
            if (!double.TryParse((hostCpuLimitText ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out hostCpuCores) ||
                hostCpuCores <= 0)
            {
                return Math.Max(0, Math.Min(100, totalContainerCpuPercent));
            }

            return Math.Max(0, Math.Min(100, totalContainerCpuPercent / hostCpuCores));
        }

        private static double CalculateContainerMemoryUsagePercent(IEnumerable<DockerContainerStatsInfo> stats, int hostMemoryTotalMb)
        {
            if (stats == null || hostMemoryTotalMb <= 0)
            {
                return 0;
            }

            var usedBytes = stats.Sum(s => ExtractMemoryUsedBytes(s == null ? string.Empty : s.MemoryUsageText));
            if (usedBytes <= 0)
            {
                return 0;
            }

            var totalBytes = hostMemoryTotalMb * 1024d * 1024d;
            return Math.Max(0, Math.Min(100, usedBytes * 100d / totalBytes));
        }

        private static double ExtractMemoryUsedBytes(string memoryUsageText)
        {
            if (string.IsNullOrWhiteSpace(memoryUsageText))
            {
                return 0;
            }

            var parts = memoryUsageText.Split('/');
            return parts.Length == 0 ? 0 : ParseSizeToBytes(parts[0]);
        }

        private static double ParseSizeToBytes(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }

            var input = text.Trim();
            var match = System.Text.RegularExpressions.Regex.Match(input, @"^([0-9]+(?:\.[0-9]+)?)\s*([a-zA-Z]+)?$");
            if (!match.Success)
            {
                return 0;
            }

            double value;
            if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            {
                return 0;
            }

            var unit = (match.Groups[2].Value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(unit))
            {
                unit = "B";
            }

            switch (unit.ToUpperInvariant())
            {
                case "B":
                    return value;
                case "KB":
                case "KIB":
                case "K":
                    return value * 1024d;
                case "MB":
                case "MIB":
                case "M":
                    return value * 1024d * 1024d;
                case "GB":
                case "GIB":
                case "G":
                    return value * 1024d * 1024d * 1024d;
                case "TB":
                case "TIB":
                case "T":
                    return value * 1024d * 1024d * 1024d * 1024d;
                default:
                    return 0;
            }
        }

        private bool TryRunDockerCommand(string commandArgs, string contextName, out string output, int timeoutMs = 12000)
        {
            output = string.Empty;
            var ok = false;
            if (!string.IsNullOrWhiteSpace(contextName) && contextName.StartsWith(SshContextPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var identity = contextName.Substring(SshContextPrefix.Length).Trim();
                ok = TryRunDockerCommandOverSsh(identity, commandArgs, out output, timeoutMs);
                return ok;
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

            var args = commandArgs;
            if (!string.IsNullOrWhiteSpace(contextName))
            {
                args = string.Format("--context \"{0}\" {1}", contextName, commandArgs);
            }

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

                        Debug.WriteLine("Docker command timeout: " + args);
                        if (!string.IsNullOrWhiteSpace(fallbackSshIdentity))
                        {
                            ok = TryRunDockerCommandOverSsh(fallbackSshIdentity, commandArgs, out output, timeoutMs);
                            return ok;
                        }

                        return false;
                    }
                    string stdOut;
                    string stdErr;
                    CollectProcessStreams(stdOutTask, stdErrTask, 3000, out stdOut, out stdErr);

                    if (process.ExitCode != 0)
                    {
                        Debug.WriteLine("Docker command failed: " + args + " | " + stdErr);
                        var merged = (stdOut ?? string.Empty).Trim();
                        var err = (stdErr ?? string.Empty).Trim();
                        if (!string.IsNullOrWhiteSpace(err))
                        {
                            merged = string.IsNullOrWhiteSpace(merged) ? err : (merged + Environment.NewLine + err);
                        }
                        output = merged;
                        if (!string.IsNullOrWhiteSpace(fallbackSshIdentity))
                        {
                            ok = TryRunDockerCommandOverSsh(fallbackSshIdentity, commandArgs, out output, timeoutMs);
                            return ok;
                        }

                        return false;
                    }

                    output = stdOut;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Docker command exception: " + ex.Message);
                if (!string.IsNullOrWhiteSpace(fallbackSshIdentity))
                {
                    ok = TryRunDockerCommandOverSsh(fallbackSshIdentity, commandArgs, out output, timeoutMs);
                    return ok;
                }

                return false;
            }
        }

        private async Task<DockerRealtimeRunResult> RunDockerCommandWithProgressAsync(string commandArgs, string contextName, int timeoutMs, Action<string> onLine)
        {
            if (!string.IsNullOrWhiteSpace(contextName) && contextName.StartsWith(SshContextPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var sshOut = string.Empty;
                var sshTask = Task.Run(() => TryRunDockerCommand(commandArgs, contextName, out sshOut, timeoutMs));
                var completed = await Task.WhenAny(sshTask, Task.Delay(timeoutMs));
                if (completed != sshTask)
                {
                    return new DockerRealtimeRunResult(false, "SSH 执行超时。", true);
                }

                var sshOk = await sshTask;
                if (!string.IsNullOrWhiteSpace(sshOut))
                {
                    var lines = sshOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    for (var i = 0; i < lines.Length; i++)
                    {
                        onLine(lines[i]);
                    }
                }

                return new DockerRealtimeRunResult(sshOk, sshOut, false);
            }

            var args = commandArgs;
            if (!string.IsNullOrWhiteSpace(contextName))
            {
                args = string.Format("--context \"{0}\" {1}", contextName, commandArgs);
            }

            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var buffer = new StringBuilder();
            try
            {
                using (var process = new Process())
                {
                    process.StartInfo = psi;
                    process.OutputDataReceived += (_, e) =>
                    {
                        if (string.IsNullOrWhiteSpace(e.Data))
                        {
                            return;
                        }

                        lock (buffer)
                        {
                            buffer.AppendLine(e.Data);
                        }
                        onLine(e.Data);
                    };
                    process.ErrorDataReceived += (_, e) =>
                    {
                        if (string.IsNullOrWhiteSpace(e.Data))
                        {
                            return;
                        }

                        lock (buffer)
                        {
                            buffer.AppendLine(e.Data);
                        }
                        onLine(e.Data);
                    };

                    if (!process.Start())
                    {
                        return new DockerRealtimeRunResult(false, string.Empty, false);
                    }

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    var exitedTask = Task.Run(() => process.WaitForExit());
                    var completed = await Task.WhenAny(exitedTask, Task.Delay(timeoutMs));
                    if (completed != exitedTask)
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                        }

                        return new DockerRealtimeRunResult(false, "命令执行超时。", true);
                    }

                    await exitedTask;
                    var output = buffer.ToString().Trim();
                    return new DockerRealtimeRunResult(process.ExitCode == 0, output, false);
                }
            }
            catch (Exception ex)
            {
                return new DockerRealtimeRunResult(false, ex.Message, false);
            }
        }

        private static bool ShouldShowComposeProgressLine(string line)
        {
            var text = (line ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var lowered = text.ToLowerInvariant();
            return lowered.Contains("creating") ||
                   lowered.Contains("created") ||
                   lowered.Contains("starting") ||
                   lowered.Contains("started") ||
                   lowered.Contains("running") ||
                   lowered.Contains("recreating") ||
                   lowered.Contains("pulling") ||
                   lowered.Contains("pulled") ||
                   lowered.Contains("network") ||
                   lowered.Contains("volume") ||
                   lowered.Contains("container") ||
                   lowered.Contains("error") ||
                   lowered.Contains("fail") ||
                   lowered.Contains("up-to-date");
        }

        private sealed class DockerRealtimeRunResult
        {
            public DockerRealtimeRunResult(bool ok, string output, bool timedOut)
            {
                Ok = ok;
                Output = output ?? string.Empty;
                TimedOut = timedOut;
            }

            public bool Ok { get; }

            public string Output { get; }

            public bool TimedOut { get; }
        }

        private bool TryRunDockerCommandOverSsh(string targetIdentity, string commandArgs, out string output, int timeoutMs)
        {
            output = string.Empty;
            string user;
            string host;
            string password;
            if (!TryResolveSshTarget(targetIdentity, out user, out host, out password))
            {
                return false;
            }

            // SSH 模式允许长任务（如 image load）使用调用方传入的更大超时。
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
            var bestErrorText = string.Empty;
            var isLongRunningCommand = IsLongRunningDockerCommand(commandArgs);
            var candidateIndexes = new List<int>();
            int preferredIndex;
            var hasPreferred = _preferredSshDockerCandidateIndex.TryGetValue(identity, out preferredIndex) &&
                               preferredIndex >= 0 &&
                               preferredIndex < candidates.Count;
            if (hasPreferred)
            {
                candidateIndexes.Add(preferredIndex);
            }
            else if (isLongRunningCommand)
            {
                // 对 load/save/pull 等长命令，避免多候选串行重试导致长时间卡住界面。
                candidateIndexes.Add(0);
            }
            else
            {
                for (var idx = 0; idx < candidates.Count; idx++)
                {
                    candidateIndexes.Add(idx);
                }
            }

            if (hasPreferred && !isLongRunningCommand)
            {
                for (var idx = 0; idx < candidates.Count; idx++)
                {
                    if (idx != preferredIndex)
                    {
                        candidateIndexes.Add(idx);
                    }
                }
            }

            for (var k = 0; k < candidateIndexes.Count; k++)
            {
                var i = candidateIndexes[k];
                string stdOut;
                string stdErr;
                var ok = ExecuteSshRemoteCommand(
                    user,
                    host,
                    password,
                    candidates[i],
                    effectiveTimeoutMs,
                    out stdOut,
                    out stdErr,
                    "ssh-docker-candidate[" + i.ToString(CultureInfo.InvariantCulture) + "]");
                if (ok)
                {
                    output = stdOut;
                    _lastSshErrorByTarget.Remove(identity);
                    _preferredSshDockerCandidateIndex[identity] = i;
                    return true;
                }

                var err = (stdErr ?? string.Empty).Trim();
                var outText = (stdOut ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(err))
                {
                    bestErrorText = err;
                }
                else if (!string.IsNullOrWhiteSpace(outText))
                {
                    bestErrorText = outText;
                }
            }

            if (string.IsNullOrWhiteSpace(bestErrorText))
            {
                bestErrorText = "远端命令执行失败（无 stdout/stderr 输出）。";
            }

            _lastSshErrorByTarget[identity] = bestErrorText;
            Debug.WriteLine("SSH docker command failed after retries: " + identity + " | " + commandArgs + " | " + bestErrorText);
            return false;
        }

        private void BeginReadTimingCollection()
        {
            lock (_readTimingLock)
            {
                _readTimingRecords.Clear();
                _readWallClock = Stopwatch.StartNew();
                _isCollectingReadTimings = true;
            }
        }

        private void EndReadTimingCollection()
        {
            lock (_readTimingLock)
            {
                _isCollectingReadTimings = false;
                if (_readWallClock != null && _readWallClock.IsRunning)
                {
                    _readWallClock.Stop();
                }
            }
        }

        private void AppendReadTiming(string route, string contextName, string command, long elapsedMs, bool ok)
        {
            lock (_readTimingLock)
            {
                if (!_isCollectingReadTimings)
                {
                    return;
                }

                _readTimingRecords.Add(new ReadTimingRecord(
                    route,
                    contextName,
                    command,
                    elapsedMs,
                    ok));
            }
        }

        private string BuildReadTimingSummary()
        {
            List<ReadTimingRecord> snapshot;
            long wallClockMs;
            lock (_readTimingLock)
            {
                snapshot = _readTimingRecords.ToList();
                wallClockMs = _readWallClock == null ? 0 : _readWallClock.ElapsedMilliseconds;
            }

            var totalMs = snapshot.Sum(r => r.ElapsedMs);
            var successCount = snapshot.Count(r => r.Success);
            var failedCount = snapshot.Count - successCount;
            var top = snapshot
                .OrderByDescending(r => r.ElapsedMs)
                .Take(8)
                .ToList();

            var lines = new List<string>
            {
                string.Format("真实墙钟总耗时: {0} ms", wallClockMs),
                string.Format("实际远程命令数: {0}（成功 {1}，失败 {2}）", snapshot.Count, successCount, failedCount),
                string.Format("远程命令累计耗时: {0} ms", totalMs),
                "最慢命令 Top 8:"
            };

            if (top.Count == 0)
            {
                lines.Add("无 SSH 命令记录。");
            }

            for (var i = 0; i < top.Count; i++)
            {
                var r = top[i];
                var cmd = (r.Command ?? string.Empty).Trim();
                if (cmd.Length > 72)
                {
                    cmd = cmd.Substring(0, 72) + "...";
                }

                lines.Add(string.Format(
                    "{0}. [{1}] {2} ms | {3} | {4} | {5}",
                    i + 1,
                    r.Success ? "OK" : "FAIL",
                    r.ElapsedMs,
                    string.IsNullOrWhiteSpace(r.Route) ? "-" : r.Route,
                    string.IsNullOrWhiteSpace(r.ContextName) ? "-" : r.ContextName,
                    cmd));
            }

            return string.Join("\n", lines);
        }

        private sealed class ReadTimingRecord
        {
            public ReadTimingRecord(string route, string contextName, string command, long elapsedMs, bool success)
            {
                Route = route ?? string.Empty;
                ContextName = contextName ?? string.Empty;
                Command = command ?? string.Empty;
                ElapsedMs = elapsedMs;
                Success = success;
            }

            public string Route { get; }
            public string ContextName { get; }
            public string Command { get; }
            public long ElapsedMs { get; }
            public bool Success { get; }
        }

        private static bool IsLongRunningDockerCommand(string commandArgs)
        {
            var cmd = (commandArgs ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(cmd))
            {
                return false;
            }

            return cmd.StartsWith("image load ", StringComparison.OrdinalIgnoreCase) ||
                   cmd.StartsWith("image save ", StringComparison.OrdinalIgnoreCase) ||
                   cmd.StartsWith("image pull ", StringComparison.OrdinalIgnoreCase);
        }

        private bool TryResolveSshTarget(string targetIdentity, out string user, out string host, out string password)
        {
            user = string.Empty;
            host = string.Empty;
            password = string.Empty;

            var identity = (targetIdentity ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(identity))
            {
                return false;
            }

            var at = identity.IndexOf('@');
            if (at <= 0 || at >= identity.Length - 1)
            {
                return false;
            }

            user = identity.Substring(0, at).Trim();
            host = identity.Substring(at + 1).Trim();
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(host))
            {
                return false;
            }

            if (!_sshPasswordByTarget.TryGetValue(identity, out password) || string.IsNullOrWhiteSpace(password))
            {
                return false;
            }

            return true;
        }

        private string BuildSshDockerDiagnostics(string targetIdentity)
        {
            try
            {
                string user;
                string host;
                string password;
                if (!TryResolveSshTarget(targetIdentity, out user, out host, out password))
                {
                    return "无法解析 SSH 目标或密码缓存缺失。";
                }

                var checks = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("whoami", "whoami"),
                    new KeyValuePair<string, string>("docker_path", "command -v docker || which docker || echo docker_not_found"),
                    new KeyValuePair<string, string>("docker_version", "docker version --format '{{.Server.Version}}' || docker --version"),
                    new KeyValuePair<string, string>("docker_images", "docker image ls --no-trunc --format '{{.Repository}}:{{.Tag}}' | head -n 5"),
                    new KeyValuePair<string, string>("docker_ps", "docker ps -a --no-trunc --format '{{.Names}}|{{.Status}}' | head -n 5")
                };

                var sb = new StringBuilder();
                for (var i = 0; i < checks.Count; i++)
                {
                    var check = checks[i];
                    string stdOut;
                    string stdErr;
                    var ok = ExecuteSshRemoteCommand(user, host, password, check.Value, 20000, out stdOut, out stdErr);
                    var merged = (stdOut ?? string.Empty).Trim();
                    var errText = (stdErr ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(errText))
                    {
                        merged = string.IsNullOrWhiteSpace(merged) ? errText : (merged + " | " + errText);
                    }

                    if (merged.Length > 320)
                    {
                        merged = merged.Substring(0, 320) + "...";
                    }

                    if (string.IsNullOrWhiteSpace(merged))
                    {
                        merged = "无输出";
                    }

                    sb.AppendLine(string.Format("[{0}] {1}: {2}", check.Key, ok ? "OK" : "FAIL", merged));
                }

                return sb.ToString().Trim();
            }
            catch (Exception ex)
            {
                return "诊断执行异常: " + ex.Message;
            }
        }

        private static string EscapeShellSingleQuoted(string value)
        {
            return (value ?? string.Empty).Replace("'", "'\"'\"'");
        }

        private bool ExecuteSshRemoteCommand(
            string user,
            string host,
            string sshPassword,
            string remoteCommandRaw,
            int timeoutMs,
            out string stdOut,
            out string stdErr,
            string timingRoute = "ssh")
        {
            stdOut = string.Empty;
            stdErr = string.Empty;

            var timingContext = BuildSshTargetIdentity(user, host);
            var timingCommand = SanitizeReadTimingCommand(remoteCommandRaw, sshPassword);
            var timingStopwatch = Stopwatch.StartNew();
            var remoteCommand = (remoteCommandRaw ?? string.Empty).Replace("\"", "\\\"");
            var sshExecutable = ResolveSshExecutablePath();
            if (string.IsNullOrWhiteSpace(sshExecutable))
            {
                stdErr = "未找到可用的 ssh.exe。请安装 Windows OpenSSH 客户端。";
                return false;
            }
            var askPassFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ssh_askpass_" + Guid.NewGuid().ToString("N") + ".cmd");
            try
            {
                File.WriteAllText(askPassFile, "@echo off\r\necho " + sshPassword + "\r\n", new System.Text.UTF8Encoding(false));
                var commonArgs = string.Format(
                    "-o BatchMode=no -o PreferredAuthentications=password,keyboard-interactive -o PubkeyAuthentication=no -o NumberOfPasswordPrompts=1 -o StrictHostKeyChecking=no -o UserKnownHostsFile=NUL {0}@{1} \"{2}\"",
                    user,
                    host,
                    remoteCommand);

                // First try: SSH_ASKPASS mode
                var psi = new ProcessStartInfo
                {
                    FileName = sshExecutable,
                    Arguments = commonArgs,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.EnvironmentVariables["SSH_ASKPASS"] = askPassFile;
                psi.EnvironmentVariables["SSH_ASKPASS_REQUIRE"] = "force";
                psi.EnvironmentVariables["DISPLAY"] = "codex";

                using (var process = Process.Start(psi))
                {
                    if (process == null)
                    {
                        stdErr = "无法启动 ssh 进程（ASKPASS）。";
                        var fallbackOk = TryExecuteSshWithStdinPassword(
                            sshExecutable,
                            commonArgs,
                            sshPassword,
                            timeoutMs,
                            out stdOut,
                            out stdErr);
                        timingStopwatch.Stop();
                        AppendReadTiming(timingRoute + "-stdin-fallback", timingContext, timingCommand, timingStopwatch.ElapsedMilliseconds, fallbackOk);
                        return fallbackOk;
                    }

                    var stdOutTask = process.StandardOutput.ReadToEndAsync();
                    var stdErrTask = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(timeoutMs))
                    {
                        try { process.Kill(); } catch { }
                        Debug.WriteLine("SSH remote command timeout: " + user + "@" + host + " | " + timingCommand);
                        stdErr = "SSH 执行超时（ASKPASS 模式）。";
                        timingStopwatch.Stop();
                        AppendReadTiming(timingRoute + "-askpass", timingContext, timingCommand, timingStopwatch.ElapsedMilliseconds, false);
                        return false;
                    }

                    CollectProcessStreams(stdOutTask, stdErrTask, 3000, out stdOut, out stdErr);

                    if (process.ExitCode != 0)
                    {
                        // fallback to stdin password mode
                        var fallbackOk = TryExecuteSshWithStdinPassword(
                            sshExecutable,
                            commonArgs,
                            sshPassword,
                            timeoutMs,
                            out stdOut,
                            out stdErr);
                        timingStopwatch.Stop();
                        AppendReadTiming(timingRoute + "-askpass+stdin", timingContext, timingCommand, timingStopwatch.ElapsedMilliseconds, fallbackOk);
                        return fallbackOk;
                    }

                    timingStopwatch.Stop();
                    AppendReadTiming(timingRoute + "-askpass", timingContext, timingCommand, timingStopwatch.ElapsedMilliseconds, true);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("SSH remote command exception: " + ex.Message);
                stdErr = "SSH 执行异常: " + ex.Message;
                var fallbackOk = TryExecuteSshWithStdinPassword(
                    sshExecutable,
                    string.Format(
                        "-o BatchMode=no -o PreferredAuthentications=password,keyboard-interactive -o PubkeyAuthentication=no -o NumberOfPasswordPrompts=1 -o StrictHostKeyChecking=no -o UserKnownHostsFile=NUL {0}@{1} \"{2}\"",
                        user,
                        host,
                        remoteCommand),
                    sshPassword,
                    timeoutMs,
                    out stdOut,
                    out stdErr);
                timingStopwatch.Stop();
                AppendReadTiming(timingRoute + "-stdin-fallback", timingContext, timingCommand, timingStopwatch.ElapsedMilliseconds, fallbackOk);
                return fallbackOk;
            }
            finally
            {
                TryDeleteFileQuietly(askPassFile);
            }
        }

        private static string ResolveSshExecutablePath()
        {
            var candidates = new List<string>();
            var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrWhiteSpace(windir))
            {
                candidates.Add(System.IO.Path.Combine(windir, "System32", "OpenSSH", "ssh.exe"));
                candidates.Add(System.IO.Path.Combine(windir, "Sysnative", "OpenSSH", "ssh.exe"));
            }

            candidates.Add(@"C:\Program Files\Git\usr\bin\ssh.exe");
            candidates.Add(@"C:\Program Files\Git\bin\ssh.exe");

            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                try
                {
                    if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                }
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "where",
                    Arguments = "ssh",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var process = Process.Start(psi))
                {
                    if (process != null)
                    {
                        var stdOut = process.StandardOutput.ReadToEnd();
                        process.WaitForExit(3000);
                        var first = (stdOut ?? string.Empty)
                            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                            .FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(first) && File.Exists(first.Trim()))
                        {
                            return first.Trim();
                        }
                    }
                }
            }
            catch
            {
            }

            return "ssh";
        }

        private bool TryExecuteSshWithStdinPassword(
            string sshExecutable,
            string commonArgs,
            string sshPassword,
            int timeoutMs,
            out string stdOut,
            out string stdErr)
        {
            stdOut = string.Empty;
            stdErr = string.Empty;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = string.IsNullOrWhiteSpace(sshExecutable) ? "ssh" : sshExecutable,
                    Arguments = commonArgs,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psi))
                {
                    if (process == null)
                    {
                        stdErr = "无法启动 ssh 进程（STDIN）。";
                        return false;
                    }

                    process.StandardInput.WriteLine(sshPassword ?? string.Empty);
                    process.StandardInput.Flush();
                    process.StandardInput.Close();

                    var stdOutTask = process.StandardOutput.ReadToEndAsync();
                    var stdErrTask = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(timeoutMs))
                    {
                        try { process.Kill(); } catch { }
                        stdErr = string.IsNullOrWhiteSpace(stdErr) ? "SSH 执行超时（STDIN 模式）。" : stdErr;
                        return false;
                    }

                    CollectProcessStreams(stdOutTask, stdErrTask, 3000, out stdOut, out stdErr);

                    var ok = process.ExitCode == 0;
                    return ok;
                }
            }
            catch (Exception ex)
            {
                stdErr = "SSH STDIN 模式异常: " + ex.Message;
                return false;
            }
        }

        private static string SanitizeReadTimingCommand(string command, string password)
        {
            var value = command ?? string.Empty;
            if (value.IndexOf(LightweightImagesBeginMarker, StringComparison.Ordinal) >= 0 ||
                value.IndexOf(LightweightCompleteMarker, StringComparison.Ordinal) >= 0)
            {
                return "lightweight-batch";
            }

            if (!string.IsNullOrEmpty(password))
            {
                value = value.Replace(password, "***");
                value = value.Replace(EscapeShellSingleQuoted(password), "***");
            }
            return value;
        }

        private async Task RefreshDeviceImageRowsFastAsync(string selectedDeviceName)
        {
            if (!_hasReadDeviceInfo || _devices.Count == 0)
            {
                return;
            }

            var targets = _devices
                .Where(d => d != null && !string.IsNullOrWhiteSpace(d.Name))
                .Where(d => string.IsNullOrWhiteSpace(selectedDeviceName) || string.Equals(d.Name, selectedDeviceName, StringComparison.OrdinalIgnoreCase))
                .Select(d =>
                {
                    string contextName;
                    if (!_deviceContextMap.TryGetValue(d.Name, out contextName))
                    {
                        contextName = d.ContextName;
                    }

                    contextName = ResolveFastOperationContext((contextName ?? string.Empty).Trim());
                    return new
                    {
                        Device = d,
                        ContextName = contextName
                    };
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.ContextName))
                .ToList();

            if (targets.Count == 0)
            {
                return;
            }

            var refreshedRows = await Task.Run(() =>
            {
                var rows = new List<DeviceImageRow>();
                for (var i = 0; i < targets.Count; i++)
                {
                    var target = targets[i];
                    var images = ReadDockerImages(target.ContextName);
                    for (var j = 0; j < images.Count; j++)
                    {
                        var img = images[j];
                        var imageChineseName = ResolveImageChineseName(
                            target.ContextName,
                            target.Device.Name,
                            target.Device.Ip,
                            img.Repository,
                            img.Tag,
                            img.Id);
                        rows.Add(new DeviceImageRow(
                            target.Device.Ip,
                            img.Repository,
                            img.Tag,
                            img.CreatedAt,
                            img.Size,
                            "-",
                            ShortId(img.Id, 24),
                            imageChineseName,
                            target.Device.Name,
                            target.ContextName));
                    }
                }

                return rows;
            });

            if (!string.IsNullOrWhiteSpace(selectedDeviceName))
            {
                _allImageRows.RemoveAll(r => string.Equals(r.DeviceName, selectedDeviceName, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                _allImageRows.Clear();
            }

            _allImageRows.AddRange(refreshedRows);

            var imageCountByDevice = _allImageRows
                .GroupBy(r => r.DeviceName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < _devices.Count; i++)
            {
                var d = _devices[i];
                int newImageCount;
                imageCountByDevice.TryGetValue(d.Name ?? string.Empty, out newImageCount);
                _devices[i] = new DeviceInfo(
                    d.Name,
                    d.Ip,
                    d.CpuUsage,
                    d.MemoryUsage,
                    d.DiskUsage,
                    newImageCount,
                    d.ContainerCount,
                    d.ContextName);
            }

            if (string.IsNullOrWhiteSpace(selectedDeviceName))
            {
                UpdateAllDevicesDashboard();
                BindImageRows(string.Empty, string.Empty);
            }
            else
            {
                var selectedDevice = _devices.FirstOrDefault(d => string.Equals(d.Name, selectedDeviceName, StringComparison.OrdinalIgnoreCase));
                if (selectedDevice != null)
                {
                    UpdateDeviceDashboard(selectedDevice);
                    BindImageRows(selectedDevice.Name, selectedDevice.Ip);
                }
                else
                {
                    UpdateAllDevicesDashboard();
                    BindImageRows(string.Empty, string.Empty);
                }
            }
        }

        private string ResolveFastOperationContext(string contextName)
        {
            var ctx = (contextName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(ctx))
            {
                return string.Empty;
            }

            string identity;
            if (TryResolveSshIdentityForContext(ctx, out identity) &&
                !string.IsNullOrWhiteSpace(identity) &&
                _sshPasswordByTarget.ContainsKey(identity))
            {
                return SshContextPrefix + identity;
            }

            return ctx;
        }

        private static void CollectProcessStreams(Task<string> stdOutTask, Task<string> stdErrTask, int waitMs, out string stdOut, out string stdErr)
        {
            stdOut = string.Empty;
            stdErr = string.Empty;

            try
            {
                Task.WaitAll(new Task[] { stdOutTask, stdErrTask }, Math.Max(0, waitMs));
            }
            catch
            {
            }

            if (stdOutTask != null && stdOutTask.Status == TaskStatus.RanToCompletion)
            {
                stdOut = stdOutTask.Result ?? string.Empty;
            }

            if (stdErrTask != null && stdErrTask.Status == TaskStatus.RanToCompletion)
            {
                stdErr = stdErrTask.Result ?? string.Empty;
            }
        }

        private static string MergeProcessStreams(string stdOut, string stdErr)
        {
            var outText = (stdOut ?? string.Empty).Trim();
            var errText = (stdErr ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(errText))
            {
                return outText;
            }

            return string.IsNullOrWhiteSpace(outText) ? errText : (outText + Environment.NewLine + errText);
        }

        private static string NormalizeImageId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "-";
            }

            var normalized = value.Trim();
            if (normalized.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring("sha256:".Length);
            }

            return string.IsNullOrWhiteSpace(normalized) ? "-" : normalized;
        }

        private static double ParsePercent(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return 0;
            }

            var normalized = value.Replace("%", string.Empty).Trim();
            double result;
            if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out result) &&
                !double.TryParse(normalized, NumberStyles.Float, CultureInfo.CurrentCulture, out result))
            {
                return 0;
            }

            return Math.Max(0, result);
        }

        private static string NormalizePercentText(string value)
        {
            var p = ParsePercent(value);
            return string.Format("{0:0.##}%", p);
        }

        private static string ResolveContextIp(string dockerHost)
        {
            if (string.IsNullOrWhiteSpace(dockerHost))
            {
                return GetLocalIpv4();
            }

            var host = dockerHost.Trim();
            if (host.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase) ||
                host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                host.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                host.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            {
                Uri uri;
                if (Uri.TryCreate(host, UriKind.Absolute, out uri) && !string.IsNullOrWhiteSpace(uri.Host))
                {
                    return uri.Host;
                }
            }

            if (host.StartsWith("npipe://", StringComparison.OrdinalIgnoreCase) ||
                host.StartsWith("unix://", StringComparison.OrdinalIgnoreCase))
            {
                return GetLocalIpv4();
            }

            return GetLocalIpv4();
        }

        private static string GetLocalIpv4()
        {
            try
            {
                var entry = Dns.GetHostEntry(Dns.GetHostName());
                for (var i = 0; i < entry.AddressList.Length; i++)
                {
                    var ip = entry.AddressList[i];
                    if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    {
                        return ip.ToString();
                    }
                }
            }
            catch
            {
            }

            return "127.0.0.1";
        }

        private static string BuildChineseImageName(string repository)
        {
            if (string.IsNullOrWhiteSpace(repository) || repository == "<none>")
            {
                return "未命名镜像";
            }

            var baseName = repository;
            var slashIndex = baseName.LastIndexOf('/');
            if (slashIndex >= 0 && slashIndex < baseName.Length - 1)
            {
                baseName = baseName.Substring(slashIndex + 1);
            }

            return baseName;
        }

        private static string BuildChineseContainerName(string containerName)
        {
            var normalized = (containerName ?? string.Empty).Trim().TrimStart('/');
            if (string.IsNullOrWhiteSpace(normalized))
            {
                normalized = "未命名";
            }

            return normalized;
        }

        private static string ShortId(string value, int keepLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "-";
            }

            var normalized = value.Trim();
            if (normalized.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(7);
            }

            if (normalized.Length <= keepLength)
            {
                return normalized;
            }

            return normalized.Substring(0, keepLength) + "...";
        }

        private async void ContainerDeviceSelector_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selectedDeviceName = GetSelectedContainerDeviceName();
            BindContainerRows(selectedDeviceName);

            if (_suppressContainerDeviceSelectionRefresh || !IsServiceContainerPageActive())
            {
                return;
            }

            try
            {
                await RefreshSelectedContainerDeviceAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Refresh container rows on device switch failed: " + ex.Message);
            }
        }

        private void BindContainerRows(string deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                ContainerComposeGrid.ItemsSource = _containerRows.ToList();
                return;
            }

            ContainerComposeGrid.ItemsSource = _containerRows
                .Where(row => string.Equals(row.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private async void ContainerActionButton_OnClick(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null)
            {
                return;
            }

            var lockedContainerIds = new List<string>();
            Window progressWindow = null;
            TextBlock progressText = null;
            TextBlock progressStageText = null;
            ProgressBar progressBar = null;
            try
            {
                var action = button.Content as string ?? string.Empty;

                var contextName = GetSelectedContainerContextName();
                if (string.IsNullOrWhiteSpace(contextName))
                {
                    MessageBox.Show(this, "未找到当前设备对应的 Docker 上下文。", "容器编排", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var selectedRows = ContainerComposeGrid.SelectedItems
                    .Cast<object>()
                    .Select(item => item as ContainerComposeRow)
                    .Where(row => row != null)
                    .ToList();

                var selectedRow = selectedRows.FirstOrDefault();

                var targetsSelectedContainers =
                    action == "启动容器" ||
                    action == "停止容器" ||
                    action == "删除容器" ||
                    action == "模型部署" ||
                    action == "算法部署";
                var defersValidationUntilAfterDialog = action == "创建容器" || action == "压力测试";
                if (action != "查看日志" && !defersValidationUntilAfterDialog)
                {
                    var rowsToConfirm = targetsSelectedContainers ? selectedRows : new List<ContainerComposeRow>();
                    if (!await ConfirmContainerOperationAsync(contextName, rowsToConfirm))
                    {
                        return;
                    }

                    if (targetsSelectedContainers && selectedRows.Count > 0 && !TryLockContainerRows(selectedRows, out lockedContainerIds))
                    {
                        MessageBox.Show(this, "所选容器正在执行其他操作，请稍后重试。", "容器编排", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                }

                if (action == "压力测试")
                {
                    var deviceName = GetSelectedContainerDeviceName();
                    var imageCandidates = GetDeviceImageCandidates(deviceName);
                    if (imageCandidates.Count == 0)
                    {
                        MessageBox.Show(this, "当前设备没有可用镜像，无法执行压力测试。", "容器编排", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    var request = ShowContainerStressTestDialog(imageCandidates);
                    if (request == null)
                    {
                        return;
                    }

                    if (!await ConfirmContainerOperationAsync(contextName, new List<ContainerComposeRow>()))
                    {
                        return;
                    }

                    await ExecuteContainerStressTestAsync(contextName, request);
                    return;
                }

                if (action == "创建容器")
                {
                    var selectedDeviceNameForCreate = GetSelectedContainerDeviceName();
                    var deviceName = GetSelectedContainerDeviceName();
                    var imageCandidates = GetDeviceImageCandidates(deviceName);
                    if (imageCandidates.Count == 0)
                    {
                        MessageBox.Show(this, "当前设备没有可用镜像，无法创建容器。", "容器编排", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    var request = ShowCreateContainerDialog(contextName, imageCandidates);
                    if (request == null)
                    {
                        return;
                    }

                    if (!await ConfirmContainerOperationAsync(contextName, new List<ContainerComposeRow>()))
                    {
                        return;
                    }

                    var createOut = string.Empty;
                    var createCommand = BuildCreateContainerCommand(request);
                    var resolveIdCommand = string.Format("ps -a --no-trunc --filter \"name=^{0}$\" --format \"{{{{.ID}}}}\"", request.ContainerName);
                    var inspectLimitCommand = "container inspect <containerId> --format \"{{.HostConfig.NanoCpus}}|{{.HostConfig.Memory}}|{{.HostConfig.CpuQuota}}|{{.HostConfig.CpuPeriod}}|{{.HostConfig.CpusetCpus}}\"";
                    progressStageText = new TextBlock
                    {
                        FontSize = 14,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(Color.FromRgb(36, 64, 98)),
                        Margin = new Thickness(0, 0, 0, 8),
                        Text = "阶段：准备创建参数"
                    };
                    progressBar = new ProgressBar
                    {
                        Minimum = 0,
                        Maximum = 100,
                        Height = 16,
                        Margin = new Thickness(0, 0, 0, 10),
                        Value = 20
                    };
                    progressText = new TextBlock
                    {
                        Text = string.Format("容器名：{0}\n镜像：{1}\n即将执行命令：docker {2}", request.ContainerName, request.Image, createCommand),
                        FontSize = 14,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Color.FromRgb(42, 53, 68))
                    };
                    var createPanel = new StackPanel
                    {
                        Margin = new Thickness(18, 14, 18, 14)
                    };
                    createPanel.Children.Add(progressStageText);
                    createPanel.Children.Add(progressBar);
                    createPanel.Children.Add(progressText);
                    progressWindow = new Window
                    {
                        Title = "正在创建容器",
                        Width = 620,
                        Height = 240,
                        ResizeMode = ResizeMode.NoResize,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Owner = this,
                        Content = createPanel
                    };
                    progressWindow.Show();
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    if (progressBar != null)
                    {
                        progressBar.Value = 55;
                    }
                    if (progressStageText != null)
                    {
                        progressStageText.Text = "阶段：执行创建命令";
                    }
                    SetProgressText(progressText, "正在执行命令：\ndocker " + createCommand);
                    var createOk = await Task.Run(() => TryRunDockerCommand(createCommand, contextName, out createOut));
                    if (!createOk)
                    {
                        AddContainerOperationLog(contextName, "创建容器", string.Empty, request.ContainerName, "失败", "创建命令执行失败。");
                        CloseWindowQuietly(ref progressWindow);
                        MessageBox.Show(this, "创建容器失败，请检查镜像、端口占用和 Docker 状态。", "容器编排", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    var createdContainerId = ExtractContainerId(createOut);
                    if (string.IsNullOrWhiteSpace(createdContainerId))
                    {
                        if (progressBar != null)
                        {
                            progressBar.Value = 68;
                        }
                        if (progressStageText != null)
                        {
                            progressStageText.Text = "阶段：按名称反查容器ID";
                        }
                        SetProgressText(progressText, "创建命令已返回，正在反查容器ID：\ndocker " + resolveIdCommand);
                        createdContainerId = ResolveContainerIdByName(contextName, request.ContainerName);
                    }
                    var appliedLimit = DockerContainerLimitInfo.Empty;
                    var canVerifyLimit = !string.IsNullOrWhiteSpace(createdContainerId);
                    var cassConfigNote = string.Empty;
                    if (canVerifyLimit && request.RequiresCassConfigUpdate)
                    {
                        if (progressBar != null)
                        {
                            progressBar.Value = 72;
                        }
                        if (progressStageText != null)
                        {
                            progressStageText.Text = "阶段：写入 CASS 配置";
                        }
                        SetProgressText(progressText, "正在启动容器并写入 CASS 配置文件...");
                        var cassConfigOut = string.Empty;
                        var cassConfigOk = await Task.Run(() => TryApplyCassContainerConfig(contextName, createdContainerId, request.CassMonitorPort, request.CassBootPort, out cassConfigOut));
                        cassConfigNote = cassConfigOk
                            ? string.Format("\nCASS配置: ePLC_Monitor_Port={0}, ePLC_Boot_Port/CASS_UDP_PORT__={1}", request.CassMonitorPort, request.CassBootPort)
                            : "\n警告：CASS配置写入失败：" + (string.IsNullOrWhiteSpace(cassConfigOut) ? "无输出" : cassConfigOut.Trim());
                        AddContainerOperationLog(
                            contextName,
                            "CASS配置",
                            createdContainerId,
                            request.ContainerName,
                            cassConfigOk ? "成功" : "失败",
                            cassConfigOk ? cassConfigNote.Trim() : cassConfigOut);
                    }

                    if (canVerifyLimit)
                    {
                        if (progressBar != null)
                        {
                            progressBar.Value = 76;
                        }
                        if (progressStageText != null)
                        {
                            progressStageText.Text = "阶段：校验容器CPU限制";
                        }
                        SetProgressText(progressText, "正在读取容器限制：\ndocker " + inspectLimitCommand.Replace("<containerId>", createdContainerId));
                        appliedLimit = ReadContainerLimitInfo(contextName, createdContainerId);
                    }

                    _lastOperatedContextName = contextName;
                    _lastOperatedContainerId = createdContainerId ?? string.Empty;
                    _lastOperatedContainerName = request.ContainerName;

                    var createdRow = _containerRows.FirstOrDefault(r =>
                        string.Equals(r.DeviceName, GetSelectedContainerDeviceName(), StringComparison.OrdinalIgnoreCase) &&
                        (string.Equals(r.Name, "/" + request.ContainerName, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(r.Name, request.ContainerName, StringComparison.OrdinalIgnoreCase)));
                    if (createdRow != null)
                    {
                        if (string.IsNullOrWhiteSpace(createdContainerId))
                        {
                            createdContainerId = createdRow.FullId;
                            canVerifyLimit = !string.IsNullOrWhiteSpace(createdContainerId);
                            if (canVerifyLimit)
                            {
                                appliedLimit = ReadContainerLimitInfo(contextName, createdContainerId);
                            }
                        }
                        _lastOperatedContainerId = createdRow.FullId;
                        _lastOperatedContainerName = createdRow.Name;
                    }
                    if (progressBar != null)
                    {
                        progressBar.Value = 85;
                    }
                    if (progressStageText != null)
                    {
                        progressStageText.Text = "阶段：更新当前容器";
                    }
                    SetProgressText(progressText, "命令执行完毕，正在更新当前容器行与日志...");

                    var requestedCpuText = request.CpuCores.ToString("0.##", CultureInfo.InvariantCulture);
                    var requestedMemoryText = request.MemoryLimitMb.ToString(CultureInfo.InvariantCulture) + "MB";
                    var verifyNote = string.Empty;

                    if (canVerifyLimit)
                    {
                        var cpuMatched = IsCpuLimitMatched(request.CpuCores, appliedLimit.CpuCoresText);
                        verifyNote = string.Format(
                            "\n请求: CPU {0}核, 内存 {1}\n实际: CPU {2}核",
                            requestedCpuText,
                            requestedMemoryText,
                            appliedLimit.CpuCoresText);

                        if (!cpuMatched)
                        {
                            verifyNote += "\n警告：实际CPU限制与请求值不一致，请检查 Docker 后端限制能力。";
                        }

                        verifyNote += cassConfigNote;
                    }
                    else
                    {
                        verifyNote = "\n提示：未解析到新容器ID，暂无法自动校验实际CPU限制。";
                        verifyNote += cassConfigNote;
                    }

                    CloseWindowQuietly(ref progressWindow);
                    MessageBox.Show(
                        this,
                        "创建容器成功：" + request.ContainerName + verifyNote,
                        "容器编排",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    AddContainerOperationLog(
                        contextName,
                        "创建容器",
                        _lastOperatedContainerId,
                        _lastOperatedContainerName,
                        "成功",
                        verifyNote);

                    var createdLogRow = new ContainerComposeRow(
                        selectedDeviceNameForCreate,
                        ShortContainerId(createdContainerId ?? (createdRow == null ? string.Empty : createdRow.FullId)),
                        createdRow == null ? "/" + request.ContainerName : createdRow.Name,
                        request.ContainerName,
                        createdRow == null ? request.Image : createdRow.Image,
                        createdRow == null ? "created" : createdRow.Status,
                        request.PortMappings == null || request.PortMappings.Count == 0
                            ? "-"
                            : string.Join(", ", request.PortMappings.Select(m => string.Format("{0}:{1}", m.HostPort, m.ContainerPort))),
                        request.CpuCores.ToString("0.##", CultureInfo.InvariantCulture),
                        "-",
                        request.MemoryLimitMb.ToString(CultureInfo.InvariantCulture) + "MB",
                        createdRow == null ? "-" : createdRow.MemoryPercent,
                        createdRow == null ? "-" : createdRow.DiskReadWrite,
                        string.Empty,
                        createdContainerId ?? (createdRow == null ? string.Empty : createdRow.FullId),
                        createdRow == null ? "-" : createdRow.Size);
                    if (!string.IsNullOrWhiteSpace(createdContainerId))
                    {
                        if (progressBar != null)
                        {
                            progressBar.Value = 92;
                        }
                        if (progressStageText != null)
                        {
                            progressStageText.Text = "阶段：补查新容器运行指标";
                        }
                        SetProgressText(progressText, "正在补查新容器指标：docker ps -a --size / docker stats");

                        var latestInfo = await Task.Run(() => ReadSingleContainerInfo(contextName, createdContainerId, request.ContainerName));
                        var latestStats = await Task.Run(() => ReadSingleContainerStatsInfo(contextName, createdContainerId));
                        var hostCpuLimitText = ReadHostCpuLimitText(contextName);
                        var cpuCoresText = BuildCpuCoresDisplayText(appliedLimit.CpuCoresText, hostCpuLimitText);

                        createdLogRow = new ContainerComposeRow(
                            selectedDeviceNameForCreate,
                            ShortContainerId(createdContainerId),
                            "/" + request.ContainerName,
                            request.ContainerName,
                            latestInfo == null ? request.Image : NormalizeImageNameWithTag(latestInfo.Image),
                            latestInfo == null ? "created" : (string.IsNullOrWhiteSpace(latestInfo.State) ? "created" : latestInfo.State),
                            latestInfo == null || string.IsNullOrWhiteSpace(latestInfo.Ports) ? createdLogRow.Ports : latestInfo.Ports,
                            string.IsNullOrWhiteSpace(cpuCoresText) ? request.CpuCores.ToString("0.##", CultureInfo.InvariantCulture) : cpuCoresText,
                            latestStats == null ? "-" : latestStats.CpuPercentText,
                            latestStats == null ? request.MemoryLimitMb.ToString(CultureInfo.InvariantCulture) + "MB" : latestStats.MemoryUsageText,
                            latestStats == null ? "-" : latestStats.MemoryPercentText,
                            latestStats == null ? "-" : latestStats.BlockIoText,
                            latestInfo == null ? string.Empty : latestInfo.Status,
                            createdContainerId,
                            latestInfo == null ? "-" : latestInfo.Size);
                    }
                    AppendContainerOperationCsvByDeviceName(
                        selectedDeviceNameForCreate,
                        "创建容器",
                        createdLogRow);
                    var existingIndex = _containerRows.FindIndex(r =>
                        string.Equals(r.DeviceName, selectedDeviceNameForCreate, StringComparison.OrdinalIgnoreCase) &&
                        (IsContainerMatch(r, createdContainerId) ||
                         string.Equals((r.Name ?? string.Empty).TrimStart('/'), request.ContainerName, StringComparison.OrdinalIgnoreCase)));
                    if (existingIndex >= 0)
                    {
                        _containerRows[existingIndex] = createdLogRow;
                    }
                    else
                    {
                        _containerRows.Insert(0, createdLogRow);
                    }
                    BindContainerRows(GetSelectedContainerDeviceName());
                    if (progressBar != null)
                    {
                        progressBar.Value = 100;
                    }
                    return;
                }

                if (action == "查看日志")
                {
                    var selectedDeviceNameForLogs = GetSelectedContainerDeviceName();
                    if (string.IsNullOrWhiteSpace(selectedDeviceNameForLogs))
                    {
                        MessageBox.Show(this, "请先选择设备。", "容器编排", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    var csvPath = EnsureContainerLogCsvByDeviceName(selectedDeviceNameForLogs);
                    if (string.IsNullOrWhiteSpace(csvPath) || !File.Exists(csvPath))
                    {
                        MessageBox.Show(this, "未找到该设备日志文件。", "容器编排", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    var lines = File.ReadAllLines(csvPath, Encoding.UTF8)
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .ToList();
                    var deviceIp = GetDeviceIpByName(selectedDeviceNameForLogs);
                    var title = string.Format(
                        "{0}_{1}_容器操作日志",
                        selectedDeviceNameForLogs,
                        string.IsNullOrWhiteSpace(deviceIp) ? "unknown" : deviceIp);
                    var displayRows = BuildContainerLogDisplayRows(lines, selectedDeviceNameForLogs, deviceIp);
                    ShowLogsWindow(title, displayRows);
                    return;
                }

            if (action == "模型部署")
            {
                await ExecuteRewriteJsonFileAsync();
                return;
            }

            if (action == "算法部署")
            {
                var selectedRowsForAlgorithm = ContainerComposeGrid == null
                    ? new List<ContainerComposeRow>()
                    : ContainerComposeGrid.SelectedItems
                        .Cast<object>()
                        .Select(item => item as ContainerComposeRow)
                        .Where(row => row != null)
                        .ToList();
                if (selectedRowsForAlgorithm.Count == 0)
                {
                    MessageBox.Show(this, "算法部署前请至少选中一个容器。", "容器编排", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                await ExecuteAlgorithmDeployAsync();
                return;
            }

                if (selectedRow == null)
                {
                    MessageBox.Show(this, "请先选中一行容器。", "容器编排", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (selectedRows.Count == 0)
                {
                    MessageBox.Show(this, "请至少选中一行容器。", "容器编排", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var containerIds = selectedRows
                    .Select(r => r.FullId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (containerIds.Count == 0)
                {
                    MessageBox.Show(this, "所选容器无有效 ID。", "容器编排", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var quotedIds = string.Join(" ", containerIds.Select(id => string.Format("\"{0}\"", id)));
                string dockerActionCommand;
                switch (action)
                {
                    case "启动容器":
                        dockerActionCommand = "start " + quotedIds;
                        break;
                    case "停止容器":
                        dockerActionCommand = "stop " + quotedIds;
                        break;
                    case "删除容器":
                        dockerActionCommand = "rm -f " + quotedIds;
                        break;
                    default:
                        MessageBox.Show(this, string.Format("暂不支持的操作：{0}", action), "容器编排", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                }

                string actionOut = string.Empty;
                var actionVerb = action;
                if (string.Equals(action, "启动容器", StringComparison.OrdinalIgnoreCase))
                {
                    progressStageText = new TextBlock
                    {
                        FontSize = 14,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(Color.FromRgb(36, 64, 98)),
                        Margin = new Thickness(0, 0, 0, 8),
                        Text = "阶段：准备编排文件"
                    };
                    progressBar = new ProgressBar
                    {
                        Minimum = 0,
                        Maximum = 100,
                        Height = 16,
                        Margin = new Thickness(0, 0, 0, 10),
                        Value = 20
                    };
                    progressText = new TextBlock
                    {
                        Text = string.Format("正在{0}容器中...\n目标数量：{1}", actionVerb, containerIds.Count),
                        FontSize = 14,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Color.FromRgb(42, 53, 68))
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
                        Title = "正在启动容器",
                        Width = 620,
                        Height = 240,
                        ResizeMode = ResizeMode.NoResize,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Owner = this,
                        Content = panel
                    };
                }
                else
                {
                    progressWindow = CreateProgressDialog("正在执行操作");
                    progressText = progressWindow.Content as TextBlock;
                    SetProgressText(
                        progressText,
                        string.Format("正在{0}容器中...\n目标数量：{1}", actionVerb, containerIds.Count));
                }

                progressWindow.Show();
                await Dispatcher.Yield(DispatcherPriority.Background);
                if (progressBar != null)
                {
                    progressBar.Value = 55;
                }
                if (progressStageText != null)
                {
                    progressStageText.Text = "阶段：执行启动命令";
                }
                var actionCommandTimeoutMs = string.Equals(action, "启动容器", StringComparison.OrdinalIgnoreCase) ? 60000 : 12000;
                var actionOk = await Task.Run(() => TryRunDockerCommand(dockerActionCommand, contextName, out actionOut, actionCommandTimeoutMs));
                if (!actionOk)
                {
                    for (var i = 0; i < selectedRows.Count; i++)
                    {
                        var row = selectedRows[i];
                        var id = row.FullId;
                        AddContainerOperationLog(contextName, action, id, row.Name, "失败", string.IsNullOrWhiteSpace(actionOut) ? "命令执行失败。" : actionOut.Trim());
                    }
                    CloseWindowQuietly(ref progressWindow);
                    MessageBox.Show(this, string.Format("{0}失败。", action), "容器编排", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                _lastOperatedContextName = contextName;
                _lastOperatedContainerId = containerIds[0];
                _lastOperatedContainerName = selectedRows[0].Name;

                var waitTimeoutMs = string.Equals(action, "启动容器", StringComparison.OrdinalIgnoreCase) ? 90000 : 5000;
                if (progressBar != null)
                {
                    progressBar.Value = 80;
                }
                if (progressStageText != null)
                {
                    progressStageText.Text = "阶段：确认运行状态";
                }
                if (string.Equals(action, "启动容器", StringComparison.OrdinalIgnoreCase))
                {
                    SetProgressText(progressText, "命令已提交，后台等待容器就绪...\n正在轮询容器状态（最多约90秒）");
                }
                else
                {
                    SetProgressText(progressText, string.Format("命令已执行，正在确认{0}结果...", actionVerb));
                }
                var statusStopwatch = Stopwatch.StartNew();
                var statusHeartbeat = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(500)
                };
                statusHeartbeat.Tick += (_, __) =>
                {
                    if (progressBar != null)
                    {
                        var next = progressBar.Value + 0.8;
                        progressBar.Value = next > 95 ? 80 : next;
                    }

                    if (string.Equals(action, "启动容器", StringComparison.OrdinalIgnoreCase))
                    {
                        SetProgressText(
                            progressText,
                            string.Format(
                                "命令已提交，后台等待容器就绪...\n正在轮询容器状态（最多约90秒）\n已等待：{0}s",
                                Math.Max(1, (int)statusStopwatch.Elapsed.TotalSeconds)));
                    }
                };
                statusHeartbeat.Start();
                var opVisible = await WaitForContainerRowsAsync(contextName, rows =>
                {
                    switch (action)
                    {
                        case "启动容器":
                            return containerIds.All(id => rows.Any(r =>
                                IsContainerMatch(r, id) &&
                                string.Equals(r.Status, "running", StringComparison.OrdinalIgnoreCase)));
                        case "停止容器":
                            return containerIds.All(id => rows.Any(r =>
                                IsContainerMatch(r, id) &&
                                !string.Equals(r.Status, "running", StringComparison.OrdinalIgnoreCase)));
                        case "删除容器":
                            return containerIds.All(id => rows.All(r => !IsContainerMatch(r, id)));
                        default:
                            return true;
                    }
                }, timeoutMs: waitTimeoutMs);
                statusHeartbeat.Stop();

                if (progressBar != null)
                {
                    progressBar.Value = 96;
                }
                if (progressStageText != null)
                {
                    progressStageText.Text = "阶段：刷新界面";
                }
                SetProgressText(progressText, string.Format("{0}完成，正在刷新界面...", actionVerb));
                for (var i = 0; i < selectedRows.Count; i++)
                {
                    var row = selectedRows[i];
                    var id = row.FullId;
                    AddContainerOperationLog(
                        contextName,
                        action,
                        id,
                        row.Name,
                        opVisible ? "成功" : "命令成功，待确认",
                        string.IsNullOrWhiteSpace(actionOut) ? string.Empty : actionOut.Trim());
                    AppendContainerOperationCsvByDeviceName(
                        string.IsNullOrWhiteSpace(row.DeviceName) ? GetSelectedContainerDeviceName() : row.DeviceName,
                        action,
                        BuildContainerCsvLogRow(row, action));
                }
                if (string.Equals(action, "启动容器", StringComparison.OrdinalIgnoreCase))
                {
                    CloseWindowQuietly(ref progressWindow);
                    MessageBox.Show(
                        this,
                        opVisible
                            ? string.Format("{0}成功，共处理 {1} 个容器。", action, containerIds.Count)
                            : string.Format("{0}未在预期时间内完成，请检查容器日志和镜像配置。", action),
                        "容器编排",
                        MessageBoxButton.OK,
                        opVisible ? MessageBoxImage.Information : MessageBoxImage.Warning);
                    return;
                }

                CloseWindowQuietly(ref progressWindow);
                MessageBox.Show(
                    this,
                    opVisible
                        ? string.Format("{0}成功，共处理 {1} 个容器。", action, containerIds.Count)
                        : string.Format("{0}命令执行成功，但列表刷新稍慢，请稍后查看。", action),
                    "容器编排",
                    MessageBoxButton.OK,
                    opVisible ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                CloseWindowQuietly(ref progressWindow);
                MessageBox.Show(this, "操作失败：" + ex.Message, "容器编排", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (progressWindow != null)
                {
                    try
                    {
                        progressWindow.Close();
                    }
                    catch
                    {
                    }
                }
                UnlockContainerRows(lockedContainerIds);
                UpdateContainerActionAvailability();
                RestoreMainWindowFocus();
            }
        }

        private static void CloseWindowQuietly(ref Window window)
        {
            if (window == null)
            {
                return;
            }

            try
            {
                window.Close();
            }
            catch
            {
            }
            finally
            {
                window = null;
            }
        }

        private bool IsServiceContainerPageActive()
        {
            return string.Equals(_activeSidebarKey, "ServiceContainer", StringComparison.OrdinalIgnoreCase) &&
                   ServiceContainerPanel != null &&
                   ServiceContainerPanel.Visibility == Visibility.Visible;
        }

        private Window ShowContainerLoadingWindowIfActive(string text)
        {
            if (!IsServiceContainerPageActive())
            {
                return null;
            }

            CloseContainerLoadingWindow();
            _containerLoadingWindow = CreateContainerLoadingWindow(text);
            _containerLoadingWindow.Show();
            return _containerLoadingWindow;
        }

        private void CloseContainerLoadingWindow()
        {
            if (_containerLoadingWindow == null)
            {
                return;
            }

            try
            {
                _containerLoadingWindow.Close();
            }
            catch
            {
            }
            finally
            {
                _containerLoadingWindow = null;
            }
        }

        private void RestoreMainWindowFocus()
        {
            try
            {
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.BeginInvoke(new Action(RestoreMainWindowFocus), DispatcherPriority.ApplicationIdle);
                    return;
                }
            }
            catch
            {
            }
        }

        private void SetContainerActionButtonsEnabled(bool enabled)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetContainerActionButtonsEnabled(enabled));
                return;
            }

            if (ContainerActionPanel == null)
            {
                return;
            }

            foreach (var child in ContainerActionPanel.Children)
            {
                var actionButton = child as Button;
                if (actionButton != null)
                {
                    var action = actionButton.Content as string ?? string.Empty;
                    actionButton.IsEnabled = enabled || string.Equals(action, "查看日志", StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        private List<string> GetDeviceImageCandidates(string deviceName)
        {
            return _allImageRows
                .Where(r => string.Equals(r.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                .Select(r => string.Format("{0}:{1}", r.Repository, r.Tag))
                .Where(s => !string.IsNullOrWhiteSpace(s) && !s.StartsWith("<none>", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s)
                .ToList();
        }

        private List<string> ReadImageExposedPorts(string contextName, string image)
        {
            if (string.IsNullOrWhiteSpace(image))
            {
                return new List<string>();
            }

            string output;
            var command = string.Format("image inspect \"{0}\" --format \"{{{{json .Config.ExposedPorts}}}}\"", image);
            if (!TryRunDockerCommand(command, contextName, out output))
            {
                return new List<string>();
            }

            var raw = (output ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(raw) || raw == "null" || raw == "{}")
            {
                return new List<string>();
            }

            var keys = new List<string>();
            var matches = System.Text.RegularExpressions.Regex.Matches(raw, "\"([0-9]+/(tcp|udp))\"");
            for (var i = 0; i < matches.Count; i++)
            {
                var val = matches[i].Groups[1].Value;
                if (!string.IsNullOrWhiteSpace(val))
                {
                    keys.Add(val.Trim());
                }
            }

            return keys
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool IsCassImage(string image)
        {
            var text = (image ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var slashIndex = text.LastIndexOf('/');
            if (slashIndex >= 0 && slashIndex < text.Length - 1)
            {
                text = text.Substring(slashIndex + 1);
            }

            return text.StartsWith("cass_", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsValidTcpUdpPort(string value)
        {
            int port;
            return int.TryParse((value ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port) &&
                   port > 0 &&
                   port <= 65535;
        }

        private CreateContainerRequest ShowCreateContainerDialog(string contextName, List<string> imageCandidates)
        {
            var imageCombo = new ComboBox
            {
                Width = 500,
                Height = 34,
                ItemsSource = imageCandidates
            };
            imageCombo.SelectedIndex = 0;

            var nameBox = new TextBox
            {
                Width = 500,
                Height = 34,
                Text = "created_" + DateTime.Now.ToString("yyyyMMddHHmmss")
            };

            var hostCpuLimitText = ReadHostCpuLimitText(contextName);
            var hostCpuLimitCores = 0d;
            int hostCpuInt;
            if (int.TryParse(hostCpuLimitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out hostCpuInt) && hostCpuInt > 0)
            {
                hostCpuLimitCores = hostCpuInt;
            }

            var cpuCoreOptions = BuildCpuCoreOptions(hostCpuLimitCores);
            var cpuCoreCombo = new ComboBox
            {
                Width = 500,
                Height = 34,
                ItemsSource = cpuCoreOptions
            };
            var defaultCpu = hostCpuLimitCores > 0 && hostCpuLimitCores < 2 ? "1" : "2";
            cpuCoreCombo.SelectedItem = cpuCoreOptions.Contains(defaultCpu, StringComparer.Ordinal) ? defaultCpu : cpuCoreOptions.FirstOrDefault();

            var memoryLimitBox = new TextBox
            {
                Width = 140,
                Height = 34,
                Text = "1024",
                ToolTip = "单位 MB，例如 512、1024、2048（用于限制容器运行内存）"
            };
            var hostMemoryTotalMb = ReadHostMemoryLimitMb(contextName);
            var uiMemoryUpperMb = hostMemoryTotalMb > 0
                ? (int)Math.Max(256, Math.Min(65536, Math.Floor(hostMemoryTotalMb * 0.8d)))
                : 65536;
            var memorySlider = new Slider
            {
                Width = 340,
                Minimum = 4,
                Maximum = uiMemoryUpperMb,
                TickFrequency = 4,
                IsSnapToTickEnabled = true,
                Value = 1024,
                SmallChange = 4,
                LargeChange = 256,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var memoryHintText = new TextBlock
            {
                Margin = new Thickness(0, 6, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105))
            };

            var syncingMemoryUi = false;
            Action updateMemoryHint = () =>
            {
                int memoryMb;
                if (!int.TryParse((memoryLimitBox.Text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out memoryMb))
                {
                    memoryHintText.Text = "内存限制必须是正整数，且为 4 MB 的倍数。";
                    return;
                }

                if (memoryMb <= 0 || memoryMb % 4 != 0)
                {
                    memoryHintText.Text = "内存限制必须是正整数，且为 4 MB 的倍数。";
                    return;
                }

                var gb = memoryMb / 1024d;
                memoryHintText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "当前: {0} MB ({1:0.##} GB)  |  容器运行内存上限",
                    memoryMb,
                    gb);
            };

            memorySlider.ValueChanged += (_, __) =>
            {
                if (syncingMemoryUi)
                {
                    return;
                }

                syncingMemoryUi = true;
                memoryLimitBox.Text = ((int)memorySlider.Value).ToString(CultureInfo.InvariantCulture);
                updateMemoryHint();
                syncingMemoryUi = false;
            };

            memoryLimitBox.TextChanged += (_, __) =>
            {
                if (syncingMemoryUi)
                {
                    return;
                }

                int memoryMb;
                if (!int.TryParse((memoryLimitBox.Text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out memoryMb))
                {
                    updateMemoryHint();
                    return;
                }

                if (memoryMb < 4)
                {
                    memoryMb = 4;
                }

                if (memoryMb > uiMemoryUpperMb)
                {
                    memoryMb = uiMemoryUpperMb;
                }

                memoryMb = (memoryMb / 4) * 4;

                syncingMemoryUi = true;
                memorySlider.Value = memoryMb;
                updateMemoryHint();
                syncingMemoryUi = false;
            };

            var portInputRows = new List<PortInputRow>();
            var portsPanel = new StackPanel();
            var cassPortPanel = new StackPanel
            {
                Margin = new Thickness(0, 10, 0, 0),
                Visibility = Visibility.Collapsed
            };
            var cassMonitorPortBox = new TextBox
            {
                Width = 220,
                Height = 34,
                Margin = new Thickness(0, 0, 12, 0),
                Text = "5222",
                ToolTip = "写入 EngineConfig.txt 的 ePLC_Monitor_Port"
            };
            var cassBootPortBox = new TextBox
            {
                Width = 220,
                Height = 34,
                Text = "5221",
                ToolTip = "写入 EngineConfig.txt 的 ePLC_Boot_Port 和 BootConfig.txt 的 CASS_UDP_PORT__"
            };

            Action refreshPortsUi = () =>
            {
                portsPanel.Children.Clear();
                portInputRows.Clear();

                var selectedImage = imageCombo.SelectedItem as string;
                var isCassImage = IsCassImage(selectedImage);
                cassPortPanel.Visibility = isCassImage ? Visibility.Visible : Visibility.Collapsed;
                if (isCassImage)
                {
                    var defaultCassPorts = FindAvailableUdpPorts(contextName, 3221, 3222);
                    cassMonitorPortBox.Text = defaultCassPorts.Item1;
                    cassBootPortBox.Text = defaultCassPorts.Item2;
                }

                var exposedPorts = ReadImageExposedPorts(contextName, selectedImage);
                if (exposedPorts.Count == 0)
                {
                    portsPanel.Children.Add(new TextBlock
                    {
                        Text = "该镜像未声明 ExposedPorts（可直接创建，或后续手动发布端口）。",
                        Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                        Margin = new Thickness(4, 2, 0, 2)
                    });
                    return;
                }

                for (var i = 0; i < exposedPorts.Count; i++)
                {
                    var containerPort = exposedPorts[i];
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
                    var hostPortBox = new TextBox
                    {
                        Width = 200,
                        Height = 34,
                        Margin = new Thickness(0, 0, 10, 0),
                        ToolTip = "Host port（必填，1-65535）"
                    };

                    row.Children.Add(hostPortBox);
                    row.Children.Add(new TextBlock
                    {
                        Text = ":" + containerPort,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                        FontSize = 15
                    });

                    portsPanel.Children.Add(row);
                    portInputRows.Add(new PortInputRow(containerPort, hostPortBox));
                }
            };

            imageCombo.SelectionChanged += (_, __) => refreshPortsUi();

            var panel = new StackPanel { Margin = new Thickness(18, 14, 18, 12) };
            panel.Children.Add(new TextBlock { Text = "Run a new container", FontSize = 28, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(30, 41, 59)) });
            panel.Children.Add(new TextBlock { Text = "Optional settings", Margin = new Thickness(0, 14, 0, 8), FontSize = 18, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = "镜像", Margin = new Thickness(0, 4, 0, 6), Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85)) });
            panel.Children.Add(imageCombo);
            panel.Children.Add(new TextBlock { Text = "容器名", Margin = new Thickness(0, 12, 0, 6), Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85)) });
            panel.Children.Add(nameBox);
            panel.Children.Add(new TextBlock { Text = "CPU 核数", Margin = new Thickness(0, 12, 0, 6), Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85)) });
            panel.Children.Add(cpuCoreCombo);
            panel.Children.Add(new TextBlock
            {
                Text = hostCpuLimitCores > 0
                    ? string.Format("当前宿主机上限：{0} 核（支持 0.5 步进）", hostCpuLimitCores.ToString("0.##", CultureInfo.InvariantCulture))
                    : "未获取到宿主机核数，已使用默认选项。",
                Margin = new Thickness(0, 6, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105))
            });
            panel.Children.Add(new TextBlock { Text = "内存限制 (MB)", Margin = new Thickness(0, 12, 0, 6), Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85)) });
            panel.Children.Add(new TextBlock
            {
                Text = "限制容器运行时 RAM，必须为 4 MB 的倍数（不等于镜像/磁盘大小）。",
                Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105))
            });
            var memoryRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            memoryRow.Children.Add(memoryLimitBox);
            memoryRow.Children.Add(new TextBlock
            {
                Text = "MB",
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85))
            });
            memoryRow.Children.Add(memorySlider);
            panel.Children.Add(memoryRow);
            panel.Children.Add(memoryHintText);
            panel.Children.Add(new TextBlock
            {
                Text = hostMemoryTotalMb > 0
                    ? string.Format(CultureInfo.InvariantCulture, "宿主机总内存约 {0} MB，当前输入上限按 80% 计算为 {1} MB。", hostMemoryTotalMb, uiMemoryUpperMb)
                    : "未获取到宿主机总内存，已使用默认上限 65536 MB。",
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105))
            });
            panel.Children.Add(new TextBlock { Text = "Ports", Margin = new Thickness(0, 14, 0, 4), FontSize = 18, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock
            {
                Text = "多个端口时至少填写一个 Host 端口（1-65535）；其余可留空，不支持 0。",
                Margin = new Thickness(0, 0, 0, 8),
                Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105))
            });

            var portsScroll = new ScrollViewer
            {
                MaxHeight = 120,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = portsPanel
            };
            panel.Children.Add(portsScroll);
            cassPortPanel.Children.Add(new TextBlock
            {
                Text = "CASS 配置端口",
                Margin = new Thickness(0, 6, 0, 8),
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(30, 41, 59))
            });
            var cassPortRow = new StackPanel { Orientation = Orientation.Horizontal };
            var cassMonitorPanel = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
            cassMonitorPanel.Children.Add(new TextBlock
            {
                Text = "远程端口号",
                Margin = new Thickness(0, 0, 0, 6),
                Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85))
            });
            cassMonitorPanel.Children.Add(cassMonitorPortBox);
            var cassBootPanel = new StackPanel();
            cassBootPanel.Children.Add(new TextBlock
            {
                Text = "远程端口",
                Margin = new Thickness(0, 0, 0, 6),
                Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85))
            });
            cassBootPanel.Children.Add(cassBootPortBox);
            cassPortRow.Children.Add(cassMonitorPanel);
            cassPortRow.Children.Add(cassBootPanel);
            cassPortPanel.Children.Add(cassPortRow);
            panel.Children.Add(cassPortPanel);
            refreshPortsUi();

            CreateContainerRequest request = null;
            var okButton = new Button { Content = "确定", Width = 86, Height = 32, Margin = new Thickness(0, 14, 8, 0), IsDefault = true };
            var cancelButton = new Button { Content = "取消", Width = 86, Height = 32, Margin = new Thickness(0, 14, 0, 0), IsCancel = true };
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            btnPanel.Children.Add(okButton);
            btnPanel.Children.Add(cancelButton);

            var scrollHost = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = panel
            };

            var rootPanel = new DockPanel();
            DockPanel.SetDock(btnPanel, Dock.Bottom);
            rootPanel.Children.Add(btnPanel);
            rootPanel.Children.Add(scrollHost);

            updateMemoryHint();

            var dialog = new Window
            {
                Title = "创建容器",
                Width = 820,
                Height = 760,
                MinWidth = 760,
                MinHeight = 680,
                ResizeMode = ResizeMode.CanResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = rootPanel
            };

            okButton.Click += (_, __) =>
            {
                var image = imageCombo.SelectedItem as string;
                var name = (nameBox.Text ?? string.Empty).Trim();
                var cpuCoreText = (cpuCoreCombo.SelectedItem as string ?? string.Empty).Trim();
                var memoryLimitText = (memoryLimitBox.Text ?? string.Empty).Trim();
                var isCassImage = IsCassImage(image);

                if (string.IsNullOrWhiteSpace(image))
                {
                    MessageBox.Show("请选择镜像。", "创建容器", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (string.IsNullOrWhiteSpace(name))
                {
                    MessageBox.Show("请输入容器名。", "创建容器", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-zA-Z0-9_.-]+$"))
                {
                    MessageBox.Show("容器名称只能包含[a-z A-Z 0-9 _ . -]（字母、数字、下划线、点、横杠）。", "创建容器", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                double cpuCores;
                if (!double.TryParse(cpuCoreText, NumberStyles.Float, CultureInfo.InvariantCulture, out cpuCores) || cpuCores < 0.5)
                {
                    MessageBox.Show("CPU 核数请输入有效数字（最小 0.5）。", "创建容器", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (hostCpuLimitCores > 0 && cpuCores > hostCpuLimitCores)
                {
                    MessageBox.Show(
                        string.Format("CPU 核数不能超过当前宿主机上限 {0} 核。", hostCpuLimitCores.ToString("0.##", CultureInfo.InvariantCulture)),
                        "创建容器",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                int memoryLimitMb;
                if (!int.TryParse(memoryLimitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out memoryLimitMb) || memoryLimitMb <= 0)
                {
                    MessageBox.Show("内存限制必须是正整数（单位 MB）。", "创建容器", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (memoryLimitMb % 4 != 0)
                {
                    MessageBox.Show("内存限制必须是 4 MB 的倍数。", "创建容器", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                string cassMonitorPort = string.Empty;
                string cassBootPort = string.Empty;
                if (isCassImage)
                {
                    cassMonitorPort = (cassMonitorPortBox.Text ?? string.Empty).Trim();
                    cassBootPort = (cassBootPortBox.Text ?? string.Empty).Trim();
                    if (!IsValidTcpUdpPort(cassMonitorPort) || !IsValidTcpUdpPort(cassBootPort))
                    {
                        MessageBox.Show("CASS 远程端口号和远程端口必须是 1-65535 的数字。", "创建容器", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                }

                var mappings = new List<PortMapping>();
                for (var i = 0; i < portInputRows.Count; i++)
                {
                    var input = portInputRows[i];
                    var hostPort = (input.HostPortBox.Text ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(hostPort))
                    {
                        continue;
                    }

                    int portNum;
                    if (!int.TryParse(hostPort, out portNum) || portNum <= 0 || portNum > 65535)
                    {
                        MessageBox.Show("Host端口必须是 1-65535 的数字。", "创建容器", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    mappings.Add(new PortMapping(hostPort, input.ContainerPort));
                }

                if (portInputRows.Count > 0 && mappings.Count == 0)
                {
                    MessageBox.Show("请至少填写一个 Host端口。", "创建容器", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var duplicatePort = mappings
                    .GroupBy(m => m.HostPort, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(g => g.Count() > 1);
                if (duplicatePort != null)
                {
                    MessageBox.Show("Host端口存在重复：" + duplicatePort.Key, "创建容器", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (IsContainerNameExists(contextName, name))
                {
                    MessageBox.Show("容器名已存在，请更换一个容器名。", "创建容器", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var usedPorts = ReadUsedHostPorts(contextName);
                var conflictPort = mappings
                    .Select(m => m.HostPort)
                    .FirstOrDefault(p => usedPorts.Contains(p));
                if (!string.IsNullOrWhiteSpace(conflictPort))
                {
                    MessageBox.Show(
                        "Host端口 " + conflictPort + " 已被其他容器占用，请更换端口后再创建。",
                        "创建容器",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                request = new CreateContainerRequest(image, name, cpuCores, memoryLimitMb, mappings, cassMonitorPort, cassBootPort);
                dialog.DialogResult = true;
            };

            if (dialog.ShowDialog() == true)
            {
                return request;
            }

            return null;
        }

        private ContainerStressTestRequest ShowContainerStressTestDialog(List<string> imageCandidates)
        {
            var imageCombo = new ComboBox
            {
                Height = 32,
                IsEditable = true,
                ItemsSource = imageCandidates,
                SelectedIndex = imageCandidates.Count > 0 ? 0 : -1,
                Margin = new Thickness(0, 0, 0, 10)
            };

            var modeCombo = new ComboBox
            {
                Height = 32,
                Margin = new Thickness(0, 0, 0, 10)
            };
            modeCombo.Items.Add("create - 只创建容器");
            modeCombo.Items.Add("run - 创建并启动容器");
            modeCombo.SelectedIndex = 0;

            var prefixBox = new TextBox { Text = "stress", Height = 30, Margin = new Thickness(0, 0, 0, 10) };
            var maxBox = new TextBox { Text = "100", Height = 30, Margin = new Thickness(0, 0, 0, 10) };
            var batchBox = new TextBox { Text = "10", Height = 30, Margin = new Thickness(0, 0, 0, 10) };
            var cpuBox = new TextBox { Text = "0.5", Height = 30, Margin = new Thickness(0, 0, 0, 10) };
            var memoryBox = new TextBox { Text = "128", Height = 30, Margin = new Thickness(0, 0, 0, 10) };
            var cassStressPanel = new StackPanel
            {
                Margin = new Thickness(0, 0, 0, 10),
                Visibility = Visibility.Collapsed
            };
            var cassMonitorStartBox = new TextBox { Text = "3221", Height = 30, Margin = new Thickness(0, 0, 0, 10) };
            var cassBootStartBox = new TextBox { Text = "3222", Height = 30, Margin = new Thickness(0, 0, 0, 10) };
            var cleanupBox = new CheckBox
            {
                Content = "测试结束后自动删除本次创建的容器",
                Margin = new Thickness(0, 2, 0, 10),
                IsChecked = false
            };

            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock { Text = "镜像", Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85)), Margin = new Thickness(0, 0, 0, 6) });
            panel.Children.Add(imageCombo);
            panel.Children.Add(new TextBlock { Text = "测试模式", Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85)), Margin = new Thickness(0, 0, 0, 6) });
            panel.Children.Add(modeCombo);
            panel.Children.Add(new TextBlock { Text = "容器名前缀", Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85)), Margin = new Thickness(0, 0, 0, 6) });
            panel.Children.Add(prefixBox);
            panel.Children.Add(new TextBlock { Text = "最大尝试数量", Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85)), Margin = new Thickness(0, 0, 0, 6) });
            panel.Children.Add(maxBox);
            panel.Children.Add(new TextBlock { Text = "每多少个记录一次日志", Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85)), Margin = new Thickness(0, 0, 0, 6) });
            panel.Children.Add(batchBox);
            panel.Children.Add(new TextBlock { Text = "每个容器 CPU 核数", Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85)), Margin = new Thickness(0, 0, 0, 6) });
            panel.Children.Add(cpuBox);
            panel.Children.Add(new TextBlock { Text = "每个容器内存限制 MB", Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85)), Margin = new Thickness(0, 0, 0, 6) });
            panel.Children.Add(memoryBox);
            cassStressPanel.Children.Add(new TextBlock { Text = "CASS 远程端口号起点", Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85)), Margin = new Thickness(0, 0, 0, 6) });
            cassStressPanel.Children.Add(cassMonitorStartBox);
            cassStressPanel.Children.Add(new TextBlock { Text = "CASS 远程端口起点", Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85)), Margin = new Thickness(0, 0, 0, 6) });
            cassStressPanel.Children.Add(cassBootStartBox);
            panel.Children.Add(cassStressPanel);
            panel.Children.Add(cleanupBox);

            ContainerStressTestRequest request = null;
            var okButton = new Button
            {
                Content = "开始测试",
                Width = 96,
                Height = 32,
                Margin = new Thickness(0, 6, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                Cursor = Cursors.Hand
            };
            var cancelButton = new Button
            {
                Content = "取消",
                Width = 78,
                Height = 32,
                Margin = new Thickness(0, 6, 0, 0),
                Cursor = Cursors.Hand
            };
            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            panel.Children.Add(buttonPanel);

            var dialog = new Window
            {
                Title = "容器压力测试",
                Width = 500,
                Height = 700,
                ResizeMode = ResizeMode.CanResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
            };

            Action refreshStressCassUi = () =>
            {
                cassStressPanel.Visibility = IsCassImage(imageCombo.Text) ? Visibility.Visible : Visibility.Collapsed;
            };
            imageCombo.SelectionChanged += (_, __) => refreshStressCassUi();
            imageCombo.LostFocus += (_, __) => refreshStressCassUi();
            refreshStressCassUi();

            okButton.Click += (s, e) =>
            {
                var image = (imageCombo.Text ?? string.Empty).Trim();
                var prefix = (prefixBox.Text ?? string.Empty).Trim();
                var isCassImage = IsCassImage(image);
                int maxCount;
                int batchSize;
                double cpus;
                int memoryMb;
                int cassMonitorStartPort = 0;
                int cassBootStartPort = 0;

                if (string.IsNullOrWhiteSpace(image))
                {
                    MessageBox.Show(dialog, "请选择镜像。", "容器压力测试", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (string.IsNullOrWhiteSpace(prefix) || !Regex.IsMatch(prefix, "^[a-zA-Z0-9_.-]+$"))
                {
                    MessageBox.Show(dialog, "容器名前缀只能包含字母、数字、下划线、点、横杠。", "容器压力测试", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!int.TryParse(maxBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out maxCount) || maxCount <= 0)
                {
                    MessageBox.Show(dialog, "最大尝试数量请输入正整数。", "容器压力测试", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (!int.TryParse(batchBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out batchSize) || batchSize <= 0)
                {
                    MessageBox.Show(dialog, "日志批次请输入正整数。", "容器压力测试", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (!double.TryParse(cpuBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out cpus) || cpus <= 0)
                {
                    MessageBox.Show(dialog, "CPU 核数请输入正数。", "容器压力测试", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (!int.TryParse(memoryBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out memoryMb) || memoryMb <= 0)
                {
                    MessageBox.Show(dialog, "内存限制请输入正整数。", "容器压力测试", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (isCassImage)
                {
                    if (!int.TryParse(cassMonitorStartBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out cassMonitorStartPort) ||
                        cassMonitorStartPort <= 0 ||
                        cassMonitorStartPort > 65535 ||
                        !int.TryParse(cassBootStartBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out cassBootStartPort) ||
                        cassBootStartPort <= 0 ||
                        cassBootStartPort > 65535)
                    {
                        MessageBox.Show(dialog, "CASS 端口起点必须是 1-65535 的数字。", "容器压力测试", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                }

                request = new ContainerStressTestRequest(
                    image,
                    prefix,
                    modeCombo.SelectedIndex == 1,
                    maxCount,
                    batchSize,
                    cpus,
                    memoryMb,
                    cleanupBox.IsChecked == true,
                    cassMonitorStartPort,
                    cassBootStartPort);
                dialog.DialogResult = true;
                dialog.Close();
            };
            cancelButton.Click += (s, e) =>
            {
                dialog.DialogResult = false;
                dialog.Close();
            };

            dialog.ShowDialog();
            return request;
        }

        private async Task ExecuteContainerStressTestAsync(string contextName, ContainerStressTestRequest request)
        {
            Window progressWindow = null;
            TextBlock progressText = null;
            TextBlock progressStageText;
            ProgressBar progressBar;
            var createdNames = new List<string>();
            var failed = false;
            var lastOutput = string.Empty;

            try
            {
                progressStageText = new TextBlock
                {
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(36, 64, 98)),
                    Margin = new Thickness(0, 0, 0, 8),
                    Text = "阶段：准备压力测试"
                };
                progressBar = new ProgressBar
                {
                    Minimum = 0,
                    Maximum = request.MaxCount,
                    Height = 16,
                    Margin = new Thickness(0, 0, 0, 10),
                    Value = 0
                };
                progressText = new TextBlock
                {
                    Text = string.Format("镜像：{0}\n模式：{1}\n最大尝试数量：{2}", request.Image, request.RunContainers ? "run" : "create", request.MaxCount),
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromRgb(42, 53, 68))
                };
                var panel = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
                panel.Children.Add(progressStageText);
                panel.Children.Add(progressBar);
                panel.Children.Add(progressText);
                progressWindow = new Window
                {
                    Title = "正在执行容器压力测试",
                    Width = 660,
                    Height = 260,
                    ResizeMode = ResizeMode.NoResize,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this,
                    Content = panel
                };
                progressWindow.Show();
                await Dispatcher.Yield(DispatcherPriority.Background);

                for (var i = 1; i <= request.MaxCount; i++)
                {
                    var name = string.Format(CultureInfo.InvariantCulture, "{0}-{1:D5}", request.Prefix, i);
                    var command = BuildContainerStressTestCommand(request, name);
                    Tuple<string, string> cassPorts = null;
                    if (request.RequiresCassConfigUpdate)
                    {
                        cassPorts = FindAvailableUdpPorts(contextName, request.CassMonitorStartPort, request.CassBootStartPort);
                    }
                    if (progressStageText != null)
                    {
                        progressStageText.Text = string.Format(CultureInfo.InvariantCulture, "阶段：{0} 容器 {1}/{2}", request.RunContainers ? "创建并启动" : "创建", i, request.MaxCount);
                    }
                    if (progressBar != null)
                    {
                        progressBar.Value = i - 1;
                    }
                    SetProgressText(progressText, string.Format(CultureInfo.InvariantCulture, "正在处理：{0}\n命令：docker {1}\n已成功：{2}", name, command, createdNames.Count));

                    var output = string.Empty;
                    var ok = await Task.Run(() => TryRunDockerCommand(command, contextName, out output, 30000));
                    lastOutput = output;
                    if (!ok)
                    {
                        failed = true;
                        AddContainerOperationLog(contextName, "容器压力测试", string.Empty, name, "失败", string.IsNullOrWhiteSpace(output) ? "docker 命令执行失败。" : output.Trim());
                        break;
                    }

                    createdNames.Add(name);
                    if (request.RequiresCassConfigUpdate && cassPorts != null)
                    {
                        var createdContainerId = ResolveContainerIdByName(contextName, name);
                        if (!string.IsNullOrWhiteSpace(createdContainerId))
                        {
                            var cassConfigOut = string.Empty;
                            var cassOk = TryApplyCassContainerConfig(contextName, createdContainerId, cassPorts.Item1, cassPorts.Item2, out cassConfigOut);
                            AddContainerOperationLog(
                                contextName,
                                "CASS配置",
                                createdContainerId,
                                name,
                                cassOk ? "成功" : "失败",
                                cassOk
                                    ? string.Format(CultureInfo.InvariantCulture, "ePLC_Monitor_Port={0}, ePLC_Boot_Port/CASS_UDP_PORT__={1}", cassPorts.Item1, cassPorts.Item2)
                                    : cassConfigOut);
                        }
                    }
                    if ((i % request.BatchSize) == 0 || i == 1)
                    {
                        AddContainerOperationLog(contextName, "容器压力测试", string.Empty, name, "成功", string.Format(CultureInfo.InvariantCulture, "已成功创建 {0} 个。", createdNames.Count));
                    }
                }

                if (progressBar != null)
                {
                    progressBar.Value = createdNames.Count;
                }

                if (request.CleanupAfterTest && createdNames.Count > 0)
                {
                    if (progressStageText != null)
                    {
                        progressStageText.Text = "阶段：清理压力测试容器";
                    }
                    SetProgressText(progressText, string.Format(CultureInfo.InvariantCulture, "正在删除本次创建的 {0} 个容器...", createdNames.Count));
                    await CleanupStressTestContainersAsync(contextName, createdNames);
                }

                await RefreshFullContainerRowsForContextAsync(contextName);
                CloseWindowQuietly(ref progressWindow);

                var detail = failed
                    ? string.Format(CultureInfo.InvariantCulture, "压力测试停止。成功创建 {0} 个容器。\n失败输出：\n{1}", createdNames.Count, string.IsNullOrWhiteSpace(lastOutput) ? "无输出" : lastOutput.Trim())
                    : string.Format(CultureInfo.InvariantCulture, "压力测试完成。成功创建 {0} 个容器。", createdNames.Count);

                AddContainerOperationLog(contextName, "容器压力测试", string.Empty, request.Prefix, failed ? "失败" : "成功", detail);
                MessageBox.Show(this, detail, "容器压力测试", MessageBoxButton.OK, failed ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            finally
            {
                CloseWindowQuietly(ref progressWindow);
            }
        }

        private static string BuildContainerStressTestCommand(ContainerStressTestRequest request, string containerName)
        {
            var command = request.RunContainers ? "run -it -d" : "create -it";
            command += string.Format(" --name \"{0}\"", containerName);
            command += " --privileged";
            command += " --net=host";
            command += " -v /home/ftpuser:/home/ftpuser";
            command += string.Format(" --cpus {0}", request.Cpus.ToString("0.##", CultureInfo.InvariantCulture));
            command += string.Format(" --memory {0}m", request.MemoryMb);
            command += string.Format(" \"{0}\"", request.Image);
            return command;
        }

        private bool TryApplyCassContainerConfig(string contextName, string containerId, string monitorPort, string bootPort, out string output)
        {
            output = string.Empty;
            if (string.IsNullOrWhiteSpace(containerId))
            {
                output = "容器 ID 为空。";
                return false;
            }

            string startOut;
            TryRunDockerCommand(string.Format("start \"{0}\"", containerId), contextName, out startOut, 30000);

            var script = string.Format(
                CultureInfo.InvariantCulture,
                "set -e; " +
                "engine=$(find / -name EngineConfig.txt -type f 2>/dev/null | head -n 1); " +
                "boot=$(find / -name BootConfig.txt -type f 2>/dev/null | head -n 1); " +
                "test -n \"$engine\"; test -n \"$boot\"; " +
                "sed -i -E \"s/^(ePLC_Monitor_Port:).*/\\1{0}/; s/^(ePLC_Boot_Port:).*/\\1{1}/\" \"$engine\"; " +
                "sed -i -E \"s/^(CASS_UDP_PORT__:).*/\\1{1}/\" \"$boot\"; " +
                "printf \"EngineConfig=%s\\nBootConfig=%s\\n\" \"$engine\" \"$boot\"",
                monitorPort,
                bootPort);

            var command = string.Format("exec \"{0}\" sh -c '{1}'", containerId, script);
            if (TryRunDockerCommand(command, contextName, out output, 30000))
            {
                return true;
            }

            var fallbackScript = script.Replace("sed -i -E", "sed -i");
            command = string.Format("exec \"{0}\" sh -c '{1}'", containerId, fallbackScript);
            return TryRunDockerCommand(command, contextName, out output, 30000);
        }

        private async Task CleanupStressTestContainersAsync(string contextName, List<string> containerNames)
        {
            var containerIds = (containerNames ?? new List<string>())
                .Select(name => ResolveContainerIdByName(contextName, name))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            for (var i = 0; i < containerIds.Count; i += 50)
            {
                var chunk = containerIds.Skip(i).Take(50).Select(id => string.Format("\"{0}\"", id)).ToList();
                if (chunk.Count == 0)
                {
                    continue;
                }

                var command = "rm -f " + string.Join(" ", chunk);
                var output = string.Empty;
                await Task.Run(() => TryRunDockerCommand(command, contextName, out output, 30000));
            }
        }

        private static string BuildCreateContainerCommand(CreateContainerRequest request)
        {
            var command = string.Format("create -it --name \"{0}\"", request.ContainerName);
            command += " --privileged";
            command += " --net=host";
            command += " -v /home/ftpuser:/home/ftpuser";
            command += string.Format(" --cpus {0}", request.CpuCores.ToString("0.##", CultureInfo.InvariantCulture));
            command += string.Format(" --memory {0}m", request.MemoryLimitMb);
            for (var i = 0; i < request.PortMappings.Count; i++)
            {
                var mapping = request.PortMappings[i];
                command += string.Format(" -p {0}:{1}", mapping.HostPort, mapping.ContainerPort);
            }

            command += string.Format(" \"{0}\"", request.Image);
            return command;
        }

        private Window CreateContainerLoadingWindow(string text)
        {
            var stageText = new TextBlock
            {
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(36, 64, 98)),
                Margin = new Thickness(0, 0, 0, 8),
                Text = "阶段：加载中"
            };
            var progressBar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Height = 16,
                Margin = new Thickness(0, 0, 0, 10),
                IsIndeterminate = true
            };
            var detailText = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(text) ? "正在加载..." : text,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(42, 53, 68))
            };
            var panel = new StackPanel
            {
                Margin = new Thickness(18, 14, 18, 14)
            };
            panel.Children.Add(stageText);
            panel.Children.Add(progressBar);
            panel.Children.Add(detailText);

            return new Window
            {
                Title = "正在加载",
                Width = 520,
                Height = 210,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Content = panel
            };
        }

        private static List<string> BuildCpuCoreOptions(double hostCpuLimitCores)
        {
            if (hostCpuLimitCores <= 0)
            {
                return new List<string> { "0.5", "1", "2", "4", "8", "16" };
            }

            var options = new List<string>();
            var maxHalfStep = (int)Math.Floor(hostCpuLimitCores * 2d + 0.0001d);
            for (var step = 1; step <= maxHalfStep; step++)
            {
                var value = step / 2d;
                if (value > hostCpuLimitCores + 0.0001d)
                {
                    break;
                }

                options.Add(value.ToString("0.##", CultureInfo.InvariantCulture));
            }

            return options.Distinct(StringComparer.Ordinal).ToList();
        }

        private bool IsContainerNameExists(string contextName, string containerName)
        {
            if (string.IsNullOrWhiteSpace(containerName))
            {
                return false;
            }

            string output;
            if (!TryRunDockerCommand("ps -a --format \"{{.Names}}\"", contextName, out output))
            {
                return false;
            }

            var names = (output ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(n => n.Trim())
                .Where(n => !string.IsNullOrWhiteSpace(n));
            return names.Any(n => string.Equals(n, containerName.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        private HashSet<string> ReadUsedHostPorts(string contextName)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string output;
            if (!TryRunDockerCommand("ps -a --format \"{{.Ports}}\"", contextName, out output))
            {
                return result;
            }

            var lines = (output ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i] ?? string.Empty;
                var matches = System.Text.RegularExpressions.Regex.Matches(line, @"(?<port>\d+)->\d+/(tcp|udp)");
                for (var j = 0; j < matches.Count; j++)
                {
                    var port = matches[j].Groups["port"].Value;
                    if (!string.IsNullOrWhiteSpace(port))
                    {
                        result.Add(port);
                    }
                }
            }

            return result;
        }

        private Tuple<string, string> FindAvailableUdpPorts(string contextName, int preferredMonitorPort, int preferredBootPort)
        {
            var usedPorts = ReadUsedUdpPorts(contextName);
            var monitorPort = FindNextAvailablePort(preferredMonitorPort, usedPorts);
            usedPorts.Add(monitorPort.ToString(CultureInfo.InvariantCulture));
            var bootPort = FindNextAvailablePort(preferredBootPort, usedPorts);
            return Tuple.Create(
                monitorPort.ToString(CultureInfo.InvariantCulture),
                bootPort.ToString(CultureInfo.InvariantCulture));
        }

        private static int FindNextAvailablePort(int preferredPort, HashSet<string> usedPorts)
        {
            var port = Math.Max(1, Math.Min(65535, preferredPort));
            while (port <= 65535)
            {
                if (usedPorts == null || !usedPorts.Contains(port.ToString(CultureInfo.InvariantCulture)))
                {
                    return port;
                }

                port++;
            }

            return preferredPort;
        }

        private HashSet<string> ReadUsedUdpPorts(string contextName)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string dockerPorts;
            if (TryRunDockerCommand("ps -a --format \"{{.Ports}}\"", contextName, out dockerPorts, 8000))
            {
                var lines = (dockerPorts ?? string.Empty)
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i] ?? string.Empty;
                    var matches = Regex.Matches(line, @"(?<port>\d+)->\d+/udp");
                    for (var j = 0; j < matches.Count; j++)
                    {
                        var port = matches[j].Groups["port"].Value;
                        if (!string.IsNullOrWhiteSpace(port))
                        {
                            result.Add(port);
                        }
                    }
                }
            }

            string ssOutput;
            if (TryReadHostUdpPorts(contextName, out ssOutput))
            {
                var matches = Regex.Matches(ssOutput ?? string.Empty, @":(?<port>\d+)\s");
                for (var i = 0; i < matches.Count; i++)
                {
                    var port = matches[i].Groups["port"].Value;
                    if (!string.IsNullOrWhiteSpace(port))
                    {
                        result.Add(port);
                    }
                }
            }

            return result;
        }

        private bool TryReadHostUdpPorts(string contextName, out string output)
        {
            output = string.Empty;
            string identity;
            if (!TryResolveSshIdentityForContext(contextName, out identity) || string.IsNullOrWhiteSpace(identity))
            {
                return false;
            }

            string user;
            string host;
            string password;
            if (!TryResolveSshTarget(identity, out user, out host, out password))
            {
                return false;
            }

            string stdErr;
            return ExecuteSshRemoteCommand(user, host, password, "ss -lun 2>/dev/null || netstat -lun 2>/dev/null", 8000, out output, out stdErr);
        }

        private static string ExtractContainerId(string output)
        {
            var lines = (output ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var candidate = (lines[i] ?? string.Empty).Trim();
                if (candidate.Length >= 12 &&
                    candidate.All(ch => (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F')))
                {
                    return candidate;
                }
            }

            return string.Empty;
        }

        private string ResolveContainerIdByName(string contextName, string containerName)
        {
            var name = (containerName ?? string.Empty).Trim().TrimStart('/');
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            string output;
            var command = string.Format(
                "ps -a --no-trunc --filter \"name=^/{0}$\" --format \"{{{{.ID}}}}\"",
                name.Replace("\"", "\\\""));
            if (!TryRunDockerCommand(command, contextName, out output))
            {
                return string.Empty;
            }

            var id = (output ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => (line ?? string.Empty).Trim())
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));

            return id ?? string.Empty;
        }

        private static bool IsCpuLimitMatched(double requestedCores, string actualCpuCoresText)
        {
            double actualCores;
            if (!double.TryParse(actualCpuCoresText, NumberStyles.Float, CultureInfo.InvariantCulture, out actualCores))
            {
                return false;
            }

            return Math.Abs(requestedCores - actualCores) < 0.01d;
        }

        private static bool IsMemoryLimitMatched(int requestedMemoryMb, string actualMemoryLimitText)
        {
            var text = (actualMemoryLimitText ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text) || string.Equals(text, "不限", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (TryParseMemoryLimitToMb(text, out var actualMb))
            {
                return Math.Abs(requestedMemoryMb - actualMb) <= 1;
            }

            return false;
        }

        private static bool TryParseMemoryLimitToMb(string value, out int mb)
        {
            mb = 0;
            var text = (value ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var numberPart = new string(text.TakeWhile(ch => (ch >= '0' && ch <= '9') || ch == '.').ToArray());
            var unitPart = text.Substring(numberPart.Length).Trim();
            if (string.IsNullOrWhiteSpace(numberPart))
            {
                return false;
            }

            double num;
            if (!double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out num))
            {
                return false;
            }

            switch (unitPart)
            {
                case "B":
                    mb = (int)Math.Round(num / (1024d * 1024d), MidpointRounding.AwayFromZero);
                    return true;
                case "KB":
                    mb = (int)Math.Round(num / 1024d, MidpointRounding.AwayFromZero);
                    return true;
                case "MB":
                    mb = (int)Math.Round(num, MidpointRounding.AwayFromZero);
                    return true;
                case "GB":
                    mb = (int)Math.Round(num * 1024d, MidpointRounding.AwayFromZero);
                    return true;
                case "TB":
                    mb = (int)Math.Round(num * 1024d * 1024d, MidpointRounding.AwayFromZero);
                    return true;
                default:
                    return false;
            }
        }

        private string GetSelectedContainerContextName()
        {
            var deviceName = GetSelectedContainerDeviceName();
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                return string.Empty;
            }

            string contextName;
            if (_deviceContextMap.TryGetValue(deviceName, out contextName))
            {
                return contextName;
            }

            var device = _devices.FirstOrDefault(d => string.Equals(d.Name, deviceName, StringComparison.OrdinalIgnoreCase));
            if (device != null && !string.IsNullOrWhiteSpace(device.ContextName))
            {
                return device.ContextName;
            }

            return string.Empty;
        }

        private async Task<bool> WaitForContainerRowsAsync(string contextName, Func<List<ContainerComposeRow>, bool> predicate, int timeoutMs)
        {
            var context = (contextName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(context))
            {
                return false;
            }

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                var remainingMs = timeoutMs - (int)sw.ElapsedMilliseconds;
                if (remainingMs <= 0)
                {
                    return false;
                }

                var refreshTask = RefreshFullContainerRowsForContextAsync(context);
                var refreshCompleted = await Task.WhenAny(
                    refreshTask,
                    Task.Delay(remainingMs));
                if (refreshCompleted != refreshTask)
                {
                    return false;
                }

                await refreshTask;
                var rows = _containerRows
                    .Where(r => string.Equals(GetContainerContextNameByDevice(r.DeviceName), context, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (predicate(rows))
                {
                    return true;
                }

                await Task.Delay(250);
            }

            return false;
        }

        private async Task RefreshFullContainerRowsForContextAsync(string contextName)
        {
            var context = (contextName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(context))
            {
                return;
            }

            var deviceName = GetDeviceNameByContextName(context);
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                return;
            }

            var summaryVersion = InvalidateContainerRefresh(context);
            var query = await Task.Run(() => QueryContainerSummary(context));
            if (!IsContainerRefreshVersionCurrent(context, summaryVersion))
            {
                return;
            }

            if (!query.Success)
            {
                InvalidateContainerRefresh(context);
                SetContainerDeviceOnline(context, false);
                if (string.Equals(GetSelectedContainerContextName(), context, StringComparison.OrdinalIgnoreCase))
                {
                    SetContainerPageState("设备离线，正在显示只读缓存数据", false, true);
                }
                return;
            }

            ApplyContainerSummary(context, deviceName, query.Containers);
            SetContainerDeviceOnline(context, true);
            if (string.Equals(GetSelectedContainerContextName(), context, StringComparison.OrdinalIgnoreCase))
            {
                SetContainerPageState(query.Containers.Count == 0 ? "暂无容器" : "设备在线，概要已同步", true, false);
            }

            var refreshState = BeginContainerDetailRefresh(context);
            var containerIdSnapshot = BuildContainerIdSnapshot(query.Containers);
            StartContainerDetailsRefresh(context, deviceName, query.Containers, containerIdSnapshot, refreshState.Item1, refreshState.Item2);
        }

        private void ApplyRefreshedContainerRows(string deviceName, List<ContainerComposeRow> refreshedRows)
        {
            _containerRows.RemoveAll(r => string.Equals(r.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
            if (refreshedRows != null && refreshedRows.Count > 0)
            {
                _containerRows.AddRange(refreshedRows);
            }

            BindContainerRows(GetSelectedContainerDeviceName());
        }

        private string ResolveContainerPortsForDisplay(string contextName, string containerId, string portsText, string fallbackPorts)
        {
            var direct = (portsText ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(direct))
            {
                return direct;
            }

            var inspected = ReadContainerPorts(contextName, containerId);
            if (!string.IsNullOrWhiteSpace(inspected))
            {
                return inspected;
            }

            var fallback = (fallbackPorts ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(fallback) ? "-" : fallback;
        }

        private void StartContainerSizeBackfill(string contextName, string deviceName)
        {
            var context = (contextName ?? string.Empty).Trim();
            var device = (deviceName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(context) || string.IsNullOrWhiteSpace(device))
            {
                return;
            }

            lock (_containerSizeBackfillRunningContexts)
            {
                if (_containerSizeBackfillRunningContexts.Contains(context))
                {
                    return;
                }

                _containerSizeBackfillRunningContexts.Add(context);
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var rowsSnapshot = _containerRows
                        .Where(r =>
                            string.Equals(r.DeviceName, device, StringComparison.OrdinalIgnoreCase) &&
                            (string.IsNullOrWhiteSpace(r.Size) || r.Size == "-"))
                        .ToList();

                    for (var i = 0; i < rowsSnapshot.Count; i++)
                    {
                        var row = rowsSnapshot[i];
                        var fullId = row.FullId;
                        if (string.IsNullOrWhiteSpace(fullId))
                        {
                            continue;
                        }

                        string output;
                        var inspectCmd = string.Format("container inspect --size \"{0}\" --format \"{{{{.SizeRw}}}}\"", fullId);
                        var ok = TryRunDockerCommand(inspectCmd, context, out output, 10000);
                        if (!ok)
                        {
                            continue;
                        }

                        var text = (output ?? string.Empty).Trim();
                        long sizeBytes;
                        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out sizeBytes) || sizeBytes < 0)
                        {
                            continue;
                        }

                        var sizeText = FormatBytes(sizeBytes);
                        if (!Dispatcher.CheckAccess())
                        {
                            await Dispatcher.InvokeAsync(() => UpdateContainerRowSize(device, fullId, sizeText));
                        }
                        else
                        {
                            UpdateContainerRowSize(device, fullId, sizeText);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Container size backfill failed: " + ex.Message);
                }
                finally
                {
                    lock (_containerSizeBackfillRunningContexts)
                    {
                        _containerSizeBackfillRunningContexts.Remove(context);
                    }
                }
            });
        }

        private void UpdateContainerRowSize(string deviceName, string fullId, string sizeText)
        {
            if (string.IsNullOrWhiteSpace(deviceName) || string.IsNullOrWhiteSpace(fullId))
            {
                return;
            }

            var index = _containerRows.FindIndex(r =>
                string.Equals(r.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase) &&
                IsContainerMatch(r, fullId));
            if (index < 0)
            {
                return;
            }

            var current = _containerRows[index];
            _containerRows[index] = new ContainerComposeRow(
                current.DeviceName,
                current.Id,
                current.Name,
                current.ChineseName,
                current.Image,
                current.Status,
                current.Ports,
                current.CpuCores,
                current.CpuPercent,
                current.MemoryUsage,
                current.MemoryPercent,
                current.DiskReadWrite,
                current.Detail,
                current.FullId,
                string.IsNullOrWhiteSpace(sizeText) ? "-" : sizeText);

            var selectedDeviceName = GetSelectedContainerDeviceName();
            if (string.IsNullOrWhiteSpace(selectedDeviceName) ||
                string.Equals(selectedDeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                BindContainerRows(selectedDeviceName);
            }
        }

        private string GetDeviceNameByContextName(string contextName)
        {
            var context = (contextName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(context))
            {
                return string.Empty;
            }

            var mapHit = _deviceContextMap
                .FirstOrDefault(kv => string.Equals(kv.Value, context, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(mapHit.Key))
            {
                return mapHit.Key;
            }

            var device = _devices.FirstOrDefault(d => string.Equals((d.ContextName ?? string.Empty).Trim(), context, StringComparison.OrdinalIgnoreCase));
            return device == null ? string.Empty : (device.Name ?? string.Empty);
        }

        private string GetContainerContextNameByDevice(string deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                return string.Empty;
            }

            string contextName;
            if (_deviceContextMap.TryGetValue(deviceName, out contextName))
            {
                return contextName;
            }

            var device = _devices.FirstOrDefault(d => string.Equals(d.Name, deviceName, StringComparison.OrdinalIgnoreCase));
            return device == null ? string.Empty : (device.ContextName ?? string.Empty);
        }

        private void AddContainerOperationLog(string contextName, string action, string containerId, string containerName, string result, string detail)
        {
            var entry = new ContainerOperationLog(
                DateTime.Now,
                contextName ?? string.Empty,
                (containerId ?? string.Empty).Trim(),
                (containerName ?? string.Empty).Trim(),
                action ?? string.Empty,
                result ?? string.Empty,
                detail ?? string.Empty);

            _containerOperationLogs.Add(entry);
            if (_containerOperationLogs.Count > 500)
            {
                _containerOperationLogs.RemoveRange(0, _containerOperationLogs.Count - 500);
            }
        }

        private string BuildContainerOperationLogsText(string contextName, string containerId, string containerName)
        {
            var normalizedContext = (contextName ?? string.Empty).Trim();
            var normalizedId = (containerId ?? string.Empty).Trim();
            var normalizedName = (containerName ?? string.Empty).Trim();

            var query = _containerOperationLogs.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(normalizedContext))
            {
                query = query.Where(l => string.Equals(l.ContextName, normalizedContext, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(normalizedId) || !string.IsNullOrWhiteSpace(normalizedName))
            {
                query = query.Where(l =>
                    (!string.IsNullOrWhiteSpace(normalizedId) &&
                     (string.Equals(l.ContainerId, normalizedId, StringComparison.OrdinalIgnoreCase) ||
                      normalizedId.StartsWith(l.ContainerId, StringComparison.OrdinalIgnoreCase) ||
                      l.ContainerId.StartsWith(normalizedId, StringComparison.OrdinalIgnoreCase))) ||
                    (!string.IsNullOrWhiteSpace(normalizedName) &&
                     string.Equals((l.ContainerName ?? string.Empty).Trim(), normalizedName, StringComparison.OrdinalIgnoreCase)));
            }

            var lines = query
                .OrderByDescending(l => l.Time)
                .Take(50)
                .Select(l => string.Format(
                    "[{0}] {1} {2} {3} {4}",
                    l.Time.ToString("yyyy-MM-dd HH:mm:ss"),
                    l.Action,
                    string.IsNullOrWhiteSpace(l.Result) ? "-" : l.Result,
                    string.IsNullOrWhiteSpace(l.ContainerName) ? "-" : l.ContainerName,
                    string.IsNullOrWhiteSpace(l.Detail) ? string.Empty : ("| " + l.Detail)))
                .ToList();

            return lines.Count == 0 ? "暂无操作记录。" : string.Join(Environment.NewLine, lines);
        }

        private static string GetContainerLogCsvDirectory()
        {
            return GetProjectRootDirectory();
        }

        private static void CleanupContainerLogCsvFiles()
        {
            try
            {
                var dir = GetContainerLogCsvDirectory();
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    return;
                }

                var files = Directory.GetFiles(dir, "*_container_log.csv", SearchOption.TopDirectoryOnly);
                for (var i = 0; i < files.Length; i++)
                {
                    try
                    {
                        File.Delete(files[i]);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private static string GetContainerLogCsvPathByIp(string deviceIp)
        {
            var ip = (deviceIp ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(ip))
            {
                ip = "unknown";
            }

            var safe = ip.Replace(":", "_").Replace("/", "_").Replace("\\", "_");
            return System.IO.Path.Combine(GetContainerLogCsvDirectory(), safe + "_container_log.csv");
        }

        private void EnsureContainerLogCsvFilesForKnownDevices()
        {
            for (var i = 0; i < _devices.Count; i++)
            {
                EnsureContainerLogCsvForDevice(_devices[i]);
            }
        }

        private string EnsureContainerLogCsvByDeviceName(string deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                return string.Empty;
            }

            var device = _devices.FirstOrDefault(d => string.Equals(d.Name, deviceName, StringComparison.OrdinalIgnoreCase));
            return device == null ? string.Empty : EnsureContainerLogCsvForDevice(device);
        }

        private string EnsureContainerLogCsvForDevice(DeviceInfo device)
        {
            if (device == null || string.IsNullOrWhiteSpace(device.Ip))
            {
                return string.Empty;
            }

            var path = GetContainerLogCsvPathByIp(device.Ip);
            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var header = "操作时间,操作,名称,镜像,状态,CPU核数量,内存大小,Port";
            if (!File.Exists(path))
            {
                File.WriteAllText(path, header + Environment.NewLine, Encoding.UTF8);
            }
            else
            {
                var lines = File.ReadAllLines(path, Encoding.UTF8).ToList();
                var rewritten = false;
                if (lines.Count == 0)
                {
                    lines.Add(header);
                    rewritten = true;
                }
                else if (!string.Equals((lines[0] ?? string.Empty).Trim(), header, StringComparison.Ordinal))
                {
                    lines.Insert(0, header);
                    rewritten = true;
                }

                for (var i = 1; i < lines.Count; i++)
                {
                    var normalizedLine = NormalizeLegacyLogCsvLineUnit(lines[i]);
                    if (!string.Equals(normalizedLine, lines[i], StringComparison.Ordinal))
                    {
                        lines[i] = normalizedLine;
                        rewritten = true;
                    }
                }

                if (rewritten)
                {
                    File.WriteAllLines(path, lines, Encoding.UTF8);
                }
            }

            return path;
        }

        private void AppendContainerOperationCsvByDeviceName(string deviceName, string action, ContainerComposeRow row)
        {
            if (row == null || string.IsNullOrWhiteSpace(deviceName))
            {
                return;
            }

            var path = EnsureContainerLogCsvByDeviceName(deviceName);
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var line = BuildContainerOperationCsvLine(action, row);
            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
        }

        private ContainerComposeRow BuildContainerCsvLogRow(ContainerComposeRow row, string action)
        {
            if (row == null)
            {
                return null;
            }

            var status = row.Status;
            if (string.Equals(action, "启动容器", StringComparison.OrdinalIgnoreCase))
            {
                status = "running";
            }
            else if (string.Equals(action, "停止容器", StringComparison.OrdinalIgnoreCase))
            {
                status = "exited";
            }
            else if (string.Equals(action, "删除容器", StringComparison.OrdinalIgnoreCase))
            {
                status = "deleted";
            }

            return new ContainerComposeRow(
                row.DeviceName,
                row.Id,
                row.Name,
                row.ChineseName,
                row.Image,
                status,
                row.Ports,
                row.CpuCores,
                row.CpuPercent,
                row.MemoryUsage,
                row.MemoryPercent,
                row.DiskReadWrite,
                row.Detail,
                row.FullId);
        }

        private static string EscapeSimpleCsv(string value)
        {
            var text = value ?? string.Empty;
            if (text.Contains("\""))
            {
                text = text.Replace("\"", "\"\"");
            }

            if (text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
            {
                return "\"" + text + "\"";
            }

            return text;
        }

        private static string ShortContainerId(string fullId)
        {
            var id = (fullId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                return string.Empty;
            }

            return id.Length <= 12 ? id : id.Substring(0, 12);
        }

        private static string BuildContainerOperationCsvLine(string action, ContainerComposeRow row)
        {
            var timeText = DateTime.Now.ToString("yyyy/MM/dd_HH:mm:ss", CultureInfo.InvariantCulture);
            var chineseName = row == null ? string.Empty : (row.ChineseName ?? string.Empty);
            var image = row == null ? string.Empty : (row.Image ?? string.Empty);
            var status = row == null ? string.Empty : (row.Status ?? string.Empty);
            var cpu = row == null ? string.Empty : (row.CpuCores ?? string.Empty);
            var size = NormalizeLogMemoryUnit(row == null ? string.Empty : (row.MemoryUsage ?? string.Empty));
            if (string.IsNullOrWhiteSpace(size) || size == "-")
            {
                size = NormalizeLogMemoryUnit(row == null ? string.Empty : (row.Size ?? string.Empty));
            }
            var port = row == null ? string.Empty : (row.Ports ?? string.Empty);
            if (string.IsNullOrWhiteSpace(port))
            {
                port = "-";
            }

            var values = new[]
            {
                EscapeSimpleCsv(timeText),
                EscapeSimpleCsv(action ?? string.Empty),
                EscapeSimpleCsv(chineseName),
                EscapeSimpleCsv(image),
                EscapeSimpleCsv(status),
                EscapeSimpleCsv(cpu),
                EscapeSimpleCsv(size),
                EscapeSimpleCsv(port)
            };
            return string.Join(",", values);
        }

        private static string NormalizeLogMemoryUnit(string sizeText)
        {
            var text = (sizeText ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            text = text.Replace("GiB", "GB")
                       .Replace("GIB", "GB")
                       .Replace("MiB", "MB")
                       .Replace("MIB", "MB")
                       .Replace("KiB", "KB")
                       .Replace("KIB", "KB")
                       .Replace("TiB", "TB")
                       .Replace("TIB", "TB");
            return text;
        }

        private static string NormalizeLegacyLogCsvLineUnit(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return line ?? string.Empty;
            }

            var values = ParseCsvLine(line);
            if (values.Count >= 9)
            {
                values[7] = NormalizeLogMemoryUnit(values[7]);
                return string.Join(",", values.Select(EscapeSimpleCsv));
            }

            if (values.Count >= 8)
            {
                values[6] = NormalizeLogMemoryUnit(values[6]);
                return string.Join(",", values.Select(EscapeSimpleCsv));
            }

            return NormalizeLogMemoryUnit(line);
        }

        private string GetDeviceIpByName(string deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                return string.Empty;
            }

            var device = _devices.FirstOrDefault(d => string.Equals(d.Name, deviceName, StringComparison.OrdinalIgnoreCase));
            return device == null ? string.Empty : (device.Ip ?? string.Empty);
        }

        private static bool IsContainerMatch(ContainerComposeRow row, string fullId)
        {
            if (row == null || string.IsNullOrWhiteSpace(fullId))
            {
                return false;
            }

            var rowId = row.FullId;
            if (string.IsNullOrWhiteSpace(rowId))
            {
                return false;
            }

            return string.Equals(rowId, fullId, StringComparison.OrdinalIgnoreCase);
        }

        private string GetSelectedContainerDeviceName()
        {
            var selectedItem = ContainerDeviceSelector.SelectedItem as ComboBoxItem;
            if (selectedItem != null)
            {
                var marker = selectedItem.Tag as string;
                if (string.Equals(marker, "NONE", StringComparison.OrdinalIgnoreCase))
                {
                    return string.Empty;
                }

                var d = selectedItem.Tag as DeviceInfo;
                if (d != null)
                {
                    return d.Name;
                }

                var content = selectedItem.Content as string;
                if (!string.IsNullOrWhiteSpace(content))
                {
                    var idx = content.IndexOf(" (", StringComparison.Ordinal);
                    return idx > 0 ? content.Substring(0, idx) : content;
                }
            }

            var direct = ContainerDeviceSelector.SelectedItem as string;
            return direct ?? string.Empty;
        }

        private static List<ContainerLogDisplayRow> BuildContainerLogDisplayRows(
            List<string> lines,
            string deviceName,
            string deviceIp)
        {
            var rows = new List<ContainerLogDisplayRow>();
            if (lines == null || lines.Count == 0)
            {
                return rows;
            }

            for (var i = 0; i < lines.Count; i++)
            {
                var line = (lines[i] ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (line.StartsWith("操作时间,", StringComparison.Ordinal))
                {
                    continue;
                }

                var values = ParseCsvLine(line);
                if (values.Count >= 9)
                {
                    // Backward compatible with old CSV format:
                    // 操作时间,操作,Name,名称,镜像,状态,CPU核数量,内存大小,Port
                    rows.Add(new ContainerLogDisplayRow(
                        values[0],
                        values[1],
                        values[3],
                        values[4],
                        values[5],
                        values[6],
                        NormalizeLogMemoryUnit(values[7]),
                        values[8]));
                }
                else if (values.Count >= 8)
                {
                    // New CSV format:
                    // 操作时间,操作,名称,镜像,状态,CPU核数量,内存大小,Port
                    rows.Add(new ContainerLogDisplayRow(
                        values[0],
                        values[1],
                        values[2],
                        values[3],
                        values[4],
                        values[5],
                        NormalizeLogMemoryUnit(values[6]),
                        values[7]));
                }
            }

            return rows;
        }

        private static List<string> ParseCsvLine(string line)
        {
            var result = new List<string>();
            if (line == null)
            {
                return result;
            }

            var sb = new StringBuilder();
            var inQuotes = false;
            for (var i = 0; i < line.Length; i++)
            {
                var ch = line[i];
                if (ch == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (ch == ',' && !inQuotes)
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                }
                else
                {
                    sb.Append(ch);
                }
            }

            result.Add(sb.ToString());
            return result;
        }

        private static void ShowLogsWindow(string title, List<ContainerLogDisplayRow> rows)
        {
            var wrappedTextStyle = new Style(typeof(TextBlock));
            wrappedTextStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
            wrappedTextStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.None));
            wrappedTextStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            wrappedTextStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Left));
            wrappedTextStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(6, 0, 0, 0)));

            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                ItemsSource = rows ?? new List<ContainerLogDisplayRow>(),
                IsReadOnly = true,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserResizeRows = false,
                CanUserSortColumns = false,
                SelectionMode = DataGridSelectionMode.Single,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                RowHeaderWidth = 0,
                GridLinesVisibility = DataGridGridLinesVisibility.All,
                VerticalGridLinesBrush = new SolidColorBrush(Color.FromRgb(198, 204, 213)),
                HorizontalGridLinesBrush = new SolidColorBrush(Color.FromRgb(220, 226, 235)),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontSize = 18,
                FontFamily = new FontFamily("Microsoft YaHei"),
                Background = new SolidColorBrush(Color.FromRgb(250, 251, 252)),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(224, 228, 233))
            };
            ScrollViewer.SetCanContentScroll(grid, true);

            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "操作时间",
                Width = new DataGridLength(1.8, DataGridLengthUnitType.Star),
                MinWidth = 170,
                Binding = new Binding("OperationTime"),
                ElementStyle = wrappedTextStyle
            });
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "操作",
                Width = new DataGridLength(1.0, DataGridLengthUnitType.Star),
                MinWidth = 95,
                Binding = new Binding("Action"),
                ElementStyle = wrappedTextStyle
            });
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "名称",
                Width = new DataGridLength(1.7, DataGridLengthUnitType.Star),
                MinWidth = 145,
                Binding = new Binding("ChineseName"),
                ElementStyle = wrappedTextStyle
            });
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "镜像",
                Width = new DataGridLength(1.5, DataGridLengthUnitType.Star),
                MinWidth = 130,
                Binding = new Binding("Image"),
                ElementStyle = wrappedTextStyle
            });
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "状态",
                Width = new DataGridLength(0.95, DataGridLengthUnitType.Star),
                MinWidth = 95,
                Binding = new Binding("Status"),
                ElementStyle = wrappedTextStyle
            });
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "CPU核数量",
                Width = new DataGridLength(1.2, DataGridLengthUnitType.Star),
                MinWidth = 130,
                Binding = new Binding("CpuCores"),
                ElementStyle = wrappedTextStyle
            });
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "内存大小",
                Width = new DataGridLength(1.2, DataGridLengthUnitType.Star),
                MinWidth = 130,
                Binding = new Binding("MemorySize"),
                ElementStyle = wrappedTextStyle
            });
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Port",
                Width = new DataGridLength(1.4, DataGridLengthUnitType.Star),
                MinWidth = 150,
                Binding = new Binding("Port"),
                ElementStyle = wrappedTextStyle
            });

            var headerStyle = new Style(typeof(DataGridColumnHeader));
            headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(242, 244, 247))));
            headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(71, 85, 105))));
            headerStyle.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(232, 236, 241))));
            headerStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
            headerStyle.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
            headerStyle.Setters.Add(new Setter(Control.FontSizeProperty, 16.0));
            headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 6, 8, 6)));
            headerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
            headerStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.None));
            headerStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.NoWrap));
            grid.ColumnHeaderStyle = headerStyle;

            var rowStyle = new Style(typeof(DataGridRow));
            rowStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
            rowStyle.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(51, 65, 85))));
            rowStyle.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(238, 241, 245))));
            rowStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
            rowStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 6, 8, 6)));
            var selectedTrigger = new Trigger { Property = DataGridRow.IsSelectedProperty, Value = true };
            selectedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(236, 246, 255))));
            selectedTrigger.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(30, 41, 59))));
            rowStyle.Triggers.Add(selectedTrigger);
            grid.RowStyle = rowStyle;

            var cellStyle = new Style(typeof(DataGridCell));
            cellStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            cellStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 8, 10, 8)));
            cellStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
            cellStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            grid.CellStyle = cellStyle;

            var deviceText = title ?? string.Empty;
            var parts = deviceText.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                deviceText = parts[0] + " (" + parts[1] + ")";
            }

            var topPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(18, 12, 18, 10)
            };
            var deviceRow = new StackPanel { Orientation = Orientation.Horizontal };
            deviceRow.Children.Add(new TextBlock
            {
                Text = "设备：",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55)),
                VerticalAlignment = VerticalAlignment.Center
            });
            deviceRow.Children.Add(new Border
            {
                Margin = new Thickness(10, 0, 0, 0),
                Padding = new Thickness(12, 6, 12, 6),
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(214, 221, 232)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = new TextBlock
                {
                    Text = deviceText,
                    FontSize = 15,
                    Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85))
                }
            });
            topPanel.Children.Add(deviceRow);

            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            mainGrid.Children.Add(topPanel);

            var tableBorder = new Border
            {
                Margin = new Thickness(18, 0, 18, 18),
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(218, 226, 238)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8)
            };
            tableBorder.Child = grid;
            Grid.SetRow(tableBorder, 1);
            mainGrid.Children.Add(tableBorder);

            var shell = new Border
            {
                Width = 1320,
                Height = 760,
                Background = new SolidColorBrush(Color.FromRgb(240, 244, 250)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(210, 219, 232)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Child = mainGrid
            };

            var viewbox = new Viewbox
            {
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.Both,
                Child = shell
            };

            var root = new Grid
            {
                Background = new SolidColorBrush(Color.FromRgb(236, 240, 246)),
                Margin = new Thickness(20)
            };
            root.Children.Add(viewbox);

            var window = new Window
            {
                Title = title,
                Width = 1360,
                Height = 820,
                MinWidth = 900,
                MinHeight = 560,
                Background = new SolidColorBrush(Color.FromRgb(236, 240, 246)),
                Content = root
            };

            window.ShowDialog();
        }

        private sealed class ContainerLogDisplayRow
        {
            public ContainerLogDisplayRow(
                string operationTime,
                string action,
                string chineseName,
                string image,
                string status,
                string cpuCores,
                string memorySize,
                string port)
            {
                OperationTime = operationTime;
                Action = action;
                ChineseName = chineseName;
                Image = image;
                Status = status;
                CpuCores = cpuCores;
                MemorySize = memorySize;
                Port = port;
            }

            public string OperationTime { get; }
            public string Action { get; }
            public string ChineseName { get; }
            public string Image { get; }
            public string Status { get; }
            public string CpuCores { get; }
            public string MemorySize { get; }
            public string Port { get; }
        }

        private sealed class ContainerOperationLog
        {
            public ContainerOperationLog(DateTime time, string contextName, string containerId, string containerName, string action, string result, string detail)
            {
                Time = time;
                ContextName = contextName;
                ContainerId = containerId;
                ContainerName = containerName;
                Action = action;
                Result = result;
                Detail = detail;
            }

            public DateTime Time { get; }
            public string ContextName { get; }
            public string ContainerId { get; }
            public string ContainerName { get; }
            public string Action { get; }
            public string Result { get; }
            public string Detail { get; }
        }

        private sealed class CreateContainerRequest
        {
            public CreateContainerRequest(string image, string containerName, double cpuCores, int memoryLimitMb, List<PortMapping> portMappings, string cassMonitorPort = "", string cassBootPort = "")
            {
                Image = image;
                ContainerName = containerName;
                CpuCores = cpuCores;
                MemoryLimitMb = memoryLimitMb;
                PortMappings = portMappings;
                CassMonitorPort = cassMonitorPort ?? string.Empty;
                CassBootPort = cassBootPort ?? string.Empty;
            }

            public string Image { get; }
            public string ContainerName { get; }
            public double CpuCores { get; }
            public int MemoryLimitMb { get; }
            public List<PortMapping> PortMappings { get; }
            public string CassMonitorPort { get; }
            public string CassBootPort { get; }
            public bool RequiresCassConfigUpdate
            {
                get
                {
                    return !string.IsNullOrWhiteSpace(CassMonitorPort) &&
                           !string.IsNullOrWhiteSpace(CassBootPort);
                }
            }
        }

        private sealed class ContainerStressTestRequest
        {
            public ContainerStressTestRequest(string image, string prefix, bool runContainers, int maxCount, int batchSize, double cpus, int memoryMb, bool cleanupAfterTest, int cassMonitorStartPort = 0, int cassBootStartPort = 0)
            {
                Image = image;
                Prefix = prefix;
                RunContainers = runContainers;
                MaxCount = maxCount;
                BatchSize = batchSize;
                Cpus = cpus;
                MemoryMb = memoryMb;
                CleanupAfterTest = cleanupAfterTest;
                CassMonitorStartPort = cassMonitorStartPort;
                CassBootStartPort = cassBootStartPort;
            }

            public string Image { get; }
            public string Prefix { get; }
            public bool RunContainers { get; }
            public int MaxCount { get; }
            public int BatchSize { get; }
            public double Cpus { get; }
            public int MemoryMb { get; }
            public bool CleanupAfterTest { get; }
            public int CassMonitorStartPort { get; }
            public int CassBootStartPort { get; }
            public bool RequiresCassConfigUpdate
            {
                get
                {
                    return CassMonitorStartPort > 0 && CassBootStartPort > 0;
                }
            }
        }

        private sealed class PortInputRow
        {
            public PortInputRow(string containerPort, TextBox hostPortBox)
            {
                ContainerPort = containerPort;
                HostPortBox = hostPortBox;
            }

            public string ContainerPort { get; }
            public TextBox HostPortBox { get; }
        }

        private sealed class PortMapping
        {
            public PortMapping(string hostPort, string containerPort)
            {
                HostPort = hostPort;
                ContainerPort = containerPort;
            }

            public string HostPort { get; }
            public string ContainerPort { get; }
        }

        private sealed class DockerContainerLimitInfo
        {
            public static readonly DockerContainerLimitInfo Empty = new DockerContainerLimitInfo("-", "-");

            public DockerContainerLimitInfo(string cpuCoresText, string memoryLimitText)
            {
                CpuCoresText = cpuCoresText;
                MemoryLimitText = memoryLimitText;
            }

            public string CpuCoresText { get; }
            public string MemoryLimitText { get; }
        }

        private enum ContainerReadMode
        {
            LightweightSummary,
            FullDetails
        }

        private enum SnapshotApplyMode
        {
            ReplaceAll,
            MergeDevices
        }

        private sealed class LightweightDockerData
        {
            public LightweightDockerData(
                List<DockerImageInfo> images,
                int runningContainerCount,
                double cpuUsagePercent,
                double memoryUsagePercent,
                double diskUsagePercent)
            {
                Images = images ?? new List<DockerImageInfo>();
                RunningContainerCount = runningContainerCount;
                CpuUsagePercent = cpuUsagePercent;
                MemoryUsagePercent = memoryUsagePercent;
                DiskUsagePercent = diskUsagePercent;
            }

            public List<DockerImageInfo> Images { get; }
            public int RunningContainerCount { get; }
            public double CpuUsagePercent { get; }
            public double MemoryUsagePercent { get; }
            public double DiskUsagePercent { get; }
        }

        private sealed class DockerRuntimeSnapshot
        {
            public DockerRuntimeSnapshot(
                List<DeviceInfo> devices,
                List<DeviceImageRow> deviceImageRows,
                List<ContainerComposeRow> containerRows,
                Dictionary<string, string> deviceContexts,
                ContainerReadMode containerReadMode)
            {
                Devices = devices;
                DeviceImageRows = deviceImageRows;
                ContainerRows = containerRows;
                DeviceContexts = deviceContexts;
                ContainerReadMode = containerReadMode;
            }

            public List<DeviceInfo> Devices { get; }
            public List<DeviceImageRow> DeviceImageRows { get; }
            public List<ContainerComposeRow> ContainerRows { get; }
            public Dictionary<string, string> DeviceContexts { get; }
            public ContainerReadMode ContainerReadMode { get; }
        }

        private sealed class DockerContextInfo
        {
            public DockerContextInfo(string name, string dockerHost)
            {
                Name = name;
                DockerHost = dockerHost;
            }

            public string Name { get; }
            public string DockerHost { get; }
        }

        private sealed class DockerImageInfo
        {
            public DockerImageInfo(string id, string repository, string tag, string createdAt, string size)
            {
                Id = id;
                Repository = repository;
                Tag = tag;
                CreatedAt = createdAt;
                Size = size;
            }

            public string Id { get; }
            public string Repository { get; }
            public string Tag { get; }
            public string CreatedAt { get; }
            public string Size { get; }
        }

        private sealed class DockerContainerInfo
        {
            public DockerContainerInfo(string id, string name, string image, string ports, string status, string state, string size)
            {
                Id = id;
                Name = name;
                Image = image;
                Ports = ports;
                Status = status;
                State = state;
                Size = size;
            }

            public string Id { get; }
            public string Name { get; }
            public string Image { get; }
            public string Ports { get; }
            public string Status { get; }
            public string State { get; }
            public string Size { get; }
        }

        private sealed class DockerContainerStatsInfo
        {
            public DockerContainerStatsInfo(double cpuPercent, string cpuPercentText, string memoryUsageText, string memoryPercentText, string blockIoText)
            {
                CpuPercent = cpuPercent;
                CpuPercentText = cpuPercentText;
                MemoryUsageText = memoryUsageText;
                MemoryPercentText = memoryPercentText;
                BlockIoText = blockIoText;
            }

            public double CpuPercent { get; }
            public string CpuPercentText { get; }
            public string MemoryUsageText { get; }
            public string MemoryPercentText { get; }
            public string BlockIoText { get; }
        }
    }

}
