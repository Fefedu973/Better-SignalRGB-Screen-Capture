using System.Diagnostics;
using System.Text.Json;
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Models;

namespace Better_SignalRGB_Screen_Capture.Services.NativeOutput;

internal sealed record NativeCompositionContext(NativeSceneState Scene, long ControlRevision, SignalRgbEffectSettings Settings);
internal sealed record NativeSurfaceReference(string OutputId, string Channel, string Generation, string Sequence,
    int Width, int Height, int Stride, string Format = "BGRA8_OPAQUE_SRGB");
internal sealed record NativePublishedState(long StateRevision, long ControlRevision, NativeSceneState Scene,
    NativeRenderMetadata Rendering, NativeSurfaceReference Image, NativeSurfaceReference Coverage);
internal sealed record NativeOutputStatus(bool Enabled, string Status, string? Error, long PublishedFrames,
    long DroppedFrames, double CompositionMilliseconds, double PublicationMilliseconds, double FrameAgeMilliseconds,
    NativePublishedState? EffectiveState);

/// <summary>Two bounded CPU surfaces and a single pending owned frame. No reader runs on capture/UI threads.</summary>
internal sealed class NativeOutputService : IDisposable
{
    private readonly CompositeFrameService _compositor;
    private readonly Func<CancellationToken, Task<CompositeRenderSnapshot>> _snapshot;
    private readonly NativeControlService _control;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Queue<NativePublishedState> _history = new();
    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private RawCompositeFrame? _pending;
    private FrameSurfacePublisher? _image, _coverage;
    private NativeOutputStatus _status = new(false, "disabled", null, 0, 0, 0, 0, 0, null);
    private string? _stateKey;
    private string _prefix = "";
    private long _revision;
    // Worker-owned failure streaks: refreshing an old frame must not hide repeated
    // failures to publish a newer one, and publishing raw pixels cannot heal coverage.
    private long _publicationFailedAt, _heartbeatFailedAt;
    private string? _publicationError, _heartbeatError;
    private bool _disposed;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const ulong Capacity = 800UL * 600 * 4;
    private static readonly TimeSpan TransportFailureGrace = TimeSpan.FromSeconds(2);

    public NativeOutputService(CompositeFrameService compositor, NativeControlService control,
        Func<CancellationToken, Task<CompositeRenderSnapshot>> snapshot)
    { _compositor = compositor; _control = control; _snapshot = snapshot; }
    public NativeOutputStatus Status { get { lock (_gate) return _status; } }
    public NativePublishedState? FindState(string generation)
    { lock (_gate) return _history.LastOrDefault(state => state.Image.Generation == generation); }

