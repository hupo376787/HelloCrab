using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace HelloCrab.Core.Remote.Views;

/// <summary>
/// 网页宽屏布局细节修正：左栏加宽，使“主机操作”的四个按钮稳定按两列显示；
/// 右侧历史栏也略微加宽，给作者信息留出更多横向空间。
/// </summary>
public partial class RemoteMainView
{
    private static readonly IDisposable BrowserWidePolishDataContextHandler =
        StyledElement.DataContextProperty.Changed.AddClassHandler<RemoteMainView>((view, _) =>
            Dispatcher.UIThread.Post(
                view.InitializeBrowserWidePolish,
                DispatcherPriority.Background));

    private bool _browserWidePolishInitialized;
    private int _browserWidePolishInstallAttempts;

    private void InitializeBrowserWidePolish()
    {
        if (_browserWidePolishInitialized || !OperatingSystem.IsBrowser())
            return;

        if (!_browserWideLayoutInitialized
            || _browserWideGrid is null
            || _browserWideGrid.ColumnDefinitions.Count < 3)
        {
            if (_browserWidePolishInstallAttempts++ < 16)
            {
                Dispatcher.UIThread.Post(
                    InitializeBrowserWidePolish,
                    DispatcherPriority.Background);
            }
            return;
        }

        _browserWideGrid.ColumnDefinitions[0].Width = new GridLength(430);
        _browserWideGrid.ColumnDefinitions[2].Width = new GridLength(390);
        _browserWidePolishInitialized = true;
    }
}
