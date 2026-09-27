namespace Better_SignalRGB_Screen_Capture.Core.Helpers;

/// <summary>
/// Seekable write sink for the recorder's unused MP4 output. Stores only stream metadata,
/// so capture duration cannot grow a managed video buffer.
/// </summary>
public sealed class CaptureDiscardStream : Stream
{
    private long _length;
    private long _position;
    private bool _disposed;
    public override bool CanRead => false;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => !_disposed;
    public override long Length { get { ThrowIfDisposed(); return _length; } }
    public override long Position
    {
        get { ThrowIfDisposed(); return _position; }
        set { ThrowIfDisposed(); ArgumentOutOfRangeException.ThrowIfNegative(value); _position = value; }
    }
    public override void Flush() => ThrowIfDisposed();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        Position = checked(offset + (origin switch
        {
            SeekOrigin.Begin => 0,
            SeekOrigin.Current => _position,
            SeekOrigin.End => _length,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        }));
        return _position;
    }
    public override void SetLength(long value)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        _length = value;
    }
    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count) throw new ArgumentException("Invalid buffer range.");
        Advance(count);
    }
    public override void Write(ReadOnlySpan<byte> buffer) => Advance(buffer.Length);
    private void Advance(int count)
    {
        ThrowIfDisposed();
        _position = checked(_position + count);
        _length = Math.Max(_length, _position);
    }
    protected override void Dispose(bool disposing) { _disposed = true; base.Dispose(disposing); }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
