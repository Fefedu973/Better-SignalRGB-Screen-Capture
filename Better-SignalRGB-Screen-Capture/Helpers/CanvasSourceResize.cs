using Better_SignalRGB_Screen_Capture.Models;
using Windows.Foundation;

namespace Better_SignalRGB_Screen_Capture.Helpers;

/// <summary>Resize the visible crop frame while preserving the opposite visible anchor.</summary>
internal static class CanvasSourceResize
{
    public static bool SupportedDimensions(Rect bounds) => bounds.Width is >= 1 and <= 7680 && bounds.Height is >= 1 and <= 4320;

    public static Point ConstrainPosition(SourceItem source, double x, double y, Size canvas)
    {
        var local = SourceGeometry.GetVisibleAreaAabb(source, 0, 0, source.CanvasWidth, source.CanvasHeight, source.Rotation);
        static double Axis(double value, double lower, double upper) => lower <= upper
            ? Math.Clamp(Math.Round(value), lower, upper) : Math.Round((lower + upper) / 2);
        return new Point(Axis(x, Math.Ceiling(-local.Left), Math.Floor(canvas.Width - local.Right)),
            Axis(y, Math.Ceiling(-local.Top), Math.Floor(canvas.Height - local.Bottom)));
    }

    public static Rect VisibleLocalBounds(SourceItem source, double width, double height) =>
        SourceGeometry.Bounds(SourceGeometry.GetVisiblePolygon(source, 0, 0, width, height, 0));

    public static Rect VisibleFrame(SourceItem source, Rect bounds, double rotation)
    {
        var visible = VisibleLocalBounds(source, bounds.Width, bounds.Height);
        var localCenter = CanvasInteractionGeometry.Center(visible);
        var center = CanvasInteractionGeometry.Rotate(new Point(bounds.X + localCenter.X, bounds.Y + localCenter.Y), CanvasInteractionGeometry.Center(bounds), rotation);
        return new Rect(center.X - visible.Width / 2, center.Y - visible.Height / 2, visible.Width, visible.Height);
    }

    public static Rect Rotate(SourceItem source, Rect bounds, double startRotation, double targetRotation)
    {
        var pivot = CanvasInteractionGeometry.Center(VisibleFrame(source, bounds, startRotation));
        var localCenter = CanvasInteractionGeometry.Center(VisibleLocalBounds(source, bounds.Width, bounds.Height));
        var offset = CanvasInteractionGeometry.Rotate(new Point(localCenter.X - bounds.Width / 2, localCenter.Y - bounds.Height / 2), default, targetRotation);
        return new Rect(pivot.X - offset.X - bounds.Width / 2, pivot.Y - offset.Y - bounds.Height / 2, bounds.Width, bounds.Height);
    }

    // A rotated mask changes its intersection under anisotropic scaling. Uniform
    // scaling keeps the visible shape and its anchor exact in the current model.
    public static bool RequiresUniformScale(SourceItem source) => Math.Abs(Math.Sin(source.CropRotation * Math.PI / 180)) > .00001;

    public static Rect Resize(SourceItem source, CanvasGesture gesture, Point delta, bool keepAspect)
    {
        var full = gesture.StartBounds;
        var localVisible = VisibleLocalBounds(source, full.Width, full.Height);
        if (localVisible.IsEmpty || localVisible.Width <= 0 || localVisible.Height <= 0) return full;
        var visibleFrame = VisibleFrame(source, full, gesture.StartRotation);
        var resized = CanvasInteractionGeometry.Resize(visibleFrame, delta, gesture.StartRotation, gesture.Handle,
            localVisible.Width / full.Width, localVisible.Height / full.Height, keepAspect || RequiresUniformScale(source));
        var width = Math.Max(1, full.Width * resized.Width / localVisible.Width);
        var height = Math.Max(1, full.Height * resized.Height / localVisible.Height);
        var newLocal = VisibleLocalBounds(source, width, height);
        var newLocalCenter = CanvasInteractionGeometry.Center(newLocal);
        var offset = CanvasInteractionGeometry.Rotate(new Point(newLocalCenter.X - width / 2, newLocalCenter.Y - height / 2), default, gesture.StartRotation);
        var targetCenter = CanvasInteractionGeometry.Center(resized);
        return new Rect(targetCenter.X - offset.X - width / 2, targetCenter.Y - offset.Y - height / 2, width, height);
    }

