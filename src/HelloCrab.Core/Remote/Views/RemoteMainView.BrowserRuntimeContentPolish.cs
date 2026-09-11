using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Threading;
using HelloCrab.Core.Remote.ViewModels;

namespace HelloCrab.Core.Remote.Views;

/// <summary>
/// Browser-only polish for the runtime center column:
/// - grow the log list to the bottom of the current browser viewport in wide layout;
/// - render CurrentWork and log text through the same Twemoji pipeline used by history names,
///   because Browser/WASM cannot reliably use the client system emoji font.
/// </summary>
public partial class RemoteMainView
{
    private const double BrowserLogBottomMargin = 18d;
    private const double BrowserLogMinimumHeight = 270d;
    private const double BrowserLogMaximumHeight = 1200d;

    private static readonly IDisposable BrowserRuntimeContentDataContextHandler =
        StyledElement.DataContextProperty.Changed.AddClassHandler<RemoteMainView>((view, _) =>
            Dispatcher.UIThread.Post(
                view.InitializeBrowserRuntimeContentPolish,
                DispatcherPriority.Loaded));

    private bool _browserRuntimeContentPolishInitialized;
    private int _browserRuntimeContentInstallAttempts;
    private bool _browserLogSizingQueued;
    private int _browserLogSizingRetryCount;
    private ListBox? _browserEmojiLogList;
    private TextBlock? _browserCurrentWorkTextBlock;
    private RemoteMainViewModel? _browserRuntimeContentViewModel;

    private void InitializeBrowserRuntimeContentPolish()
    {
        if (!OperatingSystem.IsBrowser())
            return;

        if (_browserRuntimeContentPolishInitialized)
        {
            if (DataContext is RemoteMainViewModel existingViewModel)
                BindBrowserRuntimeContentViewModel(existingViewModel);
            QueueBrowserLogBottomSizing();
            return;
        }

        if (!_browserWideLayoutInitialized
            || !_finalRemoteUiPolishInitialized
            || _browserLogsCard is null
            || _browserStatusCard is null
            || DataContext is not RemoteMainViewModel viewModel)
        {
            RetryBrowserRuntimeContentPolishInitialization();
            return;
        }

        _browserEmojiLogList = FindRemoteHistoryList(_browserLogsCard);
        _browserCurrentWorkTextBlock = FindBrowserCurrentWorkTextBlock();
        if (_browserEmojiLogList is null || _browserCurrentWorkTextBlock is null)
        {
            RetryBrowserRuntimeContentPolishInitialization();
            return;
        }

        ConfigureBrowserEmojiLogList(_browserEmojiLogList);
        BindBrowserRuntimeContentViewModel(viewModel);

        _browserRuntimeContentPolishInitialized = true;
        _browserRuntimeContentInstallAttempts = 0;

        SizeChanged += (_, _) => QueueBrowserLogBottomSizing();
        QueueBrowserLogBottomSizing();
    }

    private void RetryBrowserRuntimeContentPolishInitialization()
    {
        if (_browserRuntimeContentInstallAttempts++ >= 32)
            return;

        Dispatcher.UIThread.Post(
            InitializeBrowserRuntimeContentPolish,
            DispatcherPriority.Background);
    }

    private TextBlock? FindBrowserCurrentWorkTextBlock()
    {
        if (_browserStatusCard is not Border { Child: Grid statusGrid })
            return null;

        var currentWorkHost = statusGrid.Children
            .OfType<StackPanel>()
            .FirstOrDefault(panel => Grid.GetRow(panel) == 2 && Grid.GetColumn(panel) == 1);

        return currentWorkHost?.Children.OfType<TextBlock>().FirstOrDefault();
    }

    private void ConfigureBrowserEmojiLogList(ListBox logList)
    {
        logList.ItemTemplate = new FuncDataTemplate<string>(
            (text, _) => CreateBrowserEmojiLogRow(text),
            supportsRecycling: true);
    }

