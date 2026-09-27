using Better_SignalRGB_Screen_Capture.Helpers;
using System.Globalization;

namespace BetterSignalRGB.RegressionTests;

internal static class WebsitePreviewTests
{
    public static void Run()
    {
        var state = new WebsitePreviewState();
        var first = state.BeginInitialization();
        var replacement = state.BeginInitialization();
        Assert.True(!state.IsInitializationCurrent(first) && state.IsInitializationCurrent(replacement), "A replacement invalidates the previous asynchronous initialization");

        state.RequestedUrl = "https://example.test/first";
        state.NavigationStarted(1);
        Assert.True(state.IsInitializationCurrent(replacement), "An old page navigating does not cancel replacement browser initialization");
        state.NavigationStarted(2);
        Assert.True(!state.CompleteNavigation(1, true), "Late completion cannot mark a different navigation ready");
        Assert.True(!state.Ready, "A page stays unavailable until its own navigation completes");
        Assert.True(state.CompleteNavigation(2, true) && state.Ready, "Current navigation becomes ready after applying zoom");
        var frameGeneration = state.Generation;
        state.NavigationStarted(3);
        Assert.True(!state.IsCurrent(frameGeneration), "Navigation invalidates an in-flight JPEG");
        state.CompleteNavigation(3, false, "Host unavailable");
        Assert.Equal("Host unavailable", state.Error, "Navigation failures are available to capture diagnostics");
        Assert.True(!state.Ready && state.RequestedUrl == null, "Failure permits retry when reopening the editor");
        frameGeneration = state.Generation;
        state.RequestNavigation("https://example.test/retry");
        Assert.True(state.Error == null && !state.Ready && !state.IsCurrent(frameGeneration), "Requesting a retry clears the old error before the browser raises NavigationStarting");
        Assert.True(!state.CompleteNavigation(3, true), "A prior navigation cannot complete a requested retry");
        state.NavigationStarted(4);
        Assert.True(state.Error == null, "A retry clears the previous failure");
        state.CompleteNavigation(4, true);
        Assert.True(state.Ready, "A successful retry recovers the preview");
        frameGeneration = state.Generation;
        state.Reset();
        Assert.True(!state.IsCurrent(frameGeneration) && !state.Ready && state.RequestedUrl == null, "Delete/unload invalidates frames and page state");
        state.FailInitialization("Runtime unavailable");
        Assert.Equal("Runtime unavailable", state.Error, "Initialization failures are not silently treated as empty frames");

        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal("document.documentElement.style.zoom='1.25';", WebsitePreviewState.ZoomScript(1.25), "Zoom is locale independent");
            Assert.Equal("document.documentElement.style.zoom='1';", WebsitePreviewState.ZoomScript(double.NaN), "Invalid zoom resets to normal");
            Assert.Equal("document.documentElement.style.zoom='4';", WebsitePreviewState.ZoomScript(99), "Zoom obeys source bounds");
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
    }
}
