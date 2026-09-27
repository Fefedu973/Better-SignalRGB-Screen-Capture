using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;

namespace Better_SignalRGB_Screen_Capture.Helpers;

/// <summary>Physical desktop bounds and current display color state. Null HDR/white values mean unknown.</summary>
public sealed record DisplayColorState(bool? HdrEnabled, double? SdrWhiteNits, string? Name,
    string? DeviceName, Rectangle Bounds, nint MonitorHandle, string? Error)
{
    public bool IsKnown => HdrEnabled.HasValue;
}

/// <summary>
/// Read-only display queries, intended for capture workers. The active topology/color
/// snapshot is cached for at most one second; no UI dispatcher or HDR-setting API is used.
/// </summary>
public static class DisplayColorInfo
{
    private static readonly DisplayColorCache Cache = new(QueryDisplays, () => Environment.TickCount64);

    public static IReadOnlyList<DisplayColorState> Enumerate() => Cache.Get().Displays;

    public static DisplayColorState ForDeviceName(string? deviceName)
    {
        var snapshot = Cache.Get();
        return Resolve(snapshot.Displays, deviceName, default, 0, snapshot.Error);
    }

    public static DisplayColorState ForMonitor(nint monitor)
    {
        if (monitor == 0) return Unknown(null, default, monitor, "No monitor was supplied.");
        var snapshot = Cache.Get();
        var known = snapshot.Displays.FirstOrDefault(display => display.MonitorHandle == monitor);
        if (known != null) return known;
        var info = Native.MonitorInfo.Create();
        if (!Native.GetMonitorInfo(monitor, ref info))
            return Unknown(null, default, monitor, "The monitor is no longer available.");
        return Resolve(snapshot.Displays, info.DeviceName, info.Monitor.ToRectangle(), monitor, snapshot.Error);
    }

    /// <summary>Uses the monitor with the largest window intersection, or the nearest monitor when off screen.</summary>
    public static DisplayColorState ForWindow(nint window)
    {
        if (window == 0 || !Native.IsWindow(window))
            return Unknown(null, default, 0, "The source window is no longer available.");
        return ForMonitor(Native.MonitorFromWindow(window, 2));
    }

    internal static DisplayColorState Resolve(IReadOnlyList<DisplayColorState> displays, string? deviceName,
        Rectangle fallbackBounds, nint monitor, string? error = null)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return Unknown(deviceName, fallbackBounds, monitor, error ?? "No display name was supplied.");
        var matches = displays.Where(display => string.Equals(display.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0) return Unknown(deviceName, fallbackBounds, monitor, error ?? "The display is not in the active topology.");
        var first = matches[0];
        if (matches.Any(display => display.HdrEnabled != first.HdrEnabled || display.SdrWhiteNits != first.SdrWhiteNits))
            return Unknown(deviceName, fallbackBounds, monitor, "Cloned display targets report different HDR or SDR white levels.");
        return first with
        {
            MonitorHandle = monitor != 0 ? monitor : first.MonitorHandle,
            Bounds = first.Bounds.Width > 0 && first.Bounds.Height > 0 ? first.Bounds : fallbackBounds
        };
    }

    internal static DisplayColorState Decode(string? name, string? deviceName, Rectangle bounds,
        int color2Result, Native.AdvancedColor2 color2, int legacyResult, Native.AdvancedColor legacy,
        int whiteResult, uint whiteLevel)
    {
        bool? hdr = color2Result == 0 ? color2.ActiveColorMode switch { 0 or 1 => false, 2 => true, _ => null }
            : legacyResult == 0 && (legacy.Flags & 2) == 0 ? false : null;
        // SDRWhiteLevel is fixed-point: 1000 represents 80 nits. Do not invent a
        // default when Windows cannot provide the display's actual SDR white level.
        double? white = whiteResult == 0 && whiteLevel > 0 ? whiteLevel * 0.08 : null;
        string? error = !hdr.HasValue ? $"The active HDR mode is unknown (advanced color queries: {color2Result}/{legacyResult})."
            : hdr == true && !white.HasValue ? $"The HDR display's SDR white level is unavailable ({whiteResult})." : null;
        return new(hdr, white, name, deviceName, bounds, 0, error);
    }

