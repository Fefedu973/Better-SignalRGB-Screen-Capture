using Better_SignalRGB_Screen_Capture.Helpers;
using System.Runtime.InteropServices;
using Microsoft.UI; // for WindowId
using WinRT;

using Windows.UI.ViewManagement;

namespace Better_SignalRGB_Screen_Capture;

public sealed partial class MainWindow : WindowEx
{
    private Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue;

    private UISettings settings;

    public MainWindow()
    {
        InitializeComponent();

        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets/WindowIcon.ico"));
        Content = null;
        Title = "AppDisplayName".GetLocalized();

        // Theme change code picked from https://github.com/microsoft/WinUI-Gallery/pull/1239
        dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        settings = new UISettings();
        settings.ColorValuesChanged += Settings_ColorValuesChanged; // cannot use FrameworkElement.ActualThemeChanged event

        TitleBarHelper.ApplySystemThemeToCaptionButtons();

        // Subscribe to AppWindow Closing event to minimize to tray instead of exiting
        AppWindow.Closing += OnAppWindowClosing;

        RegisterMinimizeHook();
    }

    // this handles updating the caption button colors correctly when indows system theme is changed
    // while the app is open
    private void Settings_ColorValuesChanged(UISettings sender, object args)
    {
        // This calls comes off-thread, hence we will need to dispatch it to current app's thread
        dispatcherQueue.TryEnqueue(() =>
        {
            TitleBarHelper.ApplySystemThemeToCaptionButtons();
        });
    }

    private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        // Cancel close and hide to tray
        args.Cancel = true;
        this.Hide();
    }

    private void RegisterMinimizeHook()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _wndProc = new SubclassProc(WndProc);
        SetWindowSubclass(hwnd, _wndProc, IntPtr.Zero, IntPtr.Zero);
    }

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);
    private SubclassProc _wndProc;

    private const uint WM_SIZE = 0x0005;
    private const int SIZE_MINIMIZED = 6;

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (msg == WM_SIZE && wParam.ToInt32() == SIZE_MINIMIZED)
        {
            // Hide to tray
            this.Hide();
            return IntPtr.Zero;
        }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
}
