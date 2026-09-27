using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using ScreenRecorderLib;
using Better_SignalRGB_Screen_Capture.Core.Helpers;

namespace Better_SignalRGB_Screen_Capture.Helpers;

/// <summary>
/// Utility class for mapping coordinates between virtual screen space and monitor-local space
/// for region recording functionality. Handles complex monitor setups with negative coordinates.
/// </summary>
public static class CoordinateMapper
{
    /// <summary>
    /// Clips each monitor contribution and positions it relative to the requested region origin.
    /// </summary>
    /// <param name="regionRect">The region in virtual screen coordinates</param>
    /// <param name="intersectingDisplays">List of displays and their monitor bounds</param>
    /// <returns>Mapped coordinates for each display source</returns>
    public static List<RegionMapping> MapRegionToDisplays(
        Rectangle regionRect,
        List<(RecordableDisplay display, Rectangle monitorBounds)> intersectingDisplays)
    {
        var mappings = new List<RegionMapping>();

        foreach (var (display, monitorBounds) in intersectingDisplays)
        {
            if (!CanvasGeometry.TryMapRegion(regionRect, monitorBounds,
                out var source, out var destination, out var overlap)) continue;
            mappings.Add(new RegionMapping
            {
                Display = display,
                MonitorBounds = monitorBounds,
                SourceRect = new ScreenRect(source.X, source.Y, source.Width, source.Height),
                Position = new ScreenPoint(destination.X, destination.Y),
                OverlapInVirtualScreen = overlap
            });
        }
        
        return mappings;
    }

    /// <summary>
    /// Finds displays that intersect with a region and gets their proper monitor bounds
    /// using Windows API for accurate virtual screen coordinates.
    /// </summary>
    /// <param name="regionRect">The region to analyze</param>
    /// <param name="displays">Available displays from ScreenRecorderLib</param>
    /// <returns>List of displays with their accurate monitor bounds</returns>
    public static List<(RecordableDisplay display, Rectangle monitorBounds)> FindIntersectingDisplays(
        Rectangle regionRect, 
        IEnumerable<RecordableDisplay> displays)
    {
        var intersectingDisplays = new List<(RecordableDisplay display, Rectangle monitorBounds)>();
        
        // Get Windows API monitor information for accurate coordinates
        var monitorsByDeviceName = GetWindowsApiMonitorBounds();
        
        foreach (var display in displays)
        {
            var monitorBounds = GetMonitorBounds(display, monitorsByDeviceName);
            if (!monitorBounds.HasValue) continue;
            
            if (CanvasGeometry.TryMapRegion(regionRect, monitorBounds.Value, out _, out _, out _))
                intersectingDisplays.Add((display, monitorBounds.Value));
        }

        return intersectingDisplays;
    }

    /// <summary>
    /// Gets Windows API monitor bounds for all monitors with accurate virtual screen coordinates.
    /// </summary>
    private static Dictionary<string, Rectangle> GetWindowsApiMonitorBounds()
    {
        var monitorsByDeviceName = new Dictionary<string, Rectangle>(StringComparer.OrdinalIgnoreCase);
        
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
        {
            var info = new MONITORINFOEX();
            if (GetMonitorInfo(hMonitor, info))
            {
                var rect = new Rectangle(
                    info.rcMonitor.left,
                    info.rcMonitor.top,
                    info.rcMonitor.right - info.rcMonitor.left,
                    info.rcMonitor.bottom - info.rcMonitor.top
                );
                
                string deviceName = info.szDevice.TrimEnd('\0');
                monitorsByDeviceName[deviceName] = rect;
                
                Debug.WriteLine($"🖥️ Windows API Monitor: '{deviceName}' at ({rect.X},{rect.Y}) {rect.Width}x{rect.Height}");
                if (rect.X < 0 || rect.Y < 0)
                {
                    Debug.WriteLine($"   ⚠️ Has negative coordinates (normal for monitors left/above primary)");
                }
            }
            return true;
        }, IntPtr.Zero);
        
        return monitorsByDeviceName;
    }

    /// <summary>
    /// Gets the monitor bounds for a specific display using Windows API coordinates.
    /// </summary>
    private static Rectangle? GetMonitorBounds(RecordableDisplay display, Dictionary<string, Rectangle> monitorsByDeviceName)
    {
        // Try exact device name match first
        if (display.DeviceName != null && monitorsByDeviceName.ContainsKey(display.DeviceName))
        {
            return monitorsByDeviceName[display.DeviceName];
        }
        
        // Device numbering is not enumeration order (DISPLAY3 may be the first active
        // monitor). Match normalized names only, never capture a different screen by index.
        var normalizedName = display.DeviceName?.Replace(@"\\.\", "");
        foreach (var monitor in monitorsByDeviceName)
        {
            if (string.Equals(monitor.Key.Replace(@"\\.\", ""), normalizedName,
                StringComparison.OrdinalIgnoreCase)) return monitor.Value;
        }
        
        Debug.WriteLine($"❌ Could not find monitor bounds for display: {display.FriendlyName} ({display.DeviceName})");
        return null;
    }

    /// <summary>
    /// Calculates border position for preview display, using the same coordinate mapping logic
    /// as the debug visualization which works correctly.
    /// </summary>
    public static (double borderX, double borderY) CalculatePreviewBorderPosition(
        Rectangle regionRect,
        List<(RecordableDisplay display, Rectangle monitorBounds)> intersectingDisplays,
        OutputDimensions outputDimensions,
        double scale)
    {
        if (intersectingDisplays.Count == 0) return (0, 0);
        var originX = intersectingDisplays.Min(d => d.monitorBounds.X);
        var originY = intersectingDisplays.Min(d => d.monitorBounds.Y);
        return ((regionRect.X - originX) * scale, (regionRect.Y - originY) * scale);
    }

    // P/Invoke declarations
    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
    
    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);
    
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, [In, Out] MONITORINFOEX lpmi);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public class MONITORINFOEX
    {
        public int cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice = "";
    }
}

/// <summary>
/// Represents the mapping of a region to a specific display for recording purposes.
/// </summary>
public class RegionMapping
{
    public RecordableDisplay Display { get; set; } = null!;
    public Rectangle MonitorBounds { get; set; }
    public ScreenRect SourceRect { get; set; } = new();
    public ScreenPoint? Position { get; set; }
    public Rectangle OverlapInVirtualScreen { get; set; }
}
