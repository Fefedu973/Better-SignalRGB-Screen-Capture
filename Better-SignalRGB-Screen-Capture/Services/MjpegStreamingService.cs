using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Better_SignalRGB_Screen_Capture.Contracts.Services;

namespace Better_SignalRGB_Screen_Capture.Services;

public class MjpegStreamingService : IMjpegStreamingService
{
    private HttpListener? _httpListener;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly ConcurrentBag<Stream> _clientStreams = new();
    private readonly object _frameLock = new();
    private byte[]? _currentFrame;
    private int _port;
    private readonly ICaptureService _captureService;

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
        // Update the current frame with the latest data
        if (e.FrameData != null)
        {
            UpdateFrame(e.FrameData);
        }
    }

    public async Task StartStreamingAsync(int port = 8080)
    {
        if (IsStreaming)
            return;

        _port = port;
        _httpListener = new HttpListener();
        _httpListener.Prefixes.Add($"http://localhost:{port}/stream/");
        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            _httpListener.Start();
            StreamingUrl = $"http://localhost:{port}/stream/";
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

        // Close all client streams
        foreach (var stream in _clientStreams)
        {
            try
            {
                stream.Close();
            }
            catch { /* Ignore */ }
        }

        _httpListener?.Close();
        _httpListener = null;
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
        
        StreamingUrl = null;
        StreamingUrlChanged?.Invoke(this, string.Empty);

        await Task.CompletedTask;
    }

    public void UpdateFrame(byte[] jpegData)
    {
        lock (_frameLock)
        {
            _currentFrame = jpegData;
        }

        // Send frame to all connected clients
        _ = Task.Run(() => BroadcastFrame(jpegData));
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
        var response = context.Response;
        
        try
        {
            // Set MJPEG headers
            response.ContentType = "multipart/x-mixed-replace; boundary=--mjpegboundary";
            response.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
            response.Headers.Add("Pragma", "no-cache");
            response.Headers.Add("Expires", "0");
            response.Headers.Add("Connection", "keep-alive");

            var outputStream = response.OutputStream;
            _clientStreams.Add(outputStream);

            // Send initial boundary
            var boundary = Encoding.UTF8.GetBytes("\r\n--mjpegboundary\r\n");
            await outputStream.WriteAsync(boundary, 0, boundary.Length, cancellationToken);

            // Keep connection alive and send frames
            while (!cancellationToken.IsCancellationRequested && outputStream.CanWrite)
            {
                byte[]? frameData;
                lock (_frameLock)
                {
                    frameData = _currentFrame;
                }

                if (frameData != null)
                {
                    try
                    {
                        // Send frame headers
                        var headers = Encoding.UTF8.GetBytes(
                            $"Content-Type: image/jpeg\r\n" +
                            $"Content-Length: {frameData.Length}\r\n\r\n");
                        
                        await outputStream.WriteAsync(headers, 0, headers.Length, cancellationToken);
                        await outputStream.WriteAsync(frameData, 0, frameData.Length, cancellationToken);
                        await outputStream.WriteAsync(boundary, 0, boundary.Length, cancellationToken);
                        await outputStream.FlushAsync(cancellationToken);
                    }
                    catch
                    {
                        // Client disconnected
                        break;
                    }
                }
                else
                {
                    // Get a frame from the capture service
                    var mjpegFrame = _captureService.GetMjpegFrame();
                    if (mjpegFrame != null)
                    {
                        try
                        {
                            var headers = Encoding.UTF8.GetBytes(
                                $"Content-Type: image/jpeg\r\n" +
                                $"Content-Length: {mjpegFrame.Length}\r\n\r\n");
                            
                            await outputStream.WriteAsync(headers, 0, headers.Length, cancellationToken);
                            await outputStream.WriteAsync(mjpegFrame, 0, mjpegFrame.Length, cancellationToken);
                            await outputStream.WriteAsync(boundary, 0, boundary.Length, cancellationToken);
                            await outputStream.FlushAsync(cancellationToken);
                        }
                        catch
                        {
                            break;
                        }
                    }
                }

                // Wait a bit before sending next frame
                await Task.Delay(33, cancellationToken); // ~30 FPS
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

    private byte[]? CreateBlackFrame800x600()
    {
        try
        {
            using var bitmap = new System.Drawing.Bitmap(800, 600, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.Clear(System.Drawing.Color.Black);
            
            using var stream = new MemoryStream();
            var jpegEncoder = GetJpegEncoder();
            var encoderParams = new System.Drawing.Imaging.EncoderParameters(1);
            encoderParams.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
            
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

    private async Task BroadcastFrame(byte[] frameData)
    {
        var boundary = Encoding.UTF8.GetBytes("\r\n--mjpegboundary\r\n");
        var headers = Encoding.UTF8.GetBytes(
            $"Content-Type: image/jpeg\r\n" +
            $"Content-Length: {frameData.Length}\r\n\r\n");

        var streamsToRemove = new List<Stream>();

        foreach (var stream in _clientStreams)
        {
            try
            {
                if (stream.CanWrite)
                {
                    await stream.WriteAsync(headers, 0, headers.Length);
                    await stream.WriteAsync(frameData, 0, frameData.Length);
                    await stream.WriteAsync(boundary, 0, boundary.Length);
                    await stream.FlushAsync();
                }
                else
                {
                    streamsToRemove.Add(stream);
                }
            }
            catch
            {
                // Stream is broken, mark for removal
                streamsToRemove.Add(stream);
            }
        }

        // Remove broken streams
        foreach (var stream in streamsToRemove)
        {
            try
            {
                stream.Close();
            }
            catch { /* Ignore */ }
        }
    }
} 