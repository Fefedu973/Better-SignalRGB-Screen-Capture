using Better_SignalRGB_Screen_Capture.Helpers;
using Better_SignalRGB_Screen_Capture.Models;

internal static class DeviceIdentityTests
{
    private sealed record Device(string Id, string Name);
    public static void Run()
    {
        var devices = new[] { new Device("first", "Same name"), new Device("second", "Same name"), new Device("third", "Unique") };
        Device? Select(string id, string name) => CaptureSourceFactory.FindDevice(devices, id, name, device => device.Id, device => device.Name);
        if (Select("SECOND", "Same name") != devices[1]) throw new InvalidOperationException("Stable device ID lost to an earlier friendly-name match.");
        if (Select("missing", "Same name") != null) throw new InvalidOperationException("Missing stable device ID selected a different camera/monitor.");
        if (Select("", "Same name") != null) throw new InvalidOperationException("Ambiguous legacy device name must remain unavailable.");
        if (Select("", "Unique") != devices[2]) throw new InvalidOperationException("Unique legacy device name could not recover.");
        var windows = new[] {
            new CaptureSourceFactory.WindowIdentity(11, 101, "First", "app", @"C:\App\app.exe"),
            new CaptureSourceFactory.WindowIdentity(12, 101, "Second", "app", @"C:\App\app.exe"),
            new CaptureSourceFactory.WindowIdentity(13, 102, "First", "app", @"C:\App\app.exe"),
            new CaptureSourceFactory.WindowIdentity(14, 103, "Other", "app", @"C:\Other\app.exe") };
        var source = new SourceItem { Type = SourceType.Process, ProcessId = 101, ProcessPath = @"C:\App\app.exe", WindowHandle = 12, WindowTitle = "Old title" };
        CaptureSourceFactory.WindowIdentity? Selected() => CaptureSourceFactory.SelectWindow(windows, source);
        if (Selected() != windows[1]) throw new InvalidOperationException("Exact HWND should survive a title change.");
        source.WindowHandle = 13; source.WindowTitle = "Second";
        if (Selected() != windows[1]) throw new InvalidOperationException("A reused handle must not select another process instance.");
        source.ProcessId = 999;
        if (Selected() != windows[1]) throw new InvalidOperationException("Unique path+title recovery failed.");
        source.WindowTitle = "First";
        if (Selected() != null) throw new InvalidOperationException("Ambiguous path+title selected an arbitrary window.");
        source.WindowTitle = null; source.WindowHandle = null; source.ProcessId = 101;
        if (Selected() != null) throw new InvalidOperationException("Legacy PID with multiple windows must be unavailable.");
        source.ProcessId = 103;
        if (Selected() != null) throw new InvalidOperationException("Matching process name must not override an exact executable path.");
        source.ProcessPath = @"C:\Other\app.exe";
        if (Selected() != windows[3]) throw new InvalidOperationException("Unique legacy path+PID should recover.");
        Console.WriteLine("PASS: exact device identity, duplicate friendly names, missing devices and unique legacy recovery.");
        Console.WriteLine("PASS: exact HWND, title changes, reused PID/handle rejection, unique recovery and ambiguous windows.");
    }
}
