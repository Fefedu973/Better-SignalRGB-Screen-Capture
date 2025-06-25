using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Linq;
using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.Contracts.Services;

namespace Better_SignalRGB_Screen_Capture.ViewModels;

public partial class MainViewModel : ObservableRecipient
{
    private readonly ILocalSettingsService _localSettingsService;
    private readonly ICaptureService _captureService;
    private readonly IMjpegStreamingService _mjpegStreamingService;
    private readonly ICompositeFrameService _compositeFrameService;
    private const string SourcesSettingsKey = "SavedSources";
    private const string PreviewFpsSettingsKey = "PreviewFps";
    private CancellationTokenSource? _testFrameCancellation;
    private List<SourceItem> _copiedSources = new();
    
    public ObservableCollection<SourceItem> Sources { get; } = new();

    public ObservableCollection<SourceItem> SelectedSources { get; } = new();
    
    [ObservableProperty]
    private int previewFps = 30; // Default to 30 FPS
    
    [ObservableProperty]
    private bool isPreviewing;

    [ObservableProperty]
    private string? streamingUrl;

    public MainViewModel(ILocalSettingsService localSettingsService, ICaptureService captureService, IMjpegStreamingService mjpegStreamingService, ICompositeFrameService compositeFrameService)
    {
        _localSettingsService = localSettingsService;
        _captureService = captureService;
        _mjpegStreamingService = mjpegStreamingService;
        _compositeFrameService = compositeFrameService;
        
        // Subscribe to events
        _captureService.FrameAvailable += OnFrameAvailable;
        _mjpegStreamingService.StreamingUrlChanged += OnStreamingUrlChanged;
        _compositeFrameService.CompositeFrameAvailable += OnCompositeFrameAvailable;
        
        // Listen to Sources collection changes to attach/detach property change listeners
        Sources.CollectionChanged += Sources_CollectionChanged;
        
        _ = LoadSourcesAsync();
        _ = LoadSettingsAsync();
        
        // Initialize composite frame service with canvas size
        _compositeFrameService.SetCanvasSize(800, 600);
        
        // Start MJPEG streaming
        _ = Task.Run(async () => await _mjpegStreamingService.StartStreamingAsync());

        // Listen to selection changes
        SelectedSources.CollectionChanged += (s, e) => OnSelectionChanged();
    }
    
    partial void OnPreviewFpsChanged(int value)
    {
        // Save FPS setting when it changes
        _ = SavePreviewFpsAsync();
        
        // Update capture service framerate
        _captureService.SetFrameRate(value);
    }
    
    [RelayCommand]
    private async Task AddSourceAsync(SourceItem? newSource = null)
    {
        if (newSource != null)
        {
            // Find a good position for the new source on canvas
            var (x, y) = FindAvailableCanvasPosition();
            newSource.CanvasX = x;
            newSource.CanvasY = y;
            
            Sources.Add(newSource);
            await SaveSourcesAsync();
            
            // Start capture for the new source
            await _captureService.StartCaptureAsync(newSource);
        }
    }
    
    [RelayCommand]
    private async Task DeleteSourceAsync(object? parameter)
    {
        var sourcesToDelete = new List<SourceItem>();
        if (parameter is SourceItem source)
        {
            sourcesToDelete.Add(source);
        }
        else if (parameter is IEnumerable<SourceItem> sources)
        {
            sourcesToDelete.AddRange(sources);
        }
        else
        {
            sourcesToDelete.AddRange(SelectedSources);
        }

        foreach(var s in sourcesToDelete.ToList())
        {
            await DeleteSingleSourceAsync(s);
        }
    }

    private async Task DeleteSingleSourceAsync(SourceItem source)
    {
        if (Sources.Contains(source))
        {
            await _captureService.StopCaptureAsync(source);
            _compositeFrameService.RemoveSource(source);
            Sources.Remove(source);
            await SaveSourcesAsync();
        }
    }
    
