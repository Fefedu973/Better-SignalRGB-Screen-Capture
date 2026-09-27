using System.Buffers;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Core.Helpers;
using Better_SignalRGB_Screen_Capture.Helpers;
using Better_SignalRGB_Screen_Capture.Models;
using Microsoft.Graphics.Canvas;
using ScreenRecorderLib;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using WinRT;
using Rect = Windows.Foundation.Rect;

namespace Better_SignalRGB_Screen_Capture.Services;

/// <summary>Capture HDR before quantization. No H.264 sink/carrier is needed for screen previews.</summary>
public sealed class GraphicsCaptureFactory : IGraphicsCaptureFactory
{
    public IGraphicsFrameCapture Create(SourceItem source, int width, int height, int frameRate) =>
        new GraphicsFrameCapture(source, width, height, frameRate);
}

internal sealed class GraphicsFrameCapture(SourceItem source, int width, int height, int frameRate) : IGraphicsFrameCapture
{
    private const DirectXPixelFormat Format = DirectXPixelFormat.R16G16B16A16Float;
    private sealed record Plan(nint Handle, bool IsWindow, Rectangle? Crop, Rectangle Destination);

    public async Task RunAsync(Action<byte[], int, int> onFrame, Action<CaptureColorInfo> onColor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("Windows Graphics Capture is unavailable.");
        if (width is < 1 or > CaptureGeometry.MaximumDimension || height is < 1 or > CaptureGeometry.MaximumDimension ||
            (long)width * height > CaptureGeometry.MaximumPixels)
            throw new ArgumentException("Invalid graphics capture output size.");
        using var device = new CanvasDevice();
        if (!device.IsPixelFormatSupported(Format)) throw new NotSupportedException("This graphics device cannot preserve HDR capture precision.");
        var tiles = new List<Tile>();
        try
        {
            foreach (var plan in BuildPlans())
            {
                cancellationToken.ThrowIfCancellationRequested();
                tiles.Add(new Tile(device, plan));
            }
            var firstFrame = Stopwatch.GetTimestamp();
            var delivered = false;
            CaptureColorInfo? lastInfo = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var started = Stopwatch.GetTimestamp();
                var changed = false;
                foreach (var tile in tiles) changed |= tile.Update();
                var colors = tiles.Select(tile => tile.Color).ToArray();
                var mode = colors.Any(color => !CaptureColorPolicy.Resolve(color.HdrEnabled, color.SdrWhiteNits).IsKnown) ? CaptureColorMode.Unknown :
                    colors.Any(color => color.HdrEnabled == true) ? CaptureColorMode.HdrToneMapped : CaptureColorMode.Sdr;
                var whites = colors.Select(color => color.SdrWhiteNits).Distinct().ToArray();
                var info = new CaptureColorInfo(mode, "WGC FP16 → sRGB", whites.Length == 1 ? whites[0] : null);
                if (lastInfo != info) { onColor(info); lastInfo = info; }
                // A region's first image must include every intersecting monitor; the
                // one-shot region preview also consumes this first complete image.
                if (changed && tiles.All(tile => tile.HasFrame))
                {
                    byte[]? pixels = ArrayPool<byte>.Shared.Rent(checked(width * height * 4));
                    try
                    {
                        var span = pixels.AsSpan(0, width * height * 4);
                        span.Clear();
                        for (var i = 3; i < span.Length; i += 4) span[i] = 255;
                        foreach (var tile in tiles) tile.CopyTo(pixels, width);
                        onFrame(pixels, width, height);
                        pixels = null;
                        delivered = true;
                    }
                    finally { if (pixels != null) ArrayPool<byte>.Shared.Return(pixels); }
                }
                if (!delivered && Stopwatch.GetElapsedTime(firstFrame) > TimeSpan.FromSeconds(10))
                    throw new InvalidOperationException("The screen/window did not provide a capture frame. Check whether it is available and not minimized.");
                var delay = TimeSpan.FromSeconds(1d / Math.Clamp(frameRate, 1, 60)) - Stopwatch.GetElapsedTime(started);
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { foreach (var tile in tiles) tile.Dispose(); }
    }

    private IEnumerable<Plan> BuildPlans()
    {
        var destination = new Rectangle(0, 0, width, height);
        if (source.Type == SourceType.Process)
        {
            var selected = (WindowRecordingSource)CaptureSourceFactory.Create(source).Single();
            yield return new(selected.Handle, true, null, destination);
        }
        else if (source.Type == SourceType.Monitor)
        {
            var selected = (DisplayRecordingSource)CaptureSourceFactory.Create(source).Single();
            var monitor = DisplayColorInfo.ForDeviceName(selected.DeviceName);
            if (monitor.MonitorHandle == 0) throw new InvalidOperationException("The selected display is no longer available.");
            yield return new(monitor.MonitorHandle, false, null, destination);
        }
        else if (source.Type == SourceType.Region && source.RegionBounds is { Width: > 0, Height: > 0 } region)
        {
            if ((long)region.X + region.Width > int.MaxValue || (long)region.Y + region.Height > int.MaxValue)
                throw new ArgumentException("The capture region exceeds desktop coordinate bounds.");
            var rectangle = new Rectangle(region.X, region.Y, region.Width, region.Height);
            var found = false;
            foreach (var monitor in DisplayColorInfo.Enumerate())
            {
                var overlap = Rectangle.Intersect(rectangle, monitor.Bounds);
                if (overlap.Width <= 0 || overlap.Height <= 0) continue;
                int X(long x) => (int)Math.Round((x - rectangle.Left) * (double)width / rectangle.Width);
                int Y(long y) => (int)Math.Round((y - rectangle.Top) * (double)height / rectangle.Height);
                var target = Rectangle.FromLTRB(X(overlap.Left), Y(overlap.Top), X(overlap.Right), Y(overlap.Bottom));
                if (target.Width < 1 || target.Height < 1) continue;
                found = true;
                yield return new(monitor.MonitorHandle, false,
                    new Rectangle(overlap.Left - monitor.Bounds.Left, overlap.Top - monitor.Bounds.Top, overlap.Width, overlap.Height), target);
            }
            if (!found) throw new InvalidOperationException("No display intersects the selected capture region.");
        }
        else throw new InvalidOperationException("This source does not use screen/window graphics capture.");
    }

    private sealed class Tile : IDisposable
    {
        private readonly CanvasDevice _device;
        private readonly Plan _plan;
        private GraphicsCaptureItem? _item;
        private Direct3D11CaptureFramePool? _pool;
        private GraphicsCaptureSession? _session;
        private CanvasRenderTarget? _scaled;
        private SizeInt32 _poolSize;
        private byte[]? _linear, _pixels;
        private Windows.Storage.Streams.IBuffer? _readback;
        private int _closed;
        public DisplayColorState Color { get; private set; }
        public bool HasFrame => _pixels != null;

        public Tile(CanvasDevice device, Plan plan)
        {
            _device = device; _plan = plan;
            Color = ReadColor();
            try
            {
                _item = CaptureItemInterop.Create(plan.Handle, plan.IsWindow);
                _poolSize = _item.Size;
                ValidateCaptureSize(_poolSize);
                _item.Closed += OnClosed;
                _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, Format, 2, _poolSize);
                _session = _pool.CreateCaptureSession(_item);
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) _session.IsCursorCaptureEnabled = true;
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
                {
                    try { _session.IsBorderRequired = false; }
                    catch (UnauthorizedAccessException) { /* Windows may require its capture border. */ }
                }
                _scaled = new CanvasRenderTarget(device, plan.Destination.Width, plan.Destination.Height, 96, Format, CanvasAlphaMode.Ignore);
                _session.StartCapture();
            }
            catch { Dispose(); throw; }
        }

