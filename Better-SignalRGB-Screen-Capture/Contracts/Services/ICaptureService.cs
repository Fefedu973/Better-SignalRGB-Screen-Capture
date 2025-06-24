using System;
using System.Threading.Tasks;
using Better_SignalRGB_Screen_Capture.Models;
using Windows.Graphics.Imaging;

namespace Better_SignalRGB_Screen_Capture.Contracts.Services;

public interface ICaptureService
{
    event EventHandler<SourceFrameEventArgs>? FrameAvailable;
    
    Task StartCaptureAsync(SourceItem source);
    Task StopCaptureAsync(SourceItem source);
    Task StopAllCapturesAsync();
    
    bool IsCapturing(SourceItem source);
    void SetFrameRate(int fps);
    byte[]? GetMjpegFrame();
}

public class SourceFrameEventArgs : EventArgs
{
    public SourceItem Source { get; }
    public SoftwareBitmap Frame { get; }
    public byte[]? FrameData { get; set; }
    
    public SourceFrameEventArgs(SourceItem source, SoftwareBitmap frame)
    {
        Source = source;
        Frame = frame;
    }
} 