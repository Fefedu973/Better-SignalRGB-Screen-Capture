namespace Better_SignalRGB_Screen_Capture.Services;

/// <summary>
/// Atomically owns source streams and their availability. Failed sources cannot be
/// reopened from an old cached JPEG; only a new producer frame makes them available.
/// </summary>
internal sealed class StreamingSourceFrames
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, StreamingFrameState> _frames = new();
    private readonly HashSet<Guid> _unavailable = new();

    public void Publish(Guid id, byte[] frame)
    {
        lock (_sync)
        {
            _unavailable.Remove(id);
            GetOrCreate(id).Publish(frame);
        }
    }

    public void Recover(Guid id)
    {
        lock (_sync) _unavailable.Remove(id);
    }

    public void Invalidate(Guid id)
    {
        lock (_sync)
        {
            _unavailable.Add(id);
            if (_frames.Remove(id, out var frame)) frame.Close();
        }
    }

    public bool IsUnavailable(Guid id)
    {
        lock (_sync) return _unavailable.Contains(id);
    }

    public StreamingSourceSnapshot[] FilterAvailable(StreamingSourceSnapshot[] sources)
    {
        lock (_sync) return sources.Where(source => !_unavailable.Contains(source.Id)).ToArray();
    }

    public StreamingFrameState? Open(Guid id, Func<Guid, byte[]?> getCapturedFrame)
    {
        lock (_sync)
        {
            if (_unavailable.Contains(id)) return null;
            var state = GetOrCreate(id);
            if (state.Latest == null && getCapturedFrame(id) is { } frame) state.Publish(frame);
            return state;
        }
    }

    public byte[]? Latest(Guid id, Func<Guid, byte[]?> getCapturedFrame)
    {
        lock (_sync)
        {
            if (_unavailable.Contains(id)) return null;
            return _frames.TryGetValue(id, out var state) ? state.Latest : getCapturedFrame(id);
        }
    }

    public void CloseStreams()
    {
        lock (_sync)
        {
            foreach (var state in _frames.Values) state.Close();
            _frames.Clear();
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            CloseStreams();
            _unavailable.Clear();
        }
    }

    private StreamingFrameState GetOrCreate(Guid id)
    {
        if (!_frames.TryGetValue(id, out var state)) _frames[id] = state = new StreamingFrameState();
        return state;
    }
}
