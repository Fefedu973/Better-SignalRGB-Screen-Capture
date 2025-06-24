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
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Models;
using ScreenRecorderLib;

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
    public string? SelectedWebcamDeviceId
    {
        get; private set;
    }

    private List<ProcessInfo> _processes = new();

    // Public property to access the friendly name
    public string? FriendlyName => NameBox?.Text?.Trim();
    
    // Edit mode properties
    public bool IsEditMode { get; private set; }
    private SourceItem? _editingSource;

    public AddSourceDialog() : this(null)
    {
    }
    
    public AddSourceDialog(SourceItem? sourceToEdit)
    {
        InitializeComponent();
        
        // Set edit mode
        IsEditMode = sourceToEdit != null;
        _editingSource = sourceToEdit;
        
        // Update dialog title
        Title = IsEditMode ? "Edit Source" : "Add Source";
        
        // Apply the current app theme to the dialog
        var themeSelectorService = App.GetService<IThemeSelectorService>();
        this.RequestedTheme = themeSelectorService.Theme;
        
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
        _ = Task.Run(DiscoverWebcamsAsync);
        UpdateSettingsPanels();
        
        // Pre-fill form if editing (delay to ensure processes are discovered)
        if (IsEditMode && _editingSource != null)
        {
            // Add delay to ensure processes are discovered before pre-filling
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                PreFillForm(_editingSource);
            });
        }
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
                        Content = $"{name}  {mon.NativeResolutionInRawPixels.Width}x{mon.NativeResolutionInRawPixels.Height}",
                        Tag = mon.DeviceId
                    });
                }

                MonitorCombo.SelectedIndex = 0;
                MonitorCombo.IsEnabled = true;
                MonitorRing.IsActive = false;
                MonitorRing.Visibility = Visibility.Collapsed;
                
                // If editing, try to select the correct monitor
                if (IsEditMode && _editingSource?.Type == SourceType.Monitor && !string.IsNullOrEmpty(_editingSource.MonitorDeviceId))
                {
                    foreach (ComboBoxItem item in MonitorCombo.Items)
                    {
                        if (item.Tag?.ToString() == _editingSource.MonitorDeviceId)
                        {
                            MonitorCombo.SelectedItem = item;
                            break;
                        }
                    }
                }
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
            try
        {
            string? path = null;
                string processName = p.ProcessName;
                
                // Try to get the executable path
                try 
                { 
                    path = p.MainModule?.FileName; 
                } 
                catch 
                { 
                    // If we can't get MainModule, try alternative methods
                    try
                    {
                        // For some processes, we can still get useful info
                        if (!string.IsNullOrEmpty(processName) && p.MainWindowHandle != IntPtr.Zero)
                        {
                            // Use process name as fallback path for processes with windows
                            path = $"{processName}.exe";
                        }
                    }
                    catch { }
                }
                
                // Include processes that either have a path OR have a main window
                if (!string.IsNullOrEmpty(path) || p.MainWindowHandle != IntPtr.Zero)
                {
                    // Use actual path if available, otherwise construct from process name
                    var finalPath = !string.IsNullOrEmpty(path) ? path : $"{processName}.exe";
                    list.Add(new ProcessInfo(p.Id, processName, finalPath));
            }
        }
            catch
            {
                // Skip processes we can't access at all
                continue;
            }
        }

        // Sort by process name for better UX
        _processes = list.OrderBy(p => p.Name).ToList();

        await DispatcherQueue.EnqueueAsync(() =>
        {
            ProcessBox.IsEnabled = true;
            ProcessRing.IsActive = false;
            ProcessRing.Visibility = Visibility.Collapsed;
            
            // If we're in edit mode and waiting to pre-fill, do it now
            if (IsEditMode && _editingSource != null && _editingSource.Type == SourceType.Process)
            {
                PreFillProcessInfo(_editingSource);
            }
        });
    }

    // --------------------------------------------
    // webcam discovery
    // --------------------------------------------
    private async Task DiscoverWebcamsAsync()
    {
        try
        {
            // Discover webcams using ScreenRecorderLib
            var cameras = await Task.Run(() => Recorder.GetSystemVideoCaptureDevices());

            await DispatcherQueue.EnqueueAsync(() =>
            {
                WebcamCombo.Items.Clear();
                
                foreach (var camera in cameras)
                {
                    var friendlyName = string.IsNullOrWhiteSpace(camera.FriendlyName) 
                        ? "Camera" 
                        : camera.FriendlyName;
                        
                    WebcamCombo.Items.Add(new ComboBoxItem
                    {
                        Content = friendlyName,
                        Tag = camera.DeviceName
                    });
                }

                // Add a default item if no cameras found
                if (WebcamCombo.Items.Count == 0)
                {
                    WebcamCombo.Items.Add(new ComboBoxItem
                    {
                        Content = "No cameras found",
                        Tag = null,
                        IsEnabled = false
                    });
                }

                WebcamCombo.SelectedIndex = 0;
                WebcamCombo.IsEnabled = WebcamCombo.Items.Count > 0 && 
                                       ((ComboBoxItem)WebcamCombo.Items[0]).IsEnabled != false;
                WebcamRing.IsActive = false;
                WebcamRing.Visibility = Visibility.Collapsed;
                
                // If editing, try to select the correct webcam
                if (IsEditMode && _editingSource?.Type == SourceType.Webcam && !string.IsNullOrEmpty(_editingSource.WebcamDeviceId))
                {
                    foreach (ComboBoxItem item in WebcamCombo.Items)
                    {
                        if (item.Tag?.ToString() == _editingSource.WebcamDeviceId)
                        {
                            WebcamCombo.SelectedItem = item;
                            break;
                        }
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to discover webcams: {ex.Message}");
            
            await DispatcherQueue.EnqueueAsync(() =>
            {
                WebcamCombo.Items.Add(new ComboBoxItem
                {
                    Content = "Error discovering cameras",
                    Tag = null,
                    IsEnabled = false
                });
                WebcamCombo.SelectedIndex = 0;
                WebcamCombo.IsEnabled = false;
                WebcamRing.IsActive = false;
                WebcamRing.Visibility = Visibility.Collapsed;
            });
        }
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
            .Select(p => $"{p.Name}  (PID {p.Id}){(p.Path is null ? string.Empty : $" - {System.IO.Path.GetFileName(p.Path)}")}")
            .ToList();
    }

    private void ProcessBox_SuggestionChosen(object sender, AutoSuggestBoxSuggestionChosenEventArgs e)
    {
        if (e.SelectedItem is not string text) return;
        
        // Extract PID from the formatted string: "ProcessName  (PID 1234) - filename.exe"
        var pidMatch = System.Text.RegularExpressions.Regex.Match(text, @"\(PID (\d+)\)");
        if (pidMatch.Success && int.TryParse(pidMatch.Groups[1].Value, out int pid))
        {
            var info = _processes.FirstOrDefault(p => p.Id == pid);
            if (info.Id != 0 && !string.IsNullOrEmpty(info.Path))
            {
                // Don't save PID - only save the path which is persistent
                SelectedProcessId = null;
                SelectedProcessPath = info.Path;
                
                // Set the text to show the selected process
                ProcessBox.Text = text;
            }
            else
            {
                // Clear the selection since we can't get the executable path
                ProcessBox.Text = "";
                SelectedProcessPath = null;
            }
        }
        else
        {
            // Clear if we can't parse the PID
            ProcessBox.Text = "";
            SelectedProcessPath = null;
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
        // Show the full-desktop overlay and wait for the user to draw a rectangle
        var region = await Helpers.RegionPicker.PickAsync();
        if (region is RectInt32 r)
        {
            SelectedRegion = r;

            // Simple visual confirmation � replace with your own UI if you like
            RegionSettings.Children.Add(new TextBlock
            {
                Text = $"{r.Width} x {r.Height} at ({r.X}, {r.Y})",
                Margin = new Thickness(0, 4, 0, 0)
            });
        }
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
        WebcamSettings.Visibility = tag == "Webcam" ? Visibility.Visible : Visibility.Collapsed;
    }

    // --------------------------------------------
    // save
    // --------------------------------------------
    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (MonitorSettings.Visibility == Visibility.Visible && MonitorCombo.SelectedItem is ComboBoxItem mi)
            SelectedMonitorDeviceId = mi.Tag as string;
        else if (WebcamSettings.Visibility == Visibility.Visible && WebcamCombo.SelectedItem is ComboBoxItem wi)
            SelectedWebcamDeviceId = wi.Tag as string;
    }
    
    private void PreFillForm(SourceItem source)
    {
        // Set the friendly name
        if (NameBox != null)
        {
            NameBox.Text = source.Name;
        }
        
        // Set the source type and related data
        switch (source.Type)
        {
            case SourceType.Monitor:
                if (KindBox != null)
                {
                    // Select Monitor option
                    foreach (ComboBoxItem item in KindBox.Items)
                    {
                        if (item.Tag?.ToString() == "Monitor")
                        {
                            KindBox.SelectedItem = item;
                            break;
                        }
                    }
                }
                SelectedMonitorDeviceId = source.MonitorDeviceId;
                break;
                
            case SourceType.Process:
                if (KindBox != null)
                {
                    // Select Process option
                    foreach (ComboBoxItem item in KindBox.Items)
                    {
                        if (item.Tag?.ToString() == "Process")
                        {
                            KindBox.SelectedItem = item;
                            break;
                        }
                    }
                }
                
                // Pre-fill process info (will be called again after process discovery if needed)
                PreFillProcessInfo(source);
                break;
                
            case SourceType.Region:
                if (KindBox != null)
                {
                    // Select Region option
                    foreach (ComboBoxItem item in KindBox.Items)
                    {
                        if (item.Tag?.ToString() == "Region")
                        {
                            KindBox.SelectedItem = item;
                            break;
                        }
                    }
                }
                SelectedRegion = source.RegionBounds;
                if (SelectedRegion.HasValue && RegionSettings != null)
                {
                    var r = SelectedRegion.Value;
                    RegionSettings.Children.Add(new TextBlock
                    {
                        Text = $"{r.Width} x {r.Height} at ({r.X}, {r.Y})",
                        Margin = new Thickness(0, 4, 0, 0)
                    });
                }
                break;
                
            case SourceType.Webcam:
                if (KindBox != null)
                {
                    // Select Webcam option
                    foreach (ComboBoxItem item in KindBox.Items)
                    {
                        if (item.Tag?.ToString() == "Webcam")
                        {
                            KindBox.SelectedItem = item;
                            break;
                        }
                    }
                }
                SelectedWebcamDeviceId = source.WebcamDeviceId;
                break;
        }
        
        UpdateSettingsPanels();
    }
    
    private void PreFillProcessInfo(SourceItem source)
    {
        if (source.Type != SourceType.Process || ProcessBox == null) return;
        
        // Don't use PID for persistence - always null for saved sources
        SelectedProcessId = null;
        SelectedProcessPath = source.ProcessPath;
        
        string displayText = "";
        
        if (!string.IsNullOrEmpty(source.ProcessPath))
        {
            // Try to find if this process is currently running
            var runningProcess = _processes.FirstOrDefault(p => 
                !string.IsNullOrEmpty(p.Path) && 
                string.Equals(p.Path, source.ProcessPath, StringComparison.OrdinalIgnoreCase));
            
            if (runningProcess.Id != 0) // Found the process currently running
            {
                displayText = $"{runningProcess.Name}  (PID {runningProcess.Id}) - {System.IO.Path.GetFileName(runningProcess.Path)}";
            }
            else
            {
                // Process not currently running, just show the path
                displayText = source.ProcessPath;
            }
        }
        else
        {
            displayText = "No process path specified";
        }
        
        ProcessBox.Text = displayText;
    }
}