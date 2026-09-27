using Better_SignalRGB_Screen_Capture.Contracts.Services;

namespace Better_SignalRGB_Screen_Capture.Models;

/// <summary>Optional metadata overrides. Output activation switches are deliberately not overridable.</summary>
public sealed record NativeAppearanceOverrides
{
    public double? ScreenX { get; init; }
    public double? ScreenY { get; init; }
    public double? ScreenWidth { get; init; }
    public double? ScreenHeight { get; init; }
    public string? PictureMode { get; init; }
    public int? Hue { get; init; }
    public int? Brightness { get; init; }
    public int? Saturation { get; init; }
    public bool? Blur { get; init; }
    public bool? Ambilight { get; init; }
    public string? AmbilightStyle { get; init; }
    public int? AmbilightCutoff { get; init; }
    public int? AmbilightEdgeDepth { get; init; }
    public int? AmbilightEdgeMix { get; init; }
    public int? AmbilightEdgeReach { get; init; }
    public int? AmbilightEdgeFade { get; init; }
    public bool? AmbilightFullscreen { get; init; }
    public bool? HideSources { get; init; }
    public int? AmbilightBlur { get; init; }
    public int? AmbilightSpread { get; init; }
    public double? AmbilightSaturation { get; init; }
    public int? AmbilightIntensity { get; init; }
    public string? Interpolation { get; init; }
    public int? FrameRate { get; init; }

    public SignalRgbEffectSettings Apply(SignalRgbEffectSettings saved) => (saved with
    {
        ScreenX = ScreenX ?? saved.ScreenX, ScreenY = ScreenY ?? saved.ScreenY,
        ScreenWidth = ScreenWidth ?? saved.ScreenWidth, ScreenHeight = ScreenHeight ?? saved.ScreenHeight,
        PictureMode = PictureMode ?? saved.PictureMode, Hue = Hue ?? saved.Hue,
        Brightness = Brightness ?? saved.Brightness, Saturation = Saturation ?? saved.Saturation,
        Blur = Blur ?? saved.Blur, Ambilight = Ambilight ?? saved.Ambilight,
        AmbilightStyle = AmbilightStyle ?? saved.AmbilightStyle, AmbilightCutoff = AmbilightCutoff ?? saved.AmbilightCutoff,
        AmbilightEdgeDepth = AmbilightEdgeDepth ?? saved.AmbilightEdgeDepth,
        AmbilightEdgeMix = AmbilightEdgeMix ?? saved.AmbilightEdgeMix,
        AmbilightEdgeReach = AmbilightEdgeReach ?? saved.AmbilightEdgeReach,
        AmbilightEdgeFade = AmbilightEdgeFade ?? saved.AmbilightEdgeFade,
        AmbilightFullscreen = AmbilightFullscreen ?? saved.AmbilightFullscreen,
        HideSources = HideSources ?? saved.HideSources, AmbilightBlur = AmbilightBlur ?? saved.AmbilightBlur,
        AmbilightSpread = AmbilightSpread ?? saved.AmbilightSpread,
        AmbilightSaturation = AmbilightSaturation ?? saved.AmbilightSaturation,
        AmbilightIntensity = AmbilightIntensity ?? saved.AmbilightIntensity,
        Interpolation = Interpolation ?? saved.Interpolation, FrameRate = FrameRate ?? saved.FrameRate
    }).Normalize();
}

public sealed record NativeLeaseState(Guid Id, string ClientId, int TtlSeconds, DateTimeOffset ExpiresAt,
    bool Expired, bool AppearanceRevoked);

public sealed record NativeControlState(long Revision, NativeLeaseState? Lease, NativeSceneState Scene,
    SignalRgbEffectSettings EffectiveSettings, string? Error);
