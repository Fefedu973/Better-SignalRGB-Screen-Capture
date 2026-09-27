using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Models;
using System.Diagnostics;

namespace Better_SignalRGB_Screen_Capture.Services;

public sealed class SignalRgbEffectSettingsService(ILocalSettingsService settings) : ISignalRgbEffectSettingsService
{
    private const string SettingsKey = "SignalRgbEffect";
    private readonly SemaphoreSlim _access = new(1, 1);
    private readonly object _pendingGate = new();
    private PendingUpdate? _pending;
    private PendingUpdate? _inFlight;
    private bool _workerRunning;
    private bool _flushRequested;
    private TaskCompletionSource _scheduleChanged = NewCompletion();
    private bool _initialized;
    private SignalRgbEffectSettings _current = new();
    public SignalRgbEffectSettings Current => Volatile.Read(ref _current);
    public event EventHandler<SignalRgbEffectSettings>? Changed;

    public async Task InitializeAsync()
    {
        await _access.WaitAsync();
        try
        {
            if (_initialized) return;
            var stored = await settings.ReadSettingAsync<SignalRgbEffectSettings>(SettingsKey);
            _initialized = true;
            Publish((stored ?? new()).Normalize());
        }
        finally { _access.Release(); }
    }

    public Task UpdateAsync(SignalRgbEffectSettings value) => QueueUpdate(value, immediate: true);

    public Task ScheduleUpdateAsync(SignalRgbEffectSettings value) => QueueUpdate(value, immediate: false);

    private Task QueueUpdate(SignalRgbEffectSettings value, bool immediate)
    {
        ArgumentNullException.ThrowIfNull(value);
        value = value.Normalize();
        Task completion;
        bool startWorker;
        lock (_pendingGate)
        {
            // All edits in one debounce window await the same final persisted value.
            // An immediate update also replaces an older pending snapshot.
            _pending ??= new PendingUpdate();
            _pending.Value = value;
            _pending.ChangedAt = Stopwatch.GetTimestamp();
            completion = _pending.Completion.Task;
            _flushRequested |= immediate;
            WakeWorker();
            startWorker = !_workerRunning;
            _workerRunning = true;
        }
        if (startWorker) _ = ProcessScheduledUpdatesAsync();
        return completion;
    }

    public async Task FlushAsync()
    {
        while (true)
        {
            Task[] writes;
            lock (_pendingGate)
            {
                writes = new[] { _inFlight?.Completion.Task, _pending?.Completion.Task }
                    .OfType<Task>().ToArray();
                if (writes.Length == 0) return;
                _flushRequested = true;
                WakeWorker();
            }
            // Also drain edits received while an earlier disk write was in progress.
            await Task.WhenAll(writes).ConfigureAwait(false);
        }
    }

    private async Task ProcessScheduledUpdatesAsync()
    {
        while (true)
        {
            PendingUpdate? write;
            Task changed;
            TimeSpan delay;
            lock (_pendingGate)
            {
                if (_pending == null)
                {
                    _workerRunning = _flushRequested = false;
                    return;
                }
                delay = _flushRequested ? TimeSpan.Zero : TimeSpan.FromMilliseconds(200) - Stopwatch.GetElapsedTime(_pending.ChangedAt);
                changed = _scheduleChanged.Task;
                write = delay <= TimeSpan.Zero ? _pending : null;
                if (write != null)
                {
                    _pending = null;
                    _inFlight = write;
                }
            }
            if (write == null)
            {
                await Task.WhenAny(Task.Delay(delay), changed).ConfigureAwait(false);
                continue;
            }
            Exception? failure = null;
            try { await PersistAsync(write.Value).ConfigureAwait(false); }
            catch (Exception ex) { failure = ex; }
            lock (_pendingGate)
            {
                _inFlight = null;
                if (failure == null) write.Completion.TrySetResult();
                else write.Completion.TrySetException(failure);
            }
        }
    }

    private void WakeWorker()
    {
        _scheduleChanged.TrySetResult();
        _scheduleChanged = NewCompletion();
    }

    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class PendingUpdate
    {
        public SignalRgbEffectSettings Value = new();
        public long ChangedAt;
        public TaskCompletionSource Completion { get; } = NewCompletion();
    }

    private async Task PersistAsync(SignalRgbEffectSettings value)
    {
        await _access.WaitAsync();
        try
        {
            // Publish only the value that was successfully persisted. A failed write leaves
            // the effect and the next application launch on the same confirmed settings.
            await settings.SaveSettingAsync(SettingsKey, value);
            _initialized = true;
            Publish(value);
        }
        finally { _access.Release(); }
    }

    private void Publish(SignalRgbEffectSettings value)
    {
        if (value == _current) return;
        Volatile.Write(ref _current, value);
        Changed?.Invoke(this, value);
    }
}
