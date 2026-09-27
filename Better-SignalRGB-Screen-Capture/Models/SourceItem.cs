using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Windows.Graphics;
using System.Collections.Generic;
using System.Linq;

namespace Better_SignalRGB_Screen_Capture.Models;

public class SourceItem : INotifyPropertyChanged
{
    private int _canvasX;
    private int _canvasY;
    private int _canvasWidth = 100;
    private int _canvasHeight = 80;
    private string _name = string.Empty;
    private double _opacity = 1.0;
    private double _cropLeftPct;
    private double _cropTopPct;
    private double _cropRightPct;
    private double _cropBottomPct;
    private bool _isMirroredHorizontally;
    private bool _isMirroredVertically;
    private bool _isLivePreviewEnabled = true;
    private bool _isSelected;
    private bool _isLocked;
    private int _rotation;
    private int _cropRotation;

    public Guid Id { get; set; } = Guid.NewGuid();
    
    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }
    
    private SourceType _type;
    public SourceType Type
    {
        get => _type;
        set
        {
            if (_type != value)
            {
                _type = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(DisplaySubtitle));
            }
        }
    }
    
    // Source-specific properties
    private string? _monitorDeviceId;
    private int? _processId;
    private string? _processPath;
    private long? _windowHandle;
    private string? _windowTitle;
    private RectInt32? _regionBounds;
    private string? _webcamDeviceId;
    private string? _webcamFormatId;
    private string? _websiteUrl;
    
    // Website-specific properties for enhanced control
    private double _websiteZoom = 1.0;
    private int _websiteRefreshInterval = 0; // 0 = no auto-refresh, in seconds
    private string _websiteUserAgent = string.Empty;
    private int _websiteWidth = 1920;
    private int _websiteHeight = 1080;
    private string? _websiteNavigationState;
    
    public string? MonitorDeviceId
    {
        get => _monitorDeviceId;
        set
        {
            if (_monitorDeviceId != value)
            {
                _monitorDeviceId = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplaySubtitle));
            }
        }
    }
    
    public int? ProcessId
    {
        get => _processId;
        set
        {
            if (_processId != value)
            {
                _processId = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplaySubtitle));
            }
        }
    }
    
    public string? ProcessPath
    {
        get => _processPath;
        set
        {
            if (_processPath != value)
            {
                _processPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(DisplaySubtitle));
            }
        }
    }
    
    // Handles are hints for the current Windows session; the capture resolver verifies ownership.
    public long? WindowHandle
    {
        get => _windowHandle;
        set => SetProperty(ref _windowHandle, value);
    }

    public string? WindowTitle
    {
        get => _windowTitle;
        set
        {
            if (SetProperty(ref _windowTitle, value)) OnPropertyChanged(nameof(DisplaySubtitle));
        }
    }

    public string? WebcamFormatId
    {
        get => _webcamFormatId;
        set => SetProperty(ref _webcamFormatId, value);
    }

    public RectInt32? RegionBounds
    {
        get => _regionBounds;
        set
        {
            if (_regionBounds != value)
            {
                _regionBounds = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplaySubtitle));
                OnPropertyChanged(nameof(RegionX));
                OnPropertyChanged(nameof(RegionY));
                OnPropertyChanged(nameof(RegionWidth));
                OnPropertyChanged(nameof(RegionHeight));
            }
        }
    }
    
    // Helper properties for easier access to region bounds
    public int? RegionX => RegionBounds?.X;
    public int? RegionY => RegionBounds?.Y;
    public int? RegionWidth => RegionBounds?.Width;
    public int? RegionHeight => RegionBounds?.Height;
    
    public string? WebcamDeviceId
    {
        get => _webcamDeviceId;
        set
        {
            if (_webcamDeviceId != value)
            {
                _webcamDeviceId = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplaySubtitle));
            }
        }
    }
    
    public string? WebsiteUrl
    {
        get => _websiteUrl;
        set
        {
            if (_websiteUrl != value)
            {
                _websiteUrl = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(DisplaySubtitle));
            }
        }
    }
    
    public double WebsiteZoom
    {
        get => _websiteZoom;
        set => SetProperty(ref _websiteZoom, double.IsFinite(value) ? Math.Clamp(value, 0.25, 4.0) : 1.0);
    }
    
    public int WebsiteRefreshInterval
    {
        get => _websiteRefreshInterval;
        set => SetProperty(ref _websiteRefreshInterval, Math.Max(0, value)); // Minimum 0 (no refresh)
    }
    
    public string WebsiteUserAgent
    {
        get => _websiteUserAgent;
        set => SetProperty(ref _websiteUserAgent, value ?? string.Empty);
    }
    
    public int WebsiteWidth
    {
        get => _websiteWidth;
        set => SetProperty(ref _websiteWidth, Math.Max(320, Math.Min(7680, value))); // Clamp between 320px and 7680px (8K)
    }
    
    public int WebsiteHeight
    {
        get => _websiteHeight;
        set => SetProperty(ref _websiteHeight, Math.Max(240, Math.Min(4320, value))); // Clamp between 240px and 4320px (8K)
    }
    
    public string? WebsiteNavigationState
    {
        get => _websiteNavigationState;
        set => SetProperty(ref _websiteNavigationState, value);
    }
    
    // DeviceId property for compatibility with new CaptureService
    public string DeviceId => Type switch
    {
        SourceType.Monitor or SourceType.WallpaperEngine => MonitorDeviceId ?? string.Empty,
        SourceType.Process or SourceType.Window => ProcessId?.ToString() ?? string.Empty,
        SourceType.Region => RegionBounds.HasValue ? $"{RegionBounds.Value.X},{RegionBounds.Value.Y},{RegionBounds.Value.Width},{RegionBounds.Value.Height}" : string.Empty,
        SourceType.Webcam => WebcamDeviceId ?? string.Empty,
        SourceType.Website => WebsiteUrl ?? string.Empty,
        _ => string.Empty
    };
    
    // Logical coordinates of the 320 x 200 output canvas.
    public int CanvasX
    {
        get => _canvasX;
        set => SetProperty(ref _canvasX, value);
    }
    
    public int CanvasY
    {
        get => _canvasY;
        set => SetProperty(ref _canvasY, value);
    }
    
    public int CanvasWidth
    {
        get => _canvasWidth;
        set => SetProperty(ref _canvasWidth, Math.Clamp(value, 1, 7680));
    }
    
    public int CanvasHeight
    {
        get => _canvasHeight;
        set => SetProperty(ref _canvasHeight, Math.Clamp(value, 1, 4320));
    }
    
    public double Opacity
    {
        get => _opacity;
        set => SetProperty(ref _opacity, double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 1);
    }

    public double CropLeftPct
    {
        get => _cropLeftPct;
        set => SetProperty(ref _cropLeftPct, ClampCrop(value, _cropRightPct));
    }

    public double CropTopPct
    {
        get => _cropTopPct;
        set => SetProperty(ref _cropTopPct, ClampCrop(value, _cropBottomPct));
    }

    public double CropRightPct
    {
        get => _cropRightPct;
        set => SetProperty(ref _cropRightPct, ClampCrop(value, _cropLeftPct));
    }

    public double CropBottomPct
    {
        get => _cropBottomPct;
        set => SetProperty(ref _cropBottomPct, ClampCrop(value, _cropTopPct));
    }

    public int CropRotation
    {
        get => _cropRotation;
        set => SetProperty(ref _cropRotation, NormalizeCropRotation(value));
    }

    public bool IsMirroredHorizontally
    {
        get => _isMirroredHorizontally;
        set => SetProperty(ref _isMirroredHorizontally, value);
    }

    public bool IsMirroredVertically
    {
        get => _isMirroredVertically;
        set => SetProperty(ref _isMirroredVertically, value);
    }

    /// <summary>
    /// When false no preview frames are rendered for this source inside the design canvas.
    /// Does not affect the actual capture or MJPEG output.
    /// </summary>
    public bool IsLivePreviewEnabled
    {
        get => _isLivePreviewEnabled;
        set => SetProperty(ref _isLivePreviewEnabled, value);
    }

    public int Rotation
    {
        get => _rotation;
        set => SetProperty(ref _rotation, ((value % 360) + 360) % 360);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>Protects layout edits. Selection, naming, opacity, copying and deletion remain available.</summary>
    public bool IsLocked
    {
        get => _isLocked;
        set => SetProperty(ref _isLocked, value);
    }

    private static int NormalizeCropRotation(int angle) => ((angle % 360) + 540) % 360 - 180;

    private static double ClampCrop(double value, double opposite) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, Math.Max(0, 0.99 - opposite)) : 0;

    /// <summary>Applies an entire crop before notifying observers, so moving the crop cannot
    /// clamp against an edge from the previous gesture state.</summary>
    public void SetCrop(double left, double top, double right, double bottom, int rotation)
    {
        static (double first, double second) Normalize(double first, double second)
        {
            first = ClampCrop(first, 0);
            second = ClampCrop(second, 0);
            var sum = first + second;
            return sum > 0.99 ? (first * 0.99 / sum, second * 0.99 / sum) : (first, second);
        }
        (left, right) = Normalize(left, right);
        (top, bottom) = Normalize(top, bottom);
        rotation = NormalizeCropRotation(rotation);
        var leftChanged = _cropLeftPct != left;
        var rightChanged = _cropRightPct != right;
        var topChanged = _cropTopPct != top;
        var bottomChanged = _cropBottomPct != bottom;
        var rotationChanged = _cropRotation != rotation;
        (_cropLeftPct, _cropTopPct, _cropRightPct, _cropBottomPct, _cropRotation) = (left, top, right, bottom, rotation);
        if (leftChanged) OnPropertyChanged(nameof(CropLeftPct));
        if (topChanged) OnPropertyChanged(nameof(CropTopPct));
        if (rightChanged) OnPropertyChanged(nameof(CropRightPct));
        if (bottomChanged) OnPropertyChanged(nameof(CropBottomPct));
        if (rotationChanged) OnPropertyChanged(nameof(CropRotation));
    }

    // Visual properties
    public string DisplayName => GetDisplayName();
    public string DisplaySubtitle => GetDisplaySubtitle();
    
    /// <summary>
    /// Gets the current Process ID for this source if it's a running process.
    /// Returns null if the process is not currently running or if this is not a process source.
    /// </summary>
    public int? GetCurrentProcessId()
    {
        if (Type != SourceType.Process || string.IsNullOrWhiteSpace(ProcessPath)) return null;
        var fileName = System.IO.Path.GetFileNameWithoutExtension(ProcessPath);
        var exactPath = System.IO.Path.IsPathRooted(ProcessPath);
        Process[] processes;
        try { processes = Process.GetProcessesByName(fileName); }
        catch { return null; }
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (process.MainWindowHandle != IntPtr.Zero &&
                        (!exactPath || string.Equals(process.MainModule?.FileName, ProcessPath, StringComparison.OrdinalIgnoreCase)))
                        return process.Id;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Protected or exited processes cannot be captured.
                }
            }
            return null;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private string GetDisplayName()
    {
        // Always prioritize the friendly name if it exists
        if (!string.IsNullOrEmpty(Name))
            return Name;
            
        // Fallback to auto-generated names
        return Type switch
        {
            SourceType.Monitor => "Monitor Source",
            SourceType.Process => System.IO.Path.GetFileNameWithoutExtension(ProcessPath) ?? "Process Source",
            SourceType.Region => "Region Source",
            SourceType.Webcam => "Webcam Source",
            SourceType.Website => "Website Source",
            SourceType.WallpaperEngine => "Wallpaper Engine",
            _ => "Unknown Source"
        };
    }
    
    private string GetDisplaySubtitle()
    {
        return Type switch
        {
            SourceType.Monitor => MonitorDeviceId ?? $"ID: {Id.ToString()[..8]}",
            SourceType.Process => !string.IsNullOrWhiteSpace(WindowTitle) ? WindowTitle : !string.IsNullOrEmpty(ProcessPath) ? System.IO.Path.GetFileName(ProcessPath) : $"ID: {Id.ToString()[..8]}",
            SourceType.Region => RegionBounds.HasValue ? $"{RegionBounds.Value.Width}x{RegionBounds.Value.Height}" : $"ID: {Id.ToString()[..8]}",
            SourceType.Webcam => WebcamDeviceId ?? $"ID: {Id.ToString()[..8]}",
            SourceType.Website => WebsiteUrl ?? $"ID: {Id.ToString()[..8]}",
            SourceType.WallpaperEngine => $"Live wallpaper · {MonitorDeviceId ?? "No monitor selected"}",
            _ => $"ID: {Id.ToString()[..8]}"
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    public SourceItem Clone(bool preserveId = false)
    {
        var newItem = new SourceItem
        {
            // Do not copy Id, a new one will be assigned on paste
            Name = this.Name,
            Type = this.Type,

            // Copy source-specific properties
            MonitorDeviceId = this.MonitorDeviceId,
            ProcessId = this.ProcessId,
            ProcessPath = this.ProcessPath,
            WindowHandle = this.WindowHandle,
            WindowTitle = this.WindowTitle,
            RegionBounds = this.RegionBounds.HasValue ? new RectInt32(this.RegionBounds.Value.X, this.RegionBounds.Value.Y, this.RegionBounds.Value.Width, this.RegionBounds.Value.Height) : null,
            WebcamDeviceId = this.WebcamDeviceId,
            WebcamFormatId = this.WebcamFormatId,
            WebsiteUrl = this.WebsiteUrl,
            
            // Copy website-specific properties
            WebsiteZoom = this.WebsiteZoom,
            WebsiteRefreshInterval = this.WebsiteRefreshInterval,
            WebsiteUserAgent = this.WebsiteUserAgent,
            WebsiteWidth = this.WebsiteWidth,
            WebsiteHeight = this.WebsiteHeight,
            WebsiteNavigationState = this.WebsiteNavigationState,

            // Copy canvas and visual properties
            CanvasX = this.CanvasX,
            CanvasY = this.CanvasY,
            CanvasWidth = this.CanvasWidth,
            CanvasHeight = this.CanvasHeight,
            Opacity = this.Opacity,
            CropLeftPct = this.CropLeftPct,
            CropTopPct = this.CropTopPct,
            CropRightPct = this.CropRightPct,
            CropBottomPct = this.CropBottomPct,
            CropRotation = this.CropRotation,
            IsMirroredHorizontally = this.IsMirroredHorizontally,
            IsMirroredVertically = this.IsMirroredVertically,
            IsLivePreviewEnabled = this.IsLivePreviewEnabled,
            IsLocked = this.IsLocked,
            Rotation = this.Rotation,

            // Do not copy selection state
            IsSelected = false
        };
        if (preserveId) newItem.Id = Id;
        return newItem;
    }
}

public enum SourceType
{
    Monitor, // Kept for backward compatibility
    Display = Monitor, // New name for monitors/displays
    Process, // Kept for backward compatibility  
    Window = Process, // New name for windows/processes
    Region,
    Webcam,
    Website,
    WallpaperEngine = 5
}
