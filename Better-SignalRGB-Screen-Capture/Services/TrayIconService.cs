using System;
using System.Threading.Tasks;
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.ViewModels;
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
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

    public TrayIconService(MainViewModel mainVm, ILocalSettingsService localSettings)
    {
        _mainVm = mainVm;
        _localSettings = localSettings;

        // Create the tray icon
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "Better SignalRGB Screen Capture",
            IconSource = new BitmapImage(new Uri("ms-appx:///Assets/WindowIcon.ico")),
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
            exitItem.Click += (_, __) => ExitApplication();

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
        _trayIcon.ForceCreate();

        // Attempt to set fluent menu mode if enum available via reflection
        var modeProp = _trayIcon.GetType().GetProperty("ContextMenuMode");
        if (modeProp != null)
        {
            var enumType = modeProp.PropertyType;
            var fluentValue = Enum.GetValues(enumType).OfType<object>().FirstOrDefault(v => v.ToString()?.Contains("SecondWindow")==true);
            if (fluentValue != null)
            {
                modeProp.SetValue(_trayIcon, fluentValue);
            }
        }

        // Sync initial state
        UpdateRecordingMenuText();

        // Subscribe to view-model property changes so we stay in-sync
        _mainVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsRecording) || e.PropertyName == nameof(MainViewModel.IsRecordingLoading))
            {
                UpdateRecordingMenuText();
            }
        };
    }

    private void ExecuteOnUI(Action action)
    {
        if (App.MainWindow.DispatcherQueue.HasThreadAccess)
            action();
        else
            App.MainWindow.DispatcherQueue.TryEnqueue(() => action());
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
            if (!_mainVm.IsRecordingLoading)
            {
                await _mainVm.ToggleRecordingCommand.ExecuteAsync(null);
            }
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

    private void ExitApplication()
    {
        ExecuteOnUI(() => Application.Current.Exit());
    }

    public void Dispose()
    {
        _trayIcon?.Dispose();
    }
} 