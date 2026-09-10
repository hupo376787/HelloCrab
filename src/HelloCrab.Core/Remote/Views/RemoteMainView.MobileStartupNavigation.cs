using Avalonia;
using Avalonia.Threading;
using HelloCrab.Core.Remote.ViewModels;

namespace HelloCrab.Core.Remote.Views;

/// <summary>
/// 原生手机端启动恢复与系统返回键导航。
/// </summary>
public partial class RemoteMainView
{
    private static readonly IDisposable RemoteMobileStartupDataContextHandler =
        StyledElement.DataContextProperty.Changed.AddClassHandler<RemoteMainView>((view, _) =>
            Dispatcher.UIThread.Post(
                view.TryAutoConnectPreviousMobileServer,
                DispatcherPriority.Loaded));

    private bool _mobileAutoConnectAttempted;

    private void TryAutoConnectPreviousMobileServer()
    {
        if (_mobileAutoConnectAttempted
            || DataContext is not RemoteMainViewModel viewModel
            || !viewModel.IsNativeMobileClient)
        {
            return;
        }

        _mobileAutoConnectAttempted = true;

        // FileRemoteClientPreferencesStore 会在 Android/iOS 私有目录中恢复上次的
        // 主机地址和访问令牌。只有两者都存在时才自动连接，首次启动不发无效请求。
        if (string.IsNullOrWhiteSpace(viewModel.ServerAddress)
            || string.IsNullOrWhiteSpace(viewModel.AccessToken)
            || viewModel.IsConnected
            || viewModel.IsConnecting
            || !viewModel.ConnectCommand.CanExecute(null))
        {
            return;
        }

        _ = viewModel.ConnectCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// Android 系统返回键优先关闭当前的历史子页面/对话框，而不是直接退出 Activity。
    /// 返回 true 表示本次返回事件已经由应用内部消费。
    /// </summary>
    public bool TryHandleSystemBack()
    {
        if (_remoteHistoryDeleteOverlay?.IsVisible == true)
        {
            HideRemoteHistoryDeleteDialog();
            return true;
        }

        if (_mobileHistoryOverlay?.IsVisible == true)
        {
            HideMobileHistoryPage();
            return true;
        }

        return false;
    }
}
