using System.Drawing;
using System.Drawing.Imaging;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.Services;

namespace BetterSignalRGB.StreamingTests;

internal static partial class Program
{
    private static async Task CheckStreamResolutionAsync(Stream stream, int width, int height)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var bytes = new MemoryStream(await ReadPartAsync(stream));
            using var frame = Image.FromStream(bytes);
            if (frame.Width != width || frame.Height != height) continue;
            Check(true, $"Live HTTP/HTTPS composite switches to {width} by {height} without reconnecting");
            return;
        }
        Check(false, $"Live composite did not switch to {width} by {height}");
    }

    private static async Task CheckHighQualityCompositeAsync(CaptureProducer capture, CompositeFrameService compositor)
    {
        compositor.SetCanvasSize(800, 600);
        var source = new SourceItem { CanvasX = 32, CanvasY = 20, CanvasWidth = 128, CanvasHeight = 80 };
        using (var image = await CompositeAsync(capture, compositor, (source, Jpeg(Color.Red, Color.Blue))))
        {
            Check(image.Width == 800 && image.Height == 600, "HQ composite encodes 800 by 600 actual pixels");
            Pixel(image, 100, 100, Color.Red, "HQ retains normalized source position");
            Pixel(image, 350, 100, Color.Blue, "HQ scales source placement horizontally");
            Pixel(image, 100, 320, Color.Black, "HQ preserves normalized bottom edge");
        }
        source.Rotation = 90;
        source.CropLeftPct = .25;
        source.CropRightPct = .25;
        source.CropRotation = 90;
        using (var image = await CompositeAsync(capture, compositor, (source, Jpeg(Color.White))))
        {
            Pixel(image, 240, 180, Color.White, "HQ combined rotations retain the visible center");
            Pixel(image, 120, 180, Color.Black, "HQ scales the final rotated crop mask");
        }
        // Supply detail that cannot exist in a 320-pixel frame. This would fail
        // if HQ merely upscaled a previously composed low-resolution JPEG.
        using var detail = new Bitmap(960, 600);
        using (var paint = Graphics.FromImage(detail))
        {
            paint.Clear(Color.Black);
            for (var x = 0; x < detail.Width; x += 6) paint.FillRectangle(Brushes.White, x, 0, 3, detail.Height);
        }
        using var bytes = new MemoryStream(); detail.Save(bytes, ImageFormat.Png);
        var full = new SourceItem { CanvasWidth = 320, CanvasHeight = 200 };
        using (var image = await CompositeAsync(capture, compositor, (full, bytes.ToArray())))
        {
            var transitions = 0; var previous = image.GetPixel(0, 300).R > 127;
            for (var x = 1; x < image.Width; x++)
            {
                var white = image.GetPixel(x, 300).R > 127;
                if (white != previous) transitions++;
                previous = white;
            }
            Check(transitions > 300, "HQ composite preserves spatial detail beyond a 320-pixel intermediate surface");
        }
        compositor.SetCanvasSize(320, 200);
        Check(compositor.GetLatestCompositeFrame() == null, "Changing output size invalidates the old cached frame");
        using var standard = await CompositeAsync(capture, compositor, (full, Jpeg(Color.White)));
        Check(standard.Width == 320 && standard.Height == 200, "Disabling HQ restores the standard actual JPEG dimensions");
    }
}