    private static Control CreateBrowserEmojiLogRow(string? initialText)
    {
        var textBlock = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 5),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        textBlock.DataContextChanged += (_, _) => RefreshBrowserEmojiLogRow(textBlock);
        textBlock.AttachedToVisualTree += (_, _) => RefreshBrowserEmojiLogRow(textBlock);

        RenderRemoteHistoryEmojiName(textBlock, initialText ?? string.Empty);
        return textBlock;
    }

    private static void RefreshBrowserEmojiLogRow(TextBlock textBlock)
    {
        var value = textBlock.DataContext as string ?? string.Empty;
        RenderRemoteHistoryEmojiName(textBlock, value);
    }

    private void BindBrowserRuntimeContentViewModel(RemoteMainViewModel viewModel)
    {
        if (ReferenceEquals(_browserRuntimeContentViewModel, viewModel))
        {
            RefreshBrowserCurrentWorkEmoji();
            return;
        }

        if (_browserRuntimeContentViewModel is not null)
            _browserRuntimeContentViewModel.PropertyChanged -= BrowserRuntimeContentViewModel_PropertyChanged;

        _browserRuntimeContentViewModel = viewModel;
        _browserRuntimeContentViewModel.PropertyChanged += BrowserRuntimeContentViewModel_PropertyChanged;

        _browserCurrentWorkTextBlock?.ClearValue(TextBlock.TextProperty);
        RefreshBrowserCurrentWorkEmoji();
    }

    private void BrowserRuntimeContentViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RemoteMainViewModel.CurrentWork))
            return;

        if (Dispatcher.UIThread.CheckAccess())
            RefreshBrowserCurrentWorkEmoji();
        else
            Dispatcher.UIThread.Post(RefreshBrowserCurrentWorkEmoji, DispatcherPriority.Background);
    }

    private void RefreshBrowserCurrentWorkEmoji()
    {
        if (_browserCurrentWorkTextBlock is null || _browserRuntimeContentViewModel is null)
            return;

        RenderRemoteHistoryEmojiName(
            _browserCurrentWorkTextBlock,
            _browserRuntimeContentViewModel.CurrentWork ?? string.Empty);
    }

    private void QueueBrowserLogBottomSizing()
    {
        if (!OperatingSystem.IsBrowser() || _browserLogSizingQueued)
            return;

        _browserLogSizingQueued = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _browserLogSizingQueued = false;
                if (UpdateBrowserLogBottomSizing())
                {
                    _browserLogSizingRetryCount = 0;
                    return;
                }

                if (_browserLogSizingRetryCount++ < 8)
                    QueueBrowserLogBottomSizing();
            },
            DispatcherPriority.Loaded);
    }

    private bool UpdateBrowserLogBottomSizing()
    {
        if (_browserEmojiLogList is null || _browserLogsCard is null)
            return false;

        if (!_browserWideLayoutActive)
        {
            _browserEmojiLogList.Height = BrowserLogMinimumHeight;
            return true;
        }

        var topLeft = _browserLogsCard.TranslatePoint(default, this);
        if (topLeft is null
            || Bounds.Height <= 0
            || _browserLogsCard.Bounds.Height <= 0
            || _browserEmojiLogList.Bounds.Height <= 0)
        {
            return false;
        }

        var cardChromeHeight = Math.Max(
            0d,
            _browserLogsCard.Bounds.Height - _browserEmojiLogList.Bounds.Height);
        var desiredHeight = Bounds.Height
                            - topLeft.Value.Y
                            - BrowserLogBottomMargin
                            - cardChromeHeight;
        desiredHeight = Math.Clamp(
            desiredHeight,
            BrowserLogMinimumHeight,
            BrowserLogMaximumHeight);

        if (double.IsNaN(_browserEmojiLogList.Height)
            || Math.Abs(_browserEmojiLogList.Height - desiredHeight) > 0.5d)
        {
            _browserEmojiLogList.Height = desiredHeight;
        }

        return true;
    }
}
