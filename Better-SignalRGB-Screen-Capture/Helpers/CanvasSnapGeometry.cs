using Windows.Foundation;

namespace Better_SignalRGB_Screen_Capture.Helpers;

internal readonly record struct CanvasSnapGuide(bool Vertical, double Position, int Anchor);
internal readonly record struct CanvasSnapResult(Point Delta, IReadOnlyList<CanvasSnapGuide> Guides);

/// <summary>Screen-distance snapping against visible bounds. Resize corrections
/// act on the pointer delta, so the resize implementation keeps its own anchor.</summary>
internal static class CanvasSnapGeometry
{
    private readonly record struct Match(bool Vertical, int Anchor, double Target, double Difference, double Dx, double Dy);
    public static double Threshold(double zoom) => 6 / Math.Clamp(double.IsFinite(zoom) ? zoom : 1, .1, 10);
    public static Size Thresholds(double zoom, double scaleX, double scaleY) => new(
        Threshold(zoom) / SafeScale(scaleX), Threshold(zoom) / SafeScale(scaleY));
    private static double SafeScale(double value) => double.IsFinite(value) && value > 0 ? value : 1;
    private static double Anchor(Rect bounds, bool vertical, int index) => vertical
        ? bounds.Left + bounds.Width * index / 2
        : bounds.Top + bounds.Height * index / 2;

    public static CanvasSnapResult Move(Rect visible, IReadOnlyList<Rect> references, double threshold) =>
        Move(visible, references, new Size(threshold, threshold));

    public static CanvasSnapResult Move(Rect visible, IReadOnlyList<Rect> references, Size threshold)
    {
        var x = Find(visible, references, true, threshold.Width);
        var y = Find(visible, references, false, threshold.Height);
        var guides = new List<CanvasSnapGuide>(2);
        if (x.HasValue) guides.Add(new(true, x.Value.Target, x.Value.Anchor));
        if (y.HasValue) guides.Add(new(false, y.Value.Target, y.Value.Anchor));
        return new(new Point(x?.Difference ?? 0, y?.Difference ?? 0), guides);
    }

    public static CanvasSnapResult Resize(Point delta, Func<Point, Rect> visibleAt, IReadOnlyList<Rect> references, double threshold) =>
        Resize(delta, visibleAt, references, new Size(threshold, threshold));

    public static CanvasSnapResult Resize(Point delta, Func<Point, Rect> visibleAt, IReadOnlyList<Rect> references, Size threshold)
    {
        var bounds = visibleAt(delta);
        var afterX = visibleAt(new Point(delta.X + 1, delta.Y));
        var beforeX = visibleAt(new Point(delta.X - 1, delta.Y));
        var afterY = visibleAt(new Point(delta.X, delta.Y + 1));
        var beforeY = visibleAt(new Point(delta.X, delta.Y - 1));
        var x = Find(bounds, references, true, threshold.Width, afterX, beforeX, afterY, beforeY);
        var y = Find(bounds, references, false, threshold.Height, afterX, beforeX, afterY, beforeY);
        var correction = default(Point);
        if (x is { } xm && y is { } ym && Math.Abs(xm.Dx * ym.Dy - xm.Dy * ym.Dx) > .0001)
        {
            var determinant = xm.Dx * ym.Dy - xm.Dy * ym.Dx;
            correction = new Point((xm.Difference * ym.Dy - xm.Dy * ym.Difference) / determinant,
                (xm.Dx * ym.Difference - xm.Difference * ym.Dx) / determinant);
        }
        else
        {
            var best = x.HasValue && (!y.HasValue || Math.Abs(x.Value.Difference) / threshold.Width <= Math.Abs(y.Value.Difference) / threshold.Height) ? x : y;
            if (best is { } match)
            {
                // Minimize screen displacement, including a nonuniform HQ Viewbox.
                var sx = threshold.Width * threshold.Width; var sy = threshold.Height * threshold.Height;
                var length = match.Dx * match.Dx * sx + match.Dy * match.Dy * sy;
                correction = new Point(match.Difference * match.Dx * sx / length, match.Difference * match.Dy * sy / length);
            }
        }
        // Ill-conditioned or discontinuous geometry must never create a pointer jump.
        if (!double.IsFinite(correction.X) || !double.IsFinite(correction.Y) ||
            Math.Abs(correction.X) > threshold.Width * 4 || Math.Abs(correction.Y) > threshold.Height * 4) return new(delta, []);
        var snapped = new Point(delta.X + correction.X, delta.Y + correction.Y);
        var result = visibleAt(snapped);
        var guides = new List<CanvasSnapGuide>(2);
        foreach (var match in new[] { x, y })
            if (match is { } value && Math.Abs(Anchor(result, value.Vertical, value.Anchor) - value.Target) < .05)
                guides.Add(new(value.Vertical, value.Target, value.Anchor));
        return guides.Count == 0 ? new(delta, []) : new(snapped, guides);
    }

    public static bool Matches(CanvasSnapGuide guide, Rect actual, double tolerance) =>
        !actual.IsEmpty && Math.Abs(Anchor(actual, guide.Vertical, guide.Anchor) - guide.Position) <= tolerance;

    private static Match? Find(Rect bounds, IReadOnlyList<Rect> references, bool vertical, double threshold,
        Rect? afterX = null, Rect? beforeX = null, Rect? afterY = null, Rect? beforeY = null)
    {
        if (bounds.IsEmpty || threshold <= 0) return null;
        Match? best = null;
        for (var index = 0; index < 3; index++)
        {
            var anchor = Anchor(bounds, vertical, index);
            var dx = afterX.HasValue ? (Anchor(afterX.Value, vertical, index) - Anchor(beforeX!.Value, vertical, index)) / 2 : vertical ? 1 : 0;
            var dy = afterY.HasValue ? (Anchor(afterY.Value, vertical, index) - Anchor(beforeY!.Value, vertical, index)) / 2 : vertical ? 0 : 1;
            if (Math.Abs(dx) + Math.Abs(dy) < .001) continue;
            foreach (var reference in references)
            {
                if (reference.IsEmpty) continue;
                for (var targetIndex = 0; targetIndex < 3; targetIndex++)
                {
                    var target = Anchor(reference, vertical, targetIndex);
                    var difference = target - anchor;
                    if (Math.Abs(difference) <= threshold && (!best.HasValue || Math.Abs(difference) < Math.Abs(best.Value.Difference)))
                        best = new(vertical, index, target, difference, dx, dy);
                }
            }
        }
        return best;
    }
}
