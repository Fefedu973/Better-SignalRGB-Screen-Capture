using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Better_SignalRGB_Screen_Capture.Helpers;

/// <summary>A temporary opaque cover immediately above the desktop and below existing applications.</summary>
internal static class WallpaperDesktopOcclusion
{
    public static Task RunAsync(nint target, Func<Func<bool>, Task> capture) => Task.Run(() =>
    {
        using var dpi = new WallpaperNative.DpiScope();
        var foreground = GetForegroundWindow();
        var root = GetAncestor(target, 2);
        var className = new StringBuilder(256); GetClassName(root, className, className.Capacity);
        if (className.ToString() is not ("WorkerW" or "Progman") || !GetWindowRect(target, out var rectangle))
            throw new InvalidOperationException("The wallpaper host does not have a valid desktop root.");
        var previous = GetWindow(root, 3);
        var cover = CreateWindowEx(0x08000080, "STATIC", "Wallpaper capture test cover", 0x88000004,
            rectangle.Left, rectangle.Top, rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top, 0, 0, 0, 0);
        if (cover == 0) throw new InvalidOperationException("The desktop-only test cover could not be created.");
        try
        {
            if (!SetWindowPos(cover, previous, rectangle.Left, rectangle.Top,
                    rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top, 0x0010 | 0x0040))
                throw new InvalidOperationException("The desktop-only test cover could not be placed safely.");
            UpdateWindow(cover); PumpFor(150);
            bool IsValid() => IsWindowVisible(cover) && GetWindow(cover, 2) == root &&
                GetWindowRect(cover, out var bounds) && GetWindowRect(target, out var actual) &&
                bounds.Left <= actual.Left && bounds.Top <= actual.Top && bounds.Right >= actual.Right && bounds.Bottom >= actual.Bottom;
            if (DwmFlush() != 0 || !IsValid() || GetForegroundWindow() != foreground)
                throw new InvalidOperationException("Desktop occlusion could not be established without changing the foreground.");
            // The user may focus another application while capture runs. Prove that
            // creating our cover did not activate it, then validate only occlusion.
            PumpFor(600);
            var task = capture(IsValid);
            while (!task.IsCompleted) PumpFor(20);
            task.GetAwaiter().GetResult();
            if (!IsValid()) throw new InvalidOperationException("Desktop occlusion changed before capture completed.");
        }
        finally
        {
            DestroyWindow(cover);
            Console.WriteLine($"Wallpaper cover removed={!IsWindow(cover)}; foreground unchanged={GetForegroundWindow() == foreground}.");
        }
    });

    private static void PumpFor(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds)
        {
            while (PeekMessage(out var message, 0, 0, 0, 1))
            { TranslateMessage(ref message); DispatchMessage(ref message); }
            Thread.Sleep(10);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Hwnd; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder value, int maximum);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rectangle);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint data);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(nint window);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, nint window, uint minimum, uint maximum, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message message);
}
