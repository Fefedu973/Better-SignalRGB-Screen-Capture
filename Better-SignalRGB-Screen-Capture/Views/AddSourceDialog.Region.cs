using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics;

namespace Better_SignalRGB_Screen_Capture.Views;

public sealed partial class AddSourceDialog
{
    private CancellationTokenSource? _regionPreviewCancellation;

    private async Task AnalyzeRegionAndTakeScreenshotAsync(RectInt32 region)
    {
        _regionPreviewCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _regionPreviewCancellation = cancellation;
        ScreenshotBorder.Visibility = Visibility.Collapsed;
        RegionMonitorsText.Text = "Loading selected region preview…";
        try
        {
            var bytes = await RegionPreviewCapture.CaptureAsync(new(region.X, region.Y, region.Width, region.Height),
                cancellation.Token, App.GetService<IGraphicsCaptureFactory>());
            if (_isClosed || cancellation.IsCancellationRequested) return;
            var image = new BitmapImage();
            using var stream = new MemoryStream(bytes).AsRandomAccessStream();
            await image.SetSourceAsync(stream);
            if (_isClosed || cancellation.IsCancellationRequested) return;
            ScreenshotImage.Source = image;
            ScreenshotBorder.Visibility = Visibility.Visible;
            RegionMonitorsText.Text = "Preview of the selected capture area.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_isClosed && !cancellation.IsCancellationRequested)
                RegionMonitorsText.Text = $"Preview unavailable: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_regionPreviewCancellation, cancellation)) _regionPreviewCancellation = null;
            cancellation.Dispose();
        }
    }
}
