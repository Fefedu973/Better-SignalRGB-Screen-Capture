using Better_SignalRGB_Screen_Capture.Core.Helpers;

internal static class CaptureColorPolicyTests
{
    public static void Run(Action<bool, string> check)
    {
        foreach (var white in new double?[] { null, 80, 160, 480, double.NaN, 0 })
        {
            var sdr = CaptureColorPolicy.Resolve(false, white);
            check(sdr is { ToneMapHdr: false, SdrWhiteNits: 80, IsKnown: true },
                "Known SDR never inherits HDR exposure or shoulder from display white metadata");
            var unknown = CaptureColorPolicy.Resolve(null, white);
            check(unknown is { ToneMapHdr: false, SdrWhiteNits: 80, IsKnown: false },
                "Unknown HDR mode cannot be inferred from the presence of a white level");
        }
        foreach (var white in new[] { 1d, 80, 160, 480, 10000 })
        {
            var hdr = CaptureColorPolicy.Resolve(true, white);
            check(hdr.ToneMapHdr && hdr.IsKnown && hdr.SdrWhiteNits == white,
                "Known HDR with a valid white level retains that display's calibration");
        }
        foreach (var white in new double?[] { null, double.NaN, double.NegativeInfinity, double.PositiveInfinity, -80, 0, .99, 10000.01 })
        {
            var fallback = CaptureColorPolicy.Resolve(true, white);
            check(fallback is { ToneMapHdr: true, SdrWhiteNits: 80, IsKnown: false },
                "Known HDR without valid calibration preserves highlights at neutral gain and reports Unknown");
        }

        // Exercise the actual mapper with the decision: this catches an accidental
        // HDR shoulder on unclassified SDR even when the metadata labels look right.
        foreach (var mode in new bool?[] { false, null })
        {
            var decision = CaptureColorPolicy.Resolve(mode, 480);
            var output = HdrToneMapper.MapLinearRgb(.9f, .2f, .1f, decision.ToneMapHdr, decision.SdrWhiteNits);
            check(Math.Abs(output.Red - .9) < 1e-6 && Math.Abs(output.Green - .2) < 1e-6 && Math.Abs(output.Blue - .1) < 1e-6,
                "SDR or unknown metadata does not darken a valid linear SDR image");
        }
        var missing = CaptureColorPolicy.Resolve(true, null);
        var lower = HdrToneMapper.MapLinearRgb(2, 2, 2, missing.ToneMapHdr, missing.SdrWhiteNits).Red;
        var higher = HdrToneMapper.MapLinearRgb(4, 4, 4, missing.ToneMapHdr, missing.SdrWhiteNits).Red;
        check(lower > 0 && lower < higher && higher < 1 && !missing.IsKnown,
            "Uncalibrated but confirmed HDR retains distinct highlights without pretending accurate white metadata");

        var beforeMove = CaptureColorPolicy.Resolve(true, 480);
        var afterMove = CaptureColorPolicy.Resolve(null, null);
        var recovered = CaptureColorPolicy.Resolve(true, 160);
        check(beforeMove.SdrWhiteNits == 480 && afterMove is { ToneMapHdr: false, IsKnown: false, SdrWhiteNits: 80 } &&
              recovered is { ToneMapHdr: true, IsKnown: true, SdrWhiteNits: 160 },
            "Display changes cannot reuse a previous monitor's SDR white or hide a query failure");
    }
}
