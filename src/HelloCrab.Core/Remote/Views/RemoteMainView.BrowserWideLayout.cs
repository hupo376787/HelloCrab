using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;
using HelloCrab.Core.Remote.ViewModels;

namespace HelloCrab.Core.Remote.Views;

/// <summary>
/// Browser-only responsive layout and startup reconnect behavior.
/// Wide browser windows use the same high-level composition as the desktop app:
/// settings/actions on the left, runtime status/logs in the middle, download history on the right.
/// Narrow browser windows keep the original single-column layout.
/// </summary>
public partial class RemoteMainView
{
    private const double BrowserWideLayoutThreshold = 1280d;
    private const double BrowserWideContentMaxWidth = 1760d;
    private const double BrowserWideBottomMargin = 18d;

    private static readonly IDisposable BrowserWideLayoutDataContextHandler =
        StyledElement.DataContextProperty.Changed.AddClassHandler<RemoteMainView>((view, _) =>
            Dispatcher.UIThread.Post(
                view.InitializeBrowserWideLayout,
                DispatcherPriority.Loaded));

    private readonly Dictionary<Control, int> _browserWideOriginalIndices = new();

    private StackPanel? _browserWideRootStack;
    private Grid? _browserWideGrid;
    private ScrollViewer? _browserWideLeftScrollViewer;
    private StackPanel? _browserWideLeftColumn;
    private StackPanel? _browserWideCenterColumn;
    private StackPanel? _browserWideRightColumn;

    private Control? _browserConnectionCard;
    private Control? _browserHostActionsCard;
    private Control? _browserMetricsPanel;
    private Control? _browserStatusCard;
    private Control? _browserSettingsCard;
    private Control? _browserHistoryCard;
    private Control? _browserLogsCard;

    private double _browserNormalContentMaxWidth = 1180d;
    private bool _browserWideLayoutInitialized;
    private bool _browserWideLayoutActive;
    private bool _browserWideSizeHooked;
    private bool _browserAutoConnectAttempted;
    private int _browserWideInstallAttempts;

    private void InitializeBrowserWideLayout()
    {
        if (!OperatingSystem.IsBrowser())
            return;

        TryAutoConnectPreviousBrowserServer();

        if (RootScrollViewer?.Content is not StackPanel rootStack
            || !_remoteHistoryUiInitialized)
        {
            RetryBrowserWideLayoutInitialization();
            return;
        }

        if (!_browserWideLayoutInitialized)
        {
            if (!CaptureBrowserWideLayoutControls(rootStack))
            {
                RetryBrowserWideLayoutInitialization();
                return;
            }

            _browserWideLayoutInitialized = true;
            _browserWideInstallAttempts = 0;
        }

        if (!_browserWideSizeHooked)
        {
            _browserWideSizeHooked = true;
            SizeChanged += (_, _) => UpdateBrowserWideLayout();
        }

        UpdateBrowserWideLayout();
    }

    private void RetryBrowserWideLayoutInitialization()
    {
        if (_browserWideInstallAttempts++ >= 12)
            return;

        Dispatcher.UIThread.Post(
            InitializeBrowserWideLayout,
            DispatcherPriority.Background);
    }

    private bool CaptureBrowserWideLayoutControls(StackPanel rootStack)
    {
        _browserWideRootStack = rootStack;
        _browserNormalContentMaxWidth = rootStack.MaxWidth;

        _browserConnectionCard = FindSectionCard(rootStack, "连接桌面客户端");
        _browserHostActionsCard = FindSectionCard(rootStack, "主机操作");
        _browserSettingsCard = FindSectionCard(rootStack, "下载设置（自动读取桌面主机）");
        _browserHistoryCard = FindSectionCard(rootStack, "下载历史");
        _browserLogsCard = FindSectionCard(rootStack, "最新日志");
        _browserStatusCard = FindBrowserStatusCard(rootStack);
        _browserMetricsPanel = rootStack.Children
            .OfType<WrapPanel>()
            .FirstOrDefault();

        var controls = GetBrowserWideMovedControls();
        if (controls.Count != 7 || controls.Any(control => control is null))
            return false;

        _browserWideOriginalIndices.Clear();
        foreach (var control in controls)
        {
            var index = rootStack.Children.IndexOf(control);
            if (index < 0)
                return false;

            _browserWideOriginalIndices[control] = index;
        }

        return true;
    }

