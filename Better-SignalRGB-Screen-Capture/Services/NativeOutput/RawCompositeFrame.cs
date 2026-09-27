using System.Buffers;

namespace Better_SignalRGB_Screen_Capture.Services.NativeOutput;

internal sealed record CompositeRenderSnapshot(StreamingSourceSnapshot[] Sources, object? Context = null);

/// <summary>A lease over two owned pooled planes. Retain before returning from an event callback.</summary>
internal sealed class RawCompositeFrame : IDisposable
{
    private sealed class Storage(byte[] pixels, byte[] coverage)
    {
        public readonly byte[] Pixels = pixels, Coverage = coverage;
        public int References = 1;
    }
    private Storage? _storage;
    public int Width { get; }
    public int Height { get; }
    public int Stride => checked(Width * 4);
    public int Length => checked(Stride * Height);
    public CompositeRenderSnapshot Snapshot { get; }
    public IReadOnlySet<Guid> ActiveSourceIds { get; }
    public long ComposedAtTicks { get; }
    public double CompositionMilliseconds { get; }
    public ReadOnlyMemory<byte> Pixels => (_storage ?? throw new ObjectDisposedException(nameof(RawCompositeFrame))).Pixels.AsMemory(0, Length);
    public ReadOnlyMemory<byte> Coverage => (_storage ?? throw new ObjectDisposedException(nameof(RawCompositeFrame))).Coverage.AsMemory(0, Length);

    internal RawCompositeFrame(byte[] pixels, byte[] coverage, int width, int height,
        CompositeRenderSnapshot snapshot, IReadOnlySet<Guid> activeSourceIds, long composedAtTicks, double compositionMilliseconds)
    {
        _storage = new(pixels, coverage); Width = width; Height = height; Snapshot = snapshot;
        ActiveSourceIds = activeSourceIds; ComposedAtTicks = composedAtTicks; CompositionMilliseconds = compositionMilliseconds;
    }
    private RawCompositeFrame(RawCompositeFrame source, Storage storage)
    {
        _storage = storage; Width = source.Width; Height = source.Height; Snapshot = source.Snapshot;
        ActiveSourceIds = source.ActiveSourceIds; ComposedAtTicks = source.ComposedAtTicks;
        CompositionMilliseconds = source.CompositionMilliseconds;
    }
    public RawCompositeFrame Retain()
    {
        // A lease is owned by one caller. Synchronize retain/dispose to prevent resurrection after pool return.
        lock (this)
        {
            var storage = _storage ?? throw new ObjectDisposedException(nameof(RawCompositeFrame));
            Interlocked.Increment(ref storage.References);
            return new(this, storage);
        }
    }
    public void Dispose()
    {
        Storage? storage;
        lock (this) { storage = _storage; _storage = null; }
        if (storage != null && Interlocked.Decrement(ref storage.References) == 0)
        {
            ArrayPool<byte>.Shared.Return(storage.Pixels, clearArray: true);
            ArrayPool<byte>.Shared.Return(storage.Coverage, clearArray: true);
        }
    }
}
