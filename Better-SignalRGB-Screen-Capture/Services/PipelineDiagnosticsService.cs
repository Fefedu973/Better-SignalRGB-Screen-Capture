using System.Collections.Concurrent;
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Models;

namespace Better_SignalRGB_Screen_Capture.Services;

public sealed class PipelineDiagnosticsService(TimeProvider? timeProvider = null) : IPipelineDiagnosticsService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<Guid, SourceCounters> _sources = new();
    private readonly object _transportLock = new();
    private readonly RateWindow _transportRate = new();
    private bool _running;
    private long _sent, _sentBytes, _sendErrors;
    private double _sendMilliseconds;
    private DateTimeOffset? _lastSend;
    private string? _lastSendError;

    // Fixed-size buckets, not a queue of timestamps. Counter operations allocate
    // nothing; only the user-visible snapshot requested once per second allocates.
    private sealed class RateWindow
    {
        private readonly long[] _seconds = [-1, -1, -1, -1, -1];
        private readonly long[] _counts = new long[5];
        private double? _first;
        public void Add(double now)
        {
            _first ??= now;
            var second = (long)Math.Floor(now);
            var slot = (int)(second % 5);
            if (_seconds[slot] != second) { _seconds[slot] = second; _counts[slot] = 0; }
            _counts[slot]++;
        }
        public double Rate(double now)
        {
            if (_first is not { } first) return 0;
            var second = (long)Math.Floor(now);
            long count = 0;
            for (var index = 0; index < 5; index++)
                if (_seconds[index] >= 0 && second - _seconds[index] is >= 0 and < 5) count += _counts[index];
            return count / Math.Clamp(now - Math.Max(first, second - 4), 1, 5);
        }
        public void Clear() { Array.Fill(_seconds, -1); Array.Clear(_counts); _first = null; }
    }

    private sealed class SourceCounters(Guid id)
    {
        public Guid Id = id;
        public object Sync { get; } = new();
        public string Name = string.Empty;
        public long Generation;
        public CaptureEncoderKind Encoder;
        public CaptureColorInfo ColorInfo = CaptureColorInfo.Unknown;
        public CaptureDiagnosticState State;
        public int RequestedRate, Width, Height;
        public long Received, Produced, Dropped, Skipped, Errors, Bytes, Sent;
        public double ProcessingMilliseconds, SendMilliseconds;
        public DateTimeOffset? LastFrame;
        public string? LastError;
        public RateWindow CaptureRate { get; } = new();
        public RateWindow SendRate { get; } = new();
    }

    private sealed class Session(PipelineDiagnosticsService owner, SourceCounters counters, long generation) : ICaptureDiagnosticsSession
    {
        private bool Current => counters.Generation == generation;
        public void Received() { lock (counters.Sync) if (Current && counters.State == CaptureDiagnosticState.Capturing) counters.Received++; }
        public void Produced(int jpegBytes, double processingMilliseconds)
        {
            lock (counters.Sync)
            {
                if (!Current || counters.State != CaptureDiagnosticState.Capturing) return;
                counters.Produced++; counters.Bytes += Math.Max(0, jpegBytes);
                counters.ProcessingMilliseconds += FiniteDuration(processingMilliseconds);
                counters.LastFrame = owner._clock.GetUtcNow();
                counters.CaptureRate.Add(owner.Now);
            }
        }
        public void Dropped() { lock (counters.Sync) if (Current && counters.State == CaptureDiagnosticState.Capturing) counters.Dropped++; }
        public void Skipped() { lock (counters.Sync) if (Current && counters.State == CaptureDiagnosticState.Capturing) counters.Skipped++; }
        public void SetFrameRate(int frameRate) { lock (counters.Sync) if (Current) counters.RequestedRate = frameRate; }
        public void SetColorInfo(CaptureColorInfo info)
        {
            ArgumentNullException.ThrowIfNull(info);
            var normalized = info.Normalize();
            lock (counters.Sync)
                if (Current && counters.State == CaptureDiagnosticState.Capturing) counters.ColorInfo = normalized;
        }
        public void Error(string error, bool terminal)
        {
            lock (counters.Sync)
            {
                if (!Current || counters.State != CaptureDiagnosticState.Capturing) return;
                counters.Errors++; counters.LastError = BoundedError(error);
                if (terminal) counters.State = CaptureDiagnosticState.Failed;
            }
        }
        public void Stop()
        {
            lock (counters.Sync)
            {
                if (!Current) return;
                if (counters.State != CaptureDiagnosticState.Failed) counters.State = CaptureDiagnosticState.Stopped;
                counters.CaptureRate.Clear();
            }
        }
    }

    private double Now => (double)_clock.GetTimestamp() / _clock.TimestampFrequency;
    private static double FiniteDuration(double value) => double.IsFinite(value) && value >= 0 ? value : 0;
    private static string BoundedError(string error) => error.Length <= 1024 ? error : error[..1024];

    public ICaptureDiagnosticsSession BeginCapture(Guid sourceId, string name, CaptureEncoderKind encoder,
        int requestedFrameRate, int width, int height)
    {
        var counters = _sources.GetOrAdd(sourceId, static id => new(id));
        lock (counters.Sync)
        {
            counters.Name = name; counters.Encoder = encoder; counters.RequestedRate = requestedFrameRate;
            counters.ColorInfo = encoder switch
            {
                CaptureEncoderKind.Website => new(CaptureColorMode.Sdr, "WebView2 screenshot"),
                CaptureEncoderKind.Wallpaper => new(CaptureColorMode.HdrUnsupported, "GDI PrintWindow BGRA8"),
                CaptureEncoderKind.WindowsGraphicsCapture => new(CaptureColorMode.Unknown, "WGC FP16"),
                _ => new(CaptureColorMode.Unknown, "ScreenRecorderLib BGRA8")
            };
            counters.Width = width; counters.Height = height; counters.State = CaptureDiagnosticState.Capturing;
            counters.CaptureRate.Clear();
            return new Session(this, counters, ++counters.Generation);
        }
    }

    public void RecordSignalRgbFrame(Guid sourceId, int jpegBytes, double elapsedMilliseconds)
    {
        var now = Now;
        lock (_transportLock)
        {
            _sent++; _sentBytes += Math.Max(0, jpegBytes); _sendMilliseconds += FiniteDuration(elapsedMilliseconds);
            _lastSend = _clock.GetUtcNow(); _transportRate.Add(now);
        }
        if (_sources.TryGetValue(sourceId, out var counters)) lock (counters.Sync)
        {
            counters.Sent++; counters.SendMilliseconds += FiniteDuration(elapsedMilliseconds); counters.SendRate.Add(now);
        }
    }

    public void RecordSignalRgbError(string error)
    {
        lock (_transportLock) { _sendErrors++; _lastSendError = BoundedError(error); }
    }
    public void SetSignalRgbRunning(bool running)
    {
        lock (_transportLock) { _running = running; if (!running) _transportRate.Clear(); }
        if (!running) foreach (var counters in _sources.Values) lock (counters.Sync) counters.SendRate.Clear();
    }
    public void RemoveSource(Guid sourceId)
    {
        if (_sources.TryRemove(sourceId, out var counters)) lock (counters.Sync) counters.Generation++;
    }
    public PipelineDiagnosticsSnapshot GetSnapshot()
    {
        var now = Now;
        SignalRgbTransportDiagnosticsSnapshot transport;
        lock (_transportLock) transport = new(_running, _sent, _sentBytes, _sendErrors,
            _running ? _transportRate.Rate(now) : 0, _sent == 0 ? 0 : _sendMilliseconds / _sent, _lastSend, _lastSendError);
        var snapshots = new List<SourceDiagnosticsSnapshot>(_sources.Count);
        foreach (var counters in _sources.Values) lock (counters.Sync)
            snapshots.Add(new(counters.Id, counters.Name, counters.Encoder, counters.State, counters.RequestedRate, counters.Width, counters.Height,
                counters.Received, counters.Produced, counters.Dropped, counters.Skipped, counters.Errors, counters.Bytes, counters.Sent,
                counters.State == CaptureDiagnosticState.Capturing ? counters.CaptureRate.Rate(now) : 0,
                transport.Running ? counters.SendRate.Rate(now) : 0,
                counters.Produced == 0 ? 0 : counters.ProcessingMilliseconds / counters.Produced,
                counters.Sent == 0 ? 0 : counters.SendMilliseconds / counters.Sent, counters.LastFrame, counters.LastError)
                { ColorInfo = counters.ColorInfo });
        return new(snapshots.OrderBy(source => source.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(), transport);
    }
}
