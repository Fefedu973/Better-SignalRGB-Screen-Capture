namespace Better_SignalRGB_Screen_Capture.Contracts.Services;

/// <summary>A browser surface that produces immutable encoded images (JPEG or lossless PNG).
/// Calls are made on its owning UI dispatcher.</summary>
public interface IWebsiteFrameSource
{
    /// <summary>Initializes the browser before its first capture.</summary>
    Task PrepareCaptureAsync() => Task.CompletedTask;
    Task<byte[]?> CaptureFrameAsync();

    /// <summary>Called on the UI dispatcher when capture releases this surface.
    /// A capture-owned host closes its browser and native window.</summary>
    void CaptureStopped() { }
}
