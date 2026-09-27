using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Better_SignalRGB_Screen_Capture.Services;
using Better_SignalRGB_Screen_Capture.Services.NativeOutput;

namespace BetterSignalRGB.NativeIntegrationTests;

internal static class Program
{
    private static int _assertions;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static void Check(bool condition, string message)
    { _assertions++; if (!condition) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> condition, string message, int milliseconds = 7000)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.ElapsedMilliseconds < milliseconds) await Task.Delay(20);
        Check(condition(), message);
    }
    private sealed record Surface(byte[] Pixels, uint Width, uint Height, uint Stride, ulong Sequence, ulong Generation, ulong Timestamp);
    private static Surface ReadSurface(string channel)
    {
        var name = "Local\\OpenRGB-Room.Surface." + channel;
        using var mutex = Mutex.OpenExisting(name + ".Mutex");
        Check(mutex.WaitOne(TimeSpan.FromSeconds(1)), "Reader acquires the shared transport mutex");
        try
        {
            using var mapping = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
            using var view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            var header = new byte[128]; view.ReadArray(0, header, 0, header.Length);
            uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(offset));
            ulong U64(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(offset));
            Check(Encoding.ASCII.GetString(header, 0, 8) == "ORGBFRM1" && U32(8) == 1 && U32(12) == 128 && U32(36) == 1, "Actual integration preserves the Reader header ABI");
            Check(U32(24) > 0 && U32(28) > 0 && U32(32) >= U32(24) * 4 && U64(40) == (ulong)U32(32) * U32(28) && U64(40) <= U64(16), "Frame dimensions and payload are bounded");
            Check(header.AsSpan(88, 40).ToArray().All(value => value == 0), "Native metadata does not repurpose reserved ORGBFRM1 fields");
            var pixels = new byte[checked((int)U64(40))]; view.ReadArray(128, pixels, 0, pixels.Length);
            Check(Enumerable.Range(0, pixels.Length / 4).All(i => pixels[i * 4 + 3] == 255), "Every image/coverage transport pixel is opaque");
            return new(pixels, U32(24), U32(28), U32(32), U64(48), U64(64), U64(56));
        }
        finally { mutex.ReleaseMutex(); }
    }
    private static void Pixel(Surface surface, int x, int y, int red, int green, int blue, string label, int tolerance = 4)
    {
        var index = checked((int)(y * surface.Stride + x * 4));
        Check(Math.Abs(surface.Pixels[index] - blue) <= tolerance && Math.Abs(surface.Pixels[index + 1] - green) <= tolerance && Math.Abs(surface.Pixels[index + 2] - red) <= tolerance,
            $"{label}: expected RGB {red},{green},{blue}, got {surface.Pixels[index + 2]},{surface.Pixels[index + 1]},{surface.Pixels[index]}");
    }
    private static ulong Hash(byte[] bytes)
    { var hash = 14695981039346656037UL; foreach (var value in bytes) hash = unchecked((hash ^ value) * 1099511628211UL); return hash; }

    private static async Task JoinedCppReader(string executable, NativeSurfaceReference reference, Surface expected)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(reference.Channel);
        using var reader = Process.Start(start) ?? throw new IOException("Could not start the C++ Reader.");
        try
        {
            var hello = await reader.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(hello?.StartsWith("READY ", StringComparison.Ordinal) == true, "Actual C++ FrameSurface Reader starts");
            await reader.StandardInput.WriteLineAsync("read 2000 5"); await reader.StandardInput.FlushAsync();
            var result = (await reader.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))?.Split(' ') ?? [];
            Check(result.Length == 10 && result[0] == "NewFrame", "Actual C++ Reader consumes the integrated production compositor surface");
            Check(ulong.Parse(result[5], CultureInfo.InvariantCulture) == expected.Generation && ulong.Parse(result[7], CultureInfo.InvariantCulture) == Hash(expected.Pixels), "C++ Reader generation/hash match synchronized C# pixels");
            Check(uint.Parse(result[1], CultureInfo.InvariantCulture) == expected.Width && uint.Parse(result[2], CultureInfo.InvariantCulture) == expected.Height, "C++ Reader sees native output dimensions");
            await reader.StandardInput.WriteLineAsync("quit"); await reader.StandardInput.FlushAsync();
            await reader.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { if (!reader.HasExited) { reader.Kill(entireProcessTree:true); await reader.WaitForExitAsync(); } }
    }

    private sealed class HeldSurfaceMutex : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly Thread _thread;
        private readonly TaskCompletionSource _acquired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HeldSurfaceMutex(string channel)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    using var mutex = Mutex.OpenExisting("Local\\OpenRGB-Room.Surface." + channel + ".Mutex");
                    if (!mutex.WaitOne(1000)) throw new TimeoutException("Synthetic reader cannot acquire transport mutex.");
                    try { _acquired.SetResult(); _release.Wait(TimeSpan.FromSeconds(8)); }
                    finally { mutex.ReleaseMutex(); }
                }
                catch (Exception error) { _acquired.TrySetException(error); }
            }) { IsBackground = true };
            _thread.Start();
            try { _acquired.Task.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            _release.Set();
            if (!_thread.Join(2000)) throw new TimeoutException("Synthetic transport reader did not stop.");
            _release.Dispose();
        }
    }

    private static ulong ReadTimestamp(string channel)
    {
        var name = "Local\\OpenRGB-Room.Surface." + channel;
        using var mutex = Mutex.OpenExisting(name + ".Mutex");
        if (!mutex.WaitOne(1000)) throw new TimeoutException("Timestamp reader could not acquire transport mutex.");
        try
        {
            using var mapping = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
            using var view = mapping.CreateViewAccessor(0, 128, MemoryMappedFileAccess.Read);
            return view.ReadUInt64(56);
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static async Task TransportHealth()
    {
        var capture = new CaptureProducer(); var scenes = new SceneController(capture); var saved = new SavedSettings();
        using var compositor = new CompositeFrameService(capture);
        using var control = new NativeControlService(scenes, saved);
        var snapshots = new SnapshotGate(scenes, control);
        using var output = new NativeOutputService(compositor, control, snapshots.ReadAsync);
        var corrupt = 0;
        // Fault injection before the output subscription: model an invalid upstream BGRA
        // buffer without replacing the real compositor, publisher or transport mutex.
        compositor.RawFrameAvailable += (_, frame) =>
        {
            if (Volatile.Read(ref corrupt) != 0 && MemoryMarshal.TryGetArray(frame.Pixels, out var bytes))
                bytes.Array![bytes.Offset + 3] = 0;
        };
        try
        {
            output.Start("TransportTest_" + Guid.NewGuid().ToString("N"));
            scenes.PublishImages();
            await Until(() => output.Status.Status == "ready" && output.Status.EffectiveState?.Rendering.Sources.Count(source => source.HasFrame) == 2,
                "Transport health fixture starts with actual composed sources");
            var state = output.Status.EffectiveState!;
            var raw = ReadSurface(state.Image.Channel);
            using (var held = new HeldSurfaceMutex(state.Image.Channel))
            {
                await Task.Delay(500);
                Check(output.Status.Status == "ready" && output.Status.Error == null, "Brief static reader contention is tolerated");
            }
            await Until(() => ReadTimestamp(state.Image.Channel) > raw.Timestamp, "Static raw heartbeat resumes after brief contention");

            var beforeCoverageBlock = ReadSurface(state.Coverage.Channel);
            ulong sequenceBeforeRelease;
            var publishedBeforeBlock = output.Status.PublishedFrames;
            using (var held = new HeldSurfaceMutex(state.Coverage.Channel))
            {
                await Until(() => output.Status.Status == "transport_degraded", "Coverage contention beyond two seconds becomes visible", 4500);
                Check(output.Status.Error?.StartsWith("Coverage heartbeat: Surface mutex is busy", StringComparison.Ordinal) == true,
                    "Coverage degradation includes the immediate publisher error");
                var whileBlocked = ReadSurface(state.Image.Channel);
                Check(whileBlocked.Timestamp > raw.Timestamp && whileBlocked.Sequence == raw.Sequence,
                    "Successful static raw heartbeat cannot conceal blocked coverage");
                capture.Publish(scenes.Red, Color.Magenta);
                await Until(() => output.Status.PublishedFrames > publishedBeforeBlock, "Raw publication can progress while coverage is blocked");
                Check(output.Status.Status == "transport_degraded" && output.Status.Error?.StartsWith("Coverage heartbeat:", StringComparison.Ordinal) == true,
                    "Successful raw publication does not heal an unrelated heartbeat failure");
                sequenceBeforeRelease = ReadSurface(state.Image.Channel).Sequence;
                Check(output.Status.EffectiveState!.Image.Generation == state.Image.Generation && output.Status.EffectiveState.Coverage.Generation == state.Coverage.Generation,
                    "Heartbeat contention does not churn publisher generations");
            }
            await Until(() => output.Status.Status == "ready" && output.Status.Error == null, "Static pair returns to ready after coverage mutex is released", 2200);
            var renewedCoverage = ReadSurface(state.Coverage.Channel);
            Check(renewedCoverage.Timestamp > beforeCoverageBlock.Timestamp && renewedCoverage.Sequence == beforeCoverageBlock.Sequence &&
                ReadSurface(state.Image.Channel).Sequence == sequenceBeforeRelease, "Static heartbeat recovery preserves both sequences");

            var dropped = output.Status.DroppedFrames;
            var published = output.Status.PublishedFrames;
            using (var held = new HeldSurfaceMutex(state.Image.Channel))
            {
                capture.Publish(scenes.Red, Color.Yellow);
                await Until(() => output.Status.DroppedFrames > dropped, "Real mutex contention drops a pending publication");
                Check(output.Status.Status == "ready", "An initial dropped publication is not immediately a transport error");
                await Until(() => output.Status.Status == "transport_degraded", "Persistent pending publication failure becomes visible", 4500);
                Check(output.Status.Error?.StartsWith("Raw publication: Surface mutex is busy", StringComparison.Ordinal) == true,
                    "Publication failure keeps its own diagnostic rather than being replaced by heartbeat failure");
            }
            await Until(() => output.Status.Status == "ready" && output.Status.Error == null && output.Status.PublishedFrames > published,
                "Pending latest frame is published and its diagnostic clears after mutex release", 2200);
            Check(output.Status.EffectiveState!.Image.Generation == state.Image.Generation, "Publication contention retains the coherent generation");

            raw = ReadSurface(state.Image.Channel);
            Volatile.Write(ref corrupt, 1);
            capture.Publish(scenes.Red, Color.Cyan);
            await Until(() => output.Status.Status == "transport_degraded", "Persistent invalid upstream pixels are diagnosed despite healthy heartbeats", 4500);
            Check(output.Status.Error?.StartsWith("Raw publication: BGRA8 surface requires alpha 255", StringComparison.Ordinal) == true,
                "Publication error is captured before the succeeding heartbeat clears publisher.LastError");
            var invalid = ReadSurface(state.Image.Channel);
            Check(invalid.Sequence == raw.Sequence && invalid.Timestamp > raw.Timestamp && invalid.Pixels.SequenceEqual(raw.Pixels),
                "Healthy heartbeats retain the old valid image without concealing rejected new pixels");
            Volatile.Write(ref corrupt, 0);
            await Until(() => ReadTimestamp(state.Image.Channel) > invalid.Timestamp + 500, "Several healthy heartbeats occur while invalid latest frame is retried");
            Check(output.Status.Status == "transport_degraded", "Only a successful publication may clear publication degradation");
            capture.Publish(scenes.Red, Color.Blue);
            await Until(() => output.Status.Status == "ready" && output.Status.Error == null && ulong.Parse(output.Status.EffectiveState!.Image.Sequence) > raw.Sequence,
                "New valid source pixels recover publication status", 2200);
        }
        finally { await output.StopAsync(); }
    }

    private static async Task Main(string[] args)
    {
        string? cpp = args is ["--reader", var executable] ? Path.GetFullPath(executable) : null;
        if (args.Length > 0 && cpp == null) throw new ArgumentException("Usage: --reader <actual FrameSurfaceReader.exe>");
        if (cpp != null && !File.Exists(cpp)) throw new FileNotFoundException("Build the actual Reader with NativeOutputTests/Run-Tests.ps1 first.", cpp);
        var testRoot = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "tests", "obj", "native-integration"));
        var profile = Path.Combine(testRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        var capture = new CaptureProducer(); var scenes = new SceneController(capture); var saved = new SavedSettings(); var preferences = new LocalSettings();
        using var compositor = new CompositeFrameService(capture);
        using var control = new NativeControlService(scenes, saved);
        var snapshots = new SnapshotGate(scenes, control);
        using var output = new NativeOutputService(compositor, control, snapshots.ReadAsync);
        var api = new NativeApiServer(output, control);
        using var integration = new NativeIntegrationService(preferences, output, api, control, profile);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        string? token = null;
        Uri? baseUrl = null;
        async Task<(HttpStatusCode Status, JsonElement Body)> Request(HttpMethod method, string path, object? body = null,
            bool authenticate = true, Action<HttpRequestMessage>? configure = null)
        {
            using var request = new HttpRequestMessage(method, new Uri(baseUrl!, path));
            if (authenticate) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body != null) request.Content = new StringContent(body is string text ? text : JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
            configure?.Invoke(request);
            using var response = await client.SendAsync(request);
            Check(!response.Headers.Contains("Access-Control-Allow-Origin"), "Native API never inherits streaming CORS");
            var bytes = await response.Content.ReadAsByteArrayAsync();
            JsonElement parsed = default;
            if (bytes.Length > 0) { using var document = JsonDocument.Parse(bytes); parsed = document.RootElement.Clone(); }
            return (response.StatusCode, parsed);
        }
        async Task<NativePublishedState> Published(Func<NativePublishedState, bool>? predicate = null)
        {
            await Until(() => output.Status.EffectiveState is { } state && (predicate?.Invoke(state) ?? true), "Native effective state is published");
            return output.Status.EffectiveState!;
        }
        try
        {
            await integration.InitializeAsync();
            Check(!integration.Enabled && !File.Exists(integration.ConnectionPath), "Native output defaults off without creating credentials");
            var firstComposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void FreezeAfterFirstComposition(object? sender, RawCompositeFrame frame)
            {
                compositor.RawFrameAvailable -= FreezeAfterFirstComposition;
                snapshots.Freeze(); firstComposed.TrySetResult();
            }
            // Installed before the output subscription: the first real frame is composed,
            // then its health snapshot times out. Recovery must request composition again
            // even though no source publishes another frame while the test waits.
            compositor.RawFrameAvailable += FreezeAfterFirstComposition;
            await integration.SetEnabledAsync(true);
            await firstComposed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Until(() => output.Status.Status == "application_unresponsive", "First composed frame can be rejected after health timeout", 3000);
            Check(output.Status.EffectiveState == null, "Failed first-frame health check never announces a false effective image");
            snapshots.Thaw();
            await Published();
            Check(integration.Enabled && File.Exists(integration.ConnectionPath), "Enabling starts actual output and protected discovery descriptor");
            using (var descriptor = JsonDocument.Parse(await File.ReadAllTextAsync(integration.ConnectionPath)))
            { baseUrl = new Uri(descriptor.RootElement.GetProperty("baseUrl").GetString()!); token = descriptor.RootElement.GetProperty("token").GetString()!; }
            Check(baseUrl.Host == "127.0.0.1" && baseUrl.Port > 0, "Control listener is isolated ephemeral IPv4 loopback");
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var acl = new FileInfo(integration.ConnectionPath).GetAccessControl();
                var grants = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Where(rule => rule.AccessControlType == AccessControlType.Allow).ToArray();
                Check(grants.Length > 0 && grants.All(rule => rule.IdentityReference.Equals(identity.User)), "Discovery credential ACL grants access only to the current Windows user");
            }
            foreach (var route in new[] { "discovery", "status", "scenes", "active-scene", "states/1" })
                Check((await Request(HttpMethod.Get, "/api/native/v1/" + route, authenticate:false)).Status == HttpStatusCode.Unauthorized, "Every read route requires authentication");
            foreach (var (method, route) in new[] { (HttpMethod.Post, "leases"), (HttpMethod.Put, "leases/00000000-0000-0000-0000-000000000001/renew"),
                (HttpMethod.Put, "leases/00000000-0000-0000-0000-000000000001/scene"), (HttpMethod.Put, "leases/00000000-0000-0000-0000-000000000001/appearance"),
                (HttpMethod.Delete, "leases/00000000-0000-0000-0000-000000000001") })
                Check((await Request(method, "/api/native/v1/" + route, new { }, authenticate:false)).Status == HttpStatusCode.Unauthorized, "Every mutation route requires authentication");
            foreach (var configure in new Action<HttpRequestMessage>[] {
                request => request.Headers.Add("Origin", "https://untrusted.invalid"), request => request.Headers.Add("Sec-Fetch-Site", "none"), request => request.Headers.Host = "untrusted.invalid" })
                Check((await Request(HttpMethod.Get, "/api/native/v1/status", configure:configure)).Status == HttpStatusCode.Forbidden, "Foreign host/browser-origin requests are rejected despite a valid credential");
            Check((await Request(HttpMethod.Get, "/api/native/v1/status", configure:request => request.Headers.Authorization = new("Bearer", new string('0', 64)))).Status == HttpStatusCode.Unauthorized, "Wrong token rejected");
            var discovery = await Request(HttpMethod.Get, "/api/native/v1/discovery");
            Check(discovery.Status == HttpStatusCode.OK && discovery.Body.GetProperty("outputs").GetArrayLength() == 2 && discovery.Body.GetProperty("transport").GetProperty("name").GetString() == "ORGBFRM1", "Discovery advertises both interoperable surfaces");
            Check((await Request(HttpMethod.Get, "/api/native/v1/scenes")).Body.GetProperty("scenes").GetArrayLength() == 2, "Saved scenes expose stable synthetic IDs");
            Check((await Request(HttpMethod.Get, "/api/native/v1/active-scene")).Status == HttpStatusCode.OK, "Active scene route available before source capture");
            Check((await Request(HttpMethod.Get, "/api/native/v1/states/0")).Status == HttpStatusCode.BadRequest, "Invalid generation rejected");
            Check((await Request(HttpMethod.Get, "/api/native/v2/discovery")).Status == HttpStatusCode.NotFound, "Unknown API version rejected");
            foreach (var body in new[] { "null", "{", "{\"clientId\":\"test\",\"unknown\":1}", "{\"clientId\":\"test\",\"overrides\":{\"enabled\":false}}", "{\"clientId\":\"test\",\"ttlSeconds\":301}" })
                Check((await Request(HttpMethod.Post, "/api/native/v1/leases", body)).Status == HttpStatusCode.BadRequest, "Malformed/unknown/unsupported request is rejected");
            Check((await Request(HttpMethod.Post, "/api/native/v1/leases", new string(' ', 17000))).Status == HttpStatusCode.RequestEntityTooLarge, "Oversized native JSON body is bounded");
            Check(control.Current.Lease == null, "Rejected requests did not acquire ownership");

            scenes.PublishImages();
            var initial = await Published(state => state.Rendering.Sources.Count(source => source.HasFrame) == 2);
            var raw = ReadSurface(initial.Image.Channel); var coverage = ReadSurface(initial.Coverage.Channel);
            Check(raw.Generation.ToString(CultureInfo.InvariantCulture) == initial.Image.Generation && coverage.Generation.ToString(CultureInfo.InvariantCulture) == initial.Coverage.Generation && coverage.Sequence == 1, "Raw/coverage metadata binds separate immutable generations");
            Pixel(raw, 50, 50, 127, 0, 0, "Raw source opacity over black"); Pixel(coverage, 50, 50, 128, 128, 128, "Source opacity coverage");
            Pixel(raw, 180, 50, 0, 0, 0, "Black image remains black"); Pixel(coverage, 180, 50, 255, 255, 255, "Black source retains full coverage");
            Pixel(coverage, 300, 190, 0, 0, 0, "Absent source is uncovered");
            Check(compositor.JpegEncodes == 0, "Native-only integration does not encode composite JPEGs");
            if (cpp != null) { await JoinedCppReader(cpp, initial.Image, raw); await JoinedCppReader(cpp, initial.Coverage, coverage); }
            var beforeHeartbeat = raw;
            await Task.Delay(700);
            var heartbeat = ReadSurface(initial.Image.Channel);
            Check(heartbeat.Sequence == beforeHeartbeat.Sequence && heartbeat.Timestamp > beforeHeartbeat.Timestamp && heartbeat.Pixels.SequenceEqual(beforeHeartbeat.Pixels), "Static heartbeat refreshes health without inventing a new image or changing pixels");
            snapshots.Freeze();
            await Until(() => output.Status.Status == "application_unresponsive", "A blocked application context stops reporting healthy static output", 3000);
            snapshots.Thaw();
            await Until(() => output.Status.Status == "ready" && output.Status.Error == null, "Healthy static output recovers its status without requiring a new captured image", 2200);
            capture.Publish(scenes.Red, Color.Lime);
            await Until(() => ulong.Parse(output.Status.EffectiveState!.Image.Sequence) > raw.Sequence, "Latest synthetic frame replaces the current surface");
            var sameState = output.Status.EffectiveState!;
            Check(sameState.StateRevision == initial.StateRevision && sameState.Image.Generation == initial.Image.Generation && sameState.Coverage.Sequence == "1", "New pixels retain geometry state and immutable coverage");
            var immutable = (await Request(HttpMethod.Get, "/api/native/v1/states/" + initial.Image.Generation)).Body;
            Check(immutable.GetProperty("image").GetProperty("sequence").GetString() == initial.Image.Sequence, "Generation endpoint keeps first valid image sequence immutable");
            Check((await Request(HttpMethod.Get, "/api/native/v1/status")).Body.GetProperty("output").GetProperty("effectiveState").GetProperty("image").GetProperty("sequence").GetString() == sameState.Image.Sequence, "Current status exposes latest image sequence");

            var lease = await Request(HttpMethod.Post, "/api/native/v1/leases", new { clientId = "integration-fixture", ttlSeconds = 30, overrides = new { hue = 45 } });
            Check(lease.Status == HttpStatusCode.OK && lease.Body.GetProperty("result").GetString() == "effective", "Appearance command acknowledges a genuinely published state");
            var leaseId = lease.Body.GetProperty("control").GetProperty("lease").GetProperty("id").GetGuid();
            var publicLease = (await Request(HttpMethod.Get, "/api/native/v1/status")).Body.GetProperty("control").GetProperty("lease");
            Check(!publicLease.TryGetProperty("id", out _), "Public status does not disclose another client's lease capability");
            Check(output.Status.EffectiveState!.Rendering.EffectiveSettings.Hue == 45 && saved.Writes == 0 && saved.Current.Hue == 0, "Temporary appearance is metadata-only and does not write saved preferences");
            Pixel(ReadSurface(initial.Image.Channel), 50, 50, 0, 127, 0, "Appearance override does not double-filter raw pixels");
            Check((await Request(HttpMethod.Post, "/api/native/v1/leases", new { clientId = "conflicting-client" })).Status == HttpStatusCode.Conflict, "One global scene has explicit exclusive ownership");
            Check((await Request(HttpMethod.Put, $"/api/native/v1/leases/{leaseId}/renew", new { ttlSeconds = 30 })).Status == HttpStatusCode.OK, "Lease renew route works");

            snapshots.Freeze();
            var pending = await Request(HttpMethod.Put, $"/api/native/v1/leases/{leaseId}/scene", new { sceneId = SceneController.SceneB });
            Check(pending.Status == HttpStatusCode.Accepted && pending.Body.GetProperty("result").GetString() == "pending_frame" && pending.Body.GetProperty("effectiveState").ValueKind == JsonValueKind.Null, "Unpublished scene changes return 202, never a false effective acknowledgement");
            snapshots.Thaw();
            var sceneB = await Published(state => state.Scene.ActiveSceneId == SceneController.SceneB && state.ControlRevision >= pending.Body.GetProperty("targetControlRevision").GetInt64());
            Pixel(ReadSurface(sceneB.Image.Channel), 50, 50, 0, 0, 255, "New scene pixels match newly bound geometry");
            Check(sceneB.Rendering.Sources.Count == 1 && sceneB.Rendering.Sources[0].Id == scenes.Blue.Id, "Scene acknowledgement does not mix old source geometry with new pixels");

            await saved.UpdateAsync(saved.Current with { Hue = -42 });
            await Published(state => state.Rendering.EffectiveSettings.Hue == -42);
            Check((await Request(HttpMethod.Put, $"/api/native/v1/leases/{leaseId}/appearance", new { overrides = new { hue = 10 } })).Status == HttpStatusCode.Conflict, "Manual appearance edit revokes external overrides through API");
            var released = await Request(HttpMethod.Delete, $"/api/native/v1/leases/{leaseId}");
            Check(released.Status == HttpStatusCode.OK && saved.Current.Hue == -42 && saved.Writes == 1, "Lease release preserves manual preferences and restores previous scene");
            var restored = await Published(state => state.Scene.ActiveSceneId == SceneController.SceneA);
            var shortLease = await Request(HttpMethod.Post, "/api/native/v1/leases", new { clientId = "expiry-fixture", ttlSeconds = 1, sceneId = SceneController.SceneB });
            Check(shortLease.Status == HttpStatusCode.OK, "Short lease becomes effective through the real API");
            await Until(() => control.Current.Lease == null, "Disconnected native client lease expires automatically", 4000);
            await Published(state => state.Scene.ActiveSceneId == SceneController.SceneA);
            Check(saved.Current.Hue == -42 && saved.Writes == 1, "Lease expiration preserves manual saved preferences");
            scenes.SetPlayback(true, true); await Published(state => state.Scene.IsPaused);
            Check(output.Status.Status == "paused" && (await Request(HttpMethod.Get, "/api/native/v1/scenes")).Status == HttpStatusCode.OK, "Pause keeps native image and scene-control API available");
            scenes.SetPlayback(false, false); await Published(state => !state.Scene.IsRecording);
            Check(output.Status.Status == "stopped" && (await Request(HttpMethod.Get, "/api/native/v1/discovery")).Status == HttpStatusCode.OK, "Capture stop does not stop native discovery");
            compositor.SetCanvasSize(800, 600);
            var hq = await Published(state => state.Rendering.OutputWidth == 800 && state.Rendering.OutputHeight == 600);
            Check(hq.Image.Generation != restored.Image.Generation && hq.Coverage.Generation != restored.Coverage.Generation, "Resize creates a new coherent image/coverage generation pair");
            Pixel(ReadSurface(hq.Coverage.Channel), 450, 150, 255, 255, 255, "HQ coverage scales canonical geometry");
            if (cpp != null) await JoinedCppReader(cpp, hq.Image, ReadSurface(hq.Image.Channel));
            capture.Fail(scenes.Black);
            var failed = await Published(state => !state.Rendering.Sources.Single(source => source.Id == scenes.Black.Id).HasFrame);
            Pixel(ReadSurface(failed.Coverage.Channel), 450, 150, 0, 0, 0, "Source failure removes its silhouette and opacity coverage");

            var oldToken = token;
            await integration.SetEnabledAsync(false);
            Check(!integration.Enabled && api.BaseUrl == null && !File.Exists(integration.ConnectionPath), "Disable removes listener, publication and credentials");
            await integration.SetEnabledAsync(true);
            using (var descriptor = JsonDocument.Parse(await File.ReadAllTextAsync(integration.ConnectionPath)))
            { baseUrl = new Uri(descriptor.RootElement.GetProperty("baseUrl").GetString()!); token = descriptor.RootElement.GetProperty("token").GetString()!; }
            Check(token != oldToken && integration.Enabled, "Re-enable rotates credentials and restarts independently of stopped capture");
            await Published();
            Check(compositor.JpegEncodes == 0, "All native lifecycle/control tests avoid composite JPEG encoding");
            await integration.StopAsync();
            Check(api.BaseUrl == null && !output.Status.Enabled && !File.Exists(integration.ConnectionPath), "Awaited shutdown closes API, surfaces and descriptor");
            await TransportHealth();
            Console.WriteLine($"PASS: {_assertions} native integration assertions; C++ Reader {(cpp == null ? "not requested" : "joined successfully")}. Synthetic sources only.");
        }
        finally
        {
            snapshots.Thaw(); await integration.StopAsync();
            var resolved = Path.GetFullPath(profile);
            if (resolved.StartsWith(testRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved)) Directory.Delete(resolved, recursive:true);
        }
    }
}
