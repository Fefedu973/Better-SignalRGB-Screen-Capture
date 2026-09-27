using System.Globalization;
using System.Text;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.Services;

namespace BetterSignalRGB.RegressionTests;

internal static class StreamingPipelineTests
{
    public static async Task RunAsync()
    {
        await TestLatestFrameBroadcastAsync();
        await TestMultipartAsync();
        TestCropGeometry();
        TestInvariantStyles();
    }

    private static async Task TestLatestFrameBroadcastAsync()
    {
        var state = new StreamingFrameState();
        var firstReader = state.WaitForNextAsync(null, CancellationToken.None);
        var secondReader = state.WaitForNextAsync(null, CancellationToken.None);
        Assert.True(!firstReader.IsCompleted && !secondReader.IsCompleted, "An empty source waits without sending invalid JPEGs");
        byte[] first = [1];
        state.Publish(first);
        Assert.True(ReferenceEquals(first, await firstReader.WaitAsync(TimeSpan.FromSeconds(2))), "First reader receives the shared immutable buffer");
        Assert.True(ReferenceEquals(first, await secondReader.WaitAsync(TimeSpan.FromSeconds(2))), "Every waiting client receives the frame");
        byte[] latest = first;
        for (var index = 0; index < 10000; index++) { latest = [(byte)index]; state.Publish(latest); }
        Assert.True(ReferenceEquals(latest, await state.WaitForNextAsync(first, CancellationToken.None)), "A slow client skips 10000 obsolete frames without a queue");
        using var cancellation = new CancellationTokenSource();
        var cancelledReader = state.WaitForNextAsync(latest, cancellation.Token);
        state.Publish(latest);
        Assert.True(!cancelledReader.IsCompleted, "Publishing the same buffer must not duplicate a frame");
        cancellation.Cancel();
        try { await cancelledReader; Assert.True(false, "Disconnected clients must cancel"); }
        catch (OperationCanceledException) { Assert.True(true, "Disconnected clients cancel promptly"); }
        var waiting = state.WaitForNextAsync(latest, CancellationToken.None);
        state.Close();
        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(2)) == null, "Removing a source wakes all clients");
        state.Publish([42]);
        Assert.True(state.Latest == null, "A late callback cannot resurrect a closed source");
    }

    private static async Task TestMultipartAsync()
    {
        byte[] frame = [0xff, 0xd8, 0, 1, 0xff, 0xd9];
        using var output = new MemoryStream();
        await StreamingMultipartWriter.WriteAsync(output, frame, CancellationToken.None);
        await StreamingMultipartWriter.WriteAsync(output, frame, CancellationToken.None);
        var bytes = output.ToArray();
        var prefix = Encoding.ASCII.GetBytes("--mjpegboundary\r\nContent-Type: image/jpeg\r\nContent-Length: 6\r\n\r\n");
        Assert.Equal("multipart/x-mixed-replace; boundary=mjpegboundary", StreamingMultipartWriter.ContentType, "The MIME boundary parameter excludes framing dashes");
        Assert.True(bytes.AsSpan(0, prefix.Length).SequenceEqual(prefix), "The stream starts with a boundary and CRLF headers");
        Assert.True(bytes.AsSpan(prefix.Length, frame.Length).SequenceEqual(frame), "JPEG bytes are passed through without re-encoding");
        Assert.True(bytes.AsSpan(prefix.Length + frame.Length, 2).SequenceEqual("\r\n"u8), "Each frame has trailing CRLF");
        Assert.True(bytes.AsSpan(prefix.Length + frame.Length + 2, prefix.Length).SequenceEqual(prefix), "Subsequent frames have the exact same boundary");
    }

    private static void TestCropGeometry()
    {
        var source = new SourceItem { CanvasWidth = 200, CanvasHeight = 100, CropLeftPct = 0.1, CropTopPct = 0.2, CropRightPct = 0.3, CropBottomPct = 0.1 };
        var layout = StreamingSourceSnapshot.FromSource(source, 0);
        var polygon = layout.GetCropPolygon();
        Assert.Near(20, polygon[0].X, 0.001, "Asymmetric crop left edge");
        Assert.Near(20, polygon[0].Y, 0.001, "Asymmetric crop top edge");
        Assert.Near(140, polygon[2].X, 0.001, "Asymmetric crop right edge");
        Assert.Near(90, polygon[2].Y, 0.001, "Asymmetric crop bottom edge");
        var rotated = (layout with { CropRotation = 90 }).GetCropPolygon();
        Assert.Near(115, rotated[0].X, 0.001, "Rotation uses the crop centre in pixel coordinates");
        Assert.Near(-5, rotated[0].Y, 0.001, "Non-square crop rotation preserves aspect ratio");
        foreach (var angle in new[] { 0, 45, 90, 180, 270, 359 })
        foreach (var mirrorH in new[] { false, true })
        foreach (var mirrorV in new[] { false, true })
        {
            var unmirrored = (layout with { CropRotation = angle }).GetCropPolygon();
            var mirrored = (layout with { CropRotation = angle, IsMirroredHorizontally = mirrorH, IsMirroredVertically = mirrorV }).GetCropPolygon();
            for (var i = 0; i < mirrored.Length; i++)
            {
                Assert.Near(unmirrored[i].X, mirrorH ? source.CanvasWidth - mirrored[i].X : mirrored[i].X, 0.0001, "Mirroring the image leaves the crop mask fixed horizontally");
                Assert.Near(unmirrored[i].Y, mirrorV ? source.CanvasHeight - mirrored[i].Y : mirrored[i].Y, 0.0001, "Mirroring the image leaves the crop mask fixed vertically");
            }
        }
    }

    private static void TestInvariantStyles()
    {
        var oldCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var source = new SourceItem { CanvasWidth = 200, CanvasHeight = 100, Opacity = 0.5, CropRotation = 33, CropLeftPct = 0.125 };
            var layout = StreamingSourceSnapshot.FromSource(source, 2);
            Assert.True(layout.OuterStyle.Contains("opacity:0.5;"), "CSS numeric output is independent of the French locale");
            Assert.True(layout.CropStyle.Contains("clip-path:polygon("), "Rotated crop is a mask rather than a distorted image transform");
            Assert.True(!layout.CropStyle.Contains("transform:"), "Crop rotation does not rotate the captured image");
        }
        finally { CultureInfo.CurrentCulture = oldCulture; }
    }
}
