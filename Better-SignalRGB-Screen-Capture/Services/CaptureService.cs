using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Core.Helpers;
using Better_SignalRGB_Screen_Capture.Helpers;
using Better_SignalRGB_Screen_Capture.Models;
using Microsoft.UI.Dispatching;
using ScreenRecorderLib;

namespace Better_SignalRGB_Screen_Capture.Services;

public sealed class CaptureService : ICaptureService
{
    private readonly ConcurrentDictionary<Guid, CaptureSession> _sessions = new();
    private sealed record PublishedFrames(byte[] Preview, byte[] SignalRgb);
    private readonly ConcurrentDictionary<Guid, PublishedFrames> _lastFrameData = new();
    private readonly ConcurrentDictionary<(int Width, int Height), byte> _softwareEncoderSizes = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly IWebsiteCaptureHostFactory _websiteHosts;
    private readonly IPipelineDiagnosticsService _diagnostics;
    private readonly IGraphicsCaptureFactory? _graphicsCaptures;
    private static readonly Lazy<byte[]> BlackFrame = new(CaptureFrameEncoder.CreateBlackFrame);
    private int _frameRate = 15;
    private bool _highQuality;

    // Frames are immutable JPEG arrays. Subscribers marshal UI work to their dispatcher.
    public event EventHandler<SourceFrameEventArgs>? FrameAvailable;
    public event EventHandler<CaptureFailedEventArgs>? CaptureFailed;

    public CaptureService(IWebsiteCaptureHostFactory websiteHosts, IPipelineDiagnosticsService diagnostics,
        IGraphicsCaptureFactory? graphicsCaptures = null)
    { _websiteHosts = websiteHosts; _diagnostics = diagnostics; _graphicsCaptures = graphicsCaptures; }

    private sealed class CaptureSession(SourceItem source, int frameRate, ICaptureDiagnosticsSession diagnostics)
    {
        public SourceItem Source { get; } = source;
        public ICaptureDiagnosticsSession Diagnostics { get; } = diagnostics;
        public int FrameRate { get; set; } = frameRate;
        public object PublicationLock { get; } = new();
        public volatile bool Stopped;
        public Recorder? Recorder;
        public bool UsesHardwareEncoding;
        public (int Width, int Height) EncoderSize;
        public (int Width, int Height) FrameSize;
        public (int Width, int Height) SignalFrameSize;
        public bool HighQuality;
        public CaptureDiscardStream? Output;
        public CaptureFrameMailbox<CapturedFrame> Frames { get; } = new();
        public Task Worker = Task.CompletedTask;
        public TaskCompletionSource<bool> RecordingEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DispatcherQueueTimer? WebsiteTimer;
        public bool WebsiteCapturePending;
        public Task WebsiteWork = Task.CompletedTask;
        public int ProcessingFailures;
        public IWebsiteFrameSource? WebsiteHost;
        public CancellationTokenSource? WallpaperCancellation;
        public Task WallpaperWork = Task.CompletedTask;
        public CancellationTokenSource? GraphicsCancellation;
        public Task GraphicsWork = Task.CompletedTask;
    }

    private sealed class CapturedFrame(byte[] pixels, int width, int height) : IDisposable
    {
        public byte[] Pixels { get; } = pixels;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public void Dispose() => ArrayPool<byte>.Shared.Return(Pixels);
    }

    public async Task StartCaptureAsync(SourceItem source)
    {
        ArgumentNullException.ThrowIfNull(source);
        await _lifecycle.WaitAsync();
        try { await StartSessionAsync(source); }
        finally { _lifecycle.Release(); }
    }

