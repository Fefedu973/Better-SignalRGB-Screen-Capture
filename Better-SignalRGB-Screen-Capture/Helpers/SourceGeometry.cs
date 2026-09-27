using Windows.Foundation;
using Better_SignalRGB_Screen_Capture.Models;

namespace Better_SignalRGB_Screen_Capture.Helpers;

public static class SourceGeometry
{
    /// <summary>
    /// Calculates the Axis-Aligned Bounding Box (AABB) of a source's visible (cropped) area.
    /// This is the definitive check for whether a source is "inside" the canvas.
    /// The visible area is the intersection of the crop rectangle and the source's bounds.
    /// </summary>
    /// <param name="src">The source item to check.</param>
    /// <returns>The AABB of the visible, rotated, cropped area in canvas coordinates.</returns>
    public static Rect GetVisibleAreaAabb(SourceItem src) =>
        GetVisibleAreaAabb(src, src.CanvasX, src.CanvasY, src.CanvasWidth, src.CanvasHeight, src.Rotation);

    public static Rect GetVisibleAreaAabb(SourceItem src, double x, double y, double width, double height, double rotation) =>
        Bounds(GetVisiblePolygon(src, x, y, width, height, rotation));

    public static IReadOnlyList<Point> GetVisiblePolygon(SourceItem source) =>
        GetVisiblePolygon(source, source.CanvasX, source.CanvasY, source.CanvasWidth, source.CanvasHeight, source.Rotation);

    /// <summary>The actual visible polygon in canvas coordinates; mirroring moves the image, not its mask.</summary>
    public static IReadOnlyList<Point> GetVisiblePolygon(SourceItem src, double x, double y, double width, double height, double rotation)
    {
        if (src == null || !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) ||
            !double.IsFinite(height) || !double.IsFinite(rotation) || width <= 0 || height <= 0) return Array.Empty<Point>();

        double w = width;
        double h = height;

        // Calculate the effective size and offset of the cropped area
        double effW = w * (1 - src.CropLeftPct - src.CropRightPct);
        double effH = h * (1 - src.CropTopPct - src.CropBottomPct);
        if (effW <= 0 || effH <= 0) return Array.Empty<Point>(); // fully cropped

        double offX = w * src.CropLeftPct;
        double offY = h * src.CropTopPct;

        // Build crop rectangle corners in local item space
        var cropPolygon = new List<Point>
        {
            new(offX,          offY),
            new(offX + effW,   offY),
            new(offX + effW,   offY + effH),
            new(offX,          offY + effH)
        };

        // Rotate crop rectangle around its own center if it has rotation
        if (src.CropRotation % 360 != 0)
        {
            double cropCenterX = offX + effW / 2.0;
            double cropCenterY = offY + effH / 2.0;
            double rad = src.CropRotation * Math.PI / 180.0;
            double cos = Math.Cos(rad);
            double sin = Math.Sin(rad);
            for (int i = 0; i < cropPolygon.Count; i++)
            {
                double dx = cropPolygon[i].X - cropCenterX;
                double dy = cropPolygon[i].Y - cropCenterY;
                double rx = dx * cos - dy * sin;
                double ry = dx * sin + dy * cos;
                cropPolygon[i] = new Point(rx + cropCenterX, ry + cropCenterY);
            }
        }

        // The crop rectangle can overflow the source item's bounds.
        // We need to find the intersection of the crop polygon and the source's bounding rect.
        var sourceBounds = new Rect(0, 0, w, h);
        var clippedPolygon = ClipPolygonWithRect(cropPolygon, sourceBounds);
        if (clippedPolygon.Count == 0) return clippedPolygon;

        // Now, transform the clipped polygon to canvas coordinates.
        // This involves rotating by the item's rotation and translating.
        double itemRad = rotation * Math.PI / 180.0;
        double itemCos = Math.Cos(itemRad);
        double itemSin = Math.Sin(itemRad);
        double itemCenterX = w / 2.0;
        double itemCenterY = h / 2.0;
        for (int i = 0; i < clippedPolygon.Count; i++)
        {
            double dx = clippedPolygon[i].X - itemCenterX;
            double dy = clippedPolygon[i].Y - itemCenterY;
            double rx = dx * itemCos - dy * itemSin + itemCenterX + x;
            double ry = dx * itemSin + dy * itemCos + itemCenterY + y;
            clippedPolygon[i] = new Point(rx, ry);
        }

