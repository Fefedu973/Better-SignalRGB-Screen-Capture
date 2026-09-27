using Better_SignalRGB_Screen_Capture.Helpers;
using Microsoft.UI.Xaml.Controls;
using ScreenRecorderLib;

namespace Better_SignalRGB_Screen_Capture.Views;

public sealed partial class AddSourceDialog
{
    private static void SelectDevice(ComboBox combo, string? savedId, string unavailableLabel)
    {
        var selected = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
            !string.IsNullOrWhiteSpace(savedId) && string.Equals(item.Tag as string, savedId, StringComparison.OrdinalIgnoreCase));
        if (selected == null && !string.IsNullOrWhiteSpace(savedId))
        {
            selected = new ComboBoxItem { Content = unavailableLabel, Tag = savedId };
            combo.Items.Add(selected);
        }
        combo.SelectedItem = selected ?? combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.IsEnabled);
        combo.IsEnabled = combo.Items.OfType<ComboBoxItem>().Any(item => item.IsEnabled);
    }

    private async void WebcamCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WebcamFormatCombo == null || _isClosed) return;
        var revision = ++_webcamFormatRevision;
        var deviceId = (WebcamCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        var desiredFormat = string.Equals(deviceId, SelectedWebcamDeviceId, StringComparison.OrdinalIgnoreCase)
            ? SelectedWebcamFormatId : null;
        SelectedWebcamDeviceId = deviceId;
        SelectedWebcamFormatId = desiredFormat;
        WebcamFormatCombo.IsEnabled = false;
        WebcamFormatCombo.Items.Clear();
        _loadingWebcamFormats = true;
        WebcamFormatStatus.Text = "Loading camera formats…";
        try
        {
            var formats = string.IsNullOrWhiteSpace(deviceId) ? [] : await Task.Run(() =>
                Recorder.GetSupportedVideoCaptureFormatsForDevice(deviceId).Select(format => new
                {
                    Id = CaptureSourceFactory.GetWebcamFormatId(format),
                    Label = $"{format.FrameSize.Width} × {format.FrameSize.Height} at {format.Framerate:0.##} fps — {format.VideoFormatName}"
                }).DistinctBy(format => format.Id).ToArray());
            if (_isClosed || revision != _webcamFormatRevision) return;
            WebcamFormatCombo.Items.Add(new ComboBoxItem { Content = "Auto (camera default)", Tag = null });
            foreach (var format in formats)
                WebcamFormatCombo.Items.Add(new ComboBoxItem { Content = format.Label, Tag = format.Id });
            SelectDevice(WebcamFormatCombo, desiredFormat, "Saved camera format (currently unavailable)");
            WebcamFormatStatus.Text = "The app capture frame rate is a separate limit; choosing a camera format does not change it.";
        }
        catch (Exception ex)
        {
            if (_isClosed || revision != _webcamFormatRevision) return;
            WebcamFormatCombo.Items.Add(new ComboBoxItem { Content = "Auto (camera default)", Tag = null });
            SelectDevice(WebcamFormatCombo, desiredFormat, "Saved camera format (currently unavailable)");
            WebcamFormatStatus.Text = $"Could not list camera formats: {ex.Message}";
        }
        finally
        {
            if (revision == _webcamFormatRevision) _loadingWebcamFormats = false;
        }
    }
}
