using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Text;
using System.Threading.Channels;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.Services;

namespace BetterSignalRGB.StreamingTests;

internal static partial class Program
{
    private static async Task CheckHighQualitySignalAsync()
    {
        var capture = new CaptureProducer();
        using var compositor = new CompositeFrameService(capture);
        var source = new SourceItem { CanvasX = 4, CanvasY = -9, CanvasWidth = 320, CanvasHeight = 200, Rotation = 27 };
        var snapshot = StreamingSourceSnapshot.FromSource(source, 0);
        StreamingCanvasSnapshot.Sources = [snapshot];
        var events = Channel.CreateUnbounded<string>();
        Task Send(string message, CancellationToken token) { token.ThrowIfCancellationRequested(); events.Writer.TryWrite(message); return Task.CompletedTask; }
        using var service = new MjpegStreamingService(capture, compositor, true, Send);
        var port = FreePort();
        await service.StartStreamingAsync(port);
        using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
        var preview = SizedJpeg(960, 600, Color.Red);
        var signal = SizedJpeg(320, 200, Color.Blue);
        try
        {
            capture.Publish(source, preview, signal);
            using var response = await client.GetAsync($"/stream/{source.Id}", HttpCompletionOption.ResponseHeadersRead);
            await using var stream = await response.Content.ReadAsStreamAsync();
            Check((await ReadPartAsync(stream)).SequenceEqual(preview), "HTTP source streaming preserves the detailed preview JPEG exactly");
            var transaction = await ReadSignalTransactionAsync(events.Reader, source.Id);
            Check(transaction.Image.SequenceEqual(signal), "SignalRGB receives only the cached canonical derivative, never the HQ JPEG");
            Check(transaction.Header.EndsWith($"outer:{snapshot.OuterStyle}|crop:{snapshot.CropStyle}", StringComparison.Ordinal),
                "HQ leaves canonical SignalRGB position, size, rotation and crop metadata unchanged");
            using (var input = new MemoryStream(transaction.Image))
            using (var image = Image.FromStream(input))
                Check(image.Width == 320 && image.Height == 200, "SignalRGB's decoded image remains at its normal resolution");

            await capture.StopCaptureAsync(source);
            var paused = await ReadSignalTransactionAsync(events.Reader, source.Id);
            Check(paused.Image.SequenceEqual(signal), "Paused replay retains the low-resolution derivative after the capture cache is cleared");
            capture.Fail(source, "Synthetic HQ producer failure.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (await events.Reader.ReadAsync(deadline.Token) != $"remove:{source.Id}") { }
            using var failed = await client.GetAsync($"/stream/{source.Id}");
            Check(failed.StatusCode == HttpStatusCode.ServiceUnavailable, "A terminal failure invalidates the detailed HTTP cache as well as the SignalRGB tile");
            var recoveredSignal = SizedJpeg(320, 200, Color.Green);
            capture.Publish(source, preview, recoveredSignal);
            var recovered = await ReadSignalTransactionAsync(events.Reader, source.Id);
            Check(recovered.Image.SequenceEqual(recoveredSignal), "Recovery replaces the failed low-quality cache instead of replaying its old frame");
        }
        finally { await service.StopStreamingAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private static byte[] SizedJpeg(int width, int height, Color color)
    {
        using var bitmap = new Bitmap(width, height);
        using (var drawing = Graphics.FromImage(bitmap)) drawing.Clear(color);
        using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Jpeg); return stream.ToArray();
    }

    private static async Task<(string Header, byte[] Image)> ReadSignalTransactionAsync(ChannelReader<string> reader, Guid sourceId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var payload = new StringBuilder(); var header = string.Empty;
        while (true)
        {
            var message = await reader.ReadAsync(timeout.Token);
            if (message.StartsWith($"header:{sourceId}.jpg:", StringComparison.Ordinal)) { header = message; payload.Clear(); }
            else if (message.StartsWith($"data:{sourceId}:", StringComparison.Ordinal))
                payload.Append(message[(message.IndexOf(':', $"data:{sourceId}:".Length) + 1)..]);
            else if (message == $"end:{sourceId}") return (header, Convert.FromBase64String(payload.ToString()));
        }
    }
}
