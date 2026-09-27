#nullable enable
namespace Better_SignalRGB_Screen_Capture.Core.Helpers;

/// <summary>Owns at most one waiting frame; replacing it releases the stale frame immediately.</summary>
public sealed class CaptureFrameMailbox<T> : IDisposable where T : class, IDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _available = new(0, 1);
    private T? _pending;
    private bool _completed;

    /// <returns>True only when an older waiting frame was replaced, not for shutdown rejection.</returns>
    public bool Publish(T frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        T? discarded;
        bool replaced;
        lock (_sync)
        {
            discarded = _completed ? frame : _pending;
            replaced = !_completed && discarded is not null;
            if (!_completed)
            {
                _pending = frame;
                if (discarded is null) _available.Release();
            }
        }
        discarded?.Dispose();
        return replaced;
    }

    // A single consumer owns each returned frame and must dispose it.
    public async ValueTask<T?> TakeAsync()
    {
        await _available.WaitAsync().ConfigureAwait(false);
        lock (_sync)
        {
            var frame = _pending;
            _pending = null;
            return frame;
        }
    }

    public void Dispose()
    {
        T? discarded;
        lock (_sync)
        {
            if (_completed) return;
            _completed = true;
            discarded = _pending;
            _pending = null;
            if (discarded is null) _available.Release();
        }
        discarded?.Dispose();
        // Do not dispose the semaphore while the consumer may still be awaiting it.
    }
}
