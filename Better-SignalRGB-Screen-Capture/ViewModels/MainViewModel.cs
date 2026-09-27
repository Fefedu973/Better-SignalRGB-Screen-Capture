using System.Collections.ObjectModel;
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Better_SignalRGB_Screen_Capture.ViewModels;

public partial class MainViewModel : ObservableRecipient
{
    private readonly ILocalSettingsService _localSettingsService;
    private readonly ICaptureService _captureService;
    private readonly IMjpegStreamingService _mjpegStreamingService;
    private readonly ICompositeFrameService _compositeFrameService;
    private readonly IKestrelApiService _kestrelApiService;
    private readonly IPipelineDiagnosticsService _pipelineDiagnostics;
    private const string SourcesSettingsKey = "SavedSources";
    private const string PreviewFpsSettingsKey = "PreviewFps";
    private const string IsPreviewingSettingsKey = "IsPreviewing";
    private const string WaitForSourceAvailabilityKey = "WaitForSourceAvailability";
    private const string StreamingPortKey = "StreamingPort";
    private const string HttpsPortKey = "HttpsPort";

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
    [NotifyPropertyChangedFor(nameof(IsCanvasEditable))]
    private bool isRecordingLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCanvasEditable))]
    private bool isEditingSources;

    public bool IsCanvasEditable => !IsEditingSources && !IsRecordingLoading && !_shuttingDown;

    partial void OnIsEditingSourcesChanged(bool value) => NotifyEditingAvailability();
    partial void OnIsRecordingLoadingChanged(bool value) => NotifyEditingAvailability();

    private void NotifyEditingAvailability()
    {
        NotifySelectionLocks();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        PasteSourceCommand.NotifyCanExecuteChanged();
        ToggleHighQualityCommand.NotifyCanExecuteChanged();
    }

    private IDisposable? BeginSourceEdit()
    {
        if (!IsCanvasEditable) return null;
        IsEditingSources = true;
        return new SourceEditScope(() => IsEditingSources = false);
    }

    private sealed class SourceEditScope(Action complete) : IDisposable
    {
        public void Dispose() => complete();
    }

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
    private bool _isInitializing = true;
    private readonly HashSet<SourceItem> _observedSources = new();
    private readonly System.Threading.SemaphoreSlim _captureLifecycle = new(1, 1);
    private Task? _availabilityTask;
    private System.Threading.CancellationTokenSource? _saveSourcesCts;
    private Task _pendingSourceSave = Task.CompletedTask;
    public Task Initialization { get; }

    [ObservableProperty]
    private string? statusMessage;

    [ObservableProperty]
    private bool _waitForSourceAvailability = true;

    private System.Threading.CancellationTokenSource? _availabilityCts;

    partial void OnWaitForSourceAvailabilityChanged(bool value)
    {
        // Persist change
        if (!_isInitializing) _ = SaveAvailabilitySettingAsync(value);
    }

    public MainViewModel(ILocalSettingsService localSettingsService, ICaptureService captureService, IMjpegStreamingService mjpegStreamingService, ICompositeFrameService compositeFrameService, IKestrelApiService kestrelApiService,
        ISceneLibraryService sceneLibrary, ISceneFilePickerService sceneFiles, IPipelineDiagnosticsService pipelineDiagnostics)
    {
        _localSettingsService = localSettingsService;
        _captureService = captureService;
        _mjpegStreamingService = mjpegStreamingService;
        _kestrelApiService = kestrelApiService;
        _compositeFrameService = compositeFrameService;
        _sceneLibrary = sceneLibrary;
        _sceneFiles = sceneFiles;
        _pipelineDiagnostics = pipelineDiagnostics;

        // Subscribe to events
        _captureService.CaptureFailed += (_, error) => App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            StatusMessage = $"Capture failed for {error.Source.DisplayName}: {error.Error}");
        _mjpegStreamingService.StreamingUrlChanged += OnStreamingUrlChanged;
        _kestrelApiService.StreamingUrlChanged += OnKestrelStreamingUrlChanged;

        // Listen to Sources collection changes to attach/detach property change listeners
        Sources.CollectionChanged += Sources_CollectionChanged;


        // Initialize composite frame service with canvas size
        _compositeFrameService.SetCanvasSize(320, 200);

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

        // ensure additional setting load
        Initialization = InitializeAsync();
        InitializeScenes();
    }

    partial void OnPreviewFpsChanged(int value)
    {
        // Save FPS setting when it changes
        if (!_isInitializing) _ = SavePreviewFpsAsync();

        // Update capture service framerate
        _ = UpdateCaptureFrameRateAsync(value);
    }

    private async Task UpdateCaptureFrameRateAsync(int value)
    {
        try { await _captureService.SetFrameRate(value); }
        catch (Exception ex) { StatusMessage = $"Could not change frame rate: {ex.Message}"; }
    }

    partial void OnIsPreviewingChanged(bool value)
    {
        // Save the preview setting when it changes
        if (!_isInitializing) _ = SaveIsPreviewingAsync();

        // Update IsLivePreviewEnabled for all sources
        foreach (var source in Sources)
        {
            source.IsLivePreviewEnabled = value;
        }
    }

    private void Sources_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        _compositeFrameService.InvalidateLayout();
        // Reset has no OldItems; reconcile subscriptions explicitly (including Clear/undo).
        foreach (var removed in _observedSources.Where(source => !Sources.Contains(source)).ToArray())
        {
            removed.PropertyChanged -= Source_PropertyChanged;
            _observedSources.Remove(removed);
            _compositeFrameService.RemoveSource(removed);
            _pipelineDiagnostics.RemoveSource(removed.Id);
        }
        foreach (var added in Sources)
            if (_observedSources.Add(added)) added.PropertyChanged += Source_PropertyChanged;
    }

    private void Source_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_isUndoRedoOperation) return;
        if (e.PropertyName is nameof(SourceItem.CanvasX) or nameof(SourceItem.CanvasY)
            or nameof(SourceItem.CanvasWidth) or nameof(SourceItem.CanvasHeight) or nameof(SourceItem.Rotation)
            or nameof(SourceItem.CropLeftPct) or nameof(SourceItem.CropTopPct) or nameof(SourceItem.CropRightPct)
            or nameof(SourceItem.CropBottomPct) or nameof(SourceItem.CropRotation) or nameof(SourceItem.Opacity)
            or nameof(SourceItem.IsMirroredHorizontally) or nameof(SourceItem.IsMirroredVertically))
            _compositeFrameService.InvalidateLayout();

        if (e.PropertyName == nameof(SourceItem.IsLivePreviewEnabled))
        {
            // Handle preview state changes if needed
            return;
        }

        // Check if property change affects recording
        if (IsRecording && e.PropertyName is nameof(SourceItem.Type) or nameof(SourceItem.MonitorDeviceId)
            or nameof(SourceItem.CanvasWidth) or nameof(SourceItem.CanvasHeight)
            or nameof(SourceItem.ProcessId) or nameof(SourceItem.ProcessPath) or nameof(SourceItem.RegionBounds)
            or nameof(SourceItem.WindowHandle) or nameof(SourceItem.WindowTitle)
            or nameof(SourceItem.WebcamDeviceId) or nameof(SourceItem.WebcamFormatId) or nameof(SourceItem.WebsiteUrl)
            or nameof(SourceItem.WebsiteWidth) or nameof(SourceItem.WebsiteHeight)
            or nameof(SourceItem.WebsiteUserAgent) or nameof(SourceItem.WebsiteZoom)
            or nameof(SourceItem.WebsiteRefreshInterval)) NeedsRefresh = true;

        // Save state for important property changes
        switch (e.PropertyName)
        {
            case nameof(SourceItem.IsLocked):
                NotifySelectionLocks();
                break;
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

    }




}
