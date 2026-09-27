using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.Services;
using Better_SignalRGB_Screen_Capture.Services.NativeOutput;

namespace BetterSignalRGB.StreamingTests;

internal static partial class Program
{
    private static async Task<RawCompositeFrame> NextRawAsync(CompositeFrameService compositor, Action trigger)
    {
        var result = new TaskCompletionSource<RawCompositeFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, RawCompositeFrame frame)
        {
            var lease = frame.Retain();
            if (!result.TrySetResult(lease)) lease.Dispose();
        }
        compositor.RawFrameAvailable += Handler;
        try { trigger(); return await result.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { compositor.RawFrameAvailable -= Handler; }
    }
    private static async Task CheckNativeCompositeAsync()
    {
        var capture = new CaptureProducer();
        using var compositor = new CompositeFrameService(capture);
        var source = new SourceItem { CanvasX = 20, CanvasY = 30, CanvasWidth = 64, CanvasHeight = 32, Opacity = .5 };
        StreamingCanvasSnapshot.Sources = [StreamingSourceSnapshot.FromSource(source, 0)];
        foreach (var highQuality in new[] { false, true })
        {
            compositor.SetCanvasSize(highQuality ? 800 : 320, highQuality ? 600 : 200);
            using var raw = await NextRawAsync(compositor, () => capture.Publish(source, Jpeg(Color.Black)));
            var xScale = raw.Width / 320d; var yScale = raw.Height / 200d;
            var inside = (int)(45 * yScale) * raw.Stride + (int)(40 * xScale) * 4;
            Check(raw.Pixels.Span[inside] == 0 && raw.Coverage.Span[inside] is >= 126 and <= 130,
                "Black pixels retain half-opacity coverage independently of their color");
            Check(raw.Coverage.Span[0] == 0 && raw.Pixels.Span[3] == 255 && raw.Coverage.Span[3] == 255,
                "Both raw and coverage satisfy opaque ORGBFRM1 semantics outside sources");
            Check(raw.ActiveSourceIds.Contains(source.Id), "Black source remains an active source");
            Check(raw.Width == (highQuality ? 800 : 320) && raw.Height == (highQuality ? 600 : 200), "Native dimensions follow HQ");
            Check(compositor.JpegEncodes == 0, "Native-only subscriber never encodes composite JPEG");
            using var second = await NextRawAsync(compositor, () => capture.Publish(source, Jpeg(Color.Red)));
            Check(second.Pixels.Span[inside + 2] > 100 && raw.Pixels.Span[inside + 2] == 0,
                "Retained frame survives a newer composition without torn/reused pooled pixels");
            using var sibling = second.Retain();
            second.Dispose();
            Check(sibling.Pixels.Span[inside + 2] > 100, "Disposing one lease does not invalidate another");
            var rejected = false;
            try { _ = second.Pixels; } catch (ObjectDisposedException) { rejected = true; }
            Check(rejected, "Disposed leases cannot expose pooled buffers");
        }
        // Deliberately hold snapshot dispatch while the UI invalidates geometry.
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = StreamingCanvasSnapshot.Sources;
        var calls = 0;
        compositor.NativeSnapshotFactory = async token =>
        {
            if (Interlocked.Increment(ref calls) == 1) { entered.SetResult(); await held.Task.WaitAsync(token); return new(old, "old"); }
            return new(StreamingCanvasSnapshot.Sources, "new");
        };
        var pending = NextRawAsync(compositor, compositor.InvalidateLayout);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        source.CanvasX = 120;
        StreamingCanvasSnapshot.Sources = [StreamingSourceSnapshot.FromSource(source, 0)];
        compositor.InvalidateLayout(); held.SetResult();
        using var final = await pending;
        Check((string?)final.Snapshot.Context == "new" && final.Snapshot.Sources[0].CanvasX == 120,
            "Layout changed during UI dispatch cannot publish stale geometry");
        Check(compositor.JpegEncodes == 0, "Geometry-only native updates still avoid JPEG encoding");
    }

    private static async Task BenchmarkNativeCompositeAsync(string destination)
    {
        var results = new List<object>();
        foreach (var size in new[] { (320, 200), (800, 600) })
        foreach (var encode in new[] { false, true })
        {
            var capture = new CaptureProducer();
            using var compositor = new CompositeFrameService(capture);
            compositor.SetCanvasSize(size.Item1, size.Item2);
            var a = new SourceItem { CanvasX = 10, CanvasY = 20, CanvasWidth = 150, CanvasHeight = 130, Rotation = 17, CropLeftPct = .1, CropRotation = -12 };
            var b = new SourceItem { CanvasX = 160, CanvasY = 55, CanvasWidth = 140, CanvasHeight = 130, Rotation = -11, Opacity = .7, IsMirroredHorizontally = true };
            StreamingCanvasSnapshot.Sources = [StreamingSourceSnapshot.FromSource(a, 0), StreamingSourceSnapshot.FromSource(b, 1)];
            var images = new[] { Jpeg(Color.Red, Color.Blue), Jpeg(Color.Lime, Color.Yellow) };
            capture.Publish(b, images[1]);
            var encoding = new List<double>();
            EventHandler<byte[]> jpeg = (_, _) => encoding.Add(compositor.LastEncodingMilliseconds);
            if (encode) compositor.CompositeFrameAvailable += jpeg;
            var compose = new List<double>(); var latency = new List<double>();
            using var process = Process.GetCurrentProcess();
            var run = Stopwatch.StartNew(); var cpu = process.TotalProcessorTime;
            for (var i = 0; i < 100; i++)
            {
                if (i == 10) { run.Restart(); cpu = process.TotalProcessorTime; }
                var start = Stopwatch.GetTimestamp();
                using var raw = await NextRawAsync(compositor, () => capture.Publish(a, images[i % 2]));
                if (i >= 10) { compose.Add(raw.CompositionMilliseconds); latency.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds); }
                var wait = TimeSpan.FromSeconds(1d / 15) - Stopwatch.GetElapsedTime(start);
                if (wait > TimeSpan.Zero) await Task.Delay(wait);
            }
            var elapsed = run.Elapsed.TotalSeconds; var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
            static double Quantile(List<double> items, double p) => items.Count == 0 ? 0 : items.Order().ElementAt((int)Math.Floor((items.Count - 1) * p));
            results.Add(new { width = size.Item1, height = size.Item2, targetFps = 15, samples = compose.Count,
                compositeJpeg = encode, achievedFps = 90 / elapsed, cpuMilliseconds = cpuMs, cpuOneCorePercent = cpuMs / (elapsed * 10),
                compositionP50Ms = Quantile(compose, .5), compositionP95Ms = Quantile(compose, .95),
                encodingP50Ms = Quantile(encoding.Skip(10).ToList(), .5), encodingP95Ms = Quantile(encoding.Skip(10).ToList(), .95),
                sourceEventToRawP50Ms = Quantile(latency, .5), sourceEventToRawP95Ms = Quantile(latency, .95), jpegEncodes = compositor.JpegEncodes });
            if (encode) compositor.CompositeFrameAvailable -= jpeg;
        }
        await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(new { kind = "synthetic-two-source-compositor", results,
            limits = "Includes source JPEG decode, GDI composition and raw/coverage copy; no real capture, transport reader, GPU upload or LEDs. CPU is process-wide, 100%=one core. Latency includes the existing compositor coalescing delay. Publication is benchmarked separately." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS: compositor benchmark written to {destination}");
    }
}
