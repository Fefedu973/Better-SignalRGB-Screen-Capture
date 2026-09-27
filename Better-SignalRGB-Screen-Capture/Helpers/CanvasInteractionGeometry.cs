using Better_SignalRGB_Screen_Capture.Core.Helpers;
using Windows.Foundation;

namespace Better_SignalRGB_Screen_Capture.Helpers;

internal enum CanvasHandle { None, TopLeft, TopRight, BottomLeft, BottomRight, Top, Bottom, Left, Right, Rotate, Move, CropRotate }

/// <summary>A pointer owns one gesture and one immutable starting geometry until release/cancellation.</summary>
internal sealed record CanvasGesture(uint PointerId, CanvasHandle Handle, Point StartPointer, Rect StartBounds, double StartRotation)
{
    public bool Owns(uint pointerId) => PointerId == pointerId;
}

internal static class CanvasInteractionGeometry
{
    public static readonly CanvasHandle[] ResizeHandles = [CanvasHandle.TopLeft, CanvasHandle.TopRight,
        CanvasHandle.BottomLeft, CanvasHandle.BottomRight, CanvasHandle.Top, CanvasHandle.Bottom, CanvasHandle.Left, CanvasHandle.Right];

    public static CanvasHandle OppositeHandle(CanvasHandle handle) => handle switch
    {
        CanvasHandle.TopLeft => CanvasHandle.BottomRight, CanvasHandle.TopRight => CanvasHandle.BottomLeft,
        CanvasHandle.BottomLeft => CanvasHandle.TopRight, CanvasHandle.BottomRight => CanvasHandle.TopLeft,
        CanvasHandle.Top => CanvasHandle.Bottom, CanvasHandle.Bottom => CanvasHandle.Top,
        CanvasHandle.Left => CanvasHandle.Right, CanvasHandle.Right => CanvasHandle.Left, _ => CanvasHandle.None
    };

    public static CanvasEdges Edges(CanvasHandle handle) => handle switch
    {
        CanvasHandle.Left => CanvasEdges.Left, CanvasHandle.Right => CanvasEdges.Right,
        CanvasHandle.Top => CanvasEdges.Top, CanvasHandle.Bottom => CanvasEdges.Bottom,
        CanvasHandle.TopLeft => CanvasEdges.Top | CanvasEdges.Left,
        CanvasHandle.TopRight => CanvasEdges.Top | CanvasEdges.Right,
        CanvasHandle.BottomLeft => CanvasEdges.Bottom | CanvasEdges.Left,
        CanvasHandle.BottomRight => CanvasEdges.Bottom | CanvasEdges.Right, _ => CanvasEdges.None
    };

