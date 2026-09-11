using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HelloCrab.Core.Remote.ViewModels;

namespace HelloCrab.Core.Remote.Views;

/// <summary>
/// Browser-only interaction fixes:
/// - tracks the currently focused Avalonia TextBox so the Browser host can inject paste text
///   from the native DOM paste event (works even when navigator.clipboard is unavailable on HTTP);
/// - replaces the wide-page theme button's Browser-problematic vector composition with a simple
///   PathIcon and a direct Click handler.
/// </summary>
public partial class RemoteMainView
{
    private const string BrowserSunGeometry =
        "M20,15.31 L23.31,12 L20,8.69 L20,4 L15.31,4 L12,0.69 L8.69,4 L4,4 L4,8.69 L0.69,12 L4,15.31 L4,20 L8.69,20 L12,23.31 L15.31,20 L20,20 Z M12,18 C8.69,18 6,15.31 6,12 C6,8.69 8.69,6 12,6 C15.31,6 18,8.69 18,12 C18,15.31 15.31,18 12,18 Z";

    private const string BrowserMoonGeometry =
        "M20.2,15.3 C18.8,16 17.2,16.4 15.5,16.4 C11.1,16.4 7.6,12.9 7.6,8.5 C7.6,6.8 8,5.2 8.7,3.8 C5.1,5.1 2.5,8.5 2.5,12.5 C2.5,17.5 6.5,21.5 11.5,21.5 C15.5,21.5 18.9,18.9 20.2,15.3 Z";

    private static readonly IDisposable BrowserInteractionPolishDataContextHandler =
        StyledElement.DataContextProperty.Changed.AddClassHandler<RemoteMainView>((view, _) =>
            Dispatcher.UIThread.Post(
                view.InitializeBrowserInteractionPolish,
                DispatcherPriority.Background));

    // Avalonia 12 的 GotFocusEvent 使用自己的泛型事件参数。这里让编译器直接推断参数类型，
    // 避免显式引用版本间会变化的 GotFocusEventArgs，同时覆盖运行期创建出来的搜索框等 TextBox。
    private static readonly IDisposable BrowserTextBoxFocusHandler =
        InputElement.GotFocusEvent.AddClassHandler<TextBox>(
            (textBox, _) =>
            {
                if (OperatingSystem.IsBrowser())
                    BrowserTextInputPasteBridge.SetTarget(textBox);
            },
            RoutingStrategies.Bubble,
            handledEventsToo: true);

    private bool _browserInteractionPolishInitialized;
    private int _browserInteractionPolishInstallAttempts;
    private Button? _browserThemeButton;
    private PathIcon? _browserThemeIcon;

    private void InitializeBrowserInteractionPolish()
    {
        if (_browserInteractionPolishInitialized || !OperatingSystem.IsBrowser())
            return;

        if (DataContext is not RemoteMainViewModel viewModel)
        {
            RetryBrowserInteractionPolishInitialization();
            return;
        }

        var themeButton = this.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(button => button.Classes.Contains("themeIcon"));

        if (themeButton is null)
        {
            RetryBrowserInteractionPolishInitialization();
            return;
        }

        _browserInteractionPolishInitialized = true;
        _browserThemeButton = themeButton;
        ConfigureBrowserThemeButton(viewModel);
    }

    private void RetryBrowserInteractionPolishInitialization()
    {
        if (_browserInteractionPolishInstallAttempts++ >= 32)
            return;

        Dispatcher.UIThread.Post(
            InitializeBrowserInteractionPolish,
            DispatcherPriority.Background);
    }

