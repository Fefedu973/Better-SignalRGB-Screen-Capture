using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Helpers;
using Better_SignalRGB_Screen_Capture.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Better_SignalRGB_Screen_Capture.Views;

public sealed partial class DraggableSourceItem : UserControl
{
    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(nameof(Source), typeof(SourceItem), typeof(DraggableSourceItem), 
            new PropertyMetadata(null, OnSourceChanged));

    public SourceItem Source
    {
        get => (SourceItem)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public event TypedEventHandler<DraggableSourceItem, RoutedEventArgs>? EditRequested;
    public event TypedEventHandler<DraggableSourceItem, RoutedEventArgs>? DeleteRequested;
    public event TypedEventHandler<DraggableSourceItem, RoutedEventArgs>? CopyRequested;
    public event TypedEventHandler<DraggableSourceItem, RoutedEventArgs>? PasteRequested;
    public event TypedEventHandler<DraggableSourceItem, RoutedEventArgs>? CenterRequested;
    public event EventHandler? DragStarted;

    private readonly ICaptureService? _captureService;
    private SourceItem? _observedSource;
    private bool _visualRefreshQueued;

    public DraggableSourceItem()
    {
        InitializeComponent();
        
        // Get the capture service
        _captureService = App.GetService<ICaptureService>();
        
        // Set up event handlers
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCanceled += OnPointerInteractionCanceled;
        PointerCaptureLost += OnPointerInteractionCanceled;
        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
        RightTapped += OnRightTapped;
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        Unloaded += OnUnloaded;
        KeyDown += OnInteractionKeyDown;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CancelCrop();
        EndGesture(save: true);
        _mediaClip?.Dispose(); _mediaClip = null;
        if (_observedSource != null)
        {
            _observedSource.PropertyChanged -= OnSourcePropertyChanged;
            _observedSource = null;
        }
        DetachPreview();
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DraggableSourceItem control)
        {
            control.CancelCrop();
            control.EndGesture(save: true);
            if (control._observedSource != null)
            {
                control._observedSource.PropertyChanged -= control.OnSourcePropertyChanged;
                control._observedSource = null;
            }
            Interlocked.Increment(ref control._previewGeneration);
            control._previewEnabled = false;
            control._previewSource = null;
            control.PreviewImage.Source = null;
            control._lastShade = null;
            if (e.NewValue is not SourceItem source) return;
            control.RefreshPosition();
            control.SetSelected(source.IsSelected);
            if (control.IsLoaded) control.OnLoaded(control, new RoutedEventArgs());
        }
    }

    private void OnSourcePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => OnSourcePropertyChanged(sender, e));
            return;
        }
        if (!IsLoaded || !ReferenceEquals(sender, Source)) return;
        
        switch (e.PropertyName)
        {
            case nameof(SourceItem.IsLocked):
                if (Source.IsLocked) TryCancelInteraction();
                UpdateSelectionVisuals();
                break;
            case nameof(SourceItem.IsSelected):
                if (_isCropping && !Source.IsSelected) CancelCrop();
                SetSelected(Source.IsSelected);
                break;
            case nameof(SourceItem.CanvasX):
            case nameof(SourceItem.CanvasY):
            case nameof(SourceItem.CanvasWidth):
            case nameof(SourceItem.CanvasHeight):
            case nameof(SourceItem.Rotation):
            case nameof(SourceItem.CropLeftPct):
            case nameof(SourceItem.CropTopPct):
            case nameof(SourceItem.CropRightPct):
            case nameof(SourceItem.CropBottomPct):
            case nameof(SourceItem.CropRotation):
                if (_updatingCropProperties || _visualRefreshQueued) return;
                _visualRefreshQueued = true;
                if (!DispatcherQueue.TryEnqueue(() =>
                {
                    _visualRefreshQueued = false;
                    if (!IsLoaded || Source == null) return;
                    RefreshPosition();
                    if (_isCropping)
                        UpdateCropVisuals(new Rect(
                            ActualWidth * Source.CropLeftPct,
                            ActualHeight * Source.CropTopPct,
                            ActualWidth * Math.Max(0, 1 - Source.CropLeftPct - Source.CropRightPct),
                            ActualHeight * Math.Max(0, 1 - Source.CropTopPct - Source.CropBottomPct)),
                            Source.CropRotation, updateSource: false);
                })) _visualRefreshQueued = false;
                break;
            case nameof(SourceItem.DisplayName):
                DisplayNameText.Text = Source.DisplayName;
                break;
            case nameof(SourceItem.Type):
            case nameof(SourceItem.WebsiteUrl):
                RefreshPreviewState();
                DisplayNameText.Text = Source.DisplayName;
                break;
            case nameof(SourceItem.IsLivePreviewEnabled):
                RefreshPreviewState();
                if (!_previewEnabled) PreviewImage.Source = null;
                break;
        }
    }

    public void RefreshPosition()
    {
        if (Source != null)
        {
            Canvas.SetLeft(this, Source.CanvasX);
            Canvas.SetTop(this, Source.CanvasY);
            Width = Source.CanvasWidth;
            Height = Source.CanvasHeight;
            RotateTransform.Angle = Source.Rotation;

            UpdateSelectionVisuals();
            UpdateCropShading();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Source != null)
        {
            if (_observedSource != null) _observedSource.PropertyChanged -= OnSourcePropertyChanged;
            _observedSource = Source;
            Source.PropertyChanged += OnSourcePropertyChanged;
            if (_captureService != null)
            {
                _captureService.FrameAvailable -= OnFrameAvailable;
                _captureService.FrameAvailable += OnFrameAvailable;
                _captureService.CaptureFailed -= OnPreviewCaptureFailed;
                _captureService.CaptureFailed += OnPreviewCaptureFailed;
            }
            RefreshPreviewState();
            DisplayNameText.Text = Source.DisplayName;
            RefreshPosition();
            UpdateCropShading();
        }
        // Don't initialize root clipping - we want handles to extend outside bounds
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // This is the most reliable place to update visuals that depend on the final rendered size.
        if (Source != null)
        {
            RefreshPosition();
        }
    }

    public void SetSelected(bool selected)
    {
        VisualStateManager.GoToState(this, selected ? "Selected" : "Normal", true);
        UpdateSelectionVisuals();
    }

    public void CleanupOnDelete()
    {
        CancelCrop();
        EndGesture(save: false);
        DetachPreview();
        PreviewImage.Source = null;
        _mediaClip?.Dispose();
        _mediaClip = null;
        if (_observedSource != null)
        {
            _observedSource.PropertyChanged -= OnSourcePropertyChanged;
            _observedSource = null;
        }
    }
}
