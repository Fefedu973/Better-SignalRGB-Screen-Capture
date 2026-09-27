namespace Better_SignalRGB_Screen_Capture.Core.Helpers;

/// <summary>A per-display decision for a linear scRGB capture, never for encoded SDR bytes.</summary>
public readonly record struct CaptureColorDecision(bool ToneMapHdr, double SdrWhiteNits, bool IsKnown);

public static class CaptureColorPolicy
{
    /// <summary>
    /// Missing display metadata must not turn ordinary SDR into assumed HDR. A known HDR
    /// display with an unknown white level keeps highlight compression at neutral exposure,
    /// but must remain visibly uncalibrated. Never reuse another display's previous white.
    /// </summary>
    public static CaptureColorDecision Resolve(bool? hdrEnabled, double? sdrWhiteNits)
    {
        if (hdrEnabled == false) return new(false, HdrToneMapper.ReferenceWhiteNits, true);
        if (hdrEnabled is null) return new(false, HdrToneMapper.ReferenceWhiteNits, false);
        if (sdrWhiteNits is { } white && double.IsFinite(white) && white is >= 1 and <= 10000)
            return new(true, white, true);
        // 80 nits means gain 1, not a guessed user brightness. Preserve relative HDR
        // highlights, while reporting Unknown rather than claiming calibrated conversion.
        return new(true, HdrToneMapper.ReferenceWhiteNits, false);
    }
}
