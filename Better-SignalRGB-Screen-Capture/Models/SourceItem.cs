using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Windows.Graphics;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;

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
    private RectInt32? _regionBounds;
    private string? _webcamDeviceId;
    private string? _websiteUrl;
    
    // Website-specific properties for enhanced control
    private double _websiteZoom = 1.0;
    private int _websiteRefreshInterval = 0; // 0 = no auto-refresh, in seconds
    private string _websiteUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    private int _websiteWidth = 1920;
    private int _websiteHeight = 1080;
    
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
        set => SetProperty(ref _websiteZoom, Math.Max(0.25, Math.Min(4.0, value))); // Clamp between 25% and 400%
    }
    
    public int WebsiteRefreshInterval
    {
        get => _websiteRefreshInterval;
        set => SetProperty(ref _websiteRefreshInterval, Math.Max(0, value)); // Minimum 0 (no refresh)
    }
    
    public string WebsiteUserAgent
    {
        get => _websiteUserAgent;
        set => SetProperty(ref _websiteUserAgent, value ?? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
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
    
    // DeviceId property for compatibility with new CaptureService
    public string DeviceId => Type switch
    {
        SourceType.Monitor or SourceType.Display => MonitorDeviceId ?? string.Empty,
        SourceType.Process or SourceType.Window => ProcessId?.ToString() ?? string.Empty,
        SourceType.Region => RegionBounds.HasValue ? $"{RegionBounds.Value.X},{RegionBounds.Value.Y},{RegionBounds.Value.Width},{RegionBounds.Value.Height}" : string.Empty,
        SourceType.Webcam => WebcamDeviceId ?? string.Empty,
        SourceType.Website => WebsiteUrl ?? string.Empty,
        _ => string.Empty
    };
    
    // Canvas position and size (in canvas coordinates 0-800, 0-600)
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
        set => SetProperty(ref _canvasWidth, value);
    }
    
    public int CanvasHeight
    {
        get => _canvasHeight;
        set => SetProperty(ref _canvasHeight, value);
    }
    
    public double Opacity
    {
        get => _opacity;
        set => SetProperty(ref _opacity, value);
    }

    public double CropLeftPct
    {
        get => _cropLeftPct;
        set => SetProperty(ref _cropLeftPct, value);
    }

    public double CropTopPct
    {
        get => _cropTopPct;
        set => SetProperty(ref _cropTopPct, value);
    }

    public double CropRightPct
    {
        get => _cropRightPct;
        set => SetProperty(ref _cropRightPct, value);
    }

    public double CropBottomPct
    {
        get => _cropBottomPct;
        set => SetProperty(ref _cropBottomPct, value);
    }

    public int CropRotation
    {
        get => _cropRotation;
        set => SetProperty(ref _cropRotation, value);
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
        set => SetProperty(ref _rotation, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
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
        if (Type != SourceType.Process || string.IsNullOrEmpty(ProcessPath))
            return null;
            
        try
        {
            var processes = System.Diagnostics.Process.GetProcesses();
            foreach (var process in processes)
            {
                try
                {
                    // Try exact path match first
                    var processPath = process.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(processPath) && 
                        string.Equals(processPath, ProcessPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return process.Id;
                    }
                    
                    // If ProcessPath is just a filename (like "notepad.exe"), try matching by process name
                    if (ProcessPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        var processFileName = System.IO.Path.GetFileName(ProcessPath);
                        var actualProcessName = process.ProcessName + ".exe";
                        
                        if (string.Equals(processFileName, actualProcessName, StringComparison.OrdinalIgnoreCase))
                        {
                            return process.Id;
                        }
                    }
                }
                catch
                {
                    // Skip processes we can't access
                    continue;
                }
            }
        }
        catch
        {
            // Error accessing processes
        }
        
        return null;
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
            _ => "Unknown Source"
        };
    }
    
    private string GetDisplaySubtitle()
    {
        return Type switch
        {
            SourceType.Monitor => MonitorDeviceId ?? $"ID: {Id.ToString()[..8]}",
            SourceType.Process => !string.IsNullOrEmpty(ProcessPath) ? System.IO.Path.GetFileName(ProcessPath) : $"ID: {Id.ToString()[..8]}",
            SourceType.Region => RegionBounds.HasValue ? $"{RegionBounds.Value.Width}x{RegionBounds.Value.Height}" : $"ID: {Id.ToString()[..8]}",
            SourceType.Webcam => WebcamDeviceId ?? $"ID: {Id.ToString()[..8]}",
            SourceType.Website => WebsiteUrl ?? $"ID: {Id.ToString()[..8]}",
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

    public SourceItem Clone()
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
            RegionBounds = this.RegionBounds.HasValue ? new RectInt32(this.RegionBounds.Value.X, this.RegionBounds.Value.Y, this.RegionBounds.Value.Width, this.RegionBounds.Value.Height) : null,
            WebcamDeviceId = this.WebcamDeviceId,
            WebsiteUrl = this.WebsiteUrl,
            
            // Copy website-specific properties
            WebsiteZoom = this.WebsiteZoom,
            WebsiteRefreshInterval = this.WebsiteRefreshInterval,
            WebsiteUserAgent = this.WebsiteUserAgent,
            WebsiteWidth = this.WebsiteWidth,
            WebsiteHeight = this.WebsiteHeight,

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
            Rotation = this.Rotation,

            // Do not copy selection state
            IsSelected = false
        };
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
    Website
}

public class UndoRedoManager
{
    private readonly List<string> _undoStack = new();
    private readonly List<string> _redoStack = new();
    private const int MaxHistorySize = 50;

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public event EventHandler? CanUndoRedoChanged;

    public void SaveState(ObservableCollection<SourceItem> sources)
    {
        var state = SerializeSources(sources);
        
        // Add to undo stack
        _undoStack.Add(state);
        
        // Limit stack size
        if (_undoStack.Count > MaxHistorySize)
        {
            _undoStack.RemoveAt(0);
        }
        
        // Clear redo stack when new action is performed
        _redoStack.Clear();
        
        CanUndoRedoChanged?.Invoke(this, EventArgs.Empty);
    }

    public SourceItem[]? Undo(ObservableCollection<SourceItem> currentSources)
    {
        if (!CanUndo) return null;

        // Save current state to redo stack
        var currentState = SerializeSources(currentSources);
        _redoStack.Add(currentState);
        
        // Limit redo stack size
        if (_redoStack.Count > MaxHistorySize)
        {
            _redoStack.RemoveAt(0);
        }

        // Get previous state
        var previousState = _undoStack.Last();
        _undoStack.RemoveAt(_undoStack.Count - 1);
        
        CanUndoRedoChanged?.Invoke(this, EventArgs.Empty);
        
        return DeserializeSources(previousState);
    }

    public SourceItem[]? Redo(ObservableCollection<SourceItem> currentSources)
    {
        if (!CanRedo) return null;

        // Save current state to undo stack
        var currentState = SerializeSources(currentSources);
        _undoStack.Add(currentState);
        
        // Limit undo stack size
        if (_undoStack.Count > MaxHistorySize)
        {
            _undoStack.RemoveAt(0);
        }

        // Get next state
        var nextState = _redoStack.Last();
        _redoStack.RemoveAt(_redoStack.Count - 1);
        
        CanUndoRedoChanged?.Invoke(this, EventArgs.Empty);
        
        return DeserializeSources(nextState);
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        CanUndoRedoChanged?.Invoke(this, EventArgs.Empty);
    }

    private string SerializeSources(ObservableCollection<SourceItem> sources)
    {
        return JsonSerializer.Serialize(sources.ToArray(), new JsonSerializerOptions 
        { 
            WriteIndented = false,
            IncludeFields = false
        });
    }

    private SourceItem[]? DeserializeSources(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SourceItem[]>(json);
        }
        catch
        {
            return null;
        }
    }
} 