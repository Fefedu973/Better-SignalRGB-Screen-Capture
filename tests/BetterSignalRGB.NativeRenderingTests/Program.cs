using System.Text.Json;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.Services;
using Better_SignalRGB_Screen_Capture.Services.NativeOutput;

namespace BetterSignalRGB.NativeRenderingTests;

internal static class Program
{
    private static int assertions;
    private static void Check(bool condition, string message)
    { assertions++; if (!condition) throw new InvalidOperationException(message); }
    private static void Near(double expected, double actual, string message, double tolerance = 1e-7) =>
        Check(Math.Abs(expected - actual) <= tolerance, $"{message}: {expected} != {actual}");
    private static StreamingSourceSnapshot Source(int id = 1) => new(
        Guid.Parse($"00000000-0000-0000-0000-{id:000000000000}"), "Region", "", 10, 20, 100, 60,
        0, 1, false, false, 0, 0, 0, 0, 0, 0);
    private static NativeRenderMetadata Metadata(StreamingSourceSnapshot[] sources, SignalRgbEffectSettings? settings = null,
        int width = 320, int height = 200, HashSet<Guid>? active = null) => NativeRenderMetadata.Create(7, sources,
        active ?? sources.Select(source => source.Id).ToHashSet(), width, height, settings ?? new());
    private static bool Contains(IReadOnlyList<NativePoint> polygon, NativePoint point)
    {
        var sign = 0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count];
            var cross = (b.X - a.X) * (point.Y - a.Y) - (b.Y - a.Y) * (point.X - a.X);
            if (Math.Abs(cross) < 1e-7) continue;
            if (sign != 0 && sign != Math.Sign(cross)) return false;
            sign = Math.Sign(cross);
        }
        return polygon.Count >= 3;
    }
    private static NativePoint InverseRotate(NativePoint p, double x, double y, double degrees)
    { var r = -degrees * Math.PI / 180; return new(x + (p.X - x) * Math.Cos(r) - (p.Y - y) * Math.Sin(r), y + (p.X - x) * Math.Sin(r) + (p.Y - y) * Math.Cos(r)); }

    private static void GeometryTests()
    {
        var plain = Source(); var metadata = Metadata([plain]); var geometry = metadata.Sources[0];
        Check(metadata.Version == 1 && metadata.Schema == "better.native-rendering" && metadata.StateRevision == 7, "Stable version/revision");
        Check(geometry.Contributes && geometry.CoveragePolygon.Count == 4, "Opaque black has geometry independent of image colors");
        Near(10, geometry.CoveragePolygon.Min(p => p.X), "Bounds left"); Near(80, geometry.CoveragePolygon.Max(p => p.Y), "Bounds bottom");
        var cropped = plain with { CropLeftPct = .7, CropTopPct = .1, CropRotation = 33, Rotation = 47 };
        var original = Metadata([cropped]).Sources[0];
        foreach (var (horizontal, vertical) in new[] { (true, false), (false, true), (true, true) })
        {
            var mirrored = Metadata([cropped with { IsMirroredHorizontally = horizontal, IsMirroredVertically = vertical }]).Sources[0];
            Check(original.CoveragePolygon.SequenceEqual(mirrored.CoveragePolygon), "Mirroring media preserves independently rotated crop mask");
        }
        var mirrorMatrix = Metadata([plain with { IsMirroredHorizontally = true }]).Sources[0].CanvasFromImage;
        Near(110, mirrorMatrix.Dx, "Mirror transform maps local left to original right"); Near(-1, mirrorMatrix.M11, "Mirror horizontal sign");
        var offscreen = Metadata([plain with { CanvasX = -70, CanvasY = -25, Rotation = 37, CropRotation = 29 }]).Sources[0];
        Check(offscreen.CoveragePolygon.All(p => p.X >= -1e-8 && p.Y >= -1e-8 && p.X <= 320.00000001 && p.Y <= 200.00000001), "Coverage clips to the canvas");
        var placed = Metadata([plain with { CanvasX = -50 }], new() { ScreenX = 80, ScreenY = 50, ScreenWidth = 160, ScreenHeight = 100 }).Sources[0];
        Near(80, placed.PlacedCoveragePolygon.Min(p => p.X), "Capture canvas clipping precedes global placement");
        Near(105, placed.PlacedCoveragePolygon.Max(p => p.X), "Placement preserves clipped content without rescaling it");
        var unavailable = Metadata([plain], active: []).Sources[0];
        Check(!unavailable.HasFrame && !unavailable.Contributes && unavailable.CoveragePolygon.Count == 4, "Missing-frame geometry does not contribute pixels");
        Check(!Metadata([plain with { Opacity = 0 }]).Sources[0].Contributes, "Zero-opacity source emits no silhouette");
        var ordered = Metadata([plain with { ZIndex = 3 }, Source(2) with { ZIndex = -1 }, Source(3) with { ZIndex = 3 }]);
        Check(ordered.Sources.Select(s => s.Id).SequenceEqual(new[] { Source(2).Id, plain.Id, Source(3).Id }), "Stable stacking order resolves ties by original order");
        Check(ordered.Sources.Select(s => s.Order).SequenceEqual(new[] { 0, 1, 2 }), "Explicit consecutive source order");
        var invalid = false;
        try { Metadata([plain with { Opacity = double.NaN }]); } catch (ArgumentException) { invalid = true; }
        Check(invalid, "Invalid geometry cannot be silently announced for valid pixels");
        var settings = Metadata([plain], new() { AmbilightEdgeDepth = 99, AmbilightStyle = "invalid", ScreenX = 100, ScreenWidth = 320 });
        Check(settings.EffectiveSettings.AmbilightEdgeDepth == 20 && settings.EffectiveSettings.AmbilightStyle == "Classic" && settings.EffectiveSettings.ScreenX == 0, "Effective settings use production normalization");

        // Independent inverse-transform oracle: sample points inside image bounds and the
        // crop rectangle rotated about its OWN center. This does not reuse polygon clipping.
        var random = new Random(418);
        for (var test = 0; test < 40; test++)
        {
            var source = plain with { CanvasX = random.Next(-80, 280), CanvasY = random.Next(-60, 170),
                CanvasWidth = random.Next(20, 180), CanvasHeight = random.Next(20, 130), Rotation = random.Next(-180, 180),
                CropRotation = random.Next(-180, 180), CropLeftPct = .13, CropRightPct = .21, CropTopPct = .17, CropBottomPct = .08 };
            var polygon = Metadata([source]).Sources[0].CoveragePolygon;
            for (var sample = 0; sample < 200; sample++)
            {
                var point = new NativePoint(random.NextDouble() * 360 - 20, random.NextDouble() * 240 - 20);
                var local = InverseRotate(new(point.X - source.CanvasX, point.Y - source.CanvasY), source.CanvasWidth / 2d, source.CanvasHeight / 2d, source.Rotation);
                var left = source.CanvasWidth * source.CropLeftPct; var top = source.CanvasHeight * source.CropTopPct;
                var right = source.CanvasWidth * (1 - source.CropRightPct); var bottom = source.CanvasHeight * (1 - source.CropBottomPct);
                var inCrop = InverseRotate(local, (left + right) / 2, (top + bottom) / 2, source.CropRotation);
                var expected = point.X >= 0 && point.X <= 320 && point.Y >= 0 && point.Y <= 200 &&
                    local.X >= 0 && local.X <= source.CanvasWidth && local.Y >= 0 && local.Y <= source.CanvasHeight &&
                    inCrop.X >= left && inCrop.X <= right && inCrop.Y >= top && inCrop.Y <= bottom;
                Check(expected == Contains(polygon, point), "Polygon agrees with inverse image/crop membership");
            }
        }
    }

    private static object[] Fixtures()
    {
        object Fixture(string name, StreamingSourceSnapshot[] sources, SignalRgbEffectSettings settings, int width = 320, int height = 200,
            string pattern = "quadrants", HashSet<Guid>? active = null) => new { name, pattern, sources,
                metadata = Metadata(sources, settings with { WebEnabled = true }, width, height, active) };
        var full = Source() with { CanvasX = 30, CanvasY = 25, CanvasWidth = 230, CanvasHeight = 140 };
        return [
            Fixture("black-is-covered", [full], new() { Ambilight = false }, pattern:"black"),
            Fixture("mirror-asymmetric-crop", [full with { CropLeftPct = .4, CropTopPct = .15, IsMirroredHorizontally = true }], new() { Ambilight = false }),
            Fixture("rotated-crop-offscreen", [full with { CanvasX = -20, Rotation = 29, CropRotation = -37, CropLeftPct = .18, CropRightPct = .12 }], new() { Ambilight = false }),
            Fixture("opacity-overlap-cinema", [full with { Opacity = .35 }, Source(2) with { CanvasX = 140, CanvasY = 85, Rotation = 22, Opacity = .6, ZIndex = 1 }], new() { PictureMode = "Cinema", Hue = 25, Brightness = 8, Saturation = -12, Blur = true, Ambilight = false }),
            Fixture("mode-mono", [full], new() { PictureMode = "Mono", Hue = -30, Ambilight = false }),
            Fixture("mode-vivid", [full], new() { PictureMode = "Vivid", Saturation = -10, Ambilight = false }),
            Fixture("mode-dominant", [full], new() { PictureMode = "Dominant", Brightness = 10, Ambilight = false }),
            Fixture("mode-hd", [full], new() { PictureMode = "HD", Hue = 15, Ambilight = false }),
            Fixture("classic-inset", [full], new() { ScreenX = 32, ScreenY = 20, ScreenWidth = 256, ScreenHeight = 160, AmbilightStyle = "Classic", AmbilightBlur = 12, AmbilightSpread = 7, AmbilightCutoff = 15 }),
            Fixture("soft-fullscreen", [full with { Rotation = -19, CropRotation = 13 }], new() { ScreenX = 64, ScreenY = 40, ScreenWidth = 192, ScreenHeight = 120, AmbilightStyle = "Soft", AmbilightFullscreen = true, AmbilightBlur = 14, AmbilightSpread = 10 }),
            Fixture("contours-crop-hq", [full with { Rotation = 17, CropRotation = 34, CropLeftPct = .3, Opacity = .7 }, Source(2) with { CanvasX = 225, CanvasY = 140, CanvasWidth = 80, CanvasHeight = 50, ZIndex = 1, IsMirroredVertically = true }], new() { AmbilightStyle = "Contours", AmbilightEdgeDepth = 7, AmbilightEdgeMix = 0, AmbilightEdgeReach = 45, AmbilightCutoff = 5 }, 800, 600),
            Fixture("contours-fullscreen-hide", [full with { Rotation = -15, CropLeftPct = .12 }], new() { AmbilightStyle = "Contours", AmbilightFullscreen = true, HideSources = true, Hue = 40, Blur = true, ScreenX = 32, ScreenY = 20, ScreenWidth = 256, ScreenHeight = 160 }),
            Fixture("missing-frame", [full, Source(2) with { CanvasX = 200, ZIndex = 1 }], new() { AmbilightStyle = "Contours" }, active: [full.Id])
        ];
    }

    public static void Main(string[] args)
    {
        GeometryTests();
        if (args is ["--export", var path])
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var json = JsonSerializer.Serialize(new { version = 1, schema = "better.native-rendering-fixtures",
                provenance = "Synthetic colors only; generated from production metadata and settings classes.", cases = Fixtures() },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
            File.WriteAllText(path, json + Environment.NewLine);
        }
        Console.WriteLine($"PASS: {assertions} native rendering metadata assertions.");
    }
}
