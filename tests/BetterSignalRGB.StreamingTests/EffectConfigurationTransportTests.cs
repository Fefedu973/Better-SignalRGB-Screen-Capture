using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.Services;

namespace BetterSignalRGB.StreamingTests;

internal static class EffectConfigurationTransportTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var capture = new CaptureProducer();
        using var compositor = new CompositeFrameService(capture);
        var settings = new Settings();
        var connection = new SignalRgbConnectionService();
        var messages = Channel.CreateUnbounded<string>();
        StreamingCanvasSnapshot.Sources = [];
        Task Send(string message, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            messages.Writer.TryWrite(message);
            return Task.CompletedTask;
        }
        using var service = new MjpegStreamingService(capture, compositor, true, Send, settings, connection);
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        await service.StartStreamingAsync(port);
        try
        {
            var first = await ReadConfigAsync(messages.Reader);
            check(first.GetProperty("version").GetInt32() == 1, "Sender supplies a versioned effect configuration");
            check(!first.GetProperty("enabled").GetBoolean(), "App appearance control is opt-in");
            check(settings.Initializations == 1, "Starting a sender initializes persisted effect settings");
            using var healthDocument = JsonDocument.Parse((await messages.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)))[7..]);
            var endpoint = healthDocument.RootElement.GetProperty("callback").GetString();
            var session = healthDocument.RootElement.GetProperty("session").GetString();
            using var client = new HttpClient();
            using var invalid = await client.GetAsync($"{endpoint}?session=wrong&version=1&frames=9");
            check(invalid.StatusCode == HttpStatusCode.BadRequest && !connection.Snapshot.FramesConfirmed, "Real feedback endpoint rejects unrelated sessions");
            using var accepted = await client.GetAsync($"{endpoint}?session={session}&version=1&frames=0");
            check(accepted.StatusCode == HttpStatusCode.NoContent && connection.Snapshot.EffectResponding && !connection.Snapshot.FramesConfirmed,
                "The loopback handshake distinguishes an active effect from a drawn frame");

            await settings.UpdateAsync(new SignalRgbEffectSettings
            {
                Enabled = true, Brightness = 23, Hue = -42, AmbilightIntensity = 175,
                Interpolation = "pixelated", FrameRate = 30
            });
            var updated = await ReadConfigAsync(messages.Reader);
            check(updated.GetProperty("enabled").GetBoolean() && updated.GetProperty("brightness").GetInt32() == 23,
                "A live edit reaches the effect without restarting capture");
            check(updated.GetProperty("hue").GetInt32() == -42 && updated.GetProperty("ambilightIntensity").GetInt32() == 175 &&
                updated.GetProperty("interpolation").GetString() == "pixelated" && updated.GetProperty("frameRate").GetInt32() == 30,
                "All appearance and cadence fields travel together as one snapshot");

            var replay = await ReadConfigAsync(messages.Reader);
            check(replay.GetRawText() == updated.GetRawText(), "An idle sender periodically restores controls for a reloaded effect");
            await settings.UpdateAsync(settings.Current with { Enabled = false });
            var released = await ReadConfigAsync(messages.Reader);
            check(!released.GetProperty("enabled").GetBoolean(), "Disabling app control sends an explicit release to SignalRGB");

            var source = new SourceItem { CanvasX = 21, CanvasY = 32, CanvasWidth = 90, CanvasHeight = 60, Rotation = 27 };
            StreamingCanvasSnapshot.Sources = [StreamingSourceSnapshot.FromSource(source, 0)];
            capture.Publish(source, [1, 2, 3, 4]);
            var original = await ReadFrameAsync(messages.Reader, source.Id);
            var replayStarted = System.Diagnostics.Stopwatch.StartNew();
            var replayed = await ReadFrameAsync(messages.Reader, source.Id);
            check(original.SequenceEqual(replayed), "Reloading the effect during pause recovers the same complete frame and layout");
            check(replayStarted.Elapsed >= TimeSpan.FromSeconds(1), "Paused image recovery is infrequent rather than repeated at the capture rate");
            using var rendered = await client.GetAsync($"{endpoint}?session={session}&version=1&frames=1");
            check(rendered.StatusCode == HttpStatusCode.NoContent && connection.Snapshot.FramesConfirmed && connection.Snapshot.FramesSent >= 2,
                "Effect feedback confirms rendering separately from successful source transmission");
        }
        finally { await service.StopStreamingAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    private static async Task<List<string>> ReadFrameAsync(ChannelReader<string> messages, Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var frame = new List<string>();
        while (true)
        {
            var message = await messages.ReadAsync(timeout.Token);
            if (message.StartsWith($"header:{id}") || message.StartsWith($"data:{id}:") || message == $"end:{id}")
                frame.Add(message);
            if (message == $"end:{id}") return frame;
        }
    }

    private static async Task<JsonElement> ReadConfigAsync(ChannelReader<string> messages)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var message = await messages.ReadAsync(timeout.Token);
            if (!message.StartsWith("config:")) continue;
            using var document = JsonDocument.Parse(message[7..]);
            return document.RootElement.Clone();
        }
    }

    private sealed class Settings : ISignalRgbEffectSettingsService
    {
        public SignalRgbEffectSettings Current { get; private set; } = new();
        public int Initializations;
        public event EventHandler<SignalRgbEffectSettings>? Changed;
        public Task ScheduleUpdateAsync(SignalRgbEffectSettings settings) => UpdateAsync(settings);
        public Task FlushAsync() => Task.CompletedTask;
        public Task InitializeAsync() { Initializations++; return Task.CompletedTask; }
        public Task UpdateAsync(SignalRgbEffectSettings settings)
        {
            Current = settings.Normalize();
            Changed?.Invoke(this, Current);
            return Task.CompletedTask;
        }
    }
}
