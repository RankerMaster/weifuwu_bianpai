using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Shapes;

namespace WpfApp1
{
    public partial class MainWindow : Window
    {
        private DeviceInfo _currentDevice;

        private readonly List<DeviceInfo> _devices = new List<DeviceInfo>
        {
            new DeviceInfo("设备1", "192.168.0.38", 2.53, 65.00, 90.44, 30, 5),
            new DeviceInfo("设备2", "192.168.0.40", 12.70, 44.12, 58.30, 21, 3),
            new DeviceInfo("设备3", "192.168.0.46", 33.18, 25.37, 41.06, 17, 2),
            new DeviceInfo("设备4", "192.168.0.52", 7.40, 72.50, 20.10, 28, 4)
        };

        private readonly List<DeviceImageRow> _allImageRows = new List<DeviceImageRow>
        {
            new DeviceImageRow("192.168.0.38", "yunyoujun/cook", "latest", "2025-01-24 11:41:38", "41.93MB", "maintainer=NGINX", "01e4c695535c1bdf2aaeabfd124b31...", "云友厨房镜像"),
            new DeviceImageRow("192.168.0.38", "lovasoa/sqlpage", "latest", "2025-03-23 07:08:54", "25.97MB", "org.opencontainers.image.created=2025-03-22", "03cea01526bd8919fe06711c6de3f9...", "SQL 页面镜像"),
            new DeviceImageRow("192.168.0.38", "minio/minio", "latest", "2024-10-03 05:38:53", "157.60MB", "architecture=x86_64", "162489e21d268e1d54066abf0e5e0...", "对象存储镜像"),
            new DeviceImageRow("192.168.0.38", "amir20/dozzle", "latest", "2025-06-17 00:25:23", "54.51MB", "org.opencontainers.image.created=2025-06-16", "1e8d952886d1a98a6b512d4ba2a1...", "日志查看镜像"),
            new DeviceImageRow("192.168.0.40", "mysql", "5.7.6", "2015-03-31 04:15:40", "344.18MB", "", "2a2a35106ec50fcf259525be5302bd...", "MySQL 数据库"),
            new DeviceImageRow("192.168.0.40", "ghcr.io/coleifer/sqlite-web", "latest", "2025-03-27 21:23:02", "125.43MB", "", "4b498e295525a9c71113d5c35754c...", "SQLite 管理页"),
            new DeviceImageRow("192.168.0.40", "esperotech/yaade", "latest", "2025-04-02 17:18:05", "560.76MB", "", "5314a390e9f6f65bb3b739813f7784...", "API 调试平台"),
            new DeviceImageRow("192.168.0.46", "cars10/elasticsearch", "latest", "2024-12-22 18:08:08", "58.89MB", "maintainer=NGINX", "5603de0f9483bd234f6677e722bab...", "搜索引擎镜像"),
            new DeviceImageRow("192.168.0.46", "netdata/netdata", "latest", "2025-06-02 10:32:11", "681.27MB", "org.opencontainers.image.authors=Netdatabot", "569338efdcba57155762201daa4e...", "监控面板镜像"),
            new DeviceImageRow("192.168.0.46", "postgres", "12", "2022-01-04 09:20:14", "353.55MB", "", "58bff76313464ab65dcb0b9a2b910...", "PostgreSQL 数据库"),
            new DeviceImageRow("192.168.0.52", "chaozhu/easynode", "latest", "2024-10-17 23:57:37", "163.46MB", "", "5d731f4c94c3ce77c0f3416c69af1c0...", "节点管理镜像"),
            new DeviceImageRow("192.168.0.52", "swr.ap-southeast-1.myhuaweicloud", "latest", "2025-04-19 07:07:14", "189.58MB", "", "60513adac99894bd3aa4a1b31e568...", "华为云仓库镜像"),
            new DeviceImageRow("192.168.0.52", "wenyang0/codecv", "latest", "2023-08-17 15:48:50", "58.57MB", "", "747df3bcf031b14a8ce8de38b852e...", "代码转换镜像")
        };
        private readonly List<DeployImageCard> _strategyImages = new List<DeployImageCard>
        {
            new DeployImageCard("custom_image_20251217144522:l", "7a348006a69e", "2025-12-17 14:45", "815 MB"),
            new DeployImageCard("custom_image_20251214103810:l", "9d94db52a229", "2025-12-11 08:52", "1332 MB"),
            new DeployImageCard("custom_image_20251214104824:l", "77a8edbe31b3", "2025-12-11 08:52", "1332 MB"),
            new DeployImageCard("custom_image_20251211085233:l", "f84aa5fb4d18", "2025-12-11 08:52", "1332 MB"),
            new DeployImageCard("custom_image_20251214104635:l", "9435c3e71c25", "2025-12-11 08:52", "1332 MB"),
            new DeployImageCard("temp1:ubuntu20.04", "5c5d8a2ebde6", "2025-12-10 16:58", "774 MB"),
            new DeployImageCard("test1:latest", "7d4a5c5db7fa", "2025-12-10 11:27", "1332 MB"),
            new DeployImageCard("custom_image_20251211105919:l", "2f73b3909192", "2025-12-10 11:27", "1332 MB"),
            new DeployImageCard("temp2:ubuntu20.04", "edd7288ecb96", "2025-12-10 11:22", "774 MB"),
            new DeployImageCard("dslim/slim:latest", "27859c6a5666", "2025-12-09 09:18", "612 MB")
        };
        private readonly List<ImageComposeRow> _sourceImages = new List<ImageComposeRow>
        {
            new ImageComposeRow("temp1:ubuntu20.04", "5c5d8a2ebde6", "2025-12-10 16:58", "774.9"),
            new ImageComposeRow("test1:latest", "7d4a5c5db7fa", "2025-12-10 11:27", "1332.2"),
            new ImageComposeRow("temp2:ubuntu20.04", "edd7288ecb96", "2025-12-10 11:22", "774.5"),
            new ImageComposeRow("dslim/slim:latest", "27859c6a5666", "2024-02-02 23:19", "72.1")
        };
        private readonly List<ImageComposeRow> _preparedImages = new List<ImageComposeRow>
        {
            new ImageComposeRow("custom_image_20251217144522:l", "7a348006a69e", "2025-12-17 14:45", "815.9"),
            new ImageComposeRow("custom_image_20251214104635:l", "9435c3e71c25", "2025-12-11 08:52", "1332.6"),
            new ImageComposeRow("custom_image_20251214103810:l", "9d94db52a229", "2025-12-11 08:52", "1332.6"),
            new ImageComposeRow("custom_image_20251214104824:l", "77a8ed8e31b3", "2025-12-11 08:52", "1332.6"),
            new ImageComposeRow("custom_image_20251211085233:l", "f84aa5fb4d18", "2025-12-11 08:52", "1332.6"),
            new ImageComposeRow("custom_image_20251211105919:l", "2f33b9901992", "2025-12-10 11:27", "1332.2")
        };
        private readonly List<ProgramFileRow> _programFiles = new List<ProgramFileRow>
        {
            new ProgramFileRow("Cass-Lib-20250717.tgz", "102.38 MB", "2025-09-11 17:23"),
            new ProgramFileRow("VTTD-ABox300-Fuler-v2.4.0.tgz", "9.15 MB", "2025-09-11 17:21")
        };
        private readonly List<ContainerComposeRow> _containerRows = new List<ContainerComposeRow>
        {
            new ContainerComposeRow("13d61d8cb2a045289d5bedbba1...", "/angry_bose", "myimage:latest", "exited", "Exited (127) 6 days ago"),
            new ContainerComposeRow("916683c34a897d8051cabdb046...", "/mysql-test", "mysql:8.0", "exited", "Exited (0) 6 days ago"),
            new ContainerComposeRow("6f93c32cedb39dd3846b98ee73...", "/mystifying_karaman", "custom_image_20251211...", "exited", "Exited (0) 3 months ago"),
            new ContainerComposeRow("1ef87c52f83785f9f7e045b97f...", "/ftp_test", "test", "exited", "Exited (137) 4 months ago"),
            new ContainerComposeRow("67dde210fc302db6e0b4eb75...", "/silly_joliot", "cass-image:latest", "exited", "Exited (0) 3 months ago"),
            new ContainerComposeRow("13a3cf9d25a5e3b4438d2d826b...", "/laughing_albatta...", "myimge", "exited", "Exited (0) 4 months ago")
        };

