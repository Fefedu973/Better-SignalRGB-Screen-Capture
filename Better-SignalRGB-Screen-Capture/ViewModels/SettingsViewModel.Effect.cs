using System.Runtime.CompilerServices;

using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Models;

using CommunityToolkit.Mvvm.Input;

using Microsoft.UI.Dispatching;

namespace Better_SignalRGB_Screen_Capture.ViewModels;

public partial class SettingsViewModel
{
    private readonly ISignalRgbEffectSettingsService _effectSettingsService = App.GetService<ISignalRgbEffectSettingsService>();
    private SignalRgbEffectSettings _effectSettings = new();
    private DispatcherQueue? _effectDispatcher;
    private Task _effectInitialization = Task.CompletedTask;
    private int _effectChangeVersion;
    private bool _effectSettingsReady;
    private bool _effectSettingsSubscribed;
    private bool _effectSavePending;
    private string _effectSettingsStatus = string.Empty;

    public IReadOnlyList<string> EffectPictureModes { get; } = ["Standard", "Cinema", "Mono", "Vivid", "Dominant", "HD"];
    public IReadOnlyList<string> EffectInterpolationModes { get; } = ["Smooth", "Pixelated"];
    public bool IsEffectSettingsReady => _effectSettingsReady;
    public bool CanEditEffectSettings => _effectSettingsReady && _effectSettings.Enabled;
    public bool CanEditEffectGlow => CanEditEffectSettings && _effectSettings.Ambilight;
    public bool CanHideEffectPicture => CanEditEffectGlow && _effectSettings.AmbilightFullscreen;
    public string EffectSettingsStatus => _effectSettingsStatus;

    public bool EffectEnabled
    {
        get => _effectSettings.Enabled;
        set => ChangeEffectSettings(_effectSettings with { Enabled = value });
    }

    public string EffectPictureMode
    {
        get => _effectSettings.PictureMode;
        set
        {
            if (EffectPictureModes.Contains(value)) ChangeEffectSettings(_effectSettings with { PictureMode = value });
        }
    }

    public double EffectHue
    {
        get => _effectSettings.Hue;
        set => ChangeEffectInteger(value, -180, 180, (settings, number) => settings with { Hue = number });
    }

    public double EffectBrightness
    {
        get => _effectSettings.Brightness;
        set => ChangeEffectInteger(value, -100, 100, (settings, number) => settings with { Brightness = number });
    }

    public double EffectSaturation
    {
        get => _effectSettings.Saturation;
        set => ChangeEffectInteger(value, -100, 100, (settings, number) => settings with { Saturation = number });
    }

    public bool EffectBlur
    {
        get => _effectSettings.Blur;
        set => ChangeEffectSettings(_effectSettings with { Blur = value });
    }

    public bool EffectAmbilight
    {
        get => _effectSettings.Ambilight;
        set => ChangeEffectSettings(_effectSettings with { Ambilight = value });
    }

    public bool EffectAmbilightFullscreen
    {
        get => _effectSettings.AmbilightFullscreen;
        set => ChangeEffectSettings(_effectSettings with { AmbilightFullscreen = value });
    }

    public bool EffectHideSources
    {
        get => _effectSettings.HideSources;
        set => ChangeEffectSettings(_effectSettings with { HideSources = value });
    }

    public double EffectAmbilightBlur
    {
        get => _effectSettings.AmbilightBlur;
        set => ChangeEffectInteger(value, 0, 100, (settings, number) => settings with { AmbilightBlur = number });
    }

    public double EffectAmbilightSpread
    {
        get => _effectSettings.AmbilightSpread;
        set => ChangeEffectInteger(value, 0, 100, (settings, number) => settings with { AmbilightSpread = number });
    }

    public double EffectAmbilightSaturation
    {
        get => _effectSettings.AmbilightSaturation;
        set
        {
            if (double.IsFinite(value))
                ChangeEffectSettings(_effectSettings with { AmbilightSaturation = Math.Clamp(value, 0, 10) });
        }
    }

    public double EffectAmbilightIntensity
    {
        get => _effectSettings.AmbilightIntensity;
        set => ChangeEffectInteger(value, 0, 200, (settings, number) => settings with { AmbilightIntensity = number });
    }

    public string EffectInterpolation
    {
        get => _effectSettings.Interpolation == "pixelated" ? "Pixelated" : "Smooth";
        set
        {
            if (EffectInterpolationModes.Contains(value))
                ChangeEffectSettings(_effectSettings with { Interpolation = value == "Pixelated" ? "pixelated" : "smooth" });
        }
    }

