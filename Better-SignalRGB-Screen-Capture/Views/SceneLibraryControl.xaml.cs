using Better_SignalRGB_Screen_Capture.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Better_SignalRGB_Screen_Capture.Views;

public sealed partial class SceneLibraryControl : UserControl
{
    public MainViewModel ViewModel { get; } = App.GetService<MainViewModel>();
    public SceneLibraryControl() => InitializeComponent();
}
