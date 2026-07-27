using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HelloCrab.Core.ViewModels;

namespace HelloCrab.Core.Views;

public partial class MainWindow
{
    private static readonly IDisposable CurrentAuthorLayoutDataContextHandler =
        StyledElement.DataContextProperty.Changed.AddClassHandler<MainWindow>((window, _) =>
            Dispatcher.UIThread.Post(
                window.MoveCurrentAuthorNameBelowAvatar,
                DispatcherPriority.Loaded));

    private bool _currentAuthorLayoutAdjusted;

    private void MoveCurrentAuthorNameBelowAvatar()
    {
        if (_currentAuthorLayoutAdjusted)
            return;

        var authorNameText = this
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(textBlock =>
                textBlock.DataContext is MainWindowViewModel
                && textBlock.Classes.Contains("emojiText")
                && textBlock.Parent is StackPanel);

        if (authorNameText?.Parent is not StackPanel informationPanel
            || informationPanel.Parent is not Grid authorGrid)
        {
            Dispatcher.UIThread.Post(
                MoveCurrentAuthorNameBelowAvatar,
                DispatcherPriority.Background);
            return;
        }

        var avatarBorder = authorGrid.Children
            .OfType<Border>()
            .FirstOrDefault(border =>
                Grid.GetColumn(border) == 0
                && border.GetVisualDescendants().OfType<Image>().Any());

        if (avatarBorder is null)
        {
            Dispatcher.UIThread.Post(
                MoveCurrentAuthorNameBelowAvatar,
                DispatcherPriority.Background);
            return;
        }

        informationPanel.Children.Remove(authorNameText);
        authorGrid.Children.Remove(avatarBorder);

        authorNameText.Margin = new Thickness(0, 6, 0, 0);
        authorNameText.MaxWidth = 88;
        authorNameText.HorizontalAlignment = HorizontalAlignment.Stretch;
        authorNameText.VerticalAlignment = VerticalAlignment.Center;
        authorNameText.TextAlignment = TextAlignment.Center;
        authorNameText.TextWrapping = TextWrapping.Wrap;

        var avatarPanel = new StackPanel
        {
            Width = 88,
            Spacing = 0,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                avatarBorder,
                authorNameText
            }
        };

        Grid.SetColumn(avatarPanel, 0);
        authorGrid.Children.Add(avatarPanel);
        _currentAuthorLayoutAdjusted = true;
    }
}
