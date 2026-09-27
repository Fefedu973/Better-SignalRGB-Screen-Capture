using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Better_SignalRGB_Screen_Capture.Helpers;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.Services;

namespace BetterSignalRGB.GraphicsTests;

internal static partial class Program
{
    private sealed record Sample(int White, int Gray, int Black, int Blue, int Green, int Red, int CornerBlue, int CornerGreen, int CornerRed);

    private static async Task WindowAsync()
    {
        var monitor = DisplayColorInfo.Enumerate().FirstOrDefault(display => display.HdrEnabled == true)
            ?? throw new InvalidOperationException("No active HDR monitor for the requested synthetic window test.");
        await using var fixture = await SyntheticWindow.CreateAsync(monitor.Bounds.Left + 64, monitor.Bounds.Top + 64);
        var source = new SourceItem { Type = SourceType.Process, Name = "Synthetic window", ProcessId = Environment.ProcessId,
            ProcessPath = Environment.ProcessPath, WindowHandle = fixture.Handle.ToInt64(), WindowTitle = fixture.Title };
        var color = DisplayColorInfo.ForWindow(fixture.Handle);
        Check(color.HdrEnabled == true && color.MonitorHandle == monitor.MonitorHandle, "Own fixture belongs to the selected HDR display");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var samples = Channel.CreateBounded<Sample>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        CaptureColorInfo? reported = null;
        var worker = Task.Run(async () =>
        {
            try
            {
                await new GraphicsCaptureFactory().Create(source, 320, 200, 30).RunAsync((pixels, width, height) =>
                {
                    // Only known synthetic pixels are read. No desktop images are copied or exported.
                    try
                    {
                        if (width != 320 || height != 200)
                        {
                            samples.Writer.TryComplete(new InvalidOperationException("Resize changed the requested output dimensions"));
                            cancellation.Cancel();
                            return;
                        }
                        int At(int x, int y, int channel = 0) => pixels[(y * width + x) * 4 + channel];
                        samples.Writer.TryWrite(new(At(80, 50), At(240, 50), At(80, 150), At(240, 150), At(240, 150, 1), At(240, 150, 2),
                            At(304, 190), At(304, 190, 1), At(304, 190, 2)));
                    }
                    finally { ArrayPool<byte>.Shared.Return(pixels); }
                }, value => reported = value, cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            finally { samples.Writer.TryComplete(); }
        });
        try
        {
            foreach (var (width, height, stage) in new[] { (320, 200, 0), (640, 360, 1), (200, 300, 2) })
            {
                if (stage > 0) await fixture.ResizeAsync(width, height, stage);
                var sample = await ReadStageAsync(samples.Reader, stage);
                Console.WriteLine(JsonSerializer.Serialize(new { Test = "Synthetic HDR window resize/calibration", InputWidth = width, InputHeight = height,
                    OutputWidth = 320, OutputHeight = 200, sample.White, sample.Gray, sample.Black, Color = reported }));
                Near(240, sample.White, 4, "GDI white on HDR is normalized using this monitor's measured SDR white");
                Near(128, sample.Gray, 4, "GDI 128 sRGB gray survives WGC and receives the proper HDR white normalization");
                Near(0, sample.Black, 1, "Synthetic black remains black after resize");
                Check(stage switch { 0 => sample.CornerBlue > 180, 1 => sample.CornerGreen > 180, _ => sample.CornerRed > 180 },
                    "Fresh content fills the resized surface to its lower-right edge without stale-size clipping");
                Check(reported?.Mode == CaptureColorMode.HdrToneMapped && reported.SdrWhiteNits == monitor.SdrWhiteNits,
                    "Window capture reports its actual HDR display and SDR white");
            }
        }
        finally { cancellation.Cancel(); await worker.WaitAsync(TimeSpan.FromSeconds(5)); }
        await LifecycleAsync(source);
    }

    private static async Task<Sample> ReadStageAsync(ChannelReader<Sample> reader, int stage)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (await reader.WaitToReadAsync(timeout.Token))
        {
            while (reader.TryRead(out var sample))
            {
                if (stage switch { 0 => sample.Blue > 50 && sample.Green < 20 && sample.Red < 20,
                    1 => sample.Green > 50 && sample.Blue < 20 && sample.Red < 20,
                    _ => sample.Red > 50 && sample.Blue < 20 && sample.Green < 20 }) return sample;
            }
        }
        throw new InvalidOperationException("The capture stopped before delivering the resized fixture");
    }

    private static async Task LifecycleAsync(SourceItem source)
    {
        for (var i = 0; i < 3; i++)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var callbacks = 0;
            var worker = Task.Run(async () =>
            {
                try
                {
                    await new GraphicsCaptureFactory().Create(source, 160, 100, 30).RunAsync((bytes, _, _) =>
                    { Interlocked.Increment(ref callbacks); ArrayPool<byte>.Shared.Return(bytes); first.TrySetResult(); }, _ => { }, cancellation.Token);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            });
            var stop = new Stopwatch();
            try { await first.Task.WaitAsync(TimeSpan.FromSeconds(7)); }
            finally
            {
                stop.Start(); cancellation.Cancel();
                await worker.WaitAsync(TimeSpan.FromSeconds(3));
            }
            var atStop = Volatile.Read(ref callbacks); await Task.Delay(100);
            Check(atStop > 0 && callbacks == atStop, "Capture cancellation joins the native worker and leaves no late callbacks");
            Check(stop.Elapsed < TimeSpan.FromSeconds(3), "Rapid start/first-frame/stop cycle releases native resources promptly");
        }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var called = false;
        try
        {
            await new GraphicsCaptureFactory().Create(source, 160, 100, 30).RunAsync((bytes, _, _) =>
            { called = true; ArrayPool<byte>.Shared.Return(bytes); }, _ => called = true, canceled.Token);
            throw new InvalidOperationException("Pre-canceled capture unexpectedly completed");
        }
        catch (OperationCanceledException) { Check(!called, "Pre-canceled capture creates no frame or color callbacks"); }
        foreach (var dimensions in new[] { (0, 100), (100, 0), (16385, 1) })
        {
            try
            {
                await new GraphicsCaptureFactory().Create(source, dimensions.Item1, dimensions.Item2, 30).RunAsync((bytes, _, _) =>
                    ArrayPool<byte>.Shared.Return(bytes), _ => { }, CancellationToken.None);
                throw new InvalidOperationException("Invalid output dimensions were accepted");
            }
            catch (ArgumentException) { Check(true, "Factory rejects invalid output dimensions before starting native capture"); }
        }
        var missing = new SourceItem { Type = SourceType.Region, RegionBounds = new Windows.Graphics.RectInt32(1000000, 1000000, 10, 10) };
        try
        {
            await new GraphicsCaptureFactory().Create(missing, 10, 10, 15).RunAsync((bytes, _, _) => ArrayPool<byte>.Shared.Return(bytes), _ => { }, CancellationToken.None);
            throw new InvalidOperationException("Missing monitor region was accepted");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("No display intersects")) { Check(true, "Unavailable region fails deterministically without leaving sessions"); }
        Console.WriteLine("PASS: three native start/first-frame/cancel cycles, pre-cancellation and invalid/missing target failures.");
    }
}
