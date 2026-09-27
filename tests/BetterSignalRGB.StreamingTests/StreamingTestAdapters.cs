using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Models;

// Only the UI boundary and capture producer are substituted. Rendering, JPEG handling,
// lifecycle, HTTP routing, multipart framing and frame delivery use production files.
namespace Microsoft.UI.Xaml.Media.Imaging { public sealed class BitmapImage { } }

namespace Better_SignalRGB_Screen_Capture.Services
{
    internal static class StreamingCanvasSnapshot
    {
        public const int Width = 320, Height = 200;
        public static StreamingSourceSnapshot[] Sources = [];
        public static bool IsHighQuality;
        public static Task<StreamingWebCanvasSnapshot> CaptureWebAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new StreamingWebCanvasSnapshot(Sources, IsHighQuality ? 800 : 320, IsHighQuality ? 600 : 200));
        }
        public static Task<StreamingSourceSnapshot[]> CaptureAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Sources);
        }
    }
}

namespace BetterSignalRGB.StreamingTests
{
    internal sealed class CaptureProducer : ICaptureService
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte[]> _frames = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte[]> _signalFrames = new();
        public event EventHandler<SourceFrameEventArgs>? FrameAvailable;
        public event EventHandler<CaptureFailedEventArgs>? CaptureFailed;
        public void Publish(SourceItem source, byte[] jpeg, byte[]? signalJpeg = null)
        {
            _frames[source.Id] = jpeg;
            _signalFrames[source.Id] = signalJpeg ?? jpeg;
            FrameAvailable?.Invoke(this, new SourceFrameEventArgs(source, null) { FrameData = jpeg });
        }
        public void Fail(SourceItem source, string error)
        {
            _frames.TryRemove(source.Id, out _);
            _signalFrames.TryRemove(source.Id, out _);
            CaptureFailed?.Invoke(this, new CaptureFailedEventArgs(source, error));
        }
        public Task StartCaptureAsync(SourceItem source) => Task.CompletedTask;
        public Task StopCaptureAsync(SourceItem source) { _frames.TryRemove(source.Id, out _); _signalFrames.TryRemove(source.Id, out _); return Task.CompletedTask; }
        public Task StopAllCapturesAsync() { _frames.Clear(); _signalFrames.Clear(); return Task.CompletedTask; }
        public bool IsCapturing(SourceItem source) => _frames.ContainsKey(source.Id);
        public Task SetFrameRate(int fps) => Task.CompletedTask;
        public Task SetHighQuality(bool enabled) => Task.CompletedTask;
        public byte[]? GetLatestSignalRgbFrame(Guid sourceId) => _signalFrames.GetValueOrDefault(sourceId);
        public byte[]? GetMjpegFrame(Guid sourceId) => _frames.GetValueOrDefault(sourceId);
        public byte[]? GetMjpegFrame() => _frames.Values.FirstOrDefault();
    }
}
