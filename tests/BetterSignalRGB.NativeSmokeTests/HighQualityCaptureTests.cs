using System.Drawing;
using System.Drawing.Imaging;
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Core.Helpers;
using Better_SignalRGB_Screen_Capture.Helpers;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.Services;
using Microsoft.UI.Dispatching;

internal static class HighQualityCaptureTests
{
    public static async Task RunAsync()
    {
        var highSize = CaptureFrameEncoder.GetPreviewSize(320, 200, true);
        Check(highSize == (960, 600), "HQ capture must request genuine isotropic source detail beyond the canonical canvas.");
        var options = CaptureRecorderOptions.Create([], highSize.Width, highSize.Height, 15);
        Check(options.OutputOptions.VideoFramePreviewSize.Width == 960 && options.OutputOptions.VideoFramePreviewSize.Height == 600,
            "The native capture carrier must request the detailed pixels before JPEG encoding.");
        Check(CaptureFrameEncoder.GetPreviewSize(3, 5, true) == (9, 15), "Odd source geometry scales exactly in HQ.");
        var extreme = CaptureFrameEncoder.GetPreviewSize(int.MaxValue, int.MaxValue, true);
        Check(extreme.Width <= CaptureGeometry.MaximumDimension && (long)extreme.Width * extreme.Height <= CaptureGeometry.MaximumPixels,
            "HQ scaling must not overflow or escape the existing allocation budget.");

        using var pattern = new Bitmap(960, 600, PixelFormat.Format24bppRgb);
        using (var drawing = Graphics.FromImage(pattern))
        {
            drawing.Clear(Color.Black);
            for (var x = 0; x < pattern.Width; x += 2) drawing.FillRectangle(Brushes.White, x, 0, 1, pattern.Height);
        }
        using var input = new MemoryStream(); pattern.Save(input, ImageFormat.Png);
        var host = new Browser(input.ToArray()); var factory = new Factory(host);
        var service = new CaptureService(factory, new PipelineDiagnosticsService());
        var source = new SourceItem { Type = SourceType.Website, WebsiteUrl = "https://example.test", CanvasWidth = 320, CanvasHeight = 200 };
        byte[]? eventFrame = null;
        service.FrameAvailable += (_, args) => eventFrame = args.FrameData;
        await service.SetHighQuality(true);
        await service.StartCaptureAsync(source);
        try
        {
            Check(factory.RequestedHighQuality, "The browser factory must use lossless capture input in HQ mode.");
            var rejected = false;
            try { await service.SetHighQuality(false); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Changing quality while a capture owns native resources must be rejected.");
            DispatcherQueueTimer.Latest!.Fire();
            await Until(() => service.GetMjpegFrame(source.Id) != null && eventFrame != null);
            var preview = service.GetMjpegFrame(source.Id)!;
            var signal = service.GetLatestSignalRgbFrame(source.Id)!;
            Check(ReferenceEquals(preview, eventFrame), "Canvas subscribers must receive the same detailed immutable frame as the web stream.");
            Check(ReferenceEquals(signal, service.GetLatestSignalRgbFrame(source.Id)), "SignalRGB reads must reuse a derivative instead of transcoding for every send.");
            using var previewStream = new MemoryStream(preview); using var detailed = new Bitmap(previewStream);
            using var signalStream = new MemoryStream(signal); using var canonical = new Bitmap(signalStream);
            Check(detailed.Width == 960 && detailed.Height == 600 && canonical.Width == 320 && canonical.Height == 200,
                "Preview gains pixels while SignalRGB retains canonical source dimensions.");
            Check(detailed.GetPixel(100, 100).R > 220 && detailed.GetPixel(101, 100).R < 35,
                "HQ must retain genuine one-pixel detail, not enlarge a canonical JPEG.");
            Check(Quantization(preview).Sum(value => (int)value) < Quantization(signal).Sum(value => (int)value),
                "HQ JPEG must use finer quantization than the unchanged low-quality SignalRGB derivative.");
            using var expectedParameters = CaptureFrameEncoder.CreateParameters(65);
            using var expectedStream = new MemoryStream();
            var blackPixels = new byte[320 * 200 * 4];
            var expected65 = CaptureFrameEncoder.EncodeJpeg(blackPixels, 320, 200, 320, 200, expectedStream, expectedParameters);
            Check(Quantization(signal).SequenceEqual(Quantization(expected65)), "SignalRGB keeps the exact quality-65 quantization tables.");
        }
        finally { await service.StopAllCapturesAsync(); }
        Check(service.GetMjpegFrame(source.Id) == null && service.GetLatestSignalRgbFrame(source.Id) == null && host.Stopped == 1,
            "Stop clears both quality variants and closes the owned browser.");
        await service.SetHighQuality(false);
        factory.Host = new Browser(input.ToArray());
        await service.StartCaptureAsync(source);
        try
        {
            DispatcherQueueTimer.Latest!.Fire();
            await Until(() => service.GetMjpegFrame(source.Id) != null);
            Check(!factory.RequestedHighQuality && ReferenceEquals(service.GetMjpegFrame(source.Id), service.GetLatestSignalRgbFrame(source.Id)),
                "Standard capture keeps one shared JPEG without a redundant second encoding.");
        }
        finally { await service.StopAllCapturesAsync(); }
        Console.WriteLine("PASS: HQ native size requests, genuine pixel detail, quality92 versus65, atomic dual frame cache, browser lossless mode and standard-mode reuse.");
    }

    private static byte[] Quantization(byte[] jpeg)
    {
        var result = new List<byte>(); var offset = 2;
        while (offset + 4 <= jpeg.Length && jpeg[offset] == 0xff)
        {
            var marker = jpeg[offset + 1];
            if (marker == 0xda || marker == 0xd9) break;
            var length = jpeg[offset + 2] << 8 | jpeg[offset + 3];
            if (length < 2 || offset + 2 + length > jpeg.Length) throw new InvalidDataException("Invalid synthetic JPEG.");
            if (marker == 0xdb) result.AddRange(jpeg.AsSpan(offset + 4, length - 2).ToArray());
            offset += length + 2;
        }
        Check(result.Count > 0, "JPEG must declare quantization tables.");
        return result.ToArray();
    }
    private sealed class Browser(byte[] input) : IWebsiteFrameSource
    {
        public int Stopped;
        public Task<byte[]?> CaptureFrameAsync() => Task.FromResult<byte[]?>(input);
        public void CaptureStopped() => Stopped++;
    }
    private sealed class Factory(Browser host) : IWebsiteCaptureHostFactory
    {
        public Browser Host = host;
        public bool RequestedHighQuality;
        public IWebsiteFrameSource Create(SourceItem source, bool highQuality = false) { RequestedHighQuality = highQuality; return Host; }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
}