    private static DisplayColorState Unknown(string? deviceName, Rectangle bounds, nint monitor, string message) =>
        new(null, null, null, deviceName, bounds, monitor, message);

    private static DisplayColorSnapshot QueryDisplays()
    {
        try
        {
            var targets = QueryTargets();
            var monitors = new List<DisplayColorState>();
            Native.MonitorCallback callback = (nint monitor, nint _, ref Native.Rect __, nint ___) =>
            {
                var info = Native.MonitorInfo.Create();
                if (Native.GetMonitorInfo(monitor, ref info))
                    monitors.Add(Resolve(targets, info.DeviceName, info.Monitor.ToRectangle(), monitor));
                return true;
            };
            if (!Native.EnumDisplayMonitors(0, 0, callback, 0))
                return new(Array.Empty<DisplayColorState>(), "Windows could not enumerate monitor handles.");
            return new(monitors.AsReadOnly(), null);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or OverflowException)
        {
            return new(Array.Empty<DisplayColorState>(), $"Display color information is unavailable: {ex.Message}");
        }
    }

    private static IReadOnlyList<DisplayColorState> QueryTargets()
    {
        const uint activeOnly = 2;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var result = Native.GetDisplayConfigBufferSizes(activeOnly, out var pathCount, out var modeCount);
            if (result != 0) throw new Win32Exception(result);
            if (pathCount > 256 || modeCount > 4096) throw new InvalidOperationException("The display topology exceeds query bounds.");
            var paths = new Native.PathInfo[Math.Max(1, pathCount)];
            var modes = new Native.ModeInfo[Math.Max(1, modeCount)];
            result = Native.QueryDisplayConfig(activeOnly, ref pathCount, paths, ref modeCount, modes, 0);
            if (result == 122) continue;
            if (result != 0) throw new Win32Exception(result);
            var displays = new List<DisplayColorState>();
            for (var index = 0; index < pathCount; index++)
            {
                var path = paths[index];
                var sourceName = new Native.SourceName { Header = Native.Header.Create<Native.SourceName>(1, path.Source.Adapter, path.Source.Id) };
                if (Native.GetSourceName(ref sourceName) != 0) continue;
                var targetName = new Native.TargetName { Header = Native.Header.Create<Native.TargetName>(2, path.Target.Adapter, path.Target.Id) };
                var nameResult = Native.GetTargetName(ref targetName);
                var color2 = new Native.AdvancedColor2 { Header = Native.Header.Create<Native.AdvancedColor2>(15, path.Target.Adapter, path.Target.Id) };
                var legacy = new Native.AdvancedColor { Header = Native.Header.Create<Native.AdvancedColor>(9, path.Target.Adapter, path.Target.Id) };
                var white = new Native.WhiteLevel { Header = Native.Header.Create<Native.WhiteLevel>(11, path.Target.Adapter, path.Target.Id) };
                var color2Result = Native.GetAdvancedColor2(ref color2);
                var legacyResult = color2Result == 0 ? 0 : Native.GetAdvancedColor(ref legacy);
                var whiteResult = Native.GetWhiteLevel(ref white);
                var bounds = Rectangle.Empty;
                if (path.Source.ModeIndex < modeCount)
                {
                    var mode = modes[path.Source.ModeIndex];
                    if (mode.Type == 1) bounds = new(mode.Source.X, mode.Source.Y, checked((int)mode.Source.Width), checked((int)mode.Source.Height));
                }
                displays.Add(Decode(nameResult == 0 ? targetName.FriendlyName : null, sourceName.DeviceName, bounds,
                    color2Result, color2, legacyResult, legacy, whiteResult, white.Value));
            }
            return displays;
        }
        throw new InvalidOperationException("The display topology changed repeatedly during the query.");
    }

    // All layouts come from Windows SDK wingdi.h / winuser.h. The uint fields
    // represent native bitfields/enums; union sizes are explicit on x86/x64/ARM64.
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Luid { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)] internal struct Header
        {
            public uint Type, Size; public Luid Adapter; public uint Id;
            public static Header Create<T>(uint type, Luid adapter, uint id) where T : struct => new() { Type = type, Size = (uint)Marshal.SizeOf<T>(), Adapter = adapter, Id = id };
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Rect
        {
            public int Left, Top, Right, Bottom;
            public readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct MonitorInfo
        {
            public uint Size; public Rect Monitor, Work; public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public static MonitorInfo Create() => new() { Size = (uint)Marshal.SizeOf<MonitorInfo>(), DeviceName = "" };
        }
        [StructLayout(LayoutKind.Sequential)] internal struct SourceInfo { public Luid Adapter; public uint Id, ModeIndex, Flags; }
        [StructLayout(LayoutKind.Sequential)] internal struct Rational { public uint Numerator, Denominator; }
        [StructLayout(LayoutKind.Sequential)] internal struct TargetInfo
        {
            public Luid Adapter; public uint Id, ModeIndex, Technology, Rotation, Scaling;
            public Rational Refresh; public uint ScanOrder; public int Available; public uint Flags;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct PathInfo { public SourceInfo Source; public TargetInfo Target; public uint Flags; }
        [StructLayout(LayoutKind.Sequential)] internal struct SourceMode { public uint Width, Height, PixelFormat; public int X, Y; }
        [StructLayout(LayoutKind.Explicit, Size = 64)] internal struct ModeInfo
        {
            [FieldOffset(0)] public uint Type;
            [FieldOffset(4)] public uint Id;
            [FieldOffset(8)] public Luid Adapter;
            [FieldOffset(16)] public SourceMode Source;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct SourceName
        {
            public Header Header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct TargetName
        {
            public Header Header; public uint Flags, Technology; public ushort Manufacturer, Product; public uint Connector;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DevicePath;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct AdvancedColor { public Header Header; public uint Flags, Encoding, Bits; }
        [StructLayout(LayoutKind.Sequential)] internal struct AdvancedColor2 { public Header Header; public uint Flags, Encoding, Bits, ActiveColorMode; }
        [StructLayout(LayoutKind.Sequential)] internal struct WhiteLevel { public Header Header; public uint Value; }

        internal delegate bool MonitorCallback(nint monitor, nint dc, ref Rect bounds, nint data);
        [DllImport("user32.dll", ExactSpelling = true)] internal static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
        [DllImport("user32.dll", ExactSpelling = true)] internal static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] paths, ref uint modeCount, [Out] ModeInfo[] modes, nint topology);
        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] internal static extern int GetSourceName(ref SourceName info);
        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] internal static extern int GetTargetName(ref TargetName info);
        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] internal static extern int GetAdvancedColor(ref AdvancedColor info);
        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] internal static extern int GetAdvancedColor2(ref AdvancedColor2 info);
        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] internal static extern int GetWhiteLevel(ref WhiteLevel info);
        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
        [DllImport("user32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
        [DllImport("user32.dll", ExactSpelling = true)] internal static extern nint MonitorFromWindow(nint window, uint flags);
        [DllImport("user32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint window);
    }
}

internal sealed record DisplayColorSnapshot(IReadOnlyList<DisplayColorState> Displays, string? Error);

internal sealed class DisplayColorCache(Func<DisplayColorSnapshot> query, Func<long> timestamp)
{
    private readonly object _gate = new();
    private DisplayColorSnapshot? _snapshot;
    private long _queriedAt;
    public DisplayColorSnapshot Get()
    {
        lock (_gate)
        {
            var now = timestamp();
            if (_snapshot != null && now >= _queriedAt && now - _queriedAt < 1000) return _snapshot;
            _snapshot = query();
            _queriedAt = now;
            return _snapshot;
        }
    }
}
