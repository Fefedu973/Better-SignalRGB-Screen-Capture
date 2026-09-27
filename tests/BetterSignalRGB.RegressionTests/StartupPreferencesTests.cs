using Better_SignalRGB_Screen_Capture.Core.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Helpers;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.Services;
using Microsoft.Extensions.Options;

namespace BetterSignalRGB.RegressionTests;

internal static class StartupPreferencesTests
{
    public static async Task RunAsync()
    {
        Assert.True(!RuntimeHelper.IsMSIX, "Startup preference tests never write real package settings");
        var files = new MemoryFiles();
        var options = Options.Create(new LocalSettingsOptions { ApplicationDataFolder = "RegressionTests-NoDiskAccess" });
        LocalSettingsService Open() => new(files, options);

        Assert.True(await StartupPreferences.ReadAutoStartRecordingAsync(Open()),
            "A fresh installation starts recording by default");
        Assert.Equal(0, files.Writes, "Reading the startup default does not create an explicit preference");

        await Open().SaveSettingAsync(StartupPreferences.AutoStartRecordingKey, false);
        Assert.True(!await StartupPreferences.ReadAutoStartRecordingAsync(Open()),
            "A persisted explicit opt-out survives restart and is not replaced by the default");
        await Open().SaveSettingAsync("BootInTray", true);
        Assert.True(!await StartupPreferences.ReadAutoStartRecordingAsync(Open()),
            "An unrelated startup preference preserves the recording opt-out");

        await Open().SaveSettingAsync(StartupPreferences.AutoStartRecordingKey, true);
        Assert.True(await StartupPreferences.ReadAutoStartRecordingAsync(Open()),
            "Re-enabling automatic recording persists across restart");

        await Open().SaveSettingAsync<bool?>(StartupPreferences.AutoStartRecordingKey, null);
        Assert.True(await StartupPreferences.ReadAutoStartRecordingAsync(Open()),
            "A legacy null preference uses the same default as an absent key");

        files.ReadError = new IOException("Simulated unreadable settings file");
        try
        {
            await StartupPreferences.ReadAutoStartRecordingAsync(Open());
            Assert.True(false, "An unreadable settings file must not silently enable capture");
        }
        catch (IOException exception)
        {
            Assert.True(ReferenceEquals(files.ReadError, exception),
                "Storage failure reaches the startup error boundary with its original exception");
        }
    }

    private sealed class MemoryFiles : IFileService
    {
        private Dictionary<string, object> _stored = new();
        public int Writes { get; private set; }
        public IOException? ReadError { get; set; }
        public T Read<T>(string folderPath, string fileName)
        {
            if (ReadError != null) throw ReadError;
            return (T)(object)new Dictionary<string, object>(_stored);
        }
        public void Save<T>(string folderPath, string fileName, T content)
        {
            _stored = new Dictionary<string, object>((IDictionary<string, object>)content!);
            Writes++;
        }
        public void Delete(string folderPath, string fileName) => throw new NotSupportedException();
    }
}
