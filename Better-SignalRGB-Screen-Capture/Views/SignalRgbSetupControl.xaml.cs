using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Better_SignalRGB_Screen_Capture.Views;

public sealed partial class SignalRgbSetupControl : UserControl
{
    private const string FolderKey = "SignalRgbEffectsFolder";
    private readonly ISignalRgbConnectionService _connection = App.GetService<ISignalRgbConnectionService>();
    private readonly ILocalSettingsService _settings = App.GetService<ILocalSettingsService>();
    private readonly SignalRgbEffectInstaller _installer = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? _lifetime;
    private bool _busy, _loaded;
    private string? _folder;

    public SignalRgbSetupControl()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => RefreshConnection();
        Loaded += OnLoaded;
        Unloaded += (_, _) => { _loaded = false; _timer.Stop(); _lifetime?.Cancel(); _lifetime?.Dispose(); _lifetime = null; };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        _lifetime = new CancellationTokenSource();
        _timer.Start();
        RefreshConnection();
        await RunAsync(async () =>
        {
            _folder = await _settings.ReadSettingAsync<string>(FolderKey);
            if (string.IsNullOrWhiteSpace(_folder) || !Directory.Exists(_folder)) _folder = SignalRgbEffectInstaller.DetectEffectsFolder();
            await RefreshInstallationAsync();
        });
    }

    private async Task RefreshInstallationAsync()
    {
        var status = await _installer.InspectAsync(_folder);
        FolderBox.Text = _folder ?? "No SignalRGB effects folder detected. Choose its effects folder.";
        InstallationText.Text = !status.BundleAvailable ? "The matching HTML effect is missing beside this app. Reinstall or rebuild the application."
            : status.Matches ? $"The installed effect matches this app (revision {status.BundleRevision})."
            : status.Installed ? $"The installed effect differs from this app. Updating preserves a backup of the existing file."
            : "The matching effect is not installed in this folder.";
        InstallButton.IsEnabled = !_busy && status.BundleAvailable && _folder != null && !status.Matches;
    }

    private void RefreshConnection()
    {
        var status = _connection.Snapshot;
        ConnectionStatus.Message = status.Message;
        ConnectionStatus.Severity = status.FramesConfirmed ? InfoBarSeverity.Success : InfoBarSeverity.Informational;
        CountersText.Text = $"Sent: {status.FramesSent:N0} source images · effect draws: {status.RenderedFrames:N0} · " +
            $"{status.SourceUpdatesPerSecond:F1} source updates/s · {status.RenderFramesPerSecond:F1} effect FPS" +
            (status.LastError == null ? string.Empty : $"\nLast connection error: {status.LastError}");
    }

    private async void ChooseFolder_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;
        await _settings.SaveSettingAsync(FolderKey, folder.Path);
        _folder = folder.Path;
    });

    private async void Install_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_folder == null) return;
        var backup = await _installer.InstallAsync(_folder);
        OperationStatus.Severity = InfoBarSeverity.Success;
        OperationStatus.Message = "Effect installed. Restart SignalRGB and select Better SignalRGB Screen Capture." +
            (backup == null ? string.Empty : $" Previous file saved as {Path.GetFileName(backup)}.");
        OperationStatus.IsOpen = true;
    });

    private async void Check_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        await _connection.ProbeAsync(_lifetime?.Token ?? CancellationToken.None);
        RefreshConnection();
    });

    private async Task RunAsync(Func<Task> operation)
    {
        if (_busy) return;
        _busy = true;
        ChooseButton.IsEnabled = CheckButton.IsEnabled = InstallButton.IsEnabled = false;
        try { await operation(); }
        catch (Exception ex)
        {
            OperationStatus.Severity = InfoBarSeverity.Error;
            OperationStatus.Message = ex.Message;
            OperationStatus.IsOpen = true;
        }
        finally
        {
            _busy = false;
            ChooseButton.IsEnabled = CheckButton.IsEnabled = _loaded;
            if (_loaded)
            {
                try { await RefreshInstallationAsync(); }
                catch (Exception ex) { InstallationText.Text = $"Could not inspect the effect: {ex.Message}"; }
            }
        }
    }
}