    private async Task StartSessionAsync(SourceItem source)
    {
        if (_sessions.TryGetValue(source.Id, out var existing))
        {
            if (!existing.Stopped) return;
            await StopSessionAsync(existing);
        }
        var frameSize = CaptureFrameEncoder.GetPreviewSize(source.CanvasWidth, source.CanvasHeight, _highQuality);
        var signalFrameSize = CaptureGeometry.GetOutputSize(source.CanvasWidth, source.CanvasHeight);
        var encoderSize = CaptureGeometry.GetCarrierSize(frameSize.Width, frameSize.Height);
        var hardware = !_softwareEncoderSizes.ContainsKey(encoderSize);
        var graphics = source.Type is SourceType.Monitor or SourceType.Process or SourceType.Region;
        var rate = Volatile.Read(ref _frameRate);
        var session = new CaptureSession(source, rate, _diagnostics.BeginCapture(source.Id, source.DisplayName,
            source.Type == SourceType.Website ? CaptureEncoderKind.Website : source.Type == SourceType.WallpaperEngine ? CaptureEncoderKind.Wallpaper :
                graphics ? CaptureEncoderKind.WindowsGraphicsCapture : hardware ? CaptureEncoderKind.Hardware : CaptureEncoderKind.Software,
            rate, frameSize.Width, frameSize.Height));
        session.FrameSize = frameSize;
        session.SignalFrameSize = signalFrameSize;
        session.HighQuality = _highQuality;
        _sessions[source.Id] = session;
        try
        {
            if (graphics)
            {
                var capture = (_graphicsCaptures ?? throw new InvalidOperationException("The graphics capture backend is unavailable."))
                    .Create(source, frameSize.Width, frameSize.Height, rate);
                session.GraphicsCancellation = new CancellationTokenSource();
                session.Worker = Task.Run(() => ProcessFramesAsync(session));
                session.GraphicsWork = Task.Run(() => CaptureGraphicsAsync(session, capture));
                return;
            }
            if (source.Type == SourceType.WallpaperEngine)
            {
                session.WallpaperCancellation = new CancellationTokenSource();
                session.Worker = Task.Run(() => ProcessFramesAsync(session));
                session.WallpaperWork = Task.Run(() => CaptureWallpaperAsync(session));
                return;
            }
            if (source.Type == SourceType.Website)
            {
                await RunOnUiThreadAsync(async () =>
                {
                    if (string.IsNullOrWhiteSpace(source.WebsiteUrl))
                        throw new InvalidOperationException($"No website URL is configured for {source.Name}.");
                    session.WebsiteHost = _websiteHosts.Create(source, session.HighQuality);
                    await session.WebsiteHost.PrepareCaptureAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    var timer = App.MainWindow.DispatcherQueue.CreateTimer();
                    timer.Interval = TimeSpan.FromSeconds(1d / session.FrameRate);
                    timer.Tick += (_, _) =>
                    {
                        if (!session.WebsiteCapturePending) session.WebsiteWork = CaptureWebsiteFrameAsync(session);
                        else session.Diagnostics.Skipped();
                    };
                    session.WebsiteTimer = timer;
                    timer.Start();
                });
                return;
            }

            session.EncoderSize = encoderSize;
            session.UsesHardwareEncoding = hardware;
            var options = CreateRecorderOptions(source, session.FrameSize, session.FrameRate, session.UsesHardwareEncoding);
            session.Output = new CaptureDiscardStream();
            session.Recorder = Recorder.CreateRecorder(options);
            session.Recorder.OnFrameRecorded += (_, args) => OnFrameRecorded(session, args);
            session.Recorder.OnRecordingComplete += (_, _) => OnRecordingEnded(session, null);
            session.Recorder.OnRecordingFailed += (_, args) => OnRecordingEnded(session, args.Error);
            session.Worker = Task.Run(() => ProcessFramesAsync(session));
            session.Recorder.Record(session.Output);
        }
        catch (Exception ex)
        {
            session.Diagnostics.Error(ex.Message, terminal: true);
            await StopSessionAsync(session);
            throw;
        }
    }