        private DisplayColorState ReadColor() => _plan.IsWindow ? DisplayColorInfo.ForWindow(_plan.Handle) : DisplayColorInfo.ForMonitor(_plan.Handle);
        private void OnClosed(GraphicsCaptureItem sender, object args) => Interlocked.Exchange(ref _closed, 1);
        public bool Update()
        {
            if (Volatile.Read(ref _closed) != 0) throw new InvalidOperationException("The captured screen/window closed.");
            var previous = Color;
            Color = ReadColor();
            var changed = previous.HdrEnabled != Color.HdrEnabled || previous.SdrWhiteNits != Color.SdrWhiteNits;
            SizeInt32? resize = null;
            Direct3D11CaptureFrame? latest = null;
            try
            {
                // Drain a bounded number: high-refresh displays cannot monopolize this worker.
                for (var i = 0; i < 4; i++)
                {
                    var next = _pool!.TryGetNextFrame();
                    if (next == null) break;
                    latest?.Dispose(); latest = next;
                }
                if (latest != null)
                {
                    ValidateCaptureSize(latest.ContentSize);
                    if (latest.ContentSize.Width != _poolSize.Width || latest.ContentSize.Height != _poolSize.Height)
                        resize = latest.ContentSize;
                    else
                    {
                        ReadFrame(latest);
                        changed = true;
                    }
                }
            }
            finally { latest?.Dispose(); }
            // Never recreate a pool while a checked-out frame or a bitmap wrapper still owns it.
            if (resize is { } size) { _pool!.Recreate(_device, Format, 2, size); _poolSize = size; }
            if (changed && _linear != null)
            {
                _pixels ??= new byte[checked(_plan.Destination.Width * _plan.Destination.Height * 4)];
                var color = CaptureColorPolicy.Resolve(Color.HdrEnabled, Color.SdrWhiteNits);
                HdrToneMapper.ConvertRgba16Float(_linear, _plan.Destination.Width, _plan.Destination.Height,
                    checked(_plan.Destination.Width * 8), _pixels, color.ToneMapHdr, color.SdrWhiteNits);
            }
            return changed && _pixels != null;
        }
        private void ReadFrame(Direct3D11CaptureFrame frame)
        {
            using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(_device, frame.Surface, 96, CanvasAlphaMode.Ignore);
            var valid = new Rectangle(0, 0, Math.Min(frame.ContentSize.Width, (int)bitmap.SizeInPixels.Width),
                Math.Min(frame.ContentSize.Height, (int)bitmap.SizeInPixels.Height));
            var requested = _plan.Crop ?? valid;
            var crop = Rectangle.Intersect(requested, valid);
            using (var drawing = _scaled!.CreateDrawingSession())
            {
                drawing.Clear(Windows.UI.Color.FromArgb(255, 0, 0, 0));
                if (crop.Width > 0 && crop.Height > 0)
                {
                    var target = new Rect((crop.X - requested.X) * (double)_plan.Destination.Width / requested.Width,
                        (crop.Y - requested.Y) * (double)_plan.Destination.Height / requested.Height,
                        crop.Width * (double)_plan.Destination.Width / requested.Width,
                        crop.Height * (double)_plan.Destination.Height / requested.Height);
                    drawing.DrawImage(bitmap, target, new Rect(crop.X, crop.Y, crop.Width, crop.Height), 1, CanvasImageInterpolation.Linear);
                }
            }
            _linear ??= new byte[checked(_plan.Destination.Width * _plan.Destination.Height * 8)];
            _readback ??= _linear.AsBuffer();
            _scaled.GetPixelBytes(_readback);
        }
        public void CopyTo(byte[] target, int targetWidth)
        {
            if (_pixels == null) return;
            var row = _plan.Destination.Width * 4;
            for (var y = 0; y < _plan.Destination.Height; y++)
                _pixels.AsSpan(y * row, row).CopyTo(target.AsSpan(((_plan.Destination.Top + y) * targetWidth + _plan.Destination.Left) * 4, row));
        }
        public void Dispose()
        {
            // Device loss must not prevent the remaining native resources from being released.
            try { if (_item != null) _item.Closed -= OnClosed; }
            catch (Exception ex) { Debug.WriteLine($"Capture item cleanup: {ex.Message}"); }
            Release(_session); _session = null;
            Release(_pool); _pool = null;
            Release(_scaled); _scaled = null;
            _item = null; _readback = null; _linear = _pixels = null;
        }
        private static void Release(IDisposable? resource)
        {
            try { resource?.Dispose(); }
            catch (Exception ex) { Debug.WriteLine($"Graphics capture cleanup: {ex.Message}"); }
        }
    }

    private static void ValidateCaptureSize(SizeInt32 size)
    {
        if (size.Width < 1 || size.Height < 1 || size.Width > 16384 || size.Height > 16384 || (long)size.Width * size.Height > 64 * 1024 * 1024)
            throw new InvalidOperationException("The capture surface exceeds its graphics-memory dimensions budget.");
    }
}

internal static class CaptureItemInterop
{
    public static GraphicsCaptureItem Create(nint handle, bool window)
    {
        if (handle == 0) throw new ArgumentException("A capture target handle is required.");
        const string runtimeClass = "Windows.Graphics.Capture.GraphicsCaptureItem";
        nint name = 0, factory = 0, item = 0;
        try
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(runtimeClass, runtimeClass.Length, out name));
            var interopId = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(name, ref interopId, out factory));
            var create = Marshal.GetDelegateForFunctionPointer<CreateItem>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), (window ? 3 : 4) * IntPtr.Size));
            var itemId = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            Marshal.ThrowExceptionForHR(create(factory, handle, ref itemId, out item));
            return MarshalInterface<GraphicsCaptureItem>.FromAbi(item);
        }
        finally
        {
            if (item != 0) Marshal.Release(item);
            if (factory != 0) Marshal.Release(factory);
            if (name != 0) WindowsDeleteString(name);
        }
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateItem(nint factory, nint target, ref Guid id, out nint item);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string value, int length, out nint result);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint name, ref Guid id, out nint result);
}
