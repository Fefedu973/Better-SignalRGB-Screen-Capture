using System.Windows.Input;
using Better_SignalRGB_Screen_Capture.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace Better_SignalRGB_Screen_Capture.Helpers;

internal static class CanvasContextMenuBuilder
{
    public static MenuFlyout Create(MainViewModel model, ElementTheme theme, int count,
        RoutedEventHandler copy, RoutedEventHandler paste, RoutedEventHandler center, RoutedEventHandler delete,
        RoutedEventHandler? edit = null, RoutedEventHandler? crop = null)
    {
        var menu = new MenuFlyout();
        void Action(string text, string glyph, RoutedEventHandler clicked, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
            item.Click += clicked; menu.Items.Add(item);
        }
        if (edit != null) Action("Edit", "\uE70F", edit);
        Action("Copy", "\uE8C8", copy);
        Action("Paste", "\uE77F", paste, model.CanPasteSource());
        menu.Items.Add(Command(model.SelectedSourcesLocked ? "Unlock Selection" : "Lock Selection", "\uE72E", model.ToggleSelectedLockCommand));
        menu.Items.Add(new MenuFlyoutSeparator());
        if (count > 1) menu.Items.Add(Alignment(model));
        Action("Center", "\uF58A", center, model.CanTransformSelection);
        var layers = new MenuFlyoutSubItem { Text = "Layer", Icon = new FontIcon { Glyph = "\uE81E" } };
        layers.Items.Add(Command("Bring to Front", "\uE746", model.BringToFrontCommand));
        layers.Items.Add(Command("Bring Forward", "\uE760", model.BringForwardCommand));
        layers.Items.Add(new MenuFlyoutSeparator());
        layers.Items.Add(Command("Send Backward", "\uE761", model.SendBackwardCommand));
        layers.Items.Add(Command("Send to Back", "\uE747", model.SendToBackCommand));
        menu.Items.Add(layers);
        menu.Items.Add(new MenuFlyoutSeparator());
        var uri = new Uri($"ms-appx:///Assets/{(theme == ElementTheme.Dark ? "Flip-dark.svg" : "Flip-white.svg")}");
        foreach (var horizontal in new[] { true, false })
        {
            var icon = new ImageIcon { Source = new SvgImageSource(uri), Width = 16, Height = 16 };
            if (horizontal) icon.RenderTransform = new RotateTransform { Angle = 90, CenterX = 8, CenterY = 8 };
            menu.Items.Add(new MenuFlyoutItem { Text = horizontal ? "Mirror Content Horizontally" : "Mirror Content Vertically", Icon = icon,
                IsEnabled = model.CanTransformSelection,
                Command = horizontal ? model.ToggleFlipHorizontalCommand : model.ToggleFlipVerticalCommand });
        }
        if (crop != null) { menu.Items.Add(new MenuFlyoutSeparator()); Action("Crop", "\uE7A8", crop, model.CanTransformSelection); }
        menu.Items.Add(new MenuFlyoutSeparator());
        Action(count > 1 ? $"Delete {count} items" : "Delete", "\uE74D", delete);
        return menu;
    }

    private static MenuFlyoutItem Command(string text, string glyph, ICommand command, double rotation = 0) => new()
    {
        Text = text, Command = command,
        Icon = new FontIcon { Glyph = glyph, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform { Angle = rotation } }
    };

    private static MenuFlyoutSubItem Alignment(MainViewModel model)
    {
        var menu = new MenuFlyoutSubItem { Text = "Align", Icon = new FontIcon { Glyph = "\uE139" } };
        menu.Items.Add(Command("Align Left", "\uE8E4", model.AlignLeftCommand));
        menu.Items.Add(Command("Align Center", "\uE8E3", model.AlignCenterCommand));
        menu.Items.Add(Command("Align Right", "\uE8E2", model.AlignRightCommand));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Command("Align Top", "\uE8E2", model.AlignTopCommand, -90));
        menu.Items.Add(Command("Align Middle", "\uE8E3", model.AlignMiddleCommand, -90));
        menu.Items.Add(Command("Align Bottom", "\uE8E4", model.AlignBottomCommand, -90));
        return menu;
    }
}
