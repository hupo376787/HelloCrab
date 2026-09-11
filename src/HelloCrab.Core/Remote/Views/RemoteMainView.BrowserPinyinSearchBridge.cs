using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace HelloCrab.Core.Remote.Views;

/// <summary>
/// 浏览器历史搜索的最终桥接。
///
/// 浏览器端历史搜索此前经历了多层 TextChanged 增强，初始化先后顺序可能导致
/// 最终仍由旧的单读音筛选处理输入。这里直接监听 TextBox.TextProperty，
/// 对浏览器历史搜索框始终执行“全部读音”筛选，避免事件安装顺序影响结果。
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

            // 不依赖旧 TextChanged 事件是否已安装，直接以当前输入作为查询文本。
            view._remoteHistorySearchText = textBox.Text ?? string.Empty;

            Dispatcher.UIThread.Post(
                view.ApplyRemoteHistoryAllPinyinFilter,
                DispatcherPriority.Background);
        });
}
