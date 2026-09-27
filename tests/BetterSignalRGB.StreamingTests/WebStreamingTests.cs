using System.Diagnostics;
using System.Drawing;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.Services;

namespace BetterSignalRGB.StreamingTests;

internal static partial class Program
{
    private static async Task CheckWebStreamingAsync()
    {
        var certificatePath = Path.Combine(AppContext.BaseDirectory, "localhost.pfx");
        using (var rsa = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            await File.WriteAllBytesAsync(certificatePath, certificate.Export(X509ContentType.Pfx));
        }
        try
        {
            foreach (var https in new[] { false, true }) await CheckWebEndpointAsync(https);
            foreach (var (mime, length) in new[] { ("application/json", StreamingWebSession.MaximumStateBytes + 1), ("image/jpeg", StreamingWebSession.MaximumJpegBytes + 1) })
            {
                using var output = new MemoryStream();
                var rejected = false;
                try { await StreamingWebSession.WritePartAsync(output, new byte[length], mime, 1, null, CancellationToken.None); }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected && output.Length == 0, $"Oversized {mime} is rejected before any multipart bytes are written");
            }
            var capture = new CaptureProducer();
            var frames = new StreamingSourceFrames();
            var settings = new WebSettings();
            var composite = new WebComposite();
            StreamingCanvasSnapshot.Sources = Enumerable.Range(0, 129).Select(i => StreamingSourceSnapshot.FromSource(new SourceItem(), i)).ToArray();
            using var sink = new MemoryStream();
            var tooMany = false;
            try { await StreamingWebSession.WriteAsync(sink, frames, capture, composite, settings, CancellationToken.None); }
            catch (InvalidDataException) { tooMany = true; }
            Check(tooMany && sink.Length == 0, "A web state exceeding 128 sources is rejected before transmission");
        }
        finally
        {
            File.Delete(certificatePath);
            StreamingCanvasSnapshot.Sources = [];
            StreamingCanvasSnapshot.IsHighQuality = false;
        }
    }

    private static async Task CheckWebEndpointAsync(bool https)
    {
        var name = https ? "HTTPS" : "HTTP";
        var capture = new CaptureProducer();
        var composite = new WebComposite();
        var settings = new WebSettings();
        using var http = new MjpegStreamingService(capture, composite, false, effectSettings: settings);
        using var secure = new KestrelApiService(capture, composite, settings);
        var port = FreePort();
        Task Start() => https ? secure.StartAsync(port) : http.StartStreamingAsync(port);
        Task Stop() => https ? secure.StopAsync() : http.StopStreamingAsync();
        Task Remove(Guid id) { if (https) { secure.RemoveSource(id); return Task.CompletedTask; } return http.NotifySourceRemovedAsync(id); }
        var first = new SourceItem { CanvasWidth = 100, CanvasHeight = 60, Rotation = 37, CropLeftPct = .2 };
        var second = new SourceItem { CanvasX = 71, CanvasWidth = 120, CanvasHeight = 90, IsMirroredVertically = true };
        void SetSources(params SourceItem[] sources) => StreamingCanvasSnapshot.Sources = sources.Select((source, index) => StreamingSourceSnapshot.FromSource(source, index)).ToArray();
        SetSources(first, second);
        StreamingCanvasSnapshot.IsHighQuality = true;
        var detailed = SizedJpeg(960, 600, Color.Red);
        var other = SizedJpeg(360, 270, Color.Blue);
        var low = SizedJpeg(320, 200, Color.Green);
        capture.Publish(first, detailed, low);
        capture.Publish(second, other, low);
        await Start();
        Check(settings.Initializations == 1, $"{name} independently initializes persisted web settings");
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var client = new HttpClient(handler) { BaseAddress = new Uri($"{(https ? "https" : "http")}://localhost:{port}") };
        try
        {
            using var response = await client.GetAsync("/web-stream", HttpCompletionOption.ResponseHeadersRead);
            Check(response.Content.Headers.ContentType?.ToString() == StreamingWebSession.ContentType, $"{name} multiplexes all sources over boundary=frame");
            await using var stream = await response.Content.ReadAsStreamAsync();
            var initial = await ReadWebPartAsync(stream);
            var state = initial.Json;
            Check(initial.Type == "application/json" && initial.StateVersion == 1 && state.GetProperty("version").GetInt32() == 1, $"{name} sends a complete versioned state before images");
            Check(state.GetProperty("outputWidth").GetInt32() == 800 && state.GetProperty("outputHeight").GetInt32() == 600 &&
                state.GetProperty("canvasWidth").GetInt32() == 320 && state.GetProperty("canvasHeight").GetInt32() == 200,
                $"{name} HQ output preserves canonical canvas coordinates");
            Check(state.GetProperty("settings").GetProperty("webEnabled").GetBoolean() && !state.GetProperty("settings").GetProperty("enabled").GetBoolean(),
                $"{name} web appearance is independent of SignalRGB app control");
            Check(state.GetProperty("sources").GetArrayLength() == 2 && state.GetProperty("sources")[0].GetProperty("cropLeftPct").GetDouble() == .2,
                $"{name} snapshots retain source transform metadata");
            var image1 = await ReadWebPartAsync(stream); var image2 = await ReadWebPartAsync(stream);
            Check(image1.SourceId == first.Id && image1.Bytes.SequenceEqual(detailed) && image2.SourceId == second.Id && image2.Bytes.SequenceEqual(other),
                $"{name} multiplexes exact HQ JPEGs without re-encoding or using the SignalRGB derivative");
            Check(image1.StateVersion == initial.StateVersion && image2.StateVersion == initial.StateVersion && composite.Subscribers == 0,
                $"{name} image epochs match their layout and web effect mode does not start the server compositor");
            await capture.StopCaptureAsync(first);
            using (var pausedResponse = await client.GetAsync("/web-stream", HttpCompletionOption.ResponseHeadersRead))
            {
                await using var pausedStream = await pausedResponse.Content.ReadAsStreamAsync();
                await ReadWebPartAsync(pausedStream);
                var paused = await ReadWebPartAsync(pausedStream);
                Check(paused.SourceId == first.Id && paused.Bytes.SequenceEqual(detailed),
                    $"{name} newly connected clients retain paused pixels captured before the server started");
            }
            var changed = await CheckNoDuplicateWebFrameAsync(stream, name, () => capture.Publish(first, detailed.ToArray(), low),
                () => settings.UpdateAsync(settings.Current with { Hue = 41, FrameRate = 1 }));
            Check(changed.Type == "application/json" && changed.StateVersion > initial.StateVersion && changed.Json.GetProperty("settings").GetProperty("hue").GetInt32() == 41,
                $"{name} applies live appearance changes without reconnecting or waiting for a low image frame rate");
            var last = SizedJpeg(960, 600, Color.Purple);
            var started = Stopwatch.StartNew();
            for (var i = 0; i < 20; i++) capture.Publish(first, i == 19 ? last : detailed, low);
            var latest = await ReadWebPartAsync(stream);
            Check(latest.SourceId == first.Id && latest.Bytes.SequenceEqual(last), $"{name} a rapid producer burst delivers only the latest cached image");
            Check(started.ElapsedMilliseconds >= 500, $"{name} bounds source image cadence using web frameRate");
            first.CanvasX = -19;
            SetSources(first, second);
            var layoutWatch = Stopwatch.StartNew();
            var layout = await ReadWebPartAsync(stream);
            Check(layout.Type == "application/json" && layout.Json.GetProperty("sources")[0].GetProperty("canvasX").GetInt32() == -19,
                $"{name} layout updates remain responsive at one image per second");
            Check(layoutWatch.ElapsedMilliseconds < 500, $"{name} metadata cadence remains independent of image cadence");
            capture.Fail(first, "Synthetic web stream failure");
            var failed = await ReadWebPartAsync(stream);
            Check(failed.Type == "application/json" && failed.Json.GetProperty("sources").GetArrayLength() == 1 && failed.Json.GetProperty("sources")[0].GetProperty("id").GetGuid() == second.Id,
                $"{name} a final capture failure removes stale browser pixels through the complete state");
            capture.Publish(first, last, low);
            var recovered = await ReadWebPartAsync(stream);
            Check(recovered.Type == "application/json" && recovered.Json.GetProperty("sources").GetArrayLength() == 2,
                $"{name} a fresh producer frame restores failed source availability");
            var recoveredImage = await ReadWebPartAsync(stream);
            Check(recoveredImage.SourceId == first.Id && recoveredImage.StateVersion == recovered.StateVersion, $"{name} recovered pixels follow their restored state");
            await Remove(second.Id);
            var removed = await ReadWebPartAsync(stream);
            Check(removed.Type == "application/json" && removed.Json.GetProperty("sources").GetArrayLength() == 1, $"{name} removal updates an existing connection without individual source streams");
            StreamingCanvasSnapshot.IsHighQuality = false;
            var standard = await ReadWebPartAsync(stream);
            Check(standard.Json.GetProperty("outputWidth").GetInt32() == 320 && standard.Json.GetProperty("outputHeight").GetInt32() == 200,
                $"{name} output quality changes are sent while the connection remains open");
            composite.Publish(low);
            await settings.UpdateAsync(settings.Current with { WebEnabled = false });
            var rawState = await ReadWebPartAsync(stream);
            var raw = await ReadWebPartAsync(stream);
            Check(!rawState.Json.GetProperty("settings").GetProperty("webEnabled").GetBoolean() && raw.SourceId == null && raw.Bytes.SequenceEqual(low),
                $"{name} raw mode delivers the original composite JPEG without a source header");
            Check(composite.Subscribers == 1, $"{name} raw mode has one bounded composite subscription");
            var rawNew = SizedJpeg(320, 200, Color.Orange);
            var rawWatch = Stopwatch.StartNew();
            composite.Publish(rawNew);
            Check((await ReadWebPartAsync(stream)).Bytes.SequenceEqual(rawNew) && rawWatch.ElapsedMilliseconds < 500,
                $"{name} raw composite cadence is independent of the effect frame-rate setting");
            await CheckPreviewIsolationAsync(client, settings, composite, first.Id, last, rawNew, name);
            await settings.UpdateAsync(settings.Current with { WebEnabled = true, FrameRate = 30 });
            WebPart effectAgain;
            do { effectAgain = await ReadWebPartAsync(stream); }
            while (!effectAgain.Json.GetProperty("settings").GetProperty("webEnabled").GetBoolean());
            Check(effectAgain.Json.GetProperty("settings").GetProperty("webEnabled").GetBoolean() && composite.Subscribers == 0,
                $"{name} returning to effect mode releases the raw compositor subscription (remaining {composite.Subscribers})");
            await ReadWebPartAsync(stream);
            await CheckWebCadenceAsync(stream, capture, first, detailed, low, name);
            await Stop().WaitAsync(TimeSpan.FromSeconds(5));
            try { await ExpectStreamEndAsync(stream); }
            catch (IOException) { /* Closing an HTTP listener may terminate its TCP connection with RST. */ }
            Check(composite.Subscribers == 0, $"{name} stop terminates multiplexed clients and removes composite subscriptions");
            await settings.UpdateAsync(settings.Current with { WebEnabled = false });
            await Start();
            using var restarted = await client.GetAsync("/web-stream", HttpCompletionOption.ResponseHeadersRead);
            await using var restartedStream = await restarted.Content.ReadAsStreamAsync();
            Check((await ReadWebPartAsync(restartedStream)).StateVersion == 1, $"{name} restart begins an independent complete-state session");
            await ReadWebPartAsync(restartedStream);
            await restartedStream.DisposeAsync();
            restarted.Dispose();
            var disconnectWatch = Stopwatch.StartNew();
            while (composite.Subscribers != 0 && disconnectWatch.Elapsed.TotalSeconds < 12) await Task.Delay(100);
            Check(composite.Subscribers == 0, $"{name} an idle disconnected client releases its composite subscription without a new producer frame");
        }
        finally { await Stop().WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private static async Task CheckPreviewIsolationAsync(HttpClient client, WebSettings settings, WebComposite composite, Guid sourceId,
        byte[] sourceJpeg, byte[] rawComposite, string name)
    {
        var savedSettings = settings.Current;
        using var previewResponse = await client.GetAsync("/web-stream?preview=1", HttpCompletionOption.ResponseHeadersRead);
        await using var previewStream = await previewResponse.Content.ReadAsStreamAsync();
        var previewState = await ReadWebPartAsync(previewStream);
        var previewImage = await ReadWebPartAsync(previewStream);
        Check(previewState.Json.GetProperty("settings").GetProperty("webEnabled").GetBoolean() &&
            previewImage.SourceId == sourceId && previewImage.Bytes.SequenceEqual(sourceJpeg),
            $"{name} preview=1 receives source JPEGs even when public web output remains raw");
        Check(!settings.Current.WebEnabled && ReferenceEquals(settings.Current, savedSettings),
            $"{name} a preview connection leaves the immutable stored settings untouched");
        using (var publicResponse = await client.GetAsync("/web-stream", HttpCompletionOption.ResponseHeadersRead))
        {
            await using var publicStream = await publicResponse.Content.ReadAsStreamAsync();
            var publicState = await ReadWebPartAsync(publicStream);
            var publicFrame = await ReadWebPartAsync(publicStream);
            Check(!publicState.Json.GetProperty("settings").GetProperty("webEnabled").GetBoolean() &&
                publicFrame.SourceId == null && publicFrame.Bytes.SequenceEqual(rawComposite),
                $"{name} public web clients remain raw while a private preview is connected");
        }
        using (var rawResponse = await client.GetAsync("/stream", HttpCompletionOption.ResponseHeadersRead))
        {
            await using var rawStream = await rawResponse.Content.ReadAsStreamAsync();
            Check((await ReadPartAsync(rawStream)).SequenceEqual(rawComposite),
                $"{name} /stream retains its exact raw JPEG during a private preview");
        }
        await settings.UpdateAsync(savedSettings with { Brightness = 22 });
        var updated = await ReadWebPartAsync(previewStream);
        Check(updated.Json.GetProperty("settings").GetProperty("webEnabled").GetBoolean() &&
            updated.Json.GetProperty("settings").GetProperty("brightness").GetInt32() == 22 && !settings.Current.WebEnabled,
            $"{name} private previews follow live appearance edits without enabling public processing");
        // Wake the unchanged legacy /stream endpoint after its test reader disconnected.
        // The web output deduplicates this byte-identical publication.
        composite.Publish(rawComposite.ToArray());
        var cleanup = Stopwatch.StartNew();
        while (composite.Subscribers > 1 && cleanup.Elapsed.TotalSeconds < 3)
        {
            composite.Publish(rawComposite.ToArray());
            await Task.Delay(50);
        }
    }

    private static async Task<WebPart> CheckNoDuplicateWebFrameAsync(Stream stream, string name, Action publishClone, Func<Task> editSettings)
    {
        var pending = ReadWebPartAsync(stream);
        try
        {
            await Task.Delay(160);
            publishClone();
            await Task.Delay(160);
            Check(!pending.IsCompleted, $"{name} suppresses both repeated buffers and identical JPEG bytes");
            await editSettings();
            return await pending;
        }
        finally { if (!pending.IsCompleted) await pending; }
    }

    private static async Task CheckWebCadenceAsync(Stream stream, CaptureProducer capture, SourceItem source, byte[] jpeg, byte[] signal, string name)
    {
        using var cancellation = new CancellationTokenSource();
        var producing = Task.Run(async () =>
        {
            var serial = 0;
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    // JPEG permits trailing application data; unique bytes avoid deduplication.
                    var frame = new byte[jpeg.Length + 4];
                    jpeg.CopyTo(frame, 0);
                    BitConverter.TryWriteBytes(frame.AsSpan(jpeg.Length), ++serial);
                    capture.Publish(source, frame, signal);
                    await Task.Delay(8, cancellation.Token);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        });
        var watch = Stopwatch.StartNew();
        var count = 0;
        try
        {
            while (watch.Elapsed.TotalSeconds < 1.2)
                if ((await ReadWebPartAsync(stream)).SourceId == source.Id) count++;
            var actualRate = count / watch.Elapsed.TotalSeconds;
            Check(actualRate is >= 23 and <= 35, $"{name} 30fps request delivers {actualRate:F1}fps rather than accidentally halving cadence");
        }
        finally { cancellation.Cancel(); await producing; }
    }

    private sealed record WebPart(string Type, long StateVersion, Guid? SourceId, byte[] Bytes)
    {
        public JsonElement Json { get { using var document = JsonDocument.Parse(Bytes); return document.RootElement.Clone(); } }
    }

    private static async Task<WebPart> ReadWebPartAsync(Stream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 4096)
        {
            await stream.ReadExactlyAsync(one, timeout.Token);
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10) break;
        }
        var header = Encoding.ASCII.GetString(bytes.ToArray());
        if (!header.StartsWith("--frame\r\n", StringComparison.Ordinal)) throw new InvalidDataException("Unexpected web multipart boundary.");
        var fields = header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(line => line.Split(':', 2)).ToDictionary(pair => pair[0], pair => pair[1].Trim(), StringComparer.OrdinalIgnoreCase);
        var data = new byte[int.Parse(fields["Content-Length"])];
        await stream.ReadExactlyAsync(data, timeout.Token);
        var suffix = new byte[2];
        await stream.ReadExactlyAsync(suffix, timeout.Token);
        if (!suffix.AsSpan().SequenceEqual("\r\n"u8)) throw new InvalidDataException("Missing multipart payload terminator.");
        return new WebPart(fields["Content-Type"], long.Parse(fields["X-State-Version"]),
            fields.TryGetValue("X-Source-Id", out var id) ? Guid.Parse(id) : null, data);
    }

    private sealed class WebSettings : ISignalRgbEffectSettingsService
    {
        public SignalRgbEffectSettings Current { get; private set; } = new() { WebEnabled = true, FrameRate = 30 };
        public int Initializations;
        public event EventHandler<SignalRgbEffectSettings>? Changed;
        public Task InitializeAsync() { Initializations++; return Task.CompletedTask; }
        public Task UpdateAsync(SignalRgbEffectSettings settings) { Current = settings.Normalize(); Changed?.Invoke(this, Current); return Task.CompletedTask; }
        public Task ScheduleUpdateAsync(SignalRgbEffectSettings settings) => UpdateAsync(settings);
        public Task FlushAsync() => Task.CompletedTask;
    }

    private sealed class WebComposite : ICompositeFrameService
    {
        private readonly object _sync = new();
        private EventHandler<byte[]>? _changed;
        private byte[]? _latest;
        public int Subscribers { get { lock (_sync) return _changed?.GetInvocationList().Length ?? 0; } }
        public event EventHandler<byte[]>? CompositeFrameAvailable
        {
            add { lock (_sync) _changed += value; }
            remove { lock (_sync) _changed -= value; }
        }
        public void Publish(byte[] bytes)
        {
            EventHandler<byte[]>? changed;
            lock (_sync) { _latest = bytes; changed = _changed; }
            changed?.Invoke(this, bytes);
        }
        public byte[]? GetLatestCompositeFrame() { lock (_sync) return _latest; }
        public void UpdateSourceFrame(SourceItem source, byte[] frameData) { }
        public void RemoveSource(SourceItem source) { }
        public void SetCanvasSize(int width, int height) { }
        public void InvalidateLayout() { }
    }
}
