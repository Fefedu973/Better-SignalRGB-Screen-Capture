using Better_SignalRGB_Screen_Capture.Models;

namespace Better_SignalRGB_Screen_Capture.Contracts.Services;

/// <summary>Creates a capture-owned browser that does not depend on a loaded editor.</summary>
public interface IWebsiteCaptureHostFactory
{
    /// <summary>Called on the UI dispatcher. Each returned host belongs to one capture session.</summary>
    IWebsiteFrameSource Create(SourceItem source, bool highQuality = false);
}
