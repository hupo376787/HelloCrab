using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HelloCrab.Core.Services.Localization;
using HelloCrab.Core.ViewModels;

namespace HelloCrab.Core.Views;

public partial class MainWindow
{
    private static readonly IDisposable HistoryAndCurrentWorkLayoutDataContextHandler =
        StyledElement.DataContextProperty.Changed.AddClassHandler<MainWindow>((window, _) =>
            Dispatcher.UIThread.Post(
                window.EnsureHistoryAndCurrentWorkLayout,
                DispatcherPriority.Loaded));

    private MainWindowViewModel? _historyLayoutViewModel;
    private TextBlock? _historyAuthorCountText;
    private int _historyLayoutInstallAttempts;
    private bool _historyLayoutClosedHooked;

    private void EnsureHistoryAndCurrentWorkLayout()
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        var localization = LocalizationService.Current;
        var descendants = this.GetVisualDescendants().ToArray();

        // 下载历史标题：保留原来的 DynamicResource 标题，在后面追加当前实际显示的作者数量。
        if (_historyAuthorCountText is null)
        {
            var historyTitle = localization?.Get("History.Title", "下载历史") ?? "下载历史";
            var titleBlock = descendants
                .OfType<TextBlock>()
                .FirstOrDefault(textBlock => string.Equals(
                    textBlock.Text,
                    historyTitle,
                    StringComparison.Ordinal));

            if (titleBlock?.Parent is StackPanel headerPanel)
            {
                var titleIndex = headerPanel.Children.IndexOf(titleBlock);
                if (titleIndex >= 0)
                {
                    headerPanel.Children.RemoveAt(titleIndex);

                    var titleRow = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    titleRow.Children.Add(titleBlock);

                    var countText = new TextBlock
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 2, 0, 0),
                        FontWeight = Avalonia.Media.FontWeight.Medium
                    };
                    countText.Classes.Add("caption");
                    titleRow.Children.Add(countText);

                    headerPanel.Children.Insert(titleIndex, titleRow);
                    _historyAuthorCountText = countText;
                }
            }
        }

        // 删除“History.json 保存位置”提示块，只删除界面元素，不影响历史文件本身。
        var historyFileHint = localization?.Get(
                                  "History.FileHint",
                                  "历史记录保存在程序 exe 同目录的 History.json 中。")
                              ?? "历史记录保存在程序 exe 同目录的 History.json 中。";
        var fileHintText = descendants
            .OfType<TextBlock>()
            .FirstOrDefault(textBlock => string.Equals(
                textBlock.Text,
                historyFileHint,
                StringComparison.Ordinal));
        if (fileHintText?.Parent is Border fileHintBorder
            && fileHintBorder.Parent is Panel fileHintParent)
        {
            fileHintParent.Children.Remove(fileHintBorder);
        }

        // 当前作品详情卡片固定高度；作者昵称移到最上方并突出显示。
        var currentWorkLabel = localization?.Get("Metrics.CurrentWork", "当前作品") ?? "当前作品";
        var currentWorkText = descendants
            .OfType<TextBlock>()
            .FirstOrDefault(textBlock => string.Equals(
                textBlock.Text,
                currentWorkLabel,
                StringComparison.Ordinal));
        if (currentWorkText?.Parent is StackPanel currentWorkDetails
            && currentWorkDetails.Parent is Grid currentWorkGrid
            && currentWorkGrid.Parent is Border currentWorkCard)
        {
            currentWorkCard.Height = 156;
            currentWorkCard.MinHeight = 156;
            currentWorkCard.MaxHeight = 156;
            currentWorkCard.ClipToBounds = true;

            var authorNameText = currentWorkDetails.Children
                .OfType<TextBlock>()
                .FirstOrDefault(textBlock => textBlock.Classes.Contains("emojiText"));
            if (authorNameText is not null)
            {
                var currentIndex = currentWorkDetails.Children.IndexOf(authorNameText);
                if (currentIndex > 0)
                {
                    currentWorkDetails.Children.RemoveAt(currentIndex);
                    currentWorkDetails.Children.Insert(0, authorNameText);
                }

                // 昵称是当前下载对象的主信息，不再使用 caption 的小号次级文字样式。
                authorNameText.Classes.Remove("caption");
                authorNameText.FontSize = 18;
                authorNameText.FontWeight = Avalonia.Media.FontWeight.Bold;
                authorNameText.TextWrapping = Avalonia.Media.TextWrapping.NoWrap;
                authorNameText.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
                authorNameText.Margin = new Thickness(0, 0, 0, 1);
            }
        }

        if (_historyAuthorCountText is null)
        {
            if (_historyLayoutInstallAttempts++ < 5)
            {
                Dispatcher.UIThread.Post(
                    EnsureHistoryAndCurrentWorkLayout,
                    DispatcherPriority.Background);
            }
            return;
        }

        _historyLayoutInstallAttempts = 0;
        AttachHistoryLayoutViewModel(viewModel);
        RefreshHistoryAuthorCountText();

        if (!_historyLayoutClosedHooked)
        {
            _historyLayoutClosedHooked = true;
            Closed += HistoryAndCurrentWorkLayoutWindow_Closed;
            if (localization is not null)
                localization.LanguageChanged += HistoryAndCurrentWorkLayoutLanguageChanged;
        }
    }

    private void AttachHistoryLayoutViewModel(MainWindowViewModel viewModel)
    {
        if (ReferenceEquals(_historyLayoutViewModel, viewModel))
            return;

        if (_historyLayoutViewModel is not null)
        {
            _historyLayoutViewModel.FilteredDownloadHistory.CollectionChanged -=
                HistoryVisibleCollectionChanged;
        }

        _historyLayoutViewModel = viewModel;
        viewModel.FilteredDownloadHistory.CollectionChanged += HistoryVisibleCollectionChanged;
    }

    private void HistoryVisibleCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => Dispatcher.UIThread.Post(RefreshHistoryAuthorCountText);

    private void HistoryAndCurrentWorkLayoutLanguageChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(RefreshHistoryAuthorCountText);

    private void RefreshHistoryAuthorCountText()
    {
        if (_historyAuthorCountText is null || _historyLayoutViewModel is null)
            return;

        var count = _historyLayoutViewModel.FilteredDownloadHistory.Count;
        var code = LocalizationService.Current?.CurrentLanguageCode ?? "zh-CN";
        _historyAuthorCountText.Text = code.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? $"共{count}个作者"
            : code.StartsWith("ja", StringComparison.OrdinalIgnoreCase)
                ? $"投稿者 {count} 人"
                : count == 1
                    ? "1 author"
                    : $"{count} authors";
    }

    private void HistoryAndCurrentWorkLayoutWindow_Closed(object? sender, EventArgs e)
    {
        if (_historyLayoutViewModel is not null)
        {
            _historyLayoutViewModel.FilteredDownloadHistory.CollectionChanged -=
                HistoryVisibleCollectionChanged;
            _historyLayoutViewModel = null;
        }

        if (LocalizationService.Current is { } localization)
            localization.LanguageChanged -= HistoryAndCurrentWorkLayoutLanguageChanged;

        Closed -= HistoryAndCurrentWorkLayoutWindow_Closed;
    }
}
