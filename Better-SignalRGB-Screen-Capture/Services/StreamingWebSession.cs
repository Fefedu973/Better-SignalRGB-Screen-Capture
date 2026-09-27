using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Models;

namespace Better_SignalRGB_Screen_Capture.Services;

internal sealed record StreamingWebCanvasSnapshot(StreamingSourceSnapshot[] Sources, int OutputWidth, int OutputHeight);

/// <summary>One latest-only connection carries the complete layout and original cached JPEGs.</summary>
internal static class StreamingWebSession
{
    public const string ContentType = "multipart/x-mixed-replace; boundary=frame";
    internal const int MaximumSources = 128;
    internal const int MaximumStateBytes = 256 * 1024;
    internal const int MaximumJpegBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly SignalRgbEffectSettings DefaultSettings = new();
    private static readonly byte[] NewLine = "\r\n"u8.ToArray();

    public static async Task WriteAsync(Stream output, StreamingSourceFrames frames, ICaptureService capture,
        ICompositeFrameService composite, ISignalRgbEffectSettingsService? settingsService, CancellationToken token, bool preview = false)
    {
        var sent = new Dictionary<Guid, byte[]>();
        StreamingCompositeSession? rawSession = null;
        byte[]? rawPrevious = null;
        StreamingWebCanvasSnapshot? previousCanvas = null;
        SignalRgbEffectSettings? previousSettings = null;
        long stateVersion = 0, lastImageTimestamp = 0, lastStateTimestamp = 0;
        long nextImageTimestamp = 0;
        var imageFrameRate = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1d / 30));
        try
        {
            do
            {
                var canvas = await StreamingCanvasSnapshot.CaptureWebAsync(token).ConfigureAwait(false);
                canvas = canvas with { Sources = frames.FilterAvailable(canvas.Sources) };
                if (canvas.Sources.Length > MaximumSources) throw new InvalidDataException("Web output supports at most 128 sources.");
                var settings = settingsService?.Current ?? DefaultSettings;
                // A settings preview is private to this connection. It must never enable
                // the public web output or modify settings used by the SignalRGB sender.
                if (preview && !settings.WebEnabled) settings = settings with { WebEnabled = true };
                var modeChanged = previousSettings?.WebEnabled != settings.WebEnabled;
                if (modeChanged)
                {
                    sent.Clear();
                    rawPrevious = null;
                    lastImageTimestamp = 0;
                    nextImageTimestamp = 0;
                    rawSession?.Dispose();
                    rawSession = settings.WebEnabled ? null : new StreamingCompositeSession(composite);
                }
                var stateChanged = previousSettings != settings || previousCanvas == null ||
                    canvas.OutputWidth != previousCanvas.OutputWidth || canvas.OutputHeight != previousCanvas.OutputHeight ||
                    !canvas.Sources.AsSpan().SequenceEqual(previousCanvas.Sources);
                // HttpListener has no request-aborted token. A small unchanged-state
                // heartbeat discovers disconnected clients even while every image is frozen.
                if (stateChanged || Stopwatch.GetElapsedTime(lastStateTimestamp).TotalSeconds >= 5)
                {
                    var state = JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        version = 1, settings,
                        canvasWidth = StreamingCanvasSnapshot.Width, canvasHeight = StreamingCanvasSnapshot.Height,
                        outputWidth = canvas.OutputWidth, outputHeight = canvas.OutputHeight, sources = canvas.Sources
                    }, JsonOptions);
                    if (stateChanged) stateVersion++;
                    await WritePartAsync(output, state, "application/json", stateVersion, null, token).ConfigureAwait(false);
                    lastStateTimestamp = Stopwatch.GetTimestamp();
                    previousCanvas = canvas;
                    previousSettings = settings;
                    var activeIds = canvas.Sources.Select(source => source.Id).ToHashSet();
                    foreach (var id in sent.Keys.Where(id => !activeIds.Contains(id)).ToArray()) sent.Remove(id);
                }
                if (settings.WebEnabled)
                {
                    var frameRate = Math.Clamp(settings.FrameRate, 1, 30);
                    var interval = (long)Math.Ceiling(Stopwatch.Frequency / (double)frameRate);
                    if (imageFrameRate != frameRate)
                    {
                        imageFrameRate = frameRate;
                        nextImageTimestamp = lastImageTimestamp == 0 ? 0 : lastImageTimestamp + interval;
                    }
                    var now = Stopwatch.GetTimestamp();
                    // Accumulate deadlines instead of measuring from each slightly late
                    // timer tick. Permit 2ms timer jitter; skip missed periods, never queue.
                    if (nextImageTimestamp > now + Stopwatch.Frequency / 500) continue;
                    lastImageTimestamp = now;
                    nextImageTimestamp = nextImageTimestamp == 0 ? now + interval :
                        nextImageTimestamp + Math.Max(1, (now - nextImageTimestamp) / interval + 1) * interval;
                    foreach (var source in canvas.Sources)
                    {
                        // Seed the shared immutable cache when capture predates the server.
                        // A later pause can clear the producer cache; new clients must still
                        // see the intentionally frozen image, as with /stream/{sourceId}.
                        var frame = frames.Open(source.Id, capture.GetMjpegFrame)?.Latest;
                        if (frame == null || frame.Length == 0) continue;
                        if (sent.TryGetValue(source.Id, out var previous) && SameFrame(previous, frame)) continue;
                        // A failure/removal can race an asynchronous write of a previous source.
                        if (frames.IsUnavailable(source.Id)) continue;
                        await WritePartAsync(output, frame, "image/jpeg", stateVersion, source.Id, token).ConfigureAwait(false);
                        sent[source.Id] = frame;
                    }
                }
                else if (rawSession?.Frames.Latest is { Length: > 0 } raw && !SameFrame(rawPrevious, raw))
                {
                    await WritePartAsync(output, raw, "image/jpeg", stateVersion, null, token).ConfigureAwait(false);
                    rawPrevious = raw;
                }
            } while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        finally { rawSession?.Dispose(); }
    }

    private static bool SameFrame(byte[]? previous, byte[] frame) => ReferenceEquals(previous, frame) ||
        (previous != null && previous.AsSpan().SequenceEqual(frame));

    internal static async Task WritePartAsync(Stream output, byte[] payload, string contentType, long stateVersion,
        Guid? sourceId, CancellationToken token)
    {
        var limit = contentType == "application/json" ? MaximumStateBytes : MaximumJpegBytes;
        if (payload.Length is < 1 || payload.Length > limit) throw new InvalidDataException("Web stream part exceeds its protocol limit.");
        var sourceHeader = sourceId.HasValue ? $"X-Source-Id: {sourceId.Value:D}\r\n" : string.Empty;
        var header = Encoding.ASCII.GetBytes($"--frame\r\nContent-Type: {contentType}\r\nContent-Length: {payload.Length}\r\nX-State-Version: {stateVersion}\r\n{sourceHeader}\r\n");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        async Task Write()
        {
            await output.WriteAsync(header, timeout.Token).ConfigureAwait(false);
            await output.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
            await output.WriteAsync(NewLine, timeout.Token).ConfigureAwait(false);
            await output.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        await Write().WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
    }
}