    private async Task CaptureGraphicsAsync(CaptureSession session, IGraphicsFrameCapture capture)
    {
        try
        {
            await capture.RunAsync((pixels, width, height) =>
            {
                var frame = new CapturedFrame(pixels, width, height);
                if (session.Stopped) { frame.Dispose(); return; }
                session.Diagnostics.Received();
                if (session.Frames.Publish(frame)) session.Diagnostics.Dropped();
            }, session.Diagnostics.SetColorInfo, session.GraphicsCancellation!.Token).ConfigureAwait(false);
            if (!session.Stopped) OnRecordingEnded(session, "Graphics capture ended unexpectedly.");
        }
        catch (OperationCanceledException) when (session.Stopped) { }
        catch (Exception error)
        {
            if (!session.Stopped) OnRecordingEnded(session, $"Screen/window capture failed: {error.Message}");
        }
    }

    private async Task CaptureWallpaperAsync(CaptureSession session)
    {
        try
        {
            await WallpaperCaptureProcess.PumpAsync(session.Source.MonitorDeviceId ?? string.Empty, session.FrameSize.Width,
                session.FrameSize.Height, session.FrameRate, (pixels, width, height) =>
                {
                    var frame = new CapturedFrame(pixels, width, height);
                    if (session.Stopped) { frame.Dispose(); return; }
                    session.Diagnostics.Received();
                    if (session.Frames.Publish(frame)) session.Diagnostics.Dropped();
                }, session.WallpaperCancellation!.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (session.Stopped) { }
        catch (Exception error)
        {
            if (!session.Stopped) OnRecordingEnded(session, $"Wallpaper Engine capture failed: {error.Message}");
        }
    }

    private async Task CaptureWebsiteFrameAsync(CaptureSession session)
    {
        // Dispatcher timers use async void callbacks; explicitly prevent overlapping WebView captures.
        if (session.Stopped || session.WebsiteCapturePending) return;
        session.WebsiteCapturePending = true;
        var started = Stopwatch.GetTimestamp();
        try
        {
            // The session owns its browser independently of canvas/editor visibility.
            // Navigation or template replacement cannot exchange the capture producer.
            var frame = await session.WebsiteHost!.CaptureFrameAsync();
            if (frame is { Length: > 0 } && !session.Stopped)
            {
                session.Diagnostics.Received();
                var encoded = await Task.Run(() => CaptureFrameEncoder.EncodeBrowserFrames(frame,
                    session.FrameSize, session.SignalFrameSize, session.HighQuality));
                PublishFrame(session, encoded.Preview, encoded.SignalRgb, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                session.ProcessingFailures = 0;
            }
            else if (!session.Stopped) session.Diagnostics.Skipped();
        }
        catch (Exception ex)
        {
            if (session.Stopped) return;
            Debug.WriteLine($"Website capture failed for {session.Source.Name}: {ex.Message}");
            var terminal = ++session.ProcessingFailures >= 3;
            session.Diagnostics.Error(ex.Message, terminal);
            if (terminal) OnRecordingEnded(session, $"Website capture failed: {ex.Message}", errorRecorded: true);
        }
        finally { session.WebsiteCapturePending = false; }
    }

    public async Task StopCaptureAsync(SourceItem source)
    {
        ArgumentNullException.ThrowIfNull(source);
        await _lifecycle.WaitAsync();
        try
        {
            if (_sessions.TryGetValue(source.Id, out var session)) await StopSessionAsync(session);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task StopSessionAsync(CaptureSession session)
    {
        lock (session.PublicationLock)
        {
            session.Stopped = true;
            if (_sessions.TryGetValue(session.Source.Id, out var current) && ReferenceEquals(current, session))
            {
                _sessions.TryRemove(session.Source.Id, out _);
                _lastFrameData.TryRemove(session.Source.Id, out _);
            }
        }
        session.Frames.Dispose();
        session.Diagnostics.Stop();
        if (session.GraphicsCancellation is not null)
        {
            session.GraphicsCancellation.Cancel();
            await session.GraphicsWork;
            session.GraphicsCancellation.Dispose();
            session.GraphicsCancellation = null;
        }
        if (session.WallpaperCancellation is not null)
        {
            session.WallpaperCancellation.Cancel();
            await session.WallpaperWork;
            session.WallpaperCancellation.Dispose();
            session.WallpaperCancellation = null;
        }
        if (session.Source.Type == SourceType.Website)
        {
            try
            {
                await RunOnUiThreadAsync(() =>
                {
                    session.WebsiteTimer?.Stop();
                    session.WebsiteTimer = null;
                    var released = session.WebsiteHost;
                    session.WebsiteHost = null;
                    released?.CaptureStopped();
                });
            }
            catch (Exception ex) { Debug.WriteLine($"Website host shutdown failed: {ex.Message}"); }
            try { await session.WebsiteWork.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { Debug.WriteLine("The stopped WebView capture is still completing; its result will be discarded."); }
        }
        if (session.Recorder is not null)
        {
            var recorder = session.Recorder;
            session.Recorder = null;
            try
            {
                // Native stop/finalization can wait for callbacks. Never block the UI thread or
                // dispose the recorder from inside one of its own callbacks.
                await Task.Run(async () =>
                {
                    try
                    {
                        if (recorder.Status is RecorderStatus.Recording or RecorderStatus.Paused)
                        {
                            recorder.Stop();
                            await Task.WhenAny(session.RecordingEnded.Task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
                        }
                    }
                    finally { recorder.Dispose(); }
                });
            }
            catch (Exception ex) { Debug.WriteLine($"Recorder shutdown failed for {session.Source.Name}: {ex.Message}"); }
        }
        session.Output?.Dispose();
        session.Output = null;
        await session.Worker;
    }

    public async Task StopAllCapturesAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            foreach (var session in _sessions.Values.ToArray()) await StopSessionAsync(session);
            _lastFrameData.Clear();
        }
        finally { _lifecycle.Release(); }
    }

    public bool IsCapturing(SourceItem source) => source is not null &&
        _sessions.TryGetValue(source.Id, out var session) && !session.Stopped;

    public async Task SetFrameRate(int fps)
    {
        Volatile.Write(ref _frameRate, Math.Clamp(fps, 1, 60));
        await _lifecycle.WaitAsync();
        try
        {
            // Read the newest value after acquiring the gate, coalescing rapid slider changes.
            var desired = Volatile.Read(ref _frameRate);
            foreach (var session in _sessions.Values.ToArray())
            {
                if (session.FrameRate == desired) continue;
                if (session.WebsiteTimer is not null)
                {
                    await RunOnUiThreadAsync(() => session.WebsiteTimer.Interval = TimeSpan.FromSeconds(1d / desired));
                    session.FrameRate = desired;
                    session.Diagnostics.SetFrameRate(desired);
                }
                else
                {
                    var source = session.Source;
                    await StopSessionAsync(session);
                    try { await StartSessionAsync(source); }
                    catch (Exception ex) { NotifyCaptureFailure(source, $"Could not apply the new frame rate: {ex.Message}"); }
                }
            }
        }
        finally { _lifecycle.Release(); }
    }

    public async Task SetHighQuality(bool enabled)
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_sessions.Count != 0) throw new InvalidOperationException("Stop capture before changing its quality.");
            _highQuality = enabled;
        }
        finally { _lifecycle.Release(); }
    }

    public byte[]? GetMjpegFrame(Guid sourceId) => _lastFrameData.TryGetValue(sourceId, out var frame) ? frame.Preview : null;
    public byte[]? GetMjpegFrame() => _lastFrameData.Values.FirstOrDefault()?.Preview ?? BlackFrame.Value;
    public byte[]? GetLatestSignalRgbFrame(Guid sourceId) => _lastFrameData.TryGetValue(sourceId, out var frame) ? frame.SignalRgb : null;

    private void OnFrameRecorded(CaptureSession session, FrameRecordedEventArgs args)
    {
        if (session.Stopped) return;
        session.Diagnostics.Received();
        var bitmap = args.BitmapData;
        if (bitmap is null || bitmap.Data == IntPtr.Zero || bitmap.Width < 1 || bitmap.Height < 1 ||
            bitmap.Width > CaptureGeometry.MaximumDimension || bitmap.Height > CaptureGeometry.MaximumDimension)
        { session.Diagnostics.Error("The native preview returned invalid bitmap dimensions or pixels."); return; }
        var rowBytes = checked(bitmap.Width * 4);
        if (bitmap.Stride < rowBytes || (long)bitmap.Width * bitmap.Height > CaptureGeometry.MaximumCarrierPixels)
        { session.Diagnostics.Error("The native preview exceeded its stride or pixel allocation bounds."); return; }
        byte[]? pixels = null;
        try
        {
            pixels = ArrayPool<byte>.Shared.Rent(checked(rowBytes * bitmap.Height));
            // BitmapData is owned by ScreenRecorderLib and valid only during this callback.
            // Copy rows now, including handling GPU row padding, before handing work off.
            CaptureFrameEncoder.CopyBgraRows(bitmap.Data, bitmap.Width, bitmap.Height, bitmap.Stride, pixels);
            var frame = new CapturedFrame(pixels, bitmap.Width, bitmap.Height);
            pixels = null;
            if (session.Frames.Publish(frame)) session.Diagnostics.Dropped();
        }
        catch (Exception ex) { session.Diagnostics.Error(ex.Message); Debug.WriteLine($"Frame copy failed for {session.Source.Name}: {ex.Message}"); }
        finally { if (pixels is not null) ArrayPool<byte>.Shared.Return(pixels); }
    }

    private async Task ProcessFramesAsync(CaptureSession session)
    {
        using var output = new MemoryStream();
        using var parameters = CaptureFrameEncoder.CreateParameters(session.HighQuality ? CaptureFrameEncoder.HighQuality : CaptureFrameEncoder.StandardQuality);
        using var signalParameters = CaptureFrameEncoder.CreateParameters();
        while (await session.Frames.TakeAsync().ConfigureAwait(false) is { } frame)
        {
            using (frame)
            {
                if (session.Stopped) continue;
                try
                {
                    var started = Stopwatch.GetTimestamp();
                    var jpeg = CaptureFrameEncoder.EncodeJpeg(frame.Pixels, frame.Width, frame.Height,
                        session.FrameSize.Width, session.FrameSize.Height, output, parameters);
                    var signal = session.HighQuality ? CaptureFrameEncoder.EncodeJpeg(frame.Pixels, frame.Width, frame.Height,
                        session.SignalFrameSize.Width, session.SignalFrameSize.Height, output, signalParameters) : jpeg;
                    PublishFrame(session, jpeg, signal, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    session.ProcessingFailures = 0;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"JPEG encoding failed for {session.Source.Name}: {ex.Message}");
                    var terminal = ++session.ProcessingFailures >= 3;
                    session.Diagnostics.Error(ex.Message, terminal);
                    if (terminal) OnRecordingEnded(session, $"JPEG processing failed: {ex.Message}", errorRecorded: true);
                }
            }
        }
    }

    private void PublishFrame(CaptureSession session, byte[] frame, byte[] signalFrame, double processingMilliseconds)
    {
        lock (session.PublicationLock)
        {
            if (session.Stopped) return;
            session.Diagnostics.Produced(frame.Length, processingMilliseconds);
            _lastFrameData[session.Source.Id] = new PublishedFrames(frame, signalFrame);
            var args = new SourceFrameEventArgs(session.Source, null) { FrameData = frame };
            // A failed consumer must not terminate the capture worker or starve other consumers.
            var handlers = FrameAvailable;
            if (handlers is null) return;
            foreach (EventHandler<SourceFrameEventArgs> handler in handlers.GetInvocationList())
            {
                try { handler(this, args); }
                catch (Exception ex) { Debug.WriteLine($"Frame consumer failed: {ex.Message}"); }
            }
        }
    }

    private void OnRecordingEnded(CaptureSession session, string? error, bool errorRecorded = false)
    {
        session.RecordingEnded.TrySetResult(true);
        if (error is not null) Debug.WriteLine($"Recording failed for {session.Source.Name}: {error}");
        lock (session.PublicationLock)
        {
            if (session.Stopped) return;
            session.Stopped = true;
        }
        // User-requested stop marks the session above before native completion.
        // A completion while still active is a source failure, otherwise consumers
        // would keep streaming the last image after its producer has disappeared.
        error ??= "The capture source ended unexpectedly.";
        if (error is not null && !errorRecorded) session.Diagnostics.Error(error, terminal: true);
        _ = Task.Run(async () =>
        {
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_sessions.TryGetValue(session.Source.Id, out var current) && ReferenceEquals(current, session))
                {
                    await StopSessionAsync(session).ConfigureAwait(false);
                    if (error is not null && session.UsesHardwareEncoding && CaptureRecorderOptions.IsEncoderFailure(error))
                    {
                        // Some hardware codecs reject small or unusual dimensions only on their
                        // first encoded sample. Retry once with software and remember that size.
                        _softwareEncoderSizes.TryAdd(session.EncoderSize, 0);
                        await StartSessionAsync(session.Source).ConfigureAwait(false);
                    }
                    else if (error is not null) NotifyCaptureFailure(session.Source, error);
                }
            }
            catch (Exception ex) { NotifyCaptureFailure(session.Source, ex.Message); }
            finally { _lifecycle.Release(); }
        });
    }

