using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Helpers;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.Services;
using Microsoft.UI.Dispatching;

internal static class WebsiteCaptureServiceTests
{
    private sealed class Surface : IWebsiteFrameSource
    {
        public Func<Task<byte[]?>> Frame = () => Task.FromResult<byte[]?>(CaptureFrameEncoder.CreateBlackFrame());
        public Func<Task> Prepare = () => Task.CompletedTask;
        public int Prepared;
        public int Released;
        public Task PrepareCaptureAsync() { Prepared++; return Prepare(); }
        public Task<byte[]?> CaptureFrameAsync() => Frame();
        public void CaptureStopped() => Released++;
    }

    public static async Task RunAsync()
    {
        var diagnostics = new PipelineDiagnosticsService();
        var source = new SourceItem { Type = SourceType.Website, WebsiteUrl = "https://example.test", CanvasWidth = 3, CanvasHeight = 5 };
        var surface = new Surface();
        var nextSurface = surface; var created = 0;
        var capture = new CaptureService(new DelegateWebsiteCaptureHostFactory(_ => { created++; return nextSurface; }), diagnostics);
        var failures = 0;
        capture.CaptureFailed += (_, _) => Interlocked.Increment(ref failures);
        await capture.StartCaptureAsync(source);
        Check(surface.Prepared == 1, "Browser surface must be prepared before capture starts.");
        Check(created == 1, "Production capture must create exactly one owned host without an editor.");
        var timer = DispatcherQueueTimer.Latest!;
        timer.Fire();
        await Until(() => capture.GetMjpegFrame(source.Id) is not null);
        using (var stream = new MemoryStream(capture.GetMjpegFrame(source.Id)!))
        using (var image = System.Drawing.Image.FromStream(stream))
            Check(image.Width == 3 && image.Height == 5, "Website JPEG keeps odd logical dimensions.");
        await capture.SetFrameRate(30);
        Check(Math.Abs(timer.Interval.TotalSeconds - 1d / 30) < .00001, "Website FPS changes without replacing its surface.");
        var pending = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        surface.Frame = () => pending.Task;
        timer.Fire(); timer.Fire();
        Check(diagnostics.GetSnapshot().Sources[0].SkippedCaptures == 1, "Busy browser ticks must not overlap capture.");
        var replacement = new Surface(); nextSurface = replacement;
        var stop = capture.StopCaptureAsync(source);
        Check(!capture.IsCapturing(source) && capture.GetMjpegFrame(source.Id) is null, "Stop synchronously prevents publication and clears its frame.");
        pending.SetException(new InvalidOperationException("Browser disposed during intentional stop."));
        await stop;
        Check(surface.Released == 1 && replacement.Released == 0,
            "Stop releases only its owned host, without creating or touching a replacement.");
        Check(!timer.Running && failures == 0 && diagnostics.GetSnapshot().Sources[0].Errors == 0,
            "Intentional stop must not report late browser errors or leave its timer running.");
        replacement.Frame = () => Task.FromException<byte[]?>(new InvalidOperationException("Synthetic browser capture failure."));
        await capture.StartCaptureAsync(source);
        Check(replacement.Prepared == 1 && created == 2, "Restart must create and initialize a fresh browser host.");
        timer = DispatcherQueueTimer.Latest!;
        for (var error = 1; error <= 3; error++)
        {
            timer.Fire();
            await Until(() => diagnostics.GetSnapshot().Sources[0].Errors >= error);
        }
        await Until(() => Volatile.Read(ref failures) == 1);
        Check(!capture.IsCapturing(source) && capture.GetMjpegFrame(source.Id) is null && !timer.Running,
            "Repeated browser errors terminate capture and clear its frame/timer.");
        Check(diagnostics.GetSnapshot().Sources[0].State == CaptureDiagnosticState.Failed && replacement.Released == 1,
            "Terminal browser failure remains visible and releases its browser.");
        await capture.StopAllCapturesAsync();
        var initializationFailure = new Surface { Prepare = () => Task.FromException(new InvalidOperationException("Synthetic browser initialization failure.")) };
        nextSurface = initializationFailure;
        var rejected = false;
        try { await capture.StartCaptureAsync(source); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected && !capture.IsCapturing(source) && initializationFailure.Released == 1 && created == 3,
            "Initialization failure must release its surface and roll back the capture session.");
        Console.WriteLine("PASS: real website capture service with owned fake browser: no editor dependency, JPEG dimensions, FPS, busy ticks, fresh restart, late errors and failure cleanup.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
}

internal sealed class DelegateWebsiteCaptureHostFactory(Func<SourceItem, IWebsiteFrameSource> create) : IWebsiteCaptureHostFactory
{
    public IWebsiteFrameSource Create(SourceItem source, bool highQuality = false) => create(source);
}

// The production capture service is linked unchanged. Only UI ownership and the
// timer are deterministic adapters here; this is not a physical WebView2 test.
namespace Microsoft.UI.Dispatching
{
    public sealed class DispatcherQueue
    {
        public bool HasThreadAccess => true;
        public bool TryEnqueue(Action callback) { callback(); return true; }
        public DispatcherQueueTimer CreateTimer() => new();
    }
    public sealed class DispatcherQueueTimer
    {
        public static DispatcherQueueTimer? Latest;
        public DispatcherQueueTimer() => Latest = this;
        public TimeSpan Interval { get; set; }
        public bool Running { get; private set; }
        public event EventHandler<object>? Tick;
        public void Start() => Running = true;
        public void Stop() => Running = false;
        public void Fire() { if (Running) Tick?.Invoke(this, EventArgs.Empty); }
    }
}
namespace Microsoft.UI.Xaml.Media.Imaging { public sealed class BitmapImage { } }
namespace Better_SignalRGB_Screen_Capture
{
    internal static class App { public static Window MainWindow { get; } = new(); }
    internal sealed class Window { public DispatcherQueue DispatcherQueue { get; } = new(); }
}
