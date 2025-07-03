using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using ScreenRecorderLib;

namespace Better_SignalRGB_Screen_Capture.Helpers;

/// <summary>
/// Utility class for mapping coordinates between virtual screen space and monitor-local space
/// for region recording functionality. Handles complex monitor setups with negative coordinates.
/// </summary>
public static class CoordinateMapper
{
    /// <summary>
    /// Maps region coordinates for recording using the EXACT same logic as screenshot preview and debug visualization.
    /// Uses simple offset calculations without coordinate shifts.
    /// </summary>
    /// <param name="regionRect">The region in virtual screen coordinates</param>
    /// <param name="intersectingDisplays">List of displays and their monitor bounds</param>
    /// <param name="outputDimensions">Output dimensions from ScreenRecorderLib</param>
    /// <returns>Mapped coordinates for each display source</returns>
    public static List<RegionMapping> MapRegionToDisplays(
        Rectangle regionRect,
        List<(RecordableDisplay display, Rectangle monitorBounds)> intersectingDisplays,
        OutputDimensions outputDimensions)
    {
        var mappings = new List<RegionMapping>();

        if (intersectingDisplays.Count == 1)
        {
            // Single monitor case - use simple offset calculation like debug visualization
            var (display, monitorBounds) = intersectingDisplays[0];
            
            // Calculate the overlap between region and monitor (clamps region to monitor bounds)
            var overlap = Rectangle.Intersect(regionRect, monitorBounds);
            
            if (overlap.Width <= 0 || overlap.Height <= 0)
            {
                Debug.WriteLine($"❌ No valid overlap between region and monitor {display.FriendlyName}");
                return mappings; // Return empty list
            }
            
            // Use simple coordinate calculation like debug visualization and preview
            var sourceLeft = regionRect.X - monitorBounds.X;
            var sourceTop = regionRect.Y - monitorBounds.Y;
            var sourceWidth = regionRect.Width;
            var sourceHeight = regionRect.Height;
            
            mappings.Add(new RegionMapping
            {
                Display = display,
                MonitorBounds = monitorBounds,
                SourceRect = new ScreenRect(sourceLeft, sourceTop, sourceWidth, sourceHeight),
                Position = null, // Single monitor doesn't need position
                OverlapInVirtualScreen = overlap
            });
            
            Debug.WriteLine($"🔧 Single monitor mapping (using simple logic) for {display.FriendlyName}:");
            Debug.WriteLine($"   Monitor bounds: {monitorBounds.X},{monitorBounds.Y} {monitorBounds.Width}x{monitorBounds.Height}");
            Debug.WriteLine($"   Source rect: {sourceLeft},{sourceTop} size {sourceWidth}x{sourceHeight}");
        }
        else
        {
            // Multi-monitor case - use simple offset calculation like debug visualization
            var minX = intersectingDisplays.Min(d => d.monitorBounds.X);
            var minY = intersectingDisplays.Min(d => d.monitorBounds.Y);
            
            foreach (var (display, monitorBounds) in intersectingDisplays)
            {
                var overlap = Rectangle.Intersect(regionRect, monitorBounds);
                
                if (overlap.Width <= 0 || overlap.Height <= 0)
                {
                    Debug.WriteLine($"❌ No valid overlap between region and monitor {display.FriendlyName}");
                    continue; // Skip this monitor
                }
                
                // Use simple coordinate calculation like debug visualization and preview
                var sourceLeft = overlap.X - monitorBounds.X;
                var sourceTop = overlap.Y - monitorBounds.Y;
                var sourceWidth = overlap.Width;
                var sourceHeight = overlap.Height;
                
                // Position in combined output (relative to region origin)
                var positionX = overlap.X - regionRect.X;
                var positionY = overlap.Y - regionRect.Y;
                
                mappings.Add(new RegionMapping
                {
                    Display = display,
                    MonitorBounds = monitorBounds,
                    SourceRect = new ScreenRect(sourceLeft, sourceTop, sourceWidth, sourceHeight),
                    Position = new ScreenPoint(positionX, positionY),
                    OverlapInVirtualScreen = overlap
                });
                
                Debug.WriteLine($"🔧 Multi-monitor mapping (using simple logic) for {display.FriendlyName}:");
                Debug.WriteLine($"   Monitor bounds: {monitorBounds.X},{monitorBounds.Y} {monitorBounds.Width}x{monitorBounds.Height}");
                Debug.WriteLine($"   Source rect: {sourceLeft},{sourceTop} size {sourceWidth}x{sourceHeight}");
                Debug.WriteLine($"   Position: {positionX},{positionY}");
            }
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
            
            Debug.WriteLine($"🔍 Checking intersection for {display.FriendlyName}:");
            Debug.WriteLine($"   Monitor bounds: {monitorBounds.Value.X},{monitorBounds.Value.Y} {monitorBounds.Value.Width}x{monitorBounds.Value.Height}");
            Debug.WriteLine($"   Monitor bottom-right: {monitorBounds.Value.X + monitorBounds.Value.Width},{monitorBounds.Value.Y + monitorBounds.Value.Height}");
            Debug.WriteLine($"   Region: {regionRect.X},{regionRect.Y} {regionRect.Width}x{regionRect.Height}");
            Debug.WriteLine($"   Region bottom-right: {regionRect.X + regionRect.Width},{regionRect.Y + regionRect.Height}");
            
            // Manual intersection check to debug
            bool intersectsX = regionRect.X < monitorBounds.Value.X + monitorBounds.Value.Width && 
                              regionRect.X + regionRect.Width > monitorBounds.Value.X;
            bool intersectsY = regionRect.Y < monitorBounds.Value.Y + monitorBounds.Value.Height && 
                              regionRect.Y + regionRect.Height > monitorBounds.Value.Y;
            
            Debug.WriteLine($"   Manual X intersection check: {intersectsX}");
            Debug.WriteLine($"     Region X: {regionRect.X} to {regionRect.X + regionRect.Width}");
            Debug.WriteLine($"     Monitor X: {monitorBounds.Value.X} to {monitorBounds.Value.X + monitorBounds.Value.Width}");
            Debug.WriteLine($"   Manual Y intersection check: {intersectsY}");
            Debug.WriteLine($"     Region Y: {regionRect.Y} to {regionRect.Y + regionRect.Height}");
            Debug.WriteLine($"     Monitor Y: {monitorBounds.Value.Y} to {monitorBounds.Value.Y + monitorBounds.Value.Height}");
            
            var overlap = Rectangle.Intersect(regionRect, monitorBounds.Value);
            Debug.WriteLine($"   Rectangle.Intersect result: {overlap.X},{overlap.Y} {overlap.Width}x{overlap.Height}");
            Debug.WriteLine($"   Manual intersection result: {intersectsX && intersectsY}");
            
            if (overlap.Width > 0 && overlap.Height > 0)
            {
                intersectingDisplays.Add((display, monitorBounds.Value));
                Debug.WriteLine($"✅ Display {display.FriendlyName} intersects region!");
            }
            else
            {
                Debug.WriteLine($"❌ Display {display.FriendlyName} does NOT intersect region");
            }
        }
        
        return intersectingDisplays;
    }

