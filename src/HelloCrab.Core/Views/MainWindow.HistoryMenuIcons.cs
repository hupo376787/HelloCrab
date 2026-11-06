using Avalonia.Controls;
using IconPath = Avalonia.Controls.Shapes.Path;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace HelloCrab.Core.Views;

public partial class MainWindow
{
    private void HistoryContextMenu_Opened(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
            return;

        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            if (item.Icon is not null)
                continue;

            string? name = null;
            if (item.Classes.Contains("historyMenuHome")) name = "home";
            else if (item.Classes.Contains("historyMenuCopy")) name = "copy";
            else if (item.Classes.Contains("historyMenuFolder")) name = "folder";
            else if (item.Classes.Contains("historyMenuRename")) name = "rename";
            else if (item.Classes.Contains("historyMenuRecollect")) name = "recollect";
            else if (item.Classes.Contains("historyMenuTop")) name = "top";
            else if (item.Classes.Contains("historyMenuBottom")) name = "bottom";
            else if (item.Classes.Contains("historyMenuRemove")) name = "remove";
            else if (item.Classes.Contains(FavoriteMenuClass)) name = "favorite";

            if (name is not null)
                item.Icon = CreateHistoryMenuIcon(name);
        }
    }

    private static IconPath CreateHistoryMenuIcon(string name)
    {
        var data = name switch
        {
            "home" => "M2,8 L8,2 L14,8 M4,7 L4,14 L12,14 L12,7 M7,14 L7,10 L9,10 L9,14",
            "copy" => "M6,4 L12,4 C13,4 14,5 14,6 L14,13 C14,14 13,15 12,15 L6,15 C5,15 4,14 4,13 L4,6 C4,5 5,4 6,4 Z M2,11 L2,3 C2,2 3,1 4,1 L10,1",
            "folder" => "M1,4 L6,4 L8,6 L15,6 L15,13 L1,13 Z M1,4 L1,3 C1,2 2,2 3,2 L6,2 L8,4 L13,4 C14,4 15,5 15,6",
            "rename" => "M2,12 L11,3 L13,5 L4,14 L1,15 Z M10,4 L12,2 C13,1 14,2 15,3 L13,5",
            "recollect" => "M13,5 L13,1 L9,1 M13,5 C11,2 6,2 3,5 C0,8 2,13 6,14 C9,15 12,13 13,10",
            "favorite" => "M8,1 L10,5.5 L15,6 L11.3,9.5 L12.3,15 L8,12.5 L3.7,15 L4.7,9.5 L1,6 L6,5.5 Z",
            "top" => "M8,14 L8,4 M4,8 L8,4 L12,8 M2,2 L14,2",
            "bottom" => "M8,2 L8,12 M4,8 L8,12 L12,8 M2,14 L14,14",
            "remove" => "M3,4 L13,4 M6,4 L6,2 L10,2 L10,4 M4,4 L5,14 L11,14 L12,4 M7,7 L7,11 M9,7 L9,11",
            _ => "M2,8 L14,8"
        };

        return new IconPath
        {
            Data = Geometry.Parse(data),
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            Stroke = new SolidColorBrush(Color.Parse("#7A8498")),
            StrokeThickness = 1.7,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false
        };
    }
}
