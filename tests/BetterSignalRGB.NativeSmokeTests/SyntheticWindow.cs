using System.ComponentModel;
using System.Runtime.InteropServices;

internal sealed class SyntheticWindow : IAsyncDisposable
{
    private readonly TaskCompletionSource<nint> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly WindowProcedure _procedure;
    public nint Handle { get; private set; }
    public string Title { get; } = "BetterSignalRGB synthetic capture " + Guid.NewGuid().ToString("N");

    private SyntheticWindow(uint color)
    {
        _procedure = (window, message, wParam, lParam) =>
        {
            if (message == 0x000f)
            {
                var dc = GetDC(window); var paint = CreateSolidBrush(color);
                GetClientRect(window, out var bounds); FillRect(dc, ref bounds, paint);
                DeleteObject(paint); ReleaseDC(window, dc); ValidateRect(window, 0);
                return 0;
            }
            if (message == 0x0113) { InvalidateRect(window, 0, false); return 0; }
            if (message == 2) PostQuitMessage(0);
            return DefWindowProc(window, message, wParam, lParam);
        };
        var thread = new Thread(() => Run(color)) { IsBackground = true, Name = "Synthetic capture window" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
    }

    public static async Task<SyntheticWindow> CreateAsync(uint color)
    {
        var result = new SyntheticWindow(color);
        result.Handle = await result._ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return result;
    }

    private void Run(uint color)
    {
        var brush = CreateSolidBrush(color);
        var instance = GetModuleHandle(null);
        var className = "CaptureSmoke_" + Guid.NewGuid().ToString("N");
        try
        {
            var windowClass = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = Marshal.GetFunctionPointerForDelegate(_procedure),
                Instance = instance, Background = brush, ClassName = className };
            if (RegisterClassEx(ref windowClass) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            // The temporary, nonactivating synthetic surface contains only solid color.
            // No user's window is selected, moved, resized or brought to the foreground.
            var handle = CreateWindowEx(0x08000000, className, Title, 0x90000000, 240, 160, 300, 180,
                0, 0, instance, 0);
            if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            UpdateWindow(handle);
            SetTimer(handle, 1, 100, 0); // Exercise an actively repainting DWM surface.
            // Wait for this synthetic paint to reach DWM before beginning WGC.
            DwmFlush();
            _ready.TrySetResult(handle);
            while (GetMessage(out var message, 0, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }
        }
        catch (Exception error) { _ready.TrySetException(error); _closed.TrySetException(error); }
        finally { UnregisterClass(className, instance); DeleteObject(brush); _closed.TrySetResult(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Handle != 0) { PostMessage(Handle, 0x0010, 0, 0); Handle = 0; }
        await _closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style; public nint Procedure; public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background; public string? MenuName; public string ClassName; public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [StructLayout(LayoutKind.Sequential)] private struct Rectangle { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint extended, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string className, nint instance);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] private static extern int GetMessage(out Message message, nint window, uint first, uint last);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
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
