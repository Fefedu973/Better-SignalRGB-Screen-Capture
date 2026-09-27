using Better_SignalRGB_Screen_Capture.Core.Helpers;

namespace BetterSignalRGB.RegressionTests;

internal static class PreviewDecodeTests
{
    public static void Run()
    {
        Assert.Equal(200, PreviewDecodeGeometry.DecodeHeight(960, 600, 320, 200, 1, 0, 0, 1, 1), "Standard preview decodes displayed height");
        Assert.Equal(600, PreviewDecodeGeometry.DecodeHeight(960, 600, 320, 200, 2.5, 0, 0, 3, 1), "HQ viewport keeps its 600 physical pixels");
        Assert.Equal(400, PreviewDecodeGeometry.DecodeHeight(960, 600, 320, 200, 2, 0, 0, 2, 1), "Scroll zoom contributes to preview resolution");
        Assert.Equal(400, PreviewDecodeGeometry.DecodeHeight(960, 600, 320, 200, 1, 0, 0, 1, 2), "Display DPI contributes exactly once");
        Assert.Equal(200, PreviewDecodeGeometry.DecodeHeight(320, 200, 320, 200, 30, 0, 0, 30, 2), "Zoom never upscales the decoded JPEG");
        var cosine = Math.Cos(Math.PI / 4);
        var sine = Math.Sin(Math.PI / 4);
        Assert.Equal(600, PreviewDecodeGeometry.DecodeHeight(960, 600, 320, 200,
            -2.5 * cosine, -3 * sine, -2.5 * sine, 3 * cosine, 1), "Rotation and mirror preserve maximum HQ sampling density");
        Assert.Equal(1200, PreviewDecodeGeometry.DecodeHeight(1600, 1200, 400, 300, 3, 0, 0, 4, 1), "Anisotropic scaling preserves the more magnified image axis");
        var bounded = PreviewDecodeGeometry.DecodeHeight(7680, 4320, 7680, 4320, 10, 0, 0, 10, 2);
        Assert.True(bounded <= 1080 && bounded * (7680d / 4320) <= 1920, "Large images respect texture and memory limits");
        Assert.Equal(200, PreviewDecodeGeometry.DecodeHeight(320, 200, 320, 200, double.NaN, 0, 0, 1, 1), "Invalid transforms use bounded native resolution");

        // APP metadata and marker fill bytes precede a baseline or progressive SOF.
        byte[] jpeg = [0xff, 0xd8, 0xff, 0xe1, 0, 4, 9, 9, 0xff, 0xff, 0xc0,
            0, 11, 8, 2, 88, 3, 192, 1, 1, 0x11, 0];
        Assert.True(PreviewDecodeGeometry.TryGetJpegSize(jpeg, out var width, out var height), "Read JPEG frame metadata without decoding pixels");
        Assert.Equal(960, width, "JPEG width");
        Assert.Equal(600, height, "JPEG height");
        jpeg[10] = 0xc2;
        Assert.True(PreviewDecodeGeometry.TryGetJpegSize(jpeg, out _, out _), "Progressive JPEG metadata supported");
        for (var count = 0; count < jpeg.Length; count++)
            Assert.True(!PreviewDecodeGeometry.TryGetJpegSize(jpeg.AsSpan(0, count), out _, out _), "Truncated metadata rejected safely");
        jpeg[11] = jpeg[12] = 0;
        Assert.True(!PreviewDecodeGeometry.TryGetJpegSize(jpeg, out _, out _), "Invalid segment lengths rejected");
        Assert.True(!PreviewDecodeGeometry.TryGetJpegSize([0xff, 0xd8, 0xff, 0xda, 0, 2], out _, out _), "Entropy data is never scanned as metadata");
    }
}