    private static Border? FindBrowserStatusCard(StackPanel rootStack)
    {
        foreach (var border in rootStack.Children.OfType<Border>())
        {
            if (border.Child is not Grid grid)
                continue;

            if (grid.Children.OfType<TextBlock>().Any(text =>
                    string.Equals(text.Text, "状态", StringComparison.Ordinal)))
            {
                return border;
            }
        }

        return null;
    }

    private List<Control> GetBrowserWideMovedControls()
    {
        var result = new List<Control>(7);
        AddIfNotNull(result, _browserConnectionCard);
        AddIfNotNull(result, _browserHostActionsCard);
        AddIfNotNull(result, _browserMetricsPanel);
        AddIfNotNull(result, _browserStatusCard);
        AddIfNotNull(result, _browserSettingsCard);
        AddIfNotNull(result, _browserHistoryCard);
        AddIfNotNull(result, _browserLogsCard);
        return result;
    }

    private static void AddIfNotNull(List<Control> target, Control? control)
    {
        if (control is not null)
            target.Add(control);
    }

    private void UpdateBrowserWideLayout()
    {
        if (!OperatingSystem.IsBrowser()
            || !_browserWideLayoutInitialized
            || _browserWideRootStack is null)
        {
            return;
        }

        var useWideLayout = Bounds.Width >= BrowserWideLayoutThreshold;
        if (useWideLayout)
        {
            if (!_browserWideLayoutActive)
                ApplyBrowserWideLayout();

            UpdateBrowserWideLeftScrollHeight();
            return;
        }

        if (_browserWideLayoutActive)
            RestoreBrowserNarrowLayout();
    }

