using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Better_SignalRGB_Screen_Capture.Helpers;

/// <summary>Invoked before WinUI startup in a hidden, app-owned child process.</summary>
public static class WallpaperCaptureWorker
{
    public const string Argument = "--wallpaper-capture-worker";
    internal sealed record Options(string Monitor, int Width, int Height, int FrameRate, int ParentId, long ParentStartTicks);

    public static int Run(string[] args)
    {
        try
        {
            var options = Parse(args);
            using var dpi = new WallpaperNative.DpiScope();
            using var parent = Process.GetProcessById(options.ParentId);
            if (parent.StartTime.ToUniversalTime().Ticks != options.ParentStartTicks) throw new InvalidOperationException("The wallpaper capture parent no longer exists.");
            using var output = Console.OpenStandardOutput();
            using var resized = new Surface(options.Width, options.Height);
            var pixels = new byte[WallpaperFrameProtocol.ValidateSize(options.Width, options.Height)];
            var header = new byte[WallpaperFrameProtocol.HeaderLength];
            WallpaperFrameProtocol.WriteHeader(header, options.Width, options.Height);
            Surface? source = null;
            WallpaperEngineSourceDiscovery.Target? target = null;
            var lastDiscovery = Stopwatch.GetTimestamp() - Stopwatch.Frequency * 2;
            var unavailableSince = Stopwatch.GetTimestamp();
            try
            {
                while (!parent.HasExited)
                {
                    var started = Stopwatch.GetTimestamp();
                    if (target is null || Stopwatch.GetElapsedTime(lastDiscovery).TotalSeconds >= 1 || !WallpaperEngineSourceDiscovery.StillOwnsHost(target))
                    {
                        target = WallpaperEngineSourceDiscovery.TryDiscover(options.Monitor);
                        lastDiscovery = Stopwatch.GetTimestamp();
                    }
                    if (target is null || !WallpaperEngineSourceDiscovery.StillOwnsHost(target))
                    {
                        if (Stopwatch.GetElapsedTime(unavailableSince).TotalSeconds >= 2)
                            throw new InvalidOperationException("Wallpaper Engine has no wallpaper-only desktop surface on this display. Its renderer may be stopped.");
                        Thread.Sleep(100);
                        continue;
                    }
                    unavailableSince = Stopwatch.GetTimestamp();
                    var bounds = target.HostBounds;
                    if (source?.Width != bounds.Width || source.Height != bounds.Height)
                    {
                        source?.Dispose(); source = null;
                        source = new Surface(bounds.Width, bounds.Height);
                    }
                    // Always print the verified WorkerW itself, never a Chromium child or
                    // Progman (which can contain desktop icons). No desktop pixels are sampled.
                    if (!PrintWindow(target.Handle, source.Dc, 2))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Wallpaper Engine's desktop surface could not be captured.");
                    if (!WallpaperEngineSourceDiscovery.StillOwnsHost(target)) { target = null; continue; }
                    var crop = CalculateCrop(target.HostBounds, target.MonitorBounds, options.Width, options.Height);
                    if (!PatBlt(resized.Dc, 0, 0, resized.Width, resized.Height, 0x00000042)) throw new InvalidOperationException("Could not clear the wallpaper frame.");
                    if (crop.Source.Width > 0 && crop.Source.Height > 0 && crop.Destination.Width > 0 && crop.Destination.Height > 0 && !StretchBlt(resized.Dc,
                        crop.Destination.X, crop.Destination.Y, crop.Destination.Width, crop.Destination.Height,
                        source.Dc, crop.Source.X, crop.Source.Y, crop.Source.Width, crop.Source.Height, 0x00cc0020))
                        throw new InvalidOperationException("Could not scale the wallpaper frame.");
                    Marshal.Copy(resized.Pixels, pixels, 0, pixels.Length);
                    output.Write(header); output.Write(pixels); output.Flush();
                    // Backpressure is synchronous and there is no accumulated frame queue.
                    var remaining = 1000d / options.FrameRate - Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    if (remaining > 0) Thread.Sleep(TimeSpan.FromMilliseconds(remaining));
                }
            }
            finally { source?.Dispose(); }
            return 0;
        }
        catch (Exception error)
        {
            var message = error.Message.Replace('\r', ' ').Replace('\n', ' ');
            try { Console.Error.WriteLine(message[..Math.Min(1024, message.Length)]); } catch { }
            return 2;
        }
    }

