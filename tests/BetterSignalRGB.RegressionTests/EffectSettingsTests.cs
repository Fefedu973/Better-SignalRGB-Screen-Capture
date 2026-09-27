using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.Services;

namespace BetterSignalRGB.RegressionTests;

internal static class EffectSettingsTests
{
    public static async Task RunAsync()
    {
        var normalized = new SignalRgbEffectSettings
        {
            Enabled = true, PictureMode = "unknown", FrameRate = 900, Hue = -999,
            Brightness = 500, Saturation = -500, AmbilightBlur = -1, AmbilightSpread = 200,
            AmbilightIntensity = 900, AmbilightSaturation = double.NaN, Interpolation = "unknown"
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
