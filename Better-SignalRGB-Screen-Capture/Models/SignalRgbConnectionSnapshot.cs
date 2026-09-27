namespace Better_SignalRGB_Screen_Capture.Models;

public sealed record SignalRgbConnectionSnapshot(
    bool IsStreaming, bool ApiReachable, bool EffectResponding, bool FramesConfirmed,
    long FramesSent, long RenderedFrames, double SourceUpdatesPerSecond, double RenderFramesPerSecond,
    string Message, string? LastError);
