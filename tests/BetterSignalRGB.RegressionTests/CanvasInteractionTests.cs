using Better_SignalRGB_Screen_Capture.Helpers;
using Better_SignalRGB_Screen_Capture.Models;
using Windows.Foundation;

namespace BetterSignalRGB.RegressionTests;

internal static class CanvasInteractionTests
{
    public static void Run()
    {
        var gesture = new CanvasGesture(42, CanvasHandle.Move, default, new Rect(0, 0, 10, 10), 0);
        var stateSource = new SourceItem { CanvasX = 10, CanvasY = 20, CanvasWidth = 30, CanvasHeight = 40, Rotation = 25 };
        var state = CanvasSourceState.Capture(stateSource);
        Assert.True(!state.HasChanged, "A click without geometry changes does not enter undo history");
        stateSource.CanvasX = 100; stateSource.CanvasWidth = 300; stateSource.Rotation = 140;
        Assert.True(state.HasChanged, "Changed gesture is detected at commit");
        state.Apply();
        Assert.True(!state.HasChanged, "Canceled gesture restores all geometry and becomes a no-op for history");
        stateSource.SetCrop(.1234567890123, .234567890123, .1234567890123, .234567890123, 31);
        var originalCrop = CanvasCropState.Capture(stateSource);
        stateSource.SetCrop(0, 0, 0, 0, 0);
        originalCrop.Apply(stateSource);
        Assert.Equal(originalCrop, CanvasCropState.Capture(stateSource), "Cancel retains exact crop fractions without WinRT rectangle float truncation");
        Assert.True(gesture.Owns(42) && !gesture.Owns(43), "A second pointer cannot take over a gesture");
        Assert.Equal(CanvasHandle.None, CanvasInteractionGeometry.HitTestFrame(new Point(3, 3), new Size(6, 6), Rect.Empty, 12, 8),
            "A 6px source retains a center drag target");
        Assert.Equal(CanvasHandle.Move, CanvasInteractionGeometry.HitTestCrop(new Point(3, 3), new Rect(0, 0, 6, 6), 45),
            "A tiny rotated crop retains a center move target");
        Assert.Equal(CanvasHandle.TopLeft, CanvasInteractionGeometry.HitTestFrame(new Point(0, 0), new Size(6, 6), Rect.Empty, 12, 8),
            "Tiny source still exposes corner resize");
        var rotation = new CanvasGesture(1, CanvasHandle.Rotate, new Point(-10, .001), new Rect(-5, -5, 10, 10), 350);
        Assert.Near(350, CanvasInteractionGeometry.PointerRotation(rotation, new Point(-10, -.001), false), .02,
            "Rotation crosses atan2 seam without a 360-degree jump");

        foreach (var sourceRotation in new[] { 0, 37, 90, 217 })
        foreach (var cropRotation in new[] { 0, 35 })
        foreach (var handle in CanvasInteractionGeometry.ResizeHandles)
        {
            var source = new SourceItem { CanvasX = 40, CanvasY = 30, CanvasWidth = 180, CanvasHeight = 110, Rotation = sourceRotation };
            source.SetCrop(.23, .17, .1, .26, cropRotation);
            var start = new Rect(40, 30, 180, 110);
            var drag = new CanvasGesture(1, handle, default, start, sourceRotation);
            var end = CanvasSourceResize.Resize(source, drag, new Point(18, 9), false);
            var opposite = Opposite(handle);
            var before = VisibleAnchor(source, start, sourceRotation, opposite);
            var after = VisibleAnchor(source, end, sourceRotation, opposite);
            Assert.Near(before.X, after.X, .0001, $"Visible opposite X stays anchored ({sourceRotation}/{cropRotation}/{handle})");
            Assert.Near(before.Y, after.Y, .0001, $"Visible opposite Y stays anchored ({sourceRotation}/{cropRotation}/{handle})");
            if (cropRotation != 0)
                Assert.Near(start.Width / start.Height, end.Width / end.Height, .000001, "Rotated crop resize preserves its clipped shape");
        }

        var clipped = new SourceItem { CanvasX = 200, CanvasY = 20, CanvasWidth = 100, CanvasHeight = 60 };
        var thin = new SourceItem { CanvasWidth = 100, CanvasHeight = 100 };
        thin.SetCrop(.9, 0, 0, 0, 0);
        foreach (var angle in new[] { 0, 45, 90, 180, 270 })
        {
            var rotated = CanvasSourceResize.Rotate(thin, new Rect(0, 0, 100, 100), 0, angle);
            var pivot = CanvasInteractionGeometry.Center(CanvasSourceResize.VisibleFrame(thin, rotated, angle));
            Assert.Near(95, pivot.X, .0001, "Thin crop rotation keeps its visible center X");
            Assert.Near(50, pivot.Y, .0001, "Thin crop rotation keeps its visible center Y");
        }
        var boundaryGesture = new CanvasGesture(1, CanvasHandle.Right, default, new Rect(200, 20, 100, 60), 0);
        var result = BoundedResize(clipped, boundaryGesture, new Point(100, 0), new Size(320, 200));
        Assert.Near(120, result.Width, .001, "Large pointer jump resizes to canvas edge instead of rejecting the gesture");
        Assert.Near(200, result.X, .001, "Canvas constraint preserves the opposite edge");

        clipped.CanvasX = 0; clipped.CanvasY = 0; clipped.CanvasWidth = 100; clipped.CanvasHeight = 100;
        clipped.SetCrop(.99, 0, 0, 0, 0);
        result = BoundedResize(clipped, new CanvasGesture(1, CanvasHandle.Right, default, new Rect(0, 0, 100, 100), 0),
            new Point(150, 0), new Size(320, 200));
        Assert.Near(7680, result.Width, .001, "A thin crop cannot grow beyond the model maximum then jump off canvas");
        Assert.True(SourceGeometry.GetVisibleAreaAabb(clipped, result.X, result.Y, result.Width, result.Height, 0).Left >= 0,
            "Maximum constrained crop remains visible");
        clipped.SetCrop(.126, 0, 0, 0, 0);
        var position = CanvasSourceResize.ConstrainPosition(clipped, -100, 0, new Size(320, 200));
        Assert.Near(-12, position.X, .001, "Fractional crop boundary uses an admissible integer position");

        var crop = CanvasInteractionGeometry.ConstrainCrop(new Rect(-10, -20, .001, .001), new Size(1000, 500));
        Assert.Near(10, crop.Width, .00001, "Crop minimum matches the source model's one percent");
        Assert.Near(5, crop.Height, .00001, "Crop minimum follows each axis independently");
        var rotatedCrop = CanvasInteractionGeometry.Resize(new Rect(0, 0, 100, 100), new Point(0, 20), 45,
            CanvasHandle.TopLeft, 1, 1, false);
        rotatedCrop = CanvasInteractionGeometry.ConstrainCrop(rotatedCrop, new Size(100, 100));
        Assert.True(rotatedCrop.Width < 100 && rotatedCrop.Height < 100, "A full crop rotated 45 degrees can shrink even though its corners lie outside the source");

        var angled = new SourceItem { CanvasX = 20, CanvasY = 20, CanvasWidth = 100, CanvasHeight = 20, Rotation = 45 };
        var aligned = new SourceItem { CanvasX = 140, CanvasY = 20, CanvasWidth = 50, CanvasHeight = 40, Rotation = 90 };
        Assert.True(CanvasGroupTransform.RequiresUniformScale([CanvasSourceState.Capture(angled), CanvasSourceState.Capture(aligned)], 0),
            "Anisotropic group scaling is constrained when a child would need shear");
        Assert.True(!CanvasGroupTransform.RequiresUniformScale([CanvasSourceState.Capture(aligned)], 0),
            "Axis-aligned group members retain independent width and height");
        var change = CanvasGroupTransform.Calculate(CanvasSourceState.Capture(aligned), new Rect(0, 0, 200, 100), 0,
            new Rect(0, 0, 400, 100), 0);
        Assert.Equal(50, change.Width, "A 90 degree child keeps its local width during horizontal group scaling");
        Assert.Equal(80, change.Height, "A 90 degree child scales its local height during horizontal group scaling");
        VerifyCanvasEdgeResize();
    }

