using Better_SignalRGB_Screen_Capture.Helpers;
using Better_SignalRGB_Screen_Capture.Models;
using Windows.Foundation;

namespace BetterSignalRGB.RegressionTests;

internal static class CropInteractionTests
{
    public static void Run()
    {
        CroppedAreasDoNotReceivePointerOrMarqueeHits();
        RotationExcludesEmptyBoundingBoxCorners();
        RotatedCropIsClippedBeforeSourceRotation();
        MirroringDoesNotMoveTheCropMask();
        NarrowVisibleSliversRemainSelectable();
        PolygonHitsMatchInverseRectangleCoordinates();
        CandidateGeometryDoesNotMutateTheSource();
    }

    private static void CroppedAreasDoNotReceivePointerOrMarqueeHits()
    {
        var source = Source(40, 30, 100, 100);
        source.SetCrop(.9, 0, 0, 0, 0);
        Assert.True(!SourceGeometry.ContainsVisiblePoint(source, new Point(70, 80)),
            "A point inside the original source but outside the crop must pass through");
        Assert.True(SourceGeometry.ContainsVisiblePoint(source, new Point(135, 80)), "Visible crop remains selectable");
        Assert.True(!SourceGeometry.IntersectsVisibleArea(source, new Rect(50, 50, 30, 30)),
            "Marquee on removed content must not select the source");
        Assert.True(SourceGeometry.IntersectsVisibleArea(source, new Rect(129, 70, 3, 5)),
            "Marquee crossing the visible edge selects the source");
        Assert.True(!SourceGeometry.IntersectsVisibleArea(source, new Rect(120, 70, 10, 5)),
            "Touching a crop edge with zero overlap is not an area selection");
        Assert.True(SourceGeometry.ContainsVisiblePoint(source, new Point(130, 30)),
            "Pointer hit includes the exact visible corner");
    }

    private static void RotationExcludesEmptyBoundingBoxCorners()
    {
        var source = Source(50, 50, 100, 100, 45);
        var bounds = SourceGeometry.GetVisibleAreaAabb(source);
        Assert.Near(100 - 50 * Math.Sqrt(2), bounds.Left, 1e-4, "Diamond bounding box left");
        var emptyCorner = new Point(35, 35);
        Assert.True(bounds.Contains(emptyCorner), "The regression point is inside the bounding box");
        Assert.True(!SourceGeometry.ContainsVisiblePoint(source, emptyCorner), "Empty diamond corner is not a pointer hit");
        Assert.True(!SourceGeometry.IntersectsVisibleArea(source, new Rect(32, 32, 10, 10)),
            "Empty diamond corner is not a marquee hit");
        Assert.True(SourceGeometry.IntersectsVisibleArea(source, new Rect(95, 29, 10, 5)),
            "Marquee crossing the diamond tip hits actual content");
        Assert.True(SourceGeometry.ContainsVisiblePoint(source, new Point(100, 100)), "Diamond center is visible");
    }

    private static void RotatedCropIsClippedBeforeSourceRotation()
    {
        var source = Source(0, 0, 100, 100);
        source.SetCrop(0, 0, 0, 0, 45);
        var clipped = SourceGeometry.GetVisiblePolygon(source);
        Assert.Equal(8, clipped.Count, "Full square crop rotated 45 degrees clips to an octagon");
        Assert.True(!SourceGeometry.ContainsVisiblePoint(source, new Point(1, 1)), "Clipped octagon excludes original corner");
        Assert.True(SourceGeometry.ContainsVisiblePoint(source, new Point(50, 0)), "Clipped octagon keeps source edge midpoint");
        NearRect(new Rect(0, 0, 100, 100), SourceGeometry.Bounds(clipped), "Octagon source bounds");

        source.Rotation = 45;
        NearRect(new Rect(0, 0, 100, 100), SourceGeometry.GetVisibleAreaAabb(source),
            "The clipped octagon, not its bounding rectangle, is rotated into canvas space");

        source = Source(20, 30, 200, 100, 90);
        source.SetCrop(0, 0, 0, 0, 90);
        NearRect(new Rect(70, 30, 100, 100), SourceGeometry.GetVisibleAreaAabb(source),
            "A rotated wide crop clips to the central square before item rotation");
    }