    public void Start(string channelPrefix)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_worker != null) throw new InvalidOperationException("Native output is already running.");
        _prefix = channelPrefix;
        _cancellation = new();
        _publicationFailedAt = _heartbeatFailedAt = 0;
        _publicationError = _heartbeatError = null;
        lock (_gate) _status = new(true, "waiting_for_frame", null, 0, 0, 0, 0, 0, null);
        _compositor.NativeSnapshotFactory = _snapshot;
        _compositor.RawFrameAvailable += OnFrame;
        _control.Changed += OnControlChanged;
        _worker = Task.Run(() => PublishLoopAsync(_cancellation.Token));
    }
    private void OnControlChanged(object? sender, NativeControlState state) => _compositor.InvalidateLayout();
    private void OnFrame(object? sender, RawCompositeFrame frame)
    {
        lock (_gate)
        {
            if (!_status.Enabled) return;
            if (_pending != null) { _pending.Dispose(); _status = _status with { DroppedFrames = _status.DroppedFrames + 1 }; }
            _pending = frame.Retain();
        }
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }
    private async Task PublishLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await _wake.WaitAsync(250, token).ConfigureAwait(false);
                RawCompositeFrame? frame;
                lock (_gate) { frame = _pending; _pending = null; }
                using (frame)
                {
                    try
                    {
                        // A heartbeat requires a responsive application context. A hung UI/producer
                        // cannot keep an old image healthy merely because a timer still runs.
                        using var healthTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        healthTimeout.CancelAfter(TimeSpan.FromMilliseconds(750));
                        var healthy = await _snapshot(healthTimeout.Token).ConfigureAwait(false);
                        if (healthy.Context is not NativeCompositionContext current) continue;
                        if (frame != null && frame.Snapshot.Context is NativeCompositionContext context)
                        {
                            // Commands/manual edits that completed after this composition invalidate it.
                            if (context != current || !frame.Snapshot.Sources.SequenceEqual(healthy.Sources))
                            { Drop(); _compositor.InvalidateLayout(); continue; }
                            Publish(frame, context);
                        }
                        else if (_image == null)
                        {
                            // Recover a static first image discarded during an unresponsive UI or
                            // a failed channel open; it might never emit another capture callback.
                            _compositor.InvalidateLayout();
                        }
                        if (_image != null && _coverage != null)
                        {
                            var effective = Status.EffectiveState;
                            if (effective != null && (effective.Scene != current.Scene || effective.ControlRevision != current.ControlRevision))
                            { _compositor.InvalidateLayout(); continue; }
                            var imageAlive = _image.Heartbeat();
                            var imageError = imageAlive ? null : _image.LastError;
                            var coverageAlive = _coverage.Heartbeat();
                            var coverageError = coverageAlive ? null : _coverage.LastError;
                            if (imageAlive && coverageAlive)
                            { _heartbeatFailedAt = 0; _heartbeatError = null; }
                            else RecordFailure(ref _heartbeatFailedAt, ref _heartbeatError,
                                imageAlive ? "Coverage heartbeat" : "Raw heartbeat", imageAlive ? coverageError : imageError);
                            UpdateTransportStatus(current);
                        }
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { SetError("application_unresponsive", "The application context did not respond; frames are not refreshed."); }
                    catch (Exception ex) when (!token.IsCancellationRequested) { SetError("output_error", ex.Message); }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { ClosePublishers(); }
    }
    private void Publish(RawCompositeFrame frame, NativeCompositionContext context)
    {
        if (frame.Width is not (320 or 800) || frame.Height is not (200 or 600) || (ulong)frame.Length > Capacity)
            throw new InvalidOperationException("Native output supports the normal and high-quality canvas sizes.");
        var metadata = NativeRenderMetadata.Create(0, frame.Snapshot.Sources, frame.ActiveSourceIds,
            frame.Width, frame.Height, context.Settings);
        var key = JsonSerializer.Serialize(new { context.Scene, context.ControlRevision, rendering = metadata }, Json);
        if (System.Text.Encoding.UTF8.GetByteCount(key) > 256 * 1024) throw new InvalidOperationException("Native metadata exceeds its 256 KiB limit.");
        var changed = key != _stateKey || _image == null || _coverage == null;
        var started = Stopwatch.GetTimestamp();
        if (changed)
        {
            ClosePublishers();
            try
            {
                _image = new(_prefix + "-Raw", Capacity);
                _coverage = new(_prefix + "-Coverage", Capacity);
            }
            catch { ClosePublishers(); throw; }
            // Bind immutable coverage first. A reader may observe unavailable during this transition;
            // it must never reuse a mask or metadata from another raw generation.
            if (!_coverage.TryPublish(frame.Coverage.Span, (uint)frame.Width, (uint)frame.Height, (uint)frame.Stride))
            {
                RecordFailure(ref _publicationFailedAt, ref _publicationError, "Coverage publication", _coverage.LastError);
                UpdateTransportStatus(context);
                Drop(); ClosePublishers(); Retry(frame); return;
            }
        }
        if (!_image!.TryPublish(frame.Pixels.Span, (uint)frame.Width, (uint)frame.Height, (uint)frame.Stride))
        {
            // Capture immediately: a later successful Heartbeat clears publisher.LastError.
            RecordFailure(ref _publicationFailedAt, ref _publicationError, "Raw publication", _image.LastError);
            UpdateTransportStatus(context);
            Drop(); Retry(frame); return;
        }
        _publicationFailedAt = 0; _publicationError = null;
        if (changed) { _heartbeatFailedAt = 0; _heartbeatError = null; }
        NativeSurfaceReference Reference(FrameSurfacePublisher publisher, string id) => new(id, publisher.Channel,
            publisher.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture), publisher.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            frame.Width, frame.Height, frame.Stride);
        lock (_gate)
        {
            if (!_status.Enabled) return;
            if (changed)
            {
                var state = new NativePublishedState(++_revision, context.ControlRevision, context.Scene,
                    metadata with { StateRevision = _revision }, Reference(_image, "canvas-raw"), Reference(_coverage!, "canvas-coverage"));
                _history.Enqueue(state);
                while (_history.Count > 8) _history.Dequeue();
                _status = _status with { EffectiveState = state };
                _stateKey = key;
            }
            else _status = _status with { EffectiveState = _status.EffectiveState! with { Image = Reference(_image, "canvas-raw") } };
            _status = _status with
            {
                Status = _heartbeatError == null ? HealthyStatus(context, frame.ActiveSourceIds.Count != 0) : _status.Status,
                Error = _heartbeatError == null ? null : _status.Error, PublishedFrames = _status.PublishedFrames + 1,
                CompositionMilliseconds = frame.CompositionMilliseconds,
                PublicationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                FrameAgeMilliseconds = Stopwatch.GetElapsedTime(frame.ComposedAtTicks).TotalMilliseconds
            };
        }
    }
    private static void RecordFailure(ref long failedAt, ref string? error, string operation, string? reason)
    {
        if (failedAt == 0) failedAt = Stopwatch.GetTimestamp();
        error = operation + ": " + (reason ?? "The surface did not accept the update.");
    }
    private void UpdateTransportStatus(NativeCompositionContext context)
    {
        var publicationExpired = _publicationFailedAt != 0 && Stopwatch.GetElapsedTime(_publicationFailedAt) >= TransportFailureGrace;
        var heartbeatExpired = _heartbeatFailedAt != 0 && Stopwatch.GetElapsedTime(_heartbeatFailedAt) >= TransportFailureGrace;
        lock (_gate)
        {
            if (!_status.Enabled) return;
            if (publicationExpired || heartbeatExpired)
                _status = _status with { Status = "transport_degraded", Error = publicationExpired ? _publicationError : _heartbeatError };
            else if (_publicationError == null && _heartbeatError == null && _status.EffectiveState is { } effective &&
                _status.Status is "transport_degraded" or "application_unresponsive")
                _status = _status with { Status = HealthyStatus(context, effective.Rendering.Sources.Any(source => source.Contributes)), Error = null };
        }
    }
    private static string HealthyStatus(NativeCompositionContext context, bool hasSources) =>
        context.Scene.SceneLoading ? "scene_loading" : context.Scene.IsPaused ? "paused" :
        !context.Scene.IsRecording ? "stopped" : hasSources ? "ready" : "waiting_for_sources";
    private void Retry(RawCompositeFrame frame)
    {
        // Retain just the failed latest frame for the next timer tick; never wait on a reader.
        lock (_gate) { if (_pending == null && _status.Enabled) _pending = frame.Retain(); }
    }
    private void Drop() { lock (_gate) _status = _status with { DroppedFrames = _status.DroppedFrames + 1 }; }
    private void SetError(string status, string error) { lock (_gate) _status = _status with { Status = status, Error = error }; }
    private void ClosePublishers()
    { _image?.Dispose(); _coverage?.Dispose(); _image = _coverage = null; _stateKey = null; }

    public async Task StopAsync()
    {
        lock (_gate) _status = _status with { Enabled = false, Status = "disabled", EffectiveState = null };
        _compositor.RawFrameAvailable -= OnFrame; _control.Changed -= OnControlChanged;
        _cancellation?.Cancel();
        if (_worker != null) await _worker.ConfigureAwait(false);
        _worker = null; _cancellation?.Dispose(); _cancellation = null;
        lock (_gate)
        {
            _pending?.Dispose(); _pending = null; _history.Clear();
            _status = _status with { Enabled = false, Status = "disabled", Error = null, EffectiveState = null };
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _compositor.RawFrameAvailable -= OnFrame; _control.Changed -= OnControlChanged;
        lock (_gate) { _status = _status with { Enabled = false }; _pending?.Dispose(); _pending = null; }
        _cancellation?.Cancel();
        // Normal shutdown awaits StopAsync. The worker alone owns mapped publisher handles.
    }
}
