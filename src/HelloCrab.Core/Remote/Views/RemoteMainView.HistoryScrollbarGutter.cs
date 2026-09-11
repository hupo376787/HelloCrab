using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Threading;

namespace HelloCrab.Core.Remote.Views;

/// <summary>
/// 浏览器下载历史列表为 Fluent 覆盖式纵向滚动条预留固定右侧安全区。
/// Fluent ScrollBar 收起时 Thumb 会缩窄，指针悬停/拖动后恢复完整宽度；
/// 如果 Item 内容一直贴到最右侧，展开后的滚动条会覆盖作者操作按钮。
/// </summary>
public partial class RemoteMainView
{
    private const double BrowserHistoryScrollbarGutter = 20d;

    private static readonly IDisposable RemoteHistoryScrollbarGutterDataContextHandler =
        StyledElement.DataContextProperty.Changed.AddClassHandler<RemoteMainView>((view, _) =>
            Dispatcher.UIThread.Post(
                view.InitializeBrowserHistoryScrollbarGutter,
                DispatcherPriority.Background));

    private bool _browserHistoryScrollbarGutterInitialized;
    private int _browserHistoryScrollbarGutterInstallAttempts;

    private void InitializeBrowserHistoryScrollbarGutter()
    {
        if (_browserHistoryScrollbarGutterInitialized || !OperatingSystem.IsBrowser())
            return;

        if (!_finalRemoteUiPolishInitialized || _finalBrowserHistoryList is null)
        {
            if (_browserHistoryScrollbarGutterInstallAttempts++ < 24)
            {
                Dispatcher.UIThread.Post(
                    InitializeBrowserHistoryScrollbarGutter,
                    DispatcherPriority.Background);
            }
            return;
        }

        _browserHistoryScrollbarGutterInitialized = true;

        // 只缩进 Item 内容，不改变 ScrollViewer/Thumb 的命中区域。
        // 这样滚动条仍可贴着列表右边缘正常拖动，而“更多”按钮始终位于其左侧。
        var itemStyle = new Style(x => x.OfType<ListBoxItem>());
        itemStyle.Add(new Setter(
            ListBoxItem.PaddingProperty,
            new Thickness(0, 0, BrowserHistoryScrollbarGutter, 0)));
        itemStyle.Add(new Setter(
            ListBoxItem.HorizontalContentAlignmentProperty,
            HorizontalAlignment.Stretch));
        _finalBrowserHistoryList.Styles.Add(itemStyle);
    }
}
