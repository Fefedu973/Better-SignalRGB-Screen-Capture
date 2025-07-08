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
    public string WebsiteUserAgent { get; private set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    public int WebsiteWidth { get; private set; } = 1920;
    public int WebsiteHeight { get; private set; } = 1080;
    public string? WebsiteNavigationState { get; private set; } // New property for state persistence

    public SourceType SelectedSourceType { get; private set; }

    // Private fields
    private List<ProcessInfo> _processes = new();
    private readonly record struct DisplayInfo(string Id, string Name);
    private bool _isWebViewInitialized = false;
    private string? _pendingUrl = null;

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
            // Use ScreenRecorderLib to get displays
            var displays = await Task.Run(() => Recorder.GetDisplays());

            await DispatcherQueue.EnqueueAsync(() =>
            {
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

                MonitorCombo.SelectedIndex = 0;
                MonitorCombo.IsEnabled = MonitorCombo.Items.Count > 0 && 
                                       ((ComboBoxItem)MonitorCombo.Items[0]).IsEnabled != false;
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
        catch (Exception ex) 
        { 
            Debug.WriteLine($"Failed to discover monitors: {ex.Message}");
            
            await DispatcherQueue.EnqueueAsync(() =>
            {
                MonitorCombo.Items.Add(new ComboBoxItem
                {
                    Content = "Error discovering monitors",
                    Tag = null,
                    IsEnabled = false
                });
                MonitorCombo.SelectedIndex = 0;
                MonitorCombo.IsEnabled = false;
                MonitorRing.IsActive = false;
                MonitorRing.Visibility = Visibility.Collapsed;
            });
        }
    }

    // --------------------------------------------
    // process discovery (all running processes)
    // --------------------------------------------
    private async Task DiscoverProcessesAsync()
    {
        var list = new List<ProcessInfo>();

        try
        {
            // Use ScreenRecorderLib to get windows
            var windows = await Task.Run(() => Recorder.GetWindows());
            
            var processedNames = new HashSet<string>();
            
            foreach (var window in windows)
        {
            try
        {
                    // Skip if window title is empty or too short
                    if (string.IsNullOrWhiteSpace(window.Title) || window.Title.Length < 2)
                        continue;
                        
                    // Use P/Invoke to get process ID from window handle
                    uint processId;
                    GetWindowThreadProcessId(window.Handle, out processId);
                    
                    if (processId == 0)
                        continue;
                    
                    // Try to get the process
                    using var process = Process.GetProcessById((int)processId);
                    string processName = process.ProcessName;
                    
                    // Skip if we've already processed this process name
                    if (processedNames.Contains(processName))
                        continue;
                    
                    processedNames.Add(processName);
                    
            string? path = null;
                try 
                { 
                        path = process.MainModule?.FileName;
                } 
                catch 
                { 
                        // If we can't get the path, use process name as fallback
                            path = $"{processName}.exe";
                        }
                    
                    if (!string.IsNullOrEmpty(path))
                {
                        list.Add(new ProcessInfo((int)processId, processName, path));
            }
        }
            catch
            {
                    // Skip processes we can't access
                continue;
            }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to discover processes: {ex.Message}");
        }

        // Sort by process name for better UX
        _processes = list.OrderBy(p => p.Name).Distinct().ToList();

        await DispatcherQueue.EnqueueAsync(() =>
        {
            ProcessBox.IsEnabled = true;
            ProcessRing.IsActive = false;
            ProcessRing.Visibility = Visibility.Collapsed;
            
            Debug.WriteLine($"Discovered {_processes.Count} processes");
            
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

    // P/Invoke for getting process ID from window handle
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

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

            // Simple visual confirmation replace with your own UI if you like
            RegionSettings.Children.Add(new TextBlock
            {
                Text = $"{r.Width} x {r.Height} at ({r.X}, {r.Y})",
                Margin = new Thickness(0, 4, 0, 0)
            });
            
            // Show debug information and take screenshot
            RegionDebugInfo.Visibility = Visibility.Visible;
            RegionCoordinatesText.Text = $"Coordinates: X={r.X}, Y={r.Y}, Width={r.Width}, Height={r.Height}";
            
            // Analyze monitors and take screenshot
            await AnalyzeRegionAndTakeScreenshotAsync(r);
        }
    }
    
    private async Task AnalyzeRegionAndTakeScreenshotAsync(RectInt32 region)
    {
        try
        {
            // Get all displays
            var displays = await Task.Run(() => Recorder.GetDisplays());
            var intersectingDisplays = new List<(RecordableDisplay display, System.Drawing.Rectangle overlap)>();
            var regionRect = new System.Drawing.Rectangle(region.X, region.Y, region.Width, region.Height);
            
            Debug.WriteLine($"🎯 Region to analyze: X={region.X}, Y={region.Y}, W={region.Width}, H={region.Height}");
            
            // Get virtual screen bounds for debugging
            int virtualLeft = GetSystemMetrics(76);   // SM_XVIRTUALSCREEN
            int virtualTop = GetSystemMetrics(77);    // SM_YVIRTUALSCREEN
            int virtualWidth = GetSystemMetrics(78);  // SM_CXVIRTUALSCREEN
            int virtualHeight = GetSystemMetrics(79); // SM_CYVIRTUALSCREEN
            
            Debug.WriteLine($"📺 Virtual Screen: X={virtualLeft}, Y={virtualTop}, W={virtualWidth}, H={virtualHeight}");
            Debug.WriteLine($"📺 Found {displays.Count()} displays from ScreenRecorderLib");
            
            // First, enumerate all monitors directly to get their positions
            var monitorsByDeviceName = new Dictionary<string, System.Drawing.Rectangle>();
            var monitorsList = new List<(string deviceName, System.Drawing.Rectangle bounds)>();
            
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
            {
                var info = new MONITORINFOEX();
                if (GetMonitorInfo(hMonitor, info))
                {
                    var rect = new System.Drawing.Rectangle(
                        info.rcMonitor.left,
                        info.rcMonitor.top,
                        info.rcMonitor.right - info.rcMonitor.left,
                        info.rcMonitor.bottom - info.rcMonitor.top
                    );
                    
                    string deviceName = info.szDevice.TrimEnd('\0');
                    monitorsByDeviceName[deviceName] = rect;
                    monitorsList.Add((deviceName, rect));
                    
                    Debug.WriteLine($"🖥️ Found Monitor: '{deviceName}' at {rect.X},{rect.Y} {rect.Width}x{rect.Height}");
                }
                return true;
            }, IntPtr.Zero);
            
            Debug.WriteLine($"📺 Total monitors from EnumDisplayMonitors: {monitorsList.Count}");
            
            // Find which monitors the region intersects with
            foreach (var display in displays)
            {
                System.Drawing.Rectangle displayRect = new System.Drawing.Rectangle();
                bool foundMonitor = false;
                
                Debug.WriteLine($"🔍 Checking display: {display.FriendlyName} (DeviceName: {display.DeviceName})");
                
                // Try exact match first
                if (display.DeviceName != null && monitorsByDeviceName.ContainsKey(display.DeviceName))
                {
                    displayRect = monitorsByDeviceName[display.DeviceName];
                    foundMonitor = true;
                    Debug.WriteLine($"   ✅ Found exact match: {displayRect.X},{displayRect.Y} {displayRect.Width}x{displayRect.Height}");
                }
                else if (display.DeviceName != null)
                {
                    // Try without the \\.\  prefix
                    string simpleName = display.DeviceName.Replace(@"\\.\", "");
                    if (monitorsByDeviceName.ContainsKey(simpleName))
                    {
                        displayRect = monitorsByDeviceName[simpleName];
                        foundMonitor = true;
                        Debug.WriteLine($"   ✅ Found match without prefix: {displayRect.X},{displayRect.Y} {displayRect.Width}x{displayRect.Height}");
                    }
                    else
                    {
                        // Try to match by display number
                        var match = System.Text.RegularExpressions.Regex.Match(display.DeviceName, @"DISPLAY(\d+)");
                        if (match.Success && int.TryParse(match.Groups[1].Value, out int displayNum))
                        {
                            // Display numbers are 1-based, list is 0-based
                            int index = displayNum - 1;
                            if (index >= 0 && index < monitorsList.Count)
                            {
                                displayRect = monitorsList[index].bounds;
                                foundMonitor = true;
                                Debug.WriteLine($"   📍 Using monitor by index {index}: {displayRect.X},{displayRect.Y} {displayRect.Width}x{displayRect.Height}");
                            }
                            else
                            {
                                Debug.WriteLine($"   ❌ Display index {index} out of range (have {monitorsList.Count} monitors)");
                            }
                        }
                    }
                }
                
                if (!foundMonitor)
                {
                    Debug.WriteLine($"   ❌ Could not find monitor bounds for {display.DeviceName}");
                    
                    // Last resort: try to match by friendly name or just use index
                    if (displays.Count() == monitorsList.Count)
                    {
                        var displayIndex = displays.ToList().IndexOf(display);
                        if (displayIndex >= 0 && displayIndex < monitorsList.Count)
                        {
                            displayRect = monitorsList[displayIndex].bounds;
                            foundMonitor = true;
                            Debug.WriteLine($"   🎯 Using fallback index matching: {displayRect.X},{displayRect.Y} {displayRect.Width}x{displayRect.Height}");
                        }
                    }
                }
                
                if (!foundMonitor)
                {
                    Debug.WriteLine($"   ❌ Skipping display - could not determine bounds");
                    continue;
                }
                
                Debug.WriteLine($"🖥️ Display '{display.FriendlyName}' bounds: X={displayRect.X}, Y={displayRect.Y}, W={displayRect.Width}, H={displayRect.Height}");
                
                var overlap = System.Drawing.Rectangle.Intersect(regionRect, displayRect);
                if (overlap.Width > 0 && overlap.Height > 0)
                {
                    intersectingDisplays.Add((display, overlap));
                    Debug.WriteLine($"   ✅ Overlaps! Area: {overlap.Width}x{overlap.Height}");
                }
                else
                {
                    Debug.WriteLine($"   ❌ No overlap");
                }
            }
            
            // Show information about the selected region
            RegionCoordinatesText.Text = $"Coordinates: X={region.X}, Y={region.Y}, Width={region.Width}, Height={region.Height}";
            
            if (intersectingDisplays.Count == 0)
            {
                RegionMonitorsText.Text = "Region does not intersect with any monitor!";
                Debug.WriteLine($"❌ Region does not intersect with any monitor!");
                Debug.WriteLine($"   Region: {region.X},{region.Y} {region.Width}x{region.Height}");
                Debug.WriteLine($"   Virtual Screen: {virtualLeft},{virtualTop} {virtualWidth}x{virtualHeight}");
                return;
            }
            else if (intersectingDisplays.Count == 1)
            {
                var display = intersectingDisplays[0].display;
                RegionMonitorsText.Text = $"Monitor: {display.FriendlyName}";
            }
            else
            {
                var displayNames = string.Join(", ", intersectingDisplays.Select(d => d.display.FriendlyName));
                RegionMonitorsText.Text = $"Spans {intersectingDisplays.Count} monitors: {displayNames}";
            }
            
            // Calculate output dimensions for screenshot
            var recordingSources = intersectingDisplays.Select(d => new DisplayRecordingSource(d.display)
            {
                RecorderApi = RecorderApi.WindowsGraphicsCapture,
                IsCursorCaptureEnabled = false,
                IsBorderRequired = false
            }).Cast<RecordingSourceBase>().ToList();
            
            var outputDimensions = Recorder.GetOutputDimensionsForRecordingSources(recordingSources);
            double scale = Math.Min(1.0, 400.0 / outputDimensions.CombinedOutputSize.Width);
            
            // Take a screenshot for preview
            try
            {
                RecorderOptions options = new RecorderOptions
                {
                    OutputOptions = new OutputOptions
                    {
                        RecorderMode = RecorderMode.Screenshot,
                        SourceRect = null, // Capture everything
                        Stretch = StretchMode.Uniform
                    },
                    SourceOptions = new SourceOptions
                    {
                        RecordingSources = intersectingDisplays.Select(d => new DisplayRecordingSource(d.display)
                        {
                            RecorderApi = RecorderApi.WindowsGraphicsCapture,
                            IsCursorCaptureEnabled = false,
                            IsBorderRequired = false
                        }).Cast<RecordingSourceBase>().ToList()
                    },
                    SnapshotOptions = new SnapshotOptions
                    {
                        SnapshotFormat = ImageFormat.PNG
                    }
                };
                
                // Output dimensions tell us the combined output size
                if (intersectingDisplays.Count > 1)
                {
                    options.OutputOptions.OutputFrameSize = new ScreenSize(
                        (int)(outputDimensions.CombinedOutputSize.Width * scale),
                        (int)(outputDimensions.CombinedOutputSize.Height * scale)
                    );
                }
                else
                {
                    options.OutputOptions.OutputFrameSize = new ScreenSize(
                        (int)(intersectingDisplays[0].overlap.Width * scale),
                        (int)(intersectingDisplays[0].overlap.Height * scale)
                    );
                }
                
                Debug.WriteLine($"📷 Taking screenshot with {intersectingDisplays.Count} sources");
                Debug.WriteLine($"   Output size: {options.OutputOptions.OutputFrameSize.Width}x{options.OutputOptions.OutputFrameSize.Height}");
                
                var screenshotPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"region_preview_{Guid.NewGuid()}.png");
                var recorder = Recorder.CreateRecorder(options);
                
                // Use a simpler approach - just take the screenshot directly
                try
                {
                    recorder.Record(screenshotPath);
                    
                    // Wait a bit for the screenshot to be saved
                    await Task.Delay(500);
                    
                    // Check if file exists
                    if (System.IO.File.Exists(screenshotPath))
                    {
                        Debug.WriteLine($"✅ Screenshot saved to: {screenshotPath}");
                        
                        // Load and display the screenshot with red border overlay
                        await DisplayScreenshotWithBorderAsync(screenshotPath, regionRect, outputDimensions, scale);
                    }
                    else
                    {
                        Debug.WriteLine($"❌ Screenshot file not found at: {screenshotPath}");
                        ShowFallbackPreview(regionRect, intersectingDisplays);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"❌ Screenshot error: {ex.Message}");
                    ShowFallbackPreview(regionRect, intersectingDisplays);
                }
                finally
                {
                    recorder?.Dispose();
                    
                    // Clean up temp file
                    try
                    {
                        if (System.IO.File.Exists(screenshotPath))
                            System.IO.File.Delete(screenshotPath);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Failed to create screenshot recorder: {ex.Message}");
                ShowFallbackPreview(regionRect, intersectingDisplays);
            }
        }
        catch (Exception ex)
        {
            RegionMonitorsText.Text = $"Error: {ex.Message}";
            Debug.WriteLine($"❌ Error in AnalyzeRegionAndTakeScreenshotAsync: {ex}");
        }
    }
    
    private async Task DisplayScreenshotWithBorderAsync(string screenshotPath, System.Drawing.Rectangle regionRect, OutputDimensions outputDimensions, double scale)
    {
        try
        {
            // Load the screenshot
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(screenshotPath);
            using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            await bitmap.SetSourceAsync(stream);
            
            // Create a Canvas to hold the image and draw the border
            var canvas = new Canvas
            {
                Width = bitmap.PixelWidth,
                Height = bitmap.PixelHeight
            };
            
            // Add the screenshot image
            var image = new Image
            {
                Source = bitmap,
                Stretch = Stretch.None
            };
            canvas.Children.Add(image);
            
            // Calculate the position of the region border in the screenshot
            // The screenshot shows the combined output of all intersecting monitors
            var combinedBounds = outputDimensions.OutputCoordinates[0].Coordinates;
            
            // Calculate the region position relative to the combined output
            var borderX = (regionRect.X - combinedBounds.Left) * scale;
            var borderY = (regionRect.Y - combinedBounds.Top) * scale;
            var borderWidth = regionRect.Width * scale;
            var borderHeight = regionRect.Height * scale;
            
            // Add red border rectangle
            var border = new Rectangle
            {
                Width = borderWidth,
                Height = borderHeight,
                Stroke = new SolidColorBrush(Microsoft.UI.Colors.Red),
                StrokeThickness = 3,
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 0, 0)) // Semi-transparent red
            };
            
            Canvas.SetLeft(border, borderX);
            Canvas.SetTop(border, borderY);
            canvas.Children.Add(border);
            
            // Display the canvas in a ViewBox for proper scaling
            var viewBox = new Viewbox
            {
                Stretch = Stretch.Uniform,
                MaxHeight = 200,
                Child = canvas
            };
            
            // Clear any previous screenshot and add the new one
            ScreenshotBorder.Child = viewBox;
            ScreenshotBorder.Visibility = Visibility.Visible;
            
            // Clean up temp file
            try { System.IO.File.Delete(screenshotPath); } catch { }
        }
        catch (Exception ex)
        {
            RegionMonitorsText.Text += $"\nDisplay error: {ex.Message}";
        }
    }
    
    // Monitor info structures and P/Invoke
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice = "";
        
        public MONITORINFOEX()
        {
            cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
            szDevice = string.Empty;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
    
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);
    
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, [In, Out] MONITORINFOEX lpmi);

    private static MONITORINFOEX? GetMonitorInfoFromDisplay(RecordableDisplay display)
    {
        // This method is no longer needed since we're using ScreenRecorderLib's GetOutputDimensionsForRecordingSources
        return null;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private static System.Drawing.Rectangle? GetMonitorBounds(string deviceName)
    {
        System.Drawing.Rectangle? result = null;
        
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
        {
            var info = new MONITORINFOEX();
            if (GetMonitorInfo(hMonitor, info))
            {
                // Match by device name or primary monitor
                if (info.szDevice == deviceName || 
                    (string.IsNullOrEmpty(deviceName) && (info.dwFlags & 1) != 0)) // MONITORINFOF_PRIMARY
                {
                    result = new System.Drawing.Rectangle(
                        info.rcMonitor.left,
                        info.rcMonitor.top,
                        info.rcMonitor.right - info.rcMonitor.left,
                        info.rcMonitor.bottom - info.rcMonitor.top
                    );
                    return false; // Stop enumeration
                }
            }
            return true; // Continue enumeration
        }, IntPtr.Zero);
        
        return result;
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
                _ => SelectedSourceType
            };
        }
    }

    private void UpdateSettingsPanels()
    {
        if (MonitorSettings == null) return;   // designer safety
        string tag = (KindBox.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
        MonitorSettings.Visibility = tag == "Monitor" ? Visibility.Visible : Visibility.Collapsed;
        ProcessSettings.Visibility = tag == "Process" ? Visibility.Visible : Visibility.Collapsed;
        RegionSettings.Visibility = tag == "Region" ? Visibility.Visible : Visibility.Collapsed;
        WebcamSettings.Visibility = tag == "Webcam" ? Visibility.Visible : Visibility.Collapsed;
        WebsiteSettings.Visibility = tag == "Website" ? Visibility.Visible : Visibility.Collapsed;
    }

    // --------------------------------------------
    // save
    // --------------------------------------------
    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (MonitorSettings.Visibility == Visibility.Visible && MonitorCombo.SelectedItem is ComboBoxItem mi)
            SelectedMonitorDeviceId = mi.Tag as string;
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
        else if (WebcamSettings.Visibility == Visibility.Visible && WebcamCombo.SelectedItem is ComboBoxItem wi)
            SelectedWebcamDeviceId = wi.Tag as string;
        else if (WebsiteSettings.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(WebsiteUrlBox.Text))
        {
            WebsiteUrl = WebsiteUrlBox.Text;
            
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
            _ = Task.Run(async () =>
            {
                WebsiteNavigationState = await GetNavigationStateAsync();
            });
        }
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

    private void ShowFallbackPreview(System.Drawing.Rectangle regionRect, List<(RecordableDisplay display, System.Drawing.Rectangle overlap)> intersectingDisplays)
    {
        // Show a simple text-based preview as fallback
        ScreenshotBorder.Visibility = Visibility.Visible;
        
        // Create a simple visual representation using WriteableBitmap
        var bitmap = new WriteableBitmap(400, 300);
        
        // Fill with gray background
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            byte[] pixelData = new byte[400 * 300 * 4];
            for (int i = 0; i < pixelData.Length; i += 4)
            {
                pixelData[i] = 240;     // B
                pixelData[i + 1] = 240; // G
                pixelData[i + 2] = 240; // R
                pixelData[i + 3] = 255; // A
            }
            stream.Write(pixelData, 0, pixelData.Length);
        }
        
        ScreenshotImage.Source = bitmap;
        
        // Update the text to show we couldn't get a screenshot
        RegionMonitorsText.Text += "\n\nScreenshot preview unavailable - recording will work correctly when started.";
    }

    // Event handlers for enhanced website controls
    private void WebsiteZoomSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (WebsiteZoomLabel != null)
        {
            WebsiteZoomLabel.Text = $"{(int)(e.NewValue * 100)}%";
        }
        
        // Apply zoom to preview if loaded
        if (_isWebViewInitialized && WebsitePreview.CoreWebView2 != null)
        {
            _ = ApplyZoomToPreviewAsync(e.NewValue);
        }
    }

    private void WebsiteUserAgentCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WebsiteUserAgentCombo.SelectedItem is ComboBoxItem item)
        {
            if (WebsiteCustomUserAgentBox != null)
            {
                WebsiteCustomUserAgentBox.Visibility = item.Tag?.ToString() == "custom" ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }
    
    // New WebView functionality
    private void WebsiteUrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (WebsitePreviewSection != null)
        {
            // Show/hide preview section based on whether URL is entered
            bool hasUrl = !string.IsNullOrWhiteSpace(WebsiteUrlBox.Text);
            WebsitePreviewSection.Visibility = hasUrl ? Visibility.Visible : Visibility.Collapsed;
            
            if (LoadWebsiteButton != null)
            {
                LoadWebsiteButton.IsEnabled = hasUrl && Uri.TryCreate(WebsiteUrlBox.Text.Trim(), UriKind.Absolute, out var uri) && 
                                             (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
            }
        }
    }
    
    private async void LoadWebsiteButton_Click(object sender, RoutedEventArgs e)
    {
        var url = WebsiteUrlBox.Text?.Trim();
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || 
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }
        
        try
        {
            WebsiteLoadingRing.Visibility = Visibility.Visible;
            LoadWebsiteButton.IsEnabled = false;
            
            // Initialize WebView2 if not already done
            if (!_isWebViewInitialized)
            {
                await WebsitePreview.EnsureCoreWebView2Async();
                
                // Apply user agent before navigation
                var selectedUserAgent = GetSelectedUserAgent();
                if (!string.IsNullOrEmpty(selectedUserAgent))
                {
                    WebsitePreview.CoreWebView2.Settings.UserAgent = selectedUserAgent;
                }
                
                _isWebViewInitialized = true;
            }
            
            // Navigate to the URL
            WebsitePreview.CoreWebView2.Navigate(url);
            
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading website: {ex.Message}");
            
            WebsiteLoadingRing.Visibility = Visibility.Collapsed;
            LoadWebsiteButton.IsEnabled = true;
            
            // Show error message
            var dialog = new ContentDialog
            {
                Title = "Error",
                Content = $"Failed to load website: {ex.Message}",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            _ = dialog.ShowAsync();
        }
    }
    
    private void RefreshWebsiteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isWebViewInitialized && WebsitePreview.CoreWebView2 != null)
        {
            WebsitePreview.CoreWebView2.Reload();
        }
    }
    
    private async void WebsitePreview_NavigationCompleted(object sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
    {
        try
        {
            WebsiteLoadingRing.Visibility = Visibility.Collapsed;
            LoadWebsiteButton.IsEnabled = true;
            
            if (e.IsSuccess)
            {
                WebsitePreview.Visibility = Visibility.Visible;
                WebsitePreviewPlaceholder.Visibility = Visibility.Collapsed;
                RefreshWebsiteButton.Visibility = Visibility.Visible;
                
                // Apply zoom if set
                if (WebsiteZoomSlider != null && WebsiteZoomSlider.Value != 1.0)
                {
                    await ApplyZoomToPreviewAsync(WebsiteZoomSlider.Value);
                }
                
                // Store navigation state for persistence
                if (WebsitePreview.CoreWebView2 != null)
                {
                    WebsiteNavigationState = WebsitePreview.CoreWebView2.Source;
                }
            }
            else
            {
                // Show error
                WebsitePreviewPlaceholder.Text = "Failed to load website. Please check the URL and try again.";
                WebsitePreviewPlaceholder.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Navigation completed error: {ex.Message}");
        }
    }
    
    private async Task ApplyZoomToPreviewAsync(double zoomFactor)
    {
        try
        {
            if (WebsitePreview.CoreWebView2 != null)
            {
                // Try native ZoomFactor property first
                var zoomProp = WebsitePreview.CoreWebView2.GetType().GetProperty("ZoomFactor");
                if (zoomProp != null && zoomProp.CanWrite)
                {
                    zoomProp.SetValue(WebsitePreview.CoreWebView2, zoomFactor);
                    return;
                }
                
                // Fallback to CSS zoom
                var zoomCss = zoomFactor.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                string js = $"document.documentElement.style.zoom='{zoomCss}'; document.body.style.zoom='{zoomCss}';";
                await WebsitePreview.CoreWebView2.ExecuteScriptAsync(js);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error applying zoom: {ex.Message}");
        }
    }
    
    private string GetSelectedUserAgent()
    {
        if (WebsiteUserAgentCombo?.SelectedItem is ComboBoxItem item)
        {
            if (item.Tag?.ToString() == "custom")
            {
                return WebsiteCustomUserAgentBox?.Text ?? "";
            }
            return item.Tag?.ToString() ?? "";
        }
        return "";
    }
    
    private async Task<string?> GetNavigationStateAsync()
    {
        try
        {
            if (_isWebViewInitialized && WebsitePreview.CoreWebView2 != null)
            {
                // Get the current URL and any session data
                var currentUrl = WebsitePreview.CoreWebView2.Source;
                
                // For now, we'll just store the URL. In the future, we could store cookies, localStorage, etc.
                return currentUrl;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error getting navigation state: {ex.Message}");
        }
        return null;
    }
    
    private async Task RestoreNavigationStateAsync(string navigationState)
    {
        try
        {
            if (!string.IsNullOrEmpty(navigationState) && Uri.TryCreate(navigationState, UriKind.Absolute, out var uri))
            {
                if (!_isWebViewInitialized)
                {
                    await WebsitePreview.EnsureCoreWebView2Async();
                    _isWebViewInitialized = true;
                }
                
                WebsitePreview.CoreWebView2.Navigate(navigationState);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error restoring navigation state: {ex.Message}");
        }
    }
}