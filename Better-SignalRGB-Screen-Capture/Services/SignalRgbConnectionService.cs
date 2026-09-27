using System.Net.Http;
using System.Text.Json;
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Models;

namespace Better_SignalRGB_Screen_Capture.Services;

/// <summary>Transport reachability and explicit effect-render feedback are separate signals.</summary>
public sealed class SignalRgbConnectionService : ISignalRgbConnectionService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private string? _session;
    private bool _apiReachable;
    private DateTimeOffset? _lastApiSuccess, _lastFeedback;
    private DateTimeOffset _sendWindow, _lastSent;
    private long _framesSent, _renderedFrames, _windowSent;
    private double _sendRate, _renderRate;
    private string? _lastError;

    public SignalRgbConnectionService() : this(TimeProvider.System) { }
    internal SignalRgbConnectionService(TimeProvider clock) => _clock = clock;

    public SignalRgbConnectionSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                var now = _clock.GetUtcNow();
                var streaming = _session != null;
                var api = _apiReachable && _lastApiSuccess.HasValue && now - _lastApiSuccess.Value < TimeSpan.FromSeconds(10);
                var responding = streaming && _lastFeedback.HasValue && now - _lastFeedback.Value < TimeSpan.FromSeconds(6);
                var confirmed = responding && _renderedFrames > 0;
                var message = !streaming ? (api ? "SignalRGB API reachable. Start streaming to verify the effect." : "Start SignalRGB and check the connection.")
                    : !api ? "Waiting for the SignalRGB Canvas API. Check that SignalRGB is running."
                    : !responding ? "API reachable; effect rendering is not confirmed. Select the matching effect in SignalRGB."
                    : !confirmed ? "Effect connected; waiting for the first captured image."
                    : "Effect confirms that captured images have been drawn. LED output still depends on the SignalRGB layout.";
                return new(streaming, api, responding, confirmed, _framesSent, _renderedFrames,
                    now - _lastSent > TimeSpan.FromSeconds(2) ? 0 : _sendRate,
                    responding ? _renderRate : 0, message, _lastError);
            }
        }
    }

    public string BeginSession(int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        lock (_gate)
        {
            _session = Guid.NewGuid().ToString("N");
            _lastFeedback = null;
            _framesSent = _renderedFrames = _windowSent = 0;
            _sendRate = _renderRate = 0;
            _sendWindow = _lastSent = _clock.GetUtcNow();
            return "health:" + JsonSerializer.Serialize(new { version = 1, callback = $"http://localhost:{port}/api/effect-status", session = _session });
        }
    }

    public void EndSession()
    {
        lock (_gate) { _session = null; _lastFeedback = null; _sendRate = _renderRate = 0; }
    }

    public void ApiSucceeded()
    {
        lock (_gate) { _apiReachable = true; _lastApiSuccess = _clock.GetUtcNow(); _lastError = null; }
    }

    public void ApiFailed(string error)
    {
        lock (_gate) { _apiReachable = false; _lastError = error; }
    }

    public void FrameSent()
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            _framesSent++; _windowSent++; _lastSent = now;
            var seconds = (now - _sendWindow).TotalSeconds;
            if (seconds >= 1) { _sendRate = _windowSent / seconds; _windowSent = 0; _sendWindow = now; }
        }
    }

    public bool ReceiveFeedback(string? session, long renderedFrames, int version)
    {
        if (version != 1 || renderedFrames is < 0 or > 9_007_199_254_740_991) return false;
        lock (_gate)
        {
            if (_session == null || !string.Equals(_session, session, StringComparison.Ordinal)) return false;
            var now = _clock.GetUtcNow();
            var seconds = _lastFeedback.HasValue ? (now - _lastFeedback.Value).TotalSeconds : 0;
            // Reloading the HTML restarts its counter. Never turn that into a negative rate.
            _renderRate = seconds >= .1 && renderedFrames >= _renderedFrames ? (renderedFrames - _renderedFrames) / seconds : 0;
            _renderedFrames = renderedFrames;
            _lastFeedback = now;
            return true;
        }
    }

    public async Task ProbeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var body = new StringContent(string.Empty);
            using var response = await Client.PostAsync(
                "http://localhost:16034/canvas/event?sender=BetterSignalRGBScreenCapture&event=healthcheck", body, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            ApiSucceeded();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            ApiFailed(cancellationToken.IsCancellationRequested ? "Connection check cancelled." : ex.Message);
        }
    }
}
