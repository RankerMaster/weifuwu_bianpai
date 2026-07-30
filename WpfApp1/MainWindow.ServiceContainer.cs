using System.Windows;

namespace WpfApp1
{
    public partial class MainWindow
    {
        private async void ServiceContainerButton_OnClick(object sender, RoutedEventArgs e)
        {
            ShowServiceContainerPanel();
            SetSidebarSelected("ServiceContainer");
            Window progressWindow = null;
            try
            {
                progressWindow = ShowContainerLoadingWindowIfActive("正在加载容器编排数据...");
                if (progressWindow == null)
                {
                    return;
                }

                if (!_hasReadDeviceInfo)
                {
                    await RefreshRuntimeDataBindingsAsync(false, false);
                    return;
                }

                if (_devices.Count == 0 || ContainerDeviceSelector.Items.Count == 0)
                {
                    _suppressContainerDeviceSelectionRefresh = true;
                    try
                    {
                        await RefreshRuntimeDataBindingsAsync(true, false);
                    }
                    finally
                    {
                        _suppressContainerDeviceSelectionRefresh = false;
                    }
                }

                var contextName = GetSelectedContainerContextName();
                if (!string.IsNullOrWhiteSpace(contextName))
                {
                    await RefreshContainerRowsForContextAsync(contextName);
                }
            }
            finally
            {
                if (progressWindow != null)
                {
                    try
                    {
                        progressWindow.Close();
                        if (ReferenceEquals(_containerLoadingWindow, progressWindow))
                        {
                            _containerLoadingWindow = null;
                        }
                    }
                    catch
                    {
                    }
                }
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
    }
}