        public MainWindow()
        {
            InitializeComponent();
            InitializeDevices();
            StrategyImageItemsControl.ItemsSource = _strategyImages;
            SourceImageGrid.ItemsSource = _sourceImages;
            PreparedImageGrid.ItemsSource = _preparedImages;
            ProgramFileGrid.ItemsSource = _programFiles;
            ContainerComposeGrid.ItemsSource = _containerRows;
            ContainerDeviceSelector.ItemsSource = _devices.Select(d => d.Name).ToList();
            ContainerDeviceSelector.SelectedIndex = 0;
        }

        private void InitializeDevices()
        {
            DeviceSelector.Items.Clear();
            DevicePreviewPanel.Children.Clear();

            foreach (var device in _devices)
            {
                var item = new ComboBoxItem
                {
                    Content = string.Format("{0} ({1})", device.Name, device.Ip),
                    Tag = device
                };
                DeviceSelector.Items.Add(item);
                DevicePreviewPanel.Children.Add(CreateDevicePreviewText(device));
            }

            RunningDeviceCountText.Text = string.Format("{0}个", _devices.Count);
            DeviceSelector.SelectedIndex = 0;
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

            var device = selected.Tag as DeviceInfo;
            if (device == null)
            {
                return;
            }

            UpdateDeviceDashboard(device);
            BindImageRows(device.Ip);
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

        private void BindImageRows(string ip)
        {
            DeviceImageGrid.ItemsSource = _allImageRows.Where(row => row.DeviceIp == ip).ToList();
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

            const double centerX = 145d;
            const double centerY = 145d;
            const double radius = 138d;

            CpuSlicePath.Data = CreatePieSlice(centerX, centerY, radius, 0, cpuSweep);
            MemorySlicePath.Data = CreatePieSlice(centerX, centerY, radius, cpuSweep, memorySweep);
            DiskSlicePath.Data = CreatePieSlice(centerX, centerY, radius, cpuSweep + memorySweep, diskSweep);
        }

        private void SetPieTooltips(double cpu, double memory, double disk)
        {
            CpuSlicePath.ToolTip = string.Format("CPU 使用率: {0:F2}%", cpu);
            MemorySlicePath.ToolTip = string.Format("内存使用率: {0:F2}%", memory);
            DiskSlicePath.ToolTip = string.Format("磁盘使用率: {0:F2}%", disk);
        }

        private void PieSlice_OnMouseEnter(object sender, MouseEventArgs e)
        {
            UpdatePieDetailBySlice(sender as Path);
        }

        private void PieSlice_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            UpdatePieDetailBySlice(sender as Path);
        }

