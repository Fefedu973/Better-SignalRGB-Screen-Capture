using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.Services;

namespace BetterSignalRGB.StreamingTests;

internal static partial class Program
{
    private static int _assertions;
    private static void Check(bool value, string message)
    { _assertions++; if (!value) throw new InvalidOperationException(message); }

    private static async Task Main(string[] args)
    {
        if (args is ["--export-effect-fixtures", var fixturePath])
        {
            await EffectRenderFixtures.ExportAsync(fixturePath);
            return;
        }
        var capture = new CaptureProducer();
        await CheckFailedFrameCacheAsync();
        using var compositor = new CompositeFrameService(capture);
        await CheckCompositePixelsAsync(capture, compositor);
        await CheckHighQualityCompositeAsync(capture, compositor);
        await CheckHttpAsync(capture, compositor);
        await CheckHttpsAsync(capture, compositor);
        await CheckCanvasBackpressureAsync(capture, compositor);
        await CheckCanvasFailureRecoveryAsync(capture, compositor);
        await EffectConfigurationTransportTests.RunAsync(Check);
        await CheckHighQualitySignalAsync();
        Console.WriteLine($"PASS: {_assertions} production compositor/HTTP/HTTPS integration assertions.");
    }

    private static byte[] Jpeg(Color left, Color? right = null)
    {
        using var bitmap = new Bitmap(100, 60);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(left);
            if (right.HasValue) { using var brush = new SolidBrush(right.Value); graphics.FillRectangle(brush, 50, 0, 50, 60); }
        }
        using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Jpeg); return stream.ToArray();
    }

    private static async Task<Bitmap> CompositeAsync(CaptureProducer capture, CompositeFrameService compositor, params (SourceItem Source, byte[] Frame)[] sources)
    {
        StreamingCanvasSnapshot.Sources = sources.Select((item, index) => StreamingSourceSnapshot.FromSource(item.Source, index)).ToArray();
        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrame(object? sender, byte[] frame) => completion.TrySetResult(frame);
        compositor.CompositeFrameAvailable += OnFrame;
        try
        {
            foreach (var item in sources) capture.Publish(item.Source, item.Frame);
            compositor.InvalidateLayout();
            var bytes = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var stream = new MemoryStream(bytes);
            using var image = Image.FromStream(stream);
            return new Bitmap(image);
        }
        finally { compositor.CompositeFrameAvailable -= OnFrame; }
    }

    private static void Pixel(Bitmap bitmap, int x, int y, Color expected, string name, int tolerance = 25)
    {
        var actual = bitmap.GetPixel(x, y);
        Check(Math.Abs(actual.R - expected.R) <= tolerance && Math.Abs(actual.G - expected.G) <= tolerance && Math.Abs(actual.B - expected.B) <= tolerance,
            $"{name} at ({x},{y}): expected {expected}, got {actual}");
    }

    private static async Task CheckCompositePixelsAsync(CaptureProducer capture, CompositeFrameService compositor)
    {
        var red = Jpeg(Color.Red); var blue = Jpeg(Color.Blue); var white = Jpeg(Color.White);
        var back = new SourceItem { CanvasX = 10, CanvasY = 10, CanvasWidth = 100, CanvasHeight = 60 };
        var front = new SourceItem { CanvasX = 30, CanvasY = 20, CanvasWidth = 100, CanvasHeight = 60 };
        using (var image = await CompositeAsync(capture, compositor, (back, red), (front, blue)))
        { Pixel(image, 45, 35, Color.Blue, "Foreground source follows source list order"); Pixel(image, 15, 35, Color.Red, "Background remains visible outside foreground"); }
        front.Opacity = 0.5;
        using (var image = await CompositeAsync(capture, compositor, (back, red), (front, blue)))
            Pixel(image, 45, 35, Color.FromArgb(127, 0, 127), "Opacity blends source over previous layer");
        var offscreen = new SourceItem { CanvasX = -50, CanvasY = 20, CanvasWidth = 100, CanvasHeight = 60 };
        using (var image = await CompositeAsync(capture, compositor, (offscreen, Jpeg(Color.Red, Color.Blue))))
            Pixel(image, 10, 40, Color.Blue, "Off-canvas content is clipped without squeezing the source");
        var mirrored = new SourceItem { CanvasX = 0, CanvasY = 0, CanvasWidth = 100, CanvasHeight = 60, CropLeftPct = 0.5, IsMirroredHorizontally = true };
        using (var image = await CompositeAsync(capture, compositor, (mirrored, Jpeg(Color.Red, Color.Blue))))
        { Pixel(image, 20, 30, Color.Black, "Mirroring leaves the asymmetric crop on the same side"); Pixel(image, 75, 30, Color.Red, "Media is mirrored inside its fixed crop"); }
        var cropRotated = new SourceItem { CanvasWidth = 100, CanvasHeight = 60, CropLeftPct = 0.2, CropRightPct = 0.2, CropTopPct = 0.25, CropBottomPct = 0.25, CropRotation = 90 };
        using (var image = await CompositeAsync(capture, compositor, (cropRotated, white)))
        { Pixel(image, 50, 5, Color.White, "A rotated crop rotates its mask about its own centre"); Pixel(image, 25, 30, Color.Black, "Rotated crop clips the image"); }
        var rotated = new SourceItem { CanvasX = 100, CanvasY = 50, CanvasWidth = 80, CanvasHeight = 40, Rotation = 90 };
        using (var image = await CompositeAsync(capture, compositor, (rotated, white)))
        { Pixel(image, 140, 40, Color.White, "Outer rotation uses the source centre"); Pixel(image, 105, 70, Color.Black, "Original unrotated rectangle is not painted"); }
        using (var image = await CompositeAsync(capture, compositor)) Pixel(image, 140, 40, Color.Black, "Removing every source clears the composite");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }

    private static async Task CheckHttpAsync(CaptureProducer capture, CompositeFrameService compositor)
    {
        var source = new SourceItem { CanvasWidth = 100, CanvasHeight = 60 };
        StreamingCanvasSnapshot.Sources = [StreamingSourceSnapshot.FromSource(source, 0)];
        using var service = new MjpegStreamingService(capture, compositor, publishCanvasEvents: false);
        var port = FreePort();
        await service.StartStreamingAsync(port);
        using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
        try
        {
            await CheckEndpointsAsync(client, capture, compositor, source, () => service.NotifySourceRemovedAsync(source.Id));
            await CheckCaptureFailureAsync(client, capture, compositor, source);
        }
        finally { await service.StopStreamingAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        Check(!service.IsStreaming && service.StreamingUrl == null, "HTTP stop releases lifecycle state");
        await service.StartStreamingAsync(port);
        await service.StopStreamingAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(!service.IsStreaming, "An immediate HTTP restart/stop completes");
    }

    private static async Task CheckHttpsAsync(CaptureProducer capture, CompositeFrameService compositor)
    {
        var certificatePath = Path.Combine(AppContext.BaseDirectory, "localhost.pfx");
        using (var rsa = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); request.CertificateExtensions.Add(names.Build());
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            await File.WriteAllBytesAsync(certificatePath, certificate.Export(X509ContentType.Pfx));
        }
        var source = new SourceItem { CanvasWidth = 100, CanvasHeight = 60 };
        StreamingCanvasSnapshot.Sources = [StreamingSourceSnapshot.FromSource(source, 0)];
        using var service = new KestrelApiService(capture, compositor);
        var port = FreePort();
        await service.StartAsync(port);
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var client = new HttpClient(handler) { BaseAddress = new Uri($"https://localhost:{port}") };
        try
        {
            await CheckEndpointsAsync(client, capture, compositor, source, () => { service.RemoveSource(source.Id); return Task.CompletedTask; });
            await CheckCaptureFailureAsync(client, capture, compositor, source);
        }
        finally { await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)); File.Delete(certificatePath); }
        Check(!service.IsRunning && service.StreamingUrl == null, "HTTPS stop releases lifecycle state");
    }

    private static async Task CheckEndpointsAsync(HttpClient client, CaptureProducer capture, CompositeFrameService compositor, SourceItem source, Func<Task> remove)
    {
        var frame = Jpeg(Color.Red, Color.Blue); capture.Publish(source, frame);
        using var metadata = JsonDocument.Parse(await client.GetStringAsync("/api/canvasinfo"));
        Check(metadata.RootElement.GetProperty("canvasWidth").GetInt32() == 320 && metadata.RootElement.GetProperty("canvasHeight").GetInt32() == 200,
            "API canvas dimensions match the editor");
        Check(metadata.RootElement.GetProperty("sources")[0].GetProperty("id").GetGuid() == source.Id, "API source snapshot is serialized correctly");
        var canvasPage = await client.GetStringAsync("/canvas/");
        Check(canvasPage.Contains("fetch('/stream'"), "Output page consumes the actual composite stream");
        Check(!canvasPage.Contains("id=\"status\"") && !canvasPage.Contains("Connecting") && !canvasPage.Contains("/api/canvasinfo"),
            "Output page contains no debug text or redundant layout polling");
        Check(await client.GetStringAsync("/") == canvasPage, "The root URL is the same clean RGB output surface");
        Check((await client.GetAsync("/stream/" + Guid.NewGuid())).StatusCode == HttpStatusCode.NotFound, "Unknown sources return 404 rather than allocating an idle stream");
        using var response1 = await client.GetAsync($"/stream/{source.Id}", HttpCompletionOption.ResponseHeadersRead);
        using var response2 = await client.GetAsync($"/stream/{source.Id}", HttpCompletionOption.ResponseHeadersRead);
        Check(response1.Content.Headers.ContentType?.ToString() == StreamingMultipartWriter.ContentType, "Multipart MIME uses the same boundary on both servers");
        await using var stream1 = await response1.Content.ReadAsStreamAsync();
        await using var stream2 = await response2.Content.ReadAsStreamAsync();
        Check((await ReadPartAsync(stream1)).SequenceEqual(frame), "First client gets the exact published JPEG");
        Check((await ReadPartAsync(stream2)).SequenceEqual(frame), "Second client gets the same JPEG without per-client encoding");
        var next = Jpeg(Color.Green); capture.Publish(source, next);
        Check((await ReadPartAsync(stream1)).SequenceEqual(next), "An active client receives the next frame");
        Check((await ReadPartAsync(stream2)).SequenceEqual(next), "All clients receive the next frame");
        await remove();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var remaining = new byte[16];
        while (await stream1.ReadAsync(remaining, timeout.Token) > 0) { }
        Check(true, "Source removal closes established clients");
        using var composite = await client.GetAsync("/stream", HttpCompletionOption.ResponseHeadersRead);
        await using var compositeStream = await composite.Content.ReadAsStreamAsync();
        var compositeJpeg = await ReadPartAsync(compositeStream);
        using var bytes = new MemoryStream(compositeJpeg); using var image = Image.FromStream(bytes);
        Check(image.Width == 320 && image.Height == 200, "The combined stream endpoint delivers a decodable composite");
        compositor.SetCanvasSize(800, 600);
        await CheckStreamResolutionAsync(compositeStream, 800, 600);
        compositor.SetCanvasSize(320, 200);
        await CheckStreamResolutionAsync(compositeStream, 320, 200);
    }

    private static async Task CheckCanvasBackpressureAsync(CaptureProducer capture, CompositeFrameService compositor)
    {
        var source = new SourceItem { CanvasWidth = 100, CanvasHeight = 60 };
        StreamingCanvasSnapshot.Sources = [StreamingSourceSnapshot.FromSource(source, 0)];
        var enteredHeader = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHeader = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var twoFramesSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var messages = new List<string>();
        var active = 0; var maximumActive = 0; var headers = 0; var ends = 0;
        async Task Send(string message, CancellationToken token)
        {
            var simultaneous = Interlocked.Increment(ref active);
            maximumActive = Math.Max(maximumActive, simultaneous);
            try
            {
                messages.Add(message);
                if (message.StartsWith("header:") && ++headers == 1)
                {
                    enteredHeader.TrySetResult();
                    await releaseHeader.Task.WaitAsync(token);
                }
                if (message.StartsWith("end:") && ++ends == 2) twoFramesSent.TrySetResult();
            }
            finally { Interlocked.Decrement(ref active); }
        }
        using var service = new MjpegStreamingService(capture, compositor, publishCanvasEvents: true, canvasEventSender: Send);
        await service.StartStreamingAsync(FreePort());
        var first = Jpeg(Color.Red); var last = Jpeg(Color.Green);
        try
        {
            capture.Publish(source, first);
            await enteredHeader.Task.WaitAsync(TimeSpan.FromSeconds(3));
            for (var index = 0; index < 1000; index++) capture.Publish(source, [(byte)index]);
            capture.Publish(source, last);
            releaseHeader.TrySetResult();
            await twoFramesSent.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { await service.StopStreamingAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        Check(maximumActive == 1, "A slow SignalRGB receiver never causes overlapping header/data/end transactions");
        Check(headers == 2 && ends == 2, "1000 pending source updates collapse to one newest frame");
        var rebuilt = new List<byte[]>(); var payload = new StringBuilder();
        foreach (var message in messages)
        {
            if (message.StartsWith("header:")) payload.Clear();
            else if (message.StartsWith("data:")) payload.Append(message.Split(':', 4)[3]);
            else if (message.StartsWith("end:")) rebuilt.Add(Convert.FromBase64String(payload.ToString()));
        }
        Check(rebuilt[0].SequenceEqual(first) && rebuilt[1].SequenceEqual(last), "SignalRGB transactions retain intact first/latest JPEGs without mixed chunks");
    }

    private static async Task CheckFailedFrameCacheAsync()
    {
        var frames = new StreamingSourceFrames();
        var source = new SourceItem();
        var stale = new byte[] { 1, 2, 3 };
        frames.Publish(source.Id, stale);
        var oldReader = frames.Open(source.Id, _ => stale)!;
        frames.Invalidate(source.Id);
        Check(oldReader.Latest == null && await oldReader.WaitForNextAsync(null, CancellationToken.None) == null,
            "Failure clears cached bytes and closes readers of that generation");
        Check(frames.Open(source.Id, _ => stale) == null && frames.Latest(source.Id, _ => stale) == null,
            "Failure cannot reopen a stream from a stale provider snapshot");
        Check(frames.FilterAvailable([StreamingSourceSnapshot.FromSource(source, 0)]).Length == 0,
            "Unavailable source is excluded from the rendered layout");
        var recovered = new byte[] { 4, 5, 6 };
        frames.Publish(source.Id, recovered);
        var newReader = frames.Open(source.Id, _ => stale)!;
        Check(!ReferenceEquals(oldReader, newReader) && ReferenceEquals(newReader.Latest, recovered),
            "Recovery opens a fresh stream generation with only the new frame");
        Check(await oldReader.WaitForNextAsync(null, CancellationToken.None) == null,
            "Recovery never reopens already closed clients");
        frames.Clear();
    }

    private static async Task CheckCaptureFailureAsync(HttpClient client, CaptureProducer capture, CompositeFrameService compositor, SourceItem source)
    {
        var original = Jpeg(Color.Red);
        using (var image = await CompositeAsync(capture, compositor, (source, original)))
            Pixel(image, 20, 20, Color.Red, "Source is visible before the simulated native failure");
        using var response1 = await client.GetAsync($"/stream/{source.Id}", HttpCompletionOption.ResponseHeadersRead);
        using var response2 = await client.GetAsync($"/stream/{source.Id}", HttpCompletionOption.ResponseHeadersRead);
        await using var stream1 = await response1.Content.ReadAsStreamAsync();
        await using var stream2 = await response2.Content.ReadAsStreamAsync();
        Check((await ReadPartAsync(stream1)).SequenceEqual(original) && (await ReadPartAsync(stream2)).SequenceEqual(original),
            "Both source clients receive the frame before failure");

        capture.Fail(source, "Simulated final capture failure");
        Check(capture.GetMjpegFrame(source.Id) == null, "Failed producer clears its last JPEG");
        await ExpectStreamEndAsync(stream1);
        await ExpectStreamEndAsync(stream2);
        Check(true, "Final capture failure closes all established source clients");
        using (var unavailable = await client.GetAsync($"/stream/{source.Id}"))
            Check(unavailable.StatusCode == HttpStatusCode.ServiceUnavailable && unavailable.Headers.RetryAfter?.Delta == TimeSpan.FromSeconds(1),
                "A failed configured source returns 503 instead of replaying cached pixels or hanging");
        using (var layout = JsonDocument.Parse(await client.GetStringAsync("/api/canvasinfo")))
            Check(layout.RootElement.GetProperty("sources").GetArrayLength() == 0, "Failed source is removed from the browser canvas layout");
        using (var sources = JsonDocument.Parse(await client.GetStringAsync("/api/sources")))
            Check(sources.RootElement.GetArrayLength() == 0, "Source and canvas metadata agree about a failed source");
        using (var response = await client.GetAsync("/stream", HttpCompletionOption.ResponseHeadersRead))
        {
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var bytes = new MemoryStream(await ReadPartAsync(stream));
            using var image = new Bitmap(bytes);
            Pixel(image, 20, 20, Color.Black, "Failure clears decoded source pixels and previously rendered composite cache");
        }

        var recovered = Jpeg(Color.Blue);
        capture.Publish(source, recovered);
        using (var layout = JsonDocument.Parse(await client.GetStringAsync("/api/canvasinfo")))
            Check(layout.RootElement.GetProperty("sources").GetArrayLength() == 1, "A fresh capture frame restores the source layout");
        using (var response = await client.GetAsync($"/stream/{source.Id}", HttpCompletionOption.ResponseHeadersRead))
        {
            await using var stream = await response.Content.ReadAsStreamAsync();
            Check((await ReadPartAsync(stream)).SequenceEqual(recovered), "Recovered source delivers its fresh JPEG without a ghost from the failed generation");
        }

        // Stopping/pausing is deliberately different from an error: existing good pixels freeze.
        await capture.StopCaptureAsync(source);
        using (var response = await client.GetAsync($"/stream/{source.Id}", HttpCompletionOption.ResponseHeadersRead))
        {
            await using var stream = await response.Content.ReadAsStreamAsync();
            Check((await ReadPartAsync(stream)).SequenceEqual(recovered), "A normal stop without CaptureFailed retains the intentional frozen frame");
        }
    }

    private static async Task ExpectStreamEndAsync(Stream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var remaining = new byte[256];
        while (await stream.ReadAsync(remaining, timeout.Token) > 0) { }
    }

    private static async Task CheckCanvasFailureRecoveryAsync(CaptureProducer capture, CompositeFrameService compositor)
    {
        var source = new SourceItem { CanvasWidth = 100, CanvasHeight = 60 };
        StreamingCanvasSnapshot.Sources = [StreamingSourceSnapshot.FromSource(source, 0)];
        var enteredHeader = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHeader = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveredFrameSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var messages = new List<string>();
        var headers = 0;
        async Task Send(string message, CancellationToken token)
        {
            messages.Add(message);
            if (message.StartsWith("header:") && ++headers == 1)
            {
                enteredHeader.TrySetResult();
                await releaseHeader.Task.WaitAsync(token);
            }
            if (message.StartsWith("end:")) recoveredFrameSent.TrySetResult();
        }
        using var service = new MjpegStreamingService(capture, compositor, publishCanvasEvents: true, canvasEventSender: Send);
        await service.StartStreamingAsync(FreePort());
        var recovered = Jpeg(Color.Blue);
        try
        {
            capture.Publish(source, Jpeg(Color.Red));
            await enteredHeader.Task.WaitAsync(TimeSpan.FromSeconds(3));
            capture.Fail(source, "Failure while SignalRGB is accepting a header");
            // Recover before the old header finishes to exercise the failed transaction race.
            capture.Publish(source, recovered);
            releaseHeader.TrySetResult();
            await recoveredFrameSent.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { await service.StopStreamingAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        var removal = messages.IndexOf($"remove:{source.Id}");
        Check(removal >= 0, "Final capture failure schedules a SignalRGB source removal");
        Check(!messages.Take(removal).Any(message => message.StartsWith("end:") || message.StartsWith("data:")),
            "A failed in-flight transaction is discarded even if capture recovers immediately");
        Check(messages.Count(message => message.StartsWith("end:")) == 1,
            "Only the recovered frame is committed to SignalRGB");
        var payload = string.Concat(messages.Skip(removal + 1).Where(message => message.StartsWith("data:")).Select(message => message.Split(':', 4)[3]));
        Check(Convert.FromBase64String(payload).SequenceEqual(recovered),
            "The remove is followed by an intact recovered JPEG, never mixed chunks from the failed frame");
    }

    private static async Task<byte[]> ReadPartAsync(Stream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        async Task<string> Line()
        {
            var bytes = new List<byte>(); var single = new byte[1];
            while (true)
            {
                await stream.ReadExactlyAsync(single, timeout.Token);
                if (single[0] == '\n') return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
                bytes.Add(single[0]);
            }
        }
        string boundary; do { boundary = await Line(); } while (boundary.Length == 0);
        Check(boundary == "--mjpegboundary", "Each JPEG part starts with a valid delimiter");
        var length = 0; string header;
        while ((header = await Line()).Length != 0)
            if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(header[15..].Trim());
        Check(length > 0, "JPEG parts declare a content length");
        var frame = new byte[length]; await stream.ReadExactlyAsync(frame, timeout.Token); return frame;
    }
}
