using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Better_SignalRGB_Screen_Capture.Helpers;

/// <summary>Toolbar flow that keeps every editor action reachable on narrow windows.</summary>
public sealed class CanvasWrapPanel : Panel
{
    public double Spacing { get; set; } = 8;
    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children) child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        return Layout(availableSize.Width, false);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Width, true);
        return finalSize;
    }

    private Size Layout(double width, bool arrange)
    {
        double x = 0, y = 0, rowHeight = 0, usedWidth = 0;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > width) { x = 0; y += rowHeight + Spacing; rowHeight = 0; }
            if (arrange) child.Arrange(new Rect(x, y, size.Width, size.Height));
            usedWidth = Math.Max(usedWidth, x + size.Width);
            x += size.Width + Spacing;
            rowHeight = Math.Max(rowHeight, size.Height);
        }
        return new Size(usedWidth, y + rowHeight);
    }
}
