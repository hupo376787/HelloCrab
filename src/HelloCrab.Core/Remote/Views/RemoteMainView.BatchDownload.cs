using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using HelloCrab.Core.Remote.Services;
using HelloCrab.Core.Remote.ViewModels;

namespace HelloCrab.Core.Remote.Views;

/// <summary>
/// Remote batch-download entry and editor.
/// Native mobile / narrow browser uses a full page; desktop browser uses a modal dialog.
/// </summary>
public partial class RemoteMainView
{
    private const double RemoteBatchPageThreshold = 760d;
    private const string RemoteBatchBackGeometry =
        "M20,11H7.83L13.42,5.41L12,4L4,12L12,20L13.41,18.59L7.83,13H20V11Z";
    private const string RemoteBatchCloseGeometry =
        "M5.64,4.22L12,10.59L18.36,4.22L19.78,5.64L13.41,12L19.78,18.36L18.36,19.78L12,13.41L5.64,19.78L4.22,18.36L10.59,12L4.22,5.64Z";

    private static readonly IDisposable RemoteBatchUiDataContextHandler =
        StyledElement.DataContextProperty.Changed.AddClassHandler<RemoteMainView>((view, _) =>
            Dispatcher.UIThread.Post(view.InitializeRemoteBatchUi, DispatcherPriority.Loaded));

    private bool _remoteBatchUiInitialized;
    private int _remoteBatchInstallAttempts;
    private bool _remoteBatchSubmitting;
    private bool _remoteBatchPageMode;
    private Button? _remoteBatchEntryButton;
    private Grid? _remoteBatchOverlay;
    private Border? _remoteBatchSurface;
    private TextBox? _remoteBatchEditor;
    private TextBlock? _remoteBatchResultText;
    private TextBlock? _remoteBatchTitle;
    private TextBlock? _remoteBatchDescription;
    private Button? _remoteBatchNavButton;
    private PathIcon? _remoteBatchNavIcon;
    private Button? _remoteBatchSubmitButton;
    private RemoteMainViewModel? _remoteBatchThemeViewModel;

    private void InitializeRemoteBatchUi()
    {
        if (DataContext is not RemoteMainViewModel viewModel)
        {
            RetryRemoteBatchUiInitialization();
            return;
        }

        if (_remoteBatchUiInitialized)
        {
            BindRemoteBatchThemeViewModel(viewModel);
            return;
        }

        if (RootScrollViewer?.Content is not StackPanel rootStack
            || Content is not Grid rootGrid)
        {
            RetryRemoteBatchUiInitialization();
            return;
        }

        // Browser wide layout may already have moved the host-actions card out of rootStack.
        // Prefer the root card during normal initialization, then fall back to the captured wide-layout card.
        var hostCard = FindSectionCard(rootStack, "主机操作")
                       ?? _browserHostActionsCard as Border;
        if (hostCard?.Child is not StackPanel hostStack)
        {
            RetryRemoteBatchUiInitialization();
            return;
        }

        AddRemoteBatchEntryButton(hostStack);
        AddRemoteBatchOverlay(rootGrid);
        BindRemoteBatchThemeViewModel(viewModel);

        _remoteBatchUiInitialized = true;
        _remoteBatchInstallAttempts = 0;
        SizeChanged += (_, _) =>
        {
            if (_remoteBatchOverlay?.IsVisible == true)
                ApplyRemoteBatchOverlayMode();
        };
    }

    private void RetryRemoteBatchUiInitialization()
    {
        if (_remoteBatchInstallAttempts++ >= 32)
            return;

        Dispatcher.UIThread.Post(InitializeRemoteBatchUi, DispatcherPriority.Background);
    }

