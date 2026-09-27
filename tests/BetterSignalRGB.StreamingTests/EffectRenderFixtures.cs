using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.Services;

namespace BetterSignalRGB.StreamingTests;

// Synthetic color patterns only. The browser suite consumes styles and JPEGs
// produced by the real snapshot/compositor; it never captures a user's desktop.
internal static class EffectRenderFixtures
{
    public static async Task ExportAsync(string path)
    {
        var jpeg = Pattern();
        var cases = new List<object>();
        foreach (var (name, sources) in Cases())
        {
            var capture = new CaptureProducer();
            using var compositor = new CompositeFrameService(capture);
            compositor.SetCanvasSize(320, 200);
            var snapshots = sources.Select((source, index) => StreamingSourceSnapshot.FromSource(source, index)).ToArray();
            StreamingCanvasSnapshot.Sources = snapshots;
            var completed = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            compositor.CompositeFrameAvailable += (_, bytes) => completed.TrySetResult(bytes);
            foreach (var source in sources) capture.Publish(source, jpeg);
            compositor.InvalidateLayout();
            var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cases.Add(new
            {
                name,
                sources = snapshots.Select(source => new { id = source.Id, outer = source.OuterStyle, crop = source.CropStyle,
                    jpeg = Convert.ToBase64String(jpeg),
                    geometry = new { x = source.CanvasX, y = source.CanvasY, width = source.CanvasWidth, height = source.CanvasHeight,
                        rotation = source.Rotation, mirrorX = source.IsMirroredHorizontally, mirrorY = source.IsMirroredVertically,
                        left = source.CropLeftPct, right = source.CropRightPct, top = source.CropTopPct, bottom = source.CropBottomPct,
                        cropRotation = source.CropRotation, opacity = source.Opacity } }),
                composite = Convert.ToBase64String(result)
            });
        }
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(cases));
        Console.WriteLine($"Exported {cases.Count} synthetic production compositor fixtures.");
    }

    private static IEnumerable<(string Name, SourceItem[] Sources)> Cases()
    {
        SourceItem Source() => new() { CanvasX = 62, CanvasY = 38, CanvasWidth = 150, CanvasHeight = 100,
            Rotation = 31, CropLeftPct = .07, CropRightPct = .31, CropTopPct = .22, CropBottomPct = .09, CropRotation = -37 };
        yield return ("rotation-asymmetric-rotated-crop", [Source()]);
        var horizontal = Source(); horizontal.IsMirroredHorizontally = true;
        yield return ("horizontal-mirror-fixed-mask", [horizontal]);
        var vertical = Source(); vertical.IsMirroredVertically = true; vertical.Rotation = -58; vertical.CropRotation = 24;
        yield return ("vertical-mirror-opposite-rotations", [vertical]);
        var both = Source(); both.IsMirroredHorizontally = both.IsMirroredVertically = true; both.Rotation = 147;
        yield return ("both-mirrors-oblique-crop", [both]);
        var offscreen = Source(); offscreen.CanvasX = -28; offscreen.CanvasY = -18; offscreen.Rotation = -27;
        offscreen.CropRotation = 53; offscreen.CropRightPct = .02; offscreen.CropTopPct = .03;
        yield return ("source-and-crop-clipped-to-canvas", [offscreen]);
        var back = Source(); back.CropRotation = 19; back.Rotation = -18; back.Opacity = .6;
        var front = Source(); front.CanvasX = 110; front.CanvasY = 65; front.Rotation = 69; front.CropRotation = -43;
        front.IsMirroredHorizontally = true; front.Opacity = .7;
        yield return ("layered-rotated-masks-with-opacity", [back, front]);
        foreach (var angle in new[] { 90, -90, 180, -180, 270 })
        {
            var cardinal = new SourceItem { CanvasX = 85, CanvasY = 35, CanvasWidth = 100, CanvasHeight = 100,
                CropRotation = angle, Rotation = 23, IsMirroredHorizontally = angle < 0 };
            yield return ($"cardinal-crop-{angle}", [cardinal]);
        }
    }

    private static byte[] Pattern()
    {
        using var bitmap = new Bitmap(120, 80);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(230, 35, 45));
            using var green = new SolidBrush(Color.FromArgb(30, 210, 60));
            using var blue = new SolidBrush(Color.FromArgb(35, 60, 230));
            using var yellow = new SolidBrush(Color.FromArgb(225, 205, 25));
            using var white = new SolidBrush(Color.FromArgb(230, 235, 240));
            graphics.FillRectangle(green, 60, 0, 60, 40);
            graphics.FillRectangle(blue, 0, 40, 60, 40);
            graphics.FillRectangle(yellow, 60, 40, 60, 40);
            graphics.FillRectangle(white, 7, 9, 12, 17);
        }
        using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Jpeg); return stream.ToArray();
    }
}
