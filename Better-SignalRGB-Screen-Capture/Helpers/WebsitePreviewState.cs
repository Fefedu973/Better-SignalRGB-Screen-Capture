using System.Globalization;

namespace Better_SignalRGB_Screen_Capture.Helpers;

/// <summary>UI-thread state for asynchronous browser initialization, navigation and capture.</summary>
internal sealed class WebsitePreviewState
{
    private int _initializationGeneration;
    public int Generation { get; private set; }
    public ulong NavigationId { get; private set; }
    public string? RequestedUrl { get; set; }
    public bool Ready { get; private set; }
    public string? Error { get; private set; }

    public int BeginInitialization() => ++_initializationGeneration;
    public bool IsInitializationCurrent(int generation) => generation == _initializationGeneration;
    public bool IsCurrent(int generation) => generation == Generation;

    public void Reset()
    {
        Generation++;
        _initializationGeneration++;
        NavigationId = 0;
        RequestedUrl = null;
        Ready = false;
        Error = null;
    }

    public void NavigationStarted(ulong id)
    {
        Generation++;
        NavigationId = id;
        Ready = false;
        Error = null;
    }

    public void RequestNavigation(string url)
    {
        // Navigate() raises NavigationStarting later. Clear the old error and invalidate
        // old frames immediately so a retry is not rejected before that event arrives.
        Generation++;
        NavigationId = 0;
        RequestedUrl = url;
        Ready = false;
        Error = null;
    }

    public bool CompleteNavigation(ulong id, bool success, string? error = null)
    {
        if (id != NavigationId) return false;
        Ready = success;
        Error = success ? null : error ?? "The website could not be loaded.";
        if (!success) RequestedUrl = null; // Permit retry when the editor is reopened.
        return true;
    }

    public void FailInitialization(string error)
    {
        Generation++;
        Ready = false;
        Error = error;
        RequestedUrl = null;
    }

    public static string ZoomScript(double zoom) =>
        $"document.documentElement.style.zoom='{(double.IsFinite(zoom) ? Math.Clamp(zoom, .25, 4) : 1).ToString("0.##", CultureInfo.InvariantCulture)}';";
}
