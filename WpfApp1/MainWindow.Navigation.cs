using System.Windows.Controls;
using System.Windows.Media;
using System.Windows;
using System;

namespace WpfApp1
{
    public partial class MainWindow
    {
        private string _activeSidebarKey = string.Empty;

        private void SetSidebarSelected(string key)
        {
            var normalizedKey = key ?? string.Empty;
            var changed = !string.Equals(_activeSidebarKey, normalizedKey, StringComparison.OrdinalIgnoreCase);
            if (changed)
            {
                ResetSelectionsForSidebarSwitch();
                _activeSidebarKey = normalizedKey;
                if (!string.Equals(_activeSidebarKey, "ServiceContainer", StringComparison.OrdinalIgnoreCase))
                {
                    CloseContainerLoadingWindow();
                }
            }

            ApplySidebarItemState(AwarenessButton, AwarenessText, key == "Awareness", 19, 15);
            ApplySidebarItemState(ServiceImageButton, ServiceImageText, key == "ServiceImage", 19, 15);
            ApplySidebarItemState(ServiceContainerButton, ServiceContainerText, key == "ServiceContainer", 19, 15);
            ApplySidebarItemState(StrategyButton, StrategyText, key == "Strategy", 19, 15);
        }

        private void ResetSelectionsForSidebarSwitch()
        {
            ClearDataGridSelection(DeviceImageGrid);
            ClearDataGridSelection(SourceImageGrid);
            ClearDataGridSelection(PreparedImageGrid);
            ClearDataGridSelection(ProgramFileGrid);
            ClearDataGridSelection(ContainerComposeGrid);
            ClearDataGridSelection(StrategyLogDataGrid);
            ClearDataGridSelection(StrategyImageItemsControl);

            _stickySelectedBaseRepoTag = string.Empty;
            _stickySelectedProgramKeys.Clear();
            _stickySelectedPreparedImageKeys.Clear();
        }

        private static void ClearDataGridSelection(DataGrid grid)
        {
            if (grid == null)
            {
                return;
            }

            try
            {
                grid.UnselectAll();
                grid.SelectedItem = null;
            }
            catch
            {
            }
        }

        private static void ApplySidebarItemState(Button button, TextBlock text, bool isSelected, double selectedFontSize, double normalFontSize)
        {
            if (isSelected)
            {
                button.Background = new SolidColorBrush(Color.FromRgb(212, 234, 250));
                button.BorderBrush = new SolidColorBrush(Color.FromRgb(133, 178, 211));
                text.FontWeight = FontWeights.Bold;
                text.FontSize = selectedFontSize;
                text.Foreground = new SolidColorBrush(Color.FromRgb(33, 58, 88));
                return;
            }

            button.Background = Brushes.White;
            button.BorderBrush = new SolidColorBrush(Color.FromRgb(235, 238, 243));
            text.FontWeight = FontWeights.Normal;
            text.FontSize = normalFontSize;
            text.Foreground = new SolidColorBrush(Color.FromRgb(48, 59, 74));
        }
    }
}
