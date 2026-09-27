using Better_SignalRGB_Screen_Capture.Contracts.Services;

namespace Better_SignalRGB_Screen_Capture.Services;

internal sealed class StreamingCompositeSession : IDisposable
{
    private readonly ICompositeFrameService _composite;
    public StreamingFrameState Frames { get; } = new();

    public StreamingCompositeSession(ICompositeFrameService composite)
    {
        _composite = composite;
        composite.CompositeFrameAvailable += OnFrame;
        if (composite.GetLatestCompositeFrame() is { } frame) Frames.Publish(frame);
    }

    private void OnFrame(object? sender, byte[] frame) => Frames.Publish(frame);
    public void Dispose()
    {
        _composite.CompositeFrameAvailable -= OnFrame;
        Frames.Close();
    }
}
