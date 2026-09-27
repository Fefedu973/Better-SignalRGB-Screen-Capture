using Microsoft.Win32;
using Windows.ApplicationModel;

namespace Better_SignalRGB_Screen_Capture.Helpers;

public static class StartupHelper
{
    private const string RunRegKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string AppName = "BetterSignalRGBCapture";
    private const string StartupTaskId = "BetterSignalRGBCaptureTask";

    public static async Task<bool> IsRegisteredAsync()
    {
        if (RuntimeHelper.IsMSIX)
        {
            var task = await StartupTask.GetAsync(StartupTaskId);
            return IsEnabled(task.State);
        }
        return await Task.Run(() =>
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegKey, false);
            return key?.GetValue(AppName) != null;
        });
    }

    /// <summary>Returns the actual registration state; Windows may refuse the requested change.</summary>
    public static async Task<bool> SetStartOnBootAsync(bool enable)
    {
        if (RuntimeHelper.IsMSIX)
        {
            var task = await StartupTask.GetAsync(StartupTaskId);
            var state = task.State;
            if (enable && !IsEnabled(state))
            {
                state = await task.RequestEnableAsync();
            }
            else if (!enable && state == StartupTaskState.Enabled)
            {
                task.Disable();
                state = task.State;
            }
            return IsEnabled(state);
        }

        return await Task.Run(() =>
        {
            using var key = enable ? Registry.CurrentUser.CreateSubKey(RunRegKey, writable: true)
                : Registry.CurrentUser.OpenSubKey(RunRegKey, writable: true);
            if (key == null) return false;
            if (enable)
            {
                var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The application executable path is unavailable.");
                // Run values are command lines, so paths containing spaces need quotes.
                key.SetValue(AppName, $"\"{executable}\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
            return key.GetValue(AppName) != null;
        });
    }

    private static bool IsEnabled(StartupTaskState state) =>
        state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
}
