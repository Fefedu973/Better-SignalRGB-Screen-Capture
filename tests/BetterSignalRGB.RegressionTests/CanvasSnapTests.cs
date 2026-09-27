using Better_SignalRGB_Screen_Capture.Helpers;
using Better_SignalRGB_Screen_Capture.Models;
using Windows.Foundation;

namespace BetterSignalRGB.RegressionTests;

internal static class CanvasSnapTests
{
    public static void Run()
    {
        Rect[] references = [new(0, 0, 320, 200), new(100, 20, 50, 40)];
        var moved = CanvasSnapGeometry.Move(new Rect(46, 23, 50, 40), references, CanvasSnapGeometry.Threshold(1));
        Assert.Near(4, moved.Delta.X, .001, "Drag snaps the nearest visible edge to another source");
        Assert.Near(-3, moved.Delta.Y, .001, "Drag snaps an independent vertical alignment");
        Assert.Equal(2, moved.Guides.Count, "Both aligned axes receive guides");
        var zoomed = CanvasSnapGeometry.Move(new Rect(46, 23, 50, 40), references, CanvasSnapGeometry.Threshold(2));
        Assert.Near(0, zoomed.Delta.X, .001, "A four-unit gap exceeds six screen pixels at 200 percent zoom");
        Assert.Near(60, CanvasSnapGeometry.Threshold(.1), .001, "Snap distance follows zoomed-out screen coordinates");
        var bypass = CanvasSnapGeometry.Move(new Rect(46, 23, 50, 40), references, 0);
        Assert.Equal(0, bypass.Guides.Count, "Disabled snapping never produces guides");
        var centered = CanvasSnapGeometry.Move(new Rect(114, 75, 100, 50), [new Rect(0, 0, 320, 200)], 6);
        Assert.Near(-4, centered.Delta.X, .001, "Canvas center participates in snapping");

        var source = new SourceItem { CanvasX = 20, CanvasY = 30, CanvasWidth = 100, CanvasHeight = 60 };
        source.SetCrop(.5, 0, 0, 0, 0);
        var gesture = new CanvasGesture(1, CanvasHandle.Right, default, new Rect(20, 30, 100, 60), 0);
        Rect Visible(Point delta)
        {
            var bounds = CanvasSourceResize.Resize(source, gesture, delta, false);
            return SourceGeometry.GetVisibleAreaAabb(source, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0);
        }
        var resized = CanvasSnapGeometry.Resize(new Point(76, 0), Visible, [new Rect(200, 130, 50, 30)], 6);
        Assert.Near(80, resized.Delta.X, .01, "Resize corrects the pointer delta to reach the guide");
        Assert.Near(70, Visible(resized.Delta).Left, .001, "Snapped crop resize preserves the opposite visible anchor");
        Assert.Near(200, Visible(resized.Delta).Right, .001, "Crop edge reaches the source guide");
        Assert.True(resized.Guides.Any(guide => guide.Vertical && guide.Position == 200), "Snapped resize reports its real guide");
        Assert.True(!CanvasSnapGeometry.Matches(new CanvasSnapGuide(true, 201, 2), Visible(resized.Delta), .75),
            "A boundary-clamped result cannot advertise a guide it failed to reach");

        var rotated = new CanvasGesture(1, CanvasHandle.BottomRight, default, new Rect(70, 55, 80, 40), 35);
        Rect RotatedVisible(Point delta)
        {
            var bounds = CanvasInteractionGeometry.Resize(rotated.StartBounds, delta, 35, rotated.Handle, 1, 1, false);
            return SourceGeometry.GetRotatedAabb(CanvasInteractionGeometry.Center(bounds), new Size(bounds.Width, bounds.Height), 35);
        }
        var raw = new Point(12, 8); var rawVisible = RotatedVisible(raw);
        var snapped = CanvasSnapGeometry.Resize(raw, RotatedVisible, [new Rect(rawVisible.Right + 3, 150, 50, 20)], 6);
        Assert.True(snapped.Guides.Any(guide => guide.Vertical), "Rotated resize can reach a canvas-axis alignment without changing its anchor model");
        Assert.Near(rawVisible.Right + 3, RotatedVisible(snapped.Delta).Right, .05, "Rotated visible AABB lands on the reference");
        var locked = source.Clone(); locked.IsLocked = true;
        Assert.True(locked.Clone(preserveId: true).IsLocked, "Clone preserves layout locks for history and scenes");
        var serialized = System.Text.Json.JsonSerializer.Serialize(locked);
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<SourceItem>(serialized)!.IsLocked, "Layout lock survives persistence");
        VerifyHqScreenDistances();
    }

    private static void VerifyHqScreenDistances()
    {
        foreach (var zoom in new[] { .1, .5, 1, 2, 10 })
        {
            var threshold = CanvasSnapGeometry.Thresholds(zoom, 2.5, 3);
            Assert.Near(6, threshold.Width * zoom * 2.5, .00001, "HQ horizontal snap threshold stays six screen pixels");
            Assert.Near(6, threshold.Height * zoom * 3, .00001, "HQ vertical snap threshold stays six screen pixels");
            var bounds = new Rect(100 - 5.9 / (zoom * 2.5) - 20, 100 - 6.1 / (zoom * 3) - 20, 20, 20);
            var snap = CanvasSnapGeometry.Move(bounds, [new Rect(100, 100, 30, 30)], threshold);
            Assert.Near(5.9, snap.Delta.X * zoom * 2.5, .0001, "HQ drag reaches an edge within six horizontal screen pixels");
            Assert.Near(0, snap.Delta.Y, .0001, "HQ drag ignores an edge beyond six vertical screen pixels");
        }
        var hq = CanvasSnapGeometry.Thresholds(1, 2.5, 3);
        Rect FreeResize(Point delta) => new(10, 10, 80 + delta.X, 80 + delta.Y);
        var resized = CanvasSnapGeometry.Resize(default, FreeResize, [new Rect(92.3, 92.1, 30, 30)], hq);
        Assert.Near(2.3, resized.Delta.X, .001, "HQ free resize applies horizontal snapping in canonical coordinates");
        Assert.Near(0, resized.Delta.Y, .001, "HQ free resize leaves an out-of-range vertical alignment alone");
        Rect Proportional(Point delta) => new(0, 0, 100 + delta.X + delta.Y, 100 + delta.X + delta.Y);
        var proportional = CanvasSnapGeometry.Resize(default, Proportional, [new Rect(102, 101.9, 30, 30)], hq);
        Assert.Near(102, Proportional(proportional.Delta).Right, .001,
            "HQ proportional resize chooses the nearer screen-space guide, not the smaller canonical gap");
    }
}
