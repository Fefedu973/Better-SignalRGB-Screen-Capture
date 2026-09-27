using System.Runtime.CompilerServices;
using Better_SignalRGB_Screen_Capture.Helpers;

namespace Better_SignalRGB_Screen_Capture;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // A blocking native wallpaper read runs in an owned child process. It must
        // never initialize the application's window, settings, tray or capture jobs.
        if (args.Length > 0 && args[0] == "--wallpaper-capture-worker")
            return WallpaperCaptureWorker.Run(args);

        return RunApplication();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApplication()
    {
        // Keep the generated WinUI startup contract for ordinary launches.
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }
}
