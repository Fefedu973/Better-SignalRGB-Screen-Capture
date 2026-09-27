using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Better_SignalRGB_Screen_Capture.Core.Helpers;
using Better_SignalRGB_Screen_Capture.Helpers;
using ScreenRecorderLib;

// Opt-in feasibility probe. Enumerates Wallpaper Engine's actual windows, including
// children under WorkerW, then observes WGC pixels in memory. Never changes Wallpaper
// Engine settings, foreground windows or wallpapers; writes no captured image files.
// --occlude adds a short-lived, non-activating test window behind ordinary apps.
if (args.Length >= 2 && args[0] is "--print-worker" or "--capture-worker")
{
    var target = WallpaperWindows.Find(includeDesktopHosts: true).FirstOrDefault(surface => surface.Handle.ToInt64().ToString() == args[1]);
    if (target == null) { Console.WriteLine("GDI target is no longer available."); return 2; }
    var foreground = WallpaperWindows.Foreground;
    try
    {
        if (args[0] == "--print-worker") WallpaperWindows.ProbePrintWindow(target, args.Contains("--temporal"), args.Contains("--occlude"));
        else
        {
            WallpaperWindows.ProbeCaptureItem(target);
            if (args.Contains("--factory-only")) return 0;
            await ProbeAsync(target);
            if (args.Contains("--occlude"))
                WallpaperWindows.ProbeCoveredCapture(target, check => ProbeAsync(target, "covered", check));
        }
    }
    catch (Exception exception) { Console.WriteLine($"UNSUPPORTED {target.Class}: {exception.GetType().Name}: {exception.Message}"); }
    finally { Console.WriteLine($"WORKER foregroundUnchanged={foreground == WallpaperWindows.Foreground}"); }
    return 0;
}
var surfaces = WallpaperWindows.Find(args.Contains("--desktop-hosts"));
foreach (var surface in surfaces)
    Console.WriteLine($"Surface hwnd={surface.Handle} visible={surface.Visible} class={surface.Class} parent={surface.ParentClass} role={surface.Role} size={surface.Width}x{surface.Height}");
foreach (var host in surfaces.Where(surface => surface.Role != "renderer")) WallpaperWindows.DescribeDesktopChildren(host);
if (!args.Contains("--capture-wallpaper", StringComparer.Ordinal))
{
    Console.WriteLine("Inventory only. Pass --capture-wallpaper to probe these surfaces in memory.");
    return 0;
}
foreach (var surface in surfaces.Where(item => item.Visible && (!args.Contains("--hosts-only") || item.Role != "renderer")).Take(12))
{
    Console.WriteLine($"Probing {surface.Class}...");
    try { Console.WriteLine(await ProbeIsolatedAsync(surface, args, "--capture-worker")); }
    catch (Exception ex) { Console.WriteLine($"UNSUPPORTED {surface.Class}: {ex.GetType().Name}: {ex.Message}"); }
    if (args.Contains("--print-window", StringComparer.Ordinal))
    {
        try { Console.WriteLine(await ProbeIsolatedAsync(surface, args, "--print-worker")); }
        catch (Exception ex) { Console.WriteLine($"GDI UNSUPPORTED {surface.Class}: {ex.GetType().Name}: {ex.Message}"); }
    }
}
return 0;

static async Task<string> ProbeIsolatedAsync(WallpaperSurface surface, string[] args, string worker)
{
    // PrintWindow is synchronous and can hang in a foreign process. An await timeout
    // alone cannot cancel it, so isolate the native call and kill only our helper.
    var host = Environment.ProcessPath ?? throw new InvalidOperationException("No executable path is available.");
    var start = new ProcessStartInfo(host)
    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add(worker); start.ArgumentList.Add(surface.Handle.ToInt64().ToString());
    if (args.Contains("--temporal")) start.ArgumentList.Add("--temporal");
    if (args.Contains("--factory-only")) start.ArgumentList.Add("--factory-only");
    if (args.Contains("--occlude")) start.ArgumentList.Add("--occlude");
    using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the isolated probe.");
    var output = child.StandardOutput.ReadToEndAsync();
    var error = child.StandardError.ReadToEndAsync();
    try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(18)); }
    catch (TimeoutException)
    {
        child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync();
        return $"{worker} TIMEOUT {surface.Class}: isolated helper terminated; temporary windows removed by the OS.";
    }
    return (await output).Trim() + (child.ExitCode == 0 ? "" : $"\nWorker exit={child.ExitCode}: {(await error).Trim()}");
}