    private void NotifyCaptureFailure(SourceItem source, string error)
    {
        Debug.WriteLine($"Capture failed for {source.Name}: {error}");
        try { CaptureFailed?.Invoke(this, new CaptureFailedEventArgs(source, error)); }
        catch (Exception ex) { Debug.WriteLine($"Capture error consumer failed: {ex.Message}"); }
    }

    private static Task RunOnUiThreadAsync(Action action)
    {
        var dispatcher = App.MainWindow?.DispatcherQueue;
        if (dispatcher is null) return Task.FromException(new InvalidOperationException("The UI dispatcher is unavailable."));
        if (dispatcher.HasThreadAccess)
        {
            try { action(); return Task.CompletedTask; }
            catch (Exception ex) { return Task.FromException(ex); }
        }
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(() =>
        {
            try { action(); completion.TrySetResult(true); }
            catch (Exception ex) { completion.TrySetException(ex); }
        })) completion.TrySetException(new InvalidOperationException("The UI dispatcher has shut down."));
        return completion.Task;
    }

    private static Task RunOnUiThreadAsync(Func<Task> action)
    {
        var dispatcher = App.MainWindow?.DispatcherQueue;
        if (dispatcher is null) return Task.FromException(new InvalidOperationException("The UI dispatcher is unavailable."));
        if (dispatcher.HasThreadAccess)
        {
            try { return action(); }
            catch (Exception ex) { return Task.FromException(ex); }
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(async () =>
        {
            try { await action(); completion.TrySetResult(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        })) completion.TrySetException(new InvalidOperationException("The UI dispatcher has shut down."));
        return completion.Task;
    }

    private RecorderOptions CreateRecorderOptions(SourceItem source, (int Width, int Height) size, int frameRate, bool useHardwareEncoding)
    {
        var recordingSources = CaptureSourceFactory.Create(source);
        return CaptureRecorderOptions.Create(recordingSources, size.Width, size.Height,
            frameRate, source.Type == SourceType.Region, source.Type == SourceType.Webcam, useHardwareEncoding);
    }
    public static List<RecordableDisplay> GetAvailableDisplays() => Recorder.GetDisplays().ToList();
    public static List<RecordableWindow> GetAvailableWindows() => Recorder.GetWindows().ToList();
    public static List<RecordableCamera> GetAvailableWebcams() => Recorder.GetSystemVideoCaptureDevices().ToList();
    public static List<VideoCaptureFormat> GetWebcamFormats(string deviceName) => Recorder.GetSupportedVideoCaptureFormatsForDevice(deviceName).ToList();

}