    private void ConfigureBrowserThemeButton(RemoteMainViewModel viewModel)
    {
        if (_browserThemeButton is null)
            return;

        var button = _browserThemeButton;

        // The original action/secondary/themeIcon combination uses several Fluent states and a
        // Viewbox/Canvas icon stack. On Browser/WebGL this occasionally renders clipped and can
        // leave the command hit area in a bad state. Use one plain circular Button instead.
        button.Command = null;
        button.Classes.Remove("action");
        button.Classes.Remove("secondary");
        button.Classes.Remove("themeIcon");
        button.Width = 44;
        button.Height = 44;
        button.MinWidth = 44;
        button.MinHeight = 44;
        button.Padding = new Thickness(0);
        button.CornerRadius = new CornerRadius(22);
        button.Background = new SolidColorBrush(Color.Parse("#6657D9"));
        button.BorderBrush = new SolidColorBrush(Color.Parse("#66FFFFFF"));
        button.BorderThickness = new Thickness(1);
        button.Foreground = Brushes.White;
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.VerticalContentAlignment = VerticalAlignment.Center;
        button.HorizontalAlignment = HorizontalAlignment.Center;
        button.VerticalAlignment = VerticalAlignment.Center;
        button.IsHitTestVisible = true;
        button.Focusable = true;
        button.ZIndex = 1000;
        button.Cursor = new Cursor(StandardCursorType.Hand);

        _browserThemeIcon = new PathIcon
        {
            Width = 22,
            Height = 22,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        button.Content = _browserThemeIcon;
        button.Click += BrowserThemeButton_Click;

        RefreshBrowserThemeButton(viewModel);
    }

    private void BrowserThemeButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RemoteMainViewModel viewModel
            || !viewModel.ToggleRemoteThemeCommand.CanExecute(null))
        {
            return;
        }

        viewModel.ToggleRemoteThemeCommand.Execute(null);
        RefreshBrowserThemeButton(viewModel);
        e.Handled = true;
    }

    private void RefreshBrowserThemeButton(RemoteMainViewModel viewModel)
    {
        if (_browserThemeButton is null || _browserThemeIcon is null)
            return;

        // Dark theme shows a sun (switch to light); light theme shows a moon (switch to dark).
        _browserThemeIcon.Data = Geometry.Parse(
            viewModel.IsRemoteDarkTheme ? BrowserSunGeometry : BrowserMoonGeometry);
        ToolTip.SetTip(_browserThemeButton, viewModel.RemoteThemeButtonText);
    }
}

/// <summary>
/// Shared target for the Browser host's DOM paste fallback.
/// Kept in Core so the Browser assembly can feed native ClipboardEvent text into the currently
/// focused Avalonia TextBox without relying on navigator.clipboard permissions/secure-context rules.
/// </summary>
public static class BrowserTextInputPasteBridge
{
    private static WeakReference<TextBox>? _target;

    public static void SetTarget(TextBox? textBox)
    {
        _target = textBox is null ? null : new WeakReference<TextBox>(textBox);
    }

    public static bool TryPaste(string? text)
    {
        if (string.IsNullOrEmpty(text)
            || _target is null
            || !_target.TryGetTarget(out var target)
            || !target.IsFocused
            || target.IsReadOnly)
        {
            return false;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            PasteCore(target, text);
        }
        else
        {
            Dispatcher.UIThread.Post(
                () => PasteCore(target, text),
                DispatcherPriority.Input);
        }

        return true;
    }

    private static void PasteCore(TextBox target, string text)
    {
        var current = target.Text ?? string.Empty;
        var start = Math.Clamp(Math.Min(target.SelectionStart, target.SelectionEnd), 0, current.Length);
        var end = Math.Clamp(Math.Max(target.SelectionStart, target.SelectionEnd), 0, current.Length);

        var insert = text;
        if (!target.AcceptsReturn)
        {
            insert = insert
                .Replace("\r\n", " ", StringComparison.Ordinal)
                .Replace('\r', ' ')
                .Replace('\n', ' ');
        }

        if (target.MaxLength > 0)
        {
            var retainedLength = current.Length - (end - start);
            var allowed = Math.Max(0, target.MaxLength - retainedLength);
            if (insert.Length > allowed)
                insert = insert[..allowed];
        }

        var result = string.Concat(current.AsSpan(0, start), insert, current.AsSpan(end));
        target.Text = result;

        var caret = start + insert.Length;
        target.SelectionStart = caret;
        target.SelectionEnd = caret;
        target.CaretIndex = caret;
    }
}