    private static Rect BoundedResize(SourceItem source, CanvasGesture gesture, Point delta, Size available) =>
        CanvasSourceResize.ResizeWithinCanvas(source, gesture, delta, false, available);

    private static void VerifyCanvasEdgeResize()
    {
        var canvas = new Size(320, 200);
        foreach (var (handle, x, y, dx, dy) in new[]
        {
            (CanvasHandle.BottomRight, 0, 0, 221d, 150d),
            (CanvasHandle.TopRight, 0, 150, 220d, -151d),
            (CanvasHandle.TopLeft, 220, 150, -221d, -150d),
            (CanvasHandle.BottomLeft, 220, 0, -220d, 151d)
        })
        {
            var source = new SourceItem { CanvasX = x, CanvasY = y, CanvasWidth = 100, CanvasHeight = 50 };
            var gesture = new CanvasGesture(1, handle, default, new Rect(x, y, 100, 50), 0);
            foreach (var multiplier in new[] { 1d, 10d })
            {
                var bounds = BoundedResize(source, gesture, new Point(dx * multiplier, dy * multiplier), canvas);
                Assert.Equal(new Rect(0, 0, 320, 200), bounds, $"{handle}: hitting one edge never pulls the independent axis away from the other canvas edge");
            }
        }
        foreach (var (angle, handle) in new[] { (0, CanvasHandle.BottomRight), (90, CanvasHandle.TopRight),
                     (180, CanvasHandle.TopLeft), (270, CanvasHandle.BottomLeft) })
        {
            var sideways = angle % 180 != 0;
            var width = sideways ? 50 : 100; var height = sideways ? 100 : 50;
            var start = new Rect(50 - width / 2d, 25 - height / 2d, width, height);
            var source = new SourceItem { CanvasX = (int)start.X, CanvasY = (int)start.Y,
                CanvasWidth = width, CanvasHeight = height, Rotation = angle };
            var bounds = BoundedResize(source, new CanvasGesture(1, handle, default, start, angle), new Point(220.6, 150), canvas);
            var visible = SourceGeometry.GetVisibleAreaAabb(source, bounds.X, bounds.Y, bounds.Width, bounds.Height, angle);
            Assert.Near(0, visible.Left, .001, "Quarter-turn full coverage keeps the left anchor");
            Assert.Near(0, visible.Top, .001, "Quarter-turn full coverage keeps the top anchor");
            Assert.Near(320, visible.Right, .001, "Quarter-turn resize independently reaches the right edge");
            Assert.Near(200, visible.Bottom, .001, "Quarter-turn resize independently reaches the bottom edge");
        }
        var cropped = new SourceItem { CanvasX = -50, CanvasY = -10, CanvasWidth = 200, CanvasHeight = 100 };
        cropped.SetCrop(.25, .1, .25, .4, 0);
        var cropGesture = new CanvasGesture(1, CanvasHandle.BottomRight, default, new Rect(-50, -10, 200, 100), 0);
        var cropBounds = BoundedResize(cropped, cropGesture, new Point(230, 150), canvas);
        Assert.Equal(new Rect(0, 0, 320, 200), SourceGeometry.GetVisibleAreaAabb(cropped, cropBounds.X, cropBounds.Y,
            cropBounds.Width, cropBounds.Height, 0), "Cropped visible bounds fill both axes while full source extends beyond the canvas");

        var plain = new SourceItem { CanvasWidth = 100, CanvasHeight = 50 };
        var proportional = CanvasSourceResize.ResizeWithinCanvas(plain,
            new CanvasGesture(1, CanvasHandle.BottomRight, default, new Rect(0, 0, 100, 50), 0), new Point(220, 150), true, canvas);
        Assert.Equal(new Rect(0, 0, 320, 160), proportional, "Explicit aspect lock retains the ratio instead of stretching to fill");

        var quarter = new SourceItem { CanvasX = 25, CanvasY = -25, CanvasWidth = 50, CanvasHeight = 100, Rotation = 90 };
        var quarterGesture = new CanvasGesture(1, CanvasHandle.TopRight, default, new Rect(25, -25, 50, 100), 90);
        for (var width = 240; width < 255; width++)
        {
            var bounds = BoundedResize(quarter, quarterGesture, new Point(width - 100, 150), canvas);
            var visible = SourceGeometry.GetVisibleAreaAabb(quarter, bounds.X, bounds.Y, bounds.Width, bounds.Height, 90);
            Assert.True(CanvasInteractionGeometry.Fits(visible, canvas), "Quarter-turn integer parity never overflows the canvas");
            Assert.Near(200, visible.Bottom, .001, "Changing the other dimension preserves the already reached bottom edge");
            Assert.Near(width, visible.Right, 1.001, "Quarter-turn width remains within one representable model pixel of the pointer");
        }

        foreach (var cropAngle in new[] { 0, 31 })
        {
            var source = new SourceItem { CanvasX = 120, CanvasY = 70, CanvasWidth = 80, CanvasHeight = 50, Rotation = 37 };
            source.SetCrop(.1, .15, .2, .1, cropAngle);
            var start = new Rect(120, 70, 80, 50);
            var bounds = BoundedResize(source, new CanvasGesture(1, CanvasHandle.BottomRight, default, start, 37), new Point(1000, 800), canvas);
            Assert.True(CanvasInteractionGeometry.Fits(SourceGeometry.GetVisibleAreaAabb(source, bounds.X, bounds.Y,
                bounds.Width, bounds.Height, 37), canvas), "Rotated/cropped boundary resize still constrains image content");
            var before = VisibleAnchor(source, start, 37, CanvasHandle.TopLeft);
            var after = VisibleAnchor(source, bounds, 37, CanvasHandle.TopLeft);
            Assert.Near(before.X, after.X, 1, "Rounded rotated resize keeps opposite anchor within one model pixel X");
            Assert.Near(before.Y, after.Y, 1, "Rounded rotated resize keeps opposite anchor within one model pixel Y");
        }

        var first = new SourceItem { CanvasWidth = 100, CanvasHeight = 50 };
        var second = new SourceItem { CanvasX = 100, CanvasWidth = 100, CanvasHeight = 50 };
        var items = new[] { CanvasSourceState.Capture(first), CanvasSourceState.Capture(second) };
        var groupStart = new Rect(0, 0, 200, 50);
        var group = CanvasGroupTransform.ResizeWithinCanvas(items,
            new CanvasGesture(1, CanvasHandle.BottomRight, default, groupStart, 0), new Point(121, 150), false, canvas);
        var combined = Rect.Empty;
        foreach (var item in items)
        {
            var itemBounds = CanvasGroupTransform.Calculate(item, groupStart, 0, group, 0).VisibleBounds;
            if (combined.IsEmpty) combined = itemBounds; else combined.Union(itemBounds);
        }
        Assert.Equal(new Rect(0, 0, 320, 200), combined, "Group free resize reaches both canvas edges using actual rounded child bounds");
        var quarterItems = new[] { CanvasSourceState.Capture(quarter), CanvasSourceState.Capture(new SourceItem
            { CanvasX = 125, CanvasY = -25, CanvasWidth = 50, CanvasHeight = 100, Rotation = 90 }) };
        foreach (var (angle, frameBounds, handle) in new[]
        {
            (0, new Rect(0, 0, 200, 50), CanvasHandle.BottomRight),
            (90, new Rect(75, -75, 50, 200), CanvasHandle.TopRight)
        })
        {
            var resized = CanvasGroupTransform.ResizeWithinCanvas(quarterItems,
                new CanvasGesture(1, handle, default, frameBounds, angle), new Point(121, 150), false, canvas);
            var visible = Rect.Empty;
            foreach (var item in quarterItems)
            {
                var member = CanvasGroupTransform.Calculate(item, frameBounds, angle, resized, angle).VisibleBounds;
                if (visible.IsEmpty) visible = member; else visible.Union(member);
            }
            Assert.Equal(new Rect(0, 0, 320, 200), visible, "Quarter-turned group and members still reach both canvas edges");
        }
        second.CanvasX = 101;
        var translatedA = CanvasGroupTransform.Calculate(CanvasSourceState.Capture(first), groupStart, 0, new Rect(.5, .5, 200, 50), 0);
        var translatedB = CanvasGroupTransform.Calculate(CanvasSourceState.Capture(second), groupStart, 0, new Rect(.5, .5, 200, 50), 0);
        Assert.Equal(101, translatedB.X - translatedA.X, "A fractional group drag uses one common rounded translation and retains spacing");

        var frame = new Rect(0, 0, 320, 200);
        var knob = CanvasInteractionGeometry.RotationHandlePosition(frame);
        Assert.Equal(new Point(160, -22), knob, "Rotation handle stays outside the content at the top canvas edge");
        foreach (var angle in new[] { 0, 37, 90, 180, 270 })
        {
            var rotated = CanvasInteractionGeometry.RotationHandlePosition(frame, angle);
            var local = CanvasInteractionGeometry.Rotate(rotated, CanvasInteractionGeometry.Center(frame), -angle);
            Assert.Near(knob.X, local.X, .0001, "Rotated crop handle retains its frame-relative horizontal center");
            Assert.Near(knob.Y, local.Y, .0001, "Rotated crop handle is never clamped back into the content frame");
        }
    }

    private static Point VisibleAnchor(SourceItem source, Rect bounds, double rotation, CanvasHandle handle)
    {
        var local = CanvasSourceResize.VisibleLocalBounds(source, bounds.Width, bounds.Height);
        var point = CanvasInteractionGeometry.HandlePosition(local, handle);
        return CanvasInteractionGeometry.Rotate(new Point(bounds.X + point.X, bounds.Y + point.Y), CanvasInteractionGeometry.Center(bounds), rotation);
    }

    private static CanvasHandle Opposite(CanvasHandle handle) => handle switch
    {
        CanvasHandle.TopLeft => CanvasHandle.BottomRight, CanvasHandle.TopRight => CanvasHandle.BottomLeft,
        CanvasHandle.BottomLeft => CanvasHandle.TopRight, CanvasHandle.BottomRight => CanvasHandle.TopLeft,
        CanvasHandle.Top => CanvasHandle.Bottom, CanvasHandle.Bottom => CanvasHandle.Top,
        CanvasHandle.Left => CanvasHandle.Right, _ => CanvasHandle.Left
    };
}
