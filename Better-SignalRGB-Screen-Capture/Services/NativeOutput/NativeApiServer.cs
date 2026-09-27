using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Better_SignalRGB_Screen_Capture.Services.NativeOutput;

/// <summary>Independent control listener: capture pause/stop never removes scene control.</summary>
internal sealed class NativeApiServer(NativeOutputService output, NativeControlService control)
{
    private WebApplication? _host;
    private readonly SemaphoreSlim _mutations = new(1, 1);
    private readonly SemaphoreSlim _requests = new(16, 16);
    private NativeConnectionFile? _connection;
    public string? BaseUrl { get; private set; }
    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 12 };

    public async Task StartAsync(NativeConnectionFile connection)
    {
        if (_host != null) throw new InvalidOperationException("Native API already started.");
        _connection = connection;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0);
            options.Limits.MaxRequestBodySize = 16 * 1024;
            options.Limits.MaxConcurrentConnections = 32;
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(15);
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
        });
        var host = builder.Build();
        host.Run(HandleAsync);
        try
        {
            await host.StartAsync().ConfigureAwait(false);
            BaseUrl = host.Urls.Single();
            connection.Publish(BaseUrl);
            _host = host;
        }
        catch { await host.DisposeAsync(); _connection = null; throw; }
    }

    private async Task HandleAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        // This listener deliberately has no CORS middleware. Protect reads as well as commands;
        // do not expose scene names, owner IDs or credentials to arbitrary browser origins.
        if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote) ||
            context.Request.Headers.ContainsKey("Origin") || context.Request.Headers.ContainsKey("Sec-Fetch-Site") ||
            BaseUrl == null || !string.Equals(context.Request.Host.Value, new Uri(BaseUrl).Authority, StringComparison.OrdinalIgnoreCase))
        { await Error(context, 403, "local_client_required", "Use a native loopback client and the connection descriptor."); return; }
        if (_connection == null || !_connection.Authenticate(context.Request.Headers.Authorization.ToString()))
        { await Error(context, 401, "unauthorized", "Read the current user-only connection descriptor and send its Bearer credential."); return; }
        if (!await _requests.WaitAsync(0)) { await Error(context, 429, "busy", "Too many concurrent native API requests."); return; }
        var mutation = context.Request.Method != "GET";
        var acquired = false;
        try
        {
            if (mutation)
            {
                acquired = await _mutations.WaitAsync(0);
                if (!acquired) { await Error(context, 409, "command_in_progress", "Another native command is still running."); return; }
            }
            await RouteAsync(context).ConfigureAwait(false);
        }
        catch (NativeControlException ex)
        {
            var status = ex.Code switch { "invalid_request" => 400, "scene_not_found" => 404, "unavailable" => 503, _ => 409 };
            await Error(context, status, ex.Code, ex.Message);
        }
        catch (JsonException) { await Error(context, 400, "invalid_json", "Send a valid version 1 JSON request with supported fields only."); }
        catch (BadHttpRequestException ex) { await Error(context, ex.StatusCode, "invalid_request", "Request exceeds the native API limits."); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Native control request failed: {ex.GetType().Name}");
            await Error(context, 503, "unavailable", "Native control is temporarily unavailable; consult the application's native output status.");
        }
        finally { if (acquired) _mutations.Release(); _requests.Release(); }
    }

    private async Task RouteAsync(HttpContext context)
    {
        const string root = "/api/native/v1/";
        var path = context.Request.Path.Value ?? "";
        if (!path.StartsWith(root, StringComparison.Ordinal)) { await Error(context, 404, "not_found", "Unknown API path/version."); return; }
        var endpoint = path[root.Length..];
        var token = context.RequestAborted;
        if (context.Request.Method == "GET")
        {
            switch (endpoint)
            {
                case "discovery":
                    await Write(context, new
                    {
                        apiVersion = 1, application = "BetterSignalRGBScreenCapture", instanceId = _connection!.InstanceId,
                        transport = new { name = "ORGBFRM1", version = 1, headerBytes = 128, maxCapacity = 67108864,
                            publisherCapacity = 1920000, scope = "windows-current-user-session", recommendedTtlMs = 2000 },
                        capabilities = new[] { "raw-composite", "opacity-coverage", "rendering-metadata-v1", "saved-scenes", "exclusive-scene-lease", "metadata-appearance-overrides", "static-heartbeat" },
                        canvas = new { width = 320, height = 200 }, globalScene = true,
                        outputs = new[] {
                            new { id = "canvas-raw", channel = _connection.ChannelPrefix + "-Raw", format = "BGRA8_OPAQUE_SRGB", role = "opaque-black-composite" },
                            new { id = "canvas-coverage", channel = _connection.ChannelPrefix + "-Coverage", format = "BGRA8_OPAQUE_SRGB", role = "opacity-coverage-grayscale" } },
                        limits = new { maxSources = 128, maxMetadataBytes = 262144, retainedGenerations = 8, maxRequestBytes = 16384, maxLeaseSeconds = 300 },
                        rendering = new { schema = "better.native-rendering", version = 1, defaults = new SignalRgbEffectSettings() }
                    }); return;
                case "status":
                    await Write(context, new { apiVersion = 1, control = PublicControl(await control.GetStatusAsync(token)), output = output.Status }); return;
                case "scenes":
                    await Write(context, new { apiVersion = 1, scenes = await control.ListScenesAsync(token) }); return;
                case "active-scene":
                    await Write(context, new { apiVersion = 1, requested = (await control.GetStatusAsync(token)).Scene,
                        effective = output.Status.EffectiveState }); return;
            }
            if (endpoint.StartsWith("states/", StringComparison.Ordinal))
            {
                var generation = endpoint[7..];
                if (!ulong.TryParse(generation, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed == 0)
                { await Error(context, 400, "invalid_generation", "Generation must be a nonzero decimal uint64 string."); return; }
                var state = output.FindState(generation);
                if (state == null) { await Error(context, 404, "state_unavailable", "Read the latest raw frame and retry; this generation is pending or has expired."); return; }
                await Write(context, state); return;
            }
        }
        if (context.Request.Method == "POST" && endpoint == "leases")
        {
            var request = await Read<AcquireRequest>(context);
            await Acknowledge(context, await control.AcquireAsync(request.ClientId, request.TtlSeconds, request.SceneId, request.Overrides, token)); return;
        }
        var segments = endpoint.Split('/');
        if (segments.Length is 2 or 3 && segments[0] == "leases" && Guid.TryParse(segments[1], out var leaseId))
        {
            if (context.Request.Method == "DELETE" && segments.Length == 2)
            { await Acknowledge(context, await control.ReleaseAsync(leaseId, true, token)); return; }
            if (context.Request.Method == "PUT" && segments.Length == 3)
            {
                switch (segments[2])
                {
                    case "renew":
                        var renew = await Read<RenewRequest>(context);
                        await Write(context, await control.RenewAsync(leaseId, renew.TtlSeconds, token)); return;
                    case "scene":
                        var scene = await Read<SceneRequest>(context);
                        await Acknowledge(context, await control.SelectSceneAsync(leaseId, scene.SceneId, token)); return;
                    case "appearance":
                        var appearance = await Read<AppearanceRequest>(context);
                        await Acknowledge(context, await control.SetOverridesAsync(leaseId, appearance.Overrides, token)); return;
                }
            }
        }
        await Error(context, 404, "not_found", "Unknown native API operation.");
    }

    private async Task Acknowledge(HttpContext context, NativeControlState target)
    {
        var deadline = Environment.TickCount64 + 2000;
        NativePublishedState? effective;
        bool ready;
        do
        {
            effective = output.Status.EffectiveState;
            ready = effective != null && effective.ControlRevision == target.Revision &&
                effective.Scene.StateRevision == target.Scene.StateRevision &&
                effective.Scene.ActiveSceneId == target.Scene.ActiveSceneId && !effective.Scene.SceneLoading;
            if (ready || Environment.TickCount64 >= deadline) break;
            if (control.Current.Revision > target.Revision)
            {
                context.Response.StatusCode = 409;
                await Write(context, new { apiVersion = 1, error = "state_superseded",
                    message = "A newer user or lease transition superseded this request before a matching image was published.",
                    targetControlRevision = target.Revision, control = PublicControl(control.Current) });
                return;
            }
            await Task.Delay(20, context.RequestAborted).ConfigureAwait(false);
        } while (true);
        context.Response.StatusCode = ready ? 200 : 202;
        await Write(context, new { apiVersion = 1, result = ready ? "effective" : "pending_frame",
            targetControlRevision = target.Revision, targetSceneRevision = target.Scene.StateRevision,
            control = target, effectiveState = ready ? effective : null });
    }
    private static async Task<T> Read<T>(HttpContext context)
    {
        if (!context.Request.HasJsonContentType()) throw new NativeControlException("invalid_request", "Content-Type must be application/json.");
        // Read with an explicit limit even for chunked bodies and tests without Kestrel limits.
        using var bytes = new MemoryStream(); var buffer = new byte[4096];
        while (true)
        {
            var length = await context.Request.Body.ReadAsync(buffer, context.RequestAborted);
            if (length == 0) break;
            if (bytes.Length + length > 16384) throw new BadHttpRequestException("Oversized body", 413);
            bytes.Write(buffer, 0, length);
        }
        return JsonSerializer.Deserialize<T>(bytes.GetBuffer().AsSpan(0, (int)bytes.Length), InputJson)
            ?? throw new JsonException("Null request.");
    }
    private static Task Write<T>(HttpContext context, T value) => context.Response.WriteAsJsonAsync(value, NativeOutputService.Json, context.RequestAborted);
    private static object PublicControl(NativeControlState state) => new
    {
        state.Revision, state.Scene, state.EffectiveSettings, state.Error,
        // The lease UUID is a capability returned only to its acquiring client, never discovery/status.
        lease = state.Lease == null ? null : new { state.Lease.ClientId, state.Lease.TtlSeconds,
            state.Lease.ExpiresAt, state.Lease.Expired, state.Lease.AppearanceRevoked }
    };
    private static Task Error(HttpContext context, int status, string code, string message)
    {
        if (context.Response.HasStarted || context.RequestAborted.IsCancellationRequested) return Task.CompletedTask;
        context.Response.StatusCode = status;
        return Write(context, new { apiVersion = 1, error = code, message });
    }
    public async Task StopAsync()
    {
        if (_host is { } host)
        {
            _host = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await host.StopAsync(timeout.Token).ConfigureAwait(false); }
            finally { await host.DisposeAsync().ConfigureAwait(false); }
        }
        _connection = null; BaseUrl = null;
    }
    private sealed record AcquireRequest(string ClientId, int TtlSeconds = 30, Guid? SceneId = null, NativeAppearanceOverrides? Overrides = null);
    private sealed record RenewRequest(int? TtlSeconds);
    private sealed record SceneRequest(Guid SceneId);
    private sealed record AppearanceRequest(NativeAppearanceOverrides? Overrides);
}
