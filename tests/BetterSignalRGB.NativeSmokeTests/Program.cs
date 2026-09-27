using System.Drawing;
using System.Runtime.InteropServices;
using Better_SignalRGB_Screen_Capture.Core.Helpers;
using Better_SignalRGB_Screen_Capture.Helpers;
using Better_SignalRGB_Screen_Capture.Models;
using ScreenRecorderLib;

// Opt-in Windows integration test: briefly captures displays in memory. Only counts
// and dimensions are printed; no captured pixels are saved, displayed or transmitted.
if (args.Length > 0 && args[0] == WallpaperCaptureWorker.Argument) return WallpaperCaptureWorker.Run(args);
if (args.Length > 0 && args[0] == "--wallpaper-test-stall") { await Task.Delay(TimeSpan.FromSeconds(30)); return 0; }
if (args.Length > 0 && args[0] == "--wallpaper-test-error") { Console.Error.Write(new string('E', 16384)); return 2; }
ValidatePixelEncoding();
DeviceIdentityTests.Run();
await WebsiteCaptureServiceTests.RunAsync();
await HighQualityCaptureTests.RunAsync();
await WallpaperCaptureTests.RunAsync();
if (args.Length == 2 && args[0] == "--capture-wallpaper-exe")
{
    await WallpaperCaptureTests.RunApplicationWorkerAsync(args[1]);
    return 0;
}
if (args.Contains("--capture-wallpaper", StringComparer.Ordinal))
{
    await WallpaperCaptureTests.RunNativeAsync();
    return 0;
}
if (args.Contains("--describe-sources", StringComparer.Ordinal))
{
    foreach (var type in new[] { typeof(VideoCaptureRecordingSource), typeof(VideoCaptureFormat) })
        Console.WriteLine(type.Name + ": " + string.Join(", ", type.GetProperties().Select(property => $"{property.Name}:{property.PropertyType.Name}")));
    Console.WriteLine($"Available webcam devices: {Recorder.GetSystemVideoCaptureDevices().Count()}");
    return 0;
}
var captureDisplay = args.Contains("--capture-display", StringComparer.Ordinal);
var captureWindow = args.Contains("--capture-window", StringComparer.Ordinal);
var captureWebcam = args.Contains("--capture-webcam", StringComparer.Ordinal);
var captureRegion = args.Contains("--capture-region", StringComparer.Ordinal);
if (!captureDisplay && !captureWindow && !captureWebcam && !captureRegion)
{
    Console.WriteLine("Pass --capture-display, --capture-window, --capture-webcam or --capture-region for opt-in native capture tests.");
    return 0;
}
try
{
    if (captureWindow) await ValidateWindowAsync();
    if (captureWebcam) await ValidateWebcamsAsync();
    if (!captureDisplay && !captureRegion) return 0;
    var displays = Recorder.GetDisplays().ToList();
    var display = displays.FirstOrDefault() ?? throw new InvalidOperationException("No display is available.");
    List<RecordingSourceBase> MonitorSources() => CaptureSourceFactory.Create(new SourceItem
        { Type = SourceType.Monitor, MonitorDeviceId = display.DeviceName, Name = "Synthetic display selection test" });
    if (captureDisplay) foreach (var (width, height) in new[] { (320, 200), (100, 80), (2, 2), (1, 1), (3, 5), (319, 199),
        (1080, 1920), (7680, 120), (7680, 4320), (1307, 1586), (1439, 1439), (320, 200) })
        await CaptureWithFallbackAsync(MonitorSources, width, height, false);

    var bounds = CoordinateMapper.FindIntersectingDisplays(new Rectangle(-30000, -30000, 60000, 60000), displays);
    var first = bounds[0].monitorBounds;
    var smallRegion = new Rectangle(first.X + 10, first.Y + 10, 3, 5);
    List<RecordingSourceBase> RegionSources(Rectangle region) => CaptureSourceFactory.Create(new SourceItem
    { Type = SourceType.Region, RegionBounds = new Windows.Graphics.RectInt32(region.X, region.Y, region.Width, region.Height) });
    var preview = await RegionPreviewCapture.CaptureAsync(new Rectangle(first.X + 10, first.Y + 10, 319, 199), CancellationToken.None);
    using (var stream = new MemoryStream(preview))
    using (var image = Image.FromStream(stream))
        // Screenshot previews are indicative; SRL rounds their native dimensions down
        // to even values. Production streamed JPEGs retain exact logical dimensions.
        if (image.Width is < 318 or > 319 || image.Height is < 198 or > 199)
            throw new InvalidOperationException($"Region preview PNG dimensions exceed native rounding tolerance: {image.Width}x{image.Height}.");
    Console.WriteLine("PASS: bounded region preview, native screenshot completion and in-memory PNG decoding.");
    using (var cancellation = new CancellationTokenSource())
    {
        cancellation.Cancel();
        try { await RegionPreviewCapture.CaptureAsync(smallRegion, cancellation.Token); throw new InvalidOperationException("Cancelled preview should not capture."); }
        catch (OperationCanceledException) { }
    }
    await CaptureWithFallbackAsync(() => RegionSources(smallRegion), 3, 5, true);
    var clippedRegion = new Rectangle(first.X - 10, first.Y + 10, 40, 30);
    await CaptureWithFallbackAsync(() => RegionSources(clippedRegion), 40, 30, true);
    if (bounds.Count >= 2)
    {
        var crossMonitorRegion = Rectangle.Union(bounds[0].monitorBounds, bounds[1].monitorBounds);
        await CaptureWithFallbackAsync(() => RegionSources(crossMonitorRegion), 320, 200, true);
        Console.WriteLine("PASS: region crossing two connected displays.");
    }
    else Console.WriteLine("SKIP: cross-display capture requires two connected displays (geometry covered in regression tests).");
    Console.WriteLine("PASS: native options, hardware/software recovery, JPEG sizing, regions, stop and restart.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
    return 1;
}

static async Task CaptureWithFallbackAsync(Func<List<RecordingSourceBase>> sources, int width, int height, bool region,
    bool webcam = false, Func<Bitmap, bool>? acceptPixels = null)
{
    try { await CaptureAsync(sources(), width, height, region, true, webcam, acceptPixels); }
    catch (InvalidOperationException ex) when (CaptureRecorderOptions.IsEncoderFailure(ex.Message))
    {
        Console.WriteLine($"Hardware encoder rejected {width}x{height}; testing one software retry.");
        await CaptureAsync(sources(), width, height, region, false, webcam, acceptPixels);
    }
}

static async Task CaptureAsync(List<RecordingSourceBase> sources, int requestedWidth, int requestedHeight, bool region, bool hardware,
    bool webcam, Func<Bitmap, bool>? acceptPixels)
{
    // Exactly the production factory and fallback decision; do not copy recorder settings here.
    var options = CaptureRecorderOptions.Create(sources, requestedWidth, requestedHeight, 15,
        isRegion: region, isWebcam: webcam, useHardwareEncoding: hardware);
    var (width, height) = CaptureGeometry.GetOutputSize(requestedWidth, requestedHeight);
    using var sink = new CaptureDiscardStream();
    using var recorder = Recorder.CreateRecorder(options);
    var received = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var count = 0;
    string? recordingError = null;
    var copying = 0;
    recorder.OnRecordingFailed += (_, e) =>
    {
        recordingError = e.Error;
        received.TrySetException(new InvalidOperationException(e.Error));
        ended.TrySetResult(false);
    };
    recorder.OnRecordingComplete += (_, _) => ended.TrySetResult(true);
    recorder.OnFrameRecorded += (_, e) =>
    {
        if (received.Task.IsCompleted || Interlocked.Exchange(ref copying, 1) != 0) return;
        try
        {
            var data = e.BitmapData;
            if (data is null) return;
            var (nativeWidth, nativeHeight) = CaptureGeometry.GetCarrierSize(requestedWidth, requestedHeight);
            if (data.Width != nativeWidth || data.Height != nativeHeight || data.Stride < nativeWidth * 4)
                throw new InvalidOperationException($"Preview dimensions/stride mismatch: expected {nativeWidth}x{nativeHeight}, got {data.Width}x{data.Height}, stride {data.Stride}.");
            var pixels = new byte[nativeWidth * nativeHeight * 4];
            CaptureFrameEncoder.CopyBgraRows(data.Data, nativeWidth, nativeHeight, data.Stride, pixels);
            using var output = new MemoryStream();
            using var parameters = CaptureFrameEncoder.CreateParameters();
            var jpeg = CaptureFrameEncoder.EncodeJpeg(pixels, nativeWidth, nativeHeight, width, height, output, parameters);
            using var encoded = new MemoryStream(jpeg);
            using var decoded = Image.FromStream(encoded);
            if (decoded.Width != width || decoded.Height != height)
                throw new InvalidOperationException("JPEG decode dimensions mismatch.");
            if (acceptPixels is not null)
            {
                using var bitmap = new Bitmap(decoded);
                if (!acceptPixels(bitmap)) return;
            }
            if (Interlocked.Increment(ref count) >= 3) received.TrySetResult(count);
        }
        catch (Exception ex) { received.TrySetException(ex); }
        finally { Volatile.Write(ref copying, 0); }
    };
    var started = false;
    try
    {
        recorder.Record(sink);
        started = true;
        await received.Task.WaitAsync(TimeSpan.FromSeconds(20));
        // Some hardware MFTs fail after delivering their first preview frames. Observe the
        // encoder and its finalization as well, rather than declaring success prematurely.
        await Task.Delay(1000);
    }
    finally
    {
        if (recorder.Status is RecorderStatus.Recording or RecorderStatus.Paused) recorder.Stop();
        if (started) await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    if (recordingError is not null) throw new InvalidOperationException(recordingError);
    var kind = region ? "region" : webcam ? "webcam" : sources[0] is WindowRecordingSource ? "window" : "display";
    Console.WriteLine($"PASS: {kind} {requestedWidth}x{requestedHeight} -> JPEG {width}x{height}: {count} frames, {(hardware ? "hardware" : "software")}, stopped, {sink.Length} carrier bytes discarded.");
}

static async Task ValidateWindowAsync()
{
    await using var window = await SyntheticWindow.CreateAsync(0x000000ff);
    var source = new SourceItem { Type = SourceType.Process, ProcessId = Environment.ProcessId,
        ProcessPath = Environment.ProcessPath, Name = "Synthetic process window", WindowHandle = window.Handle.ToInt64(), WindowTitle = window.Title };
    bool Red(Bitmap bitmap)
    {
        var pixel = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
        return pixel.R > 180 && pixel.G < 40 && pixel.B < 40;
    }
    await CaptureWithFallbackAsync(() => CaptureSourceFactory.Create(source), 319, 199, false, acceptPixels: Red);
    // Cover the red window completely with a separate blue window. Window capture
    // must still contain its red pixels, rather than pixels from the desktop region.
    await using (var cover = await SyntheticWindow.CreateAsync(0x00ff0000))
        await CaptureWithFallbackAsync(() => CaptureSourceFactory.Create(source), 319, 199, false, acceptPixels: Red);
    await CaptureWithFallbackAsync(() => CaptureSourceFactory.Create(source), 100, 80, false, acceptPixels: Red);
    source.ProcessId = int.MaxValue;
    await CaptureWithFallbackAsync(() => CaptureSourceFactory.Create(source), 320, 200, false, acceptPixels: Red);
    source.ProcessId = Environment.ProcessId;
    source.ProcessPath = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "different-test-path", Path.GetFileName(Environment.ProcessPath)!);
    var rejected = false;
    try { CaptureSourceFactory.Create(source); } catch (InvalidOperationException) { rejected = true; }
    if (!rejected) throw new InvalidOperationException("Window selection accepted a reused PID with a different executable path.");
    Console.WriteLine("PASS: real HWND capture including complete occlusion, decoded red pixels, exact executable path, process recovery and stop/restart.");
}

static async Task ValidateWebcamsAsync()
{
    var devices = Recorder.GetSystemVideoCaptureDevices().ToArray();
    Console.WriteLine($"Available webcam devices: {devices.Length}");
    if (devices.Length == 0) { Console.WriteLine("SKIP: no webcam device is available."); return; }
    var passed = 0;
    for (var index = 0; index < devices.Length; index++)
    {
        var source = new SourceItem { Type = SourceType.Webcam, WebcamDeviceId = devices[index].DeviceName, Name = $"Webcam test #{index + 1}" };
        try
        {
            await CaptureWithFallbackAsync(() => CaptureSourceFactory.Create(source), 319, 199, false, webcam: true);
            var formats = Recorder.GetSupportedVideoCaptureFormatsForDevice(source.WebcamDeviceId).ToArray();
            Console.WriteLine($"Webcam #{index + 1} exposes {formats.Length} capture formats.");
            if (formats.Length > 0)
            {
                source.WebcamFormatId = CaptureSourceFactory.GetWebcamFormatId(formats[0]);
                await CaptureWithFallbackAsync(() => CaptureSourceFactory.Create(source), 319, 199, false, webcam: true);
                source.WebcamFormatId = "removed-format";
                var rejected = false;
                try { CaptureSourceFactory.Create(source); } catch (InvalidOperationException) { rejected = true; }
                if (!rejected) throw new InvalidOperationException("Missing webcam format silently selected another mode.");
            }
            passed++;
        }
        catch (Exception error) { Console.WriteLine($"UNAVAILABLE: webcam #{index + 1}: {error.GetType().Name}: {error.Message}"); }
    }
    Console.WriteLine($"Webcam device results: {passed}/{devices.Length} produced decodable frames.");
    if (passed == 0) throw new InvalidOperationException("Enumerated webcam devices did not produce a capture frame.");
}

static void ValidatePixelEncoding()
{
    const int width = 66;
    const int height = 48;
    const int stride = 512; // Deliberately larger than width * 4, as with GPU staging rows.
    var padded = Enumerable.Repeat((byte)0xee, stride * height).ToArray();
    for (var y = 0; y < height; y++)
    for (var x = 0; x < width; x++)
    {
        var offset = y * stride + x * 4;
        padded[offset] = (byte)(x < width / 2 ? 0 : 255); // BGRA, blue at the right.
        padded[offset + 1] = 0;
        padded[offset + 2] = (byte)(x < width / 2 ? 255 : 0); // Red at the left.
        padded[offset + 3] = 0; // Preview alpha must not make opaque captured pixels disappear.
    }
    var packed = new byte[width * height * 4];
    var pinned = GCHandle.Alloc(padded, GCHandleType.Pinned);
    try { CaptureFrameEncoder.CopyBgraRows(pinned.AddrOfPinnedObject(), width, height, stride, packed); }
    finally { pinned.Free(); }
    for (var y = 0; y < height; y++)
    for (var x = 0; x < width * 4; x++)
        if (packed[y * width * 4 + x] != padded[y * stride + x])
            throw new InvalidOperationException("GPU row padding contaminated the owned pixel buffer.");

    using var output = new MemoryStream();
    using var parameters = CaptureFrameEncoder.CreateParameters();
    var jpeg = CaptureFrameEncoder.EncodeJpeg(packed, width, height, 32, 20, output, parameters);
    using var stream = new MemoryStream(jpeg);
    using var image = new Bitmap(stream);
    if (image.Width != 32 || image.Height != 20) throw new InvalidOperationException("Resized JPEG dimensions mismatch.");
    var left = image.GetPixel(3, 10);
    var right = image.GetPixel(28, 10);
    if (left.R < 200 || left.B > 40 || right.B < 200 || right.R > 40)
        throw new InvalidOperationException("BGRA channel order, alpha handling or resize orientation changed.");
    var resized = CaptureFrameEncoder.ResizeJpeg(jpeg, 16, 10);
    using var resizedStream = new MemoryStream(resized);
    using var resizedImage = new Bitmap(resizedStream);
    if (resizedImage.Width != 16 || resizedImage.Height != 10 || resizedImage.GetPixel(2, 5).R < 180)
        throw new InvalidOperationException("Website JPEG resizing changed dimensions or channel order.");
    Console.WriteLine("PASS: padded BGRA row copy, zero alpha, JPEG resize, red/blue orientation and website JPEG downsampling.");
}