    private void AddRemoteBatchEntryButton(StackPanel hostStack)
    {
        if (hostStack.Children.OfType<Button>().Any(button =>
                string.Equals(button.Content?.ToString(), "批量下载", StringComparison.Ordinal)))
        {
            return;
        }

        _remoteBatchEntryButton = new Button
        {
            Content = "批量下载",
            Height = 46,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        _remoteBatchEntryButton.Classes.Add("action");
        _remoteBatchEntryButton.Classes.Add("coral");
        _remoteBatchEntryButton.Classes.Add("hostAction");
        _remoteBatchEntryButton.Bind(
            IsEnabledProperty,
            new Binding(nameof(RemoteMainViewModel.IsConnected)));
        _remoteBatchEntryButton.Click += (_, _) => ShowRemoteBatchEditor();

        var insertIndex = Math.Max(1, hostStack.Children.Count - 1);
        hostStack.Children.Insert(insertIndex, _remoteBatchEntryButton);
    }

    private void AddRemoteBatchOverlay(Grid rootGrid)
    {
        _remoteBatchEditor = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            PlaceholderText = "在这里粘贴或编辑作者地址，每行一条。允许包含分享文案，桌面端会自动提取每行中的第一个网址。",
            MinHeight = 300,
            Padding = new Thickness(14),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top
        };
        _remoteBatchEditor.Resources["TextControlBackground"] = Brushes.Transparent;
        _remoteBatchEditor.Resources["TextControlBackgroundPointerOver"] = Brushes.Transparent;
        _remoteBatchEditor.Resources["TextControlBackgroundFocused"] = Brushes.Transparent;
        _remoteBatchEditor.Resources["TextControlBackgroundDisabled"] = Brushes.Transparent;
        ScrollViewer.SetHorizontalScrollBarVisibility(_remoteBatchEditor, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_remoteBatchEditor, ScrollBarVisibility.Auto);

        _remoteBatchTitle = new TextBlock
        {
            Text = "批量下载",
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        _remoteBatchDescription = new TextBlock
        {
            Text = "每行输入一个作者主页地址，也可以直接粘贴分享文本。点击确定后发送到桌面端；再次提交的内容会追加到批量任务队列。",
            TextWrapping = TextWrapping.Wrap
        };
        _remoteBatchResultText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false
        };

        _remoteBatchNavIcon = new PathIcon
        {
            Width = 22,
            Height = 22,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _remoteBatchNavButton = new Button
        {
            Width = 42,
            Height = 42,
            MinWidth = 42,
            MinHeight = 42,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = _remoteBatchNavIcon
        };
        _remoteBatchNavButton.Click += (_, _) => HideRemoteBatchEditor();

        var header = new Grid { ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        header.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        header.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        Grid.SetColumn(_remoteBatchTitle, 1);
        Grid.SetColumn(_remoteBatchNavButton, 2);
        header.Children.Add(_remoteBatchTitle);
        header.Children.Add(_remoteBatchNavButton);

        _remoteBatchSubmitButton = new Button
        {
            Content = "确定",
            MinWidth = 112,
            Height = 42,
            Padding = new Thickness(18, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse("#FD6F71")),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            FontWeight = FontWeight.SemiBold
        };
        _remoteBatchSubmitButton.Click += async (_, _) => await SubmitRemoteBatchDownloadAsync();

        var body = new Grid { RowSpacing = 14 };
        body.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        body.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
        body.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        body.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        body.Children.Add(_remoteBatchDescription);
        Grid.SetRow(_remoteBatchEditor, 1);
        body.Children.Add(_remoteBatchEditor);
        Grid.SetRow(_remoteBatchResultText, 2);
        body.Children.Add(_remoteBatchResultText);
        Grid.SetRow(_remoteBatchSubmitButton, 3);
        body.Children.Add(_remoteBatchSubmitButton);

        var contentBorder = new Border
        {
            Padding = new Thickness(20),
            Child = body
        };
        var surfaceGrid = new Grid();
        surfaceGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        surfaceGrid.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
        var headerBorder = new Border
        {
            Padding = new Thickness(10, 8),
            Child = header
        };
        surfaceGrid.Children.Add(headerBorder);
        Grid.SetRow(contentBorder, 1);
        surfaceGrid.Children.Add(contentBorder);

        _remoteBatchSurface = new Border
        {
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = surfaceGrid
        };
        _remoteBatchOverlay = new Grid
        {
            IsVisible = false,
            ZIndex = 3000
        };
        _remoteBatchOverlay.Children.Add(_remoteBatchSurface);
        rootGrid.Children.Add(_remoteBatchOverlay);
    }

    private void ShowRemoteBatchEditor()
    {
        if (_remoteBatchOverlay is null
            || _remoteBatchEditor is null
            || _remoteBatchResultText is null
            || DataContext is not RemoteMainViewModel viewModel
            || !viewModel.IsConnected)
        {
            return;
        }

        _remoteBatchResultText.Text = string.Empty;
        _remoteBatchResultText.IsVisible = false;
        ApplyRemoteBatchOverlayMode();
        _remoteBatchOverlay.IsVisible = true;

        // 手机页不主动给 TextBox 焦点，避免一进入页面就弹软键盘。
        if (!_remoteBatchPageMode)
        {
            Dispatcher.UIThread.Post(
                () => _remoteBatchEditor.Focus(),
                DispatcherPriority.Loaded);
        }
    }

    private void HideRemoteBatchEditor()
    {
        if (_remoteBatchOverlay is not null)
            _remoteBatchOverlay.IsVisible = false;
    }

    private void ApplyRemoteBatchOverlayMode()
    {
        if (_remoteBatchOverlay is null
            || _remoteBatchSurface is null
            || _remoteBatchEditor is null
            || _remoteBatchSubmitButton is null
            || _remoteBatchNavButton is null
            || _remoteBatchNavIcon is null
            || DataContext is not RemoteMainViewModel viewModel)
        {
            return;
        }

        _remoteBatchPageMode = viewModel.IsNativeMobileClient || Bounds.Width < RemoteBatchPageThreshold;
        ApplyRemoteBatchTheme(viewModel);
        Grid.SetColumn(_remoteBatchNavButton, _remoteBatchPageMode ? 0 : 2);

        if (_remoteBatchPageMode)
        {
            _remoteBatchOverlay.Background = _remoteBatchSurface.Background;
            _remoteBatchSurface.Width = double.NaN;
            _remoteBatchSurface.Height = double.NaN;
            _remoteBatchSurface.Margin = new Thickness(0);
            _remoteBatchSurface.CornerRadius = new CornerRadius(0);
            _remoteBatchSurface.BorderThickness = new Thickness(0);
            _remoteBatchSurface.HorizontalAlignment = HorizontalAlignment.Stretch;
            _remoteBatchSurface.VerticalAlignment = VerticalAlignment.Stretch;
            _remoteBatchEditor.MinHeight = 320;
            _remoteBatchSubmitButton.HorizontalAlignment = HorizontalAlignment.Stretch;
            _remoteBatchNavButton.HorizontalAlignment = HorizontalAlignment.Left;
            _remoteBatchNavIcon.Data = Geometry.Parse(RemoteBatchBackGeometry);
            ToolTip.SetTip(_remoteBatchNavButton, "返回");
        }
        else
        {
            _remoteBatchOverlay.Background = new SolidColorBrush(Color.Parse("#88000000"));
            _remoteBatchSurface.Width = Math.Clamp(Bounds.Width - 48, 560, 780);
            _remoteBatchSurface.Height = Math.Clamp(Bounds.Height - 48, 480, 640);
            _remoteBatchSurface.Margin = new Thickness(24);
            _remoteBatchSurface.CornerRadius = new CornerRadius(14);
            _remoteBatchSurface.BorderThickness = new Thickness(1);
            _remoteBatchSurface.HorizontalAlignment = HorizontalAlignment.Center;
            _remoteBatchSurface.VerticalAlignment = VerticalAlignment.Center;
            _remoteBatchEditor.MinHeight = 300;
            _remoteBatchSubmitButton.HorizontalAlignment = HorizontalAlignment.Right;
            _remoteBatchNavButton.HorizontalAlignment = HorizontalAlignment.Right;
            _remoteBatchNavIcon.Data = Geometry.Parse(RemoteBatchCloseGeometry);
            ToolTip.SetTip(_remoteBatchNavButton, "关闭");
        }
    }

    private void BindRemoteBatchThemeViewModel(RemoteMainViewModel viewModel)
    {
        if (ReferenceEquals(_remoteBatchThemeViewModel, viewModel))
            return;

        if (_remoteBatchThemeViewModel is not null)
            _remoteBatchThemeViewModel.PropertyChanged -= RemoteBatchThemeViewModel_PropertyChanged;

        _remoteBatchThemeViewModel = viewModel;
        _remoteBatchThemeViewModel.PropertyChanged += RemoteBatchThemeViewModel_PropertyChanged;
    }

    private void RemoteBatchThemeViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RemoteMainViewModel.IsRemoteDarkTheme)
            && _remoteBatchOverlay?.IsVisible == true
            && sender is RemoteMainViewModel viewModel)
        {
            ApplyRemoteBatchTheme(viewModel);
            if (_remoteBatchPageMode && _remoteBatchOverlay is not null && _remoteBatchSurface is not null)
                _remoteBatchOverlay.Background = _remoteBatchSurface.Background;
        }
    }

    private void ApplyRemoteBatchTheme(RemoteMainViewModel viewModel)
    {
        if (_remoteBatchSurface is null
            || _remoteBatchEditor is null
            || _remoteBatchTitle is null
            || _remoteBatchDescription is null
            || _remoteBatchResultText is null
            || _remoteBatchNavButton is null
            || _remoteBatchNavIcon is null)
        {
            return;
        }

        var primary = new SolidColorBrush(Color.Parse(
            viewModel.IsRemoteDarkTheme ? "#FFF7F7FB" : "#FF202231"));
        var secondary = new SolidColorBrush(Color.Parse(
            viewModel.IsRemoteDarkTheme ? "#FFC3C7D4" : "#FF62687A"));
        var surface = new SolidColorBrush(Color.Parse(
            viewModel.IsRemoteDarkTheme ? "#FF111827" : "#FFF8FAFC"));
        var border = new SolidColorBrush(Color.Parse(
            viewModel.IsRemoteDarkTheme ? "#55FFFFFF" : "#331B1D2A"));

        _remoteBatchSurface.Background = surface;
        _remoteBatchSurface.BorderBrush = border;
        _remoteBatchTitle.Foreground = primary;
        _remoteBatchDescription.Foreground = secondary;
        _remoteBatchResultText.Foreground = secondary;
        _remoteBatchEditor.Foreground = primary;
        _remoteBatchEditor.BorderBrush = border;
        _remoteBatchNavButton.Foreground = primary;
        _remoteBatchNavIcon.Foreground = primary;
    }

    private async Task SubmitRemoteBatchDownloadAsync()
    {
        if (_remoteBatchSubmitting
            || _remoteBatchEditor is null
            || _remoteBatchResultText is null
            || _remoteBatchSubmitButton is null
            || DataContext is not RemoteMainViewModel viewModel)
        {
            return;
        }

        var text = _remoteBatchEditor.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            _remoteBatchResultText.Text = "请输入批量下载文本。";
            _remoteBatchResultText.IsVisible = true;
            return;
        }

        _remoteBatchSubmitting = true;
        _remoteBatchSubmitButton.IsEnabled = false;
        _remoteBatchSubmitButton.Content = "发送中…";

        try
        {
            var result = await RemoteBatchDownloadClient.SubmitAsync(
                viewModel.ServerAddress,
                viewModel.AccessToken,
                text);

            _remoteBatchResultText.Text = result.Message;
            _remoteBatchResultText.IsVisible = true;
            if (result.Success)
                _remoteBatchEditor.Text = string.Empty;
        }
        catch (Exception ex)
        {
            _remoteBatchResultText.Text = $"发送失败：{ex.Message}";
            _remoteBatchResultText.IsVisible = true;
        }
        finally
        {
            _remoteBatchSubmitting = false;
            _remoteBatchSubmitButton.IsEnabled = viewModel.IsConnected;
            _remoteBatchSubmitButton.Content = "确定";
        }
    }
}
