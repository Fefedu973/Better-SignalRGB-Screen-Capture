using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Models;

namespace Better_SignalRGB_Screen_Capture.Services.NativeOutput;

/// <summary>One global scene owner, with monotonic expiration and metadata-only appearance overrides.</summary>
public sealed class NativeControlService : IDisposable
{
    private readonly INativeSceneController _scenes;
    private readonly ISignalRgbEffectSettingsService _settings;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly object _sync = new();
    private readonly ITimer _timer;
    private NativeControlState _current;
    private NativeLeaseState? _lease;
    private NativeAppearanceOverrides? _overrides;
    private long _renewedAt;
    private bool _stopped;
    private bool _disposed;

    public NativeControlService(INativeSceneController scenes, ISignalRgbEffectSettingsService settings, TimeProvider? timeProvider = null)
    {
        _scenes = scenes; _settings = settings; _time = timeProvider ?? TimeProvider.System;
        _current = new(0, null, new(null, null, 0, 0, false, false, false, false, null), settings.Current, null);
        _settings.Changed += OnSavedSettingsChanged;
        _scenes.StateChanged += OnSceneChanged;
        _timer = _time.CreateTimer(state => { _ = ExpireSafelyAsync(); }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public NativeControlState Current { get { lock (_sync) return _current; } }
    public event EventHandler<NativeControlState>? Changed;

    public Task<IReadOnlyList<NativeSceneInfo>> ListScenesAsync(CancellationToken cancellationToken = default) =>
        _scenes.ListScenesAsync(cancellationToken);

    public async Task<NativeControlState> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        // A caller must be able to observe sceneLoading while a scene command is
        // awaiting capture startup. UI events keep this immutable snapshot current.
        if (!await _commands.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return Current;
        try
        {
            await _settings.InitializeAsync().ConfigureAwait(false);
            string? restorationError = null;
            try { await ExpireIfNeededAsync().ConfigureAwait(false); }
            catch (Exception ex) { restorationError = $"Previous scene restoration is pending: {ex.Message}"; }
            Publish(await _scenes.GetStateAsync(cancellationToken).ConfigureAwait(false), restorationError);
            return Current;
        }
        finally { _commands.Release(); }
    }

    public async Task<NativeControlState> AcquireAsync(string clientId, int ttlSeconds = 30,
        Guid? sceneId = null, NativeAppearanceOverrides? overrides = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId) || clientId.Length > 128 || clientId.Any(char.IsControl))
            throw new NativeControlException("invalid_request", "ClientId must contain 1–128 printable characters.");
        ValidateTtl(ttlSeconds);
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfStopped();
            await _settings.InitializeAsync().ConfigureAwait(false);
            await ExpireIfNeededAsync().ConfigureAwait(false);
            if (_lease != null) throw new NativeControlException("lease_conflict", "Another client owns the single global scene.");
            var id = Guid.NewGuid();
            var state = await _scenes.BeginTemporaryControlAsync(id, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _lease = new(id, clientId.Trim(), ttlSeconds, _time.GetUtcNow().AddSeconds(ttlSeconds), false, false);
                _renewedAt = _time.GetTimestamp(); _overrides = overrides;
            }
            _timer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            Publish(state);
            try
            {
                if (sceneId is { } requested) state = await _scenes.LoadSceneAsync(id, requested, cancellationToken).ConfigureAwait(false);
                await EnsureNotExpiredAsync().ConfigureAwait(false);
                Publish(state); return Current;
            }
            catch
            {
                // A rejected scene must not strand ownership. If cleanup is temporarily
                // busy, mark the lease expired and let the bounded timer retry restoration.
                if (_lease != null)
                {
                    lock (_sync) { _lease = _lease with { Expired = true }; _overrides = null; }
                    try { await EndLeaseAsync(true).ConfigureAwait(false); }
                    catch (Exception ex) { Publish(error: ex.Message); }
                }
                throw;
            }
        }
        finally { _commands.Release(); }
    }

    public async Task<NativeControlState> RenewAsync(Guid leaseId, int? ttlSeconds = null, CancellationToken cancellationToken = default)
    {
        if (ttlSeconds.HasValue) ValidateTtl(ttlSeconds.Value);
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RequireLeaseAsync(leaseId).ConfigureAwait(false);
            lock (_sync)
            {
                var ttl = ttlSeconds ?? _lease!.TtlSeconds;
                _lease = _lease! with { TtlSeconds = ttl, ExpiresAt = _time.GetUtcNow().AddSeconds(ttl) };
                _renewedAt = _time.GetTimestamp();
            }
            Publish(); return Current;
        }
        finally { _commands.Release(); }
    }

    public async Task<NativeControlState> SelectSceneAsync(Guid leaseId, Guid sceneId, CancellationToken cancellationToken = default)
    {
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RequireLeaseAsync(leaseId).ConfigureAwait(false);
            var scene = await _scenes.LoadSceneAsync(leaseId, sceneId, cancellationToken).ConfigureAwait(false);
            await EnsureNotExpiredAsync().ConfigureAwait(false);
            Publish(scene); return Current;
        }
        finally { _commands.Release(); }
    }

    public async Task<NativeControlState> SetOverridesAsync(Guid leaseId, NativeAppearanceOverrides? overrides, CancellationToken cancellationToken = default)
    {
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RequireLeaseAsync(leaseId).ConfigureAwait(false);
            lock (_sync)
            {
                if (_lease!.AppearanceRevoked && overrides != null)
                    throw new NativeControlException("manual_override", "The user changed appearance preferences. Acquire a new lease before overriding them again.");
                _overrides = overrides;
            }
            Publish(); return Current;
        }
        finally { _commands.Release(); }
    }

    public async Task<NativeControlState> ReleaseAsync(Guid leaseId, bool restorePreviousScene = true, CancellationToken cancellationToken = default)
    {
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RequireLeaseAsync(leaseId).ConfigureAwait(false);
            await EndLeaseAsync(restorePreviousScene).ConfigureAwait(false);
            return Current;
        }
        finally { _commands.Release(); }
    }

    public async Task StopAsync()
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            _stopped = true; _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (_lease != null) await EndLeaseAsync(true).ConfigureAwait(false);
        }
        finally { _commands.Release(); }
    }

    /// <summary>Revoke ownership immediately when the native output is disabled; retry a busy restoration in the background.</summary>
    public async Task ReleaseForDisableAsync()
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_lease == null) return;
            lock (_sync) { _lease = _lease with { Expired = true }; _overrides = null; }
            Publish();
            try { await EndLeaseAsync(true).ConfigureAwait(false); }
            catch (Exception ex) { Publish(error: $"Previous scene restoration is pending: {ex.Message}"); }
        }
        finally { _commands.Release(); }
    }

    private static void ValidateTtl(int ttlSeconds)
    {
        if (ttlSeconds is < 1 or > 300) throw new NativeControlException("invalid_request", "Lease duration must be between 1 and 300 seconds.");
    }
    private void ThrowIfStopped()
    {
        if (_stopped || _disposed) throw new NativeControlException("unavailable", "Native control is stopping.");
    }
    private bool IsExpired() => _lease != null && (_lease.Expired || _time.GetElapsedTime(_renewedAt) >= TimeSpan.FromSeconds(_lease.TtlSeconds));
    private async Task RequireLeaseAsync(Guid id)
    {
        ThrowIfStopped();
        var expiredOwner = _lease?.Id == id && IsExpired();
        await ExpireIfNeededAsync().ConfigureAwait(false);
        if (expiredOwner) throw new NativeControlException("lease_expired", "The lease expired; acquire a new lease.");
        if (_lease == null || _lease.Id != id || _lease.Expired)
            throw new NativeControlException("lease_conflict", "The request does not own the current scene lease.");
    }
    private async Task EnsureNotExpiredAsync()
    {
        if (!IsExpired()) return;
        await ExpireIfNeededAsync().ConfigureAwait(false);
        throw new NativeControlException("lease_expired", "The lease expired during the scene transition.");
    }
    private async Task ExpireIfNeededAsync()
    {
        if (!IsExpired()) return;
        lock (_sync) { _lease = _lease! with { Expired = true }; _overrides = null; }
        Publish();
        await EndLeaseAsync(true).ConfigureAwait(false);
    }
    private async Task EndLeaseAsync(bool restore)
    {
        var id = _lease!.Id;
        var scene = await _scenes.EndTemporaryControlAsync(id, restore).ConfigureAwait(false);
        lock (_sync) { _lease = null; _overrides = null; }
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Publish(scene);
    }
    private async Task ExpireSafelyAsync()
    {
        if (_disposed || _stopped || !await _commands.WaitAsync(0).ConfigureAwait(false)) return;
        try { await ExpireIfNeededAsync().ConfigureAwait(false); }
        catch (Exception ex) { Publish(error: ex.Message); }
        finally { _commands.Release(); }
    }
    private void OnSceneChanged(object? sender, NativeSceneState scene) => Publish(scene);
    private void OnSavedSettingsChanged(object? sender, SignalRgbEffectSettings settings)
    {
        lock (_sync)
        {
            _overrides = null;
            if (_lease != null) _lease = _lease with { AppearanceRevoked = true };
        }
        Publish();
    }
    private void Publish(NativeSceneState? scene = null, string? error = null)
    {
        NativeControlState next;
        lock (_sync)
        {
            var appearance = _overrides?.Apply(_settings.Current) ?? _settings.Current;
            next = new(_current.Revision, _lease, scene ?? _current.Scene, appearance, error);
            if (next == _current) return;
            next = next with { Revision = checked(_current.Revision + 1) };
            _current = next;
        }
        Changed?.Invoke(this, next);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _timer.Dispose();
        _settings.Changed -= OnSavedSettingsChanged;
        _scenes.StateChanged -= OnSceneChanged;
        // StopAsync must be awaited before dispatcher/application shutdown for restoration.
        // Do not dispose the semaphore while an already queued timer/command can still exit it.
    }
}
