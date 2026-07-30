using System.Windows;

namespace WpfApp1
{
    public partial class MainWindow
    {
        private void AwarenessButton_OnClick(object sender, RoutedEventArgs e)
        {
            ShowDashboardPanel();
            SetSidebarSelected("Awareness");
            _ = RefreshRuntimeDataBindingsAsync(false, false);
        }

        private void ShowDashboardPanel()
        {
            StrategyPanel.Visibility = Visibility.Collapsed;
            ServiceImagePanel.Visibility = Visibility.Collapsed;
            ServiceContainerPanel.Visibility = Visibility.Collapsed;
            DashboardTopPanel.Visibility = Visibility.Visible;
            DashboardMainPanel.Visibility = Visibility.Visible;
        }
    }
}
