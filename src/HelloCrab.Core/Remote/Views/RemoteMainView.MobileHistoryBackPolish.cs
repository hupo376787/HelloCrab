using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HelloCrab.Core.Remote.ViewModels;

namespace HelloCrab.Core.Remote.Views;

/// <summary>
/// 手机端下载历史页返回按钮最终修正：
/// - 普通、悬停、按下状态始终保持透明背景；
/// - 使用矢量返回图标，避免字体 glyph / 前景色问题；
/// - 图标在 42x42 点击区域内严格水平、垂直居中；
/// - 亮/暗主题切换时同步更新图标前景色。
/// </summary>
public partial class RemoteMainView
{
    private static readonly IDisposable MobileHistoryBackPolishDataContextHandler =
        StyledElement.DataContextProperty.Changed.AddClassHandler<RemoteMainView>((view, _) =>
            Dispatcher.UIThread.Post(
                view.EnsureMobileHistoryBackPolish,
                DispatcherPriority.Background));

    private bool _mobileHistoryBackPolishInitialized;
    private int _mobileHistoryBackPolishInstallAttempts;
    private RemoteMainViewModel? _mobileHistoryBackThemeViewModel;
    private PathIcon? _mobileHistoryBackIcon;

    private void EnsureMobileHistoryBackPolish()
    {
        if (_mobileHistoryBackPolishInitialized)
            return;

        if (!_finalRemoteUiPolishInitialized
            || _finalMobileHistoryBackButton is null
            || DataContext is not RemoteMainViewModel viewModel
            || !viewModel.IsNativeMobileClient)
        {
            if (_mobileHistoryBackPolishInstallAttempts++ < 32)
            {
                Dispatcher.UIThread.Post(
                    EnsureMobileHistoryBackPolish,
                    DispatcherPriority.Background);
            }
            return;
        }

        _mobileHistoryBackPolishInitialized = true;
        _mobileHistoryBackThemeViewModel = viewModel;

        var backButton = _finalMobileHistoryBackButton;

        // 去掉通用 action/secondary 外观，避免按下时继承按钮的蓝色背景和透明度变化。
        backButton.Classes.Remove("action");
        backButton.Classes.Remove("secondary");

        backButton.Width = 42;
        backButton.Height = 42;
        backButton.MinWidth = 42;
        backButton.MinHeight = 42;
        backButton.Padding = new Thickness(0);
        backButton.Background = Brushes.Transparent;
        backButton.BorderBrush = Brushes.Transparent;
        backButton.BorderThickness = new Thickness(0);
        backButton.HorizontalContentAlignment = HorizontalAlignment.Center;
        backButton.VerticalContentAlignment = VerticalAlignment.Center;

        // 用 PathIcon 代替字体字符“‹”，避免手机端字体缺失或前景色继承异常。
        _mobileHistoryBackIcon = new PathIcon
        {
            Data = Geometry.Parse("M20,11H7.83L13.42,5.41L12,4L4,12L12,20L13.41,18.59L7.83,13H20V11Z"),
            Width = 22,
            Height = 22,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        backButton.Content = _mobileHistoryBackIcon;

        UpdateMobileHistoryBackForeground();

        // Fluent Button 的 pressed/pointerover 状态会直接修改模板里的背景。
        // 给模板可视元素写入本地透明值，保证按下时也不会出现底色。
        ForceMobileHistoryBackTemplateTransparent(backButton);
        backButton.AttachedToVisualTree += (_, _) =>
            Dispatcher.UIThread.Post(
                () => ForceMobileHistoryBackTemplateTransparent(backButton),
                DispatcherPriority.Loaded);
        backButton.PointerPressed += (_, _) =>
            Dispatcher.UIThread.Post(
                () => ForceMobileHistoryBackTemplateTransparent(backButton),
                DispatcherPriority.Input);
        backButton.PointerReleased += (_, _) =>
            Dispatcher.UIThread.Post(
                () => ForceMobileHistoryBackTemplateTransparent(backButton),
                DispatcherPriority.Input);

        viewModel.PropertyChanged += MobileHistoryBackViewModelPropertyChanged;
    }

    private void MobileHistoryBackViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RemoteMainViewModel.IsRemoteDarkTheme))
            UpdateMobileHistoryBackForeground();
    }

    private void UpdateMobileHistoryBackForeground()
    {
        if (_finalMobileHistoryBackButton is null
            || _mobileHistoryBackIcon is null
            || _mobileHistoryBackThemeViewModel is null)
        {
            return;
        }

        var brush = new SolidColorBrush(Color.Parse(
            _mobileHistoryBackThemeViewModel.IsRemoteDarkTheme
                ? "#FFF5F7FF"
                : "#FF172033"));

        _finalMobileHistoryBackButton.Foreground = brush;
        _mobileHistoryBackIcon.Foreground = brush;
    }

    private static void ForceMobileHistoryBackTemplateTransparent(Button backButton)
    {
        backButton.Background = Brushes.Transparent;
        backButton.BorderBrush = Brushes.Transparent;
        backButton.BorderThickness = new Thickness(0);
        backButton.Opacity = 1;

        foreach (var border in backButton.GetVisualDescendants().OfType<Border>())
        {
            border.Background = Brushes.Transparent;
            border.BorderBrush = Brushes.Transparent;
        }

        foreach (var presenter in backButton.GetVisualDescendants().OfType<ContentPresenter>())
        {
            presenter.Background = Brushes.Transparent;
            presenter.BorderBrush = Brushes.Transparent;
            presenter.BorderThickness = new Thickness(0);
            presenter.HorizontalContentAlignment = HorizontalAlignment.Center;
            presenter.VerticalContentAlignment = VerticalAlignment.Center;
        }
    }
}
