using System.Threading.Tasks;
using Better_SignalRGB_Screen_Capture.ViewModels;

using Microsoft.UI.Xaml.Controls;

namespace Better_SignalRGB_Screen_Capture.Views;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel
    {
        get;
    }

    public MainPage()
    {
        ViewModel = App.GetService<MainViewModel>();
        InitializeComponent();
    }

    private async void Add_Sources(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var dlg = new AddSourceDialog
        {
            XamlRoot = this.XamlRoot   // always set this in WinUI 3
        };

        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            // read dlg.Whatever here
        }
    }
}
