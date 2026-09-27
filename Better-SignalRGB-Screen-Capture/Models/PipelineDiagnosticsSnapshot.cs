namespace Better_SignalRGB_Screen_Capture.Models;

public enum CaptureEncoderKind { Hardware, Software, Website, Wallpaper, WindowsGraphicsCapture }
public enum CaptureDiagnosticState { Capturing, Stopped, Failed }

/// <summary>Describes the producer's verified conversion, not the connected display's capability.</summary>
public enum CaptureColorMode { Unknown, Sdr, HdrToneMapped, HdrUnsupported }

public sealed record CaptureColorInfo(CaptureColorMode Mode, string Backend, double? SdrWhiteNits = null)
{
    public static CaptureColorInfo Unknown { get; } = new(CaptureColorMode.Unknown, "Unreported backend");

    public string Description => Mode switch
    {
        CaptureColorMode.Sdr => "SDR output",
        CaptureColorMode.HdrToneMapped => "HDR → SDR tone-mapped",
        // This is a backend limitation; it does not claim that the source currently contains HDR.
        CaptureColorMode.HdrUnsupported => "HDR tone mapping unavailable",
        _ => "Color conversion not reported"
    };

    internal CaptureColorInfo Normalize()
    {
        var backend = string.IsNullOrWhiteSpace(Backend) ? Unknown.Backend : Backend.Trim();
        backend = backend.Replace('\r', ' ').Replace('\n', ' ');
        if (backend.Length > 128) backend = backend[..128];
        var mode = Enum.IsDefined(Mode) ? Mode : CaptureColorMode.Unknown;
        var white = SdrWhiteNits is { } value && double.IsFinite(value) && value is > 0 and <= 10000 ? SdrWhiteNits : null;
        return this with { Mode = mode, Backend = backend, SdrWhiteNits = white };
    }
}

public sealed record SourceDiagnosticsSnapshot(
    Guid SourceId, string Name, CaptureEncoderKind Encoder, CaptureDiagnosticState State,
    int RequestedFrameRate, int Width, int Height, long ReceivedFrames, long ProducedFrames,
    long DroppedFrames, long SkippedCaptures, long Errors, long ProducedBytes, long SentFrames,
    double CaptureFramesPerSecond, double SentFramesPerSecond, double MeanProcessingMilliseconds,
    double MeanSendMilliseconds, DateTimeOffset? LastFrameAt, string? LastError)
{
    public CaptureColorInfo ColorInfo { get; init; } = CaptureColorInfo.Unknown;
}

public sealed record SignalRgbTransportDiagnosticsSnapshot(
    bool Running, long SentFrames, long SentJpegBytes, long Errors, double FramesPerSecond,
    double MeanSendMilliseconds, DateTimeOffset? LastSendAt, string? LastError);

public sealed record PipelineDiagnosticsSnapshot(
    IReadOnlyList<SourceDiagnosticsSnapshot> Sources, SignalRgbTransportDiagnosticsSnapshot SignalRgb);
