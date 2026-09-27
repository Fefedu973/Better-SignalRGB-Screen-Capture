using Better_SignalRGB_Screen_Capture.Core.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Helpers;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.Services;

using Microsoft.Extensions.Options;

namespace BetterSignalRGB.RegressionTests;

internal static class LocalSettingsTransactionTests
{
    public static async Task RunAsync()
    {
        // This suite tests only the unpackaged file branch. Never write real package preferences.
        Assert.True(!RuntimeHelper.IsMSIX, "File settings tests run outside an MSIX package");
        var files = new FailOnceFiles();
        var options = Options.Create(new LocalSettingsOptions { ApplicationDataFolder = "RegressionTests-NoDiskAccess" });
        var local = new LocalSettingsService(files, options);
        var original = new SignalRgbEffectSettings { Hue = 23 };
        await local.SaveSettingAsync("SignalRgbEffect", original);
        var effect = new SignalRgbEffectSettingsService(local);
        await effect.InitializeAsync();

        files.FailNextSave = true;
        try
        {
            await effect.UpdateAsync(original with { Enabled = true, Hue = 117 });
            Assert.True(false, "A failed effect write must be reported");
        }
        catch (IOException) { Assert.True(true, "Effect write failure reaches its caller"); }
        Assert.Equal(original, effect.Current, "Failed effect settings remain unpublished");
        Assert.Equal(original, await local.ReadSettingAsync<SignalRgbEffectSettings>("SignalRgbEffect"),
            "A failed write cannot contaminate the local settings cache");

        // A later unrelated save previously committed the failed effect edit as a side effect.
        await local.SaveSettingAsync("BootInTray", true);
        var reopened = new LocalSettingsService(files, options);
        var restoredEffect = new SignalRgbEffectSettingsService(reopened);
        await restoredEffect.InitializeAsync();
        Assert.Equal(original, restoredEffect.Current, "An unrelated successful write cannot resurrect a rejected effect edit");
        Assert.True(await reopened.ReadSettingAsync<bool>("BootInTray"), "The unrelated preference still persists");

        files.FailNextSave = true;
        try
        {
            await local.SaveSettingAsync("NeverCommitted", 99);
            Assert.True(false, "A failed first write must be reported");
        }
        catch (IOException) { Assert.True(true, "A failed new preference is reported"); }
        Assert.True(await local.ReadSettingAsync<int?>("NeverCommitted") is null,
            "A failed first write does not create a cached preference");
        await local.SaveSettingAsync("StreamingPort", 8092);
        reopened = new LocalSettingsService(files, options);
        Assert.True(await reopened.ReadSettingAsync<int?>("NeverCommitted") is null,
            "A later successful save does not persist a failed new preference");

        await Task.WhenAll(local.SaveSettingAsync("Left", 1), local.SaveSettingAsync("Right", 2));
        reopened = new LocalSettingsService(files, options);
        Assert.Equal(1, await reopened.ReadSettingAsync<int>("Left"), "Concurrent successful saves preserve the first key");
        Assert.Equal(2, await reopened.ReadSettingAsync<int>("Right"), "Concurrent successful saves preserve the second key");
    }

    private sealed class FailOnceFiles : IFileService
    {
        private Dictionary<string, object> _stored = new();
        public bool FailNextSave;

        public T Read<T>(string folderPath, string fileName) => (T)(object)new Dictionary<string, object>(_stored);

        public void Save<T>(string folderPath, string fileName, T content)
        {
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new IOException("Simulated disk write failure");
            }
            _stored = new Dictionary<string, object>((IDictionary<string, object>)content!);
        }

        public void Delete(string folderPath, string fileName) => throw new NotSupportedException();
    }
}
