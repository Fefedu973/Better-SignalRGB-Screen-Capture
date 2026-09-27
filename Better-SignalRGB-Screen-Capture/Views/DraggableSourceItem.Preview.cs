using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Core.Helpers;
using Better_SignalRGB_Screen_Capture.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;

namespace Better_SignalRGB_Screen_Capture.Views;

public sealed partial class DraggableSourceItem
{
    private volatile SourceItem? _previewSource;
    private volatile bool _previewEnabled;
    private int _isUpdatingFrame;
    private int _previewGeneration;
    private readonly BitmapImage _previewBitmap = new();

    private void OnFrameAvailable(object? sender, SourceFrameEventArgs e)
    {
        // Capture callbacks run on worker threads. Never read dependency properties here.
        var source = _previewSource;
        var generation = Volatile.Read(ref _previewGeneration);
        if (!_previewEnabled || source == null || e.Source.Id != source.Id ||
            e.FrameData is not { Length: > 0 } ||
            Interlocked.CompareExchange(ref _isUpdatingFrame, 1, 0) != 0) return;

        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (!IsLoaded || !_previewEnabled || !ReferenceEquals(source, Source) || generation != Volatile.Read(ref _previewGeneration)) return;
                // The encoded buffer is immutable and owned by this frame. Adapt the stream
                // directly instead of copying it through a DataWriter for every preview.
                using var stream = new MemoryStream(e.FrameData, writable: false);
                using var randomAccess = stream.AsRandomAccessStream();
                if (!PreviewDecodeGeometry.TryGetJpegSize(e.FrameData, out var width, out var height)) return;
                var xBasis = new Point(1, 0);
                var yBasis = new Point(0, 1);
                if (XamlRoot?.Content is UIElement root)
                {
                    var transform = PreviewImage.TransformToVisual(root);
                    var origin = transform.TransformPoint(default);
                    var horizontal = transform.TransformPoint(new Point(1, 0));
                    var vertical = transform.TransformPoint(new Point(0, 1));
                    xBasis = new Point(horizontal.X - origin.X, horizontal.Y - origin.Y);
                    yBasis = new Point(vertical.X - origin.X, vertical.Y - origin.Y);
                }
                _previewBitmap.DecodePixelType = DecodePixelType.Physical;
                _previewBitmap.DecodePixelHeight = PreviewDecodeGeometry.DecodeHeight(width, height,
                    PreviewImage.ActualWidth > 0 ? PreviewImage.ActualWidth : ActualWidth,
                    PreviewImage.ActualHeight > 0 ? PreviewImage.ActualHeight : ActualHeight,
                    xBasis.X, xBasis.Y, yBasis.X, yBasis.Y, XamlRoot?.RasterizationScale ?? 1);
                await _previewBitmap.SetSourceAsync(randomAccess);
                if (IsLoaded && _previewEnabled && ReferenceEquals(source, Source) && generation == Volatile.Read(ref _previewGeneration))
                {
                    PreviewImage.Source = _previewBitmap;
                    UpdatePreviewPlaceholder();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error displaying frame: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                Volatile.Write(ref _isUpdatingFrame, 0);
            }
        })) Volatile.Write(ref _isUpdatingFrame, 0);
    }

    private void RefreshPreviewState()
    {
        Interlocked.Increment(ref _previewGeneration);
        var source = Source;
        _previewSource = source;
        _previewEnabled = IsLoaded && source?.IsLivePreviewEnabled == true;
        PreviewBorder.Visibility = _previewEnabled ? Visibility.Visible : Visibility.Collapsed;
        UpdatePreviewPlaceholder();
        if (_previewEnabled && source != null && _captureService?.GetMjpegFrame(source.Id) is { Length: > 0 } frame)
        {
            // Reopening the editor or enabling preview uses the production frame immediately.
            // Website sources follow this same path; their browser belongs to the capture session.
            OnFrameAvailable(_captureService, new SourceFrameEventArgs(source, null) { FrameData = frame });
        }
    }

    private void UpdatePreviewPlaceholder()
    {
        var hasPreview = _previewEnabled && PreviewImage.Source != null;
        // The label and source icon are placeholders, not part of the captured image.
        ContentBorder.Visibility = hasPreview ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnPreviewCaptureFailed(object? sender, CaptureFailedEventArgs args)
    {
        if (_previewSource?.Id != args.Source.Id) return;
        var generation = Interlocked.Increment(ref _previewGeneration);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (IsLoaded && Source?.Id == args.Source.Id && generation == Volatile.Read(ref _previewGeneration))
            {
                PreviewImage.Source = null;
                UpdatePreviewPlaceholder();
            }
        });
    }

    private void DetachPreview()
    {
        Interlocked.Increment(ref _previewGeneration);
        _previewEnabled = false;
        _previewSource = null;
        if (_captureService == null) return;
        _captureService.FrameAvailable -= OnFrameAvailable;
        _captureService.CaptureFailed -= OnPreviewCaptureFailed;
    }
}
