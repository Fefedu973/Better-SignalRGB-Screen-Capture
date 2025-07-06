using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.ViewModels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Better_SignalRGB_Screen_Capture.Services;

public class KestrelApiService : IKestrelApiService
{
    private IHost? _host;
    private int _port;
    private readonly ICaptureService _captureService;
    private readonly ConcurrentDictionary<Guid, byte[]> _sourceFrames = new();
    private readonly object _frameLock = new();

    public event EventHandler<string>? StreamingUrlChanged;

    public bool IsRunning => _host != null;
    public string? StreamingUrl { get; private set; }

    public KestrelApiService(ICaptureService captureService)
    {
        _captureService = captureService;
        _captureService.FrameAvailable += OnFrameAvailable;
    }

    private void OnFrameAvailable(object? sender, SourceFrameEventArgs e)
    {
        if (e.FrameData != null)
        {
            lock (_frameLock)
            {
                _sourceFrames[e.Source.Id] = e.FrameData;
            }
        }
    }

    public async Task StartAsync(int httpsPort = 8443)
    {
        if (IsRunning)
            return;

        _port = httpsPort;

        var builder = WebApplication.CreateBuilder();
        
        // Add CORS service
        builder.Services.AddCors();
        
        // Configure Kestrel for HTTPS
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenLocalhost(httpsPort, listenOptions =>
            {
                // Use mkcert certificate for trusted localhost HTTPS
                var certificatePath = Path.Combine(AppContext.BaseDirectory, "localhost.pfx");
                if (File.Exists(certificatePath))
                {
                    listenOptions.UseHttps(certificatePath, ""); // Empty password
                }
                else
                {
                    // Fallback to development certificate
                    listenOptions.UseHttps();
                }
            });
        });

        // Minimal logging to reduce console spam
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        var app = builder.Build();

        // Enable CORS for browser requests
        app.UseCors(policy => policy
            .AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader());

        // API endpoints
        app.MapGet("/api/canvasinfo", HandleCanvasInfoApi);
        app.MapGet("/api/sources", HandleSourcesApi);
        app.MapGet("/stream/{sourceId}", HandleSourceStream);
        app.MapGet("/canvas", HandleCanvasPage);
        app.MapGet("/", HandleCanvasPage);

        _host = app;

        try
        {
            await _host.StartAsync();
            StreamingUrl = $"https://localhost:{httpsPort}/canvas/";
            StreamingUrlChanged?.Invoke(this, StreamingUrl);
        }
        catch (Exception)
        {
            await StopAsync();
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
            _host = null;
        }

        _sourceFrames.Clear();
        StreamingUrl = null;
        StreamingUrlChanged?.Invoke(this, string.Empty);
    }

    private async Task HandleCanvasInfoApi(HttpContext context)
    {
        context.Response.ContentType = "application/json";
        context.Response.Headers.Add("Access-Control-Allow-Origin", "*");

        try
        {
            // Get canvas size and sources
            var mainViewModel = App.GetService<MainViewModel>();
            var sources = mainViewModel?.Sources ?? new System.Collections.ObjectModel.ObservableCollection<Models.SourceItem>();

            var sourcesDto = sources.Reverse().Select(s => new {
                id = s.Id,
                type = s.Type.ToString(),
                websiteUrl = s.WebsiteUrl ?? string.Empty,
                canvasX = s.CanvasX,
                canvasY = s.CanvasY,
                canvasWidth = s.CanvasWidth,
                canvasHeight = s.CanvasHeight,
                rotation = s.Rotation,
                opacity = s.Opacity,
                isMirroredHorizontally = s.IsMirroredHorizontally,
                isMirroredVertically = s.IsMirroredVertically,
                cropLeftPct = s.CropLeftPct,
                cropTopPct = s.CropTopPct,
                cropRightPct = s.CropRightPct,
                cropBottomPct = s.CropBottomPct,
                cropRotation = s.CropRotation
            }).ToList();

            var payload = new {
                canvasWidth = 800,
                canvasHeight = 600,
                sources = sourcesDto
            };

            await context.Response.WriteAsync(JsonSerializer.Serialize(payload));
        }
        catch (Exception)
        {
            context.Response.StatusCode = 500;
        }
    }

    private async Task HandleSourcesApi(HttpContext context)
    {
        context.Response.ContentType = "application/json";
        context.Response.Headers.Add("Access-Control-Allow-Origin", "*");

        try
        {
            var mainViewModel = App.GetService<MainViewModel>();
            var sources = mainViewModel?.Sources ?? new System.Collections.ObjectModel.ObservableCollection<Models.SourceItem>();

            var sourcesDto = sources.Reverse().Select(s => new {
                id = s.Id,
                type = s.Type.ToString(),
                websiteUrl = s.WebsiteUrl ?? string.Empty,
                canvasX = s.CanvasX,
                canvasY = s.CanvasY,
                canvasWidth = s.CanvasWidth,
                canvasHeight = s.CanvasHeight,
                rotation = s.Rotation,
                opacity = s.Opacity,
                isMirroredHorizontally = s.IsMirroredHorizontally,
                isMirroredVertically = s.IsMirroredVertically,
                cropLeftPct = s.CropLeftPct,
                cropTopPct = s.CropTopPct,
                cropRightPct = s.CropRightPct,
                cropBottomPct = s.CropBottomPct,
                cropRotation = s.CropRotation
            }).ToList();

            await context.Response.WriteAsync(JsonSerializer.Serialize(sourcesDto));
        }
        catch (Exception)
        {
            context.Response.StatusCode = 500;
        }
    }

    private async Task HandleSourceStream(HttpContext context)
    {
        var sourceIdString = context.Request.RouteValues["sourceId"]?.ToString();
        if (!Guid.TryParse(sourceIdString, out var sourceId))
        {
            context.Response.StatusCode = 404;
            return;
        }

        context.Response.ContentType = "multipart/x-mixed-replace; boundary=--mjpegboundary";
        context.Response.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
        context.Response.Headers.Add("Pragma", "no-cache");
        context.Response.Headers.Add("Expires", "0");
        context.Response.Headers.Add("Connection", "keep-alive");

        var boundary = Encoding.UTF8.GetBytes("\r\n--mjpegboundary\r\n");
        await context.Response.Body.WriteAsync(boundary);

        try
        {
            while (!context.RequestAborted.IsCancellationRequested)
            {
                byte[]? frameData;
                lock (_frameLock)
                {
                    _sourceFrames.TryGetValue(sourceId, out frameData);
                }

                if (frameData != null)
                {
                    var headers = Encoding.UTF8.GetBytes(
                        $"Content-Type: image/jpeg\r\n" +
                        $"Content-Length: {frameData.Length}\r\n\r\n");

                    await context.Response.Body.WriteAsync(headers);
                    await context.Response.Body.WriteAsync(frameData);
                    await context.Response.Body.WriteAsync(boundary);
                    await context.Response.Body.FlushAsync();
                }
                else
                {
                    // Get frame from capture service
                    var mjpegFrame = _captureService.GetMjpegFrame(sourceId);
                    if (mjpegFrame != null)
                    {
                        var headers = Encoding.UTF8.GetBytes(
                            $"Content-Type: image/jpeg\r\n" +
                            $"Content-Length: {mjpegFrame.Length}\r\n\r\n");

                        await context.Response.Body.WriteAsync(headers);
                        await context.Response.Body.WriteAsync(mjpegFrame);
                        await context.Response.Body.WriteAsync(boundary);
                        await context.Response.Body.FlushAsync();
                    }
                }

                await Task.Delay(33, context.RequestAborted); // ~30 FPS
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected, normal
        }
        catch (Exception)
        {
            // Other errors, log if needed
        }
    }

    private async Task HandleCanvasPage(HttpContext context)
    {
        context.Response.ContentType = "text/html";

        try
        {
            // Get HTML from the original MjpegStreamingService logic
            var mjpegService = App.GetService<IMjpegStreamingService>();
            
            // Generate similar HTML but with HTTPS URLs
            var html = GenerateCanvasPageHtml();
            await context.Response.WriteAsync(html);
        }
        catch (Exception)
        {
            context.Response.StatusCode = 500;
        }
    }

    private string GenerateCanvasPageHtml()
    {
        // Get all sources from MainViewModel
        var mainViewModel = App.GetService<MainViewModel>();
        var sources = mainViewModel?.Sources ?? new System.Collections.ObjectModel.ObservableCollection<Models.SourceItem>();
        
        var sourcesHtml = new StringBuilder();
        
        // Render sources in reverse order
        foreach (var source in sources.Reverse())
        {
            var cropStyle = "";
            if (source.CropLeftPct > 0 || source.CropTopPct > 0 || source.CropRightPct > 0 || source.CropBottomPct > 0)
            {
                var clipLeft = source.CropLeftPct * 100;
                var clipTop = source.CropTopPct * 100;
                var clipRight = 100 - (source.CropRightPct * 100);
                var clipBottom = 100 - (source.CropBottomPct * 100);
                cropStyle = $"clip-path: polygon({clipLeft}% {clipTop}%, {clipRight}% {clipTop}%, {clipRight}% {clipBottom}%, {clipLeft}% {clipBottom}%);";
            }
            
            var transform = "";
            if (source.Rotation != 0 || source.IsMirroredHorizontally || source.IsMirroredVertically)
            {
                var scaleX = source.IsMirroredHorizontally ? -1 : 1;
                var scaleY = source.IsMirroredVertically ? -1 : 1;
                transform = $"transform: rotate({source.Rotation}deg) scale({scaleX}, {scaleY});";
            }
            
            var style = $"left: {source.CanvasX}px; top: {source.CanvasY}px; width: {source.CanvasWidth}px; height: {source.CanvasHeight}px; opacity: {source.Opacity.ToString(System.Globalization.CultureInfo.InvariantCulture)}; {transform} {cropStyle}";
            
            if (source.Type == Models.SourceType.Website && !string.IsNullOrEmpty(source.WebsiteUrl))
            {
                sourcesHtml.AppendLine($@"
        <div class='source' id='source-{source.Id}' style='{style}'>
            <iframe src='{source.WebsiteUrl}' sandbox='allow-scripts allow-same-origin'></iframe>
        </div>");
            }
            else
            {
                sourcesHtml.AppendLine($@"
        <div class='source' id='source-{source.Id}' style='{style}'>
            <img src='/stream/{source.Id}' onerror='this.style.display=""none""' onload='this.style.display=""block""'>
        </div>");
            }
        }
        
        return $@"
<!DOCTYPE html>
<html>
<head>
    <title>Signal RGB Screen Capture Canvas (HTTPS)</title>
    <meta charset='utf-8'>
    <style>
        body {{ 
            margin: 0; 
            padding: 20px; 
            background: #000; 
            font-family: Arial, sans-serif;
        }}
        .canvas {{ 
            position: relative; 
            width: 800px; 
            height: 600px; 
            background: #222; 
            margin: 0 auto;
            border: 2px solid #444;
            overflow: hidden;
        }}
        .source {{ 
            position: absolute; 
            transform-origin: center center;
            overflow: hidden;
        }}
        .source img {{ 
            width: 100%; 
            height: 100%; 
            object-fit: fill;
            display: block;
        }}
        .source iframe {{ 
            width: 100%; 
            height: 100%; 
            border: none;
        }}
        .info {{
            text-align: center;
            color: #ccc;
            margin-top: 20px;
        }}
    </style>
</head>
<body>
    <div class='canvas' id='canvas'>
{sourcesHtml}
    </div>
    <div class='info'>
        <p>Canvas: 800x600px | Sources: {sources.Count} | HTTPS Enabled</p>
        <p>Refresh page to update layout</p>
    </div>
</body>
</html>";
    }
}