static async Task ProbeAsync(WallpaperSurface surface, string phase = "baseline", Func<bool>? validate = null)
{
    var source = new WindowRecordingSource { Handle = surface.Handle, IsBorderRequired = false, IsCursorCaptureEnabled = false };
    var options = CaptureRecorderOptions.Create([source], 320, 200, 10, false, useHardwareEncoding: false);
    using var sink = new CaptureDiscardStream();
    using var recorder = Recorder.CreateRecorder(options);
    var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var distinct = new HashSet<string>();
    var frames = 0; var nonBlack = 0d; var copying = 0; var allChecksValid = true;
    var warmDistinct = new HashSet<string>();
    recorder.OnRecordingFailed += (_, e) => { received.TrySetException(new InvalidOperationException(e.Error)); ended.TrySetResult(); };
    recorder.OnRecordingComplete += (_, _) => ended.TrySetResult();
    recorder.OnFrameRecorded += (_, e) =>
    {
        if (received.Task.IsCompleted || Interlocked.Exchange(ref copying, 1) != 0) return;
        try
        {
            var bitmap = e.BitmapData;
            if (bitmap == null || bitmap.Data == IntPtr.Zero || bitmap.Width < 1 || bitmap.Height < 1) return;
            var pixels = new byte[checked(bitmap.Width * bitmap.Height * 4)];
            CaptureFrameEncoder.CopyBgraRows(bitmap.Data, bitmap.Width, bitmap.Height, bitmap.Stride, pixels);
            var lit = 0;
            for (var i = 0; i < pixels.Length; i += 4) if (pixels[i] + pixels[i + 1] + pixels[i + 2] > 9) lit++;
            nonBlack = Math.Max(nonBlack, (double)lit / (bitmap.Width * bitmap.Height));
            var hash = Convert.ToHexString(SHA256.HashData(pixels));
            distinct.Add(hash);
            if (frames >= 5) warmDistinct.Add(hash);
            allChecksValid &= validate?.Invoke() ?? true;
            if (++frames >= 20) received.TrySetResult();
        }
        catch (Exception ex) { received.TrySetException(ex); }
        finally { Volatile.Write(ref copying, 0); }
    };
    try
    {
        recorder.Record(sink);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(12));
        Console.WriteLine($"RESULT {surface.Class} phase={phase}: frames={frames}, distinct={distinct.Count}, postWarmupDistinct={warmDistinct.Count}, nonblack={nonBlack:P2}, allSampleChecksValid={allChecksValid}. No pixels saved or transmitted.");
    }
    finally
    {
        recorder.Stop();
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

internal sealed record WallpaperSurface(IntPtr Handle, bool Visible, string Class, string ParentClass, int Width, int Height, string Role = "renderer");

internal static class WallpaperWindows
{
    public static IntPtr Foreground => GetForegroundWindow();

    public static void ProbeCaptureItem(WallpaperSurface surface)
    {
        var initialized = RoInitialize(1);
        IntPtr name = IntPtr.Zero, factory = IntPtr.Zero, item = IntPtr.Zero;
        try
        {
            const string runtimeClass = "Windows.Graphics.Capture.GraphicsCaptureItem";
            Marshal.ThrowExceptionForHR(WindowsCreateString(runtimeClass, runtimeClass.Length, out name));
            var interopId = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(name, ref interopId, out factory));
            var method = Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), 3 * IntPtr.Size);
            var create = Marshal.GetDelegateForFunctionPointer<CreateForWindow>(method);
            var itemId = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            var result = create(factory, surface.Handle, ref itemId, out item);
            Console.WriteLine($"WGC NATIVE FACTORY {surface.Class}: HRESULT=0x{result:X8}, created={item != IntPtr.Zero}");
        }
        finally
        {
            if (item != IntPtr.Zero) Marshal.Release(item);
            if (factory != IntPtr.Zero) Marshal.Release(factory);
            if (name != IntPtr.Zero) WindowsDeleteString(name);
            if (initialized >= 0) RoUninitialize();
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateForWindow(IntPtr factory, IntPtr window, ref Guid id, out IntPtr item);
    [DllImport("combase.dll")] private static extern int RoInitialize(uint type);
    [DllImport("combase.dll")] private static extern void RoUninitialize();
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string value, int length, out IntPtr result);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr value);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr name, ref Guid id, out IntPtr result);
    public static void DescribeDesktopChildren(WallpaperSurface surface)
    {
        EnumChildWindows(surface.Handle, (window, _) =>
        {
            if (!GetWindowRect(window, out var rect)) return true;
            var name = ClassName(window);
            if (name is "WorkerW" or "SHELLDLL_DefView" or "SysListView32" or "WPEDesktopCEFWindow")
                Console.WriteLine($"HOST CHILD class={name} parent={ClassName(GetParent(window))} visible={IsWindowVisible(window)} rect={rect.Left},{rect.Top},{rect.Right-rect.Left},{rect.Bottom-rect.Top}");
            return true;
        }, IntPtr.Zero);
    }
    private delegate bool EnumProc(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint owner);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder value, int maximum);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PrintWindow(IntPtr hwnd, IntPtr target, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }

    private static string ClassName(IntPtr window)
    { var text = new StringBuilder(256); GetClassName(window, text, text.Capacity); return text.ToString(); }

    public static IReadOnlyList<WallpaperSurface> Find(bool includeDesktopHosts = false)
    {
        var owners = new HashSet<uint>();
        foreach (var process in Process.GetProcesses())
            using (process)
                if (process.ProcessName is "wallpaper32" or "wallpaper64" or "webwallpaper32" or "webwallpaper64")
                    owners.Add((uint)process.Id);
        var result = new List<WallpaperSurface>();
        var seen = new HashSet<IntPtr>();
        EnumProc inspect = (window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (!owners.Contains(owner) || !seen.Add(window) || !GetWindowRect(window, out var bounds)) return true;
            var width = bounds.Right - bounds.Left; var height = bounds.Bottom - bounds.Top;
            if (width >= 100 && height >= 100)
                result.Add(new(window, IsWindowVisible(window), ClassName(window), ClassName(GetParent(window)), width, height));
            return true;
        };
        EnumWindows((window, data) => { inspect(window, data); EnumChildWindows(window, inspect, IntPtr.Zero); return true; }, IntPtr.Zero);
        if (includeDesktopHosts)
        {
            var wallpaperRoots = result.Select(surface => GetAncestor(surface.Handle, 2)).ToHashSet();
            void AddHost(IntPtr window, string role)
            {
                if (!seen.Add(window) || !GetWindowRect(window, out var bounds)) return;
                var width = bounds.Right - bounds.Left; var height = bounds.Bottom - bounds.Top;
                if (width >= 100 && height >= 100)
                    result.Add(new(window, IsWindowVisible(window), ClassName(window), ClassName(GetParent(window)), width, height, role));
            }
            foreach (var renderer in result.ToArray())
            {
                var ancestor = GetParent(renderer.Handle);
                for (var depth = 0; ancestor != IntPtr.Zero && depth < 32; depth++, ancestor = GetParent(ancestor))
                    if (ClassName(ancestor) is "WorkerW" or "Progman") AddHost(ancestor, "wallpaper-desktop-host");
            }
            EnumWindows((window, _) =>
            {
                if (ClassName(window) is "WorkerW" or "Progman") AddHost(window, "desktop-shell");
                return true;
            }, IntPtr.Zero);
        }
        return result;
    }

    public static void ProbeCoveredCapture(WallpaperSurface surface, Func<Func<bool>, Task> capture)
    {
        var foreground = GetForegroundWindow();
        var cover = CreateDesktopCover(surface, out var verified);
        try
        {
            if (cover == IntPtr.Zero || !verified)
            { Console.WriteLine("WGC OCCLUSION UNVERIFIED: no desktop-only z-order placement established."); return; }
            PumpFor(TimeSpan.FromMilliseconds(600));
            var task = capture(() => CoverStillValid(cover, surface) && GetForegroundWindow() == foreground);
            while (!task.IsCompleted) PumpFor(TimeSpan.FromMilliseconds(20));
            task.GetAwaiter().GetResult();
        }
        finally
        {
            if (cover != IntPtr.Zero) DestroyWindow(cover);
            Console.WriteLine($"WGC OCCLUSION cleanupComplete={!IsWindow(cover)} foregroundUnchanged={GetForegroundWindow() == foreground}");
        }
    }

    public static void ProbePrintWindow(WallpaperSurface surface, bool temporal, bool occlude)
    {
        if ((long)surface.Width * surface.Height > 20_000_000)
        { Console.WriteLine("GDI SKIP: surface exceeds probe allocation budget."); return; }
        using var bitmap = new Bitmap(surface.Width, surface.Height);
        var baseline = Sample(surface, bitmap, temporal ? 12 : 1, "baseline");
        if (!occlude || baseline.NonBlack < .01 || baseline.Colors < 8)
        {
            if (occlude) Console.WriteLine("OCCLUSION SKIP: baseline does not establish a nontrivial rendered surface.");
            return;
        }
        var foreground = GetForegroundWindow();
        var cover = CreateDesktopCover(surface, out var verified);
        try
        {
            if (cover == IntPtr.Zero || !verified)
            { Console.WriteLine("OCCLUSION UNVERIFIED: no safe desktop-only z-order placement established."); return; }
            PumpFor(TimeSpan.FromMilliseconds(600));
            var covered = Sample(surface, bitmap, 12, "covered", () => CoverStillValid(cover, surface) && GetForegroundWindow() == foreground);
            Console.WriteLine($"OCCLUSION geometryVerified={CoverStillValid(cover, surface)} foregroundUnchanged={GetForegroundWindow() == foreground} " +
                $"allSampleChecksValid={covered.AllChecksValid} nontrivialPixels={covered.NonBlack > .01 && covered.Colors >= 8} changingPixelsAfterWarmup={covered.WarmDistinct > 1}; " +
                "local desktop cover only, not a fullscreen-application playback test.");
        }
        finally
        {
            if (cover != IntPtr.Zero) DestroyWindow(cover);
            Console.WriteLine($"OCCLUSION cleanupComplete={!IsWindow(cover)} foregroundUnchanged={GetForegroundWindow() == foreground}");
        }
    }

    private sealed record SampleSummary(int Distinct, double NonBlack, int Colors, int WarmDistinct, bool AllChecksValid);

    private static SampleSummary Sample(WallpaperSurface surface, Bitmap bitmap, int count, string phase, Func<bool>? validate = null)
    {
        var hashes = new HashSet<string>();
        var warmHashes = new HashSet<string>();
        var allChecksValid = true; var changes = 0; var lastChangeMs = 0d; string? previousHash = null;
        var durations = new List<double>();
        var maximumLit = 0d; var maximumColors = 0; var acceptedCount = 0;
        var elapsed = Stopwatch.StartNew();
        for (var frame = 0; frame < count; frame++)
        {
            allChecksValid &= validate?.Invoke() ?? true;
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Black);
                var target = graphics.GetHdc();
                var watch = Stopwatch.StartNew();
                bool accepted;
                try { accepted = PrintWindow(surface.Handle, target, 2); }
                finally { graphics.ReleaseHdc(target); }
                durations.Add(watch.Elapsed.TotalMilliseconds);
                if (!accepted)
                { Console.WriteLine($"GDI REJECTED {surface.Class}: Win32={Marshal.GetLastWin32Error()}"); break; }
            }
            acceptedCount++;
            var lit = 0; var colors = new HashSet<int>();
            var samples = new List<byte>();
            for (var y = 0; y < bitmap.Height; y += 31)
            for (var x = 0; x < bitmap.Width; x += 31)
            {
                var color = bitmap.GetPixel(x, y); colors.Add(color.ToArgb());
                if (color.R + color.G + color.B > 9) lit++;
                samples.Add(color.R); samples.Add(color.G); samples.Add(color.B);
            }
            var hash = Convert.ToHexString(SHA256.HashData(samples.ToArray()));
            hashes.Add(hash);
            if (frame >= 3) warmHashes.Add(hash);
            if (previousHash != null && hash != previousHash) { changes++; lastChangeMs = elapsed.Elapsed.TotalMilliseconds; }
            previousHash = hash;
            allChecksValid &= validate?.Invoke() ?? true;
            maximumLit = Math.Max(maximumLit, (double)lit / (samples.Count / 3));
            maximumColors = Math.Max(maximumColors, colors.Count);
            // A single black frame cannot prove support; avoid repeated large allocations/work.
            if (frame == 0 && (maximumLit < .01 || maximumColors < 8)) break;
            if (frame + 1 < count) PumpFor(TimeSpan.FromMilliseconds(200));
        }
        durations.Sort();
        Console.WriteLine($"GDI RESULT {surface.Class} phase={phase}: frames={acceptedCount}, distinctSampleHashes={hashes.Count}, " +
            $"postWarmupDistinct={warmHashes.Count}, changes={changes}, lastChangeMs={lastChangeMs:F0}, " +
            $"nonblack={maximumLit:P2}, sampledColors={maximumColors}, elapsedMs={elapsed.Elapsed.TotalMilliseconds:F0}, " +
            $"printMedianMs={(durations.Count > 0 ? durations[durations.Count / 2] : 0):F1}, printMaxMs={durations.DefaultIfEmpty().Max():F1}. No pixels saved or transmitted.");
        return new(hashes.Count, maximumLit, maximumColors, warmHashes.Count, allChecksValid);
    }

    private static IntPtr CreateDesktopCover(WallpaperSurface surface, out bool verified)
    {
        verified = false;
        var root = GetAncestor(surface.Handle, 2); // GA_ROOT
        if (ClassName(root) is not ("WorkerW" or "Progman") || !GetWindowRect(surface.Handle, out var rect)) return IntPtr.Zero;
        var previous = GetWindow(root, 3); // GW_HWNDPREV: retain every existing app above the desktop.
        var foreground = GetForegroundWindow();
        // System STATIC class paints an opaque black rectangle, with input disabled.
        var cover = CreateWindowEx(0x08000080, "STATIC", "Wallpaper capture feasibility cover", 0x88000004,
            rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (cover == IntPtr.Zero) return IntPtr.Zero;
        // HWND_TOP (0) is only used when the desktop itself had no preceding window.
        if (!SetWindowPos(cover, previous, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, 0x0010 | 0x0040)) return cover;
        UpdateWindow(cover);
        PumpFor(TimeSpan.FromMilliseconds(150));
        var synchronized = DwmFlush() == 0;
        verified = synchronized && GetForegroundWindow() == foreground && CoverStillValid(cover, surface);
        return cover;
    }

    private static bool CoverStillValid(IntPtr cover, WallpaperSurface surface)
    {
        var root = GetAncestor(surface.Handle, 2);
        return IsWindowVisible(cover) && GetWindow(cover, 2) == root && // GW_HWNDNEXT: immediately above desktop
            GetWindowRect(cover, out var bounds) && GetWindowRect(surface.Handle, out var target) &&
            bounds.Left <= target.Left && bounds.Top <= target.Top && bounds.Right >= target.Right && bounds.Bottom >= target.Bottom;
    }

    private static void PumpFor(TimeSpan duration)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < duration)
        {
            while (PeekMessage(out var message, IntPtr.Zero, 0, 0, 1))
            { TranslateMessage(ref message); DispatchMessage(ref message); }
            Thread.Sleep(10);
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [StructLayout(LayoutKind.Sequential)] private struct Message { public IntPtr Hwnd; public uint Id; public UIntPtr WParam; public IntPtr LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, IntPtr window, uint minimum, uint maximum, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Message message);
}