    [RelayCommand]
    private void CopySource(object? parameter)
    {
        _copiedSources.Clear();
        var sourcesToCopy = new List<SourceItem>();
        if (parameter is SourceItem source)
        {
            sourcesToCopy.Add(source);
        }
        else if (parameter is IEnumerable<SourceItem> sources)
        {
            sourcesToCopy.AddRange(sources);
        }
        else
        {
            sourcesToCopy.AddRange(SelectedSources);
        }

        if (sourcesToCopy.Any())
        {
            foreach (var s in sourcesToCopy)
            {
                _copiedSources.Add(s.Clone());
            }
        }
    }
    
    [RelayCommand(CanExecute = nameof(CanPasteSource))]
    private async Task PasteSourceAsync()
    {
        foreach (var copiedSource in _copiedSources)
        {
            var newSource = copiedSource.Clone();
            newSource.Id = Guid.NewGuid(); // Give it a new ID
            
            // Find a new position
            var (x, y) = FindAvailableCanvasPosition();
            newSource.CanvasX = x;
            newSource.CanvasY = y;

            await AddSourceAsync(newSource);
        }
    }

    private bool CanPasteSource() => _copiedSources.Any();
    
    [RelayCommand]
    private async Task CenterSourceAsync(SourceItem? source)
    {
        var sourcesToCenter = new List<SourceItem>();
        if (source != null)
        {
            sourcesToCenter.Add(source);
        }
        else
        {
            sourcesToCenter.AddRange(SelectedSources);
        }

        if (sourcesToCenter.Any())
        {
            // Canvas size is 800x600
            var canvasWidth = 800;
            var canvasHeight = 600;

            foreach (var s in sourcesToCenter)
            {
                s.CanvasX = (canvasWidth - s.CanvasWidth) / 2;
                s.CanvasY = (canvasHeight - s.CanvasHeight) / 2;
            }

            await SaveSourcesAsync();
        }
    }
    
    [RelayCommand]
    private async Task ResetCanvasAsync()
    {
        await StopAllCapturesAsync();
        Sources.Clear();
        await SaveSourcesAsync();
    }
    
    [RelayCommand]
    public async Task SaveSourcesAsync()
    {
        try
        {
            await _localSettingsService.SaveSettingAsync(SourcesSettingsKey, Sources.ToArray());
        }
        catch
        {
            // Handle save error if needed
        }
    }

    public void UpdateSelectedSources(IEnumerable<SourceItem> newSelection)
    {
        var newSelectionList = newSelection.ToList();
        var currentSelectionList = SelectedSources.ToList();

        if (newSelectionList.Count == currentSelectionList.Count && newSelectionList.All(currentSelectionList.Contains))
        {
            return; // No change
        }

        SelectedSources.Clear();
        foreach (var item in newSelectionList)
        {
            SelectedSources.Add(item);
        }
        // OnSelectionChanged will be called by the collection changed event
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(IsSourceSelected));
        OnPropertyChanged(nameof(IsSingleSelect));
        OnPropertyChanged(nameof(IsMultiSelect));
        OnPropertyChanged(nameof(SelectedSourceName));
        OnPropertyChanged(nameof(SelectedSourceOpacity));
        OnPropertyChanged(nameof(SelectedSourceWidth));
        OnPropertyChanged(nameof(SelectedSourceHeight));
        OnPropertyChanged(nameof(SelectedSourceX));
        OnPropertyChanged(nameof(SelectedSourceY));

        OnPropertyChanged(nameof(SelectedSourceIsMirroredHorizontally));
        OnPropertyChanged(nameof(SelectedSourceIsMirroredVertically));
        OnPropertyChanged(nameof(SelectedSourceRotation));
        OnPropertyChanged(nameof(SelectedSourceCropLeftPct));
        OnPropertyChanged(nameof(SelectedSourceCropRightPct));
        OnPropertyChanged(nameof(SelectedSourceCropTopPct));
        OnPropertyChanged(nameof(SelectedSourceCropBottomPct));

