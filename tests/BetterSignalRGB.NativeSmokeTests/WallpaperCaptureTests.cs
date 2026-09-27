using System.Buffers;
using System.Drawing;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Better_SignalRGB_Screen_Capture.Helpers;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.Services;
using ScreenRecorderLib;

internal static class WallpaperCaptureTests
{
    public static async Task RunAsync()
    {
        Check(WallpaperEngineSourceDiscovery.IsSafeHost("WorkerW", true, true, false), "Wallpaper-only Explorer WorkerW should be accepted.");
        Check(!WallpaperEngineSourceDiscovery.IsSafeHost("Progman", true, true, false), "Progman must never be a silent fallback.");
        Check(!WallpaperEngineSourceDiscovery.IsSafeHost("WorkerW", true, true, true), "Icon-containing hosts must be excluded even if icons are hidden.");
        Check(!WallpaperEngineSourceDiscovery.IsSafeHost("WorkerW", false, true, false), "Unverified shell ownership must be rejected.");
        Check(!WallpaperEngineSourceDiscovery.IsSafeHost("WorkerW", true, false, false), "A shell host without Wallpaper Engine must not be captured.");
        var crop = WallpaperCaptureWorker.CalculateCrop(new Rectangle(-1920, -494, 4480, 2560), new Rectangle(-1920, 0, 1920, 1080), 319, 199);
        Check(crop.Source == new Rectangle(0, 494, 1920, 1080) && crop.Destination == new Rectangle(0, 0, 319, 199), "Negative desktop coordinates must become host-local pixels.");
        crop = WallpaperCaptureWorker.CalculateCrop(new Rectangle(0, 0, 100, 100), new Rectangle(-50, -50, 100, 100), 100, 100);
        Check(crop.Source == new Rectangle(0, 0, 50, 50) && crop.Destination == new Rectangle(50, 50, 50, 50), "Partly absent wallpaper areas must remain black in their correct location.");
        crop = WallpaperCaptureWorker.CalculateCrop(new Rectangle(0, 0, 10, 100), new Rectangle(0, 0, 100, 100), 1, 1);
        Check(crop.Source.Width == 10 && crop.Destination.Width == 0, "Subpixel monitor coverage may legitimately round to no output pixel and must be skipped.");
        var header = new byte[WallpaperFrameProtocol.HeaderLength];
        WallpaperFrameProtocol.WriteHeader(header, 3, 5);
        Check(WallpaperFrameProtocol.ReadHeader(header, 3, 5) == 60, "Odd frame sizing must remain exact.");
        using var stream = new MemoryStream(); stream.Write(header); stream.Write(Enumerable.Range(0, 60).Select(value => (byte)value).ToArray()); stream.Position = 0;
        var frame = await WallpaperCaptureProcess.ReadFrameAsync(stream, new byte[header.Length], 3, 5, CancellationToken.None);
        try { Check(frame.Take(60).SequenceEqual(Enumerable.Range(0, 60).Select(value => (byte)value)), "Frame payload must survive pipe transport unchanged."); }
        finally { ArrayPool<byte>.Shared.Return(frame); }
        await Reject(async () => { using var truncated = new MemoryStream(header); await WallpaperCaptureProcess.ReadFrameAsync(truncated, new byte[header.Length], 3, 5, CancellationToken.None); }, "Truncated frame must fail.");
        header[0] ^= 1;
        await Reject(() => { WallpaperFrameProtocol.ReadHeader(header, 3, 5); return Task.CompletedTask; }, "Invalid magic must fail before allocation.");
        await Reject(() => { WallpaperFrameProtocol.ValidateSize(int.MaxValue, int.MaxValue); return Task.CompletedTask; }, "Oversized dimensions must fail before multiplication/allocation.");
        WallpaperFrameProtocol.WriteHeader(header, 3, 5);
        await Reject(() => { WallpaperFrameProtocol.ReadHeader(header, 5, 3); return Task.CompletedTask; }, "Unexpected geometry must be rejected.");
        await Reject(() => { WallpaperCaptureWorker.Parse([WallpaperCaptureWorker.Argument]); return Task.CompletedTask; }, "Invalid worker invocation must fail without starting UI.");
        await VerifyBlockedWorkerCleanup(false);
        await VerifyBlockedWorkerCleanup(true);
        var noisyStart = new ProcessStartInfo(Environment.ProcessPath!)
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        noisyStart.ArgumentList.Add("--wallpaper-test-error");
        var noisyRejected = false;
        try { await WallpaperCaptureProcess.PumpCoreAsync(noisyStart, 3, 5, (_, _, _) => { }, CancellationToken.None, TimeSpan.FromSeconds(5)); }
        catch (IOException error) { noisyRejected = error.Message.Length == 1024 && error.Message.All(value => value == 'E'); }
        Check(noisyRejected, "A noisy failed helper must be drained without retaining more than 1024 error characters.");
        Console.WriteLine("PASS: wallpaper-only host policy, negative/clipped monitor geometry, bounded odd BGRA framing, truncated/malformed frames and invalid worker arguments.");
        Console.WriteLine("PASS: blocked isolated worker is terminated on frame timeout and on capture cancellation; oversized stderr is drained and bounded.");
    }