    private void EnsureBrowserWideGrid()
    {
        if (_browserWideGrid is not null)
            return;

        _browserWideLeftColumn = new StackPanel
        {
            Spacing = 14,
            VerticalAlignment = VerticalAlignment.Top
        };

        _browserWideLeftScrollViewer = new ScrollViewer
        {
            Content = _browserWideLeftColumn,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            IsScrollChainingEnabled = false
        };

        _browserWideCenterColumn = new StackPanel
        {
            Spacing = 14,
            VerticalAlignment = VerticalAlignment.Top
        };

        _browserWideRightColumn = new StackPanel
        {
            Spacing = 14,
            VerticalAlignment = VerticalAlignment.Top
        };

        _browserWideGrid = new Grid
        {
            ColumnSpacing = 14,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top
        };
        _browserWideGrid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(360)
        });
        _browserWideGrid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        _browserWideGrid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(360)
        });

        Grid.SetColumn(_browserWideLeftScrollViewer, 0);
        Grid.SetColumn(_browserWideCenterColumn, 1);
        Grid.SetColumn(_browserWideRightColumn, 2);
        _browserWideGrid.Children.Add(_browserWideLeftScrollViewer);
        _browserWideGrid.Children.Add(_browserWideCenterColumn);
        _browserWideGrid.Children.Add(_browserWideRightColumn);
    }

    private void ApplyBrowserWideLayout()
    {
        if (_browserWideRootStack is null
            || _browserConnectionCard is null
            || _browserHostActionsCard is null
            || _browserMetricsPanel is null
            || _browserStatusCard is null
            || _browserSettingsCard is null
            || _browserHistoryCard is null
            || _browserLogsCard is null)
        {
            return;
        }

        EnsureBrowserWideGrid();
        if (_browserWideGrid is null
            || _browserWideLeftScrollViewer is null
            || _browserWideLeftColumn is null
            || _browserWideCenterColumn is null
            || _browserWideRightColumn is null)
        {
            return;
        }

        if (_browserWideGrid.Parent is not Panel)
        {
            var insertIndex = Math.Min(1, _browserWideRootStack.Children.Count);
            _browserWideRootStack.Children.Insert(insertIndex, _browserWideGrid);
        }

        MoveBrowserWideControl(_browserConnectionCard, _browserWideLeftColumn);
        MoveBrowserWideControl(_browserHostActionsCard, _browserWideLeftColumn);
        MoveBrowserWideControl(_browserSettingsCard, _browserWideLeftColumn);

        MoveBrowserWideControl(_browserMetricsPanel, _browserWideCenterColumn);
        MoveBrowserWideControl(_browserStatusCard, _browserWideCenterColumn);
        MoveBrowserWideControl(_browserLogsCard, _browserWideCenterColumn);

        MoveBrowserWideControl(_browserHistoryCard, _browserWideRightColumn);

        _browserWideRootStack.MaxWidth = BrowserWideContentMaxWidth;
        _browserWideLayoutActive = true;

        Dispatcher.UIThread.Post(
            UpdateBrowserWideLeftScrollHeight,
            DispatcherPriority.Loaded);
    }

    private void UpdateBrowserWideLeftScrollHeight()
    {
        if (!_browserWideLayoutActive
            || _browserWideLeftScrollViewer is null
            || _browserWideGrid is null
            || Bounds.Height <= 0)
        {
            return;
        }

        var topLeft = _browserWideGrid.TranslatePoint(default, this);
        if (topLeft is null)
            return;

        var availableHeight = Bounds.Height - topLeft.Value.Y - BrowserWideBottomMargin;
        if (availableHeight > 0)
            _browserWideLeftScrollViewer.Height = availableHeight;
    }

    private void RestoreBrowserNarrowLayout()
    {
        if (_browserWideRootStack is null)
            return;

        if (_browserWideGrid?.Parent is Panel wideParent)
            wideParent.Children.Remove(_browserWideGrid);

        foreach (var entry in _browserWideOriginalIndices.OrderBy(pair => pair.Value))
        {
            RemoveBrowserWideControlFromParent(entry.Key);
            var insertIndex = Math.Clamp(
                entry.Value,
                0,
                _browserWideRootStack.Children.Count);
            _browserWideRootStack.Children.Insert(insertIndex, entry.Key);
        }

        _browserWideRootStack.MaxWidth = _browserNormalContentMaxWidth;
        _browserWideLayoutActive = false;
    }

    private static void MoveBrowserWideControl(Control control, StackPanel target)
    {
        if (ReferenceEquals(control.Parent, target))
            return;

        RemoveBrowserWideControlFromParent(control);
        target.Children.Add(control);
    }

    private static void RemoveBrowserWideControlFromParent(Control control)
    {
        if (control.Parent is Panel panel)
            panel.Children.Remove(control);
    }

    private void TryAutoConnectPreviousBrowserServer()
    {
        if (_browserAutoConnectAttempted
            || DataContext is not RemoteMainViewModel viewModel)
        {
            return;
        }

        _browserAutoConnectAttempted = true;

        // Browser preferences are restored from localStorage before the ViewModel is assigned.
        // Requiring both address and token avoids a first-run attempt against the built-in
        // http://127.0.0.1:5088 placeholder while still reconnecting a previously used host.
        if (string.IsNullOrWhiteSpace(viewModel.ServerAddress)
            || string.IsNullOrWhiteSpace(viewModel.AccessToken))
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                if (DataContext is not RemoteMainViewModel current
                    || !ReferenceEquals(current, viewModel)
                    || current.IsConnected
                    || current.IsConnecting
                    || !current.ConnectCommand.CanExecute(null))
                {
                    return;
                }

                _ = current.ConnectCommand.ExecuteAsync(null);
            },
            DispatcherPriority.Background);
    }
}