        private void UpdatePieDetailBySlice(Path slice)
        {
            if (slice == null || _currentDevice == null)
            {
                return;
            }

            if (slice == CpuSlicePath)
            {
                PieDetailText.Text = string.Format("CPU 使用率: {0:F2}%", _currentDevice.CpuUsage);
                return;
            }

            if (slice == MemorySlicePath)
            {
                PieDetailText.Text = string.Format("内存使用率: {0:F2}%", _currentDevice.MemoryUsage);
                return;
            }

            if (slice == DiskSlicePath)
            {
                PieDetailText.Text = string.Format("磁盘使用率: {0:F2}%", _currentDevice.DiskUsage);
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

        private void AwarenessButton_OnClick(object sender, RoutedEventArgs e)
        {
            ShowDashboardPanel();
        }

        private void ServiceImageButton_OnClick(object sender, RoutedEventArgs e)
        {
            ShowServiceImagePanel();
        }

        private void ServiceContainerButton_OnClick(object sender, RoutedEventArgs e)
        {
            ShowServiceContainerPanel();
        }

        private void StrategyButton_OnClick(object sender, RoutedEventArgs e)
        {
            DashboardTopPanel.Visibility = Visibility.Collapsed;
            DashboardMainPanel.Visibility = Visibility.Collapsed;
            ServiceImagePanel.Visibility = Visibility.Collapsed;
            ServiceContainerPanel.Visibility = Visibility.Collapsed;
            StrategyPanel.Visibility = Visibility.Visible;
        }

        private void ShowDashboardPanel()
        {
            StrategyPanel.Visibility = Visibility.Collapsed;
            ServiceImagePanel.Visibility = Visibility.Collapsed;
            ServiceContainerPanel.Visibility = Visibility.Collapsed;
            DashboardTopPanel.Visibility = Visibility.Visible;
            DashboardMainPanel.Visibility = Visibility.Visible;
        }

        private void ShowServiceImagePanel()
        {
            DashboardTopPanel.Visibility = Visibility.Collapsed;
            DashboardMainPanel.Visibility = Visibility.Collapsed;
            StrategyPanel.Visibility = Visibility.Collapsed;
            ServiceContainerPanel.Visibility = Visibility.Collapsed;
            ServiceImagePanel.Visibility = Visibility.Visible;
        }

        private void ShowServiceContainerPanel()
        {
            DashboardTopPanel.Visibility = Visibility.Collapsed;
            DashboardMainPanel.Visibility = Visibility.Collapsed;
            StrategyPanel.Visibility = Visibility.Collapsed;
            ServiceImagePanel.Visibility = Visibility.Collapsed;
            ServiceContainerPanel.Visibility = Visibility.Visible;
        }
    }

    public sealed class DeviceInfo
    {
        public DeviceInfo(string name, string ip, double cpuUsage, double memoryUsage, double diskUsage, int imageCount, int containerCount)
        {
            Name = name;
            Ip = ip;
            CpuUsage = cpuUsage;
            MemoryUsage = memoryUsage;
            DiskUsage = diskUsage;
            ImageCount = imageCount;
            ContainerCount = containerCount;
        }

        public string Name { get; }
        public string Ip { get; }
        public double CpuUsage { get; }
        public double MemoryUsage { get; }
        public double DiskUsage { get; }
        public int ImageCount { get; }
        public int ContainerCount { get; }
    }

    public sealed class DeviceImageRow
    {
        public DeviceImageRow(string deviceIp, string repository, string tag, string created, string size, string labels, string imageId, string chineseName)
        {
            DeviceIp = deviceIp;
            Repository = repository;
            Tag = tag;
            Created = created;
            Size = size;
            Labels = labels;
            ImageId = imageId;
            ChineseName = chineseName;
        }

        public string DeviceIp { get; }
        public string Repository { get; }
        public string Tag { get; }
        public string Created { get; }
        public string Size { get; }
        public string Labels { get; }
        public string ImageId { get; }
        public string ChineseName { get; }
    }

    public sealed class DeployImageCard
    {
        public DeployImageCard(string name, string id, string created, string size)
        {
            Name = name;
            Id = id;
            Created = created;
            Size = size;
        }

        public string Name { get; }
        public string Id { get; }
        public string Created { get; }
        public string Size { get; }
    }

    public sealed class ImageComposeRow
    {
        public ImageComposeRow(string repoTag, string imageId, string created, string sizeMb)
        {
            RepoTag = repoTag;
            ImageId = imageId;
            Created = created;
            SizeMb = sizeMb;
        }

        public string RepoTag { get; }
        public string ImageId { get; }
        public string Created { get; }
        public string SizeMb { get; }
    }

    public sealed class ProgramFileRow
    {
        public ProgramFileRow(string name, string size, string modifiedAt)
        {
            Name = name;
            Size = size;
            ModifiedAt = modifiedAt;
        }

        public string Name { get; }
        public string Size { get; }
        public string ModifiedAt { get; }
    }

    public sealed class ContainerComposeRow
    {
        public ContainerComposeRow(string id, string name, string image, string status, string detail)
        {
            Id = id;
            Name = name;
            Image = image;
            Status = status;
            Detail = detail;
        }

        public string Id { get; }
        public string Name { get; }
        public string Image { get; }
        public string Status { get; }
        public string Detail { get; }
    }
}