    private static void MirroringDoesNotMoveTheCropMask()
    {
        var source = Source(-30, 15, 120, 80, 37);
        source.SetCrop(.6, .1, .05, .3, -28);
        var original = SourceGeometry.GetVisiblePolygon(source).ToArray();
        foreach (var horizontal in new[] { false, true })
        foreach (var vertical in new[] { false, true })
        {
            source.IsMirroredHorizontally = horizontal;
            source.IsMirroredVertically = vertical;
            var actual = SourceGeometry.GetVisiblePolygon(source);
            Assert.Equal(original.Length, actual.Count, "Image mirrors preserve mask vertex count");
            for (var index = 0; index < original.Length; index++)
            {
                Assert.Near(original[index].X, actual[index].X, 1e-8, "Image mirrors preserve mask X");
                Assert.Near(original[index].Y, actual[index].Y, 1e-8, "Image mirrors preserve mask Y");
            }
        }
    }

    private static void NarrowVisibleSliversRemainSelectable()
    {
        var source = Source(0, 0, 1, 1);
        source.SetCrop(.99, .99, 0, 0, 0);
        Assert.True(SourceGeometry.ContainsVisiblePoint(source, new Point(.995, .995)),
            "Subpixel visible crop is still hittable in logical canvas coordinates");
        Assert.True(!SourceGeometry.ContainsVisiblePoint(source, new Point(.98, .995)), "Excluded part of small source passes through");
        Assert.True(SourceGeometry.IntersectsVisibleArea(source, new Rect(.99, .99, .01, .01)),
            "Subpixel crop still has positive selectable area");
        Assert.True(!SourceGeometry.ContainsVisiblePoint(source, new Point(double.NaN, 0)), "NaN pointer is not a hit");
        Assert.True(!SourceGeometry.IntersectsVisibleArea(source, Rect.Empty), "Empty marquee is not a hit");
    }

    private static void PolygonHitsMatchInverseRectangleCoordinates()
    {
        // Independent reference: transform the query point back into the two rectangle
        // coordinate systems instead of clipping polygons and testing their edges.
        foreach (var itemAngle in new[] { 0, 37, 90, 179, 271 })
        foreach (var cropAngle in new[] { -179, -45, 0, 73 })
        {
            var source = Source(-43, 27, 120, 80, itemAngle);
            source.SetCrop(.15, .05, .4, .25, cropAngle);
            for (var row = 0; row < 6; row++)
            for (var column = 0; column < 7; column++)
            {
                var point = new Point(-70 + column * 29.37, -15 + row * 31.61);
                var local = Unrotate(new Point(point.X - source.CanvasX, point.Y - source.CanvasY),
                    new Point(60, 40), itemAngle);
                var cropLocal = Unrotate(local, new Point(45, 32), cropAngle);
                var expected = local.X >= 0 && local.X <= 120 && local.Y >= 0 && local.Y <= 80 &&
                    cropLocal.X >= 18 && cropLocal.X <= 72 && cropLocal.Y >= 4 && cropLocal.Y <= 60;
                Assert.Equal(expected, SourceGeometry.ContainsVisiblePoint(source, point),
                    $"Crop/item inverse transforms agree at angles {cropAngle}/{itemAngle}, grid {column}/{row}");
            }
        }
    }

    private static void CandidateGeometryDoesNotMutateTheSource()
    {
        var source = Source(12, 15, 100, 80, 0);
        source.SetCrop(.25, .25, .25, .25, 0);
        var candidate = SourceGeometry.GetVisiblePolygon(source, -25, 0, 200, 100, 90);
        NearRect(new Rect(50, 0, 50, 100), SourceGeometry.Bounds(candidate), "Candidate crop rotation around candidate center");
        Assert.Equal(12, source.CanvasX, "Candidate query preserves original position");
        Assert.Equal(100, source.CanvasWidth, "Candidate query preserves original size");
        Assert.Equal(0, source.Rotation, "Candidate query preserves original rotation");
        Assert.Equal(0, SourceGeometry.GetVisiblePolygon(source, 0, 0, double.NaN, 100, 0).Count,
            "Invalid candidate dimensions produce no visible polygon");
    }

    private static Point Unrotate(Point point, Point center, double angle)
    {
        var radians = angle * Math.PI / 180;
        var x = point.X - center.X;
        var y = point.Y - center.Y;
        return new Point(center.X + x * Math.Cos(radians) + y * Math.Sin(radians),
            center.Y - x * Math.Sin(radians) + y * Math.Cos(radians));
    }

    private static SourceItem Source(int x, int y, int width, int height, int angle = 0) => new()
    { CanvasX = x, CanvasY = y, CanvasWidth = width, CanvasHeight = height, Rotation = angle };

    private static void NearRect(Rect expected, Rect actual, string message)
    {
        Assert.Near(expected.X, actual.X, 1e-4, message + " X");
        Assert.Near(expected.Y, actual.Y, 1e-4, message + " Y");
        Assert.Near(expected.Width, actual.Width, 1e-4, message + " width");
        Assert.Near(expected.Height, actual.Height, 1e-4, message + " height");
    }
}
