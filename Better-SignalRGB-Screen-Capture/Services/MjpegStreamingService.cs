using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Models;

namespace Better_SignalRGB_Screen_Capture.Services;

public sealed class MjpegStreamingService : IMjpegStreamingService, IDisposable
{
    private const string CanvasApiUrl = "http://localhost:16034/canvas/event";
    private const string SenderTag = "BetterSignalRGBScreenCapture";
    private const int ChunkSize = 6144;
    private static readonly HttpClient CanvasClient = new() { Timeout = TimeSpan.FromSeconds(2) };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ICaptureService _captureService;
    private readonly ICompositeFrameService _compositeService;
    private readonly bool _publishCanvasEvents;
    private readonly Func<string, CancellationToken, Task> _sendCanvasEvent;
    private readonly ISignalRgbEffectSettingsService? _effectSettingsService;
    private static readonly SignalRgbEffectSettings DefaultEffectSettings = new();
    private readonly ISignalRgbConnectionService? _connection;
    private readonly IPipelineDiagnosticsService? _diagnostics;
    private string? _healthEvent;
    private readonly StreamingSourceFrames _frames = new();
    private readonly StreamingSourceFrames _signalFrames = new();
    private readonly ConcurrentDictionary<long, Task> _requests = new();
    private readonly ConcurrentDictionary<Guid, byte> _pendingRemovals = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private HttpListener? _listener;
    private CancellationTokenSource? _cancellation;
    private Task? _acceptLoop;
    private Task? _canvasLoop;
    private long _requestId;
    private volatile bool _isStreaming;
    private bool _disposed;

    public event EventHandler<string>? StreamingUrlChanged;
    public bool IsStreaming => _isStreaming;
    public string? StreamingUrl { get; private set; }

    public MjpegStreamingService(ICaptureService captureService, ICompositeFrameService compositeService,
        ISignalRgbEffectSettingsService effectSettings, ISignalRgbConnectionService connection, IPipelineDiagnosticsService diagnostics)
        : this(captureService, compositeService, publishCanvasEvents: true, effectSettings: effectSettings, connection: connection, diagnostics: diagnostics) { }

    // Local integration tests exercise real HTTP streaming without sending test frames to the user's SignalRGB.
    internal MjpegStreamingService(ICaptureService captureService, ICompositeFrameService compositeService, bool publishCanvasEvents,
        Func<string, CancellationToken, Task>? canvasEventSender = null, ISignalRgbEffectSettingsService? effectSettings = null,
        ISignalRgbConnectionService? connection = null, IPipelineDiagnosticsService? diagnostics = null)
    {
        _captureService = captureService;
        _compositeService = compositeService;
        _publishCanvasEvents = publishCanvasEvents;
        _sendCanvasEvent = canvasEventSender ?? PostCanvasEventAsync;
        _effectSettingsService = effectSettings;
        _connection = connection;
        _diagnostics = diagnostics;
        _captureService.FrameAvailable += OnFrameAvailable;
        _captureService.CaptureFailed += OnCaptureFailed;
    }

    private void OnFrameAvailable(object? sender, SourceFrameEventArgs e)
    {
        if (e.FrameData is not { Length: > 0 } frame) return;
        if (_isStreaming) UpdateSourceFrame(e.Source.Id, frame);
        else { _frames.Recover(e.Source.Id); _signalFrames.Recover(e.Source.Id); }
    }

    private void OnCaptureFailed(object? sender, CaptureFailedEventArgs e) => _ = NotifySourceRemovedAsync(e.Source.Id);

    public void UpdateSourceFrame(Guid sourceId, byte[] jpegData)
    {
        if (_isStreaming && jpegData.Length > 0)
        {
            _frames.Publish(sourceId, jpegData);
            // The producer publishes both immutable outputs together. Keep the small
            // SignalRGB derivative across pauses without ever downsampling per send.
            if (_captureService.GetLatestSignalRgbFrame(sourceId) is { Length: > 0 } signalFrame)
                _signalFrames.Publish(sourceId, signalFrame);
        }
    }