    internal static Options Parse(string[] args)
    {
        if (args.Length != 13 || args[0] != Argument) throw new ArgumentException("Invalid wallpaper worker arguments.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
            if (!values.TryAdd(args[index], args[index + 1])) throw new ArgumentException("Duplicate wallpaper worker option.");
        int Number(string key) => int.Parse(values[key], NumberStyles.None, CultureInfo.InvariantCulture);
        var monitor = values["--monitor"];
        if (monitor.Length is < 1 or > 64 || monitor.Any(char.IsControl)) throw new ArgumentException("Invalid wallpaper display identifier.");
        var width = Number("--width"); var height = Number("--height"); WallpaperFrameProtocol.ValidateSize(width, height);
        var fps = Number("--fps"); var parent = Number("--parent");
        var start = long.Parse(values["--parent-start"], NumberStyles.None, CultureInfo.InvariantCulture);
        if (fps is < 1 or > 60 || parent < 1 || parent == Environment.ProcessId || start < 1) throw new ArgumentException("Invalid wallpaper worker lifetime or frame rate.");
        return new(monitor, width, height, fps, parent, start);
    }

    internal static (Rectangle Source, Rectangle Destination) CalculateCrop(Rectangle host, Rectangle monitor, int width, int height)
    {
        if (host.Width <= 0 || host.Height <= 0 || monitor.Width <= 0 || monitor.Height <= 0) throw new ArgumentException("Invalid wallpaper capture bounds.");
        var intersection = Rectangle.Intersect(host, monitor);
        if (intersection.IsEmpty) return (Rectangle.Empty, Rectangle.Empty);
        int X(int coordinate) => (int)Math.Round((coordinate - (double)monitor.X) * width / monitor.Width);
        int Y(int coordinate) => (int)Math.Round((coordinate - (double)monitor.Y) * height / monitor.Height);
        return (new(intersection.X - host.X, intersection.Y - host.Y, intersection.Width, intersection.Height),
            Rectangle.FromLTRB(X(intersection.Left), Y(intersection.Top), X(intersection.Right), Y(intersection.Bottom)));
    }

    private sealed class Surface : IDisposable
    {
        public int Width { get; }
        public int Height { get; }
        public nint Dc { get; private set; }
        public nint Pixels { get; private set; }
        private nint _bitmap, _previous;
        public Surface(int width, int height)
        {
            if (width < 1 || height < 1 || width > 16384 || height > 16384 || (long)width * height > 64 * 1024 * 1024)
                throw new InvalidOperationException("The wallpaper desktop surface exceeds the native capture memory budget.");
            Width = width; Height = height;
            try
            {
                Dc = CreateCompatibleDC(0);
                if (Dc == 0) throw new System.ComponentModel.Win32Exception();
                var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
                _bitmap = CreateDIBSection(Dc, ref info, 0, out var pixels, 0, 0); Pixels = pixels;
                if (_bitmap == 0 || Pixels == 0) throw new System.ComponentModel.Win32Exception();
                _previous = SelectObject(Dc, _bitmap);
                if (_previous == 0 || _previous == -1) throw new System.ComponentModel.Win32Exception();
                SetStretchBltMode(Dc, 4); SetBrushOrgEx(Dc, 0, 0, 0);
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (Dc != 0 && _previous != 0 && _previous != -1) SelectObject(Dc, _previous);
            if (_bitmap != 0) DeleteObject(_bitmap);
            if (Dc != 0) DeleteDC(Dc);
            Dc = 0; Pixels = 0; _bitmap = 0; _previous = 0;
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, ImageSize; public int XPelsPerMeter, YPelsPerMeter;
        public uint ColorsUsed, ColorsImportant, Color;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PrintWindow(nint window, nint dc, uint flags);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint pixels, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(nint dc, int mode);
    [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(nint dc, int x, int y, nint previous);
    [DllImport("gdi32.dll")] private static extern bool PatBlt(nint dc, int x, int y, int width, int height, uint operation);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint operation);
}
