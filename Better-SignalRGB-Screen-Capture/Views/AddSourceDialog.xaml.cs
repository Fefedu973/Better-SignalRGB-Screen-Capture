using CommunityToolkit.WinUI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.ComponentModel;
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
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml.Media.Imaging;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;

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
    public long? SelectedWindowHandle { get; private set; }
    public string? SelectedWindowTitle { get; private set; }
    public string? SelectedWebcamFormatId { get; private set; }
    public RectInt32? SelectedRegion
    {
        get; private set;
    }
    public string? SelectedWebcamDeviceId
    {
        get; private set;
    }
    public string? WebsiteUrl
    {
        get; private set;
    }

    // Enhanced website properties
    public double WebsiteZoom { get; private set; } = 1.0;
    public int WebsiteRefreshInterval { get; private set; } = 0;
    public string WebsiteUserAgent { get; private set; } = string.Empty;
    public int WebsiteWidth { get; private set; } = 1920;
    public int WebsiteHeight { get; private set; } = 1080;
    public string? WebsiteNavigationState { get; private set; } // New property for state persistence

    public SourceType SelectedSourceType { get; private set; }

    // Private fields
    private IReadOnlyList<WindowChoice> _windows = Array.Empty<WindowChoice>();
    private bool _discoveringWindows;
    private int _webcamFormatRevision;
    private bool _loadingWebcamFormats;
    private readonly record struct DisplayInfo(string Id, string Name);
    private bool _isWebViewInitialized = false;
    private bool _isClosed;

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
        Closed += (_, _) =>
        {
            _isClosed = true;
            _regionPreviewCancellation?.Cancel();
            WebsitePreview.Close();
        };
    }

    // --------------------------------------------
    // startup   (kick?off background tasks)
    // --------------------------------------------
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // Restore before discovery so an unavailable saved device is never retargeted.
            if (IsEditMode && _editingSource != null) PreFillForm(_editingSource);
            UpdateSettingsPanels();
            // Each native query runs on a worker; continuations resume on this UI thread.
            await Task.WhenAll(DiscoverMonitorsAsync(), DiscoverProcessesAsync(), DiscoverWebcamsAsync());
        }
        catch (Exception exception) { ReportDialogError("load capture devices", exception); }
    }

    private void ReportDialogError(string operation, Exception exception)
    {
        Helpers.ApplicationErrorLog.Write($"AddSourceDialog: {operation}", exception);
        if (!_isClosed) ShowValidationError($"Could not {operation}: {exception.Message}");
    }

    // --------------------------------------------
    // monitor discovery
    // --------------------------------------------
    private async Task DiscoverMonitorsAsync()
    {
        try
        {
            // Use ScreenRecorderLib to get displays
            var displays = await Task.Run(() => Recorder.GetDisplays());

            if (_isClosed) return;
            MonitorCombo.Items.Clear();

            foreach (var display in displays)
            {
                string name = string.IsNullOrWhiteSpace(display.FriendlyName)
                                ? $"Monitor {MonitorCombo.Items.Count + 1}"
                                : display.FriendlyName;

                // Handle null OutputSize gracefully
                string displayInfo = "";
                if (display.OutputSize != null)
                {
                    displayInfo = $" ({display.OutputSize.Width}x{display.OutputSize.Height})";
                }

                // Add position info if available
                string positionInfo = "";
                if (display.Position != null)
                {
                    positionInfo = $" [{display.Position.Left},{display.Position.Top}]";
                }

                MonitorCombo.Items.Add(new ComboBoxItem
                {
                    Content = $"{name}{positionInfo}{displayInfo}",
                    Tag = display.DeviceName
                });
            }

            if (MonitorCombo.Items.Count == 0)
            {
                MonitorCombo.Items.Add(new ComboBoxItem
                {
                    Content = "No monitors found",
                    Tag = null,
                    IsEnabled = false
                });
            }

            SelectDevice(MonitorCombo, SelectedMonitorDeviceId, "Saved monitor (currently unavailable)");
            MonitorRing.IsActive = false;
            MonitorRing.Visibility = Visibility.Collapsed;

            // If editing, try to select the correct monitor
            if (IsEditMode && _editingSource is { Type: SourceType.Monitor or SourceType.WallpaperEngine } && !string.IsNullOrEmpty(_editingSource.MonitorDeviceId))
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
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to discover monitors: {ex.Message}");

            if (_isClosed) return;
            MonitorCombo.Items.Add(new ComboBoxItem
            {
                Content = "Error discovering monitors",
                Tag = null,
                IsEnabled = false
            });
            SelectDevice(MonitorCombo, SelectedMonitorDeviceId, "Saved monitor (discovery unavailable)");
            MonitorRing.IsActive = false;
            MonitorRing.Visibility = Visibility.Collapsed;
        }
    }

    // --------------------------------------------
    // process discovery (all running processes)
    // --------------------------------------------
    private async Task DiscoverProcessesAsync()
    {
        if (_discoveringWindows || _isClosed) return;
        _discoveringWindows = true;
        try
        {
            var windows = await Task.Run(() =>
            {
                var choices = new List<WindowChoice>();
                foreach (var window in Recorder.GetWindows())
                {
                    if (window.Handle == IntPtr.Zero || string.IsNullOrWhiteSpace(window.Title)) continue;
                    try
                    {
                        GetWindowThreadProcessId(window.Handle, out var pid);
                        if (pid == 0 || pid > int.MaxValue) continue;
                        using var process = Process.GetProcessById((int)pid);
                        string? path = null;
                        try { path = process.MainModule?.FileName; }
                        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
                        choices.Add(new(window.Handle.ToInt64(), (int)pid, process.ProcessName, window.Title, path));
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
                return choices.DistinctBy(choice => choice.Handle).OrderBy(choice => choice.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(choice => choice.Title, StringComparer.OrdinalIgnoreCase).ToArray();
            });
            if (_isClosed) return;
            _windows = windows;
            ProcessBox.IsEnabled = true;
            ProcessRing.IsActive = false;
            ProcessRing.Visibility = Visibility.Collapsed;
            WindowDiscoveryStatus.Text = $"{windows.Length} windows available. Search by title, application or PID.";
        }
        catch (Exception ex)
        {
            if (_isClosed) return;
            ProcessBox.IsEnabled = true;
            ProcessRing.IsActive = false;
            ProcessRing.Visibility = Visibility.Collapsed;
            WindowDiscoveryStatus.Text = $"Could not list windows: {ex.Message}";
        }
        finally { _discoveringWindows = false; }
    }

    private async void RefreshWindows_Click(object sender, RoutedEventArgs e)
    {
        try { await DiscoverProcessesAsync(); }
        catch (Exception exception) { ReportDialogError("refresh available windows", exception); }
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

            if (_isClosed) return;
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

            SelectDevice(WebcamCombo, SelectedWebcamDeviceId, "Saved webcam (currently unavailable)");
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
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to discover webcams: {ex.Message}");

            if (_isClosed) return;
            WebcamCombo.Items.Add(new ComboBoxItem
            {
                Content = "Error discovering cameras",
                Tag = null,
                IsEnabled = false
            });
            SelectDevice(WebcamCombo, SelectedWebcamDeviceId, "Saved webcam (discovery unavailable)");
            WebcamRing.IsActive = false;
            WebcamRing.Visibility = Visibility.Collapsed;
        }
    }

    private sealed record WindowChoice(long Handle, int Id, string Name, string Title, string? Path)
    {
        public string DisplayText => $"{Title} — {Name} (PID {Id}, window {Handle:X})";
        public override string ToString() => DisplayText;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private void ProcessBox_TextChanged(object sender, AutoSuggestBoxTextChangedEventArgs e)
    {
        if (!ProcessBox.IsEnabled || e.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        SelectedProcessId = null;
        SelectedProcessPath = null;
        SelectedWindowHandle = null;
        SelectedWindowTitle = null;
        var query = ProcessBox.Text.Trim();
        ProcessBox.ItemsSource = _windows.Where(window => window.DisplayText.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(100).ToArray();
    }

    private void ProcessBox_SuggestionChosen(object sender, AutoSuggestBoxSuggestionChosenEventArgs e)
    {
        if (e.SelectedItem is not WindowChoice window) return;
        SelectedProcessId = window.Id;
        SelectedProcessPath = window.Path;
        SelectedWindowHandle = window.Handle;
        SelectedWindowTitle = window.Title;
        ProcessBox.Text = window.DisplayText;
    }

    // --------------------------------------------
    // browse for any exe
    // --------------------------------------------
    private async void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".exe");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSingleFileAsync();
            if (file != null && !_isClosed)
            {
                ProcessBox.Text = file.Path;
                SelectedProcessId = null;
                SelectedWindowHandle = null;
                SelectedWindowTitle = null;
                SelectedProcessPath = file.Path;
            }
        }
        catch (Exception ex) { if (!_isClosed) ShowValidationError($"Could not choose an executable: {ex.Message}"); }
    }

    // --------------------------------------------
    // region picker placeholder
    // --------------------------------------------
    private async void SelectRegion_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var region = await Helpers.RegionPicker.PickAsync();
            if (region is RectInt32 r && !_isClosed)
            {
                SelectedRegion = r;
                RegionDebugInfo.Visibility = Visibility.Visible;
                RegionCoordinatesText.Text = $"Coordinates: X={r.X}, Y={r.Y}, Width={r.Width}, Height={r.Height}";
                await AnalyzeRegionAndTakeScreenshotAsync(r);
            }
        }
        catch (Exception ex) { if (!_isClosed) ShowValidationError($"Could not select a region: {ex.Message}"); }
    }

    // --------------------------------------------
    // UI switching
    // --------------------------------------------
    private void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSettingsPanels();
        if (KindBox.SelectedItem is ComboBoxItem item)
        {
            var tag = item.Tag?.ToString() ?? string.Empty;
            SelectedSourceType = tag switch
            {
                "Monitor" => SourceType.Monitor,
                "Process" => SourceType.Process,
                "Region" => SourceType.Region,
                "Webcam" => SourceType.Webcam,
                "Website" => SourceType.Website,
                "WallpaperEngine" => SourceType.WallpaperEngine,
                _ => SelectedSourceType
            };
        }
    }

    private void UpdateSettingsPanels()
    {
        if (MonitorSettings == null) return;   // designer safety
        string tag = (KindBox.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
        MonitorSettings.Visibility = tag is "Monitor" or "WallpaperEngine" ? Visibility.Visible : Visibility.Collapsed;
        ProcessSettings.Visibility = tag == "Process" ? Visibility.Visible : Visibility.Collapsed;
        RegionSettings.Visibility = tag == "Region" ? Visibility.Visible : Visibility.Collapsed;
        WebcamSettings.Visibility = tag == "Webcam" ? Visibility.Visible : Visibility.Collapsed;
        WebsiteSettings.Visibility = tag == "Website" ? Visibility.Visible : Visibility.Collapsed;
        if (WallpaperEngineInfo != null)
        {
            WallpaperEngineInfo.Visibility = tag == "WallpaperEngine" ? Visibility.Visible : Visibility.Collapsed;
            if (tag == "WallpaperEngine") _ = RefreshWallpaperEngineStatusAsync();
        }
    }

    // --------------------------------------------
    // save
    // --------------------------------------------
    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        ValidationError.IsOpen = false;
        if (MonitorSettings.Visibility == Visibility.Visible)
            SelectedMonitorDeviceId = (MonitorCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        else if (ProcessSettings.Visibility == Visibility.Visible)
        {
            // Process settings are handled via ProcessBox events, no additional action needed here
            // SelectedProcessPath should already be set from ProcessBox_SuggestionChosen or BrowseExe_Click
        }
        else if (RegionSettings.Visibility == Visibility.Visible)
        {
            // Region settings are handled via SelectRegion_Click, no additional action needed here
            // SelectedRegion should already be set
        }
        else if (WebcamSettings.Visibility == Visibility.Visible)
        {
            if (_loadingWebcamFormats)
            {
                args.Cancel = true;
                ShowValidationError("Wait for the selected camera's formats to finish loading.");
                return;
            }
            SelectedWebcamDeviceId = (WebcamCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            SelectedWebcamFormatId = (WebcamFormatCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        }
        else if (WebsiteSettings.Visibility == Visibility.Visible)
        {
            WebsiteUrl = WebsiteUrlBox.Text?.Trim();
            var validation = Better_SignalRGB_Screen_Capture.Core.Helpers.CanvasSourceValidation.ValidateWebsite(
                WebsiteUrl, WebsiteWidthBox.Value, WebsiteHeightBox.Value, WebsiteRefreshBox.Value);
            if (validation != null)
            {
                ShowValidationError(validation);
                args.Cancel = true;
                return;
            }

            // Save enhanced website settings
            WebsiteZoom = WebsiteZoomSlider?.Value ?? 1.0;
            WebsiteRefreshInterval = (int)(WebsiteRefreshBox?.Value ?? 0);

            // Get user agent
            if (WebsiteUserAgentCombo?.SelectedItem is ComboBoxItem userAgentItem)
            {
                if (userAgentItem.Tag?.ToString() == "custom")
                {
                    WebsiteUserAgent = WebsiteCustomUserAgentBox?.Text ?? WebsiteUserAgent;
                }
                else
                {
                    WebsiteUserAgent = userAgentItem.Tag?.ToString() ?? WebsiteUserAgent;
                }
            }

            WebsiteWidth = (int)(WebsiteWidthBox?.Value ?? 1920);
            WebsiteHeight = (int)(WebsiteHeightBox?.Value ?? 1080);

            // Capture navigation state for website sources
            // CoreWebView2 must be read on its owning UI thread before the dialog closes.
            WebsiteNavigationState = GetNavigationState();
            if (WebsiteNavigationState != null) WebsiteUrl = WebsiteNavigationState;
        }
        var error = SelectedSourceType switch
        {
            SourceType.Monitor when string.IsNullOrWhiteSpace(SelectedMonitorDeviceId) => "Choose an available monitor before saving.",
            SourceType.WallpaperEngine when string.IsNullOrWhiteSpace(SelectedMonitorDeviceId) => "Choose the monitor whose Wallpaper Engine output you want to capture.",
            SourceType.Process when SelectedProcessId is not > 0 && string.IsNullOrWhiteSpace(SelectedProcessPath) => "Choose a window from the suggestions or browse for an executable.",
            SourceType.Region when SelectedRegion is not { Width: > 0, Height: > 0 } => "Select a screen region before saving.",
            SourceType.Webcam when string.IsNullOrWhiteSpace(SelectedWebcamDeviceId) => "Choose an available webcam before saving.",
            _ => null
        };
        if (error != null)
        {
            args.Cancel = true;
            ShowValidationError(error);
        }
    }

    private void ShowValidationError(string message)
    {
        ValidationError.Message = message;
        ValidationError.IsOpen = true;
        ValidationError.StartBringIntoView();
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
            case SourceType.WallpaperEngine:
                if (KindBox != null)
                {
                    // Select Monitor option
                    foreach (ComboBoxItem item in KindBox.Items)
                    {
                        if (item.Tag?.ToString() == (source.Type == SourceType.WallpaperEngine ? "WallpaperEngine" : "Monitor"))
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
                    // Show debug info and screenshot for existing region
                    RegionDebugInfo.Visibility = Visibility.Visible;
                    RegionCoordinatesText.Text = $"Coordinates: X={r.X}, Y={r.Y}, Width={r.Width}, Height={r.Height}";

                    // Analyze and show screenshot
                    _ = AnalyzeRegionAndTakeScreenshotAsync(r);
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
                SelectedWebcamFormatId = source.WebcamFormatId;
                break;

            case SourceType.Website:
                if (KindBox != null)
                {
                    // Select Website option
                    foreach (ComboBoxItem item in KindBox.Items)
                    {
                        if (item.Tag?.ToString() == "Website")
                        {
                            KindBox.SelectedItem = item;
                            break;
                        }
                    }
                }
                WebsiteUrl = source.WebsiteUrl;
                WebsiteUrlBox.Text = source.WebsiteUrl;

                // Restore enhanced website settings
                if (WebsiteZoomSlider != null)
                {
                    WebsiteZoomSlider.Value = source.WebsiteZoom;
                    WebsiteZoomLabel.Text = $"{(int)(source.WebsiteZoom * 100)}%";
                }

                if (WebsiteRefreshBox != null)
                {
                    WebsiteRefreshBox.Value = source.WebsiteRefreshInterval;
                }

                // Set user agent
                if (WebsiteUserAgentCombo != null)
                {
                    bool foundUserAgent = false;
                    foreach (ComboBoxItem item in WebsiteUserAgentCombo.Items)
                    {
                        if (item.Tag?.ToString() == source.WebsiteUserAgent)
                        {
                            WebsiteUserAgentCombo.SelectedItem = item;
                            foundUserAgent = true;
                            break;
                        }
                    }

                    // If not found in predefined list, select Custom and set custom text
                    if (!foundUserAgent)
                    {
                        foreach (ComboBoxItem item in WebsiteUserAgentCombo.Items)
                        {
                            if (item.Tag?.ToString() == "custom")
                            {
                                WebsiteUserAgentCombo.SelectedItem = item;
                                WebsiteCustomUserAgentBox.Visibility = Visibility.Visible;
                                WebsiteCustomUserAgentBox.Text = source.WebsiteUserAgent;
                                break;
                            }
                        }
                    }
                }

                if (WebsiteWidthBox != null)
                {
                    WebsiteWidthBox.Value = source.WebsiteWidth;
                }

                if (WebsiteHeightBox != null)
                {
                    WebsiteHeightBox.Value = source.WebsiteHeight;
                }

                // Restore navigation state if available
                if (!string.IsNullOrEmpty(source.WebsiteNavigationState))
                {
                    _ = RestoreNavigationStateAsync(source.WebsiteNavigationState);
                }
                break;
        }

        UpdateSettingsPanels();
    }

    private void PreFillProcessInfo(SourceItem source)
    {
        if (source.Type != SourceType.Process) return;
        // Preserve exact identity even if the saved window is currently unavailable.
        SelectedProcessId = source.ProcessId;
        SelectedProcessPath = source.ProcessPath;
        SelectedWindowHandle = source.WindowHandle;
        SelectedWindowTitle = source.WindowTitle;
        ProcessBox.Text = !string.IsNullOrWhiteSpace(source.WindowTitle)
            ? $"{source.WindowTitle} — {System.IO.Path.GetFileName(source.ProcessPath)} (PID {source.ProcessId?.ToString() ?? "unknown"})"
            : source.ProcessPath ?? $"Saved window (PID {source.ProcessId?.ToString() ?? "unknown"})";
    }


}
