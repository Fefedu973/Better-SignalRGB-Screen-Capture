using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.ViewModels;
using System.Collections.Generic;
using System.Net.Http;
using System.Globalization;
using System.Diagnostics;

namespace Better_SignalRGB_Screen_Capture.Services;

public class MjpegStreamingService : IMjpegStreamingService
{
    private HttpListener? _httpListener;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly ConcurrentDictionary<Guid, ConcurrentBag<Stream>> _sourceClientStreams = new();
    private readonly ConcurrentDictionary<Guid, byte[]> _sourceFrames = new();
    private readonly object _frameLock = new();
    private int _port;
    private readonly ICaptureService _captureService;

    // Canvas API Constants
    private const string CanvasApiUrl = "http://localhost:16034/canvas/event";
    private const string SenderTag = "BetterSignalRGBScreenCapture";
    private const int ChunkSize = 6144; // 6KB chunks for optimal performance
    private const int FpsLimit = 15; // 15 FPS per source to prevent overwhelming SignalRGB

    // Canvas API Infrastructure
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(4)
    };
    private readonly ConcurrentDictionary<Guid, long> _lastSentTicks = new(); // Rate limiting per source

    public event EventHandler<string>? StreamingUrlChanged;

    public bool IsStreaming => _httpListener?.IsListening == true;
    public string? StreamingUrl { get; private set; }

    public MjpegStreamingService(ICaptureService captureService)
    {
        _captureService = captureService;
        _captureService.FrameAvailable += OnFrameAvailable;
    }

    private void OnFrameAvailable(object? sender, SourceFrameEventArgs e)
    {
        // Store the frame data for the specific source and send Canvas API style information
        if (e.FrameData != null && e.Source?.Id != null)
        {
            UpdateSourceFrame(e.Source.Id, e.FrameData);
            // Send to Canvas API with rate limiting
            var nowTicks = DateTime.UtcNow.Ticks;
            var lastSent = _lastSentTicks.GetOrAdd(e.Source.Id, 0);
            if (nowTicks - lastSent >= TimeSpan.TicksPerSecond / FpsLimit)
            {
                _lastSentTicks[e.Source.Id] = nowTicks;
                _ = Task.Run(() => SendFrameToCanvasApiAsync(e.Source, e.FrameData));
            }
        }
    }

    private async Task SendFrameToCanvasApiAsync(SourceItem source, byte[] jpegData)
    {
        try
        {
            var mainViewModel = App.GetService<MainViewModel>();
            var sources = mainViewModel?.Sources;
            var sourceIndex = sources?.IndexOf(source) ?? -1;
            var zIndex = (sources != null && sourceIndex != -1) ? sources.Count - 1 - sourceIndex : 0;
            
            // Build separated styles for proper crop rectangle handling
            var outerStyle = BuildOuterStyleString(source, zIndex);
            var cropStyle = BuildCropStyleString(source);
            var combinedStyle = $"outer:{outerStyle}|crop:{cropStyle}";
            
            // Convert to base64 and split into chunks
            var base64Data = Convert.ToBase64String(jpegData);
            var chunks = new List<string>();
            for (int i = 0; i < base64Data.Length; i += ChunkSize)
            {
                var chunkSize = Math.Min(ChunkSize, base64Data.Length - i);
                chunks.Add(base64Data.Substring(i, chunkSize));
            }
            var fileName = $"{source.Id}.jpg";
            // Send header with separated style information
            var header = $"header:{fileName}:image/jpeg:{chunks.Count}:{combinedStyle}";
            await PostCanvasEventAsync(header);
            // Send data chunks
            for (int i = 0; i < chunks.Count; i++)
            {
                await PostCanvasEventAsync($"data:{source.Id}:{i}:{chunks[i]}");
            }
            // Send end marker
            await PostCanvasEventAsync($"end:{source.Id}");
            Debug.WriteLine($"[Canvas API] Sent frame for {source.Name} ({chunks.Count} chunks, z-index: {zIndex})");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Canvas API] Error sending frame for {source.Name}: {ex.Message}");
        }
    }

    private static async Task PostCanvasEventAsync(string eventData)
    {
        try
        {
            var url = $"{CanvasApiUrl}?sender={Uri.EscapeDataString(SenderTag)}&event={Uri.EscapeDataString(eventData)}";
            var response = await _httpClient.PostAsync(url, new StringContent(string.Empty));
            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"[Canvas API] HTTP error: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Canvas API] Network error: {ex.Message}");
        }
    }

    private string BuildOuterStyleString(SourceItem source, int zIndex = 0)
    {
        var styles = new List<string>();
        
        // Position and size (always applied to outer element)
        styles.Add($"position: absolute");
        styles.Add($"left: {source.CanvasX}px");
        styles.Add($"top: {source.CanvasY}px");
        styles.Add($"width: {source.CanvasWidth}px");
        styles.Add($"height: {source.CanvasHeight}px");
        styles.Add($"opacity: {source.Opacity.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        styles.Add($"z-index: {zIndex}");
        styles.Add($"transform-origin: center center");
        
        // Build transform (rotation + mirroring, but NO crop)
        var transformParts = new List<string>();
        
        // Rotation
        if (source.Rotation != 0)
        {
            transformParts.Add($"rotate({source.Rotation}deg)");
        }
        
        // Mirroring
        if (source.IsMirroredHorizontally || source.IsMirroredVertically)
        {
            var scaleX = source.IsMirroredHorizontally ? -1 : 1;
            var scaleY = source.IsMirroredVertically ? -1 : 1;
            transformParts.Add($"scale({scaleX}, {scaleY})");
        }
        
        if (transformParts.Count > 0)
        {
            styles.Add($"transform: {string.Join(" ", transformParts)}");
        }
        
        return string.Join("; ", styles);
    }

    private string BuildCropStyleString(SourceItem source)
    {
        // ── base styles ────────────────────────────────────────────────────────────
        var styles = new List<string>
        {
            "position: relative",
            "width: 100%",
            "height: 100%",
            "overflow: hidden"
        };

        // ── rectangular crop (always inset) ───────────────────────────────────────
        double leftPct   = source.CropLeftPct   * 100.0;
        double topPct    = source.CropTopPct    * 100.0;
        double rightPct  = source.CropRightPct  * 100.0;
        double bottomPct = source.CropBottomPct * 100.0;

        styles.Add(
            $"clip-path: inset({topPct.ToString(CultureInfo.InvariantCulture)}% " +
                           $"{rightPct.ToString(CultureInfo.InvariantCulture)}% " +
                           $"{bottomPct.ToString(CultureInfo.InvariantCulture)}% " +
                           $"{leftPct.ToString(CultureInfo.InvariantCulture)}%)");

        // ── optional rotation with aspect-ratio preserving scale ──────────────────
        double rotation = source.CropRotation % 360;
        if (rotation != 0)
        {
            double w = 100.0 - leftPct - rightPct;   // crop width  (% of element)
            double h = 100.0 - topPct  - bottomPct;  // crop height (% of element)

            double rad = Math.Abs(rotation) * Math.PI / 180.0;
            double cos = Math.Cos(rad);
            double sin = Math.Sin(rad);

            // Bounding box after rotation
            double bboxW = w * cos + h * sin;
            double bboxH = w * sin + h * cos;

            // Scale so that the bounding box matches the original size again
            double scaleX = w / bboxW;
            double scaleY = h / bboxH;

            styles.Add(
                $"transform: rotate({rotation.ToString(CultureInfo.InvariantCulture)}deg) " +
                $"scale({scaleX.ToString(CultureInfo.InvariantCulture)}, {scaleY.ToString(CultureInfo.InvariantCulture)})");
            styles.Add("transform-origin: center center");
        }

        return string.Join("; ", styles);
    }

    public async Task StartStreamingAsync(int port = 8080)
    {
        if (IsStreaming)
            return;

        _port = port;
        _httpListener = new HttpListener();
        
        // Add prefixes for individual source streams and canvas page
        _httpListener.Prefixes.Add($"http://localhost:{port}/stream/");
        _httpListener.Prefixes.Add($"http://localhost:{port}/canvas/");
        _httpListener.Prefixes.Add($"http://localhost:{port}/");
        
        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            _httpListener.Start();
            StreamingUrl = $"http://localhost:{port}/canvas/";
            StreamingUrlChanged?.Invoke(this, StreamingUrl);

            // Start handling requests
            _ = Task.Run(() => HandleRequestsAsync(_cancellationTokenSource.Token));
        }
        catch (Exception)
        {
            await StopStreamingAsync();
            throw;
        }
    }

    public async Task StopStreamingAsync()
    {
        _cancellationTokenSource?.Cancel();
        
        if (_httpListener?.IsListening == true)
        {
            _httpListener.Stop();
        }

        // Close all client streams for all sources
        foreach (var sourceStreams in _sourceClientStreams.Values)
        {
            foreach (var stream in sourceStreams)
        {
            try
            {
                stream.Close();
            }
            catch { /* Ignore */ }
        }
        }
        _sourceClientStreams.Clear();
        _sourceFrames.Clear();
         _lastSentTicks.Clear();

        _httpListener?.Close();
        _httpListener = null;
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
        
        StreamingUrl = null;
        StreamingUrlChanged?.Invoke(this, string.Empty);

        await Task.CompletedTask;
    }



    public void UpdateSourceFrame(Guid sourceId, byte[] jpegData)
    {
        lock (_frameLock)
        {
            _sourceFrames[sourceId] = jpegData;
        }

        // Removed BroadcastSourceFrame call to prevent double-writing corruption
        // The HandleSourceStreamAsync loop will pick up the new frame automatically
    }

    private async Task HandleRequestsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _httpListener?.IsListening == true)
        {
            try
            {
                var context = await _httpListener.GetContextAsync();
                _ = Task.Run(() => HandleClientAsync(context, cancellationToken), cancellationToken);
            }
            catch (ObjectDisposedException)
            {
                // HttpListener was disposed
                break;
            }
            catch (HttpListenerException)
            {
                // HttpListener was stopped
                break;
            }
            catch (Exception)
            {
                // Other errors, continue
                continue;
            }
        }
    }

    private async Task HandleClientAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var response = context.Response;
        var path = request.Url?.AbsolutePath ?? "";
        
        try
        {
            if (path.StartsWith("/stream/"))
            {
                // Handle individual source stream request
                var sourceIdString = path.Substring("/stream/".Length);
                if (Guid.TryParse(sourceIdString, out var sourceId))
                {
                    await HandleSourceStreamAsync(context, sourceId, cancellationToken);
                }
                else
                {
                    response.StatusCode = 404;
                    response.Close();
                }
            }
            else if (path.StartsWith("/canvas") || path == "/")
            {
                // Handle canvas page request
                await HandleCanvasPageAsync(context, cancellationToken);
            }
            else if (path == "/api/sources")
            {
                // Handle API request for sources data
                await HandleSourcesApiAsync(context, cancellationToken);
            }
            else
            {
                response.StatusCode = 404;
                response.Close();
            }
        }
        catch (Exception)
        {
            // Client disconnected or other error
            try
            {
                response.Close();
            }
            catch { /* Ignore */ }
        }
    }

    private async Task HandleSourceStreamAsync(HttpListenerContext context, Guid sourceId, CancellationToken cancellationToken)
    {
        var response = context.Response;
        
        try
        {
            // Set MJPEG headers (Python script compatible format)
            response.ContentType = "multipart/x-mixed-replace; boundary=\"mjpegboundary\"";
            response.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
            response.Headers.Add("Pragma", "no-cache");
            response.Headers.Add("Expires", "0");
            response.Headers.Add("Connection", "keep-alive");

            var outputStream = response.OutputStream;
            
            // Add to source-specific client streams
            if (!_sourceClientStreams.ContainsKey(sourceId))
            {
                _sourceClientStreams[sourceId] = new ConcurrentBag<Stream>();
            }
            _sourceClientStreams[sourceId].Add(outputStream);

            // Proper MJPEG format: boundary before headers, not after data
            var boundaryBytes = Encoding.UTF8.GetBytes("--mjpegboundary\r\n");

            // Send an initial frame immediately to prevent the Python script from hanging
            byte[]? initialFrame;
            lock (_frameLock)
            {
                _sourceFrames.TryGetValue(sourceId, out initialFrame);
            }
            
            // Fallback to capture service if no frame in cache yet
            if (initialFrame == null)
            {
                initialFrame = _captureService.GetMjpegFrame(sourceId);
            }
            
            // Final fallback to black frame if nothing is available
            if (initialFrame == null)
            {
                initialFrame = CreateBlackFrame320x200();
            }
            
            if (initialFrame != null)
            {
                await WriteJpegChunk(outputStream, boundaryBytes, initialFrame, cancellationToken);
            }

            // Keep connection alive and send frames only when they change
            byte[]? lastSentFrame = null;
            while (!cancellationToken.IsCancellationRequested && outputStream.CanWrite)
            {
                byte[]? frameData;
                lock (_frameLock)
                {
                    _sourceFrames.TryGetValue(sourceId, out frameData);
                }

                // Only send frame if it's different from the last one sent
                if (frameData != null && !ReferenceEquals(frameData, lastSentFrame))
                {
                    try
                    {
                        await WriteJpegChunk(outputStream, boundaryBytes, frameData, cancellationToken);
                        lastSentFrame = frameData;
                    }
                    catch
                    {
                        // Client disconnected
                        break;
                    }
                }

                // Wait before checking for next frame - optimized for minimal latency
                await Task.Delay(16, cancellationToken); // ~60 FPS for lower latency
            }
        }
        catch (Exception)
        {
            // Client disconnected or other error
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch { /* Ignore */ }
        }
    }

    private byte[]? CreateBlackFrame320x200()
    {
        try
        {
            using var bitmap = new System.Drawing.Bitmap(320, 200, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.Clear(System.Drawing.Color.Black);
            
            using var stream = new MemoryStream();
            var jpegEncoder = GetJpegEncoder();
            var encoderParams = new System.Drawing.Imaging.EncoderParameters(1);
            encoderParams.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 20L); // Very aggressive compression for streaming
            
            bitmap.Save(stream, jpegEncoder, encoderParams);
            return stream.ToArray();
        }
        catch
        {
            // Fallback to minimal JPEG
            return new byte[]
            {
                0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x01, 0x00, 0x48,
                0x00, 0x48, 0x00, 0x00, 0xFF, 0xDB, 0x00, 0x43, 0x00, 0x08, 0x06, 0x06, 0x07, 0x06, 0x05, 0x08,
                0x07, 0x07, 0x07, 0x09, 0x09, 0x08, 0x0A, 0x0C, 0x14, 0x0D, 0x0C, 0x0B, 0x0B, 0x0C, 0x19, 0x12,
                0x13, 0x0F, 0x14, 0x1D, 0x1A, 0x1F, 0x1E, 0x1D, 0x1A, 0x1C, 0x1C, 0x20, 0x24, 0x2E, 0x27, 0x20,
                0x22, 0x2C, 0x23, 0x1C, 0x1C, 0x28, 0x37, 0x29, 0x2C, 0x30, 0x31, 0x34, 0x34, 0x34, 0x1F, 0x27,
                0x39, 0x3D, 0x38, 0x32, 0x3C, 0x2E, 0x33, 0x34, 0x32, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x01,
                0x00, 0x01, 0x01, 0x01, 0x11, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01, 0xFF, 0xC4, 0x00, 0x14,
                0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x08, 0xFF, 0xC4, 0x00, 0x14, 0x10, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xDA, 0x00, 0x0C, 0x03, 0x01, 0x00, 0x02,
                0x11, 0x03, 0x11, 0x00, 0x3F, 0x00, 0x00, 0xFF, 0xD9
            };
        }
    }
    
    private static System.Drawing.Imaging.ImageCodecInfo GetJpegEncoder()
    {
        var codecs = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders();
        foreach (var codec in codecs)
        {
            if (codec.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid)
                return codec;
        }
        throw new Exception("JPEG encoder not found");
    }

    private async Task HandleCanvasPageAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var response = context.Response;
        
        try
        {
            response.ContentType = "text/html";
            var html = GenerateCanvasPageHtml();
            var buffer = Encoding.UTF8.GetBytes(html);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length, cancellationToken);
        }
        catch (Exception)
        {
            // Error sending response
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch { /* Ignore */ }
        }
    }

    private string GenerateCanvasPageHtml()
    {
        // Get all sources from MainViewModel
        var mainViewModel = App.GetService<MainViewModel>();
        var sources = mainViewModel?.Sources ?? new System.Collections.ObjectModel.ObservableCollection<Models.SourceItem>();
        
        var sourcesHtml = new System.Text.StringBuilder();
        var sourcesScript = new System.Text.StringBuilder();
        
        // Render sources in reverse order so that items at the top of the list (index 0) appear on top
        foreach (var source in sources.Reverse())
        {
            // Calculate crop mask if needed
            var cropStyle = "";
            if (source.CropLeftPct > 0 || source.CropTopPct > 0 || source.CropRightPct > 0 || source.CropBottomPct > 0)
            {
                var clipLeft = source.CropLeftPct * 100;
                var clipTop = source.CropTopPct * 100;
                var clipRight = 100 - (source.CropRightPct * 100);
                var clipBottom = 100 - (source.CropBottomPct * 100);
                cropStyle = $"clip-path: polygon({clipLeft}% {clipTop}%, {clipRight}% {clipTop}%, {clipRight}% {clipBottom}%, {clipLeft}% {clipBottom}%);";
            }
            
            // Calculate transform
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
            
            // Add live update script for this source
            sourcesScript.AppendLine($@"
        updateSource('{source.Id}', {{
            x: {source.CanvasX}, y: {source.CanvasY}, 
            width: {source.CanvasWidth}, height: {source.CanvasHeight},
            rotation: {source.Rotation}, opacity: {source.Opacity.ToString(System.Globalization.CultureInfo.InvariantCulture)},
            mirrorH: {(source.IsMirroredHorizontally ? "true" : "false")}, 
            mirrorV: {(source.IsMirroredVertically ? "true" : "false")},
            cropLeft: {source.CropLeftPct.ToString(System.Globalization.CultureInfo.InvariantCulture)}, cropTop: {source.CropTopPct.ToString(System.Globalization.CultureInfo.InvariantCulture)},
            cropRight: {source.CropRightPct.ToString(System.Globalization.CultureInfo.InvariantCulture)}, cropBottom: {source.CropBottomPct.ToString(System.Globalization.CultureInfo.InvariantCulture)},
            cropRotation: {source.CropRotation}
        }});");
        }
        
        return $@"
<!DOCTYPE html>
<html>
<head>
    <title>Signal RGB Screen Capture Canvas</title>
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
            width: 320px; 
            height: 200px; 
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
        <p>Canvas: 320x200px | Sources: {sources.Count}</p>
        <p>Refresh page to update layout</p>
    </div>
    <script>
        const sources = {{}};
        
        function updateSource(id, props) {{
            sources[id] = props;
            const elem = document.getElementById('source-' + id);
            if (!elem) return;
            
            // Update position and size
            elem.style.left = props.x + 'px';
            elem.style.top = props.y + 'px';
            elem.style.width = props.width + 'px';
            elem.style.height = props.height + 'px';
            elem.style.opacity = props.opacity;
            
            // Update transform
            const scaleX = props.mirrorH ? -1 : 1;
            const scaleY = props.mirrorV ? -1 : 1;
            elem.style.transform = `rotate(${{props.rotation}}deg) scale(${{scaleX}}, ${{scaleY}})`;
            
            // Update crop with rotation support
            if (props.cropLeft > 0 || props.cropTop > 0 || props.cropRight > 0 || props.cropBottom > 0) {{
                const clipLeft = props.cropLeft * 100;
                const clipTop = props.cropTop * 100;
                const clipRight = props.cropRight * 100;
                const clipBottom = props.cropBottom * 100;
                
                // Apply rectangular inset clipping (always)
                elem.style.clipPath = `inset(${{clipTop}}% ${{clipRight}}% ${{clipBottom}}% ${{clipLeft}}%)`;
                
                // Handle rotation with compensating scale
                const rot = props.cropRotation || 0;
                const img = elem.querySelector('img, iframe');
                
                if (rot % 360 !== 0 && img) {{
                    // Calculate compensating scale (same as C#)
                    const w = 100 - (props.cropLeft + props.cropRight) * 100;
                    const h = 100 - (props.cropTop + props.cropBottom) * 100;
                    
                    const rad = Math.abs(rot) * Math.PI / 180;
                    const cos = Math.cos(rad);
                    const sin = Math.sin(rad);
                    
                    const bboxW = w * cos + h * sin;
                    const bboxH = w * sin + h * cos;
                    
                    const scaleX = w / bboxW;
                    const scaleY = h / bboxH;
                    
                    // Create wrapper for the crop transformation
                    let cropWrapper = elem.querySelector('.crop-wrapper');
                    if (!cropWrapper) {{
                        cropWrapper = document.createElement('div');
                        cropWrapper.className = 'crop-wrapper';
                        cropWrapper.style.width = '100%';
                        cropWrapper.style.height = '100%';
                        cropWrapper.style.position = 'relative';
                        cropWrapper.style.overflow = 'hidden';
                        img.parentNode.insertBefore(cropWrapper, img);
                        cropWrapper.appendChild(img);
                    }}
                    
                    // Apply rotation and compensating scale to wrapper
                    cropWrapper.style.transform = `rotate(${{rot}}deg) scale(${{scaleX}}, ${{scaleY}})`;
                    cropWrapper.style.transformOrigin = 'center center';
                    
                    // Counter-transform the media to keep it upright and unscaled
                    img.style.transform = `rotate(${{-rot}}deg) scale(${{1/scaleX}}, ${{1/scaleY}})`;
                    img.style.transformOrigin = 'center center';
                    img.style.position = 'absolute';
                    img.style.top = '0';
                    img.style.left = '0';
                    img.style.width = '100%';
                    img.style.height = '100%';
                }}
            }}
        }}
        
        // Initialize sources
{sourcesScript}
        
        // Poll for updates every 2 seconds
        let updateInterval = setInterval(async () => {{
            try {{
                const response = await fetch('/api/sources');
                if (response.ok) {{
                    const newSources = await response.json();
                    updateSourcesFromData(newSources);
                }}
            }} catch (error) {{
                console.error('Failed to fetch updates:', error);
            }}
        }}, 2000);
        
        function updateSourcesFromData(sourcesData) {{
            // Remove sources that no longer exist
            const existingIds = new Set(Object.keys(sources));
            const newIds = new Set(sourcesData.map(s => s.id));
            
            existingIds.forEach(id => {{
                if (!newIds.has(id)) {{
                    const elem = document.getElementById('source-' + id);
                    if (elem) elem.remove();
                    delete sources[id];
                }}
            }});
            
            // Update or add sources
            sourcesData.forEach(sourceData => {{
                const existingElem = document.getElementById('source-' + sourceData.id);
                
                if (!existingElem) {{
                    // Create new element
                    const newElem = document.createElement('div');
                    newElem.className = 'source';
                    newElem.id = 'source-' + sourceData.id;
                    
                    if (sourceData.type === 'Website' && sourceData.websiteUrl) {{
                        newElem.innerHTML = '<iframe src=""' + sourceData.websiteUrl + '"" sandbox=""allow-scripts allow-same-origin""></iframe>';
                    }} else {{
                        var img = document.createElement('img');
                        img.src = '/stream/' + sourceData.id;
                        img.onerror = function() {{ this.style.display = 'none'; }};
                        img.onload = function() {{ this.style.display = 'block'; }};
                        newElem.appendChild(img);
                    }}
                    
                    document.getElementById('canvas').appendChild(newElem);
                }}
                
                // Update source properties
                updateSource(sourceData.id, {{
                    x: sourceData.canvasX,
                    y: sourceData.canvasY,
                    width: sourceData.canvasWidth,
                    height: sourceData.canvasHeight,
                    rotation: sourceData.rotation,
                    opacity: sourceData.opacity,
                    mirrorH: sourceData.isMirroredHorizontally,
                    mirrorV: sourceData.isMirroredVertically,
                    cropLeft: sourceData.cropLeftPct,
                    cropTop: sourceData.cropTopPct,
                    cropRight: sourceData.cropRightPct,
                    cropBottom: sourceData.cropBottomPct,
                    cropRotation: sourceData.cropRotation
                }});
            }});
        }}
    </script>
</body>
</html>";
    }

    private async Task HandleSourcesApiAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var response = context.Response;
        
        try
        {
            response.ContentType = "application/json";
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            
            // Get all sources from MainViewModel
            var mainViewModel = App.GetService<MainViewModel>();
            var sources = mainViewModel?.Sources ?? new System.Collections.ObjectModel.ObservableCollection<Models.SourceItem>();
            
            // Create JSON array of sources
            var json = "[";
            var first = true;
            foreach (var source in sources.Reverse()) // Keep reverse order for z-index
            {
                if (!first) json += ",";
                first = false;
                
                json += $@"{{
                    ""id"": ""{source.Id}"",
                    ""type"": ""{source.Type}"",
                    ""websiteUrl"": ""{source.WebsiteUrl ?? ""}"",
                    ""canvasX"": {source.CanvasX},
                    ""canvasY"": {source.CanvasY},
                    ""canvasWidth"": {source.CanvasWidth},
                    ""canvasHeight"": {source.CanvasHeight},
                    ""rotation"": {source.Rotation},
                    ""opacity"": {source.Opacity.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                    ""isMirroredHorizontally"": {(source.IsMirroredHorizontally ? "true" : "false")},
                    ""isMirroredVertically"": {(source.IsMirroredVertically ? "true" : "false")},
                    ""cropLeftPct"": {source.CropLeftPct.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                    ""cropTopPct"": {source.CropTopPct.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                    ""cropRightPct"": {source.CropRightPct.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                    ""cropBottomPct"": {source.CropBottomPct.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                    ""cropRotation"": {source.CropRotation}
                }}";
            }
            json += "]";
            
            var buffer = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length, cancellationToken);
        }
        catch (Exception)
        {
            // Error sending response
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch { /* Ignore */ }
        }
    }



    private async Task WriteJpegChunk(Stream outputStream, byte[] boundaryBytes, byte[] frameData, CancellationToken cancellationToken)
    {
        // Send boundary first
        await outputStream.WriteAsync(boundaryBytes, 0, boundaryBytes.Length, cancellationToken);
        
        // Then headers
        var headers = Encoding.UTF8.GetBytes(
            $"Content-Type: image/jpeg\r\n" +
            $"Content-Length: {frameData.Length}\r\n\r\n");
        
        await outputStream.WriteAsync(headers, 0, headers.Length, cancellationToken);
        
        // Then JPEG data
        await outputStream.WriteAsync(frameData, 0, frameData.Length, cancellationToken);
        
        // End with CRLF before next boundary
        await outputStream.WriteAsync(Encoding.UTF8.GetBytes("\r\n"), 0, 2, cancellationToken);
        await outputStream.FlushAsync(cancellationToken);
    }

    public async Task NotifySourceRemovedAsync(Guid sourceId)
    {
        try
        {
            // Send Canvas API remove event
            await PostCanvasEventAsync($"remove:{sourceId}");
            Debug.WriteLine($"[Canvas API] Sent remove for {sourceId}");
            
            // Clean up local storage
            lock (_frameLock)
            {
                _sourceFrames.TryRemove(sourceId, out _);
            }
            
            // Remove from rate limiting
            _lastSentTicks.TryRemove(sourceId, out _);
            
            // Close any streams for this source
            if (_sourceClientStreams.TryRemove(sourceId, out var streams))
            {
                foreach (var stream in streams)
                {
                    try
                    {
                        stream.Close();
                    }
                    catch { /* Ignore cleanup errors */ }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Canvas API] Error notifying source removal: {ex.Message}");
        }
    }
} 