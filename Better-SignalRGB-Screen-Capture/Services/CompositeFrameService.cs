using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Threading.Channels;
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Models;

namespace Better_SignalRGB_Screen_Capture.Services;

/// <summary>On-demand, coalesced composite output. Source JPEGs are decoded only when they change.</summary>
public sealed class CompositeFrameService : ICompositeFrameService, IDisposable
{
    private readonly ICaptureService _captureService;
    private readonly ConcurrentDictionary<Guid, byte[]> _sourceFrames = new();
    private readonly Dictionary<Guid, (byte[] Bytes, Bitmap Image)> _decoded = new();
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _renderLock = new();
    private readonly Task _worker;
    private byte[]? _latestCompositeFrame;
    private Bitmap? _surface;
    private int _canvasWidth = StreamingCanvasSnapshot.Width;
    private int _canvasHeight = StreamingCanvasSnapshot.Height;
    private bool _disposed;
    private EventHandler<byte[]>? _frameAvailable;
    private static readonly Lazy<ImageCodecInfo> JpegEncoder = new(() => ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid));

    public event EventHandler<byte[]>? CompositeFrameAvailable
    {
        add { lock (_renderLock) _frameAvailable += value; _changes.Writer.TryWrite(true); }
        remove { lock (_renderLock) _frameAvailable -= value; }
    }

    public CompositeFrameService(ICaptureService captureService)
    {
        _captureService = captureService;
        captureService.FrameAvailable += OnFrameAvailable;
        captureService.CaptureFailed += OnCaptureFailed;
        _worker = Task.Run(RenderLoopAsync);
    }

    private void OnFrameAvailable(object? sender, SourceFrameEventArgs args)
    {
        if (args.FrameData is { Length: > 0 } frame) UpdateSourceFrame(args.Source, frame);
    }

    private void OnCaptureFailed(object? sender, CaptureFailedEventArgs args) => RemoveSource(args.Source);

    public void UpdateSourceFrame(SourceItem source, byte[] frameData)
    {
        if (_disposed || frameData.Length == 0) return;
        _sourceFrames[source.Id] = frameData;
        // The application streams individual JPEGs. Do not decode/composite them without a composite consumer.
        if (_frameAvailable != null) _changes.Writer.TryWrite(true);
    }

    public void RemoveSource(SourceItem source)
    {
        lock (_renderLock)
        {
            _sourceFrames.TryRemove(source.Id, out _);
            if (_decoded.Remove(source.Id, out var decoded)) decoded.Image.Dispose();
            Volatile.Write(ref _latestCompositeFrame, null);
        }
        _changes.Writer.TryWrite(true);
    }

    public void SetCanvasSize(int width, int height)
    {
        if (width is < 1 or > 8192 || height is < 1 or > 8192) throw new ArgumentOutOfRangeException(nameof(width));
        lock (_renderLock)
        {
            if (_canvasWidth == width && _canvasHeight == height) return;
            _canvasWidth = width; _canvasHeight = height;
            Volatile.Write(ref _latestCompositeFrame, null);
        }
        if (_frameAvailable != null) _changes.Writer.TryWrite(true);
    }

    public byte[]? GetLatestCompositeFrame()
    {
        _changes.Writer.TryWrite(true);
        return Volatile.Read(ref _latestCompositeFrame);
    }

    public void InvalidateLayout()
    {
        if (_frameAvailable != null) _changes.Writer.TryWrite(true);
    }

    private async Task RenderLoopAsync()
    {
        var token = _cancellation.Token;
        try
        {
            while (await _changes.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                await Task.Delay(TimeSpan.FromSeconds(1d / 30), token).ConfigureAwait(false);
                while (_changes.Reader.TryRead(out _)) { }
                try
                {
                    var layout = await StreamingCanvasSnapshot.CaptureAsync(token);
                    byte[] frame;
                    lock (_renderLock)
                    {
                        if (_disposed) return;
                        frame = Render(layout);
                        // Publish under the same lock as invalidation: a frame rendered just
                        // before a failure must never be republished after its cache is cleared.
                        Volatile.Write(ref _latestCompositeFrame, frame);
                        _frameAvailable?.Invoke(this, frame);
                    }
                }
                catch (Exception exception) when (!token.IsCancellationRequested)
                { Debug.WriteLine($"Composite rendering failed: {exception.Message}"); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private byte[] Render(StreamingSourceSnapshot[] sources)
    {
        if (_surface == null || _surface.Width != _canvasWidth || _surface.Height != _canvasHeight)
        {
            _surface?.Dispose();
            _surface = new Bitmap(_canvasWidth, _canvasHeight, PixelFormat.Format24bppRgb);
        }
        var ids = sources.Select(source => source.Id).ToHashSet();
        foreach (var id in _decoded.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            _decoded[id].Image.Dispose();
            _decoded.Remove(id);
            _sourceFrames.TryRemove(id, out _);
        }
        using var graphics = Graphics.FromImage(_surface);
        graphics.Clear(Color.Black);
        var highQuality = _canvasWidth > StreamingCanvasSnapshot.Width || _canvasHeight > StreamingCanvasSnapshot.Height;
        graphics.InterpolationMode = highQuality ? InterpolationMode.HighQualityBicubic : InterpolationMode.Bilinear;
        graphics.CompositingQuality = highQuality ? CompositingQuality.HighQuality : CompositingQuality.HighSpeed;
        // The model and SignalRGB keep their canonical geometry. Scale the final
        // scene, including rotated masks, rather than rounding each source twice.
        graphics.ScaleTransform((float)_canvasWidth / StreamingCanvasSnapshot.Width,
            (float)_canvasHeight / StreamingCanvasSnapshot.Height);
        foreach (var source in sources)
        {
            if (source.CanvasWidth <= 0 || source.CanvasHeight <= 0 || source.Opacity <= 0 ||
                source.CropLeftPct + source.CropRightPct >= 1 || source.CropTopPct + source.CropBottomPct >= 1) continue;
            if (!_sourceFrames.TryGetValue(source.Id, out var bytes)) continue;
            try
            {
                if (!_decoded.TryGetValue(source.Id, out var decoded) || !ReferenceEquals(bytes, decoded.Bytes))
                {
                    // Image.FromStream requires its stream to live as long as the image; detach using a Bitmap copy.
                    using var stream = new MemoryStream(bytes, writable: false);
                    using var image = Image.FromStream(stream);
                    var bitmap = new Bitmap(image);
                    decoded.Image?.Dispose();
                    decoded = (bytes, bitmap);
                    _decoded[source.Id] = decoded;
                }
                var saved = graphics.Save();
                try
                {
                    graphics.TranslateTransform(source.CanvasX + source.CanvasWidth / 2f, source.CanvasY + source.CanvasHeight / 2f);
                    graphics.RotateTransform(source.Rotation);
                    graphics.ScaleTransform(source.IsMirroredHorizontally ? -1 : 1, source.IsMirroredVertically ? -1 : 1);
                    graphics.TranslateTransform(-source.CanvasWidth / 2f, -source.CanvasHeight / 2f);
                    // Clip, never shrink the destination rectangle when a source extends outside the canvas.
                    graphics.SetClip(new Rectangle(0, 0, source.CanvasWidth, source.CanvasHeight), CombineMode.Intersect);
                    using var crop = new GraphicsPath();
                    crop.AddPolygon(source.GetCropPolygon());
                    graphics.SetClip(crop, CombineMode.Intersect);
                    using var attributes = new ImageAttributes();
                    if (highQuality) attributes.SetWrapMode(WrapMode.TileFlipXY);
                    attributes.SetColorMatrix(new ColorMatrix { Matrix33 = (float)Math.Clamp(source.Opacity, 0, 1) });
                    graphics.DrawImage(decoded.Image, new Rectangle(0, 0, source.CanvasWidth, source.CanvasHeight),
                        0, 0, decoded.Image.Width, decoded.Image.Height, GraphicsUnit.Pixel, attributes);
                }
                finally { graphics.Restore(saved); }
            }
            catch (ArgumentException exception) { Debug.WriteLine($"Invalid source JPEG: {exception.Message}"); }
        }
        using var output = new MemoryStream();
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, highQuality ? 92L : 70L);
        _surface.Save(output, JpegEncoder.Value, parameters);
        return output.ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _captureService.FrameAvailable -= OnFrameAvailable;
        _captureService.CaptureFailed -= OnCaptureFailed;
        _cancellation.Cancel();
        _changes.Writer.TryComplete();
        lock (_renderLock)
        {
            _surface?.Dispose();
            _surface = null;
            foreach (var decoded in _decoded.Values) decoded.Image.Dispose();
            _decoded.Clear();
            _sourceFrames.Clear();
        }
        _ = _worker.ContinueWith(_ => _cancellation.Dispose(), TaskScheduler.Default);
    }
}
