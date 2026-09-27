namespace Better_SignalRGB_Screen_Capture.Models;

/// <summary>Versioned output appearance and global placement; individual sources remain canvas-owned.</summary>
public sealed record SignalRgbEffectSettings
{
    public int Version => 1;
    public bool Enabled { get; init; }
    public bool WebEnabled { get; init; }
    public double ScreenX { get; init; }
    public double ScreenY { get; init; }
    public double ScreenWidth { get; init; } = 320;
    public double ScreenHeight { get; init; } = 200;
    public string PictureMode { get; init; } = "Standard";
    public int Hue { get; init; }
    public int Brightness { get; init; }
    public int Saturation { get; init; }
    public bool Blur { get; init; }
    public bool Ambilight { get; init; } = true;
    public string AmbilightStyle { get; init; } = "Classic";
    public int AmbilightCutoff { get; init; }
    public bool AmbilightFullscreen { get; init; }
    public bool HideSources { get; init; }
    public int AmbilightBlur { get; init; } = 30;
    public int AmbilightSpread { get; init; } = 10;
    public double AmbilightSaturation { get; init; } = 3;
    public int AmbilightIntensity { get; init; } = 100;
    public string Interpolation { get; init; } = "smooth";
    public int FrameRate { get; init; } = 15;

    public SignalRgbEffectSettings ResetAppearance() => new() { Enabled = Enabled, WebEnabled = WebEnabled };

    public SignalRgbEffectSettings Normalize()
    {
        var width = FiniteClamp(ScreenWidth, 320, 1, 320);
        var height = FiniteClamp(ScreenHeight, 200, 1, 200);
        return this with
        {
            ScreenWidth = width,
            ScreenHeight = height,
            ScreenX = FiniteClamp(ScreenX, 0, 0, 320 - width),
            ScreenY = FiniteClamp(ScreenY, 0, 0, 200 - height),
            PictureMode = PictureMode is "Standard" or "Cinema" or "Mono" or "Vivid" or "Dominant" or "HD" ? PictureMode : "Standard",
            Hue = Math.Clamp(Hue, -180, 180),
            Brightness = Math.Clamp(Brightness, -100, 100),
            Saturation = Math.Clamp(Saturation, -100, 100),
            AmbilightStyle = AmbilightStyle is "Classic" or "Soft" ? AmbilightStyle : "Classic",
            AmbilightCutoff = Math.Clamp(AmbilightCutoff, 0, 100),
            AmbilightBlur = Math.Clamp(AmbilightBlur, 0, 100),
            AmbilightSpread = Math.Clamp(AmbilightSpread, 0, 100),
            AmbilightSaturation = double.IsFinite(AmbilightSaturation) ? Math.Clamp(AmbilightSaturation, 0, 10) : 3,
            AmbilightIntensity = Math.Clamp(AmbilightIntensity, 0, 200),
            Interpolation = Interpolation == "pixelated" ? "pixelated" : "smooth",
            FrameRate = Math.Clamp(FrameRate, 1, 30)
        };
    }

    private static double FiniteClamp(double value, double fallback, double minimum, double maximum) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}
