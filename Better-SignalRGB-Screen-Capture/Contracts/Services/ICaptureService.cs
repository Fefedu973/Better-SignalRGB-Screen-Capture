using System;
using System.Threading.Tasks;
using Better_SignalRGB_Screen_Capture.Models;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Better_SignalRGB_Screen_Capture.Contracts.Services;

public interface ICaptureService
{
    /// <summary>Raised on the producer thread. Consumers must marshal UI updates themselves.</summary>
    event EventHandler<SourceFrameEventArgs>? FrameAvailable;
    /// <summary>Raised on the producer thread when a capture cannot continue.</summary>
    event EventHandler<CaptureFailedEventArgs>? CaptureFailed;
    
    Task StartCaptureAsync(SourceItem source);
    Task StopCaptureAsync(SourceItem source);
    Task StopAllCapturesAsync();
    
    bool IsCapturing(SourceItem source);
    Task SetFrameRate(int fps);
    /// <summary>Changes capture detail for subsequent sessions; all current sessions must be stopped.</summary>
    Task SetHighQuality(bool enabled);
    /// <summary>Canonical source-sized JPEG for the SignalRGB Canvas API, regardless of preview quality.</summary>
    byte[]? GetLatestSignalRgbFrame(Guid sourceId);
    byte[]? GetMjpegFrame(Guid sourceId);
    byte[]? GetMjpegFrame();
}

public sealed class CaptureFailedEventArgs(SourceItem source, string error) : EventArgs
{
    public SourceItem Source { get; } = source;
    public string Error { get; } = error;
}

public class SourceFrameEventArgs : EventArgs
{
    public SourceItem Source { get; }
    public BitmapImage? FrameImage { get; }
    /// <summary>Immutable JPEG data owned by the event publisher; consumers must not modify it.</summary>
    public byte[]? FrameData { get; set; }
    
    public SourceFrameEventArgs(SourceItem source, BitmapImage? frameImage)
    {
        Source = source;
        FrameImage = frameImage;
    }
}