    /// <summary>
    /// Gets Windows API monitor bounds for all monitors with accurate virtual screen coordinates.
    /// </summary>
    private static Dictionary<string, Rectangle> GetWindowsApiMonitorBounds()
    {
        var monitorsByDeviceName = new Dictionary<string, Rectangle>();
        
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
        
        // Try without \\.\\ prefix
        if (display.DeviceName != null)
        {
            string simpleName = display.DeviceName.Replace(@"\\.\", "");
            if (monitorsByDeviceName.ContainsKey(simpleName))
            {
                return monitorsByDeviceName[simpleName];
            }
        }
        
        // Try to match by display number from device name
        if (display.DeviceName != null)
        {
            var match = System.Text.RegularExpressions.Regex.Match(display.DeviceName, @"DISPLAY(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int displayNum))
            {
                var monitorsList = monitorsByDeviceName.Values.ToList();
                int index = displayNum - 1; // Display numbers are 1-based
                if (index >= 0 && index < monitorsList.Count)
                {
                    return monitorsList[index];
                }
            }
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
        if (intersectingDisplays.Count == 1)
        {
            // Single monitor - use simple offset calculation like debug visualization
            var monitorBounds = intersectingDisplays[0].monitorBounds;
            
            var borderX = (regionRect.X - monitorBounds.X) * scale;
            var borderY = (regionRect.Y - monitorBounds.Y) * scale;
            
            Debug.WriteLine($"🔧 Preview border calculation (single monitor):");
            Debug.WriteLine($"   Region: ({regionRect.X}, {regionRect.Y})");
            Debug.WriteLine($"   Monitor: ({monitorBounds.X}, {monitorBounds.Y})");
            Debug.WriteLine($"   Scale: {scale}");
            Debug.WriteLine($"   Border position: ({borderX}, {borderY})");
            
            return (borderX, borderY);
        }
        else
        {
            // Multi-monitor - use simple offset calculation like debug visualization
            var minX = intersectingDisplays.Min(d => d.monitorBounds.X);
            var minY = intersectingDisplays.Min(d => d.monitorBounds.Y);
            
            var borderX = (regionRect.X - minX) * scale;
            var borderY = (regionRect.Y - minY) * scale;
            
            Debug.WriteLine($"🔧 Preview border calculation (multi-monitor):");
            Debug.WriteLine($"   Region: ({regionRect.X}, {regionRect.Y})");
            Debug.WriteLine($"   Combined origin: ({minX}, {minY})");
            Debug.WriteLine($"   Scale: {scale}");
            Debug.WriteLine($"   Border position: ({borderX}, {borderY})");
            
            return (borderX, borderY);
        }
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