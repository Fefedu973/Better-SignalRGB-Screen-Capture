using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Models;

namespace Better_SignalRGB_Screen_Capture.Services;

public class CaptureService : ICaptureService
{
    private readonly ConcurrentDictionary<Guid, CaptureSession> _captureSessions = new();
    private int _frameRate = 30;

    public event EventHandler<SourceFrameEventArgs>? FrameAvailable;

    public Task StartCaptureAsync(SourceItem source)
    {
        if (_captureSessions.ContainsKey(source.Id))
        {
            Debug.WriteLine($"Capture already running for source {source.Id}");
            return Task.CompletedTask;
        }

        try
        {
            var session = new CaptureSession(source);
            _captureSessions[source.Id] = session;

            Debug.WriteLine($"Starting capture for {source.Type} source: {source.Name}");
            
            // Start snapshot generation for live preview
            _ = Task.Run(() => GenerateSnapshotsAsync(session));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to start capture for source {source.Id}: {ex.Message}");
            throw;
        }

        return Task.CompletedTask;
    }

    public async Task StopCaptureAsync(SourceItem source)
    {
        if (!_captureSessions.TryRemove(source.Id, out var session))
        {
            Debug.WriteLine($"No capture session found for source {source.Id}");
            return;
        }

        try
        {
            session.IsActive = false;
            Debug.WriteLine($"Stopped capture for source {source.Id}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error stopping capture for source {source.Id}: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    public async Task StopAllCapturesAsync()
    {
        var tasks = _captureSessions.Keys.Select(async sourceId =>
        {
            var source = _captureSessions[sourceId].Source;
            await StopCaptureAsync(source);
        });

        await Task.WhenAll(tasks);
    }

    public bool IsCapturing(SourceItem source)
    {
        return _captureSessions.ContainsKey(source.Id);
    }

    public void SetFrameRate(int fps)
    {
        _frameRate = Math.Max(1, Math.Min(60, fps));
        Debug.WriteLine($"Frame rate set to {_frameRate} FPS");
    }

    public byte[]? GetMjpegFrame()
    {
        // Return a test frame for now
        try
        {
            using var bitmap = new System.Drawing.Bitmap(800, 600, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            
            // Create a simple test pattern
            graphics.Clear(System.Drawing.Color.Black);
            graphics.DrawString("Live Preview", new System.Drawing.Font("Arial", 24), 
                              System.Drawing.Brushes.White, 10, 10);
            graphics.DrawString($"Time: {DateTime.Now:HH:mm:ss}", new System.Drawing.Font("Arial", 16), 
                              System.Drawing.Brushes.White, 10, 50);

            using var stream = new MemoryStream();
            var jpegEncoder = GetJpegEncoder();
            var encoderParams = new System.Drawing.Imaging.EncoderParameters(1);
            encoderParams.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
            
            bitmap.Save(stream, jpegEncoder, encoderParams);
            return stream.ToArray();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to generate test frame: {ex.Message}");
            return null;
        }
    }

    private async Task GenerateSnapshotsAsync(CaptureSession session)
    {
        var frameInterval = TimeSpan.FromMilliseconds(1000.0 / _frameRate);
        
        while (session.IsActive)
        {
            try
            {
                // Generate a test frame for now
                var frameData = GenerateTestFrame(session.Source);
                if (frameData != null)
                {
                    // Create a dummy SoftwareBitmap for the event args
                    var dummyBitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 800, 600);
                    
                    var args = new SourceFrameEventArgs(session.Source, dummyBitmap)
                    {
                        FrameData = frameData
                    };
                    
                    FrameAvailable?.Invoke(this, args);
                    
                    // Dispose the bitmap after use
                    dummyBitmap.Dispose();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error generating snapshot for source {session.Source.Id}: {ex.Message}");
            }

            await Task.Delay(frameInterval);
        }
    }

    private byte[] GenerateTestFrame(SourceItem source)
    {
        try
        {
            using var bitmap = new System.Drawing.Bitmap(800, 600, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            
            // Generate different colors for different source types
            var backgroundColor = source.Type switch
            {
                SourceType.Monitor => System.Drawing.Color.DarkBlue,
                SourceType.Process => System.Drawing.Color.DarkGreen,
                SourceType.Region => System.Drawing.Color.DarkRed,
                SourceType.Webcam => System.Drawing.Color.DarkMagenta,
                SourceType.Website => System.Drawing.Color.DarkOrange,
                _ => System.Drawing.Color.Black
            };

            graphics.Clear(backgroundColor);
            graphics.DrawString($"{source.Type}: {source.Name}", 
                              new System.Drawing.Font("Arial", 20), 
                              System.Drawing.Brushes.White, 10, 10);
            graphics.DrawString($"Time: {DateTime.Now:HH:mm:ss.fff}", 
                              new System.Drawing.Font("Arial", 14), 
                              System.Drawing.Brushes.White, 10, 40);

            using var stream = new MemoryStream();
            var jpegEncoder = GetJpegEncoder();
            var encoderParams = new System.Drawing.Imaging.EncoderParameters(1);
            encoderParams.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
            
            bitmap.Save(stream, jpegEncoder, encoderParams);
            return stream.ToArray();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to generate test frame: {ex.Message}");
            return Array.Empty<byte>();
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

    private record CaptureSession(SourceItem Source)
    {
        public bool IsActive { get; set; } = true;
    }
} 