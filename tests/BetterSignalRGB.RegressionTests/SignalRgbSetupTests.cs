using System.Text.Json;
using Better_SignalRGB_Screen_Capture.Services;

namespace BetterSignalRGB.RegressionTests;

internal static class SignalRgbSetupTests
{
    public static async Task RunAsync()
    {
        var clock = new Clock();
        var connection = new SignalRgbConnectionService(clock);
        connection.ApiSucceeded();
        Assert.True(connection.Snapshot.ApiReachable && !connection.Snapshot.FramesConfirmed,
            "An API acknowledgement alone never confirms effect rendering");
        var health = connection.BeginSession(9876);
        using var document = JsonDocument.Parse(health[7..]);
        var session = document.RootElement.GetProperty("session").GetString();
        Assert.Equal("http://localhost:9876/api/effect-status", document.RootElement.GetProperty("callback").GetString(), "Feedback uses the configured HTTP port");
        Assert.True(!connection.ReceiveFeedback("wrong-session", 5, 1), "A stale or unrelated feedback session is rejected");
        Assert.True(!connection.ReceiveFeedback(session, 5, 2), "Unknown feedback versions are rejected");
        Assert.True(!connection.ReceiveFeedback(session, -1, 1), "Negative counters are rejected");
        Assert.True(connection.ReceiveFeedback(session, 0, 1) && connection.Snapshot.EffectResponding && !connection.Snapshot.FramesConfirmed,
            "An active effect with no captured frame has a distinct waiting state");
        clock.Advance(1);
        connection.FrameSent();
        connection.ReceiveFeedback(session, 12, 1);
        Assert.True(connection.Snapshot.FramesConfirmed, "Effect feedback after drawing confirms image receipt");
        Assert.Near(12, connection.Snapshot.RenderFramesPerSecond, .001, "Effect rate is measured from acknowledged draw counts");
        clock.Advance(1);
        connection.ReceiveFeedback(session, 0, 1);
        Assert.Near(0, connection.Snapshot.RenderFramesPerSecond, .001, "Effect reload resets counters without a negative rate");
        clock.Advance(7);
        Assert.True(!connection.Snapshot.EffectResponding && connection.Snapshot.RenderFramesPerSecond == 0, "Expired feedback no longer claims a live renderer");
        connection.ApiFailed("unavailable");
        Assert.True(!connection.Snapshot.ApiReachable && connection.Snapshot.LastError == "unavailable", "Transport failures remain visible");
        connection.EndSession();
        Assert.True(!connection.ReceiveFeedback(session, 99, 1) && !connection.Snapshot.IsStreaming, "Stopped sessions reject late feedback");

        var root = Path.Combine(Path.GetTempPath(), "BetterSignalRGB-SetupTests", Guid.NewGuid().ToString("N"));
        var bundleFolder = Path.Combine(root, "app");
        var effectFolder = Path.Combine(root, "effects");
        Directory.CreateDirectory(bundleFolder);
        Directory.CreateDirectory(effectFolder);
        try
        {
            var bundle = Path.Combine(bundleFolder, SignalRgbEffectInstaller.FileName);
            var target = Path.Combine(effectFolder, SignalRgbEffectInstaller.FileName);
            await File.WriteAllTextAsync(bundle, "<html>matching effect</html>");
            await File.WriteAllTextAsync(target, "previous custom effect");
            var installer = new SignalRgbEffectInstaller(bundle);
            var before = await installer.InspectAsync(effectFolder);
            Assert.True(before.BundleAvailable && before.Installed && !before.Matches, "Different installed effect is detected by content");
            var backup = await installer.InstallAsync(effectFolder);
            Assert.Equal("previous custom effect", await File.ReadAllTextAsync(backup!), "Updating preserves the previous effect in a non-HTML backup");
            Assert.True((await installer.InspectAsync(effectFolder)).Matches, "Installed effect exactly matches the app's bundled revision");
            Assert.Equal<string?>(null, await installer.InstallAsync(effectFolder), "Already current installs are a no-op");
            Assert.Equal(2, Directory.GetFiles(effectFolder).Length, "No duplicate backups or temporary files from an unchanged install");
            var missing = new SignalRgbEffectInstaller(Path.Combine(root, "missing.html"));
            try { await missing.InstallAsync(effectFolder); Assert.True(false, "Missing bundle must fail"); }
            catch (FileNotFoundException) { Assert.True(true, "Missing bundle is reported"); }
            Assert.True((await installer.InspectAsync(effectFolder)).Matches, "Failed install preserves the existing effect");
            var engine = Path.Combine(root, "VortxEngine");
            var old = Path.Combine(engine, "app-2.9.1", "Signal-x64", "Effects", "Dynamic");
            var current = Path.Combine(engine, "app-2.10.0", "Signal-x64", "Effects", "Dynamic");
            Directory.CreateDirectory(old); Directory.CreateDirectory(current);
            Assert.Equal(current, SignalRgbEffectInstaller.DetectEffectsFolder(engine, effectFolder), "Effect folder detection orders versions numerically");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(double seconds) => _now += TimeSpan.FromSeconds(seconds);
    }
}
