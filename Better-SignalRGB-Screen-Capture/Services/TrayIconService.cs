using System;
using System.Threading.Tasks;
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.ViewModels;
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Better_SignalRGB_Screen_Capture.Services;

/// <summary>
/// Creates and manages a single TaskbarIcon instance that lives for the lifetime of the process.
/// It exposes basic menu commands such as start/stop recording, opening settings, showing the main window and exiting the application.
/// </summary>
public class TrayIconService : IDisposable
{
    private readonly TaskbarIcon _trayIcon;
    private readonly MainViewModel _mainVm;
    private readonly ILocalSettingsService _localSettings;

    private MenuFlyoutItem? _startStopItem;
    private bool _isExiting;
    private bool _disposed;

    public TrayIconService(MainViewModel mainVm, ILocalSettingsService localSettings)
    {
        _mainVm = mainVm;
        _localSettings = localSettings;

        // Create the tray icon
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "Better SignalRGB Screen Capture",
            // Load the native tray icon directly. The asynchronous XAML image path
            // requires package URI resolution and can fail after startup in unpackaged builds.
            Icon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "WindowIcon.ico")),
            ContextMenuMode = ContextMenuMode.SecondWindow,
        };

        // Build context menu on the UI thread
        ExecuteOnUI(() =>
        {
            _startStopItem = new MenuFlyoutItem { Text = "Start Recording", Icon = new FontIcon { Glyph = "\uE7C8" } };
            _startStopItem.Click += (_, __) => ToggleRecordingFromTray();

            var settingsItem = new MenuFlyoutItem { Text = "Settings", Icon = new FontIcon { Glyph = "\uE713" } };
            settingsItem.Click += (_, __) => ShowSettingsPage();

            var showItem = new MenuFlyoutItem { Text = "Show", Icon = new FontIcon { Glyph = "\uE740" } };
            showItem.Click += (_, __) => ShowMainWindow();

            var exitItem = new MenuFlyoutItem { Text = "Exit", Icon = new FontIcon { Glyph = "\uE7E8" } };
            exitItem.Click += async (_, __) => await ExitApplicationAsync();

            var flyout = new MenuFlyout();

            flyout.Items.Add(_startStopItem);
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(settingsItem);
            flyout.Items.Add(showItem);
            flyout.Items.Add(exitItem);

            _trayIcon.ContextFlyout = flyout;

            _startStopItem.IsEnabled = !_mainVm.IsRecordingLoading;
        });
        
        // Ensure the icon is created
        // The library defaults to enabling process-wide Efficiency Mode here.
        // A live capture pipeline must retain its normal scheduling priority.
        _trayIcon.ForceCreate(enablesEfficiencyMode: false);

        // Sync initial state
        UpdateRecordingMenuText();

        // Subscribe to view-model property changes so we stay in-sync
        _mainVm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsRecording) or nameof(MainViewModel.IsRecordingLoading))
            UpdateRecordingMenuText();
    }

    private void ExecuteOnUI(Action action)
    {
        void ExecuteSafely()
        {
            if (_disposed) return;
            try { action(); }
            catch (Exception exception) { ReportTrayError("complete the tray action", exception); }
        }
        if (App.MainWindow.DispatcherQueue.HasThreadAccess)
            ExecuteSafely();
        else
            App.MainWindow.DispatcherQueue.TryEnqueue(ExecuteSafely);
    }

    private void ReportTrayError(string operation, Exception exception)
    {
        Helpers.ApplicationErrorLog.Write($"TrayIcon: {operation}", exception);
        _mainVm.StatusMessage = $"Could not {operation}: {exception.Message}";
    }

    private void UpdateRecordingMenuText()
    {
        ExecuteOnUI(() => {
            if (_startStopItem == null) return;
            var icon = (FontIcon)_startStopItem.Icon;
            if (_mainVm.IsRecording)
            {
                _startStopItem.Text = "Stop Recording";
                icon.Glyph = "\uE71A";
            }
            else
            {
                _startStopItem.Text = "Start Recording";
                icon.Glyph = "\uE7C8";
            }
            _startStopItem.IsEnabled = !_mainVm.IsRecordingLoading;
        });
    }

    private void ToggleRecordingFromTray()
    {
        // always run the command on the main window's dispatcher
        App.MainWindow.DispatcherQueue.TryEnqueue(async () =>
        {
            if (_disposed || _isExiting) return;
            try
            {
                if (!_mainVm.IsRecordingLoading)
                    await _mainVm.ToggleRecordingCommand.ExecuteAsync(null);
            }
            catch (Exception exception) { ReportTrayError("change recording state", exception); }
        });
    }

    private void ShowSettingsPage()
    {
        ExecuteOnUI(() => {
            var navService = App.GetService<INavigationService>();
            navService.NavigateTo("Better_SignalRGB_Screen_Capture.ViewModels.SettingsViewModel");
            ShowMainWindow();
        });
    }

    private void ShowMainWindow()
    {
        ExecuteOnUI(() => {
            App.MainWindow.Show();
            App.MainWindow.Activate();
        });
    }

    private async Task ExitApplicationAsync()
    {
        if (_isExiting) return;
        _isExiting = true;
        try
        {
            await _mainVm.ShutdownAsync();
            await App.GetService<ISignalRgbEffectSettingsService>().FlushAsync();
            if (Application.Current is App app) app.Host.Dispose();
            else Dispose();
            Application.Current.Exit();
        }
        catch (Exception ex)
        {
            ReportTrayError("stop cleanly", ex);
            _isExiting = false;
            ShowMainWindow();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mainVm.PropertyChanged -= OnViewModelPropertyChanged;
        _trayIcon?.Dispose();
    }
}
