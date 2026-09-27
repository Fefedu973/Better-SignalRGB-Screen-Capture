using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Helpers;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.Services;

namespace BetterSignalRGB.GraphicsTests;

internal static partial class Program
{
    private static async Task PreviewAsync()
    {
        var displays = DisplayColorInfo.Enumerate();
        var hdr = displays.FirstOrDefault(display => display.HdrEnabled == true)
            ?? throw new InvalidOperationException("No active HDR display for the requested region preview test.");
        var sdr = displays.FirstOrDefault(display => display.HdrEnabled == false && display.Bounds.Left == hdr.Bounds.Right &&
            display.Bounds.Top <= hdr.Bounds.Top - 80 && display.Bounds.Bottom > hdr.Bounds.Top + 160)
            ?? throw new InvalidOperationException("The mixed preview test requires the HDR/SDR negative-Y monitor arrangement.");
        foreach (var (region, expectedWidth, expectedHeight, mixed) in new[]
        {
            (new Rectangle(hdr.Bounds.Left + 30, hdr.Bounds.Top + 30, 319, 199), 319, 199, false),
            (new Rectangle(hdr.Bounds.Right - 160, hdr.Bounds.Top - 80, 320, 240), 267, 200, true)
        })
        {
            var factory = new ObservedGraphicsFactory();
            var timer = Stopwatch.StartNew();
            var jpeg = await RegionPreviewCapture.CaptureAsync(region, CancellationToken.None, factory).WaitAsync(TimeSpan.FromSeconds(10));
            Check(factory.Active == 0 && factory.Frames > 0, "Region preview returns only after its actual WGC worker has released the sessions");
            Check(jpeg.Length > 4 && jpeg[0] == 0xff && jpeg[1] == 0xd8, "Native preview is an in-memory JPEG");
            using var stream = new MemoryStream(jpeg, writable: false);
            using var bitmap = new Bitmap(stream);
            Check(bitmap.Width == expectedWidth && bitmap.Height == expectedHeight, "Native preview keeps exact odd dimensions and respects its 400×200 aspect bound");
            Check(factory.Color?.Mode == CaptureColorMode.HdrToneMapped && factory.Color.SdrWhiteNits == (mixed ? null : hdr.SdrWhiteNits),
                "Native preview shares the calibrated per-monitor graphics path");
            if (mixed)
            {
                // Only inspect an area with no physical monitor behind it, far from
                // JPEG edges. Pixels from the user's actual screens are not sampled.
                var empty = bitmap.GetPixel(20, 20);
                Check(empty.R <= 2 && empty.G <= 2 && empty.B <= 2, "Mixed preview preserves the off-desktop black band");
            }
            Console.WriteLine(JsonSerializer.Serialize(new { Test = mixed ? "Mixed region preview" : "Odd-sized HDR region preview",
                Width = bitmap.Width, Height = bitmap.Height, Milliseconds = timer.Elapsed.TotalMilliseconds, WorkerStopped = factory.Active == 0,
                Color = factory.Color }));
        }
    }

    private sealed class ObservedGraphicsFactory : IGraphicsCaptureFactory
    {
        public int Active, Frames;
        public CaptureColorInfo? Color;
        public IGraphicsFrameCapture Create(SourceItem source, int width, int height, int frameRate) =>
            new ObservedCapture(this, new GraphicsCaptureFactory().Create(source, width, height, frameRate));
        private sealed class ObservedCapture(ObservedGraphicsFactory owner, IGraphicsFrameCapture inner) : IGraphicsFrameCapture
        {
            public async Task RunAsync(Action<byte[], int, int> onFrame, Action<CaptureColorInfo> onColor, CancellationToken token)
            {
                Interlocked.Increment(ref owner.Active);
                try
                {
                    await inner.RunAsync((bytes, width, height) => { Interlocked.Increment(ref owner.Frames); onFrame(bytes, width, height); },
                        color => { owner.Color = color; onColor(color); }, token);
                }
                finally { Interlocked.Decrement(ref owner.Active); }
            }
        }
    }
}
