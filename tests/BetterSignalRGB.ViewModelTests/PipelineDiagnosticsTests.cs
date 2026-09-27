using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.Services;

namespace BetterSignalRGB.ViewModelTests;

internal static class PipelineDiagnosticsTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var clock = new TestClock();
        var service = new PipelineDiagnosticsService(clock);
        var id = Guid.NewGuid();
        var first = service.BeginCapture(id, "Screen", CaptureEncoderKind.Hardware, 30, 319, 199);
        for (var index = 0; index < 4; index++) first.Received();
        first.Produced(1000, 3); first.Produced(500, 5); first.Dropped(); first.Skipped(); first.Skipped();
        first.Error("Hardware encoder rejected the size"); clock.Advance(TimeSpan.FromSeconds(1));
        var snapshot = service.GetSnapshot(); var source = snapshot.Sources.Single();
        check(source.ReceivedFrames == 4 && source.ProducedFrames == 2 && source.DroppedFrames == 1 && source.SkippedCaptures == 2,
            "Diagnostics distinguish received/produced frames, mailbox replacement and skipped website requests");
        check(source.ProducedBytes == 1500 && source.MeanProcessingMilliseconds == 4 && source.CaptureFramesPerSecond == 2,
            "Diagnostics report production size, measured processing time and recent rate without a frame queue");
        check(source.Width == 319 && source.Height == 199 && source.Encoder == CaptureEncoderKind.Hardware && source.Errors == 1,
            "Diagnostics retain logical output resolution and requested native encoder");
        first.Stop(); first.Produced(100, 100); first.Error("Late callback", terminal: true);
        check(service.GetSnapshot().Sources.Single() is { State: CaptureDiagnosticState.Stopped, CaptureFramesPerSecond: 0, ProducedFrames: 2, Errors: 1 },
            "A stopped diagnostic session rejects late frames and errors and exposes zero capture rate");
        var second = service.BeginCapture(id, "Screen renamed", CaptureEncoderKind.Software, 15, 320, 200);
        first.Received(); first.Produced(999, 999); first.Error("Old generation"); first.Stop();
        second.Received(); second.Produced(200, 1); second.SetFrameRate(24);
        source = service.GetSnapshot().Sources.Single();
        check(source is { Name: "Screen renamed", Encoder: CaptureEncoderKind.Software, State: CaptureDiagnosticState.Capturing,
            RequestedFrameRate: 24, ReceivedFrames: 5, ProducedFrames: 3, Errors: 1 },
            "A restarted session keeps cumulative counters but ignores callbacks from its previous generation");
        check(snapshot.Sources.Single().ProducedFrames == 2 && snapshot.Sources.Single().Name == "Screen",
            "Previously returned diagnostic snapshots never change underneath a reader");
        clock.Advance(TimeSpan.FromSeconds(6));
        check(service.GetSnapshot().Sources.Single().CaptureFramesPerSecond == 0, "Idle sources decay to zero rate even if the capture session stays active");
        second.Error("Device disconnected", terminal: true); second.Stop();
        check(service.GetSnapshot().Sources.Single() is { State: CaptureDiagnosticState.Failed, LastError: "Device disconnected", Errors: 2 },
            "Final capture failure remains distinct from a normal stopped session");
        var active = service.BeginCapture(id, "Screen", CaptureEncoderKind.Software, 30, 320, 200);
        service.SetSignalRgbRunning(true);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var index = 0; index < 2500; index++)
            {
                active.Received(); active.Produced(100, 2); active.Dropped();
                service.RecordSignalRgbFrame(id, 100, 3);
            }
        })));
        var parallel = service.GetSnapshot(); source = parallel.Sources.Single();
        check(source.ReceivedFrames == 10005 && source.ProducedFrames == 10003 && source.DroppedFrames == 10001,
            "Concurrent native callbacks and workers update source counters without losing increments");
        check(source.SentFrames == 10000 && source.MeanSendMilliseconds == 3 && parallel.SignalRgb.SentFrames == 10000 && parallel.SignalRgb.SentJpegBytes == 1000000,
            "Per-source and global transport counters include only completed source sends");
        service.RecordSignalRgbError("API unavailable");
        check(service.GetSnapshot().SignalRgb is { Errors: 1, LastError: "API unavailable" }, "Transport errors are visible independently of capture errors");
        service.SetSignalRgbRunning(false); service.SetSignalRgbRunning(true);
        check(service.GetSnapshot().SignalRgb.FramesPerSecond == 0 && service.GetSnapshot().Sources.Single().SentFramesPerSecond == 0,
            "Restarting the sender does not show stale throughput from the previous session");
        service.RemoveSource(id); active.Produced(100, 1); active.Error("Removed source");
        check(service.GetSnapshot().Sources.Count == 0, "Removed source diagnostics cannot be resurrected by stale session callbacks");
        CheckColorDiagnostics(check);
    }

    private static void CheckColorDiagnostics(Action<bool, string> check)
    {
        var service = new PipelineDiagnosticsService();
        var id = Guid.NewGuid();
        var first = service.BeginCapture(id, "Display", CaptureEncoderKind.WindowsGraphicsCapture, 15, 320, 200);
        check(service.GetSnapshot().Sources.Single().ColorInfo.Mode == CaptureColorMode.Unknown,
            "A float-capable backend does not claim successful HDR conversion before the producer reports it");
        first.SetColorInfo(new(CaptureColorMode.HdrToneMapped, "WGC FP16", 240));
        var hdr = service.GetSnapshot().Sources.Single();
        check(hdr.ColorInfo is { Mode: CaptureColorMode.HdrToneMapped, Backend: "WGC FP16", SdrWhiteNits: 240 } &&
              hdr.ColorInfo.Description.Contains("HDR → SDR", StringComparison.Ordinal),
            "HDR diagnostics identify the actual conversion and source display SDR reference white");
        first.SetColorInfo(new(CaptureColorMode.Sdr, "WGC FP16"));
        check(service.GetSnapshot().Sources.Single().ColorInfo.Mode == CaptureColorMode.Sdr &&
              hdr.ColorInfo.Mode == CaptureColorMode.HdrToneMapped,
            "An HDR-to-SDR display transition updates diagnostics without mutating an earlier snapshot");
        first.Stop();
        first.SetColorInfo(new(CaptureColorMode.HdrToneMapped, "Late callback", 400));
        check(service.GetSnapshot().Sources.Single().ColorInfo is { Mode: CaptureColorMode.Sdr, Backend: "WGC FP16" },
            "Stopped sources keep their last verified color state and reject late backend reports");
        var second = service.BeginCapture(id, "Wallpaper", CaptureEncoderKind.Wallpaper, 15, 320, 200);
        first.SetColorInfo(new(CaptureColorMode.HdrToneMapped, "Old session"));
        var wallpaper = service.GetSnapshot().Sources.Single().ColorInfo;
        check(wallpaper is { Mode: CaptureColorMode.HdrUnsupported, Backend: "GDI PrintWindow BGRA8", SdrWhiteNits: null } &&
              wallpaper.Description == "HDR tone mapping unavailable",
            "Wallpaper GDI reports its HDR limitation without inheriting a previous WGC session's tone-mapping claim");
        second.SetColorInfo(new((CaptureColorMode)999, new string('x', 256), double.NaN));
        var invalid = service.GetSnapshot().Sources.Single().ColorInfo;
        check(invalid.Mode == CaptureColorMode.Unknown && invalid.Backend.Length == 128 && invalid.SdrWhiteNits is null,
            "Invalid color metadata cannot create a false mode, unbounded label or NaN luminance");
        second.SetColorInfo(new(CaptureColorMode.Sdr, "\r\n ", -1));
        check(service.GetSnapshot().Sources.Single().ColorInfo is { Backend: "Unreported backend", SdrWhiteNits: null },
            "Empty backend labels and nonpositive reference white are normalized before display");
        second.Error("Capture failed", terminal: true);
        second.SetColorInfo(new(CaptureColorMode.HdrToneMapped, "Failed session"));
        check(service.GetSnapshot().Sources.Single().ColorInfo.Mode == CaptureColorMode.Sdr,
            "A terminal error cannot be masked by late color-conversion metadata");
        service.RemoveSource(id);
        second.SetColorInfo(new(CaptureColorMode.HdrToneMapped, "Removed session"));
        check(service.GetSnapshot().Sources.Count == 0, "Removed source color diagnostics cannot be resurrected");
        service.BeginCapture(id, "Website", CaptureEncoderKind.Website, 15, 320, 200);
        check(service.GetSnapshot().Sources.Single().ColorInfo is { Mode: CaptureColorMode.Sdr, Backend: "WebView2 screenshot" },
            "Browser screenshots are reported as SDR output rather than app-owned HDR tone mapping");
    }

    private sealed class TestClock : TimeProvider
    {
        private long _milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref _milliseconds);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(GetTimestamp());
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _milliseconds, (long)elapsed.TotalMilliseconds);
    }
}
