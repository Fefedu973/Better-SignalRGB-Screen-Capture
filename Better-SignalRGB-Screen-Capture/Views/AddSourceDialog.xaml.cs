using CommunityToolkit.WinUI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Display;      // DisplayMonitor
using Windows.Devices.Enumeration;  // DeviceInformation
using Windows.Graphics;             // RectInt32
using Windows.Storage.Pickers;      // FileOpenPicker

namespace Better_SignalRGB_Screen_Capture.Views;

public sealed partial class AddSourceDialog : ContentDialog
{
    // public results
    public string? SelectedMonitorDeviceId
    {
        get; private set;
    }
    public int? SelectedProcessId
    {
        get; private set;
    }
    public string? SelectedProcessPath
    {
        get; private set;
    }
    public RectInt32? SelectedRegion
    {
        get; private set;
    }

    private List<ProcessInfo> _processes = new();

    public AddSourceDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        PrimaryButtonClick += OnPrimaryButtonClick;
    }

    // --------------------------------------------
    // startup   (kick?off background tasks)
    // --------------------------------------------
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ = Task.Run(DiscoverMonitorsAsync);
        _ = Task.Run(DiscoverProcessesAsync);
        UpdateSettingsPanels();
    }

    // --------------------------------------------
    // monitor discovery
    // --------------------------------------------
    private async Task DiscoverMonitorsAsync()
    {
        try
        {
            var selector = DisplayMonitor.GetDeviceSelector();
            var devices = await DeviceInformation.FindAllAsync(selector);

            var monitors = await Task.WhenAll(devices.Select(d => DisplayMonitor.FromInterfaceIdAsync(d.Id).AsTask()));

            await DispatcherQueue.EnqueueAsync(() =>
            {
                foreach (var mon in monitors)
                {
                    string name = string.IsNullOrWhiteSpace(mon.DisplayName)
                                    ? $"Monitor {MonitorCombo.Items.Count + 1}"
                                    : mon.DisplayName;

                    MonitorCombo.Items.Add(new ComboBoxItem
                    {
                        Content = $"{name}  {mon.NativeResolutionInRawPixels.Width}×{mon.NativeResolutionInRawPixels.Height}",
                        Tag = mon.DeviceId
                    });
                }

                MonitorCombo.SelectedIndex = 0;
                MonitorCombo.IsEnabled = true;
                MonitorRing.IsActive = false;
                MonitorRing.Visibility = Visibility.Collapsed;
            });
        }
        catch { /* ignore & leave spinner */ }
    }

    // --------------------------------------------
    // process discovery (all running processes)
    // --------------------------------------------
    private async Task DiscoverProcessesAsync()
    {
        var list = new List<ProcessInfo>();

        foreach (var p in Process.GetProcesses())
        {
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { }
            list.Add(new ProcessInfo(p.Id, p.ProcessName, path));
        }

        _processes = list;

        await DispatcherQueue.EnqueueAsync(() =>
        {
            ProcessBox.IsEnabled = true;
            ProcessRing.IsActive = false;
            ProcessRing.Visibility = Visibility.Collapsed;
        });
    }

    private readonly record struct ProcessInfo(int Id, string Name, string? Path);

    // --------------------------------------------
    // AutoSuggest for processes
    // --------------------------------------------
    private void ProcessBox_TextChanged(object sender, AutoSuggestBoxTextChangedEventArgs e)
    {
        if (!ProcessBox.IsEnabled || e.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;

        var q = ProcessBox.Text.Trim();
        ProcessBox.ItemsSource = _processes
            .Where(p => p.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name)
            .Take(60)
            .Select(p => $"{p.Name}  (PID {p.Id}){(p.Path is null ? string.Empty : $" — {System.IO.Path.GetFileName(p.Path)}")}")
            .ToList();
    }

    private void ProcessBox_SuggestionChosen(object sender, AutoSuggestBoxSuggestionChosenEventArgs e)
    {
        if (e.SelectedItem is not string text) return;
        var digits = new string(text.Where(char.IsDigit).ToArray());
        if (int.TryParse(digits, out int pid))
        {
            var info = _processes.FirstOrDefault(p => p.Id == pid);
            SelectedProcessId = pid;
            SelectedProcessPath = info.Path;
        }
    }

    // --------------------------------------------
    // browse for any exe
    // --------------------------------------------
    private async void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            ProcessBox.Text = file.Path;
            SelectedProcessId = null;
            SelectedProcessPath = file.Path;
        }
    }

    // --------------------------------------------
    // region picker placeholder
    // --------------------------------------------
    private async void SelectRegion_Click(object sender, RoutedEventArgs e)
    {
        
    }



    // --------------------------------------------
    // UI switching
    // --------------------------------------------
    private void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSettingsPanels();

    private void UpdateSettingsPanels()
    {
        if (MonitorSettings == null) return;   // designer safety
        string tag = (KindBox.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
        MonitorSettings.Visibility = tag == "Monitor" ? Visibility.Visible : Visibility.Collapsed;
        ProcessSettings.Visibility = tag == "Process" ? Visibility.Visible : Visibility.Collapsed;
        RegionSettings.Visibility = tag == "Region" ? Visibility.Visible : Visibility.Collapsed;
    }

    // --------------------------------------------
    // save
    // --------------------------------------------
    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (MonitorSettings.Visibility == Visibility.Visible && MonitorCombo.SelectedItem is ComboBoxItem mi)
            SelectedMonitorDeviceId = mi.Tag as string;
    }
}