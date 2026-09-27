using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace Better_SignalRGB_Screen_Capture.Helpers;

/// <summary>Finds the existing wallpaper-only shell host. Never captures Progman or the desktop icon tree.</summary>
public static class WallpaperEngineSourceDiscovery
{
    public sealed record Target(nint Handle, int ShellProcessId, Rectangle HostBounds, Rectangle MonitorBounds, string MonitorDeviceId, IReadOnlySet<uint> RendererProcesses);
    private static readonly string[] RendererNames = ["wallpaper32", "wallpaper64", "webwallpaper32", "webwallpaper64", "edgewallpaper64"];

    public static bool IsAvailable(string monitorDeviceId) => TryDiscover(monitorDeviceId) is not null;

    public static Target? TryDiscover(string monitorDeviceId)
    {
        using var dpi = new WallpaperNative.DpiScope();
        var monitor = FindMonitor(monitorDeviceId);
        if (monitor is null) return null;
        var renderers = FindRendererProcesses();
        if (renderers.Count == 0) return null;
        var candidates = new List<Target>();
        var seen = new HashSet<nint>();
        var shellOwners = new Dictionary<uint, bool>();
        void Inspect(nint window)
        {
            if (!seen.Add(window) || WallpaperNative.ClassName(window) != "WorkerW" || !WallpaperNative.IsWindowVisible(window)) return;
            WallpaperNative.GetWindowThreadProcessId(window, out var owner);
            if (!shellOwners.TryGetValue(owner, out var isExplorer)) shellOwners[owner] = isExplorer = IsExplorer(owner);
            if (!isExplorer || !WallpaperNative.GetWindowRect(window, out var bounds)) return;
            var rectangle = bounds.ToRectangle();
            if (rectangle.Width <= 0 || rectangle.Height <= 0 || !rectangle.IntersectsWith(monitor.Value.Bounds)) return;
            var hasRenderer = false; var hasIcons = false;
            WallpaperNative.EnumChildWindows(window, (child, _) =>
            {
                var name = WallpaperNative.ClassName(child);
                if (name is "SHELLDLL_DefView" or "SysListView32") hasIcons = true;
                WallpaperNative.GetWindowThreadProcessId(child, out var pid);
                if (renderers.Contains(pid) && WallpaperNative.IsWindowVisible(child)) hasRenderer = true;
                return true;
            }, 0);
            if (IsSafeHost("WorkerW", true, hasRenderer, hasIcons))
                candidates.Add(new(window, (int)owner, rectangle, monitor.Value.Bounds, monitor.Value.Device, renderers));
        }
        WallpaperNative.EnumWindows((root, _) =>
        {
            Inspect(root);
            if (WallpaperNative.ClassName(root) is "Progman" or "WorkerW")
                WallpaperNative.EnumChildWindows(root, (child, _) => { Inspect(child); return true; }, 0);
            return true;
        }, 0);
        // Select the smallest safe host covering the largest part of this monitor.
        return candidates.OrderByDescending(target => Area(Rectangle.Intersect(target.HostBounds, target.MonitorBounds)))
            .ThenBy(target => Area(target.HostBounds)).FirstOrDefault();
    }

    internal static bool IsSafeHost(string className, bool isExplorer, bool hasRenderer, bool hasIcons) =>
        className == "WorkerW" && isExplorer && hasRenderer && !hasIcons;

    public static bool StillOwnsHost(Target target)
    {
        if (!WallpaperNative.IsWindow(target.Handle) || !WallpaperNative.IsWindowVisible(target.Handle) ||
            WallpaperNative.ClassName(target.Handle) != "WorkerW") return false;
        WallpaperNative.GetWindowThreadProcessId(target.Handle, out var owner);
        if (owner != target.ShellProcessId) return false;
        var hasIcons = false; var hasRenderer = false;
        WallpaperNative.EnumChildWindows(target.Handle, (child, _) =>
        {
            if (WallpaperNative.ClassName(child) is "SHELLDLL_DefView" or "SysListView32") hasIcons = true;
            WallpaperNative.GetWindowThreadProcessId(child, out var pid);
            if (target.RendererProcesses.Contains(pid) && WallpaperNative.IsWindowVisible(child)) hasRenderer = true;
            return !hasIcons;
        }, 0);
        return !hasIcons && hasRenderer && WallpaperNative.GetWindowRect(target.Handle, out var rectangle) && rectangle.ToRectangle() == target.HostBounds;
    }

    private static long Area(Rectangle rectangle) => (long)rectangle.Width * rectangle.Height;

    private static bool IsExplorer(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)pid));
            return string.Equals(process.MainModule?.FileName,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or OverflowException) { return false; }
    }

    private static HashSet<uint> FindRendererProcesses()
    {
        var result = new HashSet<uint>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var name = process.ProcessName;
                    if (!RendererNames.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    var path = process.MainModule?.FileName;
                    var directory = path is null ? null : Path.GetDirectoryName(path);
                    if (directory is null) continue;
                    var root = name.StartsWith("wallpaper", StringComparison.Ordinal) ? directory : Path.GetDirectoryName(directory);
                    if (root is not null && File.Exists(Path.Combine(root, "version.json")) &&
                        (File.Exists(Path.Combine(root, "wallpaper64.exe")) || File.Exists(Path.Combine(root, "wallpaper32.exe"))))
                        result.Add((uint)process.Id);
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return result;
    }

    private static (Rectangle Bounds, string Device)? FindMonitor(string device)
    {
        (Rectangle Bounds, string Device)? result = null;
        WallpaperNative.EnumDisplayMonitors(0, 0, (nint monitor, nint _, ref WallpaperNative.Rect unused, nint state) =>
        {
            var info = new WallpaperNative.MonitorInfo { Size = Marshal.SizeOf<WallpaperNative.MonitorInfo>(), Device = string.Empty };
            if (WallpaperNative.GetMonitorInfo(monitor, ref info) &&
                (string.Equals(device, info.Device, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(device) && (info.Flags & 1) != 0))
                result = (info.Monitor.ToRectangle(), info.Device);
            return true;
        }, 0);
        return result;
    }
}

internal static class WallpaperNative
{
    internal sealed class DpiScope : IDisposable
    {
        private readonly nint _previous = SetThreadDpiAwarenessContext(-4);
        public void Dispose() { if (_previous != 0) SetThreadDpiAwarenessContext(_previous); }
    }
    internal static string ClassName(nint window) { var buffer = new StringBuilder(256); GetClassName(window, buffer, buffer.Capacity); return buffer.ToString(); }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct MonitorInfo
    {
        public int Size; public Rect Monitor, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    internal delegate bool WindowCallback(nint window, nint parameter);
    internal delegate bool MonitorCallback(nint monitor, nint dc, ref Rect rectangle, nint parameter);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] internal static extern bool EnumChildWindows(nint parent, WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] internal static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint parameter);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)] internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int count);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint window, out Rect rectangle);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
}
