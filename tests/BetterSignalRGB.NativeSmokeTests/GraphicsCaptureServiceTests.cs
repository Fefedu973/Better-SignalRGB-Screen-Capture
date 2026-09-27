using System.Buffers;
using System.Collections.Concurrent;
using System.Drawing;
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Helpers;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.Services;

// Links the real CaptureService/JPEG encoder. Only the native graphics producer is
// replaced; every frame is synthetic and no display, window or recorder is opened.
internal static class GraphicsCaptureServiceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static int _checks;

    public static async Task RunAsync()
    {
        foreach (var (type, width, height) in new[]
        {
            (SourceType.Monitor, 1, 1), (SourceType.Process, 3, 5), (SourceType.Region, 319, 199)
        }) await CheckRouteAsync(type, width, height);
        await CheckHighQualityAsync();
        await CheckStopRestartAsync();
        await CheckFrameRateAsync();
        await CheckFailuresAsync();
        await CheckLatestFrameAsync();
        await CheckRegionPreviewAsync();
        Console.WriteLine($"PASS: {_checks} graphics capture service assertions: native factory routing, odd JPEG sizes, HQ/Signal separation, pooled frame ownership, cancellation, stale callbacks, failures, FPS restart and bounded latest-frame delivery.");
    }

    private static SourceItem Source(SourceType type, int width = 31, int height = 19) => new()
    {
        Type = type, Name = "Synthetic graphics source", CanvasWidth = width, CanvasHeight = height,
        // These deliberately unavailable identities would fail native SRL source discovery.
        MonitorDeviceId = "SYNTHETIC-NONEXISTENT-DISPLAY", ProcessId = int.MaxValue,
        WindowHandle = long.MaxValue
    };

    private static CaptureService Service(FakeFactory factory, PipelineDiagnosticsService diagnostics) =>
        new(new DelegateWebsiteCaptureHostFactory(_ => throw new InvalidOperationException("Graphics capture must not create a website host.")), diagnostics, factory);

    private static async Task CheckRouteAsync(SourceType type, int width, int height)
    {
        var factory = new FakeFactory(); var diagnostics = new PipelineDiagnosticsService();
        var service = Service(factory, diagnostics); var source = Source(type, width, height);
        var frame = new TaskCompletionSource<SourceFrameEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.FrameAvailable += (_, args) => frame.TrySetResult(args);
        try
        {
            await service.StartCaptureAsync(source);
            var capture = factory.Captures.Single(); await capture.Started.Task.WaitAsync(Timeout);
            Check(ReferenceEquals(capture.Source, source) && capture.Width == width && capture.Height == height && capture.FrameRate == 15,
                $"{type}: direct graphics producer receives logical dimensions, without the H.264 carrier minimum/even rounding.");
            await service.StartCaptureAsync(source);
            Check(factory.Captures.Count == 1, $"{type}: duplicate start does not allocate a second producer.");
            capture.ReportColor(new(CaptureColorMode.HdrToneMapped, "Synthetic WGC FP16", 480));
            capture.Emit(Color.FromArgb(220, 30, 10));
            var delivered = await frame.Task.WaitAsync(Timeout);
            Check(ReferenceEquals(delivered.Source, source) && ReferenceEquals(delivered.FrameData, service.GetMjpegFrame(source.Id)),
                $"{type}: public subscribers and frame cache share the immutable JPEG.");
            Check(ReferenceEquals(delivered.FrameData, service.GetLatestSignalRgbFrame(source.Id)),
                $"{type}: normal quality reuses the same JPEG for SignalRGB.");
            CheckImage(delivered.FrameData!, width, height, image => image.GetPixel(0, 0).R > 200 && image.GetPixel(0, 0).B < 35,
                $"{type}: real JPEG preserves size, channel order and source pixels.");
            var snapshot = diagnostics.GetSnapshot().Sources.Single();
            Check(snapshot.Encoder == CaptureEncoderKind.WindowsGraphicsCapture && snapshot.ProducedFrames == 1 && snapshot.ReceivedFrames == 1,
                $"{type}: diagnostics identify direct WGC instead of a hardware/software video encoder.");
            Check(snapshot.ColorInfo.Mode == CaptureColorMode.HdrToneMapped && snapshot.ColorInfo.SdrWhiteNits == 480,
                $"{type}: verified producer color metadata reaches diagnostics.");
        }
        finally { await service.StopAllCapturesAsync().WaitAsync(Timeout); }
        Check(!service.IsCapturing(source) && service.GetMjpegFrame(source.Id) is null && service.GetLatestSignalRgbFrame(source.Id) is null,
            $"{type}: stop clears both public frame variants.");
        Check(factory.Captures.Single().Cancelled.Task.IsCompleted && factory.Captures.Single().Ended.Task.IsCompleted,
            $"{type}: stop awaits the cancelled native producer.");
    }

    private static async Task CheckHighQualityAsync()
    {
        var factory = new FakeFactory(); var service = Service(factory, new PipelineDiagnosticsService());
        var source = Source(SourceType.Monitor, 320, 200);
        try
        {
            await service.SetHighQuality(true); await service.StartCaptureAsync(source);
            var capture = factory.Captures.Single(); await capture.Started.Task.WaitAsync(Timeout);
            Check(capture.Width == 960 && capture.Height == 600, "HQ requests genuine 3x graphics source pixels, not a post-JPEG enlargement.");
            capture.Emit(Color.White, stripes: true);
            await Until(() => service.GetMjpegFrame(source.Id) is not null);
            var preview = service.GetMjpegFrame(source.Id)!; var signal = service.GetLatestSignalRgbFrame(source.Id)!;
            Check(!ReferenceEquals(preview, signal), "HQ encodes a distinct canonical SignalRGB derivative.");
            CheckImage(preview, 960, 600, image => image.GetPixel(100, 100).R > 220 && image.GetPixel(101, 100).R < 35,
                "Detailed graphics JPEG preserves genuine one-pixel lines.");
            CheckImage(signal, 320, 200, _ => true, "SignalRGB stays at canonical source size during HQ capture.");
            Check(ReferenceEquals(signal, service.GetLatestSignalRgbFrame(source.Id)), "Repeated SignalRGB reads do not re-encode the derivative.");
        }
        finally { await service.StopAllCapturesAsync().WaitAsync(Timeout); }
    }

    private static async Task CheckStopRestartAsync()
    {
        var factory = new FakeFactory(); var diagnostics = new PipelineDiagnosticsService();
        var service = Service(factory, diagnostics); var source = Source(SourceType.Process);
        var frames = 0; var failures = 0;
        service.FrameAvailable += (_, _) => Interlocked.Increment(ref frames);
        service.CaptureFailed += (_, _) => Interlocked.Increment(ref failures);
        FakeCapture? old = null;
        try
        {
            await service.StartCaptureAsync(source); old = factory.Captures.Single(); await old.Started.Task.WaitAsync(Timeout);
            old.Emit(Color.Red); await Until(() => Volatile.Read(ref frames) == 1);
            old.HoldCancellation = new(TaskCreationOptions.RunContinuationsAsynchronously);
            old.StopError = new InvalidOperationException("Synthetic native resource disposed during intentional stop.");
            var stop = service.StopCaptureAsync(source); await old.Cancelled.Task.WaitAsync(Timeout);
            Check(!stop.IsCompleted && !service.IsCapturing(source), "Stop marks the session inactive before waiting for producer shutdown.");
            Check(service.GetMjpegFrame(source.Id) is null && service.GetLatestSignalRgbFrame(source.Id) is null,
                "Stop clears both caches even while native cancellation is finishing.");
            old.Emit(Color.Blue); old.HoldCancellation.SetResult(); await stop.WaitAsync(Timeout);
            Check(Volatile.Read(ref frames) == 1 && Volatile.Read(ref failures) == 0 && diagnostics.GetSnapshot().Sources.Single().Errors == 0,
                "Frames and backend exceptions arriving during intentional stop are ignored.");
            await service.StartCaptureAsync(source); var current = factory.Captures.Last(); await current.Started.Task.WaitAsync(Timeout);
            Check(factory.Captures.Count == 2 && !ReferenceEquals(old, current), "Restart owns a fresh graphics producer.");
            current.ReportColor(new(CaptureColorMode.Sdr, "Synthetic new generation"));
            current.Emit(Color.Lime); await Until(() => Volatile.Read(ref frames) == 2);
            var valid = service.GetMjpegFrame(source.Id);
            old.Emit(Color.Blue); old.ReportColor(new(CaptureColorMode.HdrToneMapped, "Stale producer", 480));
            Check(ReferenceEquals(service.GetMjpegFrame(source.Id), valid) && Volatile.Read(ref frames) == 2,
                "An old producer cannot publish into a restarted source with the same ID.");
            Check(diagnostics.GetSnapshot().Sources.Single().ColorInfo.Mode == CaptureColorMode.Sdr,
                "Stale native metadata cannot overwrite the new generation's color state.");
        }
        finally
        {
            old?.HoldCancellation?.TrySetResult();
            await service.StopAllCapturesAsync().WaitAsync(Timeout);
        }
    }

    private static async Task CheckFrameRateAsync()
    {
        var factory = new FakeFactory(); var diagnostics = new PipelineDiagnosticsService();
        var service = Service(factory, diagnostics); var source = Source(SourceType.Region);
        try
        {
            await service.StartCaptureAsync(source); var original = factory.Captures.Single(); await original.Started.Task.WaitAsync(Timeout);
            await service.SetFrameRate(30); var current = factory.Captures.Last(); await current.Started.Task.WaitAsync(Timeout);
            Check(factory.Captures.Count == 2 && original.Cancelled.Task.IsCompleted && original.Ended.Task.IsCompleted,
                "Changing FPS awaits the prior graphics producer before creating its replacement.");
            Check(current.FrameRate == 30 && current.Width == 31 && current.Height == 19 && service.IsCapturing(source),
                "FPS restart preserves geometry and applies the requested native rate.");
            Check(diagnostics.GetSnapshot().Sources.Single().RequestedFrameRate == 30, "FPS diagnostics follow the replacement producer.");
            current.Emit(Color.Blue); await Until(() => service.GetMjpegFrame(source.Id) is not null);
            CheckImage(service.GetMjpegFrame(source.Id)!, 31, 19, image => image.GetPixel(0, 0).B > 220, "FPS restart still feeds the real JPEG pipeline.");
            await service.SetFrameRate(30);
            Check(factory.Captures.Count == 2, "An unchanged FPS does not restart capture.");
            await service.SetFrameRate(1000); await factory.Captures.Last().Started.Task.WaitAsync(Timeout);
            Check(factory.Captures.Last().FrameRate == 60, "Graphics producers receive the existing upper FPS bound.");
        }
        finally { await service.StopAllCapturesAsync().WaitAsync(Timeout); }
    }

    private static async Task CheckFailuresAsync()
    {
        foreach (var completesNormally in new[] { false, true })
        {
            var factory = new FakeFactory(); var diagnostics = new PipelineDiagnosticsService();
            var service = Service(factory, diagnostics); var source = Source(SourceType.Monitor);
            var failure = new TaskCompletionSource<CaptureFailedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var failureCount = 0;
            service.CaptureFailed += (_, args) => { Interlocked.Increment(ref failureCount); failure.TrySetResult(args); };
            try
            {
                await service.StartCaptureAsync(source); var capture = factory.Captures.Single(); await capture.Started.Task.WaitAsync(Timeout);
                capture.Emit(Color.Red); await Until(() => service.GetMjpegFrame(source.Id) is not null);
                if (completesNormally) capture.Complete(); else capture.Fail(new InvalidOperationException("Synthetic encoder fault; not an H.264 session."));
                var error = await failure.Task.WaitAsync(Timeout);
                Check(ReferenceEquals(error.Source, source) && error.Error.Contains(completesNormally ? "unexpectedly" : "Synthetic encoder fault", StringComparison.Ordinal),
                    "A stopped/failed graphics backend reports a useful terminal source error.");
                Check(factory.Captures.Count == 1 && Volatile.Read(ref failureCount) == 1,
                    "Graphics failure is reported once, without the SRL hardware-encoder fallback/retry.");
                Check(!service.IsCapturing(source) && service.GetMjpegFrame(source.Id) is null && service.GetLatestSignalRgbFrame(source.Id) is null,
                    "Terminal graphics failure clears every cached JPEG before notifying clients.");
                Check(diagnostics.GetSnapshot().Sources.Single() is { State: CaptureDiagnosticState.Failed, Errors: 1 },
                    "Terminal graphics failure remains visible in diagnostics after resource cleanup.");
            }
            finally { await service.StopAllCapturesAsync().WaitAsync(Timeout); }
        }
        var badFactory = new FakeFactory { CreateError = new InvalidOperationException("Synthetic native initialization failure.") };
        var badDiagnostics = new PipelineDiagnosticsService(); var badService = Service(badFactory, badDiagnostics);
        var badSource = Source(SourceType.Process); var rejected = false;
        try { await badService.StartCaptureAsync(badSource); } catch (InvalidOperationException error) { rejected = error.Message.Contains("Synthetic native initialization", StringComparison.Ordinal); }
        finally { await badService.StopAllCapturesAsync().WaitAsync(Timeout); }
        Check(rejected && !badService.IsCapturing(badSource) && badService.GetMjpegFrame(badSource.Id) is null,
            "Factory initialization failures roll back the source instead of leaving a capturing placeholder.");
        Check(badDiagnostics.GetSnapshot().Sources.Single().State == CaptureDiagnosticState.Failed,
            "Initialization failure remains visible in source diagnostics.");
    }

    private static async Task CheckLatestFrameAsync()
    {
        var factory = new FakeFactory(); var diagnostics = new PipelineDiagnosticsService();
        var service = Service(factory, diagnostics); var source = Source(SourceType.Monitor);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var delivered = 0;
        service.FrameAvailable += (_, _) =>
        {
            if (Interlocked.Increment(ref delivered) == 1) { entered.TrySetResult(); release.Wait(Timeout); }
        };
        try
        {
            await service.StartCaptureAsync(source); var capture = factory.Captures.Single(); await capture.Started.Task.WaitAsync(Timeout);
            capture.Emit(Color.Red); await entered.Task.WaitAsync(Timeout);
            capture.Emit(Color.Lime); capture.Emit(Color.Blue);
            Check(diagnostics.GetSnapshot().Sources.Single().DroppedFrames == 1,
                "A busy JPEG consumer keeps only the newest pending owned buffer and counts its replacement.");
            release.Set(); await Until(() => Volatile.Read(ref delivered) == 2);
            CheckImage(service.GetMjpegFrame(source.Id)!, 31, 19, image => image.GetPixel(0, 0).B > 220,
                "The final JPEG comes from the latest queued frame, without a stale-frame backlog.");
            Check(diagnostics.GetSnapshot().Sources.Single() is { ReceivedFrames: 3, ProducedFrames: 2 },
                "Dropped native buffers are not counted as produced JPEGs.");
        }
        finally { release.Set(); await service.StopAllCapturesAsync().WaitAsync(Timeout); }
    }

    private static async Task CheckRegionPreviewAsync()
    {
        foreach (var (region, width, height) in new[]
        {
            (new Rectangle(-150, -30, 3, 5), 3, 5), (new Rectangle(10, 20, 319, 199), 319, 199),
            (new Rectangle(-3440, -494, 4880, 2560), 381, 200), (new Rectangle(5, 6, 1200, 4000), 60, 200)
        })
        {
            var factory = new FakeFactory();
            var preview = RegionPreviewCapture.CaptureAsync(region, CancellationToken.None, factory);
            await Until(() => factory.Captures.Count == 1);
            var capture = factory.Captures.Single(); await capture.Started.Task.WaitAsync(Timeout);
            Check(capture.Source.Type == SourceType.Region && capture.Source.RegionBounds is { } bounds &&
                bounds.X == region.X && bounds.Y == region.Y && bounds.Width == region.Width && bounds.Height == region.Height,
                "Region preview delegates the full desktop rectangle, including negative and cross-display coordinates, to the graphics backend.");
            Check(capture.Width == width && capture.Height == height && width <= 400 && height <= 200,
                "Region preview requests bounded proportional pixels with exact odd/small sizes.");
            capture.Emit(Color.Red); capture.Emit(Color.Blue);
            var jpeg = await preview.WaitAsync(Timeout);
            CheckImage(jpeg, width, height, image => image.GetPixel(0, 0).R > 220 && image.GetPixel(0, 0).B < 25,
                "Region preview encodes the first owned frame as JPEG, ignoring subsequent frames.");
            Check(capture.Cancelled.Task.IsCompleted && capture.Ended.Task.IsCompleted,
                "Successful region preview awaits producer cancellation and cleanup before returning.");
        }

        var cancelledFactory = new FakeFactory();
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel(); var cancelled = false;
            try { await RegionPreviewCapture.CaptureAsync(new(0, 0, 3, 5), cancellation.Token, cancelledFactory); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && cancelledFactory.Captures.IsEmpty, "Pre-cancelled region previews allocate no native producer.");
        }
        using (var cancellation = new CancellationTokenSource())
        {
            var preview = RegionPreviewCapture.CaptureAsync(new(0, 0, 31, 19), cancellation.Token, cancelledFactory);
            await Until(() => cancelledFactory.Captures.Count == 1);
            var capture = cancelledFactory.Captures.Single(); await capture.Started.Task.WaitAsync(Timeout);
            capture.HoldCancellation = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                cancellation.Cancel(); await capture.Cancelled.Task.WaitAsync(Timeout);
                Check(!preview.IsCompleted, "Cancelling preview waits for native ownership to be released.");
                capture.Emit(Color.Red); capture.HoldCancellation.SetResult();
                var cancelled = false;
                try { await preview.WaitAsync(Timeout); } catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && capture.Ended.Task.IsCompleted, "A late preview frame cannot turn caller cancellation into success.");
            }
            finally { capture.HoldCancellation.TrySetResult(); }
        }

        var timeoutFactory = new FakeFactory(); var timedOut = false;
        try { await RegionPreviewCapture.CaptureAsync(new(0, 0, 31, 19), CancellationToken.None, timeoutFactory).WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException) { timedOut = true; }
        Check(timedOut && timeoutFactory.Captures.Single().Cancelled.Task.IsCompleted && timeoutFactory.Captures.Single().Ended.Task.IsCompleted,
            "The production five-second preview timeout cancels and awaits a producer that supplies no frame.");

        foreach (var scenario in new[] { "failure", "end", "invalid-frame" })
        {
            var factory = new FakeFactory();
            var preview = RegionPreviewCapture.CaptureAsync(new(0, 0, 31, 19), CancellationToken.None, factory);
            await Until(() => factory.Captures.Count == 1);
            var capture = factory.Captures.Single(); await capture.Started.Task.WaitAsync(Timeout);
            if (scenario == "failure") capture.Fail(new IOException("Synthetic preview failure."));
            else if (scenario == "end") capture.Complete();
            else capture.EmitInvalidSize();
            var rejected = false;
            try { await preview.WaitAsync(Timeout); }
            catch (IOException error) { rejected = scenario != "failure" || error.Message == "Synthetic preview failure."; }
            catch (InvalidDataException) { rejected = scenario == "invalid-frame"; }
            Check(rejected && capture.Ended.Task.IsCompleted, $"Region preview {scenario} is reported with producer cleanup.");
        }
        var invalidRejected = false;
        try { await RegionPreviewCapture.CaptureAsync(new(0, 0, 0, 10), CancellationToken.None, new FakeFactory()); }
        catch (ArgumentOutOfRangeException) { invalidRejected = true; }
        Check(invalidRejected, "Empty preview rectangles are rejected before capture allocation.");
    }

    private sealed class FakeFactory : IGraphicsCaptureFactory
    {
        public ConcurrentQueue<FakeCapture> Captures { get; } = new();
        public Exception? CreateError;
        public IGraphicsFrameCapture Create(SourceItem source, int width, int height, int frameRate)
        {
            if (CreateError is { } error) throw error;
            var capture = new FakeCapture(source, width, height, frameRate); Captures.Enqueue(capture); return capture;
        }
    }

    private sealed class FakeCapture(SourceItem source, int width, int height, int frameRate) : IGraphicsFrameCapture
    {
        public SourceItem Source { get; } = source;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public int FrameRate { get; } = frameRate;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? HoldCancellation;
        public Exception? StopError;
        private readonly TaskCompletionSource _end = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Action<byte[], int, int>? _onFrame;
        private Action<CaptureColorInfo>? _onColor;

        public async Task RunAsync(Action<byte[], int, int> onFrame, Action<CaptureColorInfo> onColor, CancellationToken cancellationToken)
        {
            _onFrame = onFrame; _onColor = onColor; Started.SetResult();
            try { await _end.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                if (HoldCancellation is { } hold) await hold.Task.WaitAsync(Timeout);
                if (StopError is { } error) throw error;
                throw;
            }
            finally { Ended.TrySetResult(); }
        }

        public void Emit(Color color, bool stripes = false)
        {
            var pixels = ArrayPool<byte>.Shared.Rent(checked(Width * Height * 4));
            try
            {
                for (var y = 0; y < Height; y++) for (var x = 0; x < Width; x++)
                {
                    var value = stripes && x % 2 != 0 ? Color.Black : color;
                    var offset = (y * Width + x) * 4;
                    pixels[offset] = value.B; pixels[offset + 1] = value.G; pixels[offset + 2] = value.R; pixels[offset + 3] = 255;
                }
                (_onFrame ?? throw new InvalidOperationException("Await Started before emitting."))(pixels, Width, Height);
            }
            catch { ArrayPool<byte>.Shared.Return(pixels); throw; }
            // Successful callbacks take sole ownership, including after cancellation.
            // The fake never retains, returns or writes those buffers again.
        }
        public void ReportColor(CaptureColorInfo info) => (_onColor ?? throw new InvalidOperationException("Capture has not started."))(info);
        public void EmitInvalidSize()
        {
            var pixels = ArrayPool<byte>.Shared.Rent(4);
            try { (_onFrame ?? throw new InvalidOperationException("Capture has not started."))(pixels, 1, 1); }
            catch { ArrayPool<byte>.Shared.Return(pixels); throw; }
        }
        public void Fail(Exception error) => _end.TrySetException(error);
        public void Complete() => _end.TrySetResult();
    }

    private static void CheckImage(byte[] jpeg, int width, int height, Func<Bitmap, bool> pixels, string message)
    {
        using var stream = new MemoryStream(jpeg); using var image = new Bitmap(stream);
        Check(image.Width == width && image.Height == height && pixels(image), message);
    }
    private static void Check(bool value, string message)
    {
        _checks++; if (!value) throw new InvalidOperationException(message);
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
}
