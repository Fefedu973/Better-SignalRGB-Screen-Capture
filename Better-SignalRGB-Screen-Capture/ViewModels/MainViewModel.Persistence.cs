using Better_SignalRGB_Screen_Capture.Models;
using CommunityToolkit.Mvvm.Input;

namespace Better_SignalRGB_Screen_Capture.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    public Task SaveSourcesAsync()
    {
        if (_isInitializing || _nativeTemporaryScene) return Task.CompletedTask;
        _saveSourcesCts?.Cancel();
        var cancellation = new System.Threading.CancellationTokenSource();
        _saveSourcesCts = cancellation;
        // Detach mutable UI models before serialization can yield to another edit.
        var snapshot = Sources.Select(source => source.Clone(preserveId: true)).ToArray();
        return _pendingSourceSave = PersistSourcesAsync(snapshot, cancellation);
    }

    private async Task PersistSourcesAsync(SourceItem[] snapshot, System.Threading.CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(200, cancellation.Token);
            await _localSettingsService.SaveSettingAsync(SourcesSettingsKey, snapshot);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { StatusMessage = $"Could not save sources: {ex.Message}"; }
        finally
        {
            if (ReferenceEquals(_saveSourcesCts, cancellation)) _saveSourcesCts = null;
            cancellation.Dispose();
        }
    }

    private async Task LoadSourcesAsync()
    {
        try
        {
            var savedSources = await _localSettingsService.ReadSettingAsync<SourceItem[]>(SourcesSettingsKey);
            if (savedSources != null)
            {
                foreach (var source in savedSources)
                {
                    if (source != null && source.Id != Guid.Empty && !Sources.Any(existing => existing.Id == source.Id))
                    {
                        // Ensure source has valid dimensions
                        if (source.CanvasWidth <= 0) source.CanvasWidth = 200;
                        if (source.CanvasHeight <= 0) source.CanvasHeight = 150;

                        // Set preview state to match global preview toggle
                        source.IsLivePreviewEnabled = IsPreviewing;
                        source.IsSelected = false;

                        Sources.Add(source);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not load sources: {ex.Message}";
        }
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            _isSnappingEnabled = await _localSettingsService.ReadSettingAsync<bool?>(SnappingSettingsKey) ?? true;
            OnPropertyChanged(nameof(IsSnappingEnabled));
            var savedFps = await _localSettingsService.ReadSettingAsync<int?>(PreviewFpsSettingsKey);
            if (savedFps.HasValue && savedFps.Value >= 1 && savedFps.Value <= 60)
            {
                PreviewFps = savedFps.Value;
            }

            var savedIsPreviewing = await _localSettingsService.ReadSettingAsync<bool?>(IsPreviewingSettingsKey);
            if (savedIsPreviewing.HasValue)
            {
                IsPreviewing = savedIsPreviewing.Value;
            }
        }
        catch (Exception ex) { StatusMessage = $"Could not load capture settings: {ex.Message}"; }
    }

    private async Task SavePreviewFpsAsync()
    {
        try
        {
            await _localSettingsService.SaveSettingAsync(PreviewFpsSettingsKey, PreviewFps);
        }
        catch (Exception ex) { StatusMessage = $"Could not save capture settings: {ex.Message}"; }
    }

    private async Task SaveIsPreviewingAsync()
    {
        try
        {
            await _localSettingsService.SaveSettingAsync(IsPreviewingSettingsKey, IsPreviewing);
        }
        catch (Exception ex) { StatusMessage = $"Could not save capture settings: {ex.Message}"; }
    }

    private async Task InitializeAsync()
    {
        try
        {
            await LoadSettingsAsync();
            await LoadAdditionalSettingsAsync();
            await LoadOutputQualityAsync();
            await LoadSourcesAsync();
            await _captureService.SetFrameRate(PreviewFps);
        }
        catch (Exception ex) { StatusMessage = $"Initialization failed: {ex.Message}"; }
        finally { _isInitializing = false; }
    }

    private async Task SaveAvailabilitySettingAsync(bool value)
    {
        try { await _localSettingsService.SaveSettingAsync(WaitForSourceAvailabilityKey, value); }
        catch (Exception ex) { StatusMessage = $"Could not save setting: {ex.Message}"; }
    }

    private async Task LoadAdditionalSettingsAsync()
    {
        try
        {
            var waitSetting = await _localSettingsService.ReadSettingAsync<bool?>(WaitForSourceAvailabilityKey);
            if (waitSetting.HasValue) WaitForSourceAvailability = waitSetting.Value;
        }
        catch (Exception ex) { StatusMessage = $"Could not load availability setting: {ex.Message}"; }
    }

    private async Task<int> GetHttpsPortAsync()
    {
        return await _localSettingsService.ReadSettingAsync<int?>(HttpsPortKey) ?? 8443;
    }
}
