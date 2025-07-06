using Microsoft.Win32;
using Windows.ApplicationModel;

namespace Better_SignalRGB_Screen_Capture.Helpers;

public static class StartupHelper
{
    private const string RunRegKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private static string AppName => "BetterSignalRGBCapture";
    private static string ExePath => System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;

    public static bool IsRegistered()
    {
        if (RuntimeHelper.IsMSIX)
        {
            try
            {
                var task = StartupTask.GetAsync("BetterSignalRGBCaptureTask").AsTask().Result;
                return task.State == StartupTaskState.Enabled;
            }
            catch { }
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegKey, false);
            return key?.GetValue(AppName) != null;
        }
        return false;
    }

    public static void SetStartOnBoot(bool enable)
    {
        if (RuntimeHelper.IsMSIX)
        {
            try
            {
                var task = StartupTask.GetAsync("BetterSignalRGBCaptureTask").AsTask().Result;
                if (enable && task.State != StartupTaskState.Enabled)
                {
                    task.RequestEnableAsync().AsTask().Wait();
                }
                else if (!enable && task.State == StartupTaskState.Enabled)
                {
                    task.Disable();
                }
            }
            catch { }
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegKey, true);
            if (key == null) return;
            if (enable)
            {
                key.SetValue(AppName, ExePath);
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
        }
    }
} 