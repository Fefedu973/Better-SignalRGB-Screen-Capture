using Better_SignalRGB_Screen_Capture.Models;

namespace Better_SignalRGB_Screen_Capture.ViewModels;

public partial class MainViewModel
{
    /// <summary>Applies dialog capture settings to the existing source, retaining layout, selection and control identity.</summary>
    public async Task<bool> ApplySourceEditAsync(SourceItem source, Action<SourceItem> configure)
    {
        await Initialization;
        using var edit = BeginSourceEdit();
        if (edit == null || !Sources.Contains(source)) return false;
        try
        {
            // Evaluate the dialog result before stopping capture or touching the live model/history.
            var candidate = source.Clone(preserveId: true);
            configure(candidate);
            if (SceneSourceSnapshot.Capture(candidate) == SceneSourceSnapshot.Capture(source)) return true;
            await _captureLifecycle.WaitAsync();
            try
            {
                if (_shuttingDown || !Sources.Contains(source)) return false;
                await _captureService.StopCaptureAsync(source);
                if (_shuttingDown) return false;
                SaveUndoState();
                var wasRefreshNeeded = NeedsRefresh;
                CopyCaptureSettings(candidate, source);
                _compositeFrameService.RemoveSource(source);
                await _mjpegStreamingService.NotifySourceRemovedAsync(source.Id);
                _kestrelApiService.RemoveSource(source.Id);
                if (_captureRequested && IsRecording && !IsPaused && !_shuttingDown)
                    await TryStartSourceAsync(source);
                NeedsRefresh = wasRefreshNeeded;
                await SaveSourcesAsync();
                return true;
            }
            finally { _captureLifecycle.Release(); }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not update {source.DisplayName}: {ex.Message}";
            return false;
        }
    }

    private static void CopyCaptureSettings(SourceItem from, SourceItem to)
    {
        to.Name = from.Name;
        to.MonitorDeviceId = from.MonitorDeviceId;
        to.ProcessId = from.ProcessId;
        to.ProcessPath = from.ProcessPath;
        to.WindowHandle = from.WindowHandle;
        to.WindowTitle = from.WindowTitle;
        to.RegionBounds = from.RegionBounds;
        to.WebcamDeviceId = from.WebcamDeviceId;
        to.WebcamFormatId = from.WebcamFormatId;
        to.WebsiteWidth = from.WebsiteWidth;
        to.WebsiteHeight = from.WebsiteHeight;
        to.WebsiteZoom = from.WebsiteZoom;
        to.WebsiteRefreshInterval = from.WebsiteRefreshInterval;
        to.WebsiteUserAgent = from.WebsiteUserAgent;
        to.WebsiteNavigationState = from.WebsiteNavigationState;
        to.WebsiteUrl = from.WebsiteUrl;
        // Publish the new source type only after its complete capture configuration is ready.
        to.Type = from.Type;
    }
}
