using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace WpfApp1
{
    public partial class MainWindow
    {
        private bool _isServiceImageActionRunning;
        private bool _isServiceImageSelectionSyncing;
        private string _stickySelectedBaseRepoTag = string.Empty;
        private readonly HashSet<string> _stickySelectedProgramKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _stickySelectedPreparedImageKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private enum ServiceImageAction
        {
            Unknown,
            Create,
            Download,
            Deploy,
            Publish,
            Optimize,
            Delete,
            RewriteJson
        }

        private void ServiceImageButton_OnClick(object sender, RoutedEventArgs e)
        {
            ShowServiceImagePanel();
            SetSidebarSelected("ServiceImage");
            ResetServiceImageActionState();
            _ = RefreshRuntimeDataBindingsAsync(false, false);
        }

        private void ShowServiceImagePanel()
        {
            DashboardTopPanel.Visibility = Visibility.Collapsed;
            DashboardMainPanel.Visibility = Visibility.Collapsed;
            StrategyPanel.Visibility = Visibility.Collapsed;
            ServiceContainerPanel.Visibility = Visibility.Collapsed;
            ServiceImagePanel.Visibility = Visibility.Visible;

            // 不依赖“读取设备信息”，进入页面即优先展示本地基础镜像与程序包目录内容。
            RefreshLocalServiceImageLists();
            BindServiceImageRows(GetSelectedServiceImageDeviceName());
            BindProgramFileGridWithSelection(
                ProgramFileGrid,
                _programFiles
                    .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList());
        }

        private void ServiceImageDeviceSelector_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            BindServiceImageRows(GetSelectedServiceImageDeviceName());
        }

        private string GetSelectedServiceImageDeviceName()
        {
            var selected = ServiceImageDeviceSelector.SelectedItem as ComboBoxItem;
            if (selected == null)
            {
                return string.Empty;
            }

            if ((selected.Tag as string) == "ALL")
            {
                return string.Empty;
            }

            var device = selected.Tag as DeviceInfo;
            return device == null ? string.Empty : device.Name;
        }

        private void BindServiceImageRows(string deviceName)
        {
            BindImageGridWithSelection(SourceImageGrid, SortImageComposeRows(_sourceImages));
            BindPreparedImageGridWithSelection(PreparedImageGrid, SortImageComposeRows(_preparedImages));
        }

        private void SourceImageGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selected = SourceImageGrid == null ? null : SourceImageGrid.SelectedItem as ImageComposeRow;
            if (selected == null)
            {
                _stickySelectedBaseRepoTag = string.Empty;
                return;
            }

            if (!_isServiceImageSelectionSyncing)
            {
                _isServiceImageSelectionSyncing = true;
                try
                {
                    _stickySelectedPreparedImageKeys.Clear();
                    ClearDataGridSelection(PreparedImageGrid);
                }
                finally
                {
                    _isServiceImageSelectionSyncing = false;
                }
            }

            _stickySelectedBaseRepoTag = NormalizeRepoTag(selected.RepoTag);
        }

        private void ToggleDataGridRowSelection_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var grid = sender as DataGrid;
            if (grid == null)
            {
                return;
            }

            var source = e.OriginalSource as DependencyObject;
            var cellSource = source;
            while (cellSource != null && !(cellSource is DataGridCell))
            {
                cellSource = VisualTreeHelper.GetParent(cellSource);
            }

            var cell = cellSource as DataGridCell;
            if (cell != null && IsChineseNameColumn(cell.Column))
            {
                return;
            }

            while (source != null && !(source is DataGridRow))
            {
                source = VisualTreeHelper.GetParent(source);
            }

            var row = source as DataGridRow;
            if (row == null)
            {
                return;
            }

            if (!row.IsSelected)
            {
                return;
            }

            row.IsSelected = false;
            if (ReferenceEquals(grid.SelectedItem, row.Item))
            {
                grid.SelectedItem = null;
            }

            e.Handled = true;
        }

        private static System.Collections.Generic.List<ImageComposeRow> SortImageComposeRows(System.Collections.Generic.IEnumerable<ImageComposeRow> rows)
        {
            return rows
                .OrderBy(r => r.RepoTag, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.ImageId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string BuildImageRowKey(ImageComposeRow row)
        {
            if (row == null)
            {
                return string.Empty;
            }

            return string.Format(
                "{0}|{1}|{2}|{3}",
                row.DeviceName ?? string.Empty,
                row.ContextName ?? string.Empty,
                row.RepoTag ?? string.Empty,
                row.ImageId ?? string.Empty);
        }

        private void DeviceImageGrid_OnCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (!IsChineseNameColumn(e.Column))
            {
                return;
            }

            var row = e.Row == null ? null : e.Row.Item as DeviceImageRow;
            var textBox = e.EditingElement as TextBox;
            if (row == null || textBox == null)
            {
                return;
            }

            var newName = NormalizeImageChineseName(textBox.Text, row.Name);
            var grid = sender as DataGrid;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                row.ChineseName = newName;
                StoreImageChineseName(row, newName);
                RefreshImageChineseNameCells(grid);
            }), DispatcherPriority.Background);
        }

        private void ServiceImageGrid_OnCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (!IsChineseNameColumn(e.Column))
            {
                return;
            }

            var row = e.Row == null ? null : e.Row.Item as ImageComposeRow;
            var textBox = e.EditingElement as TextBox;
            if (row == null || textBox == null)
            {
                return;
            }

            var newName = NormalizeImageChineseName(textBox.Text, row.Name);
            var grid = sender as DataGrid;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                row.ChineseName = newName;
                StoreImageChineseName(row, newName);
                RefreshImageChineseNameCells(grid);
            }), DispatcherPriority.Background);
        }

        private static bool IsChineseNameColumn(DataGridColumn column)
        {
            var header = column == null ? string.Empty : Convert.ToString(column.Header, CultureInfo.InvariantCulture);
            return string.Equals(header, "中文名", StringComparison.OrdinalIgnoreCase);
        }

        private void RefreshImageChineseNameCells(DataGrid grid)
        {
            if (grid == null)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                grid.Items.Refresh();
            }), DispatcherPriority.Background);
        }

        private static string NormalizeImageChineseName(string value, string fallbackName)
        {
            var text = (value ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            return string.IsNullOrWhiteSpace(fallbackName) ? "未命名镜像" : fallbackName.Trim();
        }

        private void StoreImageChineseName(DeviceImageRow row, string chineseName)
        {
            if (row == null)
            {
                return;
            }

            StoreImageChineseName(
                row.ContextName,
                row.DeviceName,
                row.DeviceIp,
                row.Repository,
                row.Tag,
                row.ImageId,
                chineseName);
        }

        private void StoreImageChineseName(ImageComposeRow row, string chineseName)
        {
            if (row == null)
            {
                return;
            }

            StoreImageChineseName(
                row.ContextName,
                row.DeviceName,
                string.Empty,
                row.Name,
                row.Tag,
                row.ImageId,
                chineseName);
        }

        private void StoreImageChineseName(
            string contextName,
            string deviceName,
            string deviceIp,
            string repository,
            string tag,
            string imageId,
            string chineseName)
        {
            var normalizedName = NormalizeImageChineseName(chineseName, repository);
            var imageKey = BuildImageChineseNameImageIdKey(contextName, deviceName, deviceIp, imageId);
            if (!string.IsNullOrWhiteSpace(imageKey))
            {
                _imageChineseNameByImageId[imageKey] = normalizedName;
            }

            var repoTagKey = BuildImageChineseNameRepoTagKey(contextName, deviceName, deviceIp, repository, tag);
            if (!string.IsNullOrWhiteSpace(repoTagKey))
            {
                _imageChineseNameByRepoTag[repoTagKey] = normalizedName;
            }

            ApplyImageChineseNameToMatchingRows(contextName, deviceName, deviceIp, repository, tag, imageId, normalizedName);
            SaveImageChineseNameStore();
        }

        private string ResolveImageChineseName(
            string contextName,
            string deviceName,
            string deviceIp,
            string repository,
            string tag,
            string imageId)
        {
            string chineseName;
            var imageKey = BuildImageChineseNameImageIdKey(contextName, deviceName, deviceIp, imageId);
            if (!string.IsNullOrWhiteSpace(imageKey) &&
                _imageChineseNameByImageId.TryGetValue(imageKey, out chineseName) &&
                !string.IsNullOrWhiteSpace(chineseName))
            {
                return chineseName;
            }

            var repoTagKey = BuildImageChineseNameRepoTagKey(contextName, deviceName, deviceIp, repository, tag);
            if (!string.IsNullOrWhiteSpace(repoTagKey) &&
                _imageChineseNameByRepoTag.TryGetValue(repoTagKey, out chineseName) &&
                !string.IsNullOrWhiteSpace(chineseName))
            {
                return chineseName;
            }

            return BuildChineseImageName(repository);
        }

        private void ApplyStoredImageChineseNamesToCurrentRows()
        {
            for (var i = 0; i < _allImageRows.Count; i++)
            {
                var row = _allImageRows[i];
                row.ChineseName = ResolveImageChineseName(
                    row.ContextName,
                    row.DeviceName,
                    row.DeviceIp,
                    row.Repository,
                    row.Tag,
                    row.ImageId);
            }

            for (var i = 0; i < _sourceImages.Count; i++)
            {
                var row = _sourceImages[i];
                row.ChineseName = ResolveImageChineseName(
                    row.ContextName,
                    row.DeviceName,
                    string.Empty,
                    row.Name,
                    row.Tag,
                    row.ImageId);
            }

            for (var i = 0; i < _preparedImages.Count; i++)
            {
                var row = _preparedImages[i];
                row.ChineseName = ResolveImageChineseName(
                    row.ContextName,
                    row.DeviceName,
                    string.Empty,
                    row.Name,
                    row.Tag,
                    row.ImageId);
            }
        }

        private void ApplyImageChineseNameToMatchingRows(
            string contextName,
            string deviceName,
            string deviceIp,
            string repository,
            string tag,
            string imageId,
            string chineseName)
        {
            for (var i = 0; i < _allImageRows.Count; i++)
            {
                var row = _allImageRows[i];
                if (IsSameImageAliasTarget(row.ContextName, row.DeviceName, row.DeviceIp, row.Repository, row.Tag, row.ImageId, contextName, deviceName, deviceIp, repository, tag, imageId))
                {
                    row.ChineseName = chineseName;
                }
            }

            for (var i = 0; i < _sourceImages.Count; i++)
            {
                var row = _sourceImages[i];
                if (IsSameImageAliasTarget(row.ContextName, row.DeviceName, string.Empty, row.Name, row.Tag, row.ImageId, contextName, deviceName, deviceIp, repository, tag, imageId))
                {
                    row.ChineseName = chineseName;
                }
            }

            for (var i = 0; i < _preparedImages.Count; i++)
            {
                var row = _preparedImages[i];
                if (IsSameImageAliasTarget(row.ContextName, row.DeviceName, string.Empty, row.Name, row.Tag, row.ImageId, contextName, deviceName, deviceIp, repository, tag, imageId))
                {
                    row.ChineseName = chineseName;
                }
            }
        }

        private static bool IsSameImageAliasTarget(
            string rowContextName,
            string rowDeviceName,
            string rowDeviceIp,
            string rowRepository,
            string rowTag,
            string rowImageId,
            string contextName,
            string deviceName,
            string deviceIp,
            string repository,
            string tag,
            string imageId)
        {
            var sameScope =
                (!string.IsNullOrWhiteSpace(rowContextName) && string.Equals(rowContextName, contextName, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(rowDeviceName) && string.Equals(rowDeviceName, deviceName, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(rowDeviceIp) && string.Equals(rowDeviceIp, deviceIp, StringComparison.OrdinalIgnoreCase));
            if (!sameScope)
            {
                return false;
            }

            var normalizedRowImageId = NormalizeImageId(rowImageId);
            var normalizedImageId = NormalizeImageId(imageId);
            if (!string.IsNullOrWhiteSpace(normalizedRowImageId) &&
                !string.IsNullOrWhiteSpace(normalizedImageId) &&
                (normalizedRowImageId.IndexOf(normalizedImageId, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 normalizedImageId.IndexOf(normalizedRowImageId, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return true;
            }

            return string.Equals(rowRepository, repository, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(rowTag, tag, StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildImageChineseNameImageIdKey(string contextName, string deviceName, string deviceIp, string imageId)
        {
            var id = NormalizeImageId(imageId);
            if (string.IsNullOrWhiteSpace(id))
            {
                return string.Empty;
            }

            return BuildImageChineseNameScope(contextName, deviceName, deviceIp) + "|id|" + id;
        }

        private static string BuildImageChineseNameRepoTagKey(string contextName, string deviceName, string deviceIp, string repository, string tag)
        {
            var name = (repository ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var normalizedTag = string.IsNullOrWhiteSpace(tag) ? "latest" : tag.Trim();
            return BuildImageChineseNameScope(contextName, deviceName, deviceIp) + "|repo|" + name + ":" + normalizedTag;
        }

        private static string BuildImageChineseNameScope(string contextName, string deviceName, string deviceIp)
        {
            if (!string.IsNullOrWhiteSpace(contextName))
            {
                return "context:" + contextName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(deviceIp))
            {
                return "ip:" + deviceIp.Trim();
            }

            return "device:" + (deviceName ?? string.Empty).Trim();
        }

        private string GetImageChineseNameStorePath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(appData))
            {
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "image_chinese_names.store");
            }

            return Path.Combine(appData, "WpfApp1", "image_chinese_names.store");
        }

        private void LoadImageChineseNameStore()
        {
            _imageChineseNameByImageId.Clear();
            _imageChineseNameByRepoTag.Clear();

            try
            {
                var path = GetImageChineseNameStorePath();
                if (!File.Exists(path))
                {
                    return;
                }

                var lines = File.ReadAllLines(path, Encoding.UTF8);
                for (var i = 0; i < lines.Length; i++)
                {
                    var parts = lines[i].Split('\t');
                    if (parts.Length != 4 || parts[0] != "v1")
                    {
                        continue;
                    }

                    var key = DecodeImageChineseNameStoreValue(parts[2]);
                    var value = DecodeImageChineseNameStoreValue(parts[3]);
                    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    if (parts[1] == "id")
                    {
                        _imageChineseNameByImageId[key] = value;
                    }
                    else if (parts[1] == "repo")
                    {
                        _imageChineseNameByRepoTag[key] = value;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Load image Chinese name store failed: " + ex.Message);
            }
        }

        private void SaveImageChineseNameStore()
        {
            try
            {
                var lines = new List<string>();
                foreach (var pair in _imageChineseNameByImageId.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                {
                    lines.Add("v1\tid\t" + EncodeImageChineseNameStoreValue(pair.Key) + "\t" + EncodeImageChineseNameStoreValue(pair.Value));
                }

                foreach (var pair in _imageChineseNameByRepoTag.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                {
                    lines.Add("v1\trepo\t" + EncodeImageChineseNameStoreValue(pair.Key) + "\t" + EncodeImageChineseNameStoreValue(pair.Value));
                }

                var path = GetImageChineseNameStorePath();
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllLines(path, lines, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Save image Chinese name store failed: " + ex.Message);
            }
        }

        private static string EncodeImageChineseNameStoreValue(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
        }

        private static string DecodeImageChineseNameStoreValue(string value)
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

        private static void BindImageGridWithSelection(DataGrid grid, System.Collections.Generic.List<ImageComposeRow> rows)
        {
            if (grid == null)
            {
                return;
            }

            var selectedKeys = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < grid.SelectedItems.Count; i++)
            {
                var row = grid.SelectedItems[i] as ImageComposeRow;
                if (row == null)
                {
                    continue;
                }

                var key = BuildImageRowKey(row);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    selectedKeys.Add(key);
                }
            }

            var selectedItem = grid.SelectedItem as ImageComposeRow;
            var focusedKey = BuildImageRowKey(selectedItem);

            grid.ItemsSource = null;
            grid.ItemsSource = rows ?? new System.Collections.Generic.List<ImageComposeRow>();

            if (selectedKeys.Count > 0)
            {
                foreach (var item in grid.Items)
                {
                    var row = item as ImageComposeRow;
                    if (row == null)
                    {
                        continue;
                    }

                    if (selectedKeys.Contains(BuildImageRowKey(row)))
                    {
                        grid.SelectedItems.Add(row);
                    }
                }
            }

            if (grid.SelectedItem == null && !string.IsNullOrWhiteSpace(focusedKey))
            {
                foreach (var item in grid.Items)
                {
                    var row = item as ImageComposeRow;
                    if (row == null)
                    {
                        continue;
                    }

                    if (string.Equals(BuildImageRowKey(row), focusedKey, StringComparison.OrdinalIgnoreCase))
                    {
                        grid.SelectedItem = row;
                        break;
                    }
                }
            }
        }

        private void BindPreparedImageGridWithSelection(DataGrid grid, List<ImageComposeRow> rows)
        {
            if (grid == null)
            {
                return;
            }

            var selectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < grid.SelectedItems.Count; i++)
            {
                var row = grid.SelectedItems[i] as ImageComposeRow;
                if (row == null)
                {
                    continue;
                }

                var key = BuildImageRowKey(row);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    selectedKeys.Add(key);
                }
            }

            var selectedItem = grid.SelectedItem as ImageComposeRow;
            var focusedKey = BuildImageRowKey(selectedItem);

            grid.ItemsSource = null;
            grid.ItemsSource = rows ?? new List<ImageComposeRow>();

            if (selectedKeys.Count > 0)
            {
                foreach (var item in grid.Items)
                {
                    var row = item as ImageComposeRow;
                    if (row == null)
                    {
                        continue;
                    }

                    if (selectedKeys.Contains(BuildImageRowKey(row)))
                    {
                        grid.SelectedItems.Add(row);
                    }
                }
            }

            if (grid.SelectedItem == null && !string.IsNullOrWhiteSpace(focusedKey))
            {
                foreach (var item in grid.Items)
                {
                    var row = item as ImageComposeRow;
                    if (row == null)
                    {
                        continue;
                    }

                    if (string.Equals(BuildImageRowKey(row), focusedKey, StringComparison.OrdinalIgnoreCase))
                    {
                        grid.SelectedItem = row;
                        break;
                    }
                }
            }

            if (grid.SelectedItems.Count > 0)
            {
                _stickySelectedPreparedImageKeys.Clear();
                for (var i = 0; i < grid.SelectedItems.Count; i++)
                {
                    var row = grid.SelectedItems[i] as ImageComposeRow;
                    if (row == null)
                    {
                        continue;
                    }

                    var key = BuildImageRowKey(row);
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        _stickySelectedPreparedImageKeys.Add(key);
                    }
                }
            }
            else
            {
                _stickySelectedPreparedImageKeys.Clear();
            }
        }

        private static string BuildProgramFileKey(ProgramFileRow row)
        {
            if (row == null)
            {
                return string.Empty;
            }

            return string.Format(
                "{0}|{1}|{2}",
                row.Name ?? string.Empty,
                row.Size ?? string.Empty,
                row.ModifiedAt ?? string.Empty);
        }

        private void BindProgramFileGridWithSelection(DataGrid grid, List<ProgramFileRow> rows)
        {
            if (grid == null)
            {
                return;
            }

            var selectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < grid.SelectedItems.Count; i++)
            {
                var row = grid.SelectedItems[i] as ProgramFileRow;
                if (row == null)
                {
                    continue;
                }

                var key = BuildProgramFileKey(row);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    selectedKeys.Add(key);
                }
            }

            var selectedItem = grid.SelectedItem as ProgramFileRow;
            var focusedKey = BuildProgramFileKey(selectedItem);

            grid.ItemsSource = null;
            grid.ItemsSource = rows ?? new List<ProgramFileRow>();

            if (selectedKeys.Count > 0)
            {
                foreach (var item in grid.Items)
                {
                    var row = item as ProgramFileRow;
                    if (row == null)
                    {
                        continue;
                    }

                    if (selectedKeys.Contains(BuildProgramFileKey(row)))
                    {
                        grid.SelectedItems.Add(row);
                    }
                }
            }

            if (grid.SelectedItem == null && !string.IsNullOrWhiteSpace(focusedKey))
            {
                foreach (var item in grid.Items)
                {
                    var row = item as ProgramFileRow;
                    if (row == null)
                    {
                        continue;
                    }

                    if (string.Equals(BuildProgramFileKey(row), focusedKey, StringComparison.OrdinalIgnoreCase))
                    {
                        grid.SelectedItem = row;
                        break;
                    }
                }
            }

            if (grid.SelectedItems.Count > 0)
            {
                _stickySelectedProgramKeys.Clear();
                for (var i = 0; i < grid.SelectedItems.Count; i++)
                {
                    var row = grid.SelectedItems[i] as ProgramFileRow;
                    if (row == null)
                    {
                        continue;
                    }

                    var key = BuildProgramFileKey(row);
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        _stickySelectedProgramKeys.Add(key);
                    }
                }
            }
            else
            {
                _stickySelectedProgramKeys.Clear();
            }
        }

        private void ProgramFileGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ProgramFileGrid == null || ProgramFileGrid.SelectedItems.Count == 0)
            {
                _stickySelectedProgramKeys.Clear();
                return;
            }

            if (!_isServiceImageSelectionSyncing)
            {
                _isServiceImageSelectionSyncing = true;
                try
                {
                    _stickySelectedPreparedImageKeys.Clear();
                    ClearDataGridSelection(PreparedImageGrid);
                }
                finally
                {
                    _isServiceImageSelectionSyncing = false;
                }
            }

            _stickySelectedProgramKeys.Clear();
            for (var i = 0; i < ProgramFileGrid.SelectedItems.Count; i++)
            {
                var row = ProgramFileGrid.SelectedItems[i] as ProgramFileRow;
                if (row == null)
                {
                    continue;
                }

                var key = BuildProgramFileKey(row);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    _stickySelectedProgramKeys.Add(key);
                }
            }
        }

        private void PreparedImageGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PreparedImageGrid == null || PreparedImageGrid.SelectedItems.Count == 0)
            {
                _stickySelectedPreparedImageKeys.Clear();
                return;
            }

            if (!_isServiceImageSelectionSyncing)
            {
                _isServiceImageSelectionSyncing = true;
                try
                {
                    _stickySelectedBaseRepoTag = string.Empty;
                    _stickySelectedProgramKeys.Clear();
                    ClearDataGridSelection(SourceImageGrid);
                    ClearDataGridSelection(ProgramFileGrid);
                }
                finally
                {
                    _isServiceImageSelectionSyncing = false;
                }
            }

            _stickySelectedPreparedImageKeys.Clear();
            for (var i = 0; i < PreparedImageGrid.SelectedItems.Count; i++)
            {
                var row = PreparedImageGrid.SelectedItems[i] as ImageComposeRow;
                if (row == null)
                {
                    continue;
                }

                var key = BuildImageRowKey(row);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    _stickySelectedPreparedImageKeys.Add(key);
                }
            }
        }

        private async void ServiceImageActionButton_OnClick(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null)
            {
                return;
            }

            var action = ResolveServiceImageAction(button);
            if (action == ServiceImageAction.Unknown)
            {
                MessageBox.Show("未识别的镜像操作按钮。", "镜像编排", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_isServiceImageActionRunning)
            {
                return;
            }

            if (action != ServiceImageAction.Create &&
                action != ServiceImageAction.Download &&
                action != ServiceImageAction.Delete &&
                !_hasReadDeviceInfo)
            {
                MessageBox.Show("请先点击“读取设备信息”。", "镜像编排", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var contextName = string.Empty;
            if (action == ServiceImageAction.Optimize)
            {
                contextName = GetSelectedServiceImageContextName();
                if (string.IsNullOrWhiteSpace(contextName))
                {
                    MessageBox.Show("请先在“设备”中选择一个具体设备。", "镜像编排", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }

            List<ImageComposeRow> deployRows = null;
            List<ImageComposeRow> publishRows = null;
            List<ImageComposeRow> deleteRows = null;

            switch (action)
            {
                case ServiceImageAction.Deploy:
                    deployRows = GetSelectedPreparedImageRowsForDeploy();
                    if (deployRows.Count == 0)
                    {
                        MessageBox.Show("部署仅支持镜像列表，请先在镜像列表选中至少一行。", "镜像编排", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                    break;
                case ServiceImageAction.Publish:
                    publishRows = GetSelectedImageRowsForPublish();
                    if (publishRows.Count == 0)
                    {
                        MessageBox.Show("发布前请至少选择一个镜像。", "镜像编排", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                    break;
                case ServiceImageAction.Delete:
                    deleteRows = GetSelectedImageRowsForDelete();
                    if (deleteRows.Count == 0)
                    {
                        MessageBox.Show("请先在基础镜像列表或镜像列表中选中至少一行镜像。", "镜像编排", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                    break;
            }

            _isServiceImageActionRunning = true;
            SetServiceImageActionButtonsEnabled(false);
            try
            {
                switch (action)
                {
                    case ServiceImageAction.Create:
                        await ExecuteCreateImageFromLocalFilesAsync();
                        break;
                    case ServiceImageAction.Download:
                        await ExecuteDownloadImageFromPortalAsync();
                        break;
                    case ServiceImageAction.Deploy:
                        await ExecuteDeployWithDeviceSelectionAsync(deployRows);
                        break;
                    case ServiceImageAction.Publish:
                        await ExecutePublishWithSelectionAsync(publishRows);
                        break;
                    case ServiceImageAction.Optimize:
                        await ExecuteOptimizeAsync(contextName);
                        break;
                    case ServiceImageAction.Delete:
                        await ExecuteDeleteImagesAsync(deleteRows);
                        break;
                    case ServiceImageAction.RewriteJson:
                        await ExecuteRewriteJsonFileAsync();
                        break;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("操作失败：" + ex.Message, "镜像编排", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ResetServiceImageActionState();
            }
        }

        private static ServiceImageAction ResolveServiceImageAction(Button button)
        {
            var name = button.Name ?? string.Empty;
            switch (name)
            {
                case "ServiceImageCreateButton":
                    return ServiceImageAction.Create;
                case "ServiceImageDownloadButton":
                    return ServiceImageAction.Download;
                case "ServiceImageDeployButton":
                    return ServiceImageAction.Deploy;
                case "ServiceImagePublishButton":
                    return ServiceImageAction.Publish;
                case "ServiceImageDeleteButton":
                    return ServiceImageAction.Delete;
                case "ServiceImageRewriteJsonButton":
                    return ServiceImageAction.RewriteJson;
            }

            var content = (button.Content as string ?? string.Empty).Trim();
            if (content == "新建镜像") return ServiceImageAction.Create;
            if (content == "下载镜像") return ServiceImageAction.Download;
            if (content == "部署") return ServiceImageAction.Deploy;
            if (content == "发布") return ServiceImageAction.Publish;
            if (content == "优化") return ServiceImageAction.Optimize;
            if (content == "删除镜像") return ServiceImageAction.Delete;
            if (content == "模型部署") return ServiceImageAction.RewriteJson;
            return ServiceImageAction.Unknown;
        }

        private void ResetServiceImageActionState()
        {
            _isServiceImageActionRunning = false;
            SetServiceImageActionButtonsEnabled(true);
            RestoreMainWindowFocus();
        }

        private void SetServiceImageActionButtonsEnabled(bool enabled)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetServiceImageActionButtonsEnabled(enabled));
                return;
            }

            if (ServiceImageDeployButton != null)
            {
                ServiceImageDeployButton.IsEnabled = enabled;
            }

            if (ServiceImagePublishButton != null)
            {
                ServiceImagePublishButton.IsEnabled = enabled;
            }

            if (ServiceImageCreateButton != null)
            {
                ServiceImageCreateButton.IsEnabled = true;
            }

            if (ServiceImageDownloadButton != null)
            {
                ServiceImageDownloadButton.IsEnabled = true;
            }

            if (ServiceImageDeleteButton != null)
            {
                ServiceImageDeleteButton.IsEnabled = enabled;
            }

        }

        private async Task ExecuteRewriteJsonFileAsync()
        {
            var contextName = GetSelectedContainerContextName();
            if (string.IsNullOrWhiteSpace(contextName))
            {
                MessageBox.Show(this, "未找到当前设备对应的 Docker 上下文。", "容器编排", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var selectedRows = ContainerComposeGrid == null
                ? new List<ContainerComposeRow>()
                : ContainerComposeGrid.SelectedItems
                    .Cast<object>()
                    .Select(item => item as ContainerComposeRow)
                    .Where(row => row != null)
                    .ToList();
            if (selectedRows.Count == 0)
            {
                MessageBox.Show(this, "请先选中至少一个容器。", "容器编排", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var fileDialog = new OpenFileDialog
            {
                Title = "选择要写入容器的 JSON 文件",
                Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            };
            if (fileDialog.ShowDialog(this) != true)
            {
                return;
            }

            var localJsonPath = NormalizeLocalPath(fileDialog.FileName);
            if (string.IsNullOrWhiteSpace(localJsonPath) || !File.Exists(localJsonPath))
            {
                MessageBox.Show(this, "所选 JSON 文件不存在。", "容器编排", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 按用户提供的路径图：CASS-Simulator/test.json
            const string targetPathInContainer = "/home/CASS-Simulator/test.json";
            var targetCount = selectedRows.Count;
            var successCount = 0;
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
                    Foreground = new SolidColorBrush(Color.FromRgb(36, 64, 98)),
                    Margin = new Thickness(0, 0, 0, 8),
                    Text = "阶段：准备任务"
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
                    Text = "准备执行容器文件替换...",
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromRgb(42, 53, 68))
                };
                var cancelButton = new Button
                {
                    Content = "取消",
                    Width = 88,
                    Height = 32,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 10, 0, 0)
                };
                cancelButton.Click += (_, __) =>
                {
                    operationCanceled = true;
                    if (progressStageText != null)
                    {
                        progressStageText.Text = "阶段：正在取消";
                    }

                    SetProgressText(progressText, "已请求取消，等待当前步骤结束...");
                    cancelButton.IsEnabled = false;
                };

                var panel = new StackPanel
                {
                    Margin = new Thickness(18, 14, 18, 14)
                };
                panel.Children.Add(progressStageText);
                panel.Children.Add(progressBar);
                panel.Children.Add(progressText);
                panel.Children.Add(cancelButton);
                progressWindow = new Window
                {
                    Title = "正在改写 JSON 文件",
                    Width = 680,
                    Height = 290,
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
                await Dispatcher.Yield(DispatcherPriority.Background);

                if (progressBar != null)
                {
                    progressBar.Value = 8;
                }
                SetProgressText(
                    progressText,
                    string.Format("已选择 {0} 个容器。\n本地文件：{1}\n目标路径：{2}", targetCount, localJsonPath, targetPathInContainer));

                for (var i = 0; i < selectedRows.Count; i++)
                {
                    if (operationCanceled)
                    {
                        break;
                    }

                    var row = selectedRows[i];
                    var containerId = string.IsNullOrWhiteSpace(row.FullId) ? row.Id : row.FullId;
                    if (string.IsNullOrWhiteSpace(containerId))
                    {
                        failed.Add((row.Name ?? "未知容器") + "：容器 ID 为空");
                        continue;
                    }

                    var containerLabel = string.IsNullOrWhiteSpace(row.Name) ? containerId : row.Name;
                    var precheckCmd = string.Format("ps -a --filter \"id={0}\" --format \"{{{{.ID}}}}|{{{{.State}}}}|{{{{.Names}}}}\"", containerId);
                    if (progressStageText != null)
                    {
                        progressStageText.Text = "阶段：校验目标容器";
                    }
                    if (progressBar != null)
                    {
                        var start = 10d + (i * 80d / Math.Max(1, targetCount));
                        progressBar.Value = Math.Max(progressBar.Value, start);
                    }
                    SetProgressText(
                        progressText,
                        string.Format(
                            "正在处理 ({0}/{1})：{2}\n步骤1/3 校验容器\n命令：docker {3}",
                            i + 1,
                            targetCount,
                            containerLabel,
                            precheckCmd));

                    var precheckOut = string.Empty;
                    var precheckOk = await Task.Run(() => TryRunDockerCommand(precheckCmd, contextName, out precheckOut, 20000));
                    if (operationCanceled)
                    {
                        break;
                    }
                    if (!precheckOk || string.IsNullOrWhiteSpace(precheckOut))
                    {
                        var precheckDetail = string.IsNullOrWhiteSpace(precheckOut) ? "容器不存在或无法访问。" : precheckOut.Trim();
                        failed.Add(containerLabel + "：" + precheckDetail);
                        AddContainerOperationLog(contextName, "模型部署", containerId, row.Name, "失败", precheckDetail);
                        continue;
                    }

                    var sourceForDockerCp = localJsonPath;
                    var usedRemoteTempFile = false;
                    var sshIdentity = string.Empty;
                    if (TryResolveSshIdentityForContext(contextName, out sshIdentity) &&
                        !string.IsNullOrWhiteSpace(sshIdentity) &&
                        _sshPasswordByTarget.ContainsKey(sshIdentity))
                    {
                        usedRemoteTempFile = true;
                        sourceForDockerCp = "/tmp/rewrite_json_" + DateTime.Now.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + "_" + i + ".json";
                        if (progressStageText != null)
                        {
                            progressStageText.Text = "阶段：上传 JSON 到远端宿主机";
                        }
                        SetProgressText(
                            progressText,
                            string.Format(
                                "正在处理 ({0}/{1})：{2}\n步骤2/4 上传文件到远端宿主机\n命令：scp {3} {4}:{5}",
                                i + 1,
                                targetCount,
                                containerLabel,
                                localJsonPath,
                                sshIdentity,
                                sourceForDockerCp));

                        var scpDetail = string.Empty;
                        var scpOk = await Task.Run(() => TryCopyLocalFileToRemoteByScp(sshIdentity, localJsonPath, sourceForDockerCp, 120000, out scpDetail));
                        if (operationCanceled)
                        {
                            break;
                        }
                        if (!scpOk)
                        {
                            var uploadDetail = string.IsNullOrWhiteSpace(scpDetail) ? "scp 上传失败。" : scpDetail.Trim();
                            failed.Add(containerLabel + "：" + uploadDetail);
                        AddContainerOperationLog(contextName, "模型部署", containerId, row.Name, "失败", uploadDetail);
                            continue;
                        }
                    }

                    var cpCmd = string.Format(
                        "cp \"{0}\" \"{1}:{2}\"",
                        sourceForDockerCp,
                        containerId,
                        targetPathInContainer);

                    if (progressStageText != null)
                    {
                        progressStageText.Text = "阶段：写入 JSON 文件";
                    }
                    if (progressBar != null)
                    {
                        var mid = 10d + ((i + 0.5d) * 80d / Math.Max(1, targetCount));
                        progressBar.Value = Math.Max(progressBar.Value, mid);
                    }
                    SetProgressText(
                        progressText,
                        string.Format(
                            "正在处理 ({0}/{1})：{2}\n步骤2/3 写入文件\n命令：docker {3}",
                            i + 1,
                            targetCount,
                            containerLabel,
                            cpCmd));

                    var cpOut = string.Empty;
                    var ok = await Task.Run(() => TryRunDockerCommand(cpCmd, contextName, out cpOut, 60000));
                    if (operationCanceled)
                    {
                        break;
                    }
                    if (ok)
                    {
                        if (progressStageText != null)
                        {
                            progressStageText.Text = "阶段：写入结果确认";
                        }
                        if (progressBar != null)
                        {
                            var end = 10d + ((i + 1d) * 80d / Math.Max(1, targetCount));
                            progressBar.Value = Math.Max(progressBar.Value, end);
                        }
                        SetProgressText(
                            progressText,
                            string.Format(
                                "正在处理 ({0}/{1})：{2}\n步骤3/3 完成\n结果：写入成功",
                                i + 1,
                                targetCount,
                                containerLabel));
                        successCount++;
                    AddContainerOperationLog(contextName, "模型部署", containerId, row.Name, "成功", targetPathInContainer);
                    }
                    else
                    {
                        var detail = string.IsNullOrWhiteSpace(cpOut) ? "docker cp 执行失败。" : cpOut.Trim();
                        failed.Add((string.IsNullOrWhiteSpace(row.Name) ? containerId : row.Name) + "：" + detail);
                    AddContainerOperationLog(contextName, "模型部署", containerId, row.Name, "失败", detail);
                    }

                    if (usedRemoteTempFile)
                    {
                        try
                        {
                            string user;
                            string host;
                            string password;
                            if (TryResolveSshTarget(sshIdentity, out user, out host, out password))
                            {
                                string cleanupOut;
                                string cleanupErr;
                                ExecuteSshRemoteCommand(user, host, password, "rm -f \"" + sourceForDockerCp + "\"", 15000, out cleanupOut, out cleanupErr);
                            }
                        }
                        catch
                        {
                        }
                    }
                }

                if (progressStageText != null)
                {
                    progressStageText.Text = "阶段：处理完成";
                }
                if (progressBar != null)
                {
                    progressBar.Value = 100;
                }
                SetProgressText(progressText, string.Format("处理完成。\n成功 {0}/{1}，失败 {2}。", successCount, targetCount, failed.Count));
            }
            finally
            {
                progressClosedByCode = true;
                CloseWindowQuietly(ref progressWindow);
            }

            if (operationCanceled)
            {
                MessageBox.Show(
                    this,
                    string.Format("已取消本次改写。\n已完成：成功 {0}/{1}，失败 {2}。", successCount, targetCount, failed.Count),
                    "容器编排",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (failed.Count == 0)
            {
                MessageBox.Show(
                    this,
                    string.Format("JSON 文件改写完成，成功 {0}/{1}。\n目标路径：{2}", successCount, targetCount, targetPathInContainer),
                    "容器编排",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            ShowScrollableErrorDialog(
                string.Format("JSON 文件改写完成：成功 {0}/{1}，失败 {2}。", successCount, targetCount, failed.Count),
                string.Join("\n", failed));
        }

        private async Task ExecuteDownloadImageFromPortalAsync()
        {
            var downloadsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            var fileDialog = new OpenFileDialog
            {
                Title = "选择镜像 tar 文件",
                Filter = "Tar 文件 (*.tar)|*.tar|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
                InitialDirectory = Directory.Exists(downloadsDir) ? downloadsDir : Environment.CurrentDirectory
            };
            if (fileDialog.ShowDialog(this) != true)
            {
                return;
            }

            var tarPath = NormalizeLocalPath(fileDialog.FileName);
            if (string.IsNullOrWhiteSpace(tarPath) || !File.Exists(tarPath))
            {
                MessageBox.Show("未找到所选 tar 文件。", "下载镜像", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Window progressWindow = null;
            TextBlock progressText = null;
            var operationCanceled = false;
            var progressClosedByCode = false;
            try
            {
                progressWindow = CreateProgressDialog("正在导入镜像");
                progressText = progressWindow.Content as TextBlock;
                progressWindow.Closing += (_, __) =>
                {
                    if (!progressClosedByCode)
                    {
                        operationCanceled = true;
                    }
                };
                progressWindow.Show();
                SetProgressText(progressText, "正在执行 docker load ...");
                await Dispatcher.Yield(DispatcherPriority.Background);
                if (operationCanceled)
                {
                    return;
                }

                var loadOut = string.Empty;
                var loadCmd = string.Format("image load -i \"{0}\"", tarPath);
                var loadOk = await Task.Run(() => TryRunDockerCommand(loadCmd, null, out loadOut, 600000));
                if (operationCanceled)
                {
                    return;
                }
                if (!loadOk)
                {
                    progressClosedByCode = true;
                    CloseWindowQuietly(ref progressWindow);
                    if (!operationCanceled)
                    {
                        ShowScrollableErrorDialog("镜像导入失败", string.IsNullOrWhiteSpace(loadOut) ? "docker load 执行失败。" : loadOut.Trim());
                    }
                    return;
                }

                string loadedRef;
                string loadedId;
                ParseDockerLoadOutput(loadOut, out loadedRef, out loadedId);

                var sourceRef = !string.IsNullOrWhiteSpace(loadedId) ? loadedId : loadedRef;
                if (string.IsNullOrWhiteSpace(sourceRef))
                {
                    progressClosedByCode = true;
                    CloseWindowQuietly(ref progressWindow);
                    if (!operationCanceled)
                    {
                        ShowScrollableErrorDialog(
                            "镜像导入成功但无法自动打标签",
                            "未能从 docker load 输出中识别镜像引用，请手工执行 docker image tag。\n\n输出：\n" + (loadOut ?? string.Empty));
                    }
                    return;
                }

                var repositoryName = BuildRepositoryNameFromTarPath(tarPath);
                var customTag = string.Format("{0}:custom_image_{1}", repositoryName, DateTime.Now.ToString("yyyyMMddHHmmss"));
                SetProgressText(progressText, "正在打 custom_image 标签...");
                var tagOut = string.Empty;
                var tagCmd = string.Format("image tag \"{0}\" \"{1}\"", sourceRef, customTag);
                var tagOk = await Task.Run(() => TryRunDockerCommand(tagCmd, null, out tagOut, 120000));
                if (operationCanceled)
                {
                    return;
                }
                if (!tagOk)
                {
                    progressClosedByCode = true;
                    CloseWindowQuietly(ref progressWindow);
                    if (!operationCanceled)
                    {
                        ShowScrollableErrorDialog("镜像标签设置失败", string.IsNullOrWhiteSpace(tagOut) ? "docker image tag 执行失败。" : tagOut.Trim());
                    }
                    return;
                }

                SetProgressText(progressText, "正在刷新镜像列表...");
                RefreshLocalServiceImageLists();
                if (operationCanceled)
                {
                    return;
                }
                ShowServiceImagePanel();
                SetSidebarSelected("ServiceImage");
                BindServiceImageRows(GetSelectedServiceImageDeviceName());

                progressClosedByCode = true;
                CloseWindowQuietly(ref progressWindow);
                if (!operationCanceled)
                {
                    MessageBox.Show(
                        this,
                        string.Format("导入成功。\n来源文件：{0}\n新标签：{1}", tarPath, customTag),
                        "下载镜像",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
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

        private static void ParseDockerLoadOutput(string output, out string imageRef, out string imageId)
        {
            imageRef = string.Empty;
            imageId = string.Empty;

            var text = output ?? string.Empty;
            var imageMatch = Regex.Match(text, @"Loaded image:\s*(?<ref>[^\r\n]+)", RegexOptions.IgnoreCase);
            if (imageMatch.Success)
            {
                imageRef = (imageMatch.Groups["ref"].Value ?? string.Empty).Trim();
            }

            var idMatch = Regex.Match(text, @"Loaded image ID:\s*(?<id>[^\r\n]+)", RegexOptions.IgnoreCase);
            if (idMatch.Success)
            {
                imageId = (idMatch.Groups["id"].Value ?? string.Empty).Trim();
            }
        }

        private static string BuildRepositoryNameFromTarPath(string tarPath)
        {
            var fileName = Path.GetFileNameWithoutExtension(tarPath ?? string.Empty);
            var normalized = (fileName ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return "custom";
            }

            var sb = new StringBuilder();
            for (var i = 0; i < normalized.Length; i++)
            {
                var ch = normalized[i];
                var isAlphaNum = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9');
                if (isAlphaNum || ch == '-' || ch == '_' || ch == '.')
                {
                    sb.Append(ch);
                }
                else
                {
                    sb.Append('_');
                }
            }

            var value = sb.ToString().Trim('.', '-', '_');
            return string.IsNullOrWhiteSpace(value) ? "custom" : value;
        }

        private async Task ExecuteCreateImageFromLocalFilesAsync()
        {
            var contextDir = GetTempBuildImageDirectory();
            var imageTag = string.Format("local:custom_image_{0}", DateTime.Now.ToString("yyyyMMddHHmmss"));

            var baseImage = SourceImageGrid.SelectedItem as ImageComposeRow;
            if (baseImage == null || string.IsNullOrWhiteSpace(NormalizeRepoTag(baseImage.RepoTag)))
            {
                MessageBox.Show("请先在基础镜像列表中选中一个基础镜像。", "镜像编排", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var selectedPackages = GetSelectedProgramPackages();
            if (selectedPackages.Count == 0)
            {
                MessageBox.Show("请先在程序包列表中至少选中一个程序包。", "镜像编排", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var baseRepoTag = NormalizeRepoTag(baseImage.RepoTag);
            if (string.IsNullOrWhiteSpace(baseRepoTag))
            {
                MessageBox.Show("基础镜像标签无效。", "镜像编排", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var baseImageExists = await ImageExistsInContextAsync(null, baseRepoTag);
            if (!baseImageExists)
            {
                MessageBox.Show("本机 Docker 中不存在选中的基础镜像：" + baseRepoTag, "镜像编排", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var defaultDockerfileContent = BuildGeneratedDockerfile(baseRepoTag, selectedPackages);
            string dockerfileContent;
            if (!ShowDockerfileEditorDialog(defaultDockerfileContent, out dockerfileContent))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(dockerfileContent))
            {
                MessageBox.Show(this, "Dockerfile 内容不能为空。", "镜像编排", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Window progressWindow = null;
            TextBlock progressText = null;
            TextBlock progressStageText = null;
            ProgressBar progressBar = null;
            contextDir = NormalizeLocalPath(contextDir);
            if (string.IsNullOrWhiteSpace(contextDir))
            {
                MessageBox.Show("请输入有效的镜像构建目录。", "镜像编排", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                progressStageText = new TextBlock
                {
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(36, 64, 98)),
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
                    Text = "正在准备构建目录...",
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
                    Title = "正在新建镜像",
                    Width = 620,
                    Height = 240,
                    ResizeMode = ResizeMode.NoResize,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this,
                    Content = panel
                };
                progressWindow.Show();
                progressBar.Value = 10;
                progressStageText.Text = "阶段：准备构建目录";
                SetProgressText(progressText, "正在准备构建目录...");
                await Dispatcher.Yield(DispatcherPriority.Background);

                if (!Directory.Exists(contextDir))
                {
                    Directory.CreateDirectory(contextDir);
                }
                else
                {
                    var existingFiles = Directory.GetFiles(contextDir);
                    for (var i = 0; i < existingFiles.Length; i++)
                    {
                        TryDeleteFileQuietly(existingFiles[i]);
                    }
                }

                string stageError;
                if (!TryStagePackagesToContext(contextDir, selectedPackages, dockerfileContent, out stageError))
                {
                    CloseWindowQuietly(ref progressWindow);
                    MessageBox.Show(this, "新建镜像失败：" + stageError, "镜像编排", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var dockerfilePath = Path.Combine(contextDir, "Dockerfile");
                File.WriteAllText(dockerfilePath, dockerfileContent, new UTF8Encoding(false));

                progressBar.Value = 30;
                progressStageText.Text = "阶段：构建镜像";
                SetProgressText(progressText, "正在执行 docker build（首次构建可能较慢）...");
                var buildOut = string.Empty;
                var buildCmd = string.Format("build --progress=plain -t \"{0}\" \"{1}\"", imageTag, contextDir);
                var buildOk = await Task.Run(() => TryRunDockerCommand(buildCmd, null, out buildOut, 600000));
                if (!buildOk)
                {
                    var reason = string.IsNullOrWhiteSpace(buildOut) ? "未知错误（可能是 Docker 未启动、脚本阻塞或构建超时）" : buildOut.Trim();
                    CloseWindowQuietly(ref progressWindow);
                    ShowScrollableErrorDialog("新建镜像失败：自动生成 Dockerfile 后构建失败。", reason);
                    return;
                }

                progressBar.Value = 82;
                progressStageText.Text = "阶段：导出镜像";
                SetProgressText(progressText, "构建完成，正在导出镜像 tar...");
                var archivePath = Path.Combine(contextDir, BuildImageArchiveFileName(imageTag));
                var saveOut = string.Empty;
                var saveCmd = string.Format("image save \"{0}\" -o \"{1}\"", imageTag, archivePath);
                var saveOk = await Task.Run(() => TryRunDockerCommand(saveCmd, null, out saveOut, 600000));
                if (!saveOk || !File.Exists(archivePath))
                {
                    var reason = string.IsNullOrWhiteSpace(saveOut) ? "未知错误（镜像导出命令执行失败）" : saveOut.Trim();
                    CloseWindowQuietly(ref progressWindow);
                    ShowScrollableErrorDialog("镜像构建成功，但导出镜像文件失败。", reason);
                    return;
                }

                var attributes = File.GetAttributes(archivePath);
                if ((attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                {
                    CloseWindowQuietly(ref progressWindow);
                    MessageBox.Show(this, "导出的镜像文件是链接文件，请检查目录权限后重试。", "镜像编排", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var metaPath = archivePath + ".meta.txt";
                File.WriteAllText(metaPath, imageTag + Environment.NewLine, new UTF8Encoding(false));

                progressBar.Value = 94;
                progressStageText.Text = "阶段：刷新界面";
                SetProgressText(progressText, "正在刷新界面数据...");
                await RefreshRuntimeDataBindingsAsync(true);
                ShowServiceImagePanel();
                SetSidebarSelected("ServiceImage");
                BindServiceImageRows(GetSelectedServiceImageDeviceName());
                ClearCreateSelectionsAfterSuccess();
                progressBar.Value = 100;
                progressStageText.Text = "阶段：完成";
                SetProgressText(progressText, "新建镜像完成。");
                CloseWindowQuietly(ref progressWindow);
                MessageBox.Show(
                    this,
                    string.Format("新建镜像成功：\n构建目录：{0}\n镜像：{1}\nDockerfile：{2}\n镜像实体文件：{3}", contextDir, imageTag, dockerfilePath, archivePath),
                    "镜像编排",
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
            }
        }

        private async Task ExecuteDeployWithDeviceSelectionAsync(List<ImageComposeRow> imageRows)
        {
            string selectedContextName;
            string selectedDeviceName;
            if (!ShowDeployDeviceDialog(out selectedContextName, out selectedDeviceName))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(selectedContextName))
            {
                MessageBox.Show(this, "未选择有效设备。", "镜像编排", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

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

            Window progressWindow = null;
            TextBlock progressText = null;
            TextBlock progressStageText = null;
            ProgressBar progressBar = null;
            var failed = new List<string>();
            var success = new List<string>();
            var operationCanceled = false;
            var progressClosedByCode = false;
            try
            {
                progressStageText = new TextBlock
                {
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(36, 64, 98)),
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
                    Text = "正在准备部署任务...",
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
                    Title = "正在部署中",
                    Width = 620,
                    Height = 240,
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

                for (var i = 0; i < deployList.Count; i++)
                {
                    if (operationCanceled)
                    {
                        return;
                    }

                    var row = deployList[i];
                    var repoTag = NormalizeRepoTag(row.RepoTag);
                    var displayName = string.Format("{0}:{1}", row.Name, row.Tag);
                    Action<string, double> updateProgress = (stage, stagePercent) =>
                    {
                        var normalizedStage = Math.Max(0, Math.Min(1, stagePercent));
                        var overallPercent = ((i + normalizedStage) * 100d) / Math.Max(1, deployList.Count);
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
                                "正在部署中（{0}/{1}）\n当前镜像：{2}\n仓库标签：{3}\n目标设备：{4}\n当前步骤：{5}",
                                i + 1,
                                deployList.Count,
                                displayName,
                                repoTag,
                                selectedDeviceName,
                                stageText));
                    };
                    updateProgress("准备部署", 0);
                    await Dispatcher.Yield(DispatcherPriority.Background);

                    var deployTask = ExecuteDeployAsync(selectedContextName, row, false, updateProgress);
                    var deployCompleted = await Task.WhenAny(deployTask, Task.Delay(300000));
                    var deployError = deployCompleted == deployTask
                        ? await deployTask
                        : "部署失败：执行超时（超过 5 分钟，300 秒）。";
                    if (operationCanceled)
                    {
                        return;
                    }
                    if (string.IsNullOrWhiteSpace(deployError))
                    {
                        updateProgress("部署成功", 1);
                        success.Add(repoTag);
                    }
                    else
                    {
                        updateProgress("部署失败", 1);
                        failed.Add(repoTag + " => " + deployError);
                    }
                }
            }
            finally
            {
                if (progressWindow != null)
                {
                    try
                    {
                        if (!operationCanceled && failed.Count == 0)
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
                                    "部署成功：共 {0} 个镜像已同步到目标设备。\n目标设备：{1}",
                                    success.Count,
                                    selectedDeviceName));
                            await Task.Delay(1200);
                        }

                        progressClosedByCode = true;
                        progressWindow.Close();
                    }
                    catch
                    {
                    }
                }
            }

            if (operationCanceled)
            {
                return;
            }

            await RefreshRuntimeDataBindingsAsync(true);

            if (failed.Count == 0)
            {
                return;
            }

            ShowScrollableErrorDialog(
                string.Format("部署完成：成功 {0}，失败 {1}", success.Count, failed.Count),
                string.Join("\n\n", failed));
        }

        private async Task<string> ExecuteDeployAsync(string contextName, ImageComposeRow imageRow, bool showSuccessDialog = true)
        {
            var report = new Action<string, double>((_, __) => { });
            return await ExecuteDeployAsync(contextName, imageRow, showSuccessDialog, report);
        }

        private async Task<string> ExecuteDeployAsync(string contextName, ImageComposeRow imageRow, bool showSuccessDialog, Action<string, double> progressCallback)
        {
            const int deployTimeoutMs = 300000;
            var report = progressCallback ?? new Action<string, double>((_, __) => { });
            var repoTag = NormalizeRepoTag(imageRow.RepoTag);
            report("检查参数", 0.02);
            if (string.IsNullOrWhiteSpace(repoTag))
            {
                return "镜像仓库标签无效，无法部署。";
            }

            var sshIdentity = string.Empty;
            if (TryResolveSshIdentityForContext(contextName, out sshIdentity) &&
                !string.IsNullOrWhiteSpace(sshIdentity) &&
                !_sshPasswordByTarget.ContainsKey(sshIdentity))
            {
                return string.Format(
                    "当前会话未缓存目标设备 SSH 密码，已中止部署以避免长时间卡住。\n上下文：{0}\n目标：{1}\n请先点击“读取设备信息”并输入密码，再执行部署。",
                    contextName,
                    sshIdentity);
            }

            var deployContextName = contextName;
            if (!string.IsNullOrWhiteSpace(sshIdentity) && _sshPasswordByTarget.ContainsKey(sshIdentity))
            {
                // 与“读取设备信息”一致：部署时优先走 SSH 密码校验通道，避免 context 侧阻塞。
                deployContextName = SshContextPrefix + sshIdentity;
                report(string.Format("已切换 SSH 密码通道：{0}", sshIdentity), 0.06);
            }

            report("开始同步镜像", 0.05);
            var deployTask = EnsureImageExistsOnRemoteWithDetailAsync(deployContextName, repoTag, report);
            var completed = await Task.WhenAny(deployTask, Task.Delay(deployTimeoutMs));
            if (completed != deployTask)
            {
                var timeoutDetailTask = BuildDeployTimeoutDiagnosticsAsync(deployContextName, repoTag, imageRow == null ? string.Empty : imageRow.ImageId);
                var detailCompleted = await Task.WhenAny(timeoutDetailTask, Task.Delay(5000));
                var timeoutDetail = detailCompleted == timeoutDetailTask
                    ? await timeoutDetailTask
                    : "诊断采集超时（5 秒），请手工执行：docker --context \"" + deployContextName + "\" info / images";
                return string.Format(
                    "部署失败：执行超时（超过 5 分钟，300 秒）。\n上下文：{0}\n镜像：{1}\n\n诊断信息：\n{2}",
                    deployContextName,
                    repoTag,
                    timeoutDetail);
            }

            var deployError = await deployTask;
            if (!string.IsNullOrWhiteSpace(deployError))
            {
                return deployError;
            }
            report("镜像同步完成", 0.98);

            if (showSuccessDialog)
            {
                report("刷新界面数据", 0.99);
                await RefreshRuntimeDataBindingsAsync(true);
                MessageBox.Show(
                    this,
                    string.Format("部署成功：已将镜像同步到目标设备。\n镜像：{0}", repoTag),
                    "镜像编排",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            report("完成", 1.0);
            return string.Empty;
        }

        private List<ImageComposeRow> GetSelectedImageRowsForPublish()
        {
            var result = new List<ImageComposeRow>();
            var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (SourceImageGrid != null)
            {
                for (var i = 0; i < SourceImageGrid.SelectedItems.Count; i++)
                {
                    var row = SourceImageGrid.SelectedItems[i] as ImageComposeRow;
                    var tag = NormalizeRepoTag(row == null ? string.Empty : row.RepoTag);
                    if (string.IsNullOrWhiteSpace(tag) || tags.Contains(tag))
                    {
                        continue;
                    }

                    tags.Add(tag);
                    result.Add(row);
                }
            }

            if (PreparedImageGrid != null)
            {
                for (var i = 0; i < PreparedImageGrid.SelectedItems.Count; i++)
                {
                    var row = PreparedImageGrid.SelectedItems[i] as ImageComposeRow;
                    var tag = NormalizeRepoTag(row == null ? string.Empty : row.RepoTag);
                    if (string.IsNullOrWhiteSpace(tag) || tags.Contains(tag))
                    {
                        continue;
                    }

                    tags.Add(tag);
                    result.Add(row);
                }
            }

            return result;
        }

        private bool ShowPublishDialog(out string localDirectory)
        {
            localDirectory = string.Empty;
            using (var dialog = new Forms.FolderBrowserDialog
            {
                Description = "请选择镜像发布保存文件夹",
                ShowNewFolderButton = true
            })
            {
                if (dialog.ShowDialog() != Forms.DialogResult.OK)
                {
                    return false;
                }

                var selectedPath = (dialog.SelectedPath ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(selectedPath))
                {
                    MessageBox.Show("请选择有效的本地保存目录。", "镜像编排", MessageBoxButton.OK, MessageBoxImage.Information);
                    return false;
                }

                localDirectory = selectedPath;
                return true;
            }
        }

        private async Task ExecuteAlgorithmDeployAsync()
        {
            await Task.Yield();

            var jarDialog = new OpenFileDialog
            {
                Title = "选择用于算法部署的 JAR 包",
                Filter = "JAR 文件 (*.jar)|*.jar|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            };
            if (jarDialog.ShowDialog(this) != true)
            {
                return;
            }

            var selectedJarPath = NormalizeLocalPath(jarDialog.FileName);
            if (string.IsNullOrWhiteSpace(selectedJarPath) || !File.Exists(selectedJarPath))
            {
                MessageBox.Show(this, "所选 JAR 文件不存在。", "容器编排", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var workspaceRoot = GetDockerBuildWorkspaceRoot();
            var dockerBuildDir = Path.Combine(workspaceRoot, "tempBuild_image");
            Directory.CreateDirectory(dockerBuildDir);
            var dockerfilePath = Path.Combine(dockerBuildDir, "Dockerfile");
            var jarFileName = Path.GetFileName(selectedJarPath);
            var targetJarPath = Path.Combine(dockerBuildDir, jarFileName);

            File.Copy(selectedJarPath, targetJarPath, true);

            var defaultDockerfile = string.Join("\n", new[]
            {
                "FROM java:8u381",
                "RUN mkdir -p /data/log",
                "COPY mstest-0.0.1.jar /mstest.jar",
                "CMD [\"--server.port=8500\"]",
                "EXPOSE 8500",
                "ENTRYPOINT [\"/usr/local/jdk1.8.0_381/bin/java\", \"-jar\", \"/mstest.jar\"]"
            });

            var dockerfileText = File.Exists(dockerfilePath)
                ? File.ReadAllText(dockerfilePath, Encoding.UTF8)
                : defaultDockerfile;

            var copyPattern = new Regex(@"^\s*COPY\s+\S+\s+/mstest\.jar\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            var copyLine = string.Format("COPY {0} /mstest.jar", jarFileName);
            if (copyPattern.IsMatch(dockerfileText))
            {
                dockerfileText = copyPattern.Replace(dockerfileText, copyLine, 1);
            }
            else
            {
                if (!dockerfileText.EndsWith("\n", StringComparison.Ordinal))
                {
                    dockerfileText += "\n";
                }
                dockerfileText += copyLine + "\n";
            }

            File.WriteAllText(dockerfilePath, dockerfileText, new UTF8Encoding(false));

            MessageBox.Show(
                this,
                string.Format("算法部署文件已更新：\n1) 已复制 JAR：{0}\n2) 已更新 Dockerfile COPY 行：{1}", targetJarPath, copyLine),
                "容器编排",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private async Task ExecutePublishWithSelectionAsync(List<ImageComposeRow> imageRows)
        {
            if (imageRows == null || imageRows.Count == 0)
            {
                MessageBox.Show("发布前请至少选择一个镜像。", "镜像编排", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string localDirectory;
            if (!ShowPublishDialog(out localDirectory))
            {
                return;
            }

            Window progressWindow = null;
            TextBlock progressText = null;
            TextBlock progressStageText = null;
            ProgressBar progressBar = null;
            var success = new List<string>();
            var failed = new List<string>();
            try
            {
                progressStageText = new TextBlock
                {
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(36, 64, 98)),
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
                    Text = "正在准备发布任务...",
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
                    Title = "正在发布中",
                    Width = 620,
                    Height = 240,
                    ResizeMode = ResizeMode.NoResize,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this,
                    Content = panel
                };
                progressWindow.Show();

                for (var i = 0; i < imageRows.Count; i++)
                {
                    var row = imageRows[i];
                    var repoTag = NormalizeRepoTag(row == null ? string.Empty : row.RepoTag);
                    if (string.IsNullOrWhiteSpace(repoTag))
                    {
                        continue;
                    }

                    Action<string, double> report = (stage, percent) =>
                    {
                        var normalizedStage = Math.Max(0, Math.Min(1, percent));
                        var overallPercent = ((i + normalizedStage) * 100d) / Math.Max(1, imageRows.Count);
                        if (progressStageText != null)
                        {
                            progressStageText.Text = string.Format("阶段：{0}（总进度 {1:F0}%）", stage, overallPercent);
                        }

                        if (progressBar != null)
                        {
                            progressBar.Value = Math.Max(0, Math.Min(100, overallPercent));
                        }

                        SetProgressText(
                            progressText,
                            string.Format(
                                "正在发布中（{0}/{1}）\n当前镜像：{2}\n本地保存目录：{3}\n当前步骤：{4}",
                                i + 1,
                                imageRows.Count,
                                repoTag,
                                localDirectory,
                                stage));
                    };

                    var error = await PublishSingleImageArchiveAsync(repoTag, localDirectory, report);
                    if (string.IsNullOrWhiteSpace(error))
                    {
                        success.Add(repoTag);
                    }
                    else
                    {
                        failed.Add(repoTag + " => " + error);
                    }
                }
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

            if (failed.Count == 0)
            {
                MessageBox.Show(
                    string.Format("发布成功：共 {0} 个镜像已保存到本地目录：{1}", success.Count, localDirectory),
                    "镜像编排",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            ShowScrollableErrorDialog(
                string.Format("发布完成：成功 {0}，失败 {1}", success.Count, failed.Count),
                string.Join("\n\n", failed));
        }

        private async Task<string> PublishSingleImageArchiveAsync(string repoTag, string localDirectory, Action<string, double> report)
        {
            var progress = report ?? new Action<string, double>((_, __) => { });
            var targetDir = (localDirectory ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(targetDir))
            {
                return "本地保存目录为空。";
            }

            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            var outputFilePath = Path.Combine(targetDir, BuildImageArchiveFileName(repoTag));
            if (File.Exists(outputFilePath))
            {
                var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(outputFilePath);
                var extension = Path.GetExtension(outputFilePath);
                outputFilePath = Path.Combine(
                    targetDir,
                    fileNameWithoutExtension + "_" + DateTime.Now.ToString("yyyyMMddHHmmss") + extension);
            }

            try
            {
                progress("本机导出镜像（save）", 0.20);
                var localSaveOut = string.Empty;
                var localSaveCmd = string.Format("image save \"{0}\" -o \"{1}\"", repoTag, outputFilePath);
                var localSaveOk = await Task.Run(() => TryRunDockerCommand(localSaveCmd, null, out localSaveOut, 120000));
                if (!localSaveOk)
                {
                    return "本机导出镜像失败：" + (string.IsNullOrWhiteSpace(localSaveOut) ? "(无输出)" : localSaveOut.Trim());
                }
                progress("完成", 1.0);
                return string.Empty;
            }
            catch (Exception ex)
            {
                return "发布失败：" + ex.Message;
            }
        }

        private async Task ExecutePublishAsync(string contextName, ImageComposeRow imageRow)
        {
            string repository;
            string tag;
            SplitRepoTag(NormalizeRepoTag(imageRow.RepoTag), out repository, out tag);
            if (string.IsNullOrWhiteSpace(repository))
            {
                MessageBox.Show("镜像仓库名称无效，无法发布。", "镜像编排", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var releaseTag = "release-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            var publishedRepoTag = repository + ":" + releaseTag;
            var publishCmd = string.Format("tag \"{0}\" \"{1}\"", NormalizeRepoTag(imageRow.RepoTag), publishedRepoTag);

            var publishOut = string.Empty;
            var publishOk = await Task.Run(() => TryRunDockerCommand(publishCmd, contextName, out publishOut));
            if (!publishOk)
            {
                MessageBox.Show("发布失败：镜像打标签失败。", "镜像编排", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await RefreshRuntimeDataBindingsAsync(true);
            MessageBox.Show(
                string.Format("发布成功：{0}\n已生成发布标签：{1}", NormalizeRepoTag(imageRow.RepoTag), publishedRepoTag),
                "镜像编排",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private async Task ExecuteOptimizeAsync(string contextName)
        {
            var pruneOut = string.Empty;
            var pruneOk = await Task.Run(() => TryRunDockerCommand("image prune -f --filter \"dangling=true\"", contextName, out pruneOut));
            if (!pruneOk)
            {
                MessageBox.Show("优化失败：镜像清理命令执行失败。", "镜像编排", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await RefreshRuntimeDataBindingsAsync(true);
            var reclaimed = ExtractReclaimedText(pruneOut);
            MessageBox.Show(
                string.IsNullOrWhiteSpace(reclaimed)
                    ? "优化完成：已执行 dangling 镜像清理。"
                    : "优化完成：" + reclaimed,
                "镜像编排",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private List<ImageComposeRow> GetSelectedPreparedImageRowsForDeploy()
        {
            var result = new List<ImageComposeRow>();
            var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (PreparedImageGrid == null)
            {
                return result;
            }

            for (var i = 0; i < PreparedImageGrid.SelectedItems.Count; i++)
            {
                var row = PreparedImageGrid.SelectedItems[i] as ImageComposeRow;
                var tag = NormalizeRepoTag(row == null ? string.Empty : row.RepoTag);
                if (string.IsNullOrWhiteSpace(tag) || tags.Contains(tag))
                {
                    continue;
                }

                tags.Add(tag);
                result.Add(row);
            }

            if (result.Count == 0)
            {
                var row = PreparedImageGrid.SelectedItem as ImageComposeRow;
                var tag = NormalizeRepoTag(row == null ? string.Empty : row.RepoTag);
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    result.Add(row);
                }
            }

            return result;
        }

        private List<ImageComposeRow> GetSelectedImageRowsForDelete()
        {
            var result = new List<ImageComposeRow>();
            var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (SourceImageGrid != null)
            {
                for (var i = 0; i < SourceImageGrid.SelectedItems.Count; i++)
                {
                    var row = SourceImageGrid.SelectedItems[i] as ImageComposeRow;
                    var tag = NormalizeRepoTag(row == null ? string.Empty : row.RepoTag);
                    if (string.IsNullOrWhiteSpace(tag) || tags.Contains(tag))
                    {
                        continue;
                    }

                    tags.Add(tag);
                    result.Add(row);
                }
            }

            if (PreparedImageGrid != null)
            {
                for (var i = 0; i < PreparedImageGrid.SelectedItems.Count; i++)
                {
                    var row = PreparedImageGrid.SelectedItems[i] as ImageComposeRow;
                    var tag = NormalizeRepoTag(row == null ? string.Empty : row.RepoTag);
                    if (string.IsNullOrWhiteSpace(tag) || tags.Contains(tag))
                    {
                        continue;
                    }

                    tags.Add(tag);
                    result.Add(row);
                }
            }

            if (result.Count == 0)
            {
                var single = GetSelectedServiceImageRow();
                var tag = NormalizeRepoTag(single == null ? string.Empty : single.RepoTag);
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    result.Add(single);
                }
            }

            return result;
        }

        private async void PreparedImageModifyNameMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            var selectedRow = PreparedImageGrid == null ? null : PreparedImageGrid.SelectedItem as ImageComposeRow;
            if (selectedRow == null)
            {
                MessageBox.Show("请先在镜像列表选中一行镜像。", "镜像编排", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var oldRepoTag = NormalizeRepoTag(selectedRow.RepoTag);
            string oldRepository;
            string oldTag;
            SplitRepoTag(oldRepoTag, out oldRepository, out oldTag);
            oldRepository = (oldRepository ?? string.Empty).Trim();
            oldTag = (oldTag ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(oldTag))
            {
                oldTag = "latest";
            }

            if (string.IsNullOrWhiteSpace(oldRepository) || string.Equals(oldRepository, "<none>", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("当前镜像名称无效，无法修改 Name。", "镜像编排", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string newName;
            if (!ShowSingleInputDialog("修改Name", "请输入新的 Name（Repository）：", oldRepository, out newName))
            {
                return;
            }

            var newRepository = (newName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(newRepository))
            {
                MessageBox.Show("Name 不能为空。", "镜像编排", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var newRepoTag = newRepository + ":" + oldTag;
            if (string.Equals(oldRepoTag, newRepoTag, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("新旧 Name:Tag 相同，无需修改。", "镜像编排", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(
                string.Format("确定将镜像从\n{0}\n修改为\n{1}\n吗？", oldRepoTag, newRepoTag),
                "修改Name",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            Window progressWindow = null;
            TextBlock progressText = null;
            try
            {
                progressWindow = CreateProgressDialog("修改Name");
                progressText = progressWindow.Content as TextBlock;
                SetProgressText(progressText, string.Format("正在执行 修改Name...\n{0} -> {1}", oldRepoTag, newRepoTag));
                progressWindow.Show();
                await Dispatcher.Yield(DispatcherPriority.Background);

                var tagOut = string.Empty;
                var tagCmd = string.Format("tag \"{0}\" \"{1}\"", oldRepoTag, newRepoTag);
                var tagOk = await Task.Run(() => TryRunDockerCommand(tagCmd, null, out tagOut, 45000));
                if (!tagOk)
                {
                    ShowScrollableErrorDialog("修改Name失败", string.IsNullOrWhiteSpace(tagOut) ? "执行 docker tag 失败（无输出）。" : tagOut);
                    return;
                }

                SetProgressText(progressText, "正在清理旧标签...");
                var rmOut = string.Empty;
                var rmCmd = string.Format("image rm \"{0}\"", oldRepoTag);
                await Task.Run(() => TryRunDockerCommand(rmCmd, null, out rmOut, 15000));

                SetProgressText(progressText, "正在刷新镜像列表...");
                await RefreshRuntimeDataBindingsAsync(true);
                ShowServiceImagePanel();
                BindServiceImageRows(GetSelectedServiceImageDeviceName());
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

            MessageBox.Show("修改Name成功：\n" + newRepoTag, "镜像编排", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async Task ExecuteDeleteImagesAsync(List<ImageComposeRow> imageRows)
        {
            if (imageRows == null || imageRows.Count == 0)
            {
                return;
            }

            var displayList = string.Join("\n", imageRows.Select(r => NormalizeRepoTag(r.RepoTag)).Where(t => !string.IsNullOrWhiteSpace(t)).Take(10));
            if (imageRows.Count > 10)
            {
                displayList += "\n...";
            }

            var confirm = MessageBox.Show(
                string.Format("确定要删除该镜像吗？\n已选中 {0} 个：\n{1}", imageRows.Count, displayList),
                "删除镜像",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            var successTags = new List<string>();
            var failedTags = new List<string>();
            for (var i = 0; i < imageRows.Count; i++)
            {
                var row = imageRows[i];
                if (row == null)
                {
                    continue;
                }

                if (await TryDeleteSingleImageAsync(row))
                {
                    successTags.Add(NormalizeRepoTag(row.RepoTag));
                }
                else
                {
                    failedTags.Add(NormalizeRepoTag(row.RepoTag));
                }
            }

            await RefreshRuntimeDataBindingsAsync(true);

            if (failedTags.Count == 0)
            {
                MessageBox.Show(
                    string.Format("删除成功：共删除 {0} 个镜像。", successTags.Count),
                    "镜像编排",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            ShowScrollableErrorDialog(
                string.Format("批量删除完成：成功 {0}，失败 {1}", successTags.Count, failedTags.Count),
                string.Join("\n", failedTags));
        }

        private async Task<bool> TryDeleteSingleImageAsync(ImageComposeRow imageRow)
        {
            var repoTag = NormalizeRepoTag(imageRow.RepoTag);
            if (string.IsNullOrWhiteSpace(repoTag))
            {
                return false;
            }

            var rmOut = string.Empty;
            var rmCmd = string.Format("image rm \"{0}\"", repoTag);
            var rmOk = await Task.Run(() => TryRunDockerCommand(rmCmd, null, out rmOut));
            if (!rmOk)
            {
                // 标签删除失败时尝试按镜像ID删除（处理同ID多标签场景）
                var imageId = (imageRow.ImageId ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(imageId) && imageId != "-")
                {
                    var idValue = imageId.EndsWith("...", StringComparison.Ordinal) ? imageId.Substring(0, imageId.Length - 3) : imageId;
                    var rmByIdCmd = string.Format("image rm \"{0}\"", idValue);
                    rmOk = await Task.Run(() => TryRunDockerCommand(rmByIdCmd, null, out rmOut));
                }
            }
            return rmOk;
        }

        private void ClearCreateSelectionsAfterSuccess()
        {
            _stickySelectedBaseRepoTag = string.Empty;
            _stickySelectedProgramKeys.Clear();

            if (SourceImageGrid != null)
            {
                SourceImageGrid.UnselectAllCells();
                SourceImageGrid.SelectedItem = null;
            }

            if (ProgramFileGrid != null)
            {
                ProgramFileGrid.UnselectAllCells();
                ProgramFileGrid.SelectedItem = null;
            }
        }

        private string GetSelectedServiceImageContextName()
        {
            var selected = ServiceImageDeviceSelector.SelectedItem as ComboBoxItem;
            if (selected == null)
            {
                return string.Empty;
            }

            if ((selected.Tag as string) == "ALL")
            {
                return string.Empty;
            }

            var device = selected.Tag as DeviceInfo;
            if (device == null)
            {
                return string.Empty;
            }

            string contextName;
            if (_deviceContextMap.TryGetValue(device.Name, out contextName))
            {
                return contextName;
            }

            return device.ContextName ?? string.Empty;
        }

        private ImageComposeRow GetSelectedServiceImageRow()
        {
            var source = SourceImageGrid.SelectedItem as ImageComposeRow;
            if (source != null)
            {
                return source;
            }

            return PreparedImageGrid.SelectedItem as ImageComposeRow;
        }

        private static string NormalizeRepoTag(string repoTag)
        {
            return (repoTag ?? string.Empty).Trim();
        }

        private static string NormalizeLocalPath(string path)
        {
            var value = (path ?? string.Empty).Trim();
            value = value.Trim('"', '\'');
            value = value.Replace('：', ':').Replace('＼', '\\').Replace('／', '\\');
            return value;
        }

        private static string BuildDeployContainerName(string repoTag)
        {
            var baseName = NormalizeRepoTag(repoTag);
            var slash = baseName.LastIndexOf('/');
            if (slash >= 0 && slash < baseName.Length - 1)
            {
                baseName = baseName.Substring(slash + 1);
            }

            baseName = baseName.Replace(":", "_").Replace(".", "_").Replace("-", "_");
            baseName = Regex.Replace(baseName, @"[^a-zA-Z0-9_]", string.Empty);
            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = "service";
            }

            return string.Format("deploy_{0}_{1}", baseName.ToLowerInvariant(), DateTime.Now.ToString("MMddHHmmss"));
        }

        private static void SplitRepoTag(string repoTag, out string repository, out string tag)
        {
            repository = string.Empty;
            tag = "latest";
            var input = NormalizeRepoTag(repoTag);
            if (string.IsNullOrWhiteSpace(input))
            {
                return;
            }

            var slash = input.LastIndexOf('/');
            var colon = input.LastIndexOf(':');
            if (colon > slash)
            {
                repository = input.Substring(0, colon);
                tag = input.Substring(colon + 1);
                return;
            }

            repository = input;
        }

        private static string ExtractReclaimedText(string output)
        {
            var text = (output ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].IndexOf("Total reclaimed space:", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return lines[i].Trim();
                }
            }

            return lines.Length == 0 ? string.Empty : lines[lines.Length - 1].Trim();
        }

        private bool ShowDeployDeviceDialog(out string selectedContextName, out string selectedDeviceName)
        {
            selectedContextName = string.Empty;
            selectedDeviceName = string.Empty;
            var contextResult = string.Empty;
            var deviceResult = string.Empty;

            if (_devices == null || _devices.Count == 0)
            {
                MessageBox.Show("没有可用设备，请先读取设备信息。", "镜像编排", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            var combo = new ComboBox
            {
                Width = 320,
                Height = 32,
                Margin = new Thickness(0, 8, 0, 0)
            };

            for (var i = 0; i < _devices.Count; i++)
            {
                var d = _devices[i];
                combo.Items.Add(new ComboBoxItem
                {
                    Content = string.Format("{0} ({1})", d.Name, d.Ip),
                    Tag = d
                });
            }

            if (combo.Items.Count > 0)
            {
                combo.SelectedIndex = 0;
            }

            var panel = new StackPanel { Margin = new Thickness(16, 14, 16, 10) };
            panel.Children.Add(new TextBlock
            {
                Text = "请选择部署目标设备",
                FontWeight = FontWeights.SemiBold
            });
            panel.Children.Add(combo);

            var okButton = new Button { Content = "部署", Width = 86, Height = 32, Margin = new Thickness(0, 10, 8, 0), IsDefault = true };
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
                Title = "选择部署设备",
                Width = 420,
                Height = 220,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Content = root
            };

            okButton.Click += (_, __) =>
            {
                var selected = combo.SelectedItem as ComboBoxItem;
                var d = selected == null ? null : selected.Tag as DeviceInfo;
                if (d == null)
                {
                    MessageBox.Show("请选择一个设备。", "镜像编排", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                string contextName;
                if (!_deviceContextMap.TryGetValue(d.Name, out contextName) || string.IsNullOrWhiteSpace(contextName))
                {
                    contextName = d.ContextName;
                }

                if (string.IsNullOrWhiteSpace(contextName))
                {
                    MessageBox.Show("该设备无可用 Docker 上下文。", "镜像编排", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                contextResult = ResolvePreferredDeployContextName(contextName);
                deviceResult = d.Name;
                dialog.DialogResult = true;
            };

            if (dialog.ShowDialog() == true)
            {
                selectedContextName = contextResult;
                selectedDeviceName = deviceResult;
                return true;
            }

            return false;
        }

        private bool ShowBuildImageDialog(out string contextDir, out string imageTag)
        {
            contextDir = string.Empty;
            imageTag = string.Empty;
            var selectedDir = string.Empty;
            var selectedTag = string.Empty;

            var dirBox = new TextBox
            {
                Width = 460,
                Height = 32,
                Margin = new Thickness(0, 6, 0, 12)
            };

            var tagBox = new TextBox
            {
                Width = 320,
                Height = 32,
                Margin = new Thickness(0, 6, 0, 0),
                Text = "local/custom:latest"
            };

            var panel = new StackPanel { Margin = new Thickness(16, 14, 16, 10) };
            panel.Children.Add(new TextBlock
            {
                Text = "镜像构建目录（将自动生成 Dockerfile 与镜像 tar）",
                FontWeight = FontWeights.SemiBold
            });
            panel.Children.Add(dirBox);
            panel.Children.Add(new TextBlock
            {
                Text = "镜像名:Tag",
                FontWeight = FontWeights.SemiBold
            });
            panel.Children.Add(tagBox);

            var okButton = new Button { Content = "构建", Width = 86, Height = 32, Margin = new Thickness(0, 10, 8, 0), IsDefault = true };
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
                Title = "新建镜像",
                Width = 560,
                Height = 260,
                MinWidth = 560,
                MinHeight = 260,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Content = root
            };

            okButton.Click += (_, __) =>
            {
                var dir = NormalizeLocalPath(dirBox.Text);
                var tag = (tagBox.Text ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(dir))
                {
                    MessageBox.Show("请输入镜像构建目录。", "镜像编排", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (string.IsNullOrWhiteSpace(tag))
                {
                    MessageBox.Show("请输入镜像名:Tag。", "镜像编排", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                selectedDir = dir;
                selectedTag = tag;
                dialog.DialogResult = true;
            };

            if (dialog.ShowDialog() == true)
            {
                contextDir = selectedDir;
                imageTag = selectedTag;
                return true;
            }

            return false;
        }

        private List<ProgramFileRow> GetSelectedProgramPackages()
        {
            var selected = new List<ProgramFileRow>();
            if (ProgramFileGrid == null)
            {
                return selected;
            }

            for (var i = 0; i < ProgramFileGrid.SelectedItems.Count; i++)
            {
                var row = ProgramFileGrid.SelectedItems[i] as ProgramFileRow;
                if (row == null || string.IsNullOrWhiteSpace(row.Name))
                {
                    continue;
                }

                if (selected.Any(x => string.Equals(x.Name, row.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                selected.Add(row);
            }

            if (selected.Count == 0)
            {
                var single = ProgramFileGrid.SelectedItem as ProgramFileRow;
                if (single != null && !string.IsNullOrWhiteSpace(single.Name))
                {
                    selected.Add(single);
                }
            }

            return selected;
        }

        private bool TryStagePackagesToContext(string contextDir, List<ProgramFileRow> selectedPackages, string dockerfileContent, out string errorMessage)
        {
            errorMessage = string.Empty;
            for (var i = 0; i < selectedPackages.Count; i++)
            {
                var row = selectedPackages[i];
                var packageName = (row.Name ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(packageName))
                {
                    continue;
                }

                var sourcePath = ResolvePackageSourcePath(row);
                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                {
                    errorMessage = string.Format("未找到程序包文件：{0}", packageName);
                    return false;
                }

                var targetPath = Path.Combine(contextDir, packageName);
                if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                File.Copy(sourcePath, targetPath, true);
            }

            var requiredSources = ExtractDockerfileContextSourceFiles(dockerfileContent);
            for (var i = 0; i < requiredSources.Count; i++)
            {
                var required = requiredSources[i];
                var requiredPath = Path.Combine(contextDir, required.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(requiredPath))
                {
                    errorMessage = string.Format("构建目录缺少 Dockerfile 引用的程序包：{0}，请在程序包列表中提供该文件。", required);
                    return false;
                }
            }

            return true;
        }

        private static List<string> ExtractDockerfileContextSourceFiles(string dockerfileContent)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lines = (dockerfileContent ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = StripDockerfileComment(lines[i]).Trim();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var match = Regex.Match(line, @"^(COPY|ADD)\s+(.+)$", RegexOptions.IgnoreCase);
                if (!match.Success)
                {
                    continue;
                }

                var rest = match.Groups[2].Value.Trim();
                if (rest.IndexOf("--from=", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                while (rest.StartsWith("--", StringComparison.Ordinal))
                {
                    var nextSpace = rest.IndexOf(' ');
                    if (nextSpace < 0)
                    {
                        rest = string.Empty;
                        break;
                    }

                    rest = rest.Substring(nextSpace + 1).TrimStart();
                }

                var sources = ExtractDockerfileCopySources(rest);
                for (var j = 0; j < sources.Count; j++)
                {
                    var source = NormalizeDockerfileSourcePath(sources[j]);
                    if (string.IsNullOrWhiteSpace(source) || !IsPlainDockerfileContextFile(source))
                    {
                        continue;
                    }

                    if (seen.Add(source))
                    {
                        result.Add(source);
                    }
                }
            }

            return result;
        }

        private static List<string> ExtractDockerfileCopySources(string rest)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(rest))
            {
                return result;
            }

            if (rest.StartsWith("[", StringComparison.Ordinal))
            {
                var matches = Regex.Matches(rest, "\"((?:\\\\.|[^\"])*)\"");
                for (var i = 0; i < matches.Count - 1; i++)
                {
                    result.Add(Regex.Unescape(matches[i].Groups[1].Value));
                }

                return result;
            }

            var tokens = SplitDockerfileShellArgs(rest);
            for (var i = 0; i < tokens.Count - 1; i++)
            {
                result.Add(tokens[i]);
            }

            return result;
        }

        private static List<string> SplitDockerfileShellArgs(string text)
        {
            var result = new List<string>();
            var current = new StringBuilder();
            var quote = '\0';
            for (var i = 0; i < (text ?? string.Empty).Length; i++)
            {
                var ch = text[i];
                if (quote == '\0' && char.IsWhiteSpace(ch))
                {
                    if (current.Length > 0)
                    {
                        result.Add(current.ToString());
                        current.Clear();
                    }

                    continue;
                }

                if ((ch == '"' || ch == '\'') && (quote == '\0' || quote == ch))
                {
                    quote = quote == '\0' ? ch : '\0';
                    continue;
                }

                current.Append(ch);
            }

            if (current.Length > 0)
            {
                result.Add(current.ToString());
            }

            return result;
        }

        private static string StripDockerfileComment(string line)
        {
            var quote = '\0';
            for (var i = 0; i < (line ?? string.Empty).Length; i++)
            {
                var ch = line[i];
                if ((ch == '"' || ch == '\'') && (quote == '\0' || quote == ch))
                {
                    quote = quote == '\0' ? ch : '\0';
                    continue;
                }

                if (ch == '#' && quote == '\0')
                {
                    return line.Substring(0, i);
                }
            }

            return line ?? string.Empty;
        }

        private static string NormalizeDockerfileSourcePath(string source)
        {
            var value = (source ?? string.Empty).Trim().Replace('\\', '/');
            while (value.StartsWith("./", StringComparison.Ordinal))
            {
                value = value.Substring(2);
            }

            return value.TrimStart('/');
        }

        private static bool IsPlainDockerfileContextFile(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return false;
            }

            if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return source.IndexOf('*') < 0 &&
                   source.IndexOf('?') < 0 &&
                   !source.EndsWith("/", StringComparison.Ordinal);
        }

        private static string ResolvePackageSourcePath(ProgramFileRow row)
        {
            if (row == null)
            {
                return string.Empty;
            }

            var fromRow = (row.SourcePath ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(fromRow) && File.Exists(fromRow))
            {
                return fromRow;
            }

            var fileName = (row.Name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return string.Empty;
            }

            var candidates = new List<string>();
            candidates.Add(Path.Combine(GetProgramPackageDirectory(), fileName));

            for (var i = 0; i < candidates.Count; i++)
            {
                if (File.Exists(candidates[i]))
                {
                    return candidates[i];
                }
            }

            return string.Empty;
        }

        private static bool TryResolveBaseImageTarPath(string repoTag, out string tarPath)
        {
            tarPath = string.Empty;
            var normalized = NormalizeRepoTag(repoTag);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            var basicDir = GetBasicImageDirectory();
            if (!Directory.Exists(basicDir))
            {
                return false;
            }

            var preferredName = BuildBaseImageTarFileName(normalized);
            var preferredPath = Path.Combine(basicDir, preferredName);
            if (File.Exists(preferredPath))
            {
                tarPath = preferredPath;
                return true;
            }

            var altName = normalized.Replace('/', '_').Replace(':', '_') + ".tar";
            var altPath = Path.Combine(basicDir, altName);
            if (File.Exists(altPath))
            {
                tarPath = altPath;
                return true;
            }

            return false;
        }

        private static string BuildBaseImageTarFileName(string repoTag)
        {
            var repository = repoTag;
            var tag = "latest";
            var slash = repoTag.LastIndexOf('/');
            var colon = repoTag.LastIndexOf(':');
            if (colon > slash)
            {
                repository = repoTag.Substring(0, colon);
                tag = repoTag.Substring(colon + 1);
            }

            var safeRepo = repository.Replace('/', '_').Replace('\\', '_');
            var safeTag = tag.Replace('/', '_').Replace('\\', '_');
            return safeRepo + "_" + safeTag + ".tar";
        }

        private static bool EnsureBaseImageTarGz(string tarPath, out string tarGzPath, out string errorMessage)
        {
            tarGzPath = string.Empty;
            errorMessage = string.Empty;
            try
            {
                if (string.IsNullOrWhiteSpace(tarPath) || !File.Exists(tarPath))
                {
                    errorMessage = "基础镜像 tar 文件不存在。";
                    return false;
                }

                tarGzPath = tarPath + ".gz";
                var tarInfo = new FileInfo(tarPath);
                var gzInfo = new FileInfo(tarGzPath);
                if (gzInfo.Exists && gzInfo.LastWriteTimeUtc >= tarInfo.LastWriteTimeUtc && gzInfo.Length > 0)
                {
                    return true;
                }

                using (var source = File.OpenRead(tarPath))
                using (var target = File.Create(tarGzPath))
                using (var gzip = new GZipStream(target, CompressionLevel.Optimal))
                {
                    source.CopyTo(gzip);
                }

                return File.Exists(tarGzPath) && new FileInfo(tarGzPath).Length > 0;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private bool ConfirmCreateImageSelections(string baseRepoTag, List<ProgramFileRow> packages)
        {
            var text = "确认无误后将生成 Dockerfile 并构建镜像。";

            var result = MessageBox.Show(text, "确认新建镜像", MessageBoxButton.YesNo, MessageBoxImage.Question);
            return result == MessageBoxResult.Yes;
        }

        private void ShowScrollableErrorDialog(string summary, string details)
        {
            var root = new Grid
            {
                Margin = new Thickness(14)
            };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var summaryBlock = new TextBlock
            {
                Text = summary ?? string.Empty,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(summaryBlock, 0);
            root.Children.Add(summaryBlock);

            var detailsBox = new TextBox
            {
                Text = details ?? string.Empty,
                IsReadOnly = true,
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                MinHeight = 260
            };
            Grid.SetRow(detailsBox, 1);
            root.Children.Add(detailsBox);

            var closeButton = new Button
            {
                Content = "关闭",
                Width = 88,
                Height = 32,
                IsDefault = true,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            Grid.SetRow(closeButton, 2);
            root.Children.Add(closeButton);

            var dialog = new Window
            {
                Title = "镜像编排",
                Owner = this,
                Width = 840,
                Height = 560,
                MinWidth = 700,
                MinHeight = 440,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = root
            };

            closeButton.Click += (_, __) => dialog.Close();
            dialog.ShowDialog();
        }

        private static void SetProgressText(TextBlock textBlock, string text)
        {
            if (textBlock == null)
            {
                return;
            }

            textBlock.Text = string.IsNullOrWhiteSpace(text) ? "处理中..." : text;
        }

        private Window CreateProgressDialog(string title)
        {
            var textBlock = new TextBlock
            {
                Margin = new Thickness(18, 18, 18, 18),
                Text = "处理中...",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14
            };

            return new Window
            {
                Title = string.IsNullOrWhiteSpace(title) ? "请稍候" : title,
                Owner = this,
                Width = 460,
                Height = 150,
                MinWidth = 420,
                MinHeight = 130,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = textBlock
            };
        }

        private static string BuildImageArchiveFileName(string imageTag)
        {
            var value = NormalizeRepoTag(imageTag).Replace('/', '_').Replace(':', '_').Replace('\\', '_');
            value = Regex.Replace(value, @"[^a-zA-Z0-9._-]", "_");
            if (string.IsNullOrWhiteSpace(value))
            {
                value = "custom_image";
            }

            return value + ".tar";
        }

        private static string BuildGeneratedDockerfile(string baseImage, List<ProgramFileRow> selectedPackages)
        {
            var presetDockerfile = TryLoadPresetDockerfileContent(selectedPackages);
            if (!string.IsNullOrWhiteSpace(presetDockerfile))
            {
                return presetDockerfile;
            }

            var packageNames = selectedPackages
                .Select(p => (p.Name ?? string.Empty).Trim())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine("FROM " + NormalizeRepoTag(baseImage));
            sb.AppendLine();
            sb.AppendLine("ENV DEBIAN_FRONTEND=noninteractive");
            sb.AppendLine("ENV TZ=Asia/Shanghai");
            sb.AppendLine("ENV LANG=C.UTF-8");
            sb.AppendLine("ENV LC_ALL=C.UTF-8");
            sb.AppendLine();
            sb.AppendLine("ENV PATH=\"/home/conda_env/bin:$PATH\"");
            sb.AppendLine();
            sb.AppendLine("# 完全替换为阿里云源");
            sb.AppendLine("RUN echo \"deb http://mirrors.aliyun.com/ubuntu/ focal main restricted universe multiverse\" > /etc/apt/sources.list && \\");
            sb.AppendLine("    echo \"deb http://mirrors.aliyun.com/ubuntu/ focal-updates main restricted universe multiverse\" >> /etc/apt/sources.list && \\");
            sb.AppendLine("    echo \"deb http://mirrors.aliyun.com/ubuntu/ focal-backports main restricted universe multiverse\" >> /etc/apt/sources.list && \\");
            sb.AppendLine("    echo \"deb http://mirrors.aliyun.com/ubuntu/ focal-security main restricted universe multiverse\" >> /etc/apt/sources.list");
            sb.AppendLine();
            sb.AppendLine("RUN apt-get update && apt-get install -y \\");
            sb.AppendLine("    systemctl \\");
            sb.AppendLine("    psmisc \\");
            sb.AppendLine("    vim \\");
            sb.AppendLine("    gcc \\");
            sb.AppendLine("    g++ \\");
            sb.AppendLine("    nano \\");
            sb.AppendLine("    build-essential \\");
            sb.AppendLine("    net-tools \\");
            sb.AppendLine("    python3.8-dev \\");
            sb.AppendLine("    libssl-dev \\");
            sb.AppendLine("    openssl \\");
            sb.AppendLine("    curl \\");
            sb.AppendLine("    git \\");
            sb.AppendLine("    make \\");
            sb.AppendLine("    cmake \\");
            sb.AppendLine("    expect \\");
            sb.AppendLine("    unzip \\");
            sb.AppendLine("    libgtk2.0-0 \\");
            sb.AppendLine("    libx11-6 \\");
            sb.AppendLine("    libsm6 \\");
            sb.AppendLine("    libice6 \\");
            sb.AppendLine("    libusb-1.0-0 \\");
            sb.AppendLine("    && rm -rf /var/lib/apt/lists/*");
            sb.AppendLine();
            sb.AppendLine("# 拷贝程序包");

            var hasXenomai = packageNames.Any(n => string.Equals(n, "xenomai.tar.gz", StringComparison.OrdinalIgnoreCase));
            if (!hasXenomai)
            {
                sb.AppendLine("COPY xenomai.tar.gz /home");
            }

            for (var i = 0; i < packageNames.Count; i++)
            {
                sb.AppendLine("COPY " + packageNames[i] + " /home");
            }

            var extractTargets = new List<string>();
            for (var i = 0; i < packageNames.Count; i++)
            {
                var name = packageNames[i];
                if (!name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            var selectedShellScripts = packageNames
                .Where(n => n.EndsWith(".sh", StringComparison.OrdinalIgnoreCase))
                .ToList();

            sb.AppendLine();
            sb.AppendLine("RUN set -eux; \\");
            for (var i = 0; i < selectedShellScripts.Count; i++)
            {
                var script = selectedShellScripts[i];
                sb.AppendLine("    sed -i 's/\\r$//' /home/" + script + " && \\");
                sb.AppendLine("    chmod +x /home/" + script + " && \\");
                sb.AppendLine("    cd /home && /bin/bash -x ./" + script + " && \\");
            }
            sb.AppendLine("    echo \"/home/CASS-Lib\"                  >> /etc/ld.so.conf.d/cass.conf && \\");
            sb.AppendLine("    echo \"/home/CASS-Lib/camealib\"         >> /etc/ld.so.conf.d/cass.conf && \\");
            sb.AppendLine("    echo \"/home/CASS-Lib/casslib-x86\"      >> /etc/ld.so.conf.d/cass.conf && \\");
            sb.AppendLine("    echo \"/home/CASS-Lib/libevent\"         >> /etc/ld.so.conf.d/cass.conf && \\");
            sb.AppendLine("    echo \"/home/CASS-Lib/mqttlib-x86\"      >> /etc/ld.so.conf.d/cass.conf && \\");
            sb.AppendLine("    echo \"/home/CASS-Lib/opencvlib-x86\"    >> /etc/ld.so.conf.d/cass.conf && \\");
            sb.AppendLine("    echo \"/usr/xenomai/lib\"                >> /etc/ld.so.conf.d/cass.conf && \\");
            sb.AppendLine("    ldconfig");
            sb.AppendLine();
            sb.AppendLine("WORKDIR /home");
            sb.AppendLine();
            sb.AppendLine("# WORKDIR /home/Cass-ePLC-3.15");
            sb.AppendLine("# CMD [\"nohup python3 /home/PyServer/server.py\"]");
            sb.AppendLine("# CMD [\"./Boot\"]");

            return sb.ToString();
        }

        private bool ShowDockerfileEditorDialog(string defaultDockerfileContent, out string dockerfileContent)
        {
            dockerfileContent = string.Empty;
            var initialContent = string.IsNullOrWhiteSpace(defaultDockerfileContent) ? "FROM ubuntu:20.04" : defaultDockerfileContent;
            var selectedDockerfileContent = string.Empty;
            var templateDir = GetTemplateDockerfileDirectory();
            EnsureDirectoryExistsQuietly(templateDir);

            var templateFiles = new List<string>();
            if (Directory.Exists(templateDir))
            {
                templateFiles = Directory
                    .GetFiles(templateDir)
                    .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var topPanel = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            topPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var templateLabel = new TextBlock
            {
                Text = "模板：",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(templateLabel, 0);
            topPanel.Children.Add(templateLabel);

            var templateCombo = new ComboBox
            {
                MinWidth = 360,
                Height = 30,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(8, 0, 26, 0)
            };
            templateCombo.Items.Add("（默认自动生成）");
            for (var i = 0; i < templateFiles.Count; i++)
            {
                templateCombo.Items.Add(Path.GetFileName(templateFiles[i]));
            }
            templateCombo.SelectedIndex = 0;
            var comboHost = new Grid();
            comboHost.Children.Add(templateCombo);
            comboHost.Children.Add(new TextBlock
            {
                Text = "▼",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 10, 0),
                IsHitTestVisible = false
            });
            Grid.SetColumn(comboHost, 1);
            topPanel.Children.Add(comboHost);

            var resetButton = new Button
            {
                Content = "恢复默认",
                Height = 30,
                MinWidth = 96,
                Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(resetButton, 2);
            topPanel.Children.Add(resetButton);

            var templateHint = new TextBlock
            {
                Text = "模板目录：" + templateDir,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                Margin = new Thickness(10, 0, 0, 0)
            };
            Grid.SetColumn(templateHint, 3);
            topPanel.Children.Add(templateHint);

            Grid.SetRow(topPanel, 0);
            root.Children.Add(topPanel);

            var editor = new TextBox
            {
                Text = initialContent,
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 14
            };
            Grid.SetRow(editor, 1);
            root.Children.Add(editor);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            var okButton = new Button { Content = "确认并构建", Width = 120, Height = 32, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var cancelButton = new Button { Content = "取消", Width = 90, Height = 32, IsCancel = true };
            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            Grid.SetRow(buttonPanel, 2);
            root.Children.Add(buttonPanel);

            var dialog = new Window
            {
                Title = "编辑 Dockerfile",
                Width = 1100,
                Height = 760,
                MinWidth = 900,
                MinHeight = 620,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = root
            };

            templateCombo.SelectionChanged += (_, __) =>
            {
                var idx = templateCombo.SelectedIndex;
                if (idx <= 0)
                {
                    editor.Text = initialContent;
                    return;
                }

                var fileIndex = idx - 1;
                if (fileIndex < 0 || fileIndex >= templateFiles.Count)
                {
                    return;
                }

                try
                {
                    editor.Text = File.ReadAllText(templateFiles[fileIndex], new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "读取模板失败：" + ex.Message, "镜像编排", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };

            resetButton.Click += (_, __) =>
            {
                templateCombo.SelectedIndex = 0;
                editor.Text = initialContent;
            };

            okButton.Click += (_, __) =>
            {
                var text = (editor.Text ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    MessageBox.Show(this, "Dockerfile 内容不能为空。", "镜像编排", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                selectedDockerfileContent = editor.Text;
                dialog.DialogResult = true;
            };

            var ok = dialog.ShowDialog() == true;
            if (ok)
            {
                dockerfileContent = selectedDockerfileContent;
            }

            return ok;
        }

        private static string GetTemplateDockerfileDirectory()
        {
            return Path.Combine(GetDockerBuildWorkspaceRoot(), "template_dockfile");
        }

        private static void EnsureDirectoryExistsQuietly(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && !Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }
            }
            catch
            {
            }
        }

        private static string TryLoadPresetDockerfileContent(List<ProgramFileRow> selectedPackages)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < (selectedPackages ?? new List<ProgramFileRow>()).Count; i++)
            {
                var n = (selectedPackages[i].Name ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(n))
                {
                    names.Add(n);
                }
            }

            var templateFileName = ResolvePresetDockerfileName(names);
            if (string.IsNullOrWhiteSpace(templateFileName))
            {
                return string.Empty;
            }

            var templatePath = ResolvePresetDockerfilePath(templateFileName);
            if (string.IsNullOrWhiteSpace(templatePath) || !File.Exists(templatePath))
            {
                return string.Empty;
            }

            try
            {
                return File.ReadAllText(templatePath, new UTF8Encoding(false)).Trim();
            }
            catch
            {
                try
                {
                    return File.ReadAllText(templatePath).Trim();
                }
                catch
                {
                    return string.Empty;
                }
            }
        }

        private static string ResolvePresetDockerfileName(HashSet<string> names)
        {
            if (names == null || names.Count == 0)
            {
                return string.Empty;
            }

            var hasA = names.Contains("a_engine.tar.gz") && names.Contains("a_lib.tar.gz");
            var hasAi = names.Contains("ai_engine.tar.gz") && names.Contains("ai_lib.tar.gz");
            var hasBh = names.Contains("bh_engine.tar.gz") && names.Contains("bh_lib.tar.gz");
            var hasDb = names.Contains("db_engine.tar.gz") && names.Contains("db_lib.tar.gz");
            var hasImg = names.Contains("img_engine.tar.gz") && names.Contains("img_lib.tar.gz");
            var hasI = names.Contains("i_engine.tar.gz") && names.Contains("i_lib.tar.gz");
            var hasStress = names.Contains("stresstest_engine.tar.gz") && names.Contains("stresstest_lib.tar.gz");

            var matchedCount = 0;
            if (hasA) matchedCount++;
            if (hasAi) matchedCount++;
            if (hasBh) matchedCount++;
            if (hasDb) matchedCount++;
            if (hasImg) matchedCount++;
            if (hasI) matchedCount++;
            if (hasStress) matchedCount++;
            if (matchedCount != 1)
            {
                return string.Empty;
            }

            if (hasA || hasAi) return "ai_dockerfile";
            if (hasBh) return "bh_dockerfile";
            if (hasDb) return "db_dockerfile";
            if (hasImg) return "img_dockerfile";
            if (hasI) return "i_dockerfile";
            if (hasStress) return "stress_dockerfile.txt";
            return string.Empty;
        }

        private static string ResolvePresetDockerfilePath(string templateFileName)
        {
            var programDir = GetProgramPackageDirectory();
            if (string.IsNullOrWhiteSpace(programDir))
            {
                return string.Empty;
            }

            var direct = Path.Combine(programDir, templateFileName);
            if (File.Exists(direct))
            {
                return direct;
            }

            var templateDir = GetTemplateDockerfileDirectory();
            var templateDirect = Path.Combine(templateDir, templateFileName);
            if (File.Exists(templateDirect))
            {
                return templateDirect;
            }

            var subDir = Path.Combine(programDir, "新建文件夹", templateFileName);
            if (File.Exists(subDir))
            {
                return subDir;
            }

            try
            {
                var files = Directory.GetFiles(programDir, templateFileName, SearchOption.AllDirectories);
                if (files.Length > 0)
                {
                    return files[0];
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private async Task<bool> EnsureImageExistsOnRemoteAsync(string remoteContextName, string repoTag)
        {
            var error = await EnsureImageExistsOnRemoteWithDetailAsync(remoteContextName, repoTag);
            return string.IsNullOrWhiteSpace(error);
        }

        private async Task<string> EnsureImageExistsOnRemoteWithDetailAsync(string remoteContextName, string repoTag, Action<string, double> progressCallback = null)
        {
            const int deployStepTimeoutMs = 300000;
            var report = progressCallback ?? new Action<string, double>((_, __) => { });
            if (string.IsNullOrWhiteSpace(remoteContextName))
            {
                return "目标设备上下文为空，无法部署。";
            }

            if (string.IsNullOrWhiteSpace(repoTag))
            {
                return "镜像标签为空，无法部署。";
            }

            report("本机导出镜像（save）", 0.25);
            var tempFile = Path.Combine(Path.GetTempPath(), "deploy_sync_" + DateTime.Now.ToString("yyyyMMddHHmmssfff") + ".tar");
            try
            {
                string localSaveOut = string.Empty;
                var localSaveCmd = string.Format("image save \"{0}\" -o \"{1}\"", repoTag, tempFile);
                var localSaveOk = await Task.Run(() => TryRunDockerCommand(localSaveCmd, null, out localSaveOut, deployStepTimeoutMs));
                if (!localSaveOk)
                {
                    return string.Format(
                        "部署失败：本机导出镜像失败。\n镜像：{0}\n建议检查：docker image inspect \"{0}\"\n若输出包含 permission denied，请先确认 Docker Desktop 已启动且当前用户有访问权限。\n\n输出：\n{1}",
                        repoTag,
                        string.IsNullOrWhiteSpace(localSaveOut) ? "(无输出)" : localSaveOut);
                }

                report("上传镜像文件（scp）", 0.55);
                string sshIdentity;
                if (!TryResolveSshIdentityForContext(remoteContextName, out sshIdentity) ||
                    string.IsNullOrWhiteSpace(sshIdentity) ||
                    !_sshPasswordByTarget.ContainsKey(sshIdentity))
                {
                    return string.Format(
                        "部署失败：未找到可用 SSH 目标或密码缓存。\n上下文：{0}\n请先点击“读取设备信息”并输入密码后重试。",
                        remoteContextName);
                }

                var remoteLoadPath = "/tmp/deploy_sync_" + DateTime.Now.ToString("yyyyMMddHHmmssfff") + ".tar";
                var scpDetail = string.Empty;
                var scpOk = await Task.Run(() =>
                {
                    string tmpDetail;
                    var ok = TryCopyLocalFileToRemoteByScp(sshIdentity, tempFile, remoteLoadPath, deployStepTimeoutMs, out tmpDetail);
                    scpDetail = tmpDetail;
                    return ok;
                });
                if (!scpOk)
                {
                    return string.Format(
                        "部署失败：镜像文件上传远端失败。\n目标：{0}\n上下文：{1}\n\nscp 输出：\n{2}",
                        sshIdentity,
                        remoteContextName,
                        string.IsNullOrWhiteSpace(scpDetail) ? "(无输出)" : scpDetail);
                }

                report("远端导入镜像（load）", 0.80);
                var remoteLoadOutBySsh = string.Empty;
                var remoteLoadCmdBySsh = string.Format("image load -i \"{0}\"", remoteLoadPath);
                var remoteLoadOk = await Task.Run(() => TryRunDockerCommand(remoteLoadCmdBySsh, SshContextPrefix + sshIdentity, out remoteLoadOutBySsh, deployStepTimeoutMs));
                if (!remoteLoadOk)
                {
                    return string.Format(
                        "部署失败：远端 load 失败。\n上下文：{0}\n镜像：{1}\n建议检查：\n{2}\n\n远端 load 输出：\n{3}",
                        remoteContextName,
                        repoTag,
                        BuildDockerConnectivityHint(remoteContextName),
                        string.IsNullOrWhiteSpace(remoteLoadOutBySsh) ? "(无输出)" : remoteLoadOutBySsh);
                }

                report("校验远端镜像", 0.95);
                var existsAfterLoad = await ImageExistsInContextAsync(remoteContextName, repoTag);
                if (!existsAfterLoad)
                {
                    return string.Format("部署失败：已执行 load，但目标设备仍未找到镜像 {0}。\n请检查：\n{1}", repoTag, BuildDockerConnectivityHint(remoteContextName));
                }

                report("镜像已就绪", 1.0);
                return string.Empty;
            }
            finally
            {
                TryDeleteFileQuietly(tempFile);
            }
        }

        private async Task<bool> ImageExistsInContextAsync(string contextName, string repoTag)
        {
            var outText = string.Empty;
            var cmd = string.Format("image inspect \"{0}\"", repoTag);
            return await Task.Run(() => TryRunDockerCommand(cmd, contextName, out outText, 60000));
        }

        private async Task<string> BuildDeployTimeoutDiagnosticsAsync(string contextName, string repoTag, string imageIdHint)
        {
            var diagnostics = new List<string>();
            const int diagTimeoutMs = 3000;
            var timeoutText = diagTimeoutMs.ToString();

            var remoteInfoOut = string.Empty;
            var remoteInfoOk = await Task.Run(() => TryRunDockerCommand("info --format \"{{.ServerVersion}}\"", contextName, out remoteInfoOut, diagTimeoutMs));
            diagnostics.Add(string.Format(
                "[远端 info] {0}",
                remoteInfoOk ? "OK" : "FAIL"));
            if (!string.IsNullOrWhiteSpace(remoteInfoOut))
            {
                diagnostics.Add(TrimDiagnosticOutput(remoteInfoOut));
            }

            var remoteInspectOut = string.Empty;
            var remoteInspectOk = await Task.Run(() => TryRunDockerCommand(
                string.Format("image inspect \"{0}\"", repoTag),
                contextName,
                out remoteInspectOut,
                diagTimeoutMs));
            diagnostics.Add(string.Format(
                "[远端目标镜像 inspect] {0}",
                remoteInspectOk ? "已存在" : "不存在/失败"));
            if (!remoteInspectOk && !string.IsNullOrWhiteSpace(remoteInspectOut))
            {
                diagnostics.Add(TrimDiagnosticOutput(remoteInspectOut));
            }

            var localInspectOut = string.Empty;
            var localInspectOk = await Task.Run(() => TryRunDockerCommand(
                string.Format("image inspect \"{0}\"", repoTag),
                null,
                out localInspectOut,
                diagTimeoutMs));
            diagnostics.Add(string.Format(
                "[本机目标镜像 inspect] {0}",
                localInspectOk ? "存在" : "不存在/失败"));
            if (!localInspectOk && !string.IsNullOrWhiteSpace(localInspectOut))
            {
                diagnostics.Add(TrimDiagnosticOutput(localInspectOut));
            }

            var imageId = (imageIdHint ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(imageId) && imageId != "-")
            {
                var remoteByIdOut = string.Empty;
                var remoteByIdOk = await Task.Run(() => TryRunDockerCommand(
                    string.Format("image inspect \"{0}\"", imageId),
                    contextName,
                    out remoteByIdOut,
                    diagTimeoutMs));
                diagnostics.Add(string.Format(
                    "[远端按 ImageId 检查] {0}: {1}",
                    imageId,
                    remoteByIdOk ? "存在" : "不存在/失败"));
                if (!remoteByIdOk && !string.IsNullOrWhiteSpace(remoteByIdOut))
                {
                    diagnostics.Add(TrimDiagnosticOutput(remoteByIdOut));
                }
            }

            diagnostics.Add(string.Format("诊断命令超时阈值：{0}ms", timeoutText));
            return string.Join("\n", diagnostics.Where(line => !string.IsNullOrWhiteSpace(line)));
        }

        private static string TrimDiagnosticOutput(string output)
        {
            var text = (output ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            const int maxLen = 1200;
            if (text.Length <= maxLen)
            {
                return text;
            }

            return text.Substring(0, maxLen) + "...(截断)";
        }

        private async Task<DockerCommandRunResult> RunDockerCommandWithHardTimeoutAsync(string commandArgs, string contextName, int commandTimeoutMs, int hardTimeoutMs)
        {
            var runTask = Task.Run(() =>
            {
                string outText;
                var ok = TryRunDockerCommand(commandArgs, contextName, out outText, commandTimeoutMs);
                return new DockerCommandRunResult(ok, outText, false);
            });

            var completed = await Task.WhenAny(runTask, Task.Delay(hardTimeoutMs));
            if (completed != runTask)
            {
                return new DockerCommandRunResult(false, "命令执行未在硬超时内返回。", true);
            }

            return await runTask;
        }

        private async Task<DockerCommandRunResult> RunSshDockerInfoWithHardTimeoutAsync(string contextName, int commandTimeoutMs, int hardTimeoutMs)
        {
            var runTask = Task.Run(() =>
            {
                var ctx = (contextName ?? string.Empty).Trim();
                if (!ctx.StartsWith(SshContextPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return new DockerCommandRunResult(false, "无效的 SSH 上下文。", false);
                }

                var identity = ctx.Substring(SshContextPrefix.Length).Trim();
                string user;
                string host;
                string password;
                if (!TryResolveSshTarget(identity, out user, out host, out password))
                {
                    return new DockerCommandRunResult(false, "未找到 SSH 目标或密码缓存。", false);
                }

                string stdOut;
                string stdErr;
                var ok = ExecuteSshRemoteCommand(user, host, password, "docker info --format \"{{.ServerVersion}}\"", commandTimeoutMs, out stdOut, out stdErr);
                var merged = MergeProcessStreams(stdOut, stdErr);
                if (ok)
                {
                    return new DockerCommandRunResult(true, merged, false);
                }

                var escapedPassword = EscapeShellSingleQuoted(password);
                var sudoCmd = string.Format(
                    "printf '%s\\n' '{0}' | sudo -S -p '' docker info --format \"{{{{.ServerVersion}}}}\"",
                    escapedPassword);
                ok = ExecuteSshRemoteCommand(user, host, password, sudoCmd, commandTimeoutMs, out stdOut, out stdErr);
                merged = MergeProcessStreams(stdOut, stdErr);
                return new DockerCommandRunResult(ok, merged, false);
            });

            var completed = await Task.WhenAny(runTask, Task.Delay(hardTimeoutMs));
            if (completed != runTask)
            {
                return new DockerCommandRunResult(false, "命令执行未在硬超时内返回。", true);
            }

            return await runTask;
        }

        private sealed class DockerCommandRunResult
        {
            public DockerCommandRunResult(bool ok, string output, bool isHardTimeout)
            {
                Ok = ok;
                Output = output ?? string.Empty;
                IsHardTimeout = isHardTimeout;
            }

            public bool Ok { get; }
            public string Output { get; }
            public bool IsHardTimeout { get; }
        }

        private async Task<bool> SaveImageFromContextToTarAsync(string contextName, string repoTag, string outputTarPath)
        {
            var outText = string.Empty;
            var cmd = string.Format("image save \"{0}\" -o \"{1}\"", repoTag, outputTarPath);
            return await Task.Run(() => TryRunDockerCommand(cmd, contextName, out outText, 60000));
        }

        private async Task<bool> LoadImageTarToContextAsync(string contextName, string tarPath)
        {
            var outText = string.Empty;
            var cmd = string.Format("image load -i \"{0}\"", tarPath);
            return await Task.Run(() => TryRunDockerCommand(cmd, contextName, out outText, 60000));
        }

        private string ResolvePreferredDeployContextName(string contextName)
        {
            var name = (contextName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            if (!name.StartsWith(SshContextPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }

            var identity = name.Substring(SshContextPrefix.Length).Trim();
            if (string.IsNullOrWhiteSpace(identity))
            {
                return name;
            }

            var expectedHost = "ssh://" + identity;
            var contexts = ReadDockerContexts()
                .Where(IsSshContext)
                .ToList();
            for (var i = 0; i < contexts.Count; i++)
            {
                var ctx = contexts[i];
                if (string.Equals((ctx.DockerHost ?? string.Empty).Trim(), expectedHost, StringComparison.OrdinalIgnoreCase))
                {
                    return ctx.Name;
                }
            }

            return name;
        }

        private string BuildDockerConnectivityHint(string contextName)
        {
            var name = (contextName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                return "1) docker context ls";
            }

            if (name.StartsWith(SshContextPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var identity = name.Substring(SshContextPrefix.Length).Trim();
                if (!string.IsNullOrWhiteSpace(identity))
                {
                    return string.Format(
                        "1) docker --host \"ssh://{0}\" info\n2) docker --host \"ssh://{0}\" images",
                        identity);
                }
            }

            return string.Format(
                "1) docker --context \"{0}\" info\n2) docker --context \"{0}\" images",
                name);
        }

        private static void TryDeleteFileQuietly(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private bool TryCopyLocalFileToRemoteByScp(string targetIdentity, string localPath, string remotePath, int timeoutMs, out string detail)
        {
            detail = string.Empty;
            try
            {
                if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
                {
                    detail = "本地待上传文件不存在。";
                    return false;
                }

                string user;
                string host;
                string password;
                if (!TryResolveSshTarget(targetIdentity, out user, out host, out password))
                {
                    detail = "未找到 SSH 目标或密码缓存。";
                    return false;
                }

                var sshExe = ResolveSshExecutablePath();
                var scpExe = ResolveScpExecutablePath(sshExe);
                if (string.IsNullOrWhiteSpace(scpExe))
                {
                    detail = "未找到可用的 scp.exe。";
                    return false;
                }

                var askPassFile = Path.Combine(Path.GetTempPath(), "scp_askpass_" + Guid.NewGuid().ToString("N") + ".cmd");
                try
                {
                    File.WriteAllText(askPassFile, "@echo off\r\necho " + password + "\r\n", new UTF8Encoding(false));
                    var args = string.Format(
                        "-o BatchMode=no -o PreferredAuthentications=password,keyboard-interactive -o PubkeyAuthentication=no -o NumberOfPasswordPrompts=1 -o StrictHostKeyChecking=no -o UserKnownHostsFile=NUL \"{0}\" {1}@{2}:\"{3}\"",
                        localPath,
                        user,
                        host,
                        remotePath);

                    var psi = new ProcessStartInfo
                    {
                        FileName = scpExe,
                        Arguments = args,
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
                            detail = "无法启动 scp 进程。";
                            return false;
                        }

                        var stdOutTask = process.StandardOutput.ReadToEndAsync();
                        var stdErrTask = process.StandardError.ReadToEndAsync();
                        if (!process.WaitForExit(timeoutMs))
                        {
                            try { process.Kill(); } catch { }
                            detail = "scp 执行超时。";
                            return false;
                        }

                        string stdOutRaw;
                        string stdErrRaw;
                        CollectProcessStreams(stdOutTask, stdErrTask, 3000, out stdOutRaw, out stdErrRaw);
                        var stdOut = (stdOutRaw ?? string.Empty).Trim();
                        var stdErr = (stdErrRaw ?? string.Empty).Trim();
                        detail = string.IsNullOrWhiteSpace(stdErr) ? stdOut : (stdOut + Environment.NewLine + stdErr).Trim();
                        return process.ExitCode == 0;
                    }
                }
                finally
                {
                    TryDeleteFileQuietly(askPassFile);
                }
            }
            catch (Exception ex)
            {
                detail = ex.Message;
                return false;
            }
        }

        private static string ResolveScpExecutablePath(string sshExecutable)
        {
            var ssh = (sshExecutable ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(ssh))
            {
                try
                {
                    var dir = Path.GetDirectoryName(ssh);
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        var candidate = Path.Combine(dir, "scp.exe");
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                }
                catch
                {
                }
            }

            return "scp";
        }
    }
}