    public double EffectFrameRate
    {
        get => _effectSettings.FrameRate;
        set => ChangeEffectInteger(value, 1, 30, (settings, number) => settings with { FrameRate = number });
    }

    private void InitializeEffectSettings()
    {
        _effectDispatcher = DispatcherQueue.GetForCurrentThread();
        _effectInitialization = LoadEffectSettingsAsync();
    }

    private async Task LoadEffectSettingsAsync()
    {
        try
        {
            await _effectSettingsService.InitializeAsync();
            // A recently closed Settings page may still have an edit in the debounce window.
            // Load that confirmed value before a new page can edit a stale snapshot.
            await _effectSettingsService.FlushAsync();
            _effectSettingsReady = true;
            RefreshEffectSettings();
        }
        catch (Exception ex)
        {
            ReportEffectSettingsError($"Could not load effect settings: {ex.Message}");
        }
    }

    public async Task ActivateEffectSettingsAsync()
    {
        if (!_effectSettingsSubscribed)
        {
            _effectSettingsService.Changed += OnEffectSettingsChanged;
            _effectSettingsSubscribed = true;
        }
        await _effectInitialization;
        if (!_effectSavePending) RefreshEffectSettings();
    }

    public void DeactivateEffectSettings()
    {
        _effectSettingsService.Changed -= OnEffectSettingsChanged;
        _effectSettingsSubscribed = false;
        // The singleton service owns pending writes, so navigation cannot abandon an edit.
    }

    private void OnEffectSettingsChanged(object? sender, SignalRgbEffectSettings settings)
    {
        void RefreshIfIdle()
        {
            if (_effectSettingsSubscribed && !_effectSavePending) RefreshEffectSettings();
        }

        if (_effectDispatcher?.HasThreadAccess == true) RefreshIfIdle();
        else _effectDispatcher?.TryEnqueue(RefreshIfIdle);
    }

    private void RefreshEffectSettings()
    {
        _effectSettings = _effectSettingsService.Current;
        OnPropertyChanged(string.Empty);
        ResetEffectSettingsCommand.NotifyCanExecuteChanged();
    }

    private void ChangeEffectInteger(double value, int minimum, int maximum,
        Func<SignalRgbEffectSettings, int, SignalRgbEffectSettings> update,
        [CallerMemberName] string? propertyName = null)
    {
        // NumberBox emits NaN for an empty editor. Never convert it to an integer or persist it.
        if (!double.IsFinite(value)) return;
        var number = (int)Math.Round(Math.Clamp(value, minimum, maximum));
        ChangeEffectSettings(update(_effectSettings, number), propertyName);
    }

    private void ChangeEffectSettings(SignalRgbEffectSettings settings, [CallerMemberName] string? propertyName = null)
    {
        if (!_effectSettingsReady || settings == _effectSettings) return;
        _effectSettings = settings;
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(CanEditEffectSettings));
        OnPropertyChanged(nameof(CanEditEffectGlow));
        OnPropertyChanged(nameof(CanHideEffectPicture));
        ResetEffectSettingsCommand.NotifyCanExecuteChanged();

        _effectSavePending = true;
        var version = ++_effectChangeVersion;
        _ = SaveEffectSettingsAsync(version, settings);
    }

    private async Task SaveEffectSettingsAsync(int version, SignalRgbEffectSettings settings)
    {
        try
        {
            await _effectSettingsService.ScheduleUpdateAsync(settings);
            if (version == _effectChangeVersion)
            {
                _effectSettingsStatus = string.Empty;
                OnPropertyChanged(nameof(EffectSettingsStatus));
            }
        }
        catch (Exception ex)
        {
            if (version == _effectChangeVersion)
                ReportEffectSettingsError($"Could not save effect settings: {ex.Message}");
        }
        finally
        {
            if (version == _effectChangeVersion)
            {
                _effectSavePending = false;
                RefreshEffectSettings();
            }
        }
    }

    private void ReportEffectSettingsError(string message)
    {
        _effectSettingsStatus = message;
        OnPropertyChanged(nameof(EffectSettingsStatus));
        App.GetService<MainViewModel>().StatusMessage = message;
    }

    [RelayCommand(CanExecute = nameof(CanEditEffectSettings))]
    private void ResetEffectSettings()
    {
        ChangeEffectSettings(new SignalRgbEffectSettings { Enabled = _effectSettings.Enabled }, string.Empty);
    }
}
