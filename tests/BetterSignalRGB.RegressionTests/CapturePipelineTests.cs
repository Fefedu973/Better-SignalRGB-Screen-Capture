using Better_SignalRGB_Screen_Capture.Core.Helpers;

namespace BetterSignalRGB.RegressionTests;

internal static class CapturePipelineTests
{
    public static void Run()
    {
        ResolutionBounds();
        DiscardedVideoDoesNotAccumulate();
        MailboxOwnsEveryFrame().GetAwaiter().GetResult();
        ConcurrentProducerAndShutdown().GetAwaiter().GetResult();
    }

    private static void ResolutionBounds()
    {
        Assert.Equal((100, 80), CaptureGeometry.GetOutputSize(100, 80), "Small source does not upscale to 320x200");
        Assert.Equal((101, 81), CaptureGeometry.GetOutputSize(101, 81), "JPEG preserves odd dimensions independently from the codec");
        Assert.Equal((1, 1), CaptureGeometry.GetOutputSize(1, 1), "JPEG preserves a one-pixel source");
        Assert.Equal((3, 5), CaptureGeometry.GetOutputSize(3, 5), "Tiny JPEG aspect is preserved exactly");
        Assert.Equal((64, 64), CaptureGeometry.GetCarrierSize(3, 5), "Tiny sources have a separate compatible carrier");
        Assert.Equal((1308, 1586), CaptureGeometry.GetCarrierSize(1307, 1586), "Odd carrier near the pixel budget has a bounded rounding allowance");
        Assert.Equal((1920, 1080), CaptureGeometry.GetOutputSize(7680, 4320), "8K source fits bounded preview budget");
        foreach (var (width, height) in new[] { (0, 0), (-20, -100), (1, 100000), (100000, 1),
            (int.MaxValue, int.MaxValue), (4000, 4000), (1080, 1920), (2, int.MaxValue), (1307, 1586), (1439, 1439) })
        {
            var result = CaptureGeometry.GetOutputSize(width, height);
            Assert.True(result.Width >= 1 && result.Height >= 1, "JPEG output dimensions stay positive");
            Assert.True(result.Width <= CaptureGeometry.MaximumDimension && result.Height <= CaptureGeometry.MaximumDimension &&
                (long)result.Width * result.Height <= CaptureGeometry.MaximumPixels, "Output memory remains bounded for extreme inputs");
            var carrier = CaptureGeometry.GetCarrierSize(width, height);
            Assert.True(carrier.Width >= 64 && carrier.Height >= 64 && carrier.Width % 2 == 0 && carrier.Height % 2 == 0 &&
                carrier.Width <= CaptureGeometry.MaximumDimension && carrier.Height <= CaptureGeometry.MaximumDimension &&
                (long)carrier.Width * carrier.Height <= CaptureGeometry.MaximumCarrierPixels,
                "Carrier rounding stays within its independent texture and pixel budget");
        }
    }

    private static void DiscardedVideoDoesNotAccumulate()
    {
        using var sink = new CaptureDiscardStream();
        var frame = new byte[4096];
        for (var i = 0; i < 10000; i++) sink.Write(frame);
        Assert.Equal(40960000L, sink.Length, "Sink reports logical output length without retaining video bytes");
        sink.Seek(0, SeekOrigin.Begin);
        sink.Write(frame, 0, 32);
        Assert.Equal(40960000L, sink.Length, "Header rewrites preserve logical length");
        Assert.Equal(32L, sink.Position, "Header writes advance stream position");
        sink.Seek(100, SeekOrigin.End);
        Assert.Equal(40960100L, sink.Position, "Seek is supported for the native COM stream adapter");
        sink.SetLength(100);
        Assert.Equal(100L, sink.Length, "SetLength updates logical length");
        sink.Dispose();
        var threw = false;
        try { sink.Write(frame); } catch (ObjectDisposedException) { threw = true; }
        Assert.True(threw, "Disposed sink rejects writes");
    }

    private static async Task MailboxOwnsEveryFrame()
    {
        using var mailbox = new CaptureFrameMailbox<TestFrame>();
        var first = new TestFrame();
        var latest = new TestFrame();
        Assert.True(!mailbox.Publish(first), "Publishing to an empty mailbox is not a dropped frame");
        Assert.True(mailbox.Publish(latest), "Replacing an older waiting frame reports backpressure precisely");
        Assert.Equal(1, first.DisposeCount, "Replacing a waiting frame releases its pixels");
        Assert.True(ReferenceEquals(latest, await mailbox.TakeAsync()), "Consumer receives the latest frame");
        latest.Dispose();
        var waiting = mailbox.TakeAsync().AsTask();
        Assert.True(!waiting.IsCompleted, "An empty mailbox waits without spinning");
        mailbox.Dispose();
        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(5)) is null, "Shutdown wakes the waiting consumer");
        var rejected = new TestFrame();
        Assert.True(!mailbox.Publish(rejected), "Shutdown rejection is not counted as capture backpressure");
        Assert.Equal(1, rejected.DisposeCount, "Late native callback releases its buffer after shutdown");
        mailbox.Dispose();
        Assert.Equal(1, latest.DisposeCount, "A delivered frame is never disposed by the mailbox");
    }

    private static async Task ConcurrentProducerAndShutdown()
    {
        using var mailbox = new CaptureFrameMailbox<TestFrame>();
        var frames = Enumerable.Range(0, 10000).Select(_ => new TestFrame()).ToArray();
        var consumer = Task.Run(async () =>
        {
            while (await mailbox.TakeAsync() is { } frame)
            {
                await Task.Yield();
                frame.Dispose();
            }
        });
        await Task.Run(() =>
        {
            for (var i = 0; i < frames.Length; i++)
            {
                mailbox.Publish(frames[i]);
                if (i == 8000) mailbox.Dispose();
            }
        });
        await consumer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(frames.All(frame => frame.DisposeCount == 1), "Concurrent consumption, replacement and shutdown release every frame exactly once");
    }

    private sealed class TestFrame : IDisposable
    {
        private int _disposeCount;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}
