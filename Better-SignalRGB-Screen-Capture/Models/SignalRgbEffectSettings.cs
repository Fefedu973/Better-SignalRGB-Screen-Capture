namespace Better_SignalRGB_Screen_Capture.Models;

/// <summary>Versioned appearance settings. Layout and source geometry remain canvas-owned.</summary>
public sealed record SignalRgbEffectSettings
{
    public int Version => 1;
    public bool Enabled { get; init; }
    public string PictureMode { get; init; } = "Standard";
    public int Hue { get; init; }
    public int Brightness { get; init; }
    public int Saturation { get; init; }
    public bool Blur { get; init; }
    public bool Ambilight { get; init; } = true;
    public bool AmbilightFullscreen { get; init; }
    public bool HideSources { get; init; }
    public int AmbilightBlur { get; init; } = 30;
    public int AmbilightSpread { get; init; } = 10;
    public double AmbilightSaturation { get; init; } = 3;
    public int AmbilightIntensity { get; init; } = 100;
    public string Interpolation { get; init; } = "smooth";
    public int FrameRate { get; init; } = 15;

    public SignalRgbEffectSettings Normalize() => this with
    {
        PictureMode = PictureMode is "Standard" or "Cinema" or "Mono" or "Vivid" or "Dominant" or "HD" ? PictureMode : "Standard",
        Hue = Math.Clamp(Hue, -180, 180),
        Brightness = Math.Clamp(Brightness, -100, 100),
        Saturation = Math.Clamp(Saturation, -100, 100),
        AmbilightBlur = Math.Clamp(AmbilightBlur, 0, 100),
        AmbilightSpread = Math.Clamp(AmbilightSpread, 0, 100),
        AmbilightSaturation = double.IsFinite(AmbilightSaturation) ? Math.Clamp(AmbilightSaturation, 0, 10) : 3,
        AmbilightIntensity = Math.Clamp(AmbilightIntensity, 0, 200),
        Interpolation = Interpolation == "pixelated" ? "pixelated" : "smooth",
        FrameRate = Math.Clamp(FrameRate, 1, 30)
    };
}
