using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BetterSignalRGB.GraphicsTests;

// Only this process's borderless synthetic surface is moved/resized. It never
// activates, and remains at the bottom of the z-order behind the user's windows.
internal sealed class SyntheticWindow : IAsyncDisposable
{
    private readonly TaskCompletionSource<nint> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly WindowProcedure _procedure;
    private int _stage;
    public nint Handle { get; private set; }
    public string Title { get; } = "BetterSignalRGB synthetic HDR test " + Guid.NewGuid().ToString("N");

    private SyntheticWindow(int x, int y)
    {
        _procedure = (window, message, wParam, lParam) =>
        {
            if (message == 0x8001) { while (_work.TryDequeue(out var work)) work(); return 0; }
            if (message == 0xf)
            {
                var dc = GetDC(window);
                GetClientRect(window, out var bounds);
                var midX = bounds.Right / 2; var midY = bounds.Bottom / 2;
                Fill(dc, new Rectangle(0, 0, midX, midY), 0xffffff);
                Fill(dc, new Rectangle(midX, 0, bounds.Right, midY), 0x808080);
                Fill(dc, new Rectangle(0, midY, midX, bounds.Bottom), 0);
                Fill(dc, new Rectangle(midX, midY, bounds.Right, bounds.Bottom), _stage switch { 0 => 0xff0000u, 1 => 0x00ff00u, _ => 0x0000ffu });
                ReleaseDC(window, dc); ValidateRect(window, 0); return 0;
            }
            if (message == 0x113) { InvalidateRect(window, 0, false); return 0; }
            if (message == 2) PostQuitMessage(0);
            return DefWindowProc(window, message, wParam, lParam);
        };
        var thread = new Thread(() => Run(x, y)) { IsBackground = true, Name = "Synthetic HDR capture fixture" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
    }

    public static async Task<SyntheticWindow> CreateAsync(int x, int y)
    {
        var result = new SyntheticWindow(x, y);
        result.Handle = await result._ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return result;
    }

    public async Task ResizeAsync(int width, int height, int stage)
    {
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Enqueue(() =>
        {
            try
            {
                _stage = stage;
                if (!SetWindowPos(Handle, new nint(1), 0, 0, width, height, 0x2 | 0x10))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                InvalidateRect(Handle, 0, false); UpdateWindow(Handle); DwmFlush();
                complete.TrySetResult();
            }
            catch (Exception ex) { complete.TrySetException(ex); }
        });
        if (!PostMessage(Handle, 0x8001, 0, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        await complete.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static void Fill(nint dc, Rectangle rectangle, uint color)
    {
        var brush = CreateSolidBrush(color);
        try { FillRect(dc, ref rectangle, brush); }
        finally { DeleteObject(brush); }
    }

    private void Run(int x, int y)
    {
        var instance = GetModuleHandle(null); var className = "HdrSmoke_" + Guid.NewGuid().ToString("N");
        try
        {
            SetThreadDpiAwarenessContext(new nint(-4));
            var windowClass = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = Marshal.GetFunctionPointerForDelegate(_procedure),
                Instance = instance, ClassName = className };
            if (RegisterClassEx(ref windowClass) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            var handle = CreateWindowEx(0x08000000, className, Title, 0x80000000, x, y, 320, 200, 0, 0, instance, 0);
            if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            // HWND_BOTTOM + SWP_NOACTIVATE + SWP_SHOWWINDOW avoids even briefly
            // covering another application with a topmost test surface.
            if (!SetWindowPos(handle, new nint(1), x, y, 320, 200, 0x10 | 0x40)) throw new Win32Exception(Marshal.GetLastWin32Error());
            UpdateWindow(handle); SetTimer(handle, 1, 40, 0); DwmFlush(); _ready.TrySetResult(handle);
            while (GetMessage(out var message, 0, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }
        }
        catch (Exception ex) { _ready.TrySetException(ex); _closed.TrySetException(ex); }
        finally { UnregisterClass(className, instance); _closed.TrySetResult(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Handle != 0) { PostMessage(Handle, 0x10, 0, 0); Handle = 0; }
        await _closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Size, Style; public nint Procedure; public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background; public string? MenuName; public string ClassName; public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [StructLayout(LayoutKind.Sequential)] private record struct Rectangle(int Left, int Top, int Right, int Bottom);
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint extended, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string className, nint instance);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] private static extern int GetMessage(out Message message, nint window, uint first, uint last);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)] private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Rectangle rectangle);
    [DllImport("user32.dll")] private static extern int FillRect(nint dc, ref Rectangle rectangle, nint brush);
    [DllImport("user32.dll")] private static extern bool ValidateRect(nint window, nint rectangle);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(nint window, nint rectangle, bool erase);
    [DllImport("user32.dll")] private static extern nuint SetTimer(nint window, nuint id, uint milliseconds, nint callback);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint color);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
}
