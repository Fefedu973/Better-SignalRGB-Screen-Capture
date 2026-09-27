using Better_SignalRGB_Screen_Capture.Core.Helpers;
using Better_SignalRGB_Screen_Capture.Models;
using Windows.Foundation;

namespace Better_SignalRGB_Screen_Capture.Helpers;

internal sealed record CanvasSourceState(SourceItem Source, Rect Bounds, double Rotation)
{
    public static CanvasSourceState Capture(SourceItem source) => new(source,
        new Rect(source.CanvasX, source.CanvasY, source.CanvasWidth, source.CanvasHeight), source.Rotation);

    public bool HasChanged => Source.CanvasX != Bounds.X || Source.CanvasY != Bounds.Y ||
        Source.CanvasWidth != Bounds.Width || Source.CanvasHeight != Bounds.Height || Source.Rotation != Rotation;

    public void Apply()
    {
        Source.CanvasX = (int)Bounds.X; Source.CanvasY = (int)Bounds.Y;
        Source.CanvasWidth = (int)Bounds.Width; Source.CanvasHeight = (int)Bounds.Height; Source.Rotation = (int)Rotation;
    }
}

internal readonly record struct CanvasCropState(double Left, double Top, double Right, double Bottom, int Rotation)
{
    public static CanvasCropState Capture(SourceItem source) => new(source.CropLeftPct, source.CropTopPct, source.CropRightPct, source.CropBottomPct, source.CropRotation);
    public void Apply(SourceItem source) => source.SetCrop(Left, Top, Right, Bottom, Rotation);
}

internal readonly record struct CanvasSourceChange(SourceItem Source, int X, int Y, int Width, int Height, int Rotation)
{
    public Rect VisibleBounds => SourceGeometry.GetVisibleAreaAabb(Source, X, Y, Width, Height, Rotation);
    public void Apply()
    {
        Source.CanvasX = X; Source.CanvasY = Y;
        Source.CanvasWidth = Width; Source.CanvasHeight = Height; Source.Rotation = Rotation;
    }
}

internal static class CanvasGroupTransform
{
    public static bool RequiresUniformScale(IReadOnlyList<CanvasSourceState> items, double groupRotation) =>
        items.Any(item => Math.Abs(Math.Sin((item.Rotation - groupRotation) * Math.PI / 90)) > .00001 || CanvasSourceResize.RequiresUniformScale(item.Source));

    public static CanvasSourceChange Calculate(CanvasSourceState item, Rect originalGroup, double originalRotation, Rect targetGroup, double targetRotation)
    {
        if (Math.Abs(targetGroup.Width - originalGroup.Width) < .000001 &&
            Math.Abs(targetGroup.Height - originalGroup.Height) < .000001 && Math.Abs(targetRotation - originalRotation) < .000001)
        {
            // A translation is one integer offset for every member. Rounding each
            // child's final coordinate separately changes spacing at half pixels.
            var dx = (int)Math.Round(targetGroup.X - originalGroup.X);
            var dy = (int)Math.Round(targetGroup.Y - originalGroup.Y);
            return new(item.Source, (int)item.Bounds.X + dx, (int)item.Bounds.Y + dy,
                (int)item.Bounds.Width, (int)item.Bounds.Height, (int)item.Rotation);
        }
        var bounds = CalculateBounds(item, originalGroup, originalRotation, targetGroup, targetRotation);
        return new CanvasSourceChange(item.Source, (int)Math.Round(bounds.X), (int)Math.Round(bounds.Y),
            Math.Max(1, (int)Math.Round(bounds.Width)), Math.Max(1, (int)Math.Round(bounds.Height)),
            (int)Math.Round(item.Rotation + targetRotation - originalRotation));
    }

    private static Rect CalculateBounds(CanvasSourceState item, Rect originalGroup, double originalRotation, Rect targetGroup, double targetRotation)
    {
        var sx = targetGroup.Width / originalGroup.Width; var sy = targetGroup.Height / originalGroup.Height;
        var originalCenter = CanvasInteractionGeometry.Center(originalGroup);
        var targetCenter = CanvasInteractionGeometry.Center(targetGroup);
        var itemCenter = CanvasInteractionGeometry.Center(item.Bounds);
        var offset = CanvasGeometry.TransformGroupOffset(itemCenter.X - originalCenter.X, itemCenter.Y - originalCenter.Y,
            originalRotation, targetRotation, sx, sy);
        var angle = (item.Rotation - originalRotation) * Math.PI / 180;
        var cos = Math.Cos(angle); var sin = Math.Sin(angle);
        var width = item.Bounds.Width * Math.Sqrt(Math.Pow(sx * cos, 2) + Math.Pow(sy * sin, 2));
        var height = item.Bounds.Height * Math.Sqrt(Math.Pow(sx * sin, 2) + Math.Pow(sy * cos, 2));
        return new Rect(targetCenter.X + offset.x - width / 2, targetCenter.Y + offset.y - height / 2, width, height);
    }

    public static Size MinimumSize(IReadOnlyList<CanvasSourceState> items, Rect group, double rotation)
    {
        double minimumX = 0, minimumY = 0;
        foreach (var item in items)
        {
            var radians = (item.Rotation - rotation) * Math.PI / 180;
            var cos = Math.Abs(Math.Cos(radians)); var sin = Math.Abs(Math.Sin(radians));
            var projectionX = item.Bounds.Width * cos + item.Bounds.Height * sin;
            var projectionY = item.Bounds.Width * sin + item.Bounds.Height * cos;
            if (projectionX > 0) minimumX = Math.Max(minimumX, (cos + sin) / projectionX);
            if (projectionY > 0) minimumY = Math.Max(minimumY, (cos + sin) / projectionY);
        }
        return new Size(Math.Max(1, group.Width * minimumX), Math.Max(1, group.Height * minimumY));
    }

    public static Rect ResizeWithinCanvas(IReadOnlyList<CanvasSourceState> items, CanvasGesture gesture,
        Point delta, bool keepAspect, Size canvas)
    {
        var minimum = MinimumSize(items, gesture.StartBounds, gesture.StartRotation);
        var uniform = keepAspect || RequiresUniformScale(items, gesture.StartRotation);
        return CanvasInteractionGeometry.ConstrainResize(delta, gesture.StartRotation, uniform,
            change => CanvasInteractionGeometry.Resize(gesture.StartBounds, change, gesture.StartRotation,
                gesture.Handle, minimum.Width, minimum.Height, uniform), bounds => items.All(item =>
            {
                var child = CalculateBounds(item, gesture.StartBounds, gesture.StartRotation, bounds, gesture.StartRotation);
                return CanvasSourceResize.SupportedDimensions(child) && CanvasInteractionGeometry.Fits(
                    SourceGeometry.GetVisibleAreaAabb(item.Source, child.X, child.Y, child.Width, child.Height, item.Rotation), canvas);
            }));
    }
}
