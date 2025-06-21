using Microsoft.UI.Xaml.Controls;

namespace Better_SignalRGB_Screen_Capture.Views;

public sealed partial class AddSourceDialog : ContentDialog
{
    public AddSourceDialog()
    {
        InitializeComponent();
    }

    private void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Grab the Tag we set in XAML (“Full”, “Window”, “Region”)
        var tag = (KindBox.SelectedItem as ComboBoxItem)?.Tag as string;

        // Simple toggle logic
        WindowSettings.Visibility = tag == "Window" ? Visibility.Visible : Visibility.Collapsed;
        RegionSettings.Visibility = tag == "Region" ? Visibility.Visible : Visibility.Collapsed;
    }

    // Optional helpers you can read after ShowAsync()
    public string SourceName => NameBox.Text.Trim();
    public string CaptureKind => (KindBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Full";
    public bool IncludeBorder => IncludeBorderSwitch.IsOn;
    public bool ShowOverlay => ShowOverlaySwitch.IsOn;
}
