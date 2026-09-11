using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace HelloCrab.Core.Remote.Views;

/// <summary>
/// 浏览器历史搜索的最终属性桥接。
///
/// WASM 下直接监听 TextBox.TextProperty，确保无论 TextChanged 的安装顺序如何，
/// 最终都进入与桌面端共用的 HistoryPinyinMatcher 搜索路径。
/// </summary>
public partial class RemoteMainView
{
    private static readonly IDisposable BrowserHistoryAllPinyinTextHandler =
        TextBox.TextProperty.Changed.AddClassHandler<TextBox>((textBox, _) =>
        {
            if (!OperatingSystem.IsBrowser())
                return;

            var view = textBox.GetVisualAncestors()
                .OfType<RemoteMainView>()
                .FirstOrDefault();
            if (view is null
                || !ReferenceEquals(textBox, view._browserHistorySearchBox))
            {
                return;
            }

            view._remoteHistorySearchText = textBox.Text ?? string.Empty;

            Dispatcher.UIThread.Post(
                view.ApplySharedRemoteHistoryPinyinFilter,
                DispatcherPriority.Background);
        });
}