    public static async Task RunNativeAsync()
    {
        var display = Recorder.GetDisplays().First();
        var target = WallpaperEngineSourceDiscovery.TryDiscover(display.DeviceName)
            ?? throw new InvalidOperationException("No wallpaper-only WorkerW exists on the first display.");
        Console.WriteLine($"Wallpaper host discovered: {target.HostBounds.Width}x{target.HostBounds.Height}; icon subtree excluded.");
        var diagnostics = new PipelineDiagnosticsService();
        var service = new CaptureService(new DelegateWebsiteCaptureHostFactory(_ => throw new InvalidOperationException("A wallpaper capture must never create a website browser.")), diagnostics);
        var source = new SourceItem { Type = SourceType.WallpaperEngine, MonitorDeviceId = display.DeviceName, CanvasWidth = 319, CanvasHeight = 199 };
        using var current = Process.GetCurrentProcess();
        var baseline = CountProcesses(current.ProcessName);
        try
        {
            foreach (var size in new[] { (319, 199, false), (319, 199, true), (3, 5, false) })
            {
                source.CanvasWidth = size.Item1; source.CanvasHeight = size.Item2;
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var hashes = new HashSet<string>(); var frames = 0; var sampledColors = 0; Func<bool>? validateOcclusion = null;
                void Frame(object? _, Better_SignalRGB_Screen_Capture.Contracts.Services.SourceFrameEventArgs args)
                {
                    try
                    {
                        Check(validateOcclusion?.Invoke() ?? true, "Wallpaper must stay fully occluded below the existing applications on each frame.");
                        using var data = new MemoryStream(args.FrameData!); using var bitmap = new Bitmap(data);
                        Check(bitmap.Width == size.Item1 && bitmap.Height == size.Item2, "Worker JPEG dimensions changed.");
                        var colors = new HashSet<int>();
                        for (var y = 0; y < bitmap.Height; y += 11)
                        for (var x = 0; x < bitmap.Width; x += 11) colors.Add(bitmap.GetPixel(x, y).ToArgb());
                        sampledColors = Math.Max(sampledColors, colors.Count);
                        hashes.Add(Convert.ToHexString(SHA256.HashData(args.FrameData!)));
                        if (++frames == 6) completion.TrySetResult();
                    }
                    catch (Exception error) { completion.TrySetException(error); }
                }
                void Failed(object? _, Better_SignalRGB_Screen_Capture.Contracts.Services.CaptureFailedEventArgs args) => completion.TrySetException(new InvalidOperationException(args.Error));
                service.FrameAvailable += Frame; service.CaptureFailed += Failed;
                async Task Capture(Func<bool>? validate)
                {
                    validateOcclusion = validate;
                    try { await service.StartCaptureAsync(source); await completion.Task.WaitAsync(TimeSpan.FromSeconds(12)); }
                    finally { await service.StopCaptureAsync(source); }
                }
                try
                {
                    if (size.Item3) await WallpaperDesktopOcclusion.RunAsync(target.Handle, validate => Capture(validate));
                    else await Capture(null);
                }
                finally { await service.StopCaptureAsync(source); service.FrameAvailable -= Frame; service.CaptureFailed -= Failed; }
                Check(!service.IsCapturing(source) && service.GetMjpegFrame(source.Id) is null, "Stopping the worker must clear frame publication.");
                Check(CountProcesses(current.ProcessName) == baseline, "The app-owned worker must exit after stop.");
                if (size.Item3) Check(sampledColors >= 8, "A fully occluded wallpaper must still produce nontrivial picture pixels rather than the opaque cover.");
                Console.WriteLine($"PASS actual Wallpaper Engine service: {size.Item1}x{size.Item2}, covered={size.Item3}, {frames} decoded JPEGs, {hashes.Count} distinct hashes, {sampledColors} sampled colors, worker exited and frame cleared. No images saved/transmitted.");
            }
            source.MonitorDeviceId = "missing-display";
            var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.CaptureFailed += (_, _) => failure.TrySetResult();
            await service.StartCaptureAsync(source);
            await failure.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Check(!service.IsCapturing(source) && service.GetMjpegFrame(source.Id) is null && CountProcesses(current.ProcessName) == baseline,
                "A missing display must fail explicitly and leave no helper or ghost frame.");
            Console.WriteLine("PASS: missing wallpaper display reports failure and cleans the isolated helper.");
        }
        finally { await service.StopAllCapturesAsync(); }
    }
    private static int CountProcesses(string name)
    { var values = Process.GetProcessesByName(name); try { return values.Length; } finally { foreach (var value in values) value.Dispose(); } }
    private static async Task Reject(Func<Task> action, string message)
    { try { await action(); } catch (Exception error) when (error is InvalidDataException or EndOfStreamException or ArgumentException) { return; } throw new InvalidOperationException(message); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    public static async Task RunApplicationWorkerAsync(string executable)
    {
        using var current = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(Path.GetFullPath(executable))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { WallpaperCaptureWorker.Argument, "--monitor", Recorder.GetDisplays().First().DeviceName,
            "--width", "319", "--height", "199", "--fps", "30", "--parent", Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            "--parent-start", current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) }) start.ArgumentList.Add(argument);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var frames = 0; var colors = new HashSet<int>(); var workerId = 0;
        try
        {
            await WallpaperCaptureProcess.PumpCoreAsync(start, 319, 199, (pixels, width, height) =>
            {
                try
                {
                    Check(width == 319 && height == 199, "Release app worker changed the requested frame geometry.");
                    for (var i = 0; i < width * height * 4; i += 4 * 23)
                        colors.Add(pixels[i] | pixels[i + 1] << 8 | pixels[i + 2] << 16);
                    if (++frames == 3) cancellation.Cancel();
                }
                finally { ArrayPool<byte>.Shared.Return(pixels); }
            }, cancellation.Token, TimeSpan.FromSeconds(5), id => workerId = id);
        }
        catch (OperationCanceledException) when (frames == 3) { }
        Check(frames == 3 && colors.Count >= 8, "The real application entry must produce nontrivial framed BGRA pixels without creating the GUI.");
        try { using var remaining = Process.GetProcessById(workerId); Check(remaining.HasExited, "Release app worker survived capture cancellation."); }
        catch (ArgumentException) { }
        Console.WriteLine($"PASS actual application worker entry: 3 framed 319x199 BGRA frames, {colors.Count} sampled colors; owned process exited.");

