using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Linq;
using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Windows.Foundation;

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
    private readonly UndoRedoManager _undoRedoManager = new();
    private bool _isUndoRedoOperation = false; // To prevent saving undo state during undo/redo
    public event EventHandler? SourcesMoved;
    
    [ObservableProperty]
    private bool _isPasting;

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
        
        // Listen to undo/redo state changes
        _undoRedoManager.CanUndoRedoChanged += (s, e) => 
        {
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
        };
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
            SaveUndoState();
            
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
            SaveUndoState();
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
        if (!_copiedSources.Any()) return;

        IsPasting = true;
        try
        {
            SaveUndoState();
            var newSelection = new List<SourceItem>();

            foreach (var copiedSource in _copiedSources)
            {
                var newSource = copiedSource.Clone();
                newSource.Id = Guid.NewGuid();

                // Paste in the same location, do not find a new position
                newSource.CanvasX = copiedSource.CanvasX;
                newSource.CanvasY = copiedSource.CanvasY;

                Sources.Add(newSource);
                newSelection.Add(newSource);

                await _captureService.StartCaptureAsync(newSource);
            }

            UpdateSelectedSources(newSelection);
            await SaveSourcesAsync();
        }
        finally
        {
            IsPasting = false;
        }
    }

    public bool CanPasteSource() => _copiedSources.Any();
    
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

        if (!sourcesToCenter.Any()) return;

        // Canvas size is 800x600
        const double canvasWidth = 800;
        const double canvasHeight = 600;
        var canvasCenter = new Point(canvasWidth / 2.0, canvasHeight / 2.0);

        if (sourcesToCenter.Count == 1)
        {
            var s = sourcesToCenter[0];
            s.CanvasX = (int)Math.Round((canvasWidth - s.CanvasWidth) / 2.0);
            s.CanvasY = (int)Math.Round((canvasHeight - s.CanvasHeight) / 2.0);
        }
        else
        {
            // For multiple items, calculate the bounding box of the entire selection
            Rect selectionBounds = Rect.Empty;
            foreach (var s in sourcesToCenter)
            {
                var itemCenter = new Point(s.CanvasX + s.CanvasWidth / 2.0, s.CanvasY + s.CanvasHeight / 2.0);
                var itemSize = new Size(s.CanvasWidth, s.CanvasHeight);
                var itemAabb = GetRotatedAabb(itemCenter, itemSize, s.Rotation);

                if (selectionBounds.IsEmpty)
                {
                    selectionBounds = itemAabb;
                }
                else
                {
                    selectionBounds.Union(itemAabb);
                }
            }

            // Calculate the center of the selection's bounding box
            var selectionCenter = new Point(selectionBounds.X + selectionBounds.Width / 2.0, selectionBounds.Y + selectionBounds.Height / 2.0);

            // Calculate the offset needed to move the selection to the canvas center
            var offsetX = canvasCenter.X - selectionCenter.X;
            var offsetY = canvasCenter.Y - selectionCenter.Y;

            // Apply the offset to each source in the selection
            foreach (var s in sourcesToCenter)
            {
                s.CanvasX += (int)Math.Round(offsetX);
                s.CanvasY += (int)Math.Round(offsetY);
            }
        }

        await SaveSourcesWithUndoAsync();
        SourcesMoved?.Invoke(this, EventArgs.Empty);
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
    
    public async Task SaveSourcesWithUndoAsync()
    {
        SaveUndoState();
        await SaveSourcesAsync();
    }

    public void UpdateSelectedSources(IEnumerable<SourceItem> newSelection)
    {
        var newSelectionList = newSelection.ToList();

        // Use IsSelected as the source of truth.
        // First, deselect anything that shouldn't be selected anymore.
        var toDeselect = Sources.Where(s => s.IsSelected && !newSelectionList.Contains(s)).ToList();
        foreach (var item in toDeselect)
        {
            item.IsSelected = false;
        }

        // Then, select the new items.
        foreach (var item in newSelectionList)
        {
            if (!item.IsSelected)
            {
                item.IsSelected = true;
            }
        }

        // Now, update the SelectedSources collection to reflect the state of IsSelected.
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
        OnPropertyChanged(nameof(SelectedSourceCropRotation));

        AlignLeftCommand.NotifyCanExecuteChanged();
        AlignRightCommand.NotifyCanExecuteChanged();
        AlignCenterCommand.NotifyCanExecuteChanged();
        AlignTopCommand.NotifyCanExecuteChanged();
        AlignBottomCommand.NotifyCanExecuteChanged();
        AlignMiddleCommand.NotifyCanExecuteChanged();
        BringToFrontCommand.NotifyCanExecuteChanged();
        SendToBackCommand.NotifyCanExecuteChanged();
        BringForwardCommand.NotifyCanExecuteChanged();
        SendBackwardCommand.NotifyCanExecuteChanged();
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
                SaveUndoState();
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
                SaveUndoState();
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
        get => SelectedSources.FirstOrDefault()?.CanvasWidth ?? 0;
        set
        {
            if (SelectedSources.FirstOrDefault()?.CanvasWidth == value) return;
            if (_isUpdatingDimensions) return;

            foreach (var s in SelectedSources)
            {
                var testSource = s.Clone();
                testSource.CanvasWidth = (int)value;
                if (FitsInCanvas(testSource, 800, 600))
                {
                    s.CanvasWidth = (int)value;
                }
            }
            _ = SaveSourcesWithUndoAsync();
        }
    }

    public double SelectedSourceHeight
    {
        get => SelectedSources.FirstOrDefault()?.CanvasHeight ?? 0;
        set
        {
            if (SelectedSources.FirstOrDefault()?.CanvasHeight == value) return;
            if (_isUpdatingDimensions) return;

            foreach (var s in SelectedSources)
            {
                var testSource = s.Clone();
                testSource.CanvasHeight = (int)value;
                if (FitsInCanvas(testSource, 800, 600))
                {
                    s.CanvasHeight = (int)value;
                }
            }
            _ = SaveSourcesWithUndoAsync();
        }
    }

    public double SelectedSourceX
    {
        get => SelectedSources.FirstOrDefault()?.CanvasX ?? 0;
        set
        {
            if (SelectedSources.FirstOrDefault()?.CanvasX == value) return;
            if (_isUpdatingDimensions) return;
            
            foreach (var s in SelectedSources)
            {
                var testSource = s.Clone();
                testSource.CanvasX = (int)value;
                if (FitsInCanvas(testSource, 800, 600))
                {
                    s.CanvasX = (int)value;
                }
            }
            _ = SaveSourcesWithUndoAsync();
        }
    }

    public double SelectedSourceY
    {
        get => SelectedSources.FirstOrDefault()?.CanvasY ?? 0;
        set
        {
            if (SelectedSources.FirstOrDefault()?.CanvasY == value) return;
            if (_isUpdatingDimensions) return;
            
            foreach (var s in SelectedSources)
            {
                var testSource = s.Clone();
                testSource.CanvasY = (int)value;
                if (FitsInCanvas(testSource, 800, 600))
                {
                    s.CanvasY = (int)value;
                }
            }
            _ = SaveSourcesWithUndoAsync();
        }
    }
    
    public double SelectedSourceCropLeftPct
    {
        get
        {
            if (SelectedSources.Count == 0) return 0;
            if (SelectedSources.Count == 1) return SelectedSources[0].CropLeftPct * 100; // Convert to 0-100 range
            var first = SelectedSources[0].CropLeftPct;
            return SelectedSources.Skip(1).All(s => Math.Abs(s.CropLeftPct - first) < 0.001) ? first * 100 : 0;
        }
        set
        {
            if (SelectedSources.Count > 0)
            {
                SaveUndoState();
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
            if (SelectedSources.Count == 0) return 0;
            if (SelectedSources.Count == 1) return SelectedSources[0].CropRightPct * 100;
            var first = SelectedSources[0].CropRightPct;
            return SelectedSources.Skip(1).All(s => Math.Abs(s.CropRightPct - first) < 0.001) ? first * 100 : 0;
        }
        set
        {
            if (SelectedSources.Count > 0)
            {
                SaveUndoState();
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
            if (SelectedSources.Count == 0) return 0;
            if (SelectedSources.Count == 1) return SelectedSources[0].CropTopPct * 100;
            var first = SelectedSources[0].CropTopPct;
            return SelectedSources.Skip(1).All(s => Math.Abs(s.CropTopPct - first) < 0.001) ? first * 100 : 0;
        }
        set
        {
            if (SelectedSources.Count > 0)
            {
                SaveUndoState();
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
            if (SelectedSources.Count == 0) return 0;
            if (SelectedSources.Count == 1) return SelectedSources[0].CropBottomPct * 100;
            var first = SelectedSources[0].CropBottomPct;
            return SelectedSources.Skip(1).All(s => Math.Abs(s.CropBottomPct - first) < 0.001) ? first * 100 : 0;
        }
        set
        {
            if (SelectedSources.Count > 0)
            {
                SaveUndoState();
                foreach (var source in SelectedSources)
                {
                    source.CropBottomPct = Math.Clamp(value / 100.0, 0, 1);
                }
                _ = SaveSourcesAsync();
                OnPropertyChanged();
            }
        }
    }
    
    public double SelectedSourceCropRotation
    {
        get
        {
            if (SelectedSources.Count == 0) return 0;
            if (SelectedSources.Count == 1) return SelectedSources[0].CropRotation;
            var first = SelectedSources[0].CropRotation;
            return SelectedSources.Skip(1).All(s => s.CropRotation == first) ? first : 0;
        }
        set
        {
            if (SelectedSources.Count > 0)
            {
                SaveUndoState();
                foreach (var source in SelectedSources)
                {
                    source.CropRotation = (int)Math.Round(Math.Clamp(value, -180, 180));
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
                SaveUndoState();
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
        get => SelectedSources.FirstOrDefault()?.IsMirroredVertically ?? false;
        set
        {
            if (SelectedSources.FirstOrDefault()?.IsMirroredVertically != value)
            {
                foreach (var source in SelectedSources)
                {
                    source.IsMirroredVertically = value;
                }
                _ = SaveSourcesWithUndoAsync();
            }
        }
    }

    /// <summary>
    /// Calculates the Axis-Aligned Bounding Box (AABB) of a source's visible (cropped) area.
    /// This is the definitive check for whether a source is "inside" the canvas.
    /// </summary>
    /// <param name="src">The source item to check.</param>
    /// <returns>The AABB of the visible, rotated, cropped area in canvas coordinates.</returns>
    public static Rect GetVisibleAreaAabb(SourceItem src)
    {
        if (src == null) return Rect.Empty;

        // 1. Get the size of the full control
        var w = src.CanvasWidth;
        var h = src.CanvasHeight;

        // 2. Calculate the size of the visible (non-cropped) part
        var effW = w * (1 - src.CropLeftPct - src.CropRightPct);
        var effH = h * (1 - src.CropTopPct - src.CropBottomPct);
        if (effW <= 0 || effH <= 0) return Rect.Empty; // Fully cropped, no visible area

        // 3. Calculate the offset of the visible part inside the control
        var offX = w * src.CropLeftPct;
        var offY = h * src.CropTopPct;

        // 4. Determine the center of the visible part in canvas coordinates
        var centre = new Point(
            src.CanvasX + offX + effW / 2,
            src.CanvasY + offY + effH / 2);

        // 5. Get the AABB of the rotated visible part
        return GetRotatedAabb(centre, new Size(effW, effH), src.Rotation + src.CropRotation);
    }

    private static Rect GetRotatedAabb(Point center, Size size, double angleDeg)
    {
        if (angleDeg == 0) return new Rect(center.X - size.Width / 2, center.Y - size.Height / 2, size.Width, size.Height);

        var angleRad = angleDeg * Math.PI / 180.0;
        var cos = Math.Cos(angleRad);
        var sin = Math.Sin(angleRad);
        var w = size.Width;
        var h = size.Height;

        var p1 = new Point(-w / 2, -h / 2);
        var p2 = new Point(w / 2, -h / 2);
        var p3 = new Point(w / 2, h / 2);
        var p4 = new Point(-w / 2, h / 2);

        var rp1 = new Point(p1.X * cos - p1.Y * sin, p1.X * sin + p1.Y * cos);
        var rp2 = new Point(p2.X * cos - p2.Y * sin, p2.X * sin + p2.Y * cos);
        var rp3 = new Point(p3.X * cos - p3.Y * sin, p3.X * sin + p3.Y * cos);
        var rp4 = new Point(p4.X * cos - p4.Y * sin, p4.X * sin + p4.Y * cos);

        var minX = Math.Min(Math.Min(rp1.X, rp2.X), Math.Min(rp3.X, rp4.X));
        var minY = Math.Min(Math.Min(rp1.Y, rp2.Y), Math.Min(rp3.Y, rp4.Y));
        var maxX = Math.Max(Math.Max(rp1.X, rp2.X), Math.Max(rp3.X, rp4.X));
        var maxY = Math.Max(Math.Max(rp1.Y, rp2.Y), Math.Max(rp3.Y, rp4.Y));

        return new Rect(minX + center.X, minY + center.Y, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// Checks if a source item's visible (cropped) area is within the canvas boundaries.
    /// </summary>
    private bool FitsInCanvas(SourceItem src, double canvasWidth, double canvasHeight)
    {
        var aabb = GetVisibleAreaAabb(src);
        if (aabb.IsEmpty) return true; // Fully cropped is "valid"

        return aabb.Left >= 0 &&
               aabb.Top >= 0 &&
               aabb.Right <= canvasWidth &&
               aabb.Bottom <= canvasHeight;
    }

    private bool FitsInCanvas(int x, int y, int w, int h, int rotationDeg, SourceItem src)
    {
        // Create a temporary clone to test the new values
        var testSource = src.Clone();
        testSource.CanvasX = x;
        testSource.CanvasY = y;
        testSource.CanvasWidth = w;
        testSource.CanvasHeight = h;
        testSource.Rotation = rotationDeg;
        
        return FitsInCanvas(testSource, 800, 600); // Assuming 800x600 canvas
    }

    public int SelectedSourceRotation
    {
        get => SelectedSources.FirstOrDefault()?.Rotation ?? 0;
        set
        {
            if (SelectedSources.FirstOrDefault()?.Rotation == value) return;
            if (_isUpdatingDimensions) return;

            foreach (var s in SelectedSources)
            {
                var testSource = s.Clone();
                testSource.Rotation = value;
                if (FitsInCanvas(testSource, 800, 600))
                {
                    s.Rotation = value;
                }
            }
            _ = SaveSourcesWithUndoAsync();
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
        const int gridSize = 20; // A slightly larger step
        const int canvasWidth = 800;
        const int canvasHeight = 600;
        const int assumedItemWidth = 100; // Assume a default width to prevent overflow
        
        for (int row = 0; row < canvasHeight / gridSize; row++)
        {
            for (int col = 0; col < (canvasWidth - assumedItemWidth) / gridSize; col++)
            {
                int x = col * gridSize + 10; // Small margin
                int y = row * gridSize + 10;
                
                // Check if this position is already occupied, assuming a minimum size of 80x80 for spacing
                bool occupied = Sources.Any(s => 
                    Math.Abs(s.CanvasX - x) < 120 && Math.Abs(s.CanvasY - y) < 120);
                
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

    [RelayCommand(CanExecute = nameof(IsSourceSelected))]
    private void BringToFront()
    {
        var sourcesToMove = SelectedSources.ToList();
        if (!sourcesToMove.Any()) return;

        // Move to front means move to index 0 (top of list = foreground)
        foreach (var source in sourcesToMove.OrderByDescending(s => Sources.IndexOf(s)))
        {
            if (Sources.Remove(source))
                Sources.Insert(0, source);
        }
    }

    [RelayCommand(CanExecute = nameof(IsSourceSelected))]
    private void SendToBack()
    {
        var sourcesToMove = SelectedSources.ToList();
        if (!sourcesToMove.Any()) return;

        // Move to back means move to end of list (bottom of list = background)
        foreach (var source in sourcesToMove.OrderBy(s => Sources.IndexOf(s)))
        {
            if (Sources.Remove(source))
                Sources.Add(source);
        }
    }

    [RelayCommand(CanExecute = nameof(IsSourceSelected))]
    private void BringForward()
    {
        var sourcesToMove = SelectedSources.ToList();
        if (!sourcesToMove.Any()) return;

        // Move forward means move one step closer to index 0 (towards foreground)
        foreach (var source in sourcesToMove.OrderBy(s => Sources.IndexOf(s)))
        {
            var currentIndex = Sources.IndexOf(source);
            if (currentIndex > 0)
            {
                Sources.Remove(source);
                Sources.Insert(currentIndex - 1, source);
            }
        }
    }

    [RelayCommand(CanExecute = nameof(IsSourceSelected))]
    private void SendBackward()
    {
        var sourcesToMove = SelectedSources.ToList();
        if (!sourcesToMove.Any()) return;

        // Move backward means move one step away from index 0 (towards background)
        foreach (var source in sourcesToMove.OrderByDescending(s => Sources.IndexOf(s)))
        {
            var currentIndex = Sources.IndexOf(source);
            if (currentIndex < Sources.Count - 1)
            {
                Sources.Remove(source);
                Sources.Insert(currentIndex + 1, source);
            }
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
            case nameof(SourceItem.CropRotation):
                OnPropertyChanged(nameof(SelectedSourceCropRotation));
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

    // Undo/Redo properties
    public bool CanUndo => _undoRedoManager.CanUndo;
    public bool CanRedo => _undoRedoManager.CanRedo;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        var previousState = _undoRedoManager.Undo(Sources);
        if (previousState != null)
        {
            await RestoreState(previousState);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private async Task RedoAsync()
    {
        var nextState = _undoRedoManager.Redo(Sources);
        if (nextState != null)
        {
            await RestoreState(nextState);
        }
    }

    private async Task RestoreState(SourceItem[] state)
    {
        _isUndoRedoOperation = true;
        try
        {
            // Stop all current captures
            await StopAllCapturesAsync();
            
            // Clear current sources
            Sources.Clear();
            SelectedSources.Clear();
            
            // Restore sources from state
            foreach (var sourceState in state)
            {
                var newSource = sourceState.Clone();
                newSource.Id = sourceState.Id; // Preserve original ID for undo/redo
                Sources.Add(newSource);
                
                // Start capture for restored source
                await _captureService.StartCaptureAsync(newSource);
            }
            
            // Save the restored state
            await SaveSourcesAsync();
        }
        finally
        {
            _isUndoRedoOperation = false;
        }
    }

    public void SaveUndoState()
    {
        if (!_isUndoRedoOperation)
        {
            _undoRedoManager.SaveState(Sources);
        }
    }
}
