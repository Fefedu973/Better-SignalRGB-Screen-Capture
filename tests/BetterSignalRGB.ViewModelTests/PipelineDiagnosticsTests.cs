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
