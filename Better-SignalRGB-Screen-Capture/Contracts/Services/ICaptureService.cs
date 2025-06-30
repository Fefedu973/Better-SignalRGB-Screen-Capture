using System;
using System.Threading.Tasks;
using Better_SignalRGB_Screen_Capture.Models;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Better_SignalRGB_Screen_Capture.Contracts.Services;

public interface ICaptureService
{
    event EventHandler<SourceFrameEventArgs>? FrameAvailable;
    
    Task StartCaptureAsync(SourceItem source);
    Task StopCaptureAsync(SourceItem source);
    Task StopAllCapturesAsync();
    
    bool IsCapturing(SourceItem source);
    Task SetFrameRate(int fps);
    byte[]? GetMjpegFrame(Guid sourceId);
    byte[]? GetMjpegFrame();
}

public class SourceFrameEventArgs : EventArgs
{
    public SourceItem Source { get; }
    public BitmapImage FrameImage { get; }
    public byte[]? FrameData { get; set; }
    
    public SourceFrameEventArgs(SourceItem source, BitmapImage frameImage)
    {
        Source = source;
        FrameImage = frameImage;
    }
} 