    public async Task StartStreamingAsync(int port = 8080)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        await _lifecycle.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_isStreaming) return;
            if (_effectSettingsService != null)
            {
                try { await _effectSettingsService.InitializeAsync(); }
                catch (Exception exception) { Debug.WriteLine($"Could not load effect appearance: {exception.Message}"); }
            }
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            try { listener.Start(); }
            catch { listener.Close(); throw; }
            _listener = listener;
            _cancellation = new CancellationTokenSource();
            _isStreaming = true;
            _healthEvent = _publishCanvasEvents ? _connection?.BeginSession(port) : null;
            _diagnostics?.SetSignalRgbRunning(_publishCanvasEvents);
            var token = _cancellation.Token;
            _acceptLoop = AcceptRequestsAsync(listener, token);
            // Always enter the worker so an immediate Stop is handled by its cancellation cleanup.
            _canvasLoop = _publishCanvasEvents ? Task.Run(() => SendCanvasFramesAsync(token)) : Task.CompletedTask;
            StreamingUrl = $"http://localhost:{port}/canvas/";
            StreamingUrlChanged?.Invoke(this, StreamingUrl);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopStreamingAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            _isStreaming = false;
            _cancellation?.Cancel();
            _listener?.Close();
            _frames.CloseStreams();
            _signalFrames.CloseStreams();
            if (_acceptLoop != null) await _acceptLoop.ConfigureAwait(false);
            if (_canvasLoop != null) await _canvasLoop.ConfigureAwait(false);
            _connection?.EndSession();
            _diagnostics?.SetSignalRgbRunning(false);
            _healthEvent = null;
            await Task.WhenAll(_requests.Values.ToArray()).ConfigureAwait(false);
            _frames.Clear();
            _signalFrames.Clear();
            _pendingRemovals.Clear();
            _cancellation?.Dispose();
            _cancellation = null;
            _listener = null;
            _acceptLoop = _canvasLoop = null;
            StreamingUrl = null;
            StreamingUrlChanged?.Invoke(this, string.Empty);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task AcceptRequestsAsync(HttpListener listener, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().ConfigureAwait(false);
                var id = Interlocked.Increment(ref _requestId);
                var task = HandleRequestAsync(context, token);
                _requests[id] = task;
                _ = task.ContinueWith(_ => _requests.TryRemove(id, out var ignored),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or OperationCanceledException)
        { /* Listener shutdown. */ }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken token)
    {
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        token = requestCancellation.Token;
        var response = context.Response;
        StreamingCompositeSession? compositeSession = null;
        try
        {
            var path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["Access-Control-Allow-Origin"] = "*";
            if (path == "/api/effect-status")
            {
                response.Headers["Access-Control-Allow-Private-Network"] = "true";
                if (context.Request.HttpMethod == "OPTIONS")
                {
                    response.Headers["Access-Control-Allow-Methods"] = "GET, OPTIONS";
                    response.StatusCode = 204;
                    return;
                }
                var query = context.Request.QueryString;
                response.StatusCode = context.Request.HttpMethod == "GET" &&
                    long.TryParse(query["frames"], out var rendered) && int.TryParse(query["version"], out var version) &&
                    _connection?.ReceiveFeedback(query["session"], rendered, version) == true ? 204 : 400;
                return;
            }
            if (context.Request.HttpMethod != "GET") { response.StatusCode = 405; return; }
            if (path == "/stream" || path.StartsWith("/stream/", StringComparison.Ordinal))
            {
                StreamingFrameState state;
                if (path == "/stream")
                {
                    compositeSession = new StreamingCompositeSession(_compositeService);
                    state = compositeSession.Frames;
                }
                else
                {
                    if (!Guid.TryParse(path[8..], out var sourceId)) { response.StatusCode = 404; return; }
                    var sources = await StreamingCanvasSnapshot.CaptureAsync(token);
                    if (!sources.Any(source => source.Id == sourceId)) { response.StatusCode = 404; return; }
                    var available = _frames.Open(sourceId, _captureService.GetMjpegFrame);
                    if (available == null)
                    {
                        response.StatusCode = 503;
                        response.Headers["Retry-After"] = "1";
                        return;
                    }
                    state = available;
                }
                response.ContentType = StreamingMultipartWriter.ContentType;
                response.SendChunked = true;
                byte[]? previous = null;
                while (await state.WaitForNextAsync(previous, token).WaitAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false) is { } frame)
                {
                    using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    writeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                    await StreamingMultipartWriter.WriteAsync(response.OutputStream, frame, writeTimeout.Token).ConfigureAwait(false);
                    previous = frame;
                }
            }
            else if (path is "/api/sources" or "/api/canvasinfo")
            {
                var sources = _frames.FilterAvailable(await StreamingCanvasSnapshot.CaptureAsync(token));
                response.ContentType = "application/json; charset=utf-8";
                var json = path == "/api/sources" ? JsonSerializer.Serialize(sources, JsonOptions) :
                    JsonSerializer.Serialize(new { canvasWidth = StreamingCanvasSnapshot.Width, canvasHeight = StreamingCanvasSnapshot.Height, sources }, JsonOptions);
                await WriteTextAsync(response, json, token).ConfigureAwait(false);
            }
            else if (path is "" or "/canvas")
            {
                response.ContentType = "text/html; charset=utf-8";
                await WriteTextAsync(response, StreamingCanvasPage.Html, token).ConfigureAwait(false);
            }
            else response.StatusCode = 404;
        }
        catch (Exception exception) when (exception is OperationCanceledException or HttpListenerException or IOException or ObjectDisposedException or TimeoutException)
        { /* Client disconnected or service stopped. */ }
        catch (Exception exception) { Debug.WriteLine($"Streaming request failed: {exception.Message}"); }
        finally
        {
            requestCancellation.Cancel();
            compositeSession?.Dispose();
            try { response.Close(); } catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException) { }
        }
    }

    private static async Task WriteTextAsync(HttpListenerResponse response, string text, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, token).ConfigureAwait(false);
    }

    // One sender owns the header/data/end protocol; slow receivers only retain the newest JPEG per source.
    private async Task SendCanvasFramesAsync(CancellationToken token)
    {
        var sent = new Dictionary<Guid, (byte[] Frame, StreamingSourceSnapshot Layout, long Timestamp)>();
        var needsReset = true;
        SignalRgbEffectSettings? sentSettings = null;
        var lastSettingsSend = 0L;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1d / 15));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try
                {
                    if (needsReset) { await _sendCanvasEvent("reset", token); needsReset = false; }
                    // Read the atomic service snapshot directly: subscribe/read ordering can
                    // otherwise miss a concurrent edit during construction.
                    var settings = _effectSettingsService?.Current ?? DefaultEffectSettings;
                    timer.Period = TimeSpan.FromSeconds(1d / (settings.Enabled ? settings.FrameRate : 15));
                    // Re-send infrequently so a reloaded effect can recover app controls without
                    // changing or queuing images. Config and frame transactions share one writer.
                    if (sentSettings != settings || Stopwatch.GetElapsedTime(lastSettingsSend).TotalSeconds >= 2)
                    {
                        await _sendCanvasEvent("config:" + JsonSerializer.Serialize(settings, JsonOptions), token);
                        if (_healthEvent != null) await _sendCanvasEvent(_healthEvent, token);
                        _connection?.ApiSucceeded();
                        sentSettings = settings;
                        lastSettingsSend = Stopwatch.GetTimestamp();
                    }
                    foreach (var id in _pendingRemovals.Keys)
                    {
                        await _sendCanvasEvent($"remove:{id}", token);
                        _pendingRemovals.TryRemove(id, out _);
                        sent.Remove(id);
                    }
                    var sources = await StreamingCanvasSnapshot.CaptureAsync(token);
                    var activeIds = sources.Select(s => s.Id).ToHashSet();
                    foreach (var id in sent.Keys.Where(id => !activeIds.Contains(id)).ToArray())
                    {
                        await _sendCanvasEvent($"remove:{id}", token);
                        sent.Remove(id);
                        _frames.Invalidate(id);
                        _signalFrames.Invalidate(id);
                    }
                    foreach (var source in sources)
                    {
                        if (_pendingRemovals.ContainsKey(source.Id)) continue;
                        var frame = _signalFrames.Latest(source.Id, _captureService.GetLatestSignalRgbFrame);
                        if (frame == null) continue;
                        // A paused/static source still needs an occasional complete frame: the
                        // Canvas API acknowledges delivery even when the effect is being reloaded.
                        if (sent.TryGetValue(source.Id, out var previous) && ReferenceEquals(frame, previous.Frame) &&
                            source == previous.Layout && Stopwatch.GetElapsedTime(previous.Timestamp).TotalSeconds < 2) continue;
                        var sendStarted = Stopwatch.GetTimestamp();
                        var base64 = Convert.ToBase64String(frame);
                        var count = (base64.Length + ChunkSize - 1) / ChunkSize;
                        await _sendCanvasEvent($"header:{source.Id}.jpg:image/jpeg:{count}:outer:{source.OuterStyle}|crop:{source.CropStyle}", token);
                        var aborted = false;
                        for (var index = 0; index < count; index++)
                        {
                            if (_signalFrames.IsUnavailable(source.Id) || _pendingRemovals.ContainsKey(source.Id))
                            { aborted = true; break; }
                            var offset = index * ChunkSize;
                            await _sendCanvasEvent($"data:{source.Id}:{index}:{base64.Substring(offset, Math.Min(ChunkSize, base64.Length - offset))}", token);
                        }
                        if (aborted || _signalFrames.IsUnavailable(source.Id) || _pendingRemovals.ContainsKey(source.Id)) continue;
                        await _sendCanvasEvent($"end:{source.Id}", token);
                        _connection?.ApiSucceeded();
                        _connection?.FrameSent();
                        _diagnostics?.RecordSignalRgbFrame(source.Id, frame.Length, Stopwatch.GetElapsedTime(sendStarted).TotalMilliseconds);
                        sent[source.Id] = (frame, source, Stopwatch.GetTimestamp());
                    }
                }
                catch (Exception exception) when (!token.IsCancellationRequested)
                {
                    Debug.WriteLine($"SignalRGB Canvas API unavailable: {exception.Message}");
                    _connection?.ApiFailed(exception.Message);
                    _diagnostics?.RecordSignalRgbError(exception.Message);
                    // Failed transactions are discarded. Backoff prevents task and frame queues while SignalRGB is closed.
                    needsReset = true;
                    sentSettings = null;
                    sent.Clear();
                    await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private static async Task PostCanvasEventAsync(string data, CancellationToken token)
    {
        var url = $"{CanvasApiUrl}?sender={SenderTag}&event={Uri.EscapeDataString(data)}";
        using var content = new StringContent(string.Empty);
        using var response = await CanvasClient.PostAsync(url, content, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public Task NotifySourceRemovedAsync(Guid sourceId)
    {
        _pendingRemovals[sourceId] = 0;
        _frames.Invalidate(sourceId);
        _signalFrames.Invalidate(sourceId);
        return Task.CompletedTask;
    }

    public async Task ResetCanvasAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            // Reset is a stop-time operation so it cannot interrupt header/data/end transactions.
            if (_isStreaming) throw new InvalidOperationException("Stop streaming before resetting the canvas.");
            _frames.Clear();
            _signalFrames.Clear();
            _pendingRemovals.Clear();
            if (!_publishCanvasEvents) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
            try { await _sendCanvasEvent("reset", timeout.Token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
            { Debug.WriteLine($"SignalRGB canvas reset could not be delivered: {exception.Message}"); }
        }
        finally { _lifecycle.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _isStreaming = false;
        _captureService.FrameAvailable -= OnFrameAvailable;
        _captureService.CaptureFailed -= OnCaptureFailed;
        _connection?.EndSession();
        _diagnostics?.SetSignalRgbRunning(false);
        _cancellation?.Cancel();
        _listener?.Close();
        _frames.Clear();
        _signalFrames.Clear();
    }
}
