using System.Drawing;

namespace Better_SignalRGB_Screen_Capture.Core.Helpers;

/// <summary>Coordinate conversions shared by capture setup and the region editor.</summary>
public static class CanvasGeometry
{
    public static (double x, double y) TransformGroupOffset(double x, double y,
        double oldRotation, double newRotation, double scaleX, double scaleY)
    {
        var oldAngle = oldRotation * Math.PI / 180;
        var localX = (x * Math.Cos(oldAngle) + y * Math.Sin(oldAngle)) * scaleX;
        var localY = (-x * Math.Sin(oldAngle) + y * Math.Cos(oldAngle)) * scaleY;
        var newAngle = newRotation * Math.PI / 180;
        return (localX * Math.Cos(newAngle) - localY * Math.Sin(newAngle),
            localX * Math.Sin(newAngle) + localY * Math.Cos(newAngle));
    }

    /// <summary>Resize in local axes while keeping the opposite edge/corner fixed in canvas space.</summary>
    public static CanvasBounds Resize(CanvasBounds start, double dx, double dy, double rotation,
        CanvasEdges edges, double minWidth, double minHeight, bool preserveAspectRatio)
    {
        if (start.Width <= 0 || start.Height <= 0 || !double.IsFinite(dx) || !double.IsFinite(dy)) return start;
        var radians = rotation * Math.PI / 180;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var localX = dx * cos + dy * sin;
        var localY = -dx * sin + dy * cos;
        var horizontal = edges.HasFlag(CanvasEdges.Left) ? -1 : edges.HasFlag(CanvasEdges.Right) ? 1 : 0;
        var vertical = edges.HasFlag(CanvasEdges.Top) ? -1 : edges.HasFlag(CanvasEdges.Bottom) ? 1 : 0;
        var width = start.Width + horizontal * localX;
        var height = start.Height + vertical * localY;

        if (preserveAspectRatio)
        {
            // A side handle is driven only by its own axis. A corner uses the greatest
            // proportional displacement, so wide/portrait rectangles behave symmetrically.
            var widthDriven = horizontal != 0 && (vertical == 0 ||
                Math.Abs(localX / start.Width) >= Math.Abs(localY / start.Height));
            var scale = widthDriven ? width / start.Width : height / start.Height;
            scale = Math.Max(scale, Math.Max(minWidth / start.Width, minHeight / start.Height));
            width = start.Width * scale;
            height = start.Height * scale;
        }
        else
        {
            width = Math.Max(minWidth, width);
            height = Math.Max(minHeight, height);
        }

        var centerShiftX = horizontal * (width - start.Width) / 2;
        var centerShiftY = vertical * (height - start.Height) / 2;
        return new CanvasBounds(
            start.X + start.Width / 2 + centerShiftX * cos - centerShiftY * sin - width / 2,
            start.Y + start.Height / 2 + centerShiftX * sin + centerShiftY * cos - height / 2,
            width, height);
    }

    public static bool TryMapRegion(Rectangle region, Rectangle monitor,
        out Rectangle source, out Point destination, out Rectangle overlap)
    {
        source = Rectangle.Empty;
        destination = Point.Empty;
        overlap = Rectangle.Empty;
        if (region.Width <= 0 || region.Height <= 0 || monitor.Width <= 0 || monitor.Height <= 0)
            return false;
        overlap = Rectangle.Intersect(region, monitor);
        if (overlap.Width <= 0 || overlap.Height <= 0) return false;
        source = new Rectangle(overlap.X - monitor.X, overlap.Y - monitor.Y, overlap.Width, overlap.Height);
        destination = new Point(overlap.X - region.X, overlap.Y - region.Y);
        return true;
    }

    /// <summary>Convert both edges so fractional scaling cannot introduce a seam between regions.</summary>
    public static Rectangle ToPhysicalRegion(double x, double y, double width, double height,
        double scale, int originX, int originY)
    {
        if (!double.IsFinite(scale) || scale <= 0 || !double.IsFinite(x) || !double.IsFinite(y) ||
            !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            return Rectangle.Empty;
        var left = checked((int)Math.Round(x * scale));
        var top = checked((int)Math.Round(y * scale));
        var right = checked((int)Math.Round((x + width) * scale));
        var bottom = checked((int)Math.Round((y + height) * scale));
        return new Rectangle(checked(left + originX), checked(top + originY),
            Math.Max(0, right - left), Math.Max(0, bottom - top));
    }
}

[Flags]
public enum CanvasEdges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

public readonly record struct CanvasBounds(double X, double Y, double Width, double Height);
