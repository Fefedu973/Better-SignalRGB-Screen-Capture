using Better_SignalRGB_Screen_Capture.Contracts.Services;

namespace Better_SignalRGB_Screen_Capture.Helpers;

internal static class StartupPreferences
{
    public const string AutoStartRecordingKey = "AutoStartRecordingOnBoot";
    public const bool AutoStartRecordingByDefault = true;

    // Nullable deserialization distinguishes an absent preference from a saved opt-out.
    public static async Task<bool> ReadAutoStartRecordingAsync(ILocalSettingsService settings) =>
        await settings.ReadSettingAsync<bool?>(AutoStartRecordingKey) ?? AutoStartRecordingByDefault;
}
