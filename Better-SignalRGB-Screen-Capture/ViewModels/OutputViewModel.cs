using System.Runtime.CompilerServices;

using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Models;

using CommunityToolkit.Mvvm.Input;

using CommunityToolkit.Mvvm.ComponentModel;

namespace Better_SignalRGB_Screen_Capture.ViewModels;

public partial class OutputViewModel : ObservableObject
{
    private readonly ISignalRgbEffectSettingsService _effectSettingsService;
    private SignalRgbEffectSettings _effectSettings = new();
    private readonly SynchronizationContext? _uiContext;
    private Task _effectInitialization = Task.CompletedTask;
    private int _effectChangeVersion;
    private bool _effectSettingsReady;
    private bool _effectSettingsSubscribed;
    private bool _effectSavePending;
    private string _effectSettingsStatus = string.Empty;

    public IReadOnlyList<string> EffectPictureModes { get; } = ["Standard", "Cinema", "Mono", "Vivid", "Dominant", "HD"];
    public IReadOnlyList<string> EffectAmbilightStyles { get; } = ["Classic", "Soft"];
    public IReadOnlyList<string> EffectInterpolationModes { get; } = ["Smooth", "Pixelated"];
    public bool IsEffectSettingsReady => _effectSettingsReady;
    public bool CanEditEffectSettings => _effectSettingsReady;
    public SignalRgbEffectSettings PreviewSettings => _effectSettings;
    public event EventHandler<SignalRgbEffectSettings>? PreviewSettingsChanged;
    public bool CanEditEffectGlow => CanEditEffectSettings && _effectSettings.Ambilight;
    public bool CanHideEffectPicture => CanEditEffectGlow && _effectSettings.AmbilightFullscreen;
    public string EffectSettingsStatus => _effectSettingsStatus;

    public bool EffectEnabled
    {
        get => _effectSettings.Enabled;
        set => ChangeEffectSettings(_effectSettings with { Enabled = value });
    }

    public bool EffectWebEnabled
    {
        get => _effectSettings.WebEnabled;
        set => ChangeEffectSettings(_effectSettings with { WebEnabled = value });
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

    public string EffectAmbilightStyle
    {
        get => _effectSettings.AmbilightStyle;
        set { if (EffectAmbilightStyles.Contains(value)) ChangeEffectSettings(_effectSettings with { AmbilightStyle = value }); }
    }

    public double EffectAmbilightCutoff
    {
        get => _effectSettings.AmbilightCutoff;
        set => ChangeEffectInteger(value, 0, 100, (settings, number) => settings with { AmbilightCutoff = number });
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


    public double ScreenX
    {
        get => _effectSettings.ScreenX;
        set { if (double.IsFinite(value)) ChangeEffectSettings(_effectSettings with { ScreenX = value }); }
    }

    public double ScreenY
    {
        get => _effectSettings.ScreenY;
        set { if (double.IsFinite(value)) ChangeEffectSettings(_effectSettings with { ScreenY = value }); }
    }

    public double ScreenWidth
    {
        get => _effectSettings.ScreenWidth;
        set { if (double.IsFinite(value)) ChangeEffectSettings(_effectSettings with { ScreenWidth = value }); }
    }

    public double ScreenHeight
    {
        get => _effectSettings.ScreenHeight;
        set { if (double.IsFinite(value)) ChangeEffectSettings(_effectSettings with { ScreenHeight = value }); }
    }

    public void ApplyPlacement(double x, double y, double width, double height)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height)) return;
        ChangeEffectSettings(_effectSettings with { ScreenX = x, ScreenY = y, ScreenWidth = width, ScreenHeight = height });
    }

    [RelayCommand(CanExecute = nameof(CanEditEffectSettings))]
    private void FillPlacement() => ApplyPlacement(0, 0, 320, 200);

    [RelayCommand(CanExecute = nameof(CanEditEffectSettings))]
    private void InsetPlacement() => ApplyPlacement(32, 20, 256, 160);

    [RelayCommand(CanExecute = nameof(CanEditEffectSettings))]
    private void CenterPlacement() => ApplyPlacement((320 - ScreenWidth) / 2, (200 - ScreenHeight) / 2, ScreenWidth, ScreenHeight);

    public OutputViewModel(ISignalRgbEffectSettingsService effectSettingsService)
    {
        _effectSettingsService = effectSettingsService;
        _uiContext = SynchronizationContext.Current;
        _effectInitialization = LoadEffectSettingsAsync();
    }

    private async Task LoadEffectSettingsAsync()
    {
        try
        {
            await _effectSettingsService.InitializeAsync();
            // A recently closed output editor may still have an edit in the debounce window.
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

        if (_uiContext == null || SynchronizationContext.Current == _uiContext) RefreshIfIdle();
        else _uiContext.Post(_ => RefreshIfIdle(), null);
    }

    private void RefreshEffectSettings()
    {
        _effectSettings = _effectSettingsService.Current;
        OnPropertyChanged(string.Empty);
        ResetEffectSettingsCommand.NotifyCanExecuteChanged();
        FillPlacementCommand.NotifyCanExecuteChanged();
        InsetPlacementCommand.NotifyCanExecuteChanged();
        CenterPlacementCommand.NotifyCanExecuteChanged();
        PreviewSettingsChanged?.Invoke(this, _effectSettings);
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
        settings = settings.Normalize();
        if (!_effectSettingsReady || settings == _effectSettings) return;
        _effectSettings = settings;
        OnPropertyChanged(string.Empty);
        OnPropertyChanged(nameof(CanEditEffectSettings));
        OnPropertyChanged(nameof(CanEditEffectGlow));
        OnPropertyChanged(nameof(CanHideEffectPicture));
        ResetEffectSettingsCommand.NotifyCanExecuteChanged();

        _effectSavePending = true;
        var version = ++_effectChangeVersion;
        PreviewSettingsChanged?.Invoke(this, _effectSettings);
        _ = SaveEffectSettingsAsync(version, _effectSettings);
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

    public void ReportPreviewError(string message) => ReportEffectSettingsError(message);

    private void ReportEffectSettingsError(string message)
    {
        _effectSettingsStatus = message;
        OnPropertyChanged(nameof(EffectSettingsStatus));
    }

    [RelayCommand(CanExecute = nameof(CanEditEffectSettings))]
    private void ResetEffectSettings()
    {
        ChangeEffectSettings(_effectSettings.ResetAppearance(), string.Empty);
    }
}
