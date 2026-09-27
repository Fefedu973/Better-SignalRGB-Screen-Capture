using Better_SignalRGB_Screen_Capture.Models;

namespace Better_SignalRGB_Screen_Capture.Contracts.Services;

public interface ISignalRgbConnectionService
{
    SignalRgbConnectionSnapshot Snapshot { get; }
    string BeginSession(int port);
    void EndSession();
    void ApiSucceeded();
    void ApiFailed(string error);
    void FrameSent();
    bool ReceiveFeedback(string? session, long renderedFrames, int version);
    Task ProbeAsync(CancellationToken cancellationToken = default);
}
