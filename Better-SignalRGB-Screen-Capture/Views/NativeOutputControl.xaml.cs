using Better_SignalRGB_Screen_Capture.Services.NativeOutput;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Better_SignalRGB_Screen_Capture.Views;

public sealed partial class NativeOutputControl : UserControl
{
    private readonly NativeIntegrationService _integration;
    private readonly DispatcherQueueTimer _timer;
    private bool _updating = true, _busy, _loaded;
    public NativeOutputControl()
    {
        InitializeComponent();
        _integration = App.GetService<NativeIntegrationService>();
        _timer = DispatcherQueue.CreateTimer(); _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => Refresh();
        Loaded += async (_, _) =>
        {
            _loaded = true;
            await _integration.InitializeAsync();
            if (_loaded) { Refresh(); _timer.Start(); }
        };
        Unloaded += (_, _) => { _loaded = false; _timer.Stop(); };
    }
    private void Refresh()
    {
        _updating = true;
        NativeToggle.IsOn = _integration.Enabled;
        NativeToggle.IsEnabled = !_busy;
        NativeStatus.Text = _integration.StatusText;
        _updating = false;
    }
    private async void NativeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updating || _busy || !_loaded) return;
        _busy = true; NativeToggle.IsEnabled = false;
        try { await _integration.SetEnabledAsync(NativeToggle.IsOn); }
        catch { /* Service retains an actionable status; no unhandled UI exception. */ }
        finally { _busy = false; if (_loaded) Refresh(); }
    }
}
