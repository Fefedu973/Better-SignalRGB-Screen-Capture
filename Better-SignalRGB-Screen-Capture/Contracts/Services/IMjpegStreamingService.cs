using System;
using System.Threading.Tasks;

namespace Better_SignalRGB_Screen_Capture.Contracts.Services;

public interface IMjpegStreamingService
{
    event EventHandler<string>? StreamingUrlChanged;
    
    Task StartStreamingAsync(int port = 8080);
    Task StopStreamingAsync();
    
    void UpdateSourceFrame(Guid sourceId, byte[] jpegData);
    
    bool IsStreaming { get; }
    string? StreamingUrl { get; }
    
    Task NotifySourceRemovedAsync(Guid sourceId);
    
    //void EnableSignalRgbApi(bool enabled);
} 