namespace Better_SignalRGB_Screen_Capture.Models;

public enum CaptureEncoderKind { Hardware, Software, Website, Wallpaper }
public enum CaptureDiagnosticState { Capturing, Stopped, Failed }

public sealed record SourceDiagnosticsSnapshot(
    Guid SourceId, string Name, CaptureEncoderKind Encoder, CaptureDiagnosticState State,
    int RequestedFrameRate, int Width, int Height, long ReceivedFrames, long ProducedFrames,
    long DroppedFrames, long SkippedCaptures, long Errors, long ProducedBytes, long SentFrames,
    double CaptureFramesPerSecond, double SentFramesPerSecond, double MeanProcessingMilliseconds,
    double MeanSendMilliseconds, DateTimeOffset? LastFrameAt, string? LastError);

public sealed record SignalRgbTransportDiagnosticsSnapshot(
    bool Running, long SentFrames, long SentJpegBytes, long Errors, double FramesPerSecond,
    double MeanSendMilliseconds, DateTimeOffset? LastSendAt, string? LastError);

public sealed record PipelineDiagnosticsSnapshot(
    IReadOnlyList<SourceDiagnosticsSnapshot> Sources, SignalRgbTransportDiagnosticsSnapshot SignalRgb);
