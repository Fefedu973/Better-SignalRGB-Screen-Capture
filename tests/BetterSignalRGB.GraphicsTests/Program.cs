using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Better_SignalRGB_Screen_Capture.Core.Helpers;
using Better_SignalRGB_Screen_Capture.Helpers;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.Services;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;
using Rect = Windows.Foundation.Rect;

namespace BetterSignalRGB.GraphicsTests;

internal static partial class Program
{
    private static int _assertions;
    private const DirectXPixelFormat Format = DirectXPixelFormat.R16G16B16A16Float;
    private static void Check(bool condition, string message)
    { _assertions++; if (!condition) throw new InvalidOperationException(message); }
    private static void Near(double expected, double actual, double tolerance, string message) =>
        Check(double.IsFinite(actual) && Math.Abs(expected - actual) <= tolerance, $"{message}: expected {expected}, got {actual}");

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Any(arg => arg is not ("--monitor" or "--region" or "--window" or "--preview")))
                throw new ArgumentException("Usage: [--monitor] [--region] [--window] [--preview]. Default: synthetic GPU only; native capture is explicitly opt-in.");
            // No profile loading, image export, or activation of an installed application.
            // Only --window creates a nonactivating synthetic window owned by this process.
            await Task.Run(SyntheticGpu);
            if (args.Contains("--monitor")) await MonitorAsync();
            if (args.Contains("--region")) await RegionAsync();
            if (args.Contains("--window")) await WindowAsync();
            if (args.Contains("--preview")) await PreviewAsync();
            Console.WriteLine($"PASS: {_assertions} graphics assertions. No image files saved; existing application windows and settings untouched.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static byte[] FloatImage(int width, int height, Func<int, (float R, float G, float B)> pixel)
    {
        var bytes = new byte[checked(width * height * 8)];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var color = pixel(x); var offset = (y * width + x) * 8;
            WriteHalf(bytes.AsSpan(offset), color.R); WriteHalf(bytes.AsSpan(offset + 2), color.G);
            WriteHalf(bytes.AsSpan(offset + 4), color.B); WriteHalf(bytes.AsSpan(offset + 6), 1);
        }
        return bytes;
    }
    private static void WriteHalf(Span<byte> destination, float value) => BinaryPrimitives.WriteUInt16LittleEndian(destination, BitConverter.HalfToUInt16Bits((Half)value));
    private static float ReadHalf(byte[] bytes, int offset) => (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset)));

    private static void SyntheticGpu()
    {
        using var device = new CanvasDevice();
        Check(device.IsPixelFormatSupported(Format), "The actual GPU supports the FP16 capture/readback format");
        var values = new (float R, float G, float B)[] { (6, 6, 6), (3, 3, 3), (12, 12, 12), (6, .75f, .375f) };
        var input = FloatImage(16, 8, x => values[x / 4]);
        using var source = CanvasBitmap.CreateFromBytes(device, input, 16, 8, Format, 96, CanvasAlphaMode.Ignore);
        using var scaled = new CanvasRenderTarget(device, 4, 2, 96, Format, CanvasAlphaMode.Ignore);
        using (var drawing = scaled.CreateDrawingSession())
        {
            drawing.Clear(Windows.UI.Color.FromArgb(255, 0, 0, 0));
            drawing.DrawImage(source, new Rect(0, 0, 4, 2), new Rect(0, 0, 16, 8), 1, CanvasImageInterpolation.Linear);
        }
        var pixels = scaled.GetPixelBytes();
        Check(pixels.Length == 4 * 2 * 8, "GPU readback preserves eight FP16 bytes per pixel after downsampling");
        for (var y = 0; y < 2; y++)
        for (var x = 0; x < 4; x++)
        {
            var offset = (y * 4 + x) * 8;
            Near(values[x].R, ReadHalf(pixels, offset), .005, "FP16 red survives GPU scaling without clipping above one");
            Near(values[x].G, ReadHalf(pixels, offset + 2), .005, "FP16 green survives GPU scaling");
            Near(values[x].B, ReadHalf(pixels, offset + 4), .005, "FP16 blue survives GPU scaling");
        }
        var hdr480 = new byte[4 * 2 * 4]; var hdr80 = new byte[hdr480.Length]; var sdr = new byte[hdr480.Length];
        HdrToneMapper.ConvertRgba16Float(pixels, 4, 2, 32, hdr480, true, 480);
        HdrToneMapper.ConvertRgba16Float(pixels, 4, 2, 32, hdr80, true, 80);
        HdrToneMapper.ConvertRgba16Float(pixels, 4, 2, 32, sdr, false, 480);
        Near(240, hdr480[0], 1, "Six linear scRGB units map from the measured 480-nit SDR white into the HDR shoulder");
        Near(188, hdr480[4], 1, "480-nit normalization preserves a half-white neutral rather than clipping it");
        Check(hdr480[8] > hdr480[0] && hdr480[0] > hdr480[4], "HDR highlights remain distinct after GPU scaling and tone mapping");
        Check(hdr80[4] > hdr480[4] && sdr[4] == 255, "Measured white normalization changes HDR exposure while SDR does not receive HDR normalization");
        Check(hdr480[14] > hdr480[13] && hdr480[13] > hdr480[12], "The BGRA output preserves the synthetic color channel order");
        Check(Enumerable.Range(0, 8).All(i => hdr480[i * 4 + 3] == 255), "Output alpha is opaque");

        using var sdrSource = CanvasBitmap.CreateFromBytes(device, FloatImage(4, 4, _ => (.5f, .5f, .5f)), 4, 4, Format, 96, CanvasAlphaMode.Ignore);
        using var sdrTarget = new CanvasRenderTarget(device, 2, 2, 96, Format, CanvasAlphaMode.Ignore);
        using (var drawing = sdrTarget.CreateDrawingSession()) drawing.DrawImage(sdrSource, new Rect(0, 0, 2, 2), new Rect(0, 0, 4, 4), 1, CanvasImageInterpolation.Linear);
        var sdrBytes = new byte[16]; var sdrBytes80 = new byte[16];
        HdrToneMapper.ConvertRgba16Float(sdrTarget.GetPixelBytes(), 2, 2, 16, sdrBytes, false, 480);
        HdrToneMapper.ConvertRgba16Float(sdrTarget.GetPixelBytes(), 2, 2, 16, sdrBytes80, false, 80);
        Near(188, sdrBytes[0], 1, "SDR linear half-gray receives exactly one sRGB encoding");
        Check(sdrBytes.SequenceEqual(sdrBytes80), "SDR pixels are invariant to HDR white metadata");
        Console.WriteLine("PASS: actual Win2D FP16 texture → FP16 downsample → FP16 readback → production HDR480/SDR conversion.");
    }

    private static async Task MonitorAsync()
    {
        var monitor = DisplayColorInfo.Enumerate().FirstOrDefault(display => display.HdrEnabled == true)
            ?? throw new InvalidOperationException("No active HDR display is available for the explicitly requested monitor test.");
        var source = new SourceItem { Type = SourceType.Monitor, Name = "HDR test monitor", MonitorDeviceId = monitor.DeviceName! };
        await CaptureAsync(source, "HDR monitor", 800, 600, 6, CaptureColorMode.HdrToneMapped, monitor.SdrWhiteNits);
    }

    private static async Task RegionAsync()
    {
        var displays = DisplayColorInfo.Enumerate();
        var hdr = displays.FirstOrDefault(display => display.HdrEnabled == true)
            ?? throw new InvalidOperationException("No active HDR display for the mixed region test.");
        var sdr = displays.FirstOrDefault(display => display.HdrEnabled == false && display.Bounds.Left == hdr.Bounds.Right &&
            display.Bounds.Bottom > hdr.Bounds.Top && display.Bounds.Top < hdr.Bounds.Bottom)
            ?? throw new InvalidOperationException("No adjacent SDR display for the requested mixed region test.");
        var top = Math.Max(hdr.Bounds.Top, sdr.Bounds.Top);
        var height = Math.Min(240, Math.Min(hdr.Bounds.Bottom, sdr.Bounds.Bottom) - top);
        var source = new SourceItem { Type = SourceType.Region, Name = "Mixed HDR/SDR test region",
            RegionBounds = new Windows.Graphics.RectInt32(hdr.Bounds.Right - 160, top, 320, height) };
        CheckPlans(source, 319, 199, false);
        await CaptureAsync(source, "Mixed HDR/SDR region", 320, 240, 6, CaptureColorMode.HdrToneMapped, null);
        if (sdr.Bounds.Top <= hdr.Bounds.Top - 80)
        {
            source.RegionBounds = new Windows.Graphics.RectInt32(hdr.Bounds.Right - 160, hdr.Bounds.Top - 80, 320, 240);
            CheckPlans(source, 319, 199, true);
            await CaptureAsync(source, "Mixed HDR/SDR region with negative-Y empty band", 320, 240, 6,
                CaptureColorMode.HdrToneMapped, null, (bytes, actualWidth, _) =>
                {
                    // This rectangle has no physical monitor behind it. Do not
                    // compare or log any pixels that belong to the user's screens.
                    for (var y = 0; y < 80; y++)
                    for (var x = 0; x < 160; x++)
                    {
                        var at = (y * actualWidth + x) * 4;
                        if (bytes[at] != 0 || bytes[at + 1] != 0 || bytes[at + 2] != 0 || bytes[at + 3] != 255)
                            return "The monitor-free negative-Y band was not opaque black";
                    }
                    return null;
                });
        }
    }

    private static void CheckPlans(SourceItem source, int width, int height, bool missingBand)
    {
        // Exercise the production planner without opening a capture session. These
        // invariants concern actual monitor geometry, not copies of its rounding formula.
        var capture = new GraphicsFrameCapture(source, width, height, 15);
        var planner = typeof(GraphicsFrameCapture).GetMethod("BuildPlans", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("Production region planner was not found");
        var plans = ((System.Collections.IEnumerable)planner.Invoke(capture, null)!).Cast<object>().ToArray();
        Check(plans.Length == 2, "Mixed region creates two independently calibrated monitor tiles");
        var rectangles = plans.Select(plan => (System.Drawing.Rectangle)plan.GetType().GetProperty("Destination")!.GetValue(plan)!).ToArray();
        Check(rectangles.All(rect => rect.Width > 0 && rect.Height > 0 && rect.Left >= 0 && rect.Top >= 0 && rect.Right <= width && rect.Bottom <= height),
            "Odd-resolution destination rectangles remain positive and inside the output");
        Check(!rectangles[0].IntersectsWith(rectangles[1]), "Rounded adjacent tiles never overlap or overwrite each other");
        var left = rectangles.OrderBy(rect => rect.Left).First(); var right = rectangles.OrderBy(rect => rect.Left).Last();
        Check(left.Left == 0 && left.Right == right.Left && right.Right == width,
            "Odd-resolution monitor seam has no one-pixel gap or missing edge");
        Check(left.Bottom == height && right.Bottom == height && right.Top == 0 && (missingBand ? left.Top > 0 : left.Top == 0),
            "Only the physically missing upper band is excluded from mixed-monitor plans");
        foreach (var plan in plans)
        {
            var handle = (nint)plan.GetType().GetProperty("Handle")!.GetValue(plan)!;
            var crop = (System.Drawing.Rectangle?)plan.GetType().GetProperty("Crop")!.GetValue(plan);
            var monitor = DisplayColorInfo.ForMonitor(handle);
            Check(crop.HasValue && crop.Value.Left >= 0 && crop.Value.Top >= 0 && crop.Value.Right <= monitor.Bounds.Width && crop.Value.Bottom <= monitor.Bounds.Height,
                "Each tile crops in its own physical monitor coordinates");
        }
    }

    private static async Task CaptureAsync(SourceItem source, string label, int width, int height, int seconds,
        CaptureColorMode expectedMode, double? expectedWhite, Func<byte[], int, int, string?>? validateFrame = null)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var timer = Stopwatch.StartNew(); var first = double.NaN; var last = 0d;
        using var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime; var allocatedBefore = GC.GetTotalAllocatedBytes(false);
        long frames = 0; string? invalid = null; CaptureColorInfo? reportedColor = null;
        var worker = Task.Run(async () =>
        {
            try
            {
                await new GraphicsCaptureFactory().Create(source, width, height, 15).RunAsync((bytes, actualWidth, actualHeight) =>
                {
                    // No frame bytes, hashes, samples, or desktop content are logged or saved.
                    if (actualWidth != width || actualHeight != height || bytes.Length < width * height * 4) invalid = "Invalid frame dimensions";
                    else for (var i = 3; i < width * height * 4; i += 4) if (bytes[i] != 255) { invalid = "Nonopaque frame"; break; }
                    invalid ??= validateFrame?.Invoke(bytes, actualWidth, actualHeight);
                    if (frames++ == 0) first = timer.Elapsed.TotalMilliseconds;
                    last = timer.Elapsed.TotalMilliseconds;
                    ArrayPool<byte>.Shared.Return(bytes);
                }, color => reportedColor = color, cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        });
        await worker.WaitAsync(TimeSpan.FromSeconds(seconds + 15));
        timer.Stop();
        Check(timer.Elapsed.TotalSeconds >= 5, label + " observed for at least five seconds");
        Check(frames > 0 && invalid == null, label + " delivered bounded, opaque BGRA frames");
        Check(reportedColor?.Mode == expectedMode, label + " reports the actual HDR conversion backend");
        Check(reportedColor?.SdrWhiteNits == expectedWhite, label + " reports the per-display or mixed white state correctly");
        process.Refresh();
        Console.WriteLine(JsonSerializer.Serialize(new { Test = label, Seconds = timer.Elapsed.TotalSeconds, Frames = frames,
            MeanFramesPerSecond = frames / timer.Elapsed.TotalSeconds, FirstFrameMilliseconds = first, LastFrameMilliseconds = last,
            CpuCoreSeconds = (process.TotalProcessorTime - cpuBefore).TotalSeconds,
            ManagedAllocatedBytes = GC.GetTotalAllocatedBytes(false) - allocatedBefore,
            Width = width, Height = height, Color = reportedColor }));
    }
}
