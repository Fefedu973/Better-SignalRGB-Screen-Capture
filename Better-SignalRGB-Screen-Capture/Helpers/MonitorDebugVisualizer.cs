using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using ScreenRecorderLib;

namespace Better_SignalRGB_Screen_Capture.Helpers;

/// <summary>
/// Generates dynamic HTML visualizations of monitor layouts and region mappings for debugging.
/// </summary>
public static class MonitorDebugVisualizer
{
    /// <summary>
    /// Generates an HTML page showing monitor layout and region mapping.
    /// </summary>
    /// <param name="regionRect">The selected region in virtual screen coordinates</param>
    /// <param name="displays">Available displays from ScreenRecorderLib</param>
    /// <param name="intersectingDisplays">Displays that intersect with the region</param>
    /// <returns>Path to the generated HTML file</returns>
    public static string GenerateDebugVisualization(
        Rectangle regionRect, 
        IEnumerable<RecordableDisplay> displays,
        List<(RecordableDisplay display, Rectangle monitorBounds)> intersectingDisplays)
    {
        try
        {
            // Get Windows API monitor information for accurate coordinates
            var monitorsByDeviceName = GetWindowsApiMonitorBounds();
            
            // Calculate virtual screen bounds
            var allMonitorBounds = new List<Rectangle>();
            foreach (var display in displays)
            {
                var bounds = GetMonitorBounds(display, monitorsByDeviceName);
                if (bounds.HasValue)
                {
                    allMonitorBounds.Add(bounds.Value);
                }
            }
            
            if (allMonitorBounds.Count == 0)
            {
                Debug.WriteLine("❌ No monitor bounds found for visualization");
                return null;
            }
            
            var minX = allMonitorBounds.Min(m => m.X);
            var minY = allMonitorBounds.Min(m => m.Y);
            var maxX = allMonitorBounds.Max(m => m.X + m.Width);
            var maxY = allMonitorBounds.Max(m => m.Y + m.Height);
            
            var virtualScreenWidth = maxX - minX;
            var virtualScreenHeight = maxY - minY;
            
            // Generate HTML content
            var html = GenerateHtmlContent(
                regionRect, 
                displays, 
                intersectingDisplays, 
                monitorsByDeviceName,
                minX, minY, 
                virtualScreenWidth, virtualScreenHeight);
            
            // Save to temp file
            var fileName = $"monitor_debug_{DateTime.Now:yyyyMMdd_HHmmss}.html";
            var filePath = Path.Combine(Path.GetTempPath(), fileName);
            File.WriteAllText(filePath, html, Encoding.UTF8);
            
            Debug.WriteLine($"✅ Generated monitor debug visualization: {filePath}");
            return filePath;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"❌ Failed to generate debug visualization: {ex.Message}");
            return null;
        }
    }
    
    private static string GenerateHtmlContent(
        Rectangle regionRect,
        IEnumerable<RecordableDisplay> displays,
        List<(RecordableDisplay display, Rectangle monitorBounds)> intersectingDisplays,
        Dictionary<string, Rectangle> monitorsByDeviceName,
        int minX, int minY,
        int virtualScreenWidth, int virtualScreenHeight)
    {
        var sb = new StringBuilder();
        
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html>");
        sb.AppendLine("<head>");
        sb.AppendLine("    <title>Monitor Layout Debug - Better SignalRGB Screen Capture</title>");
        sb.AppendLine("    <style>");
        sb.AppendLine("        body { font-family: Arial, sans-serif; margin: 20px; background: #f5f5f5; }");
        sb.AppendLine("        .container { display: flex; gap: 20px; }");
        sb.AppendLine("        .visualization { flex: 1; }");
        sb.AppendLine("        .info-panel { width: 400px; background: white; padding: 20px; border-radius: 8px; box-shadow: 0 2px 10px rgba(0,0,0,0.1); }");
        sb.AppendLine("        #virtual-screen { position: relative; border: 2px solid #333; background: #e8e8e8; margin: 20px 0; }");
        sb.AppendLine("        .monitor { position: absolute; border: 3px solid #2196F3; background: rgba(33, 150, 243, 0.1); display: flex; align-items: center; justify-content: center; font-size: 12px; font-weight: bold; color: #1976D2; }");
        sb.AppendLine("        .monitor.intersecting { border-color: #4CAF50; background: rgba(76, 175, 80, 0.2); color: #388E3C; }");
        sb.AppendLine("        .region { position: absolute; border: 3px solid #F44336; background: rgba(244, 67, 54, 0.2); }");
        sb.AppendLine("        .axis { position: absolute; font-size: 10px; color: #666; }");
        sb.AppendLine("        .axis-x { border-top: 1px solid #999; }");
        sb.AppendLine("        .axis-y { border-left: 1px solid #999; }");
        sb.AppendLine("        .coordinates { font-size: 10px; color: #333; background: rgba(255,255,255,0.8); padding: 2px 4px; border-radius: 3px; }");
        sb.AppendLine("        .info-section { margin-bottom: 20px; }");
        sb.AppendLine("        .info-section h3 { margin: 0 0 10px 0; color: #333; }");
        sb.AppendLine("        .monitor-info { padding: 10px; margin: 5px 0; border-radius: 5px; }");
        sb.AppendLine("        .monitor-info.intersecting { background: #E8F5E8; border-left: 4px solid #4CAF50; }");
        sb.AppendLine("        .monitor-info.non-intersecting { background: #FFF3E0; border-left: 4px solid #FF9800; }");
        sb.AppendLine("        .code { font-family: 'Courier New', monospace; background: #f0f0f0; padding: 2px 4px; border-radius: 3px; }");
        sb.AppendLine("        .warning { color: #F44336; font-weight: bold; }");
        sb.AppendLine("        .success { color: #4CAF50; font-weight: bold; }");
        sb.AppendLine("        #mouse-coords { position: fixed; top: 10px; left: 10px; background: rgba(0,0,0,0.8); color: white; padding: 8px 12px; border-radius: 4px; font-family: monospace; font-size: 14px; z-index: 1000; }");
        sb.AppendLine("    </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        
        sb.AppendLine($"    <div id='mouse-coords'>Mouse: (0, 0)</div>");
        sb.AppendLine($"    <h1>Monitor Layout Debug Visualization</h1>");
        sb.AppendLine($"    <p>Generated at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>");
        
        sb.AppendLine("    <div class='container'>");
        sb.AppendLine("        <div class='visualization'>");
        
        // Calculate scale to fit nicely
        const int maxCanvasWidth = 800;
        const int maxCanvasHeight = 600;
        var scaleX = (double)maxCanvasWidth / virtualScreenWidth;
        var scaleY = (double)maxCanvasHeight / virtualScreenHeight;
        var scale = Math.Min(scaleX, scaleY) * 0.8; // 80% to leave some margin
        
        var canvasWidth = (int)(virtualScreenWidth * scale);
        var canvasHeight = (int)(virtualScreenHeight * scale);
        
        sb.AppendLine($"            <div id='virtual-screen' style='width: {canvasWidth}px; height: {canvasHeight}px;'>");
        
        // Add coordinate axes
        for (int x = 0; x <= virtualScreenWidth; x += 500)
        {
            var scaledX = (int)(x * scale);
            sb.AppendLine($"                <div class='axis axis-x' style='left: {scaledX}px; top: 0; height: {canvasHeight}px; width: 1px;'></div>");
            sb.AppendLine($"                <div class='axis' style='left: {scaledX + 2}px; top: 2px;'>{minX + x}</div>");
        }
        
        for (int y = 0; y <= virtualScreenHeight; y += 500)
        {
            var scaledY = (int)(y * scale);
            sb.AppendLine($"                <div class='axis axis-y' style='top: {scaledY}px; left: 0; width: {canvasWidth}px; height: 1px;'></div>");
            sb.AppendLine($"                <div class='axis' style='top: {scaledY + 2}px; left: 2px;'>{minY + y}</div>");
        }
        
                 // Draw monitors
         foreach (var display in displays)
         {
             var bounds = GetMonitorBounds(display, monitorsByDeviceName);
             Debug.WriteLine($"🔧 Visualizer mapping display '{display.FriendlyName}' (device: '{display.DeviceName}') to bounds: {bounds}");
             if (!bounds.HasValue) continue;
            
            var monitor = bounds.Value;
            var isIntersecting = intersectingDisplays.Any(d => d.display.DeviceName == display.DeviceName);
            
            var scaledX = (int)((monitor.X - minX) * scale);
            var scaledY = (int)((monitor.Y - minY) * scale);
            var scaledWidth = (int)(monitor.Width * scale);
            var scaledHeight = (int)(monitor.Height * scale);
            
            var monitorClass = isIntersecting ? "monitor intersecting" : "monitor";
            
            sb.AppendLine($"                <div class='{monitorClass}' style='left: {scaledX}px; top: {scaledY}px; width: {scaledWidth}px; height: {scaledHeight}px;'>");
            sb.AppendLine($"                    <div>");
            sb.AppendLine($"                        <div>{display.FriendlyName}</div>");
            sb.AppendLine($"                        <div class='coordinates'>({monitor.X}, {monitor.Y})</div>");
            sb.AppendLine($"                        <div class='coordinates'>{monitor.Width} × {monitor.Height}</div>");
            sb.AppendLine($"                    </div>");
            sb.AppendLine($"                </div>");
        }
        
        // Draw region
        var regionScaledX = (int)((regionRect.X - minX) * scale);
        var regionScaledY = (int)((regionRect.Y - minY) * scale);
        var regionScaledWidth = (int)(regionRect.Width * scale);
        var regionScaledHeight = (int)(regionRect.Height * scale);
        
        sb.AppendLine($"                <div class='region' style='left: {regionScaledX}px; top: {regionScaledY}px; width: {regionScaledWidth}px; height: {regionScaledHeight}px;'>");
        sb.AppendLine($"                    <div class='coordinates' style='position: absolute; top: -20px; left: 0;'>Region ({regionRect.X}, {regionRect.Y})</div>");
        sb.AppendLine($"                    <div class='coordinates' style='position: absolute; bottom: -20px; right: 0;'>{regionRect.Width} × {regionRect.Height}</div>");
        sb.AppendLine($"                </div>");
        
        sb.AppendLine("            </div>");
        sb.AppendLine("        </div>");
        
        // Info panel
        sb.AppendLine("        <div class='info-panel'>");
        
        sb.AppendLine("            <div class='info-section'>");
        sb.AppendLine("                <h3>Virtual Screen Info</h3>");
        sb.AppendLine($"                <div>Bounds: <span class='code'>({minX}, {minY}) to ({minX + virtualScreenWidth}, {minY + virtualScreenHeight})</span></div>");
        sb.AppendLine($"                <div>Size: <span class='code'>{virtualScreenWidth} × {virtualScreenHeight}</span></div>");
        sb.AppendLine($"                <div>Scale: <span class='code'>{scale:F3}</span></div>");
        sb.AppendLine("            </div>");
        
        sb.AppendLine("            <div class='info-section'>");
        sb.AppendLine("                <h3>Selected Region</h3>");
        sb.AppendLine($"                <div>Position: <span class='code'>({regionRect.X}, {regionRect.Y})</span></div>");
        sb.AppendLine($"                <div>Size: <span class='code'>{regionRect.Width} × {regionRect.Height}</span></div>");
        sb.AppendLine($"                <div>Bottom-Right: <span class='code'>({regionRect.X + regionRect.Width}, {regionRect.Y + regionRect.Height})</span></div>");
        sb.AppendLine("            </div>");
        
        sb.AppendLine("            <div class='info-section'>");
        sb.AppendLine("                <h3>Monitor Analysis</h3>");
        
        foreach (var display in displays)
        {
            var bounds = GetMonitorBounds(display, monitorsByDeviceName);
            if (!bounds.HasValue) continue;
            
            var monitor = bounds.Value;
            var isIntersecting = intersectingDisplays.Any(d => d.display.DeviceName == display.DeviceName);
            var overlap = Rectangle.Intersect(regionRect, monitor);
            
            var infoClass = isIntersecting ? "monitor-info intersecting" : "monitor-info non-intersecting";
            
            sb.AppendLine($"                <div class='{infoClass}'>");
            sb.AppendLine($"                    <strong>{display.FriendlyName}</strong><br>");
            sb.AppendLine($"                    Device: <span class='code'>{display.DeviceName}</span><br>");
            sb.AppendLine($"                    Bounds: <span class='code'>({monitor.X}, {monitor.Y}) to ({monitor.X + monitor.Width}, {monitor.Y + monitor.Height})</span><br>");
            sb.AppendLine($"                    Size: <span class='code'>{monitor.Width} × {monitor.Height}</span><br>");
            
            if (isIntersecting)
            {
                sb.AppendLine($"                    <span class='success'>✅ INTERSECTS</span><br>");
                sb.AppendLine($"                    Overlap: <span class='code'>({overlap.X}, {overlap.Y}) {overlap.Width} × {overlap.Height}</span>");
            }
            else
            {
                sb.AppendLine($"                    <span class='warning'>❌ NO INTERSECTION</span><br>");
                sb.AppendLine($"                    Intersection result: <span class='code'>({overlap.X}, {overlap.Y}) {overlap.Width} × {overlap.Height}</span>");
                
                // Explain why no intersection
                if (regionRect.X >= monitor.X + monitor.Width)
                    sb.AppendLine($"                    <br><small>Region is to the right of monitor</small>");
                else if (regionRect.X + regionRect.Width <= monitor.X)
                    sb.AppendLine($"                    <br><small>Region is to the left of monitor</small>");
                else if (regionRect.Y >= monitor.Y + monitor.Height)
                    sb.AppendLine($"                    <br><small>Region is below monitor</small>");
                else if (regionRect.Y + regionRect.Height <= monitor.Y)
                    sb.AppendLine($"                    <br><small>Region is above monitor</small>");
            }
            sb.AppendLine($"                </div>");
        }
        
        sb.AppendLine("            </div>");
        
        if (intersectingDisplays.Count == 0)
        {
            sb.AppendLine("            <div class='info-section'>");
            sb.AppendLine("                <h3 class='warning'>⚠️ Debug Analysis</h3>");
            sb.AppendLine("                <div>No monitors intersect with the selected region!</div>");
            sb.AppendLine("                <div>This suggests an issue with coordinate mapping or region selection.</div>");
            sb.AppendLine("            </div>");
        }
        
        sb.AppendLine("        </div>");
        sb.AppendLine("    </div>");
        sb.AppendLine("    ");
        sb.AppendLine("    <script>");
        sb.AppendLine("        const virtualScreen = document.getElementById('virtual-screen');");
        sb.AppendLine("        const mouseCoords = document.getElementById('mouse-coords');");
        sb.AppendLine($"        const scale = {scale.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)};");
        sb.AppendLine($"        const minX = {minX};");
        sb.AppendLine($"        const minY = {minY};");
        sb.AppendLine("        ");
        sb.AppendLine("        virtualScreen.addEventListener('mousemove', function(e) {");
        sb.AppendLine("            const rect = virtualScreen.getBoundingClientRect();");
        sb.AppendLine("            const canvasX = e.clientX - rect.left;");
        sb.AppendLine("            const canvasY = e.clientY - rect.top;");
        sb.AppendLine("            ");
        sb.AppendLine("            // Convert canvas coordinates back to virtual screen coordinates");
        sb.AppendLine("            const virtualX = Math.round(minX + (canvasX / scale));");
        sb.AppendLine("            const virtualY = Math.round(minY + (canvasY / scale));");
        sb.AppendLine("            ");
        sb.AppendLine("            mouseCoords.textContent = `Mouse: (${virtualX}, ${virtualY})`;");
        sb.AppendLine("        });");
        sb.AppendLine("        ");
        sb.AppendLine("        virtualScreen.addEventListener('mouseleave', function() {");
        sb.AppendLine("            mouseCoords.textContent = 'Mouse: outside';");
        sb.AppendLine("        });");
        sb.AppendLine("    </script>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        
        return sb.ToString();
    }
    
    private static Dictionary<string, Rectangle> GetWindowsApiMonitorBounds()
    {
        var monitorsByDeviceName = new Dictionary<string, Rectangle>();
        
        CoordinateMapper.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref CoordinateMapper.RECT lprcMonitor, IntPtr dwData) =>
        {
            var info = new CoordinateMapper.MONITORINFOEX();
            if (CoordinateMapper.GetMonitorInfo(hMonitor, info))
            {
                var rect = new Rectangle(
                    info.rcMonitor.left,
                    info.rcMonitor.top,
                    info.rcMonitor.right - info.rcMonitor.left,
                    info.rcMonitor.bottom - info.rcMonitor.top
                );
                
                string deviceName = info.szDevice.TrimEnd('\0');
                monitorsByDeviceName[deviceName] = rect;
                Debug.WriteLine($"🔧 Visualizer found monitor: '{deviceName}' at ({rect.X},{rect.Y}) {rect.Width}x{rect.Height}");
            }
            return true;
        }, IntPtr.Zero);
        
        return monitorsByDeviceName;
    }
    
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
        
        return null;
    }
} 