using System.Text;

namespace Better_SignalRGB_Screen_Capture.Services;

/// <summary>A latest-value broadcast slot. Slow readers skip frames without queuing JPEG buffers.</summary>
internal sealed class StreamingFrameState
{
    private readonly object _sync = new();
    private byte[]? _frame;
    private TaskCompletionSource _changed = CreateSignal();
    private bool _closed;
    private static TaskCompletionSource CreateSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Publish(byte[] frame)
    {
        TaskCompletionSource signal;
        lock (_sync)
        {
            if (_closed || ReferenceEquals(_frame, frame)) return;
            _frame = frame;
            signal = _changed;
            _changed = CreateSignal();
        }
        signal.TrySetResult();
    }

    public async Task<byte[]?> WaitForNextAsync(byte[]? previous, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (_sync)
            {
                if (_closed) return null;
                if (_frame != null && !ReferenceEquals(_frame, previous)) return _frame;
                changed = _changed.Task;
            }
            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public byte[]? Latest { get { lock (_sync) return _frame; } }
    public void Close()
    {
        lock (_sync) { _closed = true; _frame = null; _changed.TrySetResult(); }
    }
}

internal static class StreamingMultipartWriter
{
    public const string ContentType = "multipart/x-mixed-replace; boundary=mjpegboundary";
    private static readonly byte[] NewLine = "\r\n"u8.ToArray();

    public static async Task WriteAsync(Stream output, byte[] jpeg, CancellationToken cancellationToken)
    {
        var header = Encoding.ASCII.GetBytes($"--mjpegboundary\r\nContent-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n");
        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(jpeg, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(NewLine, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
