using CommunityToolkit.Mvvm.Input;

namespace Better_SignalRGB_Screen_Capture.ViewModels;

public partial class MainViewModel
{
    private const string HighQualitySettingsKey = "HighQualityWebOutput";
    private bool _isHighQuality;

    // Keep scene geometry in its original coordinate system. Scaling the final
    // surface preserves rotated crops exactly and makes repeated mode changes lossless.
    public bool IsHighQuality => _isHighQuality;
    public double CanvasOutputWidth => IsHighQuality ? 800 : 320;
    public double CanvasOutputHeight => IsHighQuality ? 600 : 200;
    public double CanvasScaleX => CanvasOutputWidth / 320;
    public double CanvasScaleY => CanvasOutputHeight / 200;
    public double CanvasViewportWidth => 400 * CanvasScaleX;
    public double CanvasViewportHeight => 280 * CanvasScaleY;

    [RelayCommand(CanExecute = nameof(IsCanvasEditable))]
    private Task ToggleHighQualityAsync() => SetHighQualityAsync(!IsHighQuality);

    public async Task SetHighQualityAsync(bool enabled)
    {
        await Initialization;
        if (enabled == IsHighQuality || !IsCanvasEditable) return;
        CanvasInteractionCancellationRequested?.Invoke(this, EventArgs.Empty);
        using var edit = BeginSourceEdit();
        if (edit == null) return;
        await _captureLifecycle.WaitAsync();
        var previous = IsHighQuality;
        var saved = false;
        var stopped = false;
        try
        {
            if (_shuttingDown) return;
            // Do not interrupt a working capture when saving the preference fails.
            await _localSettingsService.SaveSettingAsync(HighQualitySettingsKey, enabled);
            saved = true;
            if (_shuttingDown) return;
            await _captureService.StopAllCapturesAsync();
            stopped = true;
            await _captureService.SetHighQuality(enabled);
            ApplyOutputQuality(enabled);
        }
        catch (Exception exception)
        {
            StatusMessage = $"Could not change web output quality: {exception.Message}";
            if (saved && IsHighQuality == previous)
            {
                try { await _localSettingsService.SaveSettingAsync(HighQualitySettingsKey, previous); }
                catch (Exception saveError) { StatusMessage += $" Could not restore the saved preference: {saveError.Message}"; }
            }
        }
        finally
        {
            try
            {
                // Stop/pause/shutdown may arrive while the native sessions stop.
                // Preserve that newer intent; changing quality never starts recording.
                if (stopped && _captureRequested && IsRecording && !IsPaused && !_shuttingDown)
                {
                    foreach (var source in Sources.ToArray()) await TryStartSourceAsync(source);
                    NeedsRefresh = false;
                }
                OnPropertyChanged(nameof(IsHighQuality));
            }
            finally { _captureLifecycle.Release(); }
        }
    }

    private async Task LoadOutputQualityAsync()
    {
        try
        {
            var enabled = await _localSettingsService.ReadSettingAsync<bool?>(HighQualitySettingsKey) ?? false;
            await _captureService.SetHighQuality(enabled);
            ApplyOutputQuality(enabled);
        }
        catch (Exception exception) { StatusMessage = $"Could not load web output quality: {exception.Message}"; }
    }

    private void ApplyOutputQuality(bool enabled)
    {
        _isHighQuality = enabled;
        _compositeFrameService.SetCanvasSize((int)CanvasOutputWidth, (int)CanvasOutputHeight);
        OnPropertyChanged(nameof(IsHighQuality));
        OnPropertyChanged(nameof(CanvasOutputWidth));
        OnPropertyChanged(nameof(CanvasOutputHeight));
        OnPropertyChanged(nameof(CanvasScaleX));
        OnPropertyChanged(nameof(CanvasScaleY));
        OnPropertyChanged(nameof(CanvasViewportWidth));
        OnPropertyChanged(nameof(CanvasViewportHeight));
    }
}