        AlignLeftCommand.NotifyCanExecuteChanged();
        AlignRightCommand.NotifyCanExecuteChanged();
        AlignCenterCommand.NotifyCanExecuteChanged();
        AlignTopCommand.NotifyCanExecuteChanged();
        AlignBottomCommand.NotifyCanExecuteChanged();
        AlignMiddleCommand.NotifyCanExecuteChanged();
    }

    public bool IsSourceSelected => SelectedSources.Any();
    public bool IsMultiSelect => SelectedSources.Count > 1;
    public bool IsSingleSelect => SelectedSources.Count == 1;
    [ObservableProperty]
    private bool isAspectRatioLocked;
    private bool _isUpdatingDimensions;

    #region Selection Properties

    public string? SelectedSourceName
    {
        get
        {
            if (SelectedSources.Count == 0) return null;
            if (SelectedSources.Count == 1) return SelectedSources[0].Name;

            var firstName = SelectedSources[0].Name;
            return SelectedSources.Skip(1).All(s => s.Name == firstName) ? firstName : "Multiple Values";
        }
        set
        {
            if (value != null && SelectedSources.Count > 0 && value != "Multiple Values")
            {
                foreach (var source in SelectedSources)
                {
                    source.Name = value;
                }
                _ = SaveSourcesAsync();
                OnPropertyChanged();
            }
        }
    }

    public double SelectedSourceOpacity
    {
        get
        {
            if (SelectedSources.Count == 0) return 1.0;
            return SelectedSources.Count == 1 ? SelectedSources[0].Opacity : 1.0; // Slider doesn't have a good "multiple values" state, so we just return a default.
        }
        set
        {
            if (SelectedSources.Count > 0)
            {
                foreach (var source in SelectedSources)
                {
                    source.Opacity = value;
                }
                _ = SaveSourcesAsync();
                OnPropertyChanged();
            }
        }
    }

    public double SelectedSourceWidth
    {
        get => IsSingleSelect ? SelectedSources[0].CanvasWidth : 0;
        set
        {
            if (_isUpdatingDimensions) return;
            if (IsSingleSelect && SelectedSources[0].CanvasWidth != value)
            {
                var source = SelectedSources[0];
                var oldWidth = source.CanvasWidth;
                var oldHeight = source.CanvasHeight;
                
                // Apply bounds checking
                var maxWidth = 800 - source.CanvasX;
                var newWidth = Math.Max(10, Math.Min(value, maxWidth));
                source.CanvasWidth = (int)newWidth;
                OnPropertyChanged(nameof(SelectedSourceWidth));

                if (IsAspectRatioLocked && oldWidth > 0)
                {
                    _isUpdatingDimensions = true;
                    var aspectRatio = (double)oldHeight / oldWidth;
                    var newHeight = newWidth * aspectRatio;
                    var maxHeight = 600 - source.CanvasY;
                    source.CanvasHeight = (int)Math.Max(10, Math.Min(newHeight, maxHeight));
                    OnPropertyChanged(nameof(SelectedSourceHeight));
                    _isUpdatingDimensions = false;
                }
                SaveSourcesAsync();
            }
        }
    }

    public double SelectedSourceHeight
    {
        get => IsSingleSelect ? SelectedSources[0].CanvasHeight : 0;
        set
        {
            if (_isUpdatingDimensions) return;
            if (IsSingleSelect && SelectedSources[0].CanvasHeight != value)
            {
                var source = SelectedSources[0];
                var oldHeight = source.CanvasHeight;
                var oldWidth = source.CanvasWidth;
                
                // Apply bounds checking
                var maxHeight = 600 - source.CanvasY;
                var newHeight = Math.Max(10, Math.Min(value, maxHeight));
                source.CanvasHeight = (int)newHeight;
                OnPropertyChanged(nameof(SelectedSourceHeight));

                if (IsAspectRatioLocked && oldHeight > 0)
                {
                    _isUpdatingDimensions = true;
                    var aspectRatio = (double)oldWidth / oldHeight;
                    var newWidth = newHeight * aspectRatio;
                    var maxWidth = 800 - source.CanvasX;
                    source.CanvasWidth = (int)Math.Max(10, Math.Min(newWidth, maxWidth));
                    OnPropertyChanged(nameof(SelectedSourceWidth));
                    _isUpdatingDimensions = false;
                }
                SaveSourcesAsync();
            }
        }
    }

    public double SelectedSourceX
    {
        get
        {
            if (SelectedSources.Count == 0) return double.NaN;
            if (SelectedSources.Count == 1) return SelectedSources[0].CanvasX;
            var first = SelectedSources[0].CanvasX;
            return SelectedSources.Skip(1).All(s => s.CanvasX == first) ? first : double.NaN;
        }
        set
        {
            if (!double.IsNaN(value) && SelectedSources.Count > 0)
            {
                foreach (var source in SelectedSources)
                {
                    // Apply bounds checking
                    var maxX = 800 - source.CanvasWidth;
                    source.CanvasX = (int)Math.Max(0, Math.Min(value, maxX));
                }
                _ = SaveSourcesAsync();
                OnPropertyChanged();
            }
        }
    }

    public double SelectedSourceY
    {
        get
        {
            if (SelectedSources.Count == 0) return double.NaN;
            if (SelectedSources.Count == 1) return SelectedSources[0].CanvasY;
            var first = SelectedSources[0].CanvasY;
            return SelectedSources.Skip(1).All(s => s.CanvasY == first) ? first : double.NaN;
        }
        set
        {
            if (!double.IsNaN(value) && SelectedSources.Count > 0)
            {
                foreach (var source in SelectedSources)
                {
                    // Apply bounds checking
                    var maxY = 600 - source.CanvasHeight;
                    source.CanvasY = (int)Math.Max(0, Math.Min(value, maxY));
                }
                _ = SaveSourcesAsync();
                OnPropertyChanged();
            }
        }
    }
    
    public double SelectedSourceCropLeftPct
    {
        get
        {
            if (SelectedSources.Count == 0) return double.NaN;
            if (SelectedSources.Count == 1) return SelectedSources[0].CropLeftPct * 100; // Convert to 0-100 range
            var first = SelectedSources[0].CropLeftPct;
            return SelectedSources.Skip(1).All(s => Math.Abs(s.CropLeftPct - first) < 0.001) ? first * 100 : double.NaN;
        }
        set
        {
            if (!double.IsNaN(value) && SelectedSources.Count > 0)
            {
                foreach (var source in SelectedSources)
                {
                    source.CropLeftPct = Math.Clamp(value / 100.0, 0, 1); // Convert from 0-100 to 0-1 range
                }
                _ = SaveSourcesAsync();
                OnPropertyChanged();
            }
        }
    }
    
    public double SelectedSourceCropRightPct
    {
        get
        {
            if (SelectedSources.Count == 0) return double.NaN;
            if (SelectedSources.Count == 1) return SelectedSources[0].CropRightPct * 100;
            var first = SelectedSources[0].CropRightPct;
            return SelectedSources.Skip(1).All(s => Math.Abs(s.CropRightPct - first) < 0.001) ? first * 100 : double.NaN;
        }
        set
        {
            if (!double.IsNaN(value) && SelectedSources.Count > 0)
            {
                foreach (var source in SelectedSources)
                {
                    source.CropRightPct = Math.Clamp(value / 100.0, 0, 1);
                }
                _ = SaveSourcesAsync();
                OnPropertyChanged();
            }
        }
    }
    
    public double SelectedSourceCropTopPct
    {
        get
        {
            if (SelectedSources.Count == 0) return double.NaN;
            if (SelectedSources.Count == 1) return SelectedSources[0].CropTopPct * 100;
            var first = SelectedSources[0].CropTopPct;
            return SelectedSources.Skip(1).All(s => Math.Abs(s.CropTopPct - first) < 0.001) ? first * 100 : double.NaN;
        }
        set
        {
            if (!double.IsNaN(value) && SelectedSources.Count > 0)
            {
                foreach (var source in SelectedSources)
                {
                    source.CropTopPct = Math.Clamp(value / 100.0, 0, 1);
                }
                _ = SaveSourcesAsync();
                OnPropertyChanged();
            }
        }
    }
    
    public double SelectedSourceCropBottomPct
    {
        get
        {
            if (SelectedSources.Count == 0) return double.NaN;
            if (SelectedSources.Count == 1) return SelectedSources[0].CropBottomPct * 100;
            var first = SelectedSources[0].CropBottomPct;
            return SelectedSources.Skip(1).All(s => Math.Abs(s.CropBottomPct - first) < 0.001) ? first * 100 : double.NaN;
        }
        set
        {
            if (!double.IsNaN(value) && SelectedSources.Count > 0)
            {
                foreach (var source in SelectedSources)
                {
                    source.CropBottomPct = Math.Clamp(value / 100.0, 0, 1);
                }
                _ = SaveSourcesAsync();
                OnPropertyChanged();
            }
        }
    }

    public bool SelectedSourceIsMirroredHorizontally
    {
        get
        {
            if (SelectedSources.Count == 0) return false;
            if (SelectedSources.Count == 1) return SelectedSources[0].IsMirroredHorizontally;
            var first = SelectedSources[0].IsMirroredHorizontally;
            return SelectedSources.Skip(1).All(s => s.IsMirroredHorizontally == first) ? first : false;
        }
        set
        {
            if (SelectedSources.Count > 0)
            {
                foreach (var source in SelectedSources)
                {
                    source.IsMirroredHorizontally = value;
                }
                _ = SaveSourcesAsync();
                OnPropertyChanged();
            }
        }
    }

    public bool SelectedSourceIsMirroredVertically
    {
        get => IsSingleSelect ? SelectedSources[0].IsMirroredVertically : false;
        set
        {
            if (IsSingleSelect && SelectedSources[0].IsMirroredVertically != value)
            {
                SelectedSources[0].IsMirroredVertically = value;
                OnPropertyChanged(nameof(SelectedSourceIsMirroredVertically));
                SaveSourcesAsync();
            }
        }
    }

    public int SelectedSourceRotation
    {
        get => IsSingleSelect ? SelectedSources[0].Rotation : 0;
        set
        {
            if (IsSingleSelect && SelectedSources[0].Rotation != value)
            {
                SelectedSources[0].Rotation = value;
                OnPropertyChanged(nameof(SelectedSourceRotation));
                SaveSourcesAsync();
            }
        }
    }

    #endregion

    #region Alignment Commands

    [RelayCommand(CanExecute = nameof(IsMultiSelect))]
    private async Task AlignLeftAsync()
    {
        if (SelectedSources.Count < 2) return;
        var minX = SelectedSources.Min(s => s.CanvasX);
        foreach (var s in SelectedSources) s.CanvasX = minX;
        await SaveSourcesAsync();
    }
    
    [RelayCommand(CanExecute = nameof(IsMultiSelect))]
    private async Task AlignRightAsync()
    {
        if (SelectedSources.Count < 2) return;
        var maxR = SelectedSources.Max(s => s.CanvasX + s.CanvasWidth);
        foreach (var s in SelectedSources) s.CanvasX = maxR - s.CanvasWidth;
        await SaveSourcesAsync();
    }
    
    [RelayCommand(CanExecute = nameof(IsMultiSelect))]
    private async Task AlignTopAsync()
    {
        var minY = SelectedSources.Min(s => s.CanvasY);
        foreach (var s in SelectedSources) s.CanvasY = minY;
        await SaveSourcesAsync();
    }
    
    [RelayCommand(CanExecute = nameof(IsMultiSelect))]
    private async Task AlignBottomAsync()
    {
        var maxY = SelectedSources.Max(s => s.CanvasY + s.CanvasHeight);
        foreach (var s in SelectedSources) s.CanvasY = maxY - s.CanvasHeight;
        await SaveSourcesAsync();
    }

    [RelayCommand(CanExecute = nameof(IsMultiSelect))]
    private async Task AlignCenterAsync()
    {
        var averageCenterX = SelectedSources.Average(s => s.CanvasX + s.CanvasWidth / 2.0);
        foreach (var s in SelectedSources) s.CanvasX = (int)(averageCenterX - s.CanvasWidth / 2.0);
        await SaveSourcesAsync();
    }
    
    [RelayCommand(CanExecute = nameof(IsMultiSelect))]
    private async Task AlignMiddleAsync()
    {
        if (SelectedSources.Count < 2) return;
        var avgCenterY = SelectedSources.Average(s => s.CanvasY + s.CanvasHeight / 2.0);
        foreach (var s in SelectedSources) s.CanvasY = (int)(avgCenterY - s.CanvasHeight / 2.0);
        await SaveSourcesAsync();
    }

    #endregion

    [RelayCommand]
    private async Task ToggleFlipHorizontalAsync()
    {
        if (SelectedSources.Count == 0) return;

        if (SelectedSources.Count == 1)
        {
            SelectedSources[0].IsMirroredHorizontally = !SelectedSources[0].IsMirroredHorizontally;
        }
        else
        {
            var selectionRect = new System.Drawing.Rectangle(
                SelectedSources.Min(s => s.CanvasX),
                SelectedSources.Min(s => s.CanvasY),
                SelectedSources.Max(s => s.CanvasX + s.CanvasWidth) - SelectedSources.Min(s => s.CanvasX),
                SelectedSources.Max(s => s.CanvasY + s.CanvasHeight) - SelectedSources.Min(s => s.CanvasY)
            );
            var selectionCenterX = selectionRect.X + selectionRect.Width / 2.0;

            foreach (var s in SelectedSources)
            {
                var sourceCenter = s.CanvasX + s.CanvasWidth / 2.0;
                var newSourceCenter = selectionCenterX + (selectionCenterX - sourceCenter);
                s.CanvasX = (int)(newSourceCenter - s.CanvasWidth / 2.0);
                s.IsMirroredHorizontally = !s.IsMirroredHorizontally;
            }
        }
        await SaveSourcesAsync();
    }

    [RelayCommand]
    private async Task ToggleFlipVerticalAsync()
    {
        if (SelectedSources.Count == 0) return;

        if (SelectedSources.Count == 1)
        {
            SelectedSources[0].IsMirroredVertically = !SelectedSources[0].IsMirroredVertically;
        }
        else
        {
            var selectionRect = new System.Drawing.Rectangle(
                SelectedSources.Min(s => s.CanvasX),
                SelectedSources.Min(s => s.CanvasY),
                SelectedSources.Max(s => s.CanvasX + s.CanvasWidth) - SelectedSources.Min(s => s.CanvasX),
                SelectedSources.Max(s => s.CanvasY + s.CanvasHeight) - SelectedSources.Min(s => s.CanvasY)
            );
            var selectionCenterY = selectionRect.Y + selectionRect.Height / 2.0;

            foreach (var s in SelectedSources)
            {
                var sourceCenter = s.CanvasY + s.CanvasHeight / 2.0;
                var newSourceCenter = selectionCenterY + (selectionCenterY - sourceCenter);
                s.CanvasY = (int)(newSourceCenter - s.CanvasHeight / 2.0);
                s.IsMirroredVertically = !s.IsMirroredVertically;
            }
        }
        await SaveSourcesAsync();
    }

    private (int x, int y) FindAvailableCanvasPosition()
    {
        const int gridSize = 10;
        const int canvasWidth = 800;
        const int canvasHeight = 600;
        
        for (int row = 0; row < canvasHeight / gridSize; row++)
        {
            for (int col = 0; col < canvasWidth / gridSize; col++)
            {
                int x = col * gridSize + 10; // Small margin
                int y = row * gridSize + 10;
                
                // Check if this position is already occupied
                bool occupied = Sources.Any(s => 
                    Math.Abs(s.CanvasX - x) < 50 && Math.Abs(s.CanvasY - y) < 50);
                
                if (!occupied)
                    return (x, y);
            }
        }
        
        // If all positions occupied, place randomly
        var random = new Random();
        return (random.Next(0, canvasWidth - 100), random.Next(0, canvasHeight - 80));
    }
    
    private async Task LoadSourcesAsync()
    {
        try
        {
            var savedSources = await _localSettingsService.ReadSettingAsync<SourceItem[]>(SourcesSettingsKey);
            if (savedSources != null)
            {
                Sources.Clear();
                foreach (var source in savedSources)
                {
                    // Property change listener will be attached automatically via Sources.CollectionChanged
                    Sources.Add(source);
                    
                    // Start capture for loaded sources
                    _ = Task.Run(async () => await _captureService.StartCaptureAsync(source));
                }
            }
        }
        catch
        {
            // If loading fails, start with empty collection
        }
    }
    
    private async Task LoadSettingsAsync()
    {
        try
        {
            var savedFps = await _localSettingsService.ReadSettingAsync<int?>(PreviewFpsSettingsKey);
            if (savedFps.HasValue && savedFps.Value >= 1 && savedFps.Value <= 60)
            {
                PreviewFps = savedFps.Value;
            }
        }
        catch
        {
            // If loading fails, keep default value
        }
    }
    
    private async Task SavePreviewFpsAsync()
    {
        try
        {
            await _localSettingsService.SaveSettingAsync(PreviewFpsSettingsKey, PreviewFps);
        }
        catch
        {
            // Handle save error if needed
        }
    }
    
    private void OnFrameAvailable(object? sender, SourceFrameEventArgs e)
    {
        // Handle frame received from capture service
        // Send individual source frame to composite service
        if (e.FrameData != null)
        {
            _compositeFrameService.UpdateSourceFrame(e.Source, e.FrameData);
        }
    }
    
    private void OnCompositeFrameAvailable(object? sender, byte[] compositeFrame)
    {
        // Send composite frame to MJPEG streaming service
        _mjpegStreamingService.UpdateFrame(compositeFrame);
    }
    
    private void OnStreamingUrlChanged(object? sender, string url)
    {
        StreamingUrl = string.IsNullOrEmpty(url) ? null : url;
    }
    
    [RelayCommand]
    private async Task StartAllCapturesAsync()
    {
        foreach (var source in Sources)
        {
            if (!_captureService.IsCapturing(source))
            {
                await _captureService.StartCaptureAsync(source);
            }
        }
        
        // Also start a test frame generator to ensure MJPEG stream works
        _testFrameCancellation?.Cancel();
        _testFrameCancellation = new CancellationTokenSource();
        
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_testFrameCancellation.Token.IsCancellationRequested)
                {
                    // Generate a test frame every second
                    await Task.Delay(1000, _testFrameCancellation.Token);
                    
                    // Generate a simple test JPEG frame
                    var testFrame = GenerateTestFrame();
                    _mjpegStreamingService.UpdateFrame(testFrame);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
            }
        }, _testFrameCancellation.Token);
    }
    
    private byte[] GenerateTestFrame()
    {
        // Create a simple colored test frame (minimal JPEG)
        return new byte[]
        {
            0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x01, 0x00, 0x48,
            0x00, 0x48, 0x00, 0x00, 0xFF, 0xDB, 0x00, 0x43, 0x00, 0x08, 0x06, 0x06, 0x07, 0x06, 0x05, 0x08,
            0x07, 0x07, 0x07, 0x09, 0x09, 0x08, 0x0A, 0x0C, 0x14, 0x0D, 0x0C, 0x0B, 0x0B, 0x0C, 0x19, 0x12,
            0x13, 0x0F, 0x14, 0x1D, 0x1A, 0x1F, 0x1E, 0x1D, 0x1A, 0x1C, 0x1C, 0x20, 0x24, 0x2E, 0x27, 0x20,
            0x22, 0x2C, 0x23, 0x1C, 0x1C, 0x28, 0x37, 0x29, 0x2C, 0x30, 0x31, 0x34, 0x34, 0x34, 0x1F, 0x27,
            0x39, 0x3D, 0x38, 0x32, 0x3C, 0x2E, 0x33, 0x34, 0x32, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x64,
            0x00, 0x64, 0x01, 0x01, 0x11, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01, 0xFF, 0xC4, 0x00, 0x15,
            0x00, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x08, 0xFF, 0xC4, 0x00, 0x14, 0x10, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xDA, 0x00, 0x0C, 0x03, 0x01, 0x00, 0x02,
            0x11, 0x03, 0x11, 0x00, 0x3F, 0x00, 0x00, 0xFF, 0xD9
        };
    }
    
    [RelayCommand]
    private async Task StopAllCapturesAsync()
    {
        // Stop test frame generator
        _testFrameCancellation?.Cancel();
        
        await _captureService.StopAllCapturesAsync();
    }

    [RelayCommand]
    private void BringToFront()
    {
        var sourcesToMove = SelectedSources.ToList();
        if (!sourcesToMove.Any()) return;

        foreach (var source in sourcesToMove.OrderBy(s => Sources.IndexOf(s)))
        {
            if (Sources.Remove(source))
                Sources.Add(source);
        }
    }

    [RelayCommand]
    private void SendToBack()
    {
        var sourcesToMove = SelectedSources.ToList();
        if (!sourcesToMove.Any()) return;

        foreach (var source in sourcesToMove.OrderByDescending(s => Sources.IndexOf(s)))
        {
            if (Sources.Remove(source))
                Sources.Insert(0, source);
        }
    }

    private void Sources_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Attach property change listeners to new sources
        if (e.NewItems != null)
        {
            foreach (SourceItem source in e.NewItems)
            {
                source.PropertyChanged += Source_PropertyChanged;
            }
        }
        
        // Detach property change listeners from removed sources
        if (e.OldItems != null)
        {
            foreach (SourceItem source in e.OldItems)
            {
                source.PropertyChanged -= Source_PropertyChanged;
            }
        }
    }

    private void Source_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not SourceItem source || !source.IsSelected) return;

        // Update property panel if this source is currently selected
        // This works for both single and multi-select scenarios
        switch (e.PropertyName)
        {
            case nameof(SourceItem.CanvasX):
                OnPropertyChanged(nameof(SelectedSourceX));
                break;
            case nameof(SourceItem.CanvasY):
                OnPropertyChanged(nameof(SelectedSourceY));
                break;
            case nameof(SourceItem.CanvasWidth):
                OnPropertyChanged(nameof(SelectedSourceWidth));
                break;
            case nameof(SourceItem.CanvasHeight):
                OnPropertyChanged(nameof(SelectedSourceHeight));
                break;
            case nameof(SourceItem.Rotation):
                OnPropertyChanged(nameof(SelectedSourceRotation));
                break;
            case nameof(SourceItem.Opacity):
                OnPropertyChanged(nameof(SelectedSourceOpacity));
                break;
            case nameof(SourceItem.CropLeftPct):
                OnPropertyChanged(nameof(SelectedSourceCropLeftPct));
                break;
            case nameof(SourceItem.CropTopPct):
                OnPropertyChanged(nameof(SelectedSourceCropTopPct));
                break;
            case nameof(SourceItem.CropRightPct):
                OnPropertyChanged(nameof(SelectedSourceCropRightPct));
                break;
            case nameof(SourceItem.CropBottomPct):
                OnPropertyChanged(nameof(SelectedSourceCropBottomPct));
                break;
            case nameof(SourceItem.IsMirroredHorizontally):
                OnPropertyChanged(nameof(SelectedSourceIsMirroredHorizontally));
                break;
            case nameof(SourceItem.IsMirroredVertically):
                OnPropertyChanged(nameof(SelectedSourceIsMirroredVertically));
                break;
            case nameof(SourceItem.Name):
                OnPropertyChanged(nameof(SelectedSourceName));
                break;
        }
        
        // Also trigger UI state updates that depend on selection
        OnPropertyChanged(nameof(IsSourceSelected));
        OnPropertyChanged(nameof(IsMultiSelect));
        OnPropertyChanged(nameof(IsSingleSelect));
    }
}