    public static Rect Rounded(Rect bounds) => new(Math.Round(bounds.X), Math.Round(bounds.Y),
        Math.Max(1, Math.Round(bounds.Width)), Math.Max(1, Math.Round(bounds.Height)));

    public static Rect ResizeWithinCanvas(SourceItem source, CanvasGesture gesture, Point delta, bool keepAspect, Size canvas)
    {
        // Validate continuous geometry before quantization: rounding during a binary
        // search produces alternating valid/invalid half-pixel anchors at 90 degrees.
        var requested = CanvasInteractionGeometry.ConstrainResize(delta, gesture.StartRotation, keepAspect || RequiresUniformScale(source),
            change => Resize(source, gesture, change, keepAspect),
            bounds => SupportedDimensions(bounds) && CanvasInteractionGeometry.Fits(SourceGeometry.GetVisibleAreaAabb(source,
                bounds.X, bounds.Y, bounds.Width, bounds.Height, gesture.StartRotation), canvas));
        return RoundInsideCanvas(source, gesture, requested, canvas);
    }

    private static Rect RoundInsideCanvas(SourceItem source, CanvasGesture gesture, Rect requested, Size canvas)
    {
        var opposite = CanvasInteractionGeometry.OppositeHandle(gesture.Handle);
        var rotation = gesture.StartRotation;
        var anchor = CanvasInteractionGeometry.HandlePosition(VisibleFrame(source, gesture.StartBounds, rotation), opposite, rotation);
        var desired = SourceGeometry.GetVisibleAreaAabb(source, requested.X, requested.Y, requested.Width, requested.Height, rotation);
        Rect? best = null;
        var bestDistance = double.PositiveInfinity;
        // Adjacent integer sizes resolve the parity of a quarter-turn: an odd width
        // and even height cannot both touch integer canvas edges after a 90° turn.
        // Prefer preserving any edge the user has already brought to the canvas.
        foreach (var dw in new[] { 0, -1, 1 })
        foreach (var dh in new[] { 0, -1, 1 })
        {
            var width = Math.Round(requested.Width) + dw;
            var height = Math.Round(requested.Height) + dh;
            if (width is < 1 or > 7680 || height is < 1 or > 4320) continue;
            var localAnchor = CanvasInteractionGeometry.HandlePosition(VisibleLocalBounds(source, width, height), opposite);
            var offset = CanvasInteractionGeometry.Rotate(new Point(localAnchor.X - width / 2, localAnchor.Y - height / 2), default, rotation);
            var idealX = anchor.X - offset.X - width / 2; var idealY = anchor.Y - offset.Y - height / 2;
            var local = SourceGeometry.GetVisibleAreaAabb(source, 0, 0, width, height, rotation);
            var left = Math.Ceiling(-local.Left - .001); var right = Math.Floor(canvas.Width - local.Right + .001);
            var top = Math.Ceiling(-local.Top - .001); var bottom = Math.Floor(canvas.Height - local.Bottom + .001);
            if (left > right || top > bottom) continue;
            var x = Math.Clamp(Math.Round(idealX), left, right); var y = Math.Clamp(Math.Round(idealY), top, bottom);
            if (Math.Abs(x - idealX) > 1.001 || Math.Abs(y - idealY) > 1.001) continue;
            var bounds = new Rect(x, y, width, height);
            var actual = SourceGeometry.GetVisibleAreaAabb(source, x, y, width, height, rotation);
            if (!CanvasInteractionGeometry.Fits(actual, canvas)) continue;
            double Edge(double expected, double value, double edge) => Math.Pow(expected - value, 2) * (Math.Abs(expected - edge) < .01 ? 100 : 1);
            var distance = Edge(desired.Left, actual.Left, 0) + Edge(desired.Top, actual.Top, 0) +
                Edge(desired.Right, actual.Right, canvas.Width) + Edge(desired.Bottom, actual.Bottom, canvas.Height) +
                10 * (Math.Pow(x - idealX, 2) + Math.Pow(y - idealY, 2));
            if (distance < bestDistance) { bestDistance = distance; best = bounds; }
        }
        return best ?? gesture.StartBounds;
    }
}
