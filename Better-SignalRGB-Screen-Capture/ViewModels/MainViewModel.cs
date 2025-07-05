using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Linq;
using System;
using System.Collections.Concurrent;
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
    private const string IsPreviewingSettingsKey = "IsPreviewing";

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

    [ObservableProperty]
    private bool isRecording;

    [ObservableProperty]
    private bool isRecordingLoading;

    [ObservableProperty]
    private bool needsRefresh;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TogglePauseCommand))]
    private bool isPaused;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TogglePauseCommand))]
    private bool canPause;

    [ObservableProperty]
    private bool _isAspectRatioLocked;

    private bool _isUpdatingDimensions;

    // Group selection properties
    [ObservableProperty]
    private bool _isGroupAspectRatioLocked;

    private bool _isUpdatingGroupDimensions;

    public double GroupSelectionX
    {
        get => _groupSelectionX;
        set
        {
            if (_isUpdatingGroupDimensions) return;
            if (IsMultiSelect && Math.Abs(_groupSelectionX - value) > 0.01)
            {
                _isUpdatingGroupDimensions = true;
                try
                {
                    _groupSelectionX = value;
                    OnPropertyChanged();
                    // Notify MainPage to update GroupSelectionControl position
                    GroupSelectionBoundsChanged?.Invoke();
                }
                finally
                {
                    _isUpdatingGroupDimensions = false;
                }
            }
        }
    }
    private double _groupSelectionX;

    public double GroupSelectionY
    {
        get => _groupSelectionY;
        set
        {
            if (_isUpdatingGroupDimensions) return;
            if (IsMultiSelect && Math.Abs(_groupSelectionY - value) > 0.01)
            {
                _isUpdatingGroupDimensions = true;
                try
                {
                    _groupSelectionY = value;
                    OnPropertyChanged();
                    // Notify MainPage to update GroupSelectionControl position
                    GroupSelectionBoundsChanged?.Invoke();
                }
                finally
                {
                    _isUpdatingGroupDimensions = false;
                }
            }
        }
    }
    private double _groupSelectionY;

    public double GroupSelectionWidth
    {
        get => _groupSelectionWidth;
        set
        {
            if (_isUpdatingGroupDimensions) return;
            if (IsMultiSelect && Math.Abs(_groupSelectionWidth - value) > 0.01)
            {
                var newValue = Math.Max(50, value); // Minimum group width
                if (Math.Abs(_groupSelectionWidth - newValue) > 0.01)
                {
                    _isUpdatingGroupDimensions = true;
                    try
                    {
                        if (IsGroupAspectRatioLocked && _groupSelectionHeight > 0)
                        {
                            var aspectRatio = _groupSelectionWidth / _groupSelectionHeight;
                            _groupSelectionHeight = newValue / aspectRatio;
                            OnPropertyChanged(nameof(GroupSelectionHeight));
                        }
                        _groupSelectionWidth = newValue;
                        OnPropertyChanged();
                        // Notify MainPage to update GroupSelectionControl size
                        GroupSelectionBoundsChanged?.Invoke();
                    }
                    finally
                    {
                        _isUpdatingGroupDimensions = false;
                    }
                }
            }
        }
    }
    private double _groupSelectionWidth;

    public double GroupSelectionHeight
    {
        get => _groupSelectionHeight;
        set
        {
            if (_isUpdatingGroupDimensions) return;
            if (IsMultiSelect && Math.Abs(_groupSelectionHeight - value) > 0.01)
            {
                var newValue = Math.Max(50, value); // Minimum group height
                if (Math.Abs(_groupSelectionHeight - newValue) > 0.01)
                {
                    _isUpdatingGroupDimensions = true;
                    try
                    {
                        if (IsGroupAspectRatioLocked && _groupSelectionWidth > 0)
                        {
                            var aspectRatio = _groupSelectionWidth / _groupSelectionHeight;
                            _groupSelectionWidth = newValue * aspectRatio;
                            OnPropertyChanged(nameof(GroupSelectionWidth));
                        }
                        _groupSelectionHeight = newValue;
                        OnPropertyChanged();
                        // Notify MainPage to update GroupSelectionControl size
                        GroupSelectionBoundsChanged?.Invoke();
                    }
                    finally
                    {
                        _isUpdatingGroupDimensions = false;
                    }
                }
            }
        }
    }
    private double _groupSelectionHeight;

    public double GroupSelectionRotation
    {
        get => _groupSelectionRotation;
        set
        {
            if (_isUpdatingGroupDimensions) return;
            if (IsMultiSelect && Math.Abs(_groupSelectionRotation - value) > 0.01)
            {
                _isUpdatingGroupDimensions = true;
                try
                {
                    _groupSelectionRotation = value;
                    OnPropertyChanged();
                    // Notify MainPage to update GroupSelectionControl rotation
                    GroupSelectionBoundsChanged?.Invoke();
                }
                finally
                {
                    _isUpdatingGroupDimensions = false;
                }
            }
        }
    }
    private double _groupSelectionRotation;

    // Event to notify when group selection bounds change from ViewModel
    public event Action? GroupSelectionBoundsChanged;

    // Method to update group selection properties from GroupSelectionControl
    public void UpdateGroupSelectionBounds(double x, double y, double width, double height, double rotation)
    {
        if (_isUpdatingGroupDimensions) return;
        
        _isUpdatingGroupDimensions = true;
        try
        {
            _groupSelectionX = x;
            _groupSelectionY = y;
            _groupSelectionWidth = width;
            _groupSelectionHeight = height;
            _groupSelectionRotation = rotation;
            
            OnPropertyChanged(nameof(GroupSelectionX));
            OnPropertyChanged(nameof(GroupSelectionY));
            OnPropertyChanged(nameof(GroupSelectionWidth));
            OnPropertyChanged(nameof(GroupSelectionHeight));
            OnPropertyChanged(nameof(GroupSelectionRotation));
        }
        finally
        {
            _isUpdatingGroupDimensions = false;
        }
    }

    public MainViewModel(ILocalSettingsService localSettingsService, ICaptureService captureService, IMjpegStreamingService mjpegStreamingService, ICompositeFrameService compositeFrameService)
    {
        _localSettingsService = localSettingsService;
        _captureService = captureService;
        _mjpegStreamingService = mjpegStreamingService;
        _compositeFrameService = compositeFrameService;
        
        // Subscribe to events
        _captureService.FrameAvailable += OnFrameAvailable;
        _mjpegStreamingService.StreamingUrlChanged += OnStreamingUrlChanged;
        
        // Listen to Sources collection changes to attach/detach property change listeners
        Sources.CollectionChanged += Sources_CollectionChanged;
        
        _ = LoadSourcesAsync();
        _ = LoadSettingsAsync();
        
        // Initialize composite frame service with canvas size
        _compositeFrameService.SetCanvasSize(800, 600);
        
        // Don't auto-start streaming on boot - wait for user to start recording

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
        _ = _captureService.SetFrameRate(value);
    }
    
    partial void OnIsPreviewingChanged(bool value)
    {
        // Save the preview setting when it changes
        _ = SaveIsPreviewingAsync();
        
        // Update IsLivePreviewEnabled for all sources
        foreach (var source in Sources)
        {
            source.IsLivePreviewEnabled = value;
        }
    }
    
    [RelayCommand]
    private async Task AddSourceAsync(SourceItem? newSource = null)
    {
        if (newSource != null)
        {
            SaveUndoState();
            
            // Find a good position for the new source on canvas based on its size
            var (x, y) = FindAvailableCanvasPosition(newSource.CanvasWidth, newSource.CanvasHeight);
            newSource.CanvasX = x;
            newSource.CanvasY = y;
            
            // Set preview state to match global preview toggle
            newSource.IsLivePreviewEnabled = IsPreviewing;
            
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
                
                // Set preview state to match global preview toggle
                newSource.IsLivePreviewEnabled = IsPreviewing;

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
            // For multi-selection, consider it "on" if ALL are mirrored, otherwise "off"
            return SelectedSources.All(s => s.IsMirroredHorizontally);
        }
        set
        {
            if (SelectedSources.Count > 0 && SelectedSourceIsMirroredHorizontally != value)
            {
                // This setter is now the trigger for the flip action
                _ = ToggleFlipHorizontalCommand.ExecuteAsync(null);
                OnPropertyChanged();
            }
        }
    }

    public bool SelectedSourceIsMirroredVertically
    {
        get
        {
            if (SelectedSources.Count == 0) return false;
            // For multi-selection, consider it "on" if ALL are mirrored, otherwise "off"
            return SelectedSources.All(s => s.IsMirroredVertically);
        }
        set
        {
            if (SelectedSources.Count > 0 && SelectedSourceIsMirroredVertically != value)
            {
                // This setter is now the trigger for the flip action
                _ = ToggleFlipVerticalCommand.ExecuteAsync(null);
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Calculates the Axis-Aligned Bounding Box (AABB) of a source's visible (cropped) area.
    /// This is the definitive check for whether a source is "inside" the canvas.
    /// The visible area is the intersection of the crop rectangle and the source's bounds.
    /// </summary>
    /// <param name="src">The source item to check.</param>
    /// <returns>The AABB of the visible, rotated, cropped area in canvas coordinates.</returns>
    public static Rect GetVisibleAreaAabb(SourceItem src)
    {
        if (src == null) return Rect.Empty;

        double w = src.CanvasWidth;
        double h = src.CanvasHeight;

        // Calculate the effective size and offset of the cropped area
        double effW = w * (1 - src.CropLeftPct - src.CropRightPct);
        double effH = h * (1 - src.CropTopPct - src.CropBottomPct);
        if (effW <= 0 || effH <= 0) return Rect.Empty; // fully cropped

        double offX = w * src.CropLeftPct;
        double offY = h * src.CropTopPct;

        // Build crop rectangle corners in local item space
        var cropPolygon = new List<Point>
        {
            new(offX,          offY),
            new(offX + effW,   offY),
            new(offX + effW,   offY + effH),
            new(offX,          offY + effH)
        };

        // Rotate crop rectangle around its own center if it has rotation
        if (src.CropRotation % 360 != 0)
        {
            double cropCenterX = offX + effW / 2.0;
            double cropCenterY = offY + effH / 2.0;
            double rad = src.CropRotation * Math.PI / 180.0;
            double cos = Math.Cos(rad);
            double sin = Math.Sin(rad);
            for (int i = 0; i < cropPolygon.Count; i++)
            {
                double dx = cropPolygon[i].X - cropCenterX;
                double dy = cropPolygon[i].Y - cropCenterY;
                double rx = dx * cos - dy * sin;
                double ry = dx * sin + dy * cos;
                cropPolygon[i] = new Point(rx + cropCenterX, ry + cropCenterY);
            }
        }

        // The crop rectangle can overflow the source item's bounds.
        // We need to find the intersection of the crop polygon and the source's bounding rect.
        var sourceBounds = new Rect(0, 0, w, h);
        var clippedPolygon = ClipPolygonWithRect(cropPolygon, sourceBounds);
        if (clippedPolygon.Count == 0) return Rect.Empty;

        // Now, transform the clipped polygon to canvas coordinates.
        // This involves rotating by the item's rotation and translating.
        double itemRad = src.Rotation * Math.PI / 180.0;
        double itemCos = Math.Cos(itemRad);
        double itemSin = Math.Sin(itemRad);
        double itemCenterX = w / 2.0;
        double itemCenterY = h / 2.0;
        for (int i = 0; i < clippedPolygon.Count; i++)
        {
            double dx = clippedPolygon[i].X - itemCenterX;
            double dy = clippedPolygon[i].Y - itemCenterY;
            double rx = dx * itemCos - dy * itemSin + itemCenterX + src.CanvasX;
            double ry = dx * itemSin + dy * itemCos + itemCenterY + src.CanvasY;
            clippedPolygon[i] = new Point(rx, ry);
        }

        // Finally, calculate the AABB of the transformed, clipped polygon.
        double minX = clippedPolygon.Min(p => p.X);
        double minY = clippedPolygon.Min(p => p.Y);
        double maxX = clippedPolygon.Max(p => p.X);
        double maxY = clippedPolygon.Max(p => p.Y);

        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// Clips a polygon using the Sutherland-Hodgman algorithm against a rectangular clip window.
    /// </summary>
    private static List<Point> ClipPolygonWithRect(List<Point> polygon, Rect clipRect)
    {
        // Helper to find the intersection of a line segment with a clip edge.
        static Point Intersect(Point p1, Point p2, double edge, bool isVertical)
        {
            // Avoid division by zero
            if (isVertical)
            {
                if (p2.X - p1.X == 0) return new Point(edge, p1.Y); // Vertical line on the edge
                double t = (edge - p1.X) / (p2.X - p1.X);
                return new Point(edge, p1.Y + t * (p2.Y - p1.Y));
            }
            else // Horizontal edge
            {
                if (p2.Y - p1.Y == 0) return new Point(p1.X, edge); // Horizontal line on the edge
                double t = (edge - p1.Y) / (p2.Y - p1.Y);
                return new Point(p1.X + t * (p2.X - p1.X), edge);
            }
        }

        // Helper to perform clipping against a single edge of the rectangle.
        List<Point> ClipEdge(List<Point> inputPolygon, Func<Point, bool> isInside, Func<Point, Point, Point> intersectionCalc)
        {
            var outputList = new List<Point>();
            if (inputPolygon.Count == 0) return outputList;

            var s = inputPolygon[^1]; // Start with the last vertex
            foreach (var p in inputPolygon)
            {
                bool s_inside = isInside(s);
                bool p_inside = isInside(p);

                if (p_inside)
                {
                    if (!s_inside)
                    {
                        // S is outside, P is inside: intersection, then P
                        outputList.Add(intersectionCalc(s, p));
                    }
                    outputList.Add(p);
                }
                else if (s_inside)
                {
                    // S is inside, P is outside: intersection
                    outputList.Add(intersectionCalc(s, p));
                }
                // If both are outside, do nothing.
                s = p; // Move to the next edge
            }
            return outputList;
        }

        var clipped = polygon;
        clipped = ClipEdge(clipped, p => p.X >= clipRect.Left, (p1, p2) => Intersect(p1, p2, clipRect.Left, true));
        clipped = ClipEdge(clipped, p => p.X <= clipRect.Right, (p1, p2) => Intersect(p1, p2, clipRect.Right, true));
        clipped = ClipEdge(clipped, p => p.Y >= clipRect.Top, (p1, p2) => Intersect(p1, p2, clipRect.Top, false));
        clipped = ClipEdge(clipped, p => p.Y <= clipRect.Bottom, (p1, p2) => Intersect(p1, p2, clipRect.Bottom, false));

        return clipped;
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
        await FlipSelectedSourcesAsync(horizontal: true, vertical: false);
    }

    [RelayCommand]
    private async Task ToggleFlipVerticalAsync()
    {
        await FlipSelectedSourcesAsync(horizontal: false, vertical: true);
    }

    private async Task FlipSelectedSourcesAsync(bool horizontal, bool vertical)
    {
        if (SelectedSources.Count == 0) return;

        SaveUndoState();

        var sourcesToFlip = SelectedSources.ToList();

        bool horizTarget = horizontal ? !sourcesToFlip[0].IsMirroredHorizontally : default;
        bool vertTarget  = vertical ? !sourcesToFlip[0].IsMirroredVertically : default;

        Rect groupBounds = Rect.Empty;
        foreach (var s in sourcesToFlip)
        {
            var c = new Point(s.CanvasX + s.CanvasWidth * 0.5,
                              s.CanvasY + s.CanvasHeight * 0.5);
            var sz = new Size(s.CanvasWidth, s.CanvasHeight);
            var box = GetRotatedAabb(c, sz, s.Rotation);

            if (groupBounds.IsEmpty)
                groupBounds = box;
            else
                groupBounds.Union(box);
        }

        var groupCenter = new Point(groupBounds.X + groupBounds.Width * 0.5,
                                    groupBounds.Y + groupBounds.Height * 0.5);

        foreach (var src in sourcesToFlip)
        {
            if (horizontal)
            {
                src.CanvasX = (int)Math.Round(2 * groupCenter.X - src.CanvasX - src.CanvasWidth);
                src.IsMirroredHorizontally = horizTarget;
                src.Rotation = 360 - src.Rotation;
                if (src.Rotation >= 360) src.Rotation -= 360;
            }

            if (vertical)
            {
                src.CanvasY = (int)Math.Round(2 * groupCenter.Y - src.CanvasY - src.CanvasHeight);
                src.IsMirroredVertically = vertTarget;
                src.Rotation = -src.Rotation;
                if (src.Rotation < 0) src.Rotation += 360;
            }

            // After flipping, ensure the source's visible area is within the canvas
            var visibleAabb = GetVisibleAreaAabb(src);
            if (!visibleAabb.IsEmpty)
            {
                const int canvasWidth = 800;
                const int canvasHeight = 600;
                double dx = 0, dy = 0;

                if (visibleAabb.Left < 0)
                    dx = -visibleAabb.Left;
                else if (visibleAabb.Right > canvasWidth)
                    dx = canvasWidth - visibleAabb.Right;

                if (visibleAabb.Top < 0)
                    dy = -visibleAabb.Top;
                else if (visibleAabb.Bottom > canvasHeight)
                    dy = canvasHeight - visibleAabb.Bottom;

                if (dx != 0 || dy != 0)
                {
                    src.CanvasX += (int)Math.Round(dx);
                    src.CanvasY += (int)Math.Round(dy);
                }
            }
        }

        await SaveSourcesAsync();
    }

    private (int x, int y) FindAvailableCanvasPosition(int sourceWidth = 200, int sourceHeight = 150)
    {
        const int gridSize = 20; // A slightly larger step
        const int canvasWidth = 800;
        const int canvasHeight = 600;
        const int margin = 10;
        
        // Ensure source fits within canvas with margin
        sourceWidth = Math.Min(sourceWidth, canvasWidth - 2 * margin);
        sourceHeight = Math.Min(sourceHeight, canvasHeight - 2 * margin);
        
        // Try to find a position in a grid pattern
        for (int row = 0; row * gridSize + sourceHeight + margin < canvasHeight; row++)
        {
            for (int col = 0; col * gridSize + sourceWidth + margin < canvasWidth; col++)
            {
                int x = col * gridSize + margin;
                int y = row * gridSize + margin;
                
                // Check if this position overlaps with any existing source
                var testRect = new Rect(x, y, sourceWidth, sourceHeight);
                bool occupied = Sources.Any(s => 
                {
                    var sourceRect = new Rect(s.CanvasX, s.CanvasY, s.CanvasWidth, s.CanvasHeight);
                    return RectsIntersect(testRect, sourceRect);
                });
                
                if (!occupied)
                    return (x, y);
            }
        }
        
        // If all grid positions are occupied, try to find any free space
        var random = new Random();
        for (int attempt = 0; attempt < 50; attempt++)
        {
            int x = random.Next(margin, Math.Max(margin + 1, canvasWidth - sourceWidth - margin));
            int y = random.Next(margin, Math.Max(margin + 1, canvasHeight - sourceHeight - margin));
            
            var testRect = new Rect(x, y, sourceWidth, sourceHeight);
            bool occupied = Sources.Any(s => 
            {
                var sourceRect = new Rect(s.CanvasX, s.CanvasY, s.CanvasWidth, s.CanvasHeight);
                return RectsIntersect(testRect, sourceRect);
            });
            
            if (!occupied)
                return (x, y);
        }
        
        // Last resort: cascade from top-left
        return (margin + Sources.Count * 20, margin + Sources.Count * 20);
    }
    
    private async Task LoadSourcesAsync()
    {
        try
        {
            var savedSources = await _localSettingsService.ReadSettingAsync<SourceItem[]>(SourcesSettingsKey);
            if (savedSources != null)
            {
                foreach (var source in savedSources)
                {
                    if (source != null && source.Id != Guid.Empty)
                    {
                        // Ensure source has valid dimensions
                        if (source.CanvasWidth <= 0) source.CanvasWidth = 200;
                        if (source.CanvasHeight <= 0) source.CanvasHeight = 150;
                        
                        // Set preview state to match global preview toggle
                        source.IsLivePreviewEnabled = IsPreviewing;
                        
                    Sources.Add(source);
                    
                        // Don't automatically start capturing - wait for user to start recording
                    }
                }
            }
        }
        catch
        {
            // Handle load error if needed
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
            
            var savedIsPreviewing = await _localSettingsService.ReadSettingAsync<bool?>(IsPreviewingSettingsKey);
            if (savedIsPreviewing.HasValue)
            {
                IsPreviewing = savedIsPreviewing.Value;
            }
        }
        catch
        {
            // If loading fails, keep default values
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
    
    private async Task SaveIsPreviewingAsync()
    {
        try
        {
            await _localSettingsService.SaveSettingAsync(IsPreviewingSettingsKey, IsPreviewing);
        }
        catch
        {
            // Handle save error if needed
        }
    }
    
    private void OnFrameAvailable(object? sender, SourceFrameEventArgs e)
    {
        // Send individual source frame to MJPEG streaming service
        if (e.FrameData != null)
        {
            _mjpegStreamingService.UpdateSourceFrame(e.Source.Id, e.FrameData);
        }

        // Update live preview if this source is selected
        if (e.Source.IsSelected && e.FrameImage != null)
        {
            // TODO: Update live preview UI with e.FrameImage
            System.Diagnostics.Debug.WriteLine($"📺 Live preview frame for {e.Source.Name}");
        }
    }
    

    
    private void OnStreamingUrlChanged(object? sender, string url)
    {
        StreamingUrl = string.IsNullOrEmpty(url) ? null : url;
    }
    
    [RelayCommand]
    private async Task ToggleRecordingAsync()
    {
        if (IsRecording)
        {
            // Stop recording
            await StopAllCapturesAsync();
            await _mjpegStreamingService.StopStreamingAsync();
            IsRecording = false;
            IsPaused = false;
            CanPause = false;
            StreamingUrl = null;
        }
        else
        {
            // Start recording
            IsRecordingLoading = true;
            try
        {
            await StartAllCapturesAsync();
                await _mjpegStreamingService.StartStreamingAsync(PreviewFps);
            IsRecording = true;
                CanPause = true;
                NeedsRefresh = false; // Clear refresh flag when starting
            }
            finally
            {
                IsRecordingLoading = false;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanPause))]
    private async Task TogglePauseAsync()
    {
        if (IsPaused)
        {
            // Resume recording
            await StartAllCapturesAsync();
            IsPaused = false;
        }
        else
        {
            // Pause recording
            await StopAllCapturesAsync();
            IsPaused = true;
        }
    }

    [RelayCommand]
    private async Task RefreshStreamAsync()
    {
        if (!IsRecording) return;
        
        IsRecordingLoading = true;
        try
        {
            // Stop current captures
            await StopAllCapturesAsync();
            
            // Restart captures with new canvas configuration
            await StartAllCapturesAsync();
            
            // Clear the refresh flag
            NeedsRefresh = false;
            }
        finally
        {
            IsRecordingLoading = false;
        }
    }

    public async Task StartAllCapturesAsync()
    {
        try
        {
            // Start capture for each source
            foreach (var source in Sources)
            {
                await _captureService.StartCaptureAsync(source);
        }
        
            // Start streaming service
            await _mjpegStreamingService.StartStreamingAsync();
            
            System.Diagnostics.Debug.WriteLine($"✅ Started capturing {Sources.Count} sources and streaming service");
            }
        catch (Exception ex)
            {
            System.Diagnostics.Debug.WriteLine($"❌ Failed to start captures: {ex.Message}");
        }
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
    
    public async Task StopAllCapturesAsync()
    {
        try
        {
            // Stop all captures
        await _captureService.StopAllCapturesAsync();

            System.Diagnostics.Debug.WriteLine("🛑 Stopped all captures");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"❌ Failed to stop captures: {ex.Message}");
        }
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
        // Mark canvas as needing refresh if recording
        if (IsRecording && !_isUndoRedoOperation)
        {
            NeedsRefresh = true;
        }
        
        if (e.NewItems != null)
        {
            foreach (SourceItem item in e.NewItems)
            {
                item.PropertyChanged += Source_PropertyChanged;
            }
        }
        
        if (e.OldItems != null)
        {
            foreach (SourceItem item in e.OldItems)
            {
                item.PropertyChanged -= Source_PropertyChanged;
            }
        }
    }

    private void Source_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_isUndoRedoOperation) return;
        
        if (e.PropertyName == nameof(SourceItem.IsLivePreviewEnabled))
        {
            // Handle preview state changes if needed
            return;
        }

        // Check if property change affects recording
        var recordingRelatedProperties = new[]
        {
            nameof(SourceItem.CanvasWidth),
            nameof(SourceItem.CanvasHeight),
            nameof(SourceItem.CanvasX),
            nameof(SourceItem.CanvasY),
            nameof(SourceItem.Type),
            nameof(SourceItem.DeviceId),
            nameof(SourceItem.ProcessPath),
            nameof(SourceItem.Rotation),
            nameof(SourceItem.RegionX),
            nameof(SourceItem.RegionY),
            nameof(SourceItem.RegionWidth),
            nameof(SourceItem.RegionHeight)
        };

        if (IsRecording && recordingRelatedProperties.Contains(e.PropertyName))
        {
            NeedsRefresh = true;
                }

        // Save state for important property changes
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

    private static bool RectsIntersect(Rect r1, Rect r2)
    {
        return r1.X < r2.X + r2.Width && 
               r1.X + r1.Width > r2.X && 
               r1.Y < r2.Y + r2.Height && 
               r1.Y + r1.Height > r2.Y;
    }
}
