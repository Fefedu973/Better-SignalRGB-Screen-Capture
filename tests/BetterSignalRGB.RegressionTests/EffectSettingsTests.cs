using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.Services;

namespace BetterSignalRGB.RegressionTests;

internal static class EffectSettingsTests
{
    public static async Task RunAsync()
    {
        var legacy = Newtonsoft.Json.JsonConvert.DeserializeObject<SignalRgbEffectSettings>(
            """{"Enabled":true,"PictureMode":"Cinema","Brightness":12}""")!;
        Assert.True(legacy.Enabled && !legacy.WebEnabled, "Existing SignalRGB-only settings keep the web output raw");
        Assert.True(!new SignalRgbEffectSettings().WebEnabled, "New profiles keep the raw web output by default");
        Assert.Equal("Cinema", legacy.PictureMode, "Migration preserves existing appearance choices");
        Assert.Equal(320d, legacy.ScreenWidth, "Legacy settings fill the complete output width");
        Assert.Equal(200d, legacy.ScreenHeight, "Legacy settings fill the complete output height");
        Assert.Equal("Classic", legacy.AmbilightStyle, "Migration preserves the original halo renderer");
        Assert.Equal(0, legacy.AmbilightCutoff, "Legacy settings do not suppress any halo colors");
        Assert.Equal(3, legacy.AmbilightEdgeDepth, "Legacy profiles gain the default contour sample depth without enabling Contours");
        Assert.Equal(2, legacy.AmbilightEdgeMix, "Legacy profiles gain the default edge smoothing");
        Assert.Equal(60, legacy.AmbilightEdgeReach, "Legacy profiles gain the default contour reach");
        Assert.Equal(50, legacy.AmbilightEdgeFade, "Legacy profiles gain the default contour fade");
        var legacySoft = Newtonsoft.Json.JsonConvert.DeserializeObject<SignalRgbEffectSettings>(
            """{"AmbilightStyle":"Soft","AmbilightBlur":17,"AmbilightSpread":42}""")!.Normalize();
        Assert.Equal("Soft", legacySoft.AmbilightStyle, "Migration does not replace an existing Soft profile with Contours");
        Assert.Equal(17, legacySoft.AmbilightBlur, "Migration preserves the saved legacy blur");
        Assert.Equal(42, legacySoft.AmbilightSpread, "Migration preserves the saved legacy spread");
        foreach (var signalEnabled in new[] { false, true })
        foreach (var webEnabled in new[] { false, true })
        {
            var modified = new SignalRgbEffectSettings { Enabled = signalEnabled, WebEnabled = webEnabled,
                PictureMode = "Vivid", Hue = 90, Brightness = 30, Saturation = 20, Blur = true,
                Ambilight = false, AmbilightFullscreen = true, HideSources = true, AmbilightBlur = 8,
                AmbilightSpread = 40, AmbilightSaturation = 8, AmbilightIntensity = 160,
                Interpolation = "pixelated", FrameRate = 29, ScreenX = 13.25, ScreenY = 17.75,
                ScreenWidth = 200.5, ScreenHeight = 155.5, AmbilightStyle = "Contours", AmbilightCutoff = 26,
                AmbilightEdgeDepth = 12, AmbilightEdgeMix = 16, AmbilightEdgeReach = 115, AmbilightEdgeFade = 83 };
            Assert.Equal(new SignalRgbEffectSettings { Enabled = signalEnabled, WebEnabled = webEnabled }, modified.ResetAppearance(),
                "Reset restores every appearance parameter and preserves both independent output switches");
            var roundTrip = Newtonsoft.Json.JsonConvert.DeserializeObject<SignalRgbEffectSettings>(
                Newtonsoft.Json.JsonConvert.SerializeObject(modified))!;
            Assert.Equal(modified, roundTrip.Normalize(), "Both activation flags persist independently with the appearance settings");
        }
        var webOnly = legacy with { Enabled = false, WebEnabled = true, ScreenX = 13.25, ScreenY = 17.75,
            ScreenWidth = 200.5, ScreenHeight = 155.5, AmbilightStyle = "Contours", AmbilightCutoff = 26,
            AmbilightEdgeDepth = 12, AmbilightEdgeMix = 16, AmbilightEdgeReach = 115, AmbilightEdgeFade = 83 };
        using (var wire = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(webOnly,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))))
        {
            Assert.True(wire.RootElement.GetProperty("webEnabled").GetBoolean(), "Web activation has the explicit camelCase wire name");
            Assert.True(!wire.RootElement.GetProperty("enabled").GetBoolean(), "Web activation does not enable SignalRGB control");
            Assert.Equal(13.25, wire.RootElement.GetProperty("screenX").GetDouble(), "Placement preserves fractional X coordinates on the wire");
            Assert.Equal(17.75, wire.RootElement.GetProperty("screenY").GetDouble(), "Placement preserves fractional Y coordinates on the wire");
            Assert.Equal(200.5, wire.RootElement.GetProperty("screenWidth").GetDouble(), "Placement preserves fractional width on the wire");
            Assert.Equal(155.5, wire.RootElement.GetProperty("screenHeight").GetDouble(), "Placement preserves fractional height on the wire");
            Assert.Equal("Contours", wire.RootElement.GetProperty("ambilightStyle").GetString(), "Contours has a stable wire style name");
            Assert.Equal(26, wire.RootElement.GetProperty("ambilightCutoff").GetInt32(), "Halo cutoff has a stable wire name");
            Assert.Equal(12, wire.RootElement.GetProperty("ambilightEdgeDepth").GetInt32(), "Contour depth has a stable camelCase wire name");
            Assert.Equal(16, wire.RootElement.GetProperty("ambilightEdgeMix").GetInt32(), "Contour smoothing has a stable camelCase wire name");
            Assert.Equal(115, wire.RootElement.GetProperty("ambilightEdgeReach").GetInt32(), "Contour reach has a stable camelCase wire name");
            Assert.Equal(83, wire.RootElement.GetProperty("ambilightEdgeFade").GetInt32(), "Contour fade has a stable camelCase wire name");
            Assert.Equal(webOnly, System.Text.Json.JsonSerializer.Deserialize<SignalRgbEffectSettings>(wire.RootElement.GetRawText(),
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
                "Preview and streaming JSON preserve the entire Contours configuration");
        }
        foreach (var style in new[] { "Classic", "Soft", "Contours" })
        {
            var selected = legacySoft with { AmbilightStyle = style, AmbilightEdgeDepth = 12, AmbilightEdgeMix = 16,
                AmbilightEdgeReach = 115, AmbilightEdgeFade = 83 };
            Assert.Equal(selected, selected.Normalize(), "Changing halo styles preserves both legacy and contour settings");
        }
        var minimumContour = new SignalRgbEffectSettings { AmbilightEdgeDepth = int.MinValue, AmbilightEdgeMix = int.MinValue,
            AmbilightEdgeReach = int.MinValue, AmbilightEdgeFade = int.MinValue }.Normalize();
        Assert.Equal(1, minimumContour.AmbilightEdgeDepth, "Contour samples always have positive depth");
        Assert.Equal(0, minimumContour.AmbilightEdgeMix, "Contour edge smoothing can be disabled");
        Assert.Equal(1, minimumContour.AmbilightEdgeReach, "Contour light always has positive reach");
        Assert.Equal(0, minimumContour.AmbilightEdgeFade, "Uniform contour brightness is supported");
        var maximumContour = new SignalRgbEffectSettings { AmbilightEdgeDepth = int.MaxValue, AmbilightEdgeMix = int.MaxValue,
            AmbilightEdgeReach = int.MaxValue, AmbilightEdgeFade = int.MaxValue }.Normalize();
        Assert.Equal(20, maximumContour.AmbilightEdgeDepth, "Contour sample depth is bounded to twenty percent");
        Assert.Equal(30, maximumContour.AmbilightEdgeMix, "Contour edge smoothing is bounded");
        Assert.Equal(200, maximumContour.AmbilightEdgeReach, "Contour reach is bounded");
        Assert.Equal(100, maximumContour.AmbilightEdgeFade, "Contour fade is bounded to one hundred percent");
        var normalized = new SignalRgbEffectSettings
        {
            Enabled = true, PictureMode = "unknown", FrameRate = 900, Hue = -999,
            Brightness = 500, Saturation = -500, AmbilightBlur = -1, AmbilightSpread = 200,
            AmbilightIntensity = 900, AmbilightSaturation = double.NaN, Interpolation = "unknown",
            ScreenX = double.NaN, ScreenY = double.PositiveInfinity, ScreenWidth = double.NegativeInfinity,
            ScreenHeight = double.NaN, AmbilightStyle = "unknown", AmbilightCutoff = 200
        }.Normalize();
        Assert.Equal(1, normalized.Version, "Effect wire format is versioned");
        Assert.Equal("Standard", normalized.PictureMode, "Invalid saved picture mode falls back predictably");
        Assert.Equal(30, normalized.FrameRate, "Effect cadence is bounded");
        Assert.Equal(-180, normalized.Hue, "Hue is bounded");
        Assert.Equal(100, normalized.Brightness, "Brightness is bounded");
        Assert.Equal(-100, normalized.Saturation, "Saturation is bounded");
        Assert.Equal(0, normalized.AmbilightBlur, "Zero glow blur is allowed");
        Assert.Equal(100, normalized.AmbilightSpread, "Glow spread is bounded");
        Assert.Equal(200, normalized.AmbilightIntensity, "Glow intensity is bounded");
        Assert.Equal(3d, normalized.AmbilightSaturation, "Nonfinite effect saturation cannot enter JSON");
        Assert.Equal("smooth", normalized.Interpolation, "Unknown interpolation becomes smooth");
        Assert.Equal(0d, normalized.ScreenX, "Nonfinite placement X returns to the origin");
        Assert.Equal(0d, normalized.ScreenY, "Nonfinite placement Y returns to the origin");
        Assert.Equal(320d, normalized.ScreenWidth, "Nonfinite placement width fills the canvas");
        Assert.Equal(200d, normalized.ScreenHeight, "Nonfinite placement height fills the canvas");
        Assert.Equal("Classic", normalized.AmbilightStyle, "Invalid halo style has the compatible default");
        Assert.Equal(100, normalized.AmbilightCutoff, "Halo cutoff is bounded");
        var outside = new SignalRgbEffectSettings { ScreenX = 400, ScreenY = 300, ScreenWidth = 256.5, ScreenHeight = 160.25 }.Normalize();
        Assert.Equal(63.5, outside.ScreenX, "Global placement stays inside the right output edge without rounding");
        Assert.Equal(39.75, outside.ScreenY, "Global placement stays inside the bottom output edge without rounding");
        var zero = new SignalRgbEffectSettings { ScreenWidth = 0, ScreenHeight = -1, ScreenX = -3, ScreenY = -1, AmbilightCutoff = -10 }.Normalize();
        Assert.Equal(1d, zero.ScreenWidth, "Global placement cannot have zero width");
        Assert.Equal(1d, zero.ScreenHeight, "Global placement cannot have negative height");
        Assert.Equal(0d, zero.ScreenX, "Global placement cannot leave the left edge");
        Assert.Equal(0, zero.AmbilightCutoff, "Negative cutoff returns to zero");

        var storage = new Storage { Value = normalized };
        var service = new SignalRgbEffectSettingsService(storage);
        var changes = new List<SignalRgbEffectSettings>();
        service.Changed += (_, settings) => changes.Add(settings);
        await Task.WhenAll(service.InitializeAsync(), service.InitializeAsync());
        Assert.Equal(1, storage.Reads, "Concurrent initialization reads persisted effect settings once");
        Assert.Equal(normalized, service.Current, "Persisted effect controls are restored");
        Assert.Equal(1, changes.Count, "Restored configuration publishes one atomic change");

        var latest = normalized with { Brightness = 42, Ambilight = false };
        await service.UpdateAsync(latest);
        Assert.Equal(latest, storage.Value, "An appearance edit persists a complete immutable record");
        Assert.Equal(latest, service.Current, "Published appearance matches confirmed persistence");
        await service.UpdateAsync(latest);
        Assert.Equal(2, changes.Count, "Unchanged appearance does not trigger another sender update");

        storage.FailSave = true;
        try { await service.UpdateAsync(latest with { Brightness = -12 }); Assert.True(false, "Failed persistence must be reported"); }
        catch (IOException) { Assert.True(true, "Failed persistence is reported to the settings UI"); }
        Assert.Equal(latest, service.Current, "Failed writes do not change the running effect");
        Assert.Equal(2, changes.Count, "Failed writes do not publish divergent settings");

        storage = new Storage { Value = normalized };
        service = new SignalRgbEffectSettingsService(storage);
        await service.UpdateAsync(latest);
        await service.InitializeAsync();
        Assert.Equal(0, storage.Reads, "Delayed initialization cannot overwrite an already persisted edit");
        Assert.Equal(latest, service.Current, "Early edits survive delayed initialization");

        storage = new Storage { Value = legacy };
        service = new SignalRgbEffectSettingsService(storage);
        await service.InitializeAsync();
        Assert.True(!service.Current.WebEnabled, "Loading a legacy profile does not turn on web effects");
        await service.UpdateAsync(webOnly);
        Assert.True(service.Current.WebEnabled && !service.Current.Enabled, "Web-only activation is published independently");
        Assert.Equal(webOnly, storage.Value, "Web-only activation is durable");
        var reloadedService = new SignalRgbEffectSettingsService(storage);
        await reloadedService.InitializeAsync();
        Assert.Equal(webOnly, reloadedService.Current, "Restarting the settings service preserves all four Contours controls");
        await service.UpdateAsync(webOnly with { Enabled = true });
        Assert.True(service.Current.WebEnabled && service.Current.Enabled, "Both output targets can be enabled together");
        await service.UpdateAsync(service.Current with { WebEnabled = false });
        Assert.True(service.Current.Enabled && !service.Current.WebEnabled, "Disabling web effects preserves SignalRGB activation");
    }

    private sealed class Storage : ILocalSettingsService
    {
        public SignalRgbEffectSettings? Value;
        public int Reads;
        public bool FailSave;
        public async Task<T?> ReadSettingAsync<T>(string key)
        {
            Reads++;
            await Task.Yield();
            return Value is T typed ? typed : default;
        }
        public Task SaveSettingAsync<T>(string key, T value)
        {
            if (FailSave) throw new IOException("Simulated settings failure");
            Value = (SignalRgbEffectSettings)(object)value!;
            return Task.CompletedTask;
        }
    }
}