        return clippedPolygon;
    }

    public static Rect Bounds(IReadOnlyList<Point> polygon)
    {
        if (polygon.Count == 0) return Rect.Empty;
        var minX = polygon.Min(point => point.X);
        var minY = polygon.Min(point => point.Y);
        return new Rect(minX, minY, polygon.Max(point => point.X) - minX, polygon.Max(point => point.Y) - minY);
    }

    public static bool ContainsVisiblePoint(SourceItem source, Point canvasPoint) =>
        ContainsPoint(GetVisiblePolygon(source), canvasPoint);

    /// <summary>Inclusive test for the convex polygon produced by clipping the source and crop rectangles.</summary>
    public static bool ContainsPoint(IReadOnlyList<Point> polygon, Point point)
    {
        if (polygon.Count < 3 || !double.IsFinite(point.X) || !double.IsFinite(point.Y)) return false;
        var positive = false;
        var negative = false;
        for (var index = 0; index < polygon.Count; index++)
        {
            var first = polygon[index];
            var second = polygon[(index + 1) % polygon.Count];
            var cross = (second.X - first.X) * (point.Y - first.Y) - (second.Y - first.Y) * (point.X - first.X);
            positive |= cross > 0.000001;
            negative |= cross < -0.000001;
            if (positive && negative) return false;
        }
        return positive || negative;
    }

    /// <summary>Marquee selection ignores the empty corners of a rotated or cropped source's AABB.</summary>
    public static bool IntersectsVisibleArea(SourceItem source, Rect selection)
    {
        if (selection.IsEmpty || selection.Width <= 0 || selection.Height <= 0) return false;
        var clipped = ClipPolygonWithRect(GetVisiblePolygon(source).ToList(), selection);
        double twiceArea = 0;
        for (var index = 0; index < clipped.Count; index++)
        {
            var first = clipped[index];
            var second = clipped[(index + 1) % clipped.Count];
            twiceArea += first.X * second.Y - second.X * first.Y;
        }
        return Math.Abs(twiceArea) > 0.000001;
    }

    /// <summary>
    /// Clips a polygon using the Sutherland-Hodgman algorithm against a rectangular clip window.
    /// </summary>
    private static List<Point> ClipPolygonWithRect(List<Point> polygon, Rect clipRect)
    {
        // Helper to find the intersection of a line segment with a clip edge.
        static Point Intersect(Point p1, Point p2, double edge, bool isVertical)
        {
            // Avoid division by zero
            if (isVertical)
            {
                if (p2.X - p1.X == 0) return new Point(edge, p1.Y); // Vertical line on the edge
                double t = (edge - p1.X) / (p2.X - p1.X);
                return new Point(edge, p1.Y + t * (p2.Y - p1.Y));
            }
            else // Horizontal edge
            {
                if (p2.Y - p1.Y == 0) return new Point(p1.X, edge); // Horizontal line on the edge
                double t = (edge - p1.Y) / (p2.Y - p1.Y);
                return new Point(p1.X + t * (p2.X - p1.X), edge);
            }
        }

        // Helper to perform clipping against a single edge of the rectangle.
        List<Point> ClipEdge(List<Point> inputPolygon, Func<Point, bool> isInside, Func<Point, Point, Point> intersectionCalc)
        {
            var outputList = new List<Point>();
            if (inputPolygon.Count == 0) return outputList;

            var s = inputPolygon[^1]; // Start with the last vertex
            foreach (var p in inputPolygon)
            {
                bool s_inside = isInside(s);
                bool p_inside = isInside(p);

                if (p_inside)
                {
                    if (!s_inside)
                    {
                        // S is outside, P is inside: intersection, then P
                        outputList.Add(intersectionCalc(s, p));
                    }
                    outputList.Add(p);
                }
                else if (s_inside)
                {
                    // S is inside, P is outside: intersection
                    outputList.Add(intersectionCalc(s, p));
                }
                // If both are outside, do nothing.
                s = p; // Move to the next edge
            }
            return outputList;
        }

        var clipped = polygon;
        clipped = ClipEdge(clipped, p => p.X >= clipRect.Left, (p1, p2) => Intersect(p1, p2, clipRect.Left, true));
        clipped = ClipEdge(clipped, p => p.X <= clipRect.Right, (p1, p2) => Intersect(p1, p2, clipRect.Right, true));
        clipped = ClipEdge(clipped, p => p.Y >= clipRect.Top, (p1, p2) => Intersect(p1, p2, clipRect.Top, false));
        clipped = ClipEdge(clipped, p => p.Y <= clipRect.Bottom, (p1, p2) => Intersect(p1, p2, clipRect.Bottom, false));

        return clipped;
    }

    public static Rect GetRotatedAabb(Point center, Size size, double angleDeg)
    {
        var angle = angleDeg * Math.PI / 180;
        var cosine = Math.Abs(Math.Cos(angle));
        var sine = Math.Abs(Math.Sin(angle));
        var width = size.Width * cosine + size.Height * sine;
        var height = size.Width * sine + size.Height * cosine;
        return new Rect(center.X - width / 2, center.Y - height / 2, width, height);
    }
}
