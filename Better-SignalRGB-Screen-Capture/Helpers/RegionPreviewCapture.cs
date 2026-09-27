using System.Buffers;
using System.Drawing;
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Models;
using Windows.Graphics;

namespace Better_SignalRGB_Screen_Capture.Helpers;

/// <summary>One bounded JPEG from the same color-correct graphics backend as live region capture.</summary>
public static class RegionPreviewCapture
{
    private sealed record PreviewFrame(byte[] Pixels, int Width, int Height);

    public static Task<byte[]> CaptureAsync(Rectangle region, CancellationToken cancellationToken,
        IGraphicsCaptureFactory graphicsCaptures)
    {
        ArgumentNullException.ThrowIfNull(graphicsCaptures);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(region.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(region.Height);
        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scale = Math.Min(1d, Math.Min(400d / region.Width, 200d / region.Height));
            var width = Math.Max(1, (int)Math.Round(region.Width * scale));
            var height = Math.Max(1, (int)Math.Round(region.Height * scale));
            var source = new SourceItem
            {
                Name = "Region preview", Type = SourceType.Region,
                RegionBounds = new RectInt32(region.X, region.Y, region.Width, region.Height),
                CanvasWidth = width, CanvasHeight = height
            };
            var capture = graphicsCaptures.Create(source, width, height, 15);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var completion = new TaskCompletionSource<PreviewFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            // RunAsync owns and releases its native resources on cancellation. Its
            // wrapper always completes, so timeout/error cleanup can await it below.
            var pump = Task.Run(async () =>
            {
                try
                {
                    await capture.RunAsync((pixels, frameWidth, frameHeight) =>
                    {
                        var transferred = false;
                        try
                        {
                            if (cancellation.IsCancellationRequested) return;
                            if (frameWidth != width || frameHeight != height || pixels.Length < checked(width * height * 4))
                            {
                                completion.TrySetException(new InvalidDataException("The region preview returned invalid pixel dimensions."));
                                return;
                            }
                            transferred = completion.TrySetResult(new(pixels, frameWidth, frameHeight));
                        }
                        finally
                        {
                            if (!transferred) ArrayPool<byte>.Shared.Return(pixels);
                        }
                    }, _ => { }, cancellation.Token).ConfigureAwait(false);
                    completion.TrySetException(new IOException("The region capture ended before producing a preview."));
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                { completion.TrySetCanceled(cancellation.Token); }
                catch (Exception error) { completion.TrySetException(error); }
            });
            PreviewFrame? frame = null;
            try
            {
                frame = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                using var output = new MemoryStream();
                using var parameters = CaptureFrameEncoder.CreateParameters(CaptureFrameEncoder.HighQuality);
                return CaptureFrameEncoder.EncodeJpeg(frame.Pixels, frame.Width, frame.Height, width, height, output, parameters);
            }
            finally
            {
                cancellation.Cancel();
                await pump.ConfigureAwait(false);
                // Cancellation can win the wait just as a producer transfers a frame.
                // Inspect completion after the pump ends to release that raced buffer too.
                frame ??= completion.Task.IsCompletedSuccessfully ? completion.Task.Result : null;
                if (frame is not null) ArrayPool<byte>.Shared.Return(frame.Pixels);
            }
        }, cancellationToken);
    }
}
