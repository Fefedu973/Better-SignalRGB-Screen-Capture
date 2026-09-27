using System.Drawing;
using System.Runtime.InteropServices;
using Better_SignalRGB_Screen_Capture.Helpers;
using Native = Better_SignalRGB_Screen_Capture.Helpers.DisplayColorInfo.Native;

namespace BetterSignalRGB.RegressionTests;

internal static class DisplayColorInfoTests
{
    public static void Run()
    {
        // SDK native sizes are identical on x86/x64/ARM64: no pointers occur in
        // these structs, and the DISPLAYCONFIG_MODE_INFO union occupies 48 bytes.
        Assert.Equal(8, Marshal.SizeOf<Native.Luid>(), "Native LUID size");
        Assert.Equal(20, Marshal.SizeOf<Native.Header>(), "Display device header size");
        Assert.Equal(20, Marshal.SizeOf<Native.SourceInfo>(), "Display source path size");
        Assert.Equal(48, Marshal.SizeOf<Native.TargetInfo>(), "Display target path size");
        Assert.Equal(72, Marshal.SizeOf<Native.PathInfo>(), "Complete display path stride");
        Assert.Equal(64, Marshal.SizeOf<Native.ModeInfo>(), "Display mode union stride");
        Assert.Equal(84, Marshal.SizeOf<Native.SourceName>(), "GDI source-name packet size");
        Assert.Equal(420, Marshal.SizeOf<Native.TargetName>(), "Target-name packet size");
        Assert.Equal(32, Marshal.SizeOf<Native.AdvancedColor>(), "Legacy color packet size");
        Assert.Equal(36, Marshal.SizeOf<Native.AdvancedColor2>(), "Precise HDR/WCG packet size");
        Assert.Equal(24, Marshal.SizeOf<Native.WhiteLevel>(), "SDR white packet size");
        Assert.Equal(104, Marshal.SizeOf<Native.MonitorInfo>(), "MONITORINFOEXW Unicode size");
        Assert.Equal(16, (int)Marshal.OffsetOf<Native.ModeInfo>(nameof(Native.ModeInfo.Source)), "Source mode union offset");
        Assert.Equal(20, (int)Marshal.OffsetOf<Native.AdvancedColor2>(nameof(Native.AdvancedColor2.Flags)), "Color bitfield offset");
        Assert.Equal(32, (int)Marshal.OffsetOf<Native.AdvancedColor2>(nameof(Native.AdvancedColor2.ActiveColorMode)), "HDR mode offset");
        Assert.Equal(36, (int)Marshal.OffsetOf<Native.TargetName>(nameof(Native.TargetName.FriendlyName)), "Friendly monitor name offset");
        var header = Native.Header.Create<Native.AdvancedColor2>(15, new() { Low = 21, High = 4 }, 7);
        Assert.True(header.Type == 15 && header.Size == 36 && header.Id == 7 && header.Adapter.Low == 21 && header.Adapter.High == 4,
            "Native request preserves the exact adapter/target identity and packet size");

        var bounds = new Rectangle(0, 0, 3440, 1440);
        var hdr = DisplayColorInfo.Decode("Synthetic HDR", @"\\.\DISPLAY2", bounds, 0,
            new() { ActiveColorMode = 2, Flags = 0x73 }, 0, default, 0, 6000);
        Assert.True(hdr.IsKnown && hdr.HdrEnabled == true, "Active HDR is confirmed independently of capability flags");
        Assert.Near(480, hdr.SdrWhiteNits!.Value, 1e-12, "Windows white 6000 means 480 nits, matching the local HDR evidence");
        Assert.True(hdr.Error == null && hdr.Bounds == bounds, "Valid HDR retains physical bounds and no error");
        var sdr = DisplayColorInfo.Decode("Synthetic SDR", @"\\.\DISPLAY1", new(3440, -494, 2560, 1440), 0,
            new() { ActiveColorMode = 0, Flags = 0x30 }, 0, default, 0, 1000);
        Assert.True(sdr.IsKnown && sdr.HdrEnabled == false, "Inactive HDR remains SDR even if user/capability bits are set");
        Assert.Near(80, sdr.SdrWhiteNits!.Value, 1e-12, "Windows SDR reference white is 80 nits");
        var wcg = DisplayColorInfo.Decode(null, null, default, 0, new() { ActiveColorMode = 1, Flags = 0x73 }, 0, default, 0, 1250);
        Assert.True(wcg.HdrEnabled == false, "Wide color gamut is not mistaken for HDR");
        Assert.Near(100, wcg.SdrWhiteNits!.Value, 1e-12, "Fractional SDR white multipliers are preserved");
        var legacy = DisplayColorInfo.Decode(null, null, default, 87, default, 0, new() { Flags = 2 }, 0, 6000);
        Assert.True(!legacy.IsKnown && legacy.HdrEnabled == null && legacy.Error != null,
            "Legacy active Advanced Color is explicitly unknown rather than guessing HDR versus WCG");
        var legacyOff = DisplayColorInfo.Decode(null, null, default, 87, default, 0, default, 0, 1000);
        Assert.True(legacyOff.HdrEnabled == false, "Legacy inactive Advanced Color safely identifies SDR");
        var denied = DisplayColorInfo.Decode(null, null, default, 5, default, 5, default, 5, 0);
        Assert.True(denied.HdrEnabled == null && denied.SdrWhiteNits == null && denied.Error != null,
            "Query denial preserves unknown state without substituting 80 nits");
        var invalidMode = DisplayColorInfo.Decode(null, null, default, 0, new() { ActiveColorMode = 99 }, 0, default, 0, 1000);
        Assert.True(!invalidMode.IsKnown, "An unknown future color mode is not classified as SDR");
        var invalidWhite = DisplayColorInfo.Decode(null, null, default, 0, new() { ActiveColorMode = 2 }, 0, default, 0, 0);
        Assert.True(invalidWhite.HdrEnabled == true && invalidWhite.SdrWhiteNits == null && invalidWhite.Error != null,
            "An invalid white level keeps known HDR but requires an explicit caller fallback");

        var selected = DisplayColorInfo.Resolve([sdr, hdr], @"\\.\display2", Rectangle.Empty, 123);
        Assert.True(selected.HdrEnabled == true && selected.MonitorHandle == 123 && selected.Bounds == bounds,
            "Monitor resolution matches its GDI name exactly, ignoring case and list order");
        var missing = DisplayColorInfo.Resolve([sdr, hdr], @"\\.\DISPLAY3", new(1, 2, 3, 4), 456);
        Assert.True(!missing.IsKnown && missing.MonitorHandle == 456 && missing.Bounds == new Rectangle(1, 2, 3, 4),
            "An unavailable display never falls back to another monitor's HDR state");
        var ambiguous = DisplayColorInfo.Resolve([hdr, sdr with { DeviceName = hdr.DeviceName }], hdr.DeviceName, bounds, 123);
        Assert.True(!ambiguous.IsKnown && ambiguous.Error!.Contains("Cloned"), "Mixed cloned targets cannot borrow the first monitor's color state");
        var whiteMismatch = DisplayColorInfo.Resolve([hdr, hdr with { SdrWhiteNits = 80 }], hdr.DeviceName, bounds, 123);
        Assert.True(!whiteMismatch.IsKnown, "Cloned targets with different SDR white levels are ambiguous");
        var sameClone = DisplayColorInfo.Resolve([hdr, hdr with { Name = "Second identical clone" }], hdr.DeviceName, bounds, 123);
        Assert.True(sameClone.HdrEnabled == true && sameClone.SdrWhiteNits == 480, "Identical clone color settings can be resolved");
        var fallbackBounds = DisplayColorInfo.Resolve([hdr with { Bounds = Rectangle.Empty }], hdr.DeviceName, bounds, 123);
        Assert.Equal(bounds, fallbackBounds.Bounds, "Monitor bounds fill missing source mode bounds");
        Assert.True(!DisplayColorInfo.ForMonitor(0).IsKnown && !DisplayColorInfo.ForWindow(0).IsKnown,
            "Null handles never resolve to the primary monitor");

        var calls = 0; long now = 5000;
        var cache = new DisplayColorCache(() => { calls++; return new(Array.AsReadOnly(new[] { hdr }), null); }, () => now);
        var first = cache.Get(); now += 999;
        Assert.True(ReferenceEquals(first, cache.Get()) && calls == 1, "Display queries are coalesced within one second");
        now++;
        Assert.True(!ReferenceEquals(first, cache.Get()) && calls == 2, "Exactly one second refreshes HDR/white state");
        now--;
        cache.Get();
        Assert.Equal(3, calls, "A backwards test clock cannot freeze a stale cache");
        var failureCalls = 0;
        var failedCache = new DisplayColorCache(() => { failureCalls++; return new(Array.Empty<DisplayColorState>(), "Unavailable"); }, () => now);
        Assert.True(failedCache.Get().Error == "Unavailable" && failedCache.Get().Displays.Count == 0 && failureCalls == 1,
            "Failed queries are cached too, avoiding a failure loop at capture frame rate");
        var parallelCalls = 0;
        var parallel = new DisplayColorCache(() => { Interlocked.Increment(ref parallelCalls); return new(Array.Empty<DisplayColorState>(), null); }, () => 0);
        Parallel.For(0, 32, _ => parallel.Get());
        Assert.Equal(1, parallelCalls, "Concurrent capture workers share one topology query");
    }
}