    public static Point Center(Rect bounds) => new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);

    // Editor affordances are outside the content frame by design. Only the image,
    // not its rotation button, is constrained to the output canvas.
    public static Point RotationHandlePosition(Rect frame, double rotation = 0, double distance = 22) =>
        Rotate(new Point(frame.X + frame.Width / 2, frame.Top - distance), Center(frame), rotation);

    public static Point Rotate(Point point, Point pivot, double angle)
    {
        var radians = angle * Math.PI / 180;
        var cos = Math.Cos(radians); var sin = Math.Sin(radians);
        var x = point.X - pivot.X; var y = point.Y - pivot.Y;
        return new Point(pivot.X + x * cos - y * sin, pivot.Y + x * sin + y * cos);
    }

    public static double PointerRotation(CanvasGesture gesture, Point current, bool snap)
    {
        var center = Center(gesture.StartBounds);
        var start = Math.Atan2(gesture.StartPointer.Y - center.Y, gesture.StartPointer.X - center.X);
        var end = Math.Atan2(current.Y - center.Y, current.X - center.X);
        var delta = (end - start) * 180 / Math.PI;
        delta = ((delta + 180) % 360 + 360) % 360 - 180;
        var rotation = gesture.StartRotation + delta;
        return snap ? Math.Round(rotation / 15) * 15 : rotation;
    }

    public static Rect Resize(Rect bounds, Point delta, double rotation, CanvasHandle handle,
        double minWidth, double minHeight, bool keepAspect)
    {
        var resized = CanvasGeometry.Resize(new CanvasBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height),
            delta.X, delta.Y, rotation, Edges(handle), minWidth, minHeight, keepAspect);
        return new Rect(resized.X, resized.Y, resized.Width, resized.Height);
    }

    public static bool Fits(Rect bounds, Size parent) => !bounds.IsEmpty && bounds.Left >= -0.001 &&
        bounds.Top >= -0.001 && bounds.Right <= parent.Width + 0.001 && bounds.Bottom <= parent.Height + 0.001;

    public static Point ContainmentOffset(Rect bounds, Size parent)
    {
        if (bounds.IsEmpty) return default;
        double Axis(double first, double last, double available) => last - first > available
            ? (available - first - last) / 2
            : first < 0 ? -first : last > available ? available - last : 0;
        return new Point(Axis(bounds.Left, bounds.Right, parent.Width), Axis(bounds.Top, bounds.Bottom, parent.Height));
    }

    public static Point HandlePosition(Rect bounds, CanvasHandle handle, double rotation = 0)
    {
        var x = handle is CanvasHandle.TopLeft or CanvasHandle.Left or CanvasHandle.BottomLeft ? bounds.Left
            : handle is CanvasHandle.TopRight or CanvasHandle.Right or CanvasHandle.BottomRight ? bounds.Right : bounds.X + bounds.Width / 2;
        var y = handle is CanvasHandle.TopLeft or CanvasHandle.Top or CanvasHandle.TopRight ? bounds.Top
            : handle is CanvasHandle.BottomLeft or CanvasHandle.Bottom or CanvasHandle.BottomRight ? bounds.Bottom : bounds.Y + bounds.Height / 2;
        return Rotate(new Point(x, y), Center(bounds), rotation);
    }

    public static CanvasHandle HitTestCrop(Point point, Rect bounds, double rotation, bool includeRotationHandle = true)
    {
        var rotationPoint = RotationHandlePosition(bounds, rotation);
        if (includeRotationHandle && new Rect(rotationPoint.X - 8, rotationPoint.Y - 8, 16, 16).Contains(point)) return CanvasHandle.CropRotate;
        var local = Rotate(point, Center(bounds), -rotation);
        // Preserve a move target even when a crop is only a few pixels wide.
        var handleWidth = Math.Min(10, bounds.Width / 3);
        var handleHeight = Math.Min(10, bounds.Height / 3);
        foreach (var handle in ResizeHandles)
        {
            var anchor = HandlePosition(bounds, handle);
            if (new Rect(anchor.X - handleWidth / 2, anchor.Y - handleHeight / 2, handleWidth, handleHeight).Contains(local)) return handle;
        }
        return bounds.Contains(local) ? CanvasHandle.Move : CanvasHandle.None;
    }

    public static CanvasHandle HitTestFrame(Point point, Size size, Rect rotationHandle, double cornerSize, double edgeSize, double rotationMargin = 0)
    {
        if (rotationHandle.Contains(point)) return CanvasHandle.Rotate;
        cornerSize = Math.Min(cornerSize, Math.Min(size.Width, size.Height) / 3);
        edgeSize = Math.Min(edgeSize, Math.Min(size.Width, size.Height) / 4);
        var corners = new[] { new Rect(0, 0, cornerSize, cornerSize), new Rect(size.Width - cornerSize, 0, cornerSize, cornerSize),
            new Rect(0, size.Height - cornerSize, cornerSize, cornerSize), new Rect(size.Width - cornerSize, size.Height - cornerSize, cornerSize, cornerSize) };
        for (var index = 0; index < corners.Length; index++)
            if (corners[index].Contains(point)) return ResizeHandles[index];
        if (rotationMargin > 0)
        {
            var extended = new[] { new Rect(-rotationMargin, -rotationMargin, cornerSize + rotationMargin, cornerSize + rotationMargin),
                new Rect(size.Width - cornerSize, -rotationMargin, cornerSize + rotationMargin, cornerSize + rotationMargin),
                new Rect(-rotationMargin, size.Height - cornerSize, cornerSize + rotationMargin, cornerSize + rotationMargin),
                new Rect(size.Width - cornerSize, size.Height - cornerSize, cornerSize + rotationMargin, cornerSize + rotationMargin) };
            if (extended.Any(bounds => bounds.Contains(point))) return CanvasHandle.Rotate;
        }
        if (new Rect(0, 0, size.Width, edgeSize).Contains(point)) return CanvasHandle.Top;
        if (new Rect(0, size.Height - edgeSize, size.Width, edgeSize).Contains(point)) return CanvasHandle.Bottom;
        if (new Rect(0, 0, edgeSize, size.Height).Contains(point)) return CanvasHandle.Left;
        if (new Rect(size.Width - edgeSize, 0, edgeSize, size.Height).Contains(point)) return CanvasHandle.Right;
        return CanvasHandle.None;
    }

    /// <summary>Clamp along a gesture, preserving its anchor instead of rejecting a large pointer jump.</summary>
    public static Rect FurthestValid(Func<double, Rect> candidate, Func<Rect, bool> isValid)
    {
        var target = candidate(1);
        if (isValid(target)) return target;
        var start = candidate(0);
        if (!isValid(start)) return start;
        double lower = 0, upper = 1;
        for (var index = 0; index < 24; index++)
        {
            var middle = (lower + upper) / 2;
            if (isValid(candidate(middle))) lower = middle; else upper = middle;
        }
        return candidate(lower);
    }

    /// <summary>Free resizing has independent limits on canvas-aligned axes. Hitting
    /// one edge must not pull the other dimension back from its requested position.</summary>
    public static Rect ConstrainResize(Point delta, double rotation, bool keepAspect,
        Func<Point, Rect> candidate, Func<Rect, bool> isValid)
    {
        var target = candidate(delta);
        if (isValid(target)) return target;
        if (keepAspect || Math.Abs(Math.Sin(rotation * Math.PI / 90)) > .00001)
            return FurthestValid(t => candidate(new Point(delta.X * t, delta.Y * t)), isValid);

        var local = Rotate(delta, default, -rotation);
        Rect At(double x, double y) => candidate(Rotate(new Point(x, y), default, rotation));
        if (!isValid(At(0, 0))) return At(0, 0);
        double ClampAxis(double requested, Func<double, bool> fits)
        {
            if (fits(requested)) return requested;
            double lower = 0, upper = 1;
            for (var index = 0; index < 24; index++)
            {
                var middle = (lower + upper) / 2;
                if (fits(requested * middle)) lower = middle; else upper = middle;
            }
            return requested * lower;
        }
        var x = ClampAxis(local.X, value => isValid(At(value, 0)));
        var y = ClampAxis(local.Y, value => isValid(At(x, value)));
        return At(x, y);
    }

    /// <summary>The mask rectangle is stored in source coordinates; its rotation may extend outside the image.</summary>
    public static Rect ConstrainCrop(Rect bounds, Size source)
    {
        var width = Math.Clamp(bounds.Width, source.Width * .01, source.Width);
        var height = Math.Clamp(bounds.Height, source.Height * .01, source.Height);
        return new Rect(Math.Clamp(bounds.X, 0, source.Width - width), Math.Clamp(bounds.Y, 0, source.Height - height), width, height);
    }

    // The four resize cursor axes, counted clockwise from north/south.
    public static int CursorAxis(CanvasHandle handle, double rotation)
    {
        var direction = handle switch { CanvasHandle.Top => 0, CanvasHandle.TopRight => 1, CanvasHandle.Right => 2,
            CanvasHandle.BottomRight => 3, CanvasHandle.Bottom => 4, CanvasHandle.BottomLeft => 5,
            CanvasHandle.Left => 6, CanvasHandle.TopLeft => 7, _ => 0 };
        var steps = (int)Math.Round(rotation / 45);
        return ((direction + steps) % 4 + 4) % 4;
    }
}
