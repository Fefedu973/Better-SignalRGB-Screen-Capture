using Better_SignalRGB_Screen_Capture.Models;

namespace Better_SignalRGB_Screen_Capture.Contracts.Services;

public interface IGraphicsCaptureFactory
{
    IGraphicsFrameCapture Create(SourceItem source, int width, int height, int frameRate);
}

public interface IGraphicsFrameCapture
{
    /// <summary>The receiver owns each ArrayPool byte buffer once onFrame returns successfully.</summary>
    Task RunAsync(Action<byte[], int, int> onFrame, Action<CaptureColorInfo> onColor, CancellationToken cancellationToken);
}
