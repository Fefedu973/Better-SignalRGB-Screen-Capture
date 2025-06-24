using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Windows.Graphics;

namespace Better_SignalRGB_Screen_Capture.Models;

public class SourceItem : INotifyPropertyChanged
{
    private int _canvasX;
    private int _canvasY;
    private int _canvasWidth = 100;
    private int _canvasHeight = 80;
    private string _name = string.Empty;

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
            }
        }
    }
    
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
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

public enum SourceType
{
    Monitor,
    Process,
    Region,
    Webcam
} 