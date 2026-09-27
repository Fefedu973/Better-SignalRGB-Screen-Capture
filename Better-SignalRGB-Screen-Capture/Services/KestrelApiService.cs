using System.Diagnostics;
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Better_SignalRGB_Screen_Capture.Services;

public sealed class KestrelApiService : IKestrelApiService, IDisposable
{
    private readonly ICaptureService _captureService;
    private readonly ICompositeFrameService _compositeService;
    private readonly ISignalRgbEffectSettingsService? _effectSettingsService;
    private readonly StreamingSourceFrames _frames = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private WebApplication? _host;
    private CancellationTokenSource? _cancellation;
    private volatile bool _isRunning;
    private bool _disposed;

    public event EventHandler<string>? StreamingUrlChanged;
    public bool IsRunning => _isRunning;
    public string? StreamingUrl { get; private set; }

    public KestrelApiService(ICaptureService captureService, ICompositeFrameService compositeService,
        ISignalRgbEffectSettingsService? effectSettings = null)
    {
        _captureService = captureService;
        _compositeService = compositeService;
        _effectSettingsService = effectSettings;
        _captureService.FrameAvailable += OnFrameAvailable;
        _captureService.CaptureFailed += OnCaptureFailed;
    }

    private void OnFrameAvailable(object? sender, SourceFrameEventArgs e)
    {
        if (e.FrameData is not { Length: > 0 } frame) return;
        if (_isRunning) _frames.Publish(e.Source.Id, frame);
        else _frames.Recover(e.Source.Id);
    }

    private void OnCaptureFailed(object? sender, CaptureFailedEventArgs e) => RemoveSource(e.Source.Id);

    public async Task StartAsync(int httpsPort = 8443)
    {
        if (httpsPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(httpsPort));
        await _lifecycle.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_isRunning) return;
            if (_effectSettingsService != null)
            {
                try { await _effectSettingsService.InitializeAsync(); }
                catch (Exception exception) { Debug.WriteLine($"Could not load web effect appearance: {exception.Message}"); }
            }
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>(), ContentRootPath = AppContext.BaseDirectory });
            builder.Services.AddCors();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(httpsPort, listener =>
            {
                var certificate = Path.Combine(AppContext.BaseDirectory, "localhost.pfx");
                if (File.Exists(certificate)) listener.UseHttps(certificate, string.Empty);
                else listener.UseHttps();
            }));
            var app = builder.Build();
            app.UseCors(policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
            app.MapGet("/api/canvasinfo", context => HandleLayoutAsync(context, includeCanvas: true));
            app.MapGet("/api/sources", context => HandleLayoutAsync(context, includeCanvas: false));
            app.MapGet("/stream/{sourceId:guid}", HandleSourceStreamAsync);
            app.MapGet("/stream", HandleSourceStreamAsync);
            app.MapGet("/web-stream", HandleWebStreamAsync);
            app.MapGet("/canvas", HandleCanvasPageAsync);
            app.MapGet("/", HandleCanvasPageAsync);
            _cancellation = new CancellationTokenSource();
            try { await app.StartAsync().ConfigureAwait(false); }
            catch { await app.DisposeAsync(); _cancellation.Dispose(); _cancellation = null; throw; }
            _host = app;
            _isRunning = true;
            StreamingUrl = $"https://localhost:{httpsPort}/canvas/";
            StreamingUrlChanged?.Invoke(this, StreamingUrl);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            _isRunning = false;
            _cancellation?.Cancel();
            _frames.CloseStreams();
            if (_host != null)
            {
                try { await _host.StopAsync().ConfigureAwait(false); }
                finally { await _host.DisposeAsync(); _host = null; }
            }
            _frames.Clear();
            _cancellation?.Dispose();
            _cancellation = null;
            StreamingUrl = null;
            StreamingUrlChanged?.Invoke(this, string.Empty);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task HandleLayoutAsync(HttpContext context, bool includeCanvas)
    {
        context.Response.Headers.CacheControl = "no-store";
        var sources = _frames.FilterAvailable(await StreamingCanvasSnapshot.CaptureAsync(context.RequestAborted));
        if (includeCanvas)
            await context.Response.WriteAsJsonAsync(new { canvasWidth = StreamingCanvasSnapshot.Width, canvasHeight = StreamingCanvasSnapshot.Height, sources }, context.RequestAborted);
        else await context.Response.WriteAsJsonAsync(sources, context.RequestAborted);
    }

    private async Task HandleSourceStreamAsync(HttpContext context)
    {
        var isComposite = !context.Request.RouteValues.ContainsKey("sourceId");
        var id = Guid.Empty;
        if (!isComposite && !Guid.TryParse(context.Request.RouteValues["sourceId"]?.ToString(), out id))
        { context.Response.StatusCode = 404; return; }
        using var compositeSession = isComposite ? new StreamingCompositeSession(_compositeService) : null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _cancellation?.Token ?? new CancellationToken(true));
        var token = cancellation.Token;
        try
        {
            StreamingFrameState state;
            if (compositeSession != null) state = compositeSession.Frames;
            else
            {
                var sources = await StreamingCanvasSnapshot.CaptureAsync(token);
                if (!sources.Any(source => source.Id == id)) { context.Response.StatusCode = 404; return; }
                var available = _frames.Open(id, _captureService.GetMjpegFrame);
                if (available == null)
                {
                    context.Response.StatusCode = 503;
                    context.Response.Headers.RetryAfter = "1";
                    return;
                }
                state = available;
            }
            context.Response.ContentType = StreamingMultipartWriter.ContentType;
            context.Response.Headers.CacheControl = "no-store";
            byte[]? previous = null;
            while (await state.WaitForNextAsync(previous, token).ConfigureAwait(false) is { } frame)
            {
                using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                writeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                await StreamingMultipartWriter.WriteAsync(context.Response.Body, frame, writeTimeout.Token).ConfigureAwait(false);
                previous = frame;
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        { /* Normal stream cancellation/disconnection. */ }
        catch (Exception exception) { Debug.WriteLine($"HTTPS stream failed: {exception.Message}"); }
    }

    private async Task HandleWebStreamAsync(HttpContext context)
    {
        context.Response.ContentType = StreamingWebSession.ContentType;
        context.Response.Headers.CacheControl = "no-store";
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted,
            _cancellation?.Token ?? new CancellationToken(true));
        try
        {
            await StreamingWebSession.WriteAsync(context.Response.Body, _frames, _captureService,
                _compositeService, _effectSettingsService, cancellation.Token,
                preview: context.Request.Query["preview"] == "1").ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException or TimeoutException)
        { /* Stream disconnected, stopped, or exceeded its bounded protocol. */ }
        catch (Exception exception) { Debug.WriteLine($"HTTPS web stream failed: {exception.Message}"); }
    }

    private static Task HandleCanvasPageAsync(HttpContext context)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsync(StreamingCanvasPage.Html, context.RequestAborted);
    }

    public void RemoveSource(Guid id)
    {
        _frames.Invalidate(id);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _isRunning = false;
        _captureService.FrameAvailable -= OnFrameAvailable;
        _captureService.CaptureFailed -= OnCaptureFailed;
        _cancellation?.Cancel();
        _frames.Clear();
        // App shutdown awaits StopAsync before its service provider is disposed.
    }
}
