using System;
using System.Threading.Tasks;

namespace Better_SignalRGB_Screen_Capture.Contracts.Services;

public interface IKestrelApiService
{
    event EventHandler<string>? StreamingUrlChanged;
    
    Task StartAsync(int httpsPort = 8443);
    Task StopAsync();
    
    bool IsRunning { get; }
    string? StreamingUrl { get; }
}