using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Better_SignalRGB_Screen_Capture.Helpers;

internal static class CanvasInteractionVisuals
{
    private static readonly Dictionary<InputSystemCursorShape, InputSystemCursor> Cursors = [];
    public static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(child); parent != null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is T match) return match;
        return null;
    }

    public static InputCursor Cursor(CanvasHandle handle, double rotation)
    {
        var shape = handle is CanvasHandle.Rotate or CanvasHandle.CropRotate or CanvasHandle.Move
            ? InputSystemCursorShape.SizeAll
            : handle == CanvasHandle.None ? InputSystemCursorShape.Arrow
            : CanvasInteractionGeometry.CursorAxis(handle, rotation) switch
            {
                0 => InputSystemCursorShape.SizeNorthSouth, 1 => InputSystemCursorShape.SizeNortheastSouthwest,
                2 => InputSystemCursorShape.SizeWestEast, _ => InputSystemCursorShape.SizeNorthwestSoutheast
            };
        if (!Cursors.TryGetValue(shape, out var cursor)) Cursors.Add(shape, cursor = InputSystemCursor.Create(shape));
        return cursor;
    }

    public static Rect Bounds(FrameworkElement element) => new(Canvas.GetLeft(element), Canvas.GetTop(element), element.Width, element.Height);
    public static void Place(FrameworkElement element, Rect bounds)
    {
        Canvas.SetLeft(element, bounds.X); Canvas.SetTop(element, bounds.Y);
        element.Width = bounds.Width; element.Height = bounds.Height;
    }
}