        start.ArgumentList.Clear(); start.ArgumentList.Add(WallpaperCaptureWorker.Argument);
        using var invalid = Process.Start(start)!;
        var error = invalid.StandardError.ReadToEndAsync();
        try { await invalid.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { if (!invalid.HasExited) invalid.Kill(); }
        Check(invalid.ExitCode != 0 && (await error).Length is > 0 and <= 1025 && invalid.StandardOutput.ReadToEnd().Length == 0,
            "Invalid worker arguments must exit with a bounded error instead of launching the application.");
        Console.WriteLine("PASS actual application invalid worker invocation: nonzero exit, bounded stderr and empty frame stream.");
    }

    private static async Task VerifyBlockedWorkerCleanup(bool cancel)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--wallpaper-test-stall");
        using var cancellation = new CancellationTokenSource();
        var workerId = 0; var rejected = false;
        try
        {
            await WallpaperCaptureProcess.PumpCoreAsync(start, 3, 5, (_, _, _) => throw new InvalidOperationException("A stalled worker produced a frame."),
                cancellation.Token, TimeSpan.FromMilliseconds(cancel ? 5000 : 300), pid => { workerId = pid; if (cancel) cancellation.CancelAfter(200); });
        }
        catch (TimeoutException) when (!cancel) { rejected = true; }
        catch (OperationCanceledException) when (cancel) { rejected = true; }
        Check(rejected && workerId > 0, "Stalled helper must stop with the expected timeout/cancellation.");
        try { using var remaining = Process.GetProcessById(workerId); Check(remaining.HasExited, "Owned helper survived cancellation/timeout."); }
        catch (ArgumentException) { }
    }
}
