using Better_SignalRGB_Screen_Capture.Models;

namespace Better_SignalRGB_Screen_Capture.Contracts.Services;

/// <summary>Thread-safe counters. UI reads immutable snapshots; producers never dispatch UI events.</summary>
public interface IPipelineDiagnosticsService
{
    ICaptureDiagnosticsSession BeginCapture(Guid sourceId, string name, CaptureEncoderKind encoder,
        int requestedFrameRate, int width, int height);
    void RecordSignalRgbFrame(Guid sourceId, int jpegBytes, double elapsedMilliseconds);
    void RecordSignalRgbError(string error);
    void SetSignalRgbRunning(bool running);
    void RemoveSource(Guid sourceId);
    PipelineDiagnosticsSnapshot GetSnapshot();
}

public interface ICaptureDiagnosticsSession
{
    void Received();
    void Produced(int jpegBytes, double processingMilliseconds);
    void Dropped();
    void Skipped();
    void SetFrameRate(int frameRate);
    /// <summary>Update when the capture backend or its verified color conversion changes.</summary>
    void SetColorInfo(CaptureColorInfo info);
    void Error(string error, bool terminal = false);
    void Stop();
}
