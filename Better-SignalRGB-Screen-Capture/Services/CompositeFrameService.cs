using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Models;

namespace Better_SignalRGB_Screen_Capture.Services;

public class CompositeFrameService : ICompositeFrameService
{
    private readonly ConcurrentDictionary<Guid, SourceFrameData> _sourceFrames = new();
    private readonly object _compositeLock = new();
    private int _canvasWidth = 800;
    private int _canvasHeight = 600;
    private byte[]? _latestCompositeFrame;

    public event EventHandler<byte[]>? CompositeFrameAvailable;

    public void UpdateSourceFrame(SourceItem source, byte[] frameData)
    {
        _sourceFrames.AddOrUpdate(source.Id, 
            new SourceFrameData(source, frameData),
            (key, existing) => new SourceFrameData(source, frameData));

        GenerateCompositeFrame();
    }

    public void RemoveSource(SourceItem source)
    {
        _sourceFrames.TryRemove(source.Id, out _);
        GenerateCompositeFrame();
    }

    public void SetCanvasSize(int width, int height)
    {
        _canvasWidth = width;
        _canvasHeight = height;
        GenerateCompositeFrame();
    }

    public byte[]? GetLatestCompositeFrame()
    {
        lock (_compositeLock)
        {
            return _latestCompositeFrame;
        }
    }

    private void GenerateCompositeFrame()
    {
        try
        {
            using var compositeBitmap = new Bitmap(_canvasWidth, _canvasHeight, PixelFormat.Format24bppRgb);
            using var graphics = Graphics.FromImage(compositeBitmap);
            
            // Fill with black background
            graphics.Clear(Color.Black);
            
            // Set high-quality rendering
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;

            // Draw each source frame
            foreach (var sourceFrame in _sourceFrames.Values)
            {
                try
                {
                    using var sourceImage = Image.FromStream(new MemoryStream(sourceFrame.FrameData));
                    
                    // Calculate destination rectangle on canvas
                    var destRect = new Rectangle(
                        sourceFrame.Source.CanvasX,
                        sourceFrame.Source.CanvasY,
                        sourceFrame.Source.CanvasWidth,
                        sourceFrame.Source.CanvasHeight);

                    // Ensure destination rectangle is within canvas bounds
                    destRect.Intersect(new Rectangle(0, 0, _canvasWidth, _canvasHeight));

                    if (destRect.Width > 0 && destRect.Height > 0)
                    {
                        graphics.DrawImage(sourceImage, destRect);
                    }
                }
                catch
                {
                    // Skip invalid frames
                    continue;
                }
            }

            // Convert to JPEG
            using var outputStream = new MemoryStream();
            var jpegEncoder = GetJpegEncoder();
            var encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 85L); // High quality

            compositeBitmap.Save(outputStream, jpegEncoder, encoderParams);
            var compositeBytes = outputStream.ToArray();

            lock (_compositeLock)
            {
                _latestCompositeFrame = compositeBytes;
            }

            CompositeFrameAvailable?.Invoke(this, compositeBytes);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to generate composite frame: {ex.Message}");
        }
    }

    private static ImageCodecInfo GetJpegEncoder()
    {
        var codecs = ImageCodecInfo.GetImageEncoders();
        foreach (var codec in codecs)
        {
            if (codec.FormatID == ImageFormat.Jpeg.Guid)
                return codec;
        }
        throw new Exception("JPEG encoder not found");
    }

    private record SourceFrameData(SourceItem Source, byte[] FrameData);
} 