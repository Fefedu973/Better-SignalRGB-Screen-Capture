using Better_SignalRGB_Screen_Capture.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Better_SignalRGB_Screen_Capture.Views;

public sealed partial class AddSourceDialog
{
    private int _wallpaperStatusGeneration;

    private void MonitorCombo_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if ((KindBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "WallpaperEngine")
            _ = RefreshWallpaperEngineStatusAsync();
    }

    private async void CheckWallpaperEngine_Click(object sender, RoutedEventArgs args) =>
        await RefreshWallpaperEngineStatusAsync();

    private async Task RefreshWallpaperEngineStatusAsync()
    {
        if (_isClosed || WallpaperEngineStatus == null) return;
        var generation = ++_wallpaperStatusGeneration;
        var device = (MonitorCombo?.SelectedItem as ComboBoxItem)?.Tag as string;
        if (string.IsNullOrWhiteSpace(device))
        {
            WallpaperEngineStatus.Text = "Select a monitor to check its live wallpaper.";
            return;
        }
        WallpaperEngineStatus.Text = "Checking the selected monitor…";
        string status;
        try
        {
            var available = await Task.Run(() => WallpaperEngineSourceDiscovery.IsAvailable(device));
            status = available
                ? "Live wallpaper surface found on this monitor."
                : "No Wallpaper Engine surface is available on this monitor. Start Wallpaper Engine and apply a wallpaper, then check again. You can save this source and reconnect later.";
        }
        catch (Exception exception) { status = $"Could not check Wallpaper Engine: {exception.Message}"; }
        if (!_isClosed && generation == _wallpaperStatusGeneration) WallpaperEngineStatus.Text = status;
    }
}
