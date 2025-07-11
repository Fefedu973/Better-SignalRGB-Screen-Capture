using System;
using System.Numerics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.ViewModels;
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using System.IO;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using System.Diagnostics;

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
    public event EventHandler<Point>? DragDelta;
    public event EventHandler? DragStarted;

    private bool _isDragging;
    private bool _isResizing;
    private ResizeMode _resizeMode = ResizeMode.None;
    private bool _isSelected;
    private bool _isCropping;
    private readonly ICaptureService? _captureService;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _websiteRefreshTimer;

    // Drag/resize start state
    private Point _actionStartPointerPosition;
    private Rect _actionStartBounds;
    private double _actionStartRotation;
    
    // Crop state
    private Rect _cropStartRect;
    private ResizeMode _cropResizeMode = ResizeMode.None;
    private Point _cropActionStartPointerPosition;
    private Rect _cropActionStartBounds;
    private double _cropActionStartRotation;

    private enum ResizeMode { None, TopLeft, TopRight, BottomLeft, BottomRight, Top, Bottom, Left, Right, Rotate, Move, CropRotate }

    /// <summary>
    /// Converts a point from rotated coordinate space back to unrotated coordinate space
    /// </summary>
    /// <param name="p">Point in rotated coordinate space</param>
    /// <returns>Point in unrotated coordinate space</returns>
    private Point ToUnrotated(Point p)
    {
        if (RotateTransform == null || RotateTransform.Angle == 0) return p;
        
        var centerX = ActualWidth * 0.5;
        var centerY = ActualHeight * 0.5;
        
        // Translate to origin
        var translatedX = p.X - centerX;
        var translatedY = p.Y - centerY;
        
        // Apply reverse rotation
        var angleRad = -RotateTransform.Angle * Math.PI / 180.0;
        var cos = Math.Cos(angleRad);
        var sin = Math.Sin(angleRad);
        
        var unrotatedX = translatedX * cos - translatedY * sin;
        var unrotatedY = translatedX * sin + translatedY * cos;
        
        // Translate back
        return new Point(unrotatedX + centerX, unrotatedY + centerY);
    }

    /// <summary>
    /// Returns the axis-aligned bounding box (AABB) of a rotated rectangle
    /// </summary>
    /// <param name="center">Center point of the rectangle</param>
    /// <param name="size">Size of the rectangle</param>
    /// <param name="angleDeg">Rotation angle in degrees</param>
    /// <returns>The AABB that fully contains the rotated rectangle</returns>
    private static Rect GetRotatedAabb(Point center, Size size, double angleDeg)
    {
        var angle = angleDeg * Math.PI / 180;
        var cos = Math.Abs(Math.Cos(angle));
        var sin = Math.Abs(Math.Sin(angle));

        var w = size.Width * cos + size.Height * sin;
        var h = size.Height * cos + size.Width * sin;

        return new Rect(center.X - w / 2, center.Y - h / 2, w, h);
    }

    private static Rect IntersectRects(Rect r1, Rect r2)
    {
        double x1 = Math.Max(r1.X, r2.X);
        double y1 = Math.Max(r1.Y, r2.Y);
        double x2 = Math.Min(r1.X + r1.Width, r2.X + r2.Width);
        double y2 = Math.Min(r1.Y + r1.Height, r2.Y + r2.Height);

        if (x2 >= x1 && y2 >= y1)
            return new Rect(x1, y1, x2 - x1, y2 - y1);
        return Rect.Empty;
    }

    /// <summary>
    /// Gets the effective hit box for this source item, taking into account cropping
    /// </summary>
    /// <returns>The hit box rectangle in canvas coordinates</returns>
    public Rect GetEffectiveHitBox()
    {
        if (Source == null) return new Rect();

        // Calculate the visible (non-cropped) area
        double visibleLeft = ActualWidth * Source.CropLeftPct;
        double visibleTop = ActualHeight * Source.CropTopPct;
        double visibleRight = ActualWidth * Source.CropRightPct;
        double visibleBottom = ActualHeight * Source.CropBottomPct;
        
        double visibleWidth = ActualWidth - visibleLeft - visibleRight;
        double visibleHeight = ActualHeight - visibleTop - visibleBottom;
        
        if (visibleWidth <= 0 || visibleHeight <= 0)
            return new Rect(); // Completely cropped
        
        // The visible area within this control
        var visibleRect = new Rect(visibleLeft, visibleTop, visibleWidth, visibleHeight);
        
        // Transform to canvas coordinates
        var canvasRect = new Rect(
            Source.CanvasX + visibleRect.X,
            Source.CanvasY + visibleRect.Y,
            visibleRect.Width,
            visibleRect.Height
        );
        
        return canvasRect;
    }

    public DraggableSourceItem()
    {
        InitializeComponent();
        
        // Get the capture service
        _captureService = App.GetService<ICaptureService>();
        
        // Subscribe to frame updates
        if (_captureService != null)
        {
            _captureService.FrameAvailable += OnFrameAvailable;
        }
        
        // Set up event handlers
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
        RightTapped += OnRightTapped;
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        Unloaded += OnUnloaded;
    }

    private void OnFrameAvailable(object? sender, SourceFrameEventArgs e)
    {
        if (Source == null || e.Source.Id != Source.Id || !Source.IsLivePreviewEnabled) return;
        
        // Skip frame if previous update is still in progress
        if (_isUpdatingFrame) return;
        _isUpdatingFrame = true;
        
        // Must create BitmapImage on UI thread
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (e.FrameData != null && e.FrameData.Length > 0)
                {
                    // For website sources, skip the preview image as we use WebView
                    if (Source.Type == SourceType.Website)
                    {
                        _isUpdatingFrame = false;
                        return;
                    }
                    
                    // Convert JPEG byte array to BitmapImage
                    using var stream = new InMemoryRandomAccessStream();
                    using var writer = new Windows.Storage.Streams.DataWriter(stream);
                    writer.WriteBytes(e.FrameData);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                    writer.DetachStream();
                    
                    stream.Seek(0);
                    
                    var bitmap = new BitmapImage();
                    bitmap.DecodePixelHeight = 600; // Limit size to prevent memory issues
                    await bitmap.SetSourceAsync(stream);
                    
                    PreviewImage.Source = bitmap;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error displaying frame: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                _isUpdatingFrame = false;
            }
        });
    }
    
    private bool _isUpdatingFrame = false;

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (Source != null)
        {
            var mainViewModel = App.GetService<MainViewModel>();
            mainViewModel?.UnregisterDraggableSourceControl(Source);
        }

        if (_websiteRefreshTimer != null)
        {
            _websiteRefreshTimer.Stop();
            _websiteRefreshTimer = null;
        }

        // Dispose WebView resources to stop audio and clean up properly
        DisposeWebViewResources();

        if (_captureService != null)
        {
            _captureService.FrameAvailable -= OnFrameAvailable;
        }
    }
    
    private void DisposeWebViewResources()
    {
        try
        {
            if (WebsitePreview?.CoreWebView2 != null)
            {
                // Stop any ongoing navigation
                WebsitePreview.CoreWebView2.Stop();
                
                // Navigate to about:blank to stop any media playback (videos, audio, etc.)
                WebsitePreview.CoreWebView2.Navigate("about:blank");
                
                // Unsubscribe from events
                WebsitePreview.CoreWebView2.NavigationCompleted -= WebView_NavigationCompleted;
                
                Debug.WriteLine($"🧹 Disposed WebView resources for {Source?.Name}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error disposing WebView resources: {ex.Message}");
        }
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DraggableSourceItem control && e.NewValue is SourceItem source)
        {
            // Unsubscribe from old source if any
            if (e.OldValue is SourceItem oldSource)
            {
                oldSource.PropertyChanged -= control.OnSourcePropertyChanged;
            }
            
            // Subscribe to new source and update position
            source.PropertyChanged += control.OnSourcePropertyChanged;
            control.UpdatePosition();
            control.UpdateDisplay(source);
            control.SetSelected(source.IsSelected);
        }
    }

    private void OnSourcePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (Source == null) return;
        
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(SourceItem.CanvasX):
                case nameof(SourceItem.CanvasY):
                case nameof(SourceItem.CanvasWidth):
                case nameof(SourceItem.CanvasHeight):
                    RefreshPosition();
                    break;
                
                case nameof(SourceItem.IsSelected):
                    _isSelected = Source.IsSelected;
                    if (_isCropping && !_isSelected)
                    {
                        CancelCrop();
                    }
                    SetSelected(_isSelected);
                    break;

                case nameof(SourceItem.Rotation):
                    RotateTransform.Angle = Source.Rotation;
                    break;
                
                case nameof(SourceItem.CropLeftPct):
                case nameof(SourceItem.CropTopPct):
                case nameof(SourceItem.CropRightPct):
                case nameof(SourceItem.CropBottomPct):
                case nameof(SourceItem.CropRotation):
                    UpdateCropShading();
                    // If in crop mode, also update the crop rectangle visuals
                    if (_isCropping)
                    {
                        var newCropRect = new Rect(
                            ActualWidth * Source.CropLeftPct,
                            ActualHeight * Source.CropTopPct,
                            ActualWidth * (1 - Source.CropLeftPct - Source.CropRightPct),
                            ActualHeight * (1 - Source.CropTopPct - Source.CropBottomPct)
                        );
                        UpdateCropVisuals(newCropRect, Source.CropRotation);
                    }
                    break;

                case nameof(SourceItem.DisplayName):
                case nameof(SourceItem.Type):
                case nameof(SourceItem.WebsiteUrl):
                    UpdateDisplay(Source);
                    break;
                    
                case nameof(SourceItem.WebsiteZoom):
                    if (Source.Type == SourceType.Website)
                    {
                        _ = SetPageZoomAsync();
                    }
                    break;
                    
                case nameof(SourceItem.WebsiteRefreshInterval):
                    if (Source.Type == SourceType.Website)
                    {
                        SetupWebsiteRefreshTimer(); // Restart timer with new interval
                    }
                    break;
                    
                case nameof(SourceItem.WebsiteUserAgent):
                    if (Source.Type == SourceType.Website && WebsitePreview.CoreWebView2 != null)
                    {
                        WebsitePreview.CoreWebView2.Settings.UserAgent = Source.WebsiteUserAgent;
                        // Reload to apply new user agent
                        if (!string.IsNullOrEmpty(Source.WebsiteUrl))
                        {
                            WebsitePreview.CoreWebView2.Navigate(Source.WebsiteUrl);
                        }
                    }
                    break;
            }
        });
    }

    private void UpdatePosition()
    {
        if (Source != null)
        {
            RefreshPosition();
        }
    }
    
    public void ForcePositionUpdate()
    {
        if (Source != null)
        {
            // Force immediate position update without any conditions
            Canvas.SetLeft(this, Source.CanvasX);
            Canvas.SetTop(this, Source.CanvasY);
            Width = Source.CanvasWidth;
            Height = Source.CanvasHeight;
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

            // Position rotation handle
            var handleX = Source.CanvasWidth / 2.0;
            Canvas.SetLeft(RotationHandle, handleX - (RotationHandle.Width / 2));
            Canvas.SetTop(RotationHandle, -30); // 30px above the item

            RotationHandleLine.X1 = handleX;
            RotationHandleLine.Y1 = -14; // Start from handle bottom
            RotationHandleLine.X2 = handleX;
            RotationHandleLine.Y2 = 0; // End at top of item

            UpdateCropShading();
        }
    }

    private void UpdateCropShadingWithValues(
    double cropLeftPct, double cropTopPct,
    double cropRightPct, double cropBottomPct,
    double cropRotation)
    {
        // Check if no cropping is applied (all percentages are 0 or very close to 0)
        const double tolerance = 0.001;
        bool noCroppingApplied = cropLeftPct < tolerance && cropTopPct < tolerance && 
                                cropRightPct < tolerance && cropBottomPct < tolerance;
        
        if (noCroppingApplied)
        {
            // No cropping applied - hide the shade completely
            CropShadePath.Data = null;
            return;
        }

        // ─────── Create rounded outer bounds instead of rectangular ───────
        var cornerRadius = GetControlCornerRadius();
        var outer = CreateRoundedRectangleGeometry(0, 0, ActualWidth, ActualHeight, cornerRadius);

        // ─────── inner "hole" rect (the visible part) ───────
        double left = ActualWidth * cropLeftPct;
        double top = ActualHeight * cropTopPct;
        double right = ActualWidth * cropRightPct;
        double bottom = ActualHeight * cropBottomPct;

        var innerWidth = Math.Max(0, ActualWidth - left - right);
        var innerHeight = Math.Max(0, ActualHeight - top - bottom);
        
        // If the inner area would be too small or invalid, show full shade
        if (innerWidth < 1 || innerHeight < 1)
        {
            CropShadePath.Data = outer;
            return;
        }

        var inner = new Microsoft.UI.Xaml.Media.RectangleGeometry
        {
            Rect = new Rect(left, top, innerWidth, innerHeight)
        };

        // centre of the visible part → rotation pivot
        double pivotX = left + innerWidth / 2;
        double pivotY = top + innerHeight / 2;

        inner.Transform = Math.Abs(cropRotation) < 0.1
                            ? null
                            : new Microsoft.UI.Xaml.Media.RotateTransform
                            {
                                Angle = cropRotation,
                                CenterX = pivotX,
                                CenterY = pivotY
                            };

        // ─────── combine with EvenOdd rule ("outer minus inner") ───────
        var geometryCollection = new Microsoft.UI.Xaml.Media.GeometryCollection();
        geometryCollection.Add(outer);
        geometryCollection.Add(inner);

        var geometryGroup = new Microsoft.UI.Xaml.Media.GeometryGroup
        {
            FillRule = Microsoft.UI.Xaml.Media.FillRule.EvenOdd,
            Children = geometryCollection
        };

        CropShadePath.Data = geometryGroup;
    }

    private void UpdateCropShading()
    {
        if (Source == null) return;

        UpdateCropShadingWithValues(Source.CropLeftPct, Source.CropTopPct, Source.CropRightPct, Source.CropBottomPct, Source.CropRotation);
    }

    public string GetTypeDisplayString(SourceType type)
    {
        return type switch
        {
            SourceType.Monitor => "Monitor",
            SourceType.Process => "Process",
            SourceType.Region => "Region",
            SourceType.Webcam => "Webcam",
            _ => "Unknown"
        };
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isCropping) return;

        _actionStartPointerPosition = e.GetCurrentPoint(this.Parent as UIElement).Position;
        _actionStartBounds = new Rect(Source.CanvasX, Source.CanvasY, Source.CanvasWidth, Source.CanvasHeight);
        _actionStartRotation = Source.Rotation;
        _resizeMode = GetResizeMode(e.GetCurrentPoint(this).Position);

        // If the item isn't selected, handle the selection now.
        // If it's already selected, we don't want to change the selection state
        // (e.g., deselecting it on a shift+click) when starting a resize.
        if (_resizeMode != ResizeMode.None && !Source.IsSelected)
        {
            var isCtrlDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
            var isShiftDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
            HandleSelection(isCtrlDown || isShiftDown);
        }

        if (_resizeMode != ResizeMode.None)
        {
            this.CapturePointer(e.Pointer);
            ProtectedCursor = GetCursor(_resizeMode);
            if (_resizeMode == ResizeMode.Rotate)
            {
                _isResizing = false;
                _isDragging = false;
            }
            else
            {
                _isResizing = true;
            }
        }
        else
        {
            _isDragging = true;
            this.CapturePointer(e.Pointer);
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
            DragStarted?.Invoke(this, EventArgs.Empty);
        }
        
        // Ensure the main page has focus to receive keyboard shortcuts
        var mainPage = FindParent<MainPage>(this);
        mainPage?.Focus(FocusState.Programmatic);
        
        e.Handled = true;
    }

    private void HandleSelection(bool isMultiSelect)
    {
        if (Source == null) return;

        // Find the MainPage to access its selection methods
        var mainPage = FindParent<MainPage>(this);
        if (mainPage == null) return;

        // If there's a group selection active and this item is not part of it, clear group selection first
        var viewModel = mainPage.ViewModel;
        if (viewModel != null && viewModel.SelectedSources.Count > 1 && !Source.IsSelected && !isMultiSelect)
        {
            // Clear multi-selection before selecting this single item
            mainPage.ClearSelection();
        }

        // Call the centralized selection method
        mainPage.SelectSourceItem(Source, isMultiSelect);
    }

    private T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parent = VisualTreeHelper.GetParent(child);
        if (parent == null) return null;
        if (parent is T parentOfType) return parentOfType;
        return FindParent<T>(parent);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var parent = this.Parent as FrameworkElement;
        if (parent == null) return;
        
        var currentPoint = e.GetCurrentPoint(parent).Position;
        var isShiftDown = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift);

        if (_resizeMode == ResizeMode.Rotate)
        {
            var centerPoint = new Point(_actionStartBounds.X + _actionStartBounds.Width / 2, _actionStartBounds.Y + _actionStartBounds.Height / 2);
            var startVector = new Point(_actionStartPointerPosition.X - centerPoint.X, _actionStartPointerPosition.Y - centerPoint.Y);
            var currentVector = new Point(currentPoint.X - centerPoint.X, currentPoint.Y - centerPoint.Y);

            var startAngle = Math.Atan2(startVector.Y, startVector.X) * (180.0 / Math.PI);
            var currentAngle = Math.Atan2(currentVector.Y, currentVector.X) * (180.0 / Math.PI);

            var angleDelta = currentAngle - startAngle;

            // Normalize the delta to handle the wrap-around from +180 to -180
            if (angleDelta > 180)
            {
                angleDelta -= 360;
            }
            if (angleDelta < -180)
            {
                angleDelta += 360;
            }
            
            var newAngle = _actionStartRotation + angleDelta;
            
            if (isShiftDown)
            {
                newAngle = Math.Round(newAngle / 15.0) * 15.0; // Snap to 15-degree increments
            }

            // Create a test source to check boundaries
            var testSource = Source.Clone();
            testSource.Rotation = (int)Math.Round(newAngle);
            var aabb = MainViewModel.GetVisibleAreaAabb(testSource);
            
            // If the rotated AABB would overflow, don't apply the rotation
            if (aabb.Left >= 0 && aabb.Top >= 0 &&
                aabb.Right <= parent.ActualWidth && aabb.Bottom <= parent.ActualHeight)
            {
                Source.Rotation = (int)Math.Round(newAngle);
            }
        }
        else if (_isResizing)
        {
            var currentPointOnCanvas = e.GetCurrentPoint(parent).Position;
            var dxScreen = currentPointOnCanvas.X - _actionStartPointerPosition.X;
            var dyScreen = currentPointOnCanvas.Y - _actionStartPointerPosition.Y;
            
            // This method calculates the new bounds based on which handle is dragged,
            // keeping the opposite side anchored.
            ApplyResize(dxScreen, dyScreen, isShiftDown);
        }
        else if (_isDragging)
        {
            var deltaX = currentPoint.X - _actionStartPointerPosition.X;
            var deltaY = currentPoint.Y - _actionStartPointerPosition.Y;
            var newX = _actionStartBounds.X + deltaX;
            var newY = _actionStartBounds.Y + deltaY;

            // Use the authoritative helper to clamp the new position
            var testSource = Source.Clone();
            testSource.CanvasX = (int)newX;
            testSource.CanvasY = (int)newY;
            
            var aabb = MainViewModel.GetVisibleAreaAabb(testSource);

            double adjustX = 0, adjustY = 0;
            if (aabb.Left < 0) adjustX = -aabb.Left;
            if (aabb.Top < 0) adjustY = -aabb.Top;
            if (aabb.Right > parent.ActualWidth) adjustX = parent.ActualWidth - aabb.Right;
            if (aabb.Bottom > parent.ActualHeight) adjustY = parent.ActualHeight - aabb.Bottom;

            var finalNewX = newX + adjustX;
            var finalNewY = newY + adjustY;

            if (Source != null)
            {
                Source.CanvasX = (int)Math.Round(finalNewX);
                Source.CanvasY = (int)Math.Round(finalNewY);
            }
            DragDelta?.Invoke(this, new Point(finalNewX - _actionStartBounds.X, finalNewY - _actionStartBounds.Y));
        }
        else
        {
            var resizeMode = GetResizeMode(e.GetCurrentPoint(this).Position);
            ProtectedCursor = GetCursor(resizeMode);
        }
    }

    private void ApplyResize(double dxScreen, double dyScreen, bool keepRatio)
    {
        if (Source is null || Parent is not FrameworkElement canvas) return;

        // ---------- 0.  Work in the item's LOCAL (un-rotated) space -------------
        double theta = Source.Rotation * Math.PI / 180.0;
        double cos = Math.Cos(theta);
        double sin = Math.Sin(theta);

        // helper: screen->local  and  local->screen
        Point ToLocal(double x, double y)  => new(x *  cos + y * sin, -x * sin + y * cos);
        Point ToScreen(double x, double y) => new(x *  cos - y * sin,  x * sin + y * cos);

        Point deltaLocal = ToLocal(dxScreen, dyScreen);

        // ---------- 1.  Which edges actually move? ------------------------------
        bool mL = _resizeMode is ResizeMode.Left or   ResizeMode.TopLeft or ResizeMode.BottomLeft;
        bool mR = _resizeMode is ResizeMode.Right or  ResizeMode.TopRight or ResizeMode.BottomRight;
        bool mT = _resizeMode is ResizeMode.Top or    ResizeMode.TopLeft or ResizeMode.TopRight;
        bool mB = _resizeMode is ResizeMode.Bottom or ResizeMode.BottomLeft or ResizeMode.BottomRight;

        double startW = _actionStartBounds.Width;
        double startH = _actionStartBounds.Height;

        // ---------- 2.  Build the new size in LOCAL space -----------------------
        double newW = startW + (mR ?  deltaLocal.X : 0) - (mL ? deltaLocal.X : 0);
        double newH = startH + (mB ?  deltaLocal.Y : 0) - (mT ? deltaLocal.Y : 0);

        // enforce minimums
        newW = Math.Max(MinWidth,  newW);
        newH = Math.Max(MinHeight, newH);

        // ---------- 3.  Keep aspect ratio if Shift not held ---------------------
        if (keepRatio && startH > 0 && startW > 0)
        {
            double ratio = startW / startH;
            if (Math.Abs(deltaLocal.X) >= Math.Abs(deltaLocal.Y))
                newH = newW / ratio;
            else
                newW = newH * ratio;
        }

        // ---------- 4.  Keep the ANCHOR (opposite edge / corner) fixed ----------
        // In local coordinates the anchor is the part that is *not* moving.
        int ax = mL ? +1 : mR ? -1 : 0;   // +1 = anchor is right side, -1 = left
        int ay = mT ? +1 : mB ? -1 : 0;   // +1 = bottom, -1 = top,  0 = centre

        // local vector from centre to anchor *before* resize
        Point anchorLocal0 = new(ax * startW / 2.0, ay * startH / 2.0);
        // local vector from centre to anchor *after* resize
        Point anchorLocal1 = new(ax * newW  / 2.0, ay * newH  / 2.0);

        // world coordinates
        Point centre0 = new(_actionStartBounds.X + startW / 2.0,
                            _actionStartBounds.Y + startH / 2.0);
        Point anchorW = new(
            centre0.X + ToScreen(anchorLocal0.X, anchorLocal0.Y).X,
            centre0.Y + ToScreen(anchorLocal0.X, anchorLocal0.Y).Y);

        // new centre so that anchorWorld stays put
        Point centre1 = new(
            anchorW.X - ToScreen(anchorLocal1.X, anchorLocal1.Y).X,
            anchorW.Y - ToScreen(anchorLocal1.X, anchorLocal1.Y).Y);

        double newX = centre1.X - newW / 2.0;
        double newY = centre1.Y - newH / 2.0;

        // ---------- 5.  Check if the oriented box would overflow the canvas -------
        // Create a test source with the new values to check against the canvas
        var testSource = Source.Clone();
        testSource.CanvasX = (int)Math.Round(newX);
        testSource.CanvasY = (int)Math.Round(newY);
        testSource.CanvasWidth = (int)Math.Round(newW);
        testSource.CanvasHeight = (int)Math.Round(newH);

        var aabb = MainViewModel.GetVisibleAreaAabb(testSource);

        if (aabb.Left >= 0 && aabb.Top >= 0 &&
            aabb.Right <= canvas.ActualWidth &&
            aabb.Bottom <= canvas.ActualHeight)
        {
            // ---------- 6.  Commit if it fits -------------------------------------
            Source.CanvasX = (int)Math.Round(newX);
            Source.CanvasY = (int)Math.Round(newY);
            Source.CanvasWidth = (int)Math.Round(newW);
            Source.CanvasHeight = (int)Math.Round(newH);
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isDragging = false;
        _isResizing = false;
        _resizeMode = ResizeMode.None;
        this.ReleasePointerCapture(e.Pointer);
        ProtectedCursor = null;

        // Final save to trigger persistence after drag/resize is complete
        if (Source != null)
        {
           var viewModel = App.GetService<MainViewModel>();
           if (viewModel != null)
           {
               viewModel.SaveUndoState();
               viewModel.SaveSourcesCommand.Execute(null);
           }
        }
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging && !_isResizing)
        {
            var resizeMode = GetResizeMode(e.GetCurrentPoint(this).Position);
            ProtectedCursor = GetCursor(resizeMode);
        }
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging && !_isResizing)
        {
            ProtectedCursor = null;
        }
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // Handle selection on right-click
        var isCtrlDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        var isShiftDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        
        var mainPage = FindParent<MainPage>(this);
        var viewModel = mainPage?.ViewModel;

        if (viewModel == null) return;

        // If right-clicking an item that is NOT selected, and it's not a ctrl/shift click,
        // clear the existing selection and select only this item.
        if (!Source.IsSelected && !isCtrlDown && !isShiftDown)
        {
            viewModel.UpdateSelectedSources(new[] { Source });
        }
        // If right-clicking an item that IS selected, and there's a multi-selection,
        // we assume the user wants to act on the entire selection.
        else if (Source.IsSelected && viewModel.SelectedSources.Count > 1)
        {
            // Do nothing, keep the multi-selection as is.
        }
        else
        {
            HandleSelection(isCtrlDown || isShiftDown);
        }

        var menuFlyout = new MenuFlyout();
        
        // Decide whether to show the single-item or multi-item menu
        bool isMultiSelectAction = viewModel.SelectedSources.Count > 1 && Source.IsSelected;

        if (isMultiSelectAction)
        {
            BuildMultiSelectContextMenu(menuFlyout, viewModel);
        }
        else
        {
            BuildSingleSelectContextMenu(menuFlyout, viewModel);
        }

        menuFlyout.ShowAt(this, e.GetPosition(this));
        e.Handled = true;
    }

    private void BuildSingleSelectContextMenu(MenuFlyout menuFlyout, MainViewModel viewModel)
    {
        var editItem = new MenuFlyoutItem { Text = "Edit", Icon = new FontIcon { Glyph = "\uE70F" } };
        editItem.Click += (s, a) => EditRequested?.Invoke(this, a);
        menuFlyout.Items.Add(editItem);

        var copyItem = new MenuFlyoutItem { Text = "Copy", Icon = new FontIcon { Glyph = "\uE8C8" } };
        copyItem.Click += CopyMenuItem_Click;
        menuFlyout.Items.Add(copyItem);

        var pasteItem = new MenuFlyoutItem { Text = "Paste", Icon = new FontIcon { Glyph = "\uE77F" }, IsEnabled = viewModel.CanPasteSource() };
        pasteItem.Click += PasteMenuItem_Click;
        menuFlyout.Items.Add(pasteItem);

        menuFlyout.Items.Add(new MenuFlyoutSeparator());

        // Layer submenu
        var layerSubMenu = new MenuFlyoutSubItem { Text = "Layer", Icon = new FontIcon { Glyph = "\uE81E" } };
        BuildLayerMenuItems(layerSubMenu, viewModel);
        menuFlyout.Items.Add(layerSubMenu);

        menuFlyout.Items.Add(new MenuFlyoutSeparator());

        var flipHorizontalItem = new MenuFlyoutItem { Text = "Flip Horizontally" };
        var flipVerticalItem = new MenuFlyoutItem { Text = "Flip Vertically" };

        // Set icons based on theme
        var theme = (this.XamlRoot.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Light;
        var iconName = theme == ElementTheme.Dark ? "Flip-dark.svg" : "Flip-white.svg";
        var iconUri = new Uri($"ms-appx:///Assets/{iconName}");

        var verticalIcon = new ImageIcon { Source = new SvgImageSource(iconUri), Width = 16, Height = 16 };
        flipVerticalItem.Icon = verticalIcon;

        var horizontalIcon = new ImageIcon { Source = new SvgImageSource(iconUri), Width = 16, Height = 16 };
        horizontalIcon.RenderTransform = new RotateTransform { Angle = 90, CenterX = 8, CenterY = 8 };
        flipHorizontalItem.Icon = horizontalIcon;

        flipHorizontalItem.Click += (s, a) => viewModel.ToggleFlipHorizontalCommand.Execute(null);
        flipVerticalItem.Click += (s, a) => viewModel.ToggleFlipVerticalCommand.Execute(null);
        menuFlyout.Items.Add(flipHorizontalItem);
        menuFlyout.Items.Add(flipVerticalItem);

        menuFlyout.Items.Add(new MenuFlyoutSeparator());

        var centerItem = new MenuFlyoutItem { Text = "Center", Icon = new FontIcon { Glyph = "\uF58A" } };
        centerItem.Click += CenterMenuItem_Click;
        menuFlyout.Items.Add(centerItem);

        menuFlyout.Items.Add(new MenuFlyoutSeparator());
        
        var cropItem = new MenuFlyoutItem { Text = "Crop", Icon = new FontIcon { Glyph = "\uE7A8" } };
        cropItem.Click += (s, a) => EnterCropMode();
        menuFlyout.Items.Add(cropItem);

        menuFlyout.Items.Add(new MenuFlyoutSeparator());

        var deleteItem = new MenuFlyoutItem { Text = "Delete", Icon = new FontIcon { Glyph = "\uE74D" } };
        deleteItem.Click += DeleteMenuItem_Click;
        menuFlyout.Items.Add(deleteItem);
    }

    private void BuildMultiSelectContextMenu(MenuFlyout menuFlyout, MainViewModel viewModel)
    {
        var copyItem = new MenuFlyoutItem { Text = "Copy", Icon = new FontIcon { Glyph = "\uE8C8" } };
        copyItem.Click += CopyMenuItem_Click;
        menuFlyout.Items.Add(copyItem);

        var pasteItem = new MenuFlyoutItem { Text = "Paste", Icon = new FontIcon { Glyph = "\uE77F" }, IsEnabled = viewModel.CanPasteSource() };
        pasteItem.Click += PasteMenuItem_Click;
        menuFlyout.Items.Add(pasteItem);

        menuFlyout.Items.Add(new MenuFlyoutSeparator());

        var alignSubMenu = new MenuFlyoutSubItem { Text = "Align", Icon = new FontIcon { Glyph = "\uE139" } };
        BuildAlignMenuItems(alignSubMenu, viewModel);
        menuFlyout.Items.Add(alignSubMenu);

        var centerItem = new MenuFlyoutItem { Text = "Center", Icon = new FontIcon { Glyph = "\uF58A" } };
        centerItem.Click += CenterMenuItem_Click;
        menuFlyout.Items.Add(centerItem);

        menuFlyout.Items.Add(new MenuFlyoutSeparator());
        
        var layerSubMenu = new MenuFlyoutSubItem { Text = "Layer", Icon = new FontIcon { Glyph = "\uE81E" } };
        BuildLayerMenuItems(layerSubMenu, viewModel);
        menuFlyout.Items.Add(layerSubMenu);

        menuFlyout.Items.Add(new MenuFlyoutSeparator());

        var flipHorizontalItem = new MenuFlyoutItem { Text = "Flip Horizontally" };
        var flipVerticalItem = new MenuFlyoutItem { Text = "Flip Vertically" };
        
        // Set icons based on theme
        var theme = (this.XamlRoot.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Light;
        var iconName = theme == ElementTheme.Dark ? "Flip-dark.svg" : "Flip-white.svg";
        var iconUri = new Uri($"ms-appx:///Assets/{iconName}");

        var verticalIcon = new ImageIcon { Source = new SvgImageSource(iconUri), Width = 16, Height = 16 };
        flipVerticalItem.Icon = verticalIcon;

        var horizontalIcon = new ImageIcon { Source = new SvgImageSource(iconUri), Width = 16, Height = 16 };
        horizontalIcon.RenderTransform = new RotateTransform { Angle = 90, CenterX = 8, CenterY = 8 };
        flipHorizontalItem.Icon = horizontalIcon;

        flipHorizontalItem.Click += (s, a) => viewModel.ToggleFlipHorizontalCommand.Execute(null);
        flipVerticalItem.Click += (s, a) => viewModel.ToggleFlipVerticalCommand.Execute(null);
        menuFlyout.Items.Add(flipHorizontalItem);
        menuFlyout.Items.Add(flipVerticalItem);

        menuFlyout.Items.Add(new MenuFlyoutSeparator());

        var deleteItem = new MenuFlyoutItem { Text = $"Delete {viewModel.SelectedSources.Count} items", Icon = new FontIcon { Glyph = "\uE74D" } };
        deleteItem.Click += DeleteMenuItem_Click;
        menuFlyout.Items.Add(deleteItem);
    }
    
    private void BuildLayerMenuItems(MenuFlyoutSubItem layerSubMenu, MainViewModel viewModel)
    {
        var bringToFrontItem = new MenuFlyoutItem { Text = "Bring to Front", Icon = new FontIcon { Glyph = "\uE746" } };
        bringToFrontItem.Click += async (s, a) =>
        {
            if (viewModel == null) return;
            var sourcesToMove = viewModel.SelectedSources.ToList();
            if (!sourcesToMove.Any()) return;
            foreach (var source in sourcesToMove.OrderByDescending(src => viewModel.Sources.IndexOf(src)))
            {
                if (viewModel.Sources.Remove(source))
                    viewModel.Sources.Insert(0, source);
            }
            await viewModel.SaveSourcesAsync();
        };
        layerSubMenu.Items.Add(bringToFrontItem);

        var bringForwardItem = new MenuFlyoutItem { Text = "Bring Forward", Icon = new FontIcon { Glyph = "\uE760" } };
        bringForwardItem.Click += async (s, a) =>
        {
            if (viewModel == null) return;
            var sourcesToMove = viewModel.SelectedSources.ToList();
            if (!sourcesToMove.Any()) return;
            foreach (var source in sourcesToMove.OrderBy(src => viewModel.Sources.IndexOf(src)))
            {
                var index = viewModel.Sources.IndexOf(source);
                if (index > 0)
                {
                    viewModel.Sources.Remove(source);
                    viewModel.Sources.Insert(index - 1, source);
                }
            }
            await viewModel.SaveSourcesAsync();
        };
        layerSubMenu.Items.Add(bringForwardItem);

        layerSubMenu.Items.Add(new MenuFlyoutSeparator());

        var sendBackwardItem = new MenuFlyoutItem { Text = "Send Backward", Icon = new FontIcon { Glyph = "\uE761" } };
        sendBackwardItem.Click += async (s, a) =>
        {
            if (viewModel == null) return;
            var sourcesToMove = viewModel.SelectedSources.ToList();
            if (!sourcesToMove.Any()) return;
            foreach (var source in sourcesToMove.OrderByDescending(src => viewModel.Sources.IndexOf(src)))
            {
                var index = viewModel.Sources.IndexOf(source);
                if (index < viewModel.Sources.Count - 1)
                {
                    viewModel.Sources.Remove(source);
                    viewModel.Sources.Insert(index + 1, source);
                }
            }
            await viewModel.SaveSourcesAsync();
        };
        layerSubMenu.Items.Add(sendBackwardItem);

        var sendToBackItem = new MenuFlyoutItem { Text = "Send to Back", Icon = new FontIcon { Glyph = "\uE747" } };
        sendToBackItem.Click += async (s, a) =>
        {
            if (viewModel == null) return;
            var sourcesToMove = viewModel.SelectedSources.ToList();
            if (!sourcesToMove.Any()) return;
            foreach (var source in sourcesToMove.OrderBy(src => viewModel.Sources.IndexOf(src)))
            {
                if (viewModel.Sources.Remove(source))
                    viewModel.Sources.Add(source);
            }
            await viewModel.SaveSourcesAsync();
        };
        layerSubMenu.Items.Add(sendToBackItem);
    }

    private void BuildAlignMenuItems(MenuFlyoutSubItem alignSubMenu, MainViewModel viewModel)
    {
        var alignLeft = new MenuFlyoutItem { Text = "Align Left", Command = viewModel.AlignLeftCommand, Icon = new FontIcon { Glyph = "\uE8E4" } };
        var alignCenter = new MenuFlyoutItem { Text = "Align Center", Command = viewModel.AlignCenterCommand, Icon = new FontIcon { Glyph = "\uE8E3" } };
        var alignRight = new MenuFlyoutItem { Text = "Align Right", Command = viewModel.AlignRightCommand, Icon = new FontIcon { Glyph = "\uE8E2" } };
        alignSubMenu.Items.Add(alignLeft);
        alignSubMenu.Items.Add(alignCenter);
        alignSubMenu.Items.Add(alignRight);

        alignSubMenu.Items.Add(new MenuFlyoutSeparator());

        var alignTopIcon = new FontIcon { Glyph = "\uE8E2", RenderTransformOrigin = new Point(0.5, 0.5) };
        alignTopIcon.RenderTransform = new RotateTransform { Angle = -90 };
        var alignTop = new MenuFlyoutItem { Text = "Align Top", Command = viewModel.AlignTopCommand, Icon = alignTopIcon };

        var alignMiddleIcon = new FontIcon { Glyph = "\uE8E3", RenderTransformOrigin = new Point(0.5, 0.5) };
        alignMiddleIcon.RenderTransform = new RotateTransform { Angle = -90 };
        var alignMiddle = new MenuFlyoutItem { Text = "Align Middle", Command = viewModel.AlignMiddleCommand, Icon = alignMiddleIcon };

        var alignBottomIcon = new FontIcon { Glyph = "\uE8E4", RenderTransformOrigin = new Point(0.5, 0.5) };
        alignBottomIcon.RenderTransform = new RotateTransform { Angle = -90 };
        var alignBottom = new MenuFlyoutItem { Text = "Align Bottom", Command = viewModel.AlignBottomCommand, Icon = alignBottomIcon };
        
        alignSubMenu.Items.Add(alignTop);
        alignSubMenu.Items.Add(alignMiddle);
        alignSubMenu.Items.Add(alignBottom);
    }

    private void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        DeleteRequested?.Invoke(this, e);
    }

    private void CopyMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CopyRequested?.Invoke(this, e);
    }

    private void PasteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        PasteRequested?.Invoke(this, e);
    }

    private void CenterMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CenterRequested?.Invoke(this, e);
    }

    private void EnterCropMode()
    {
        _isCropping = true;
        CropCanvas.Visibility = Visibility.Visible;
        SelectionBorder.Visibility = Visibility.Collapsed; // Hide selection visuals
        RotationHandleCanvas.Visibility = Visibility.Collapsed;

        // The crop shade path will be made visible by setting its Data property in UpdateCropVisuals

        // Convert percentage to pixels for crop editing
        double left = ActualWidth * (Source?.CropLeftPct ?? 0);
        double top = ActualHeight * (Source?.CropTopPct ?? 0);
        double right = ActualWidth * (Source?.CropRightPct ?? 0);
        double bottom = ActualHeight * (Source?.CropBottomPct ?? 0);
        
        _cropStartRect = new Rect(left, top,
            Math.Max(0, ActualWidth - left - right),
            Math.Max(0, ActualHeight - top - bottom)
        );

        UpdateCropVisuals(_cropStartRect, Source?.CropRotation ?? 0);
    }

    private void ExitCropMode()
    {
        _isCropping = false;
        CropCanvas.Visibility = Visibility.Collapsed;
        CropShadePath.Data = null; // Hide the cropping overlay

        // Update to show the final, persisted crop shading
        UpdateCropShading();

        // Restore visibility so the VSM can handle opacity
        SelectionBorder.Visibility = Visibility.Visible;
        RotationHandleCanvas.Visibility = Visibility.Visible;

        _cropResizeMode = ResizeMode.None;
        
        // Give focus back to the page so global shortcuts fire again
        var mainPage = FindParent<MainPage>(this);
        mainPage?.Focus(FocusState.Programmatic);
    }

    private void AcceptCropButton_Click(object sender, RoutedEventArgs e)
    {
        if (Source == null)
        {
            ExitCropMode();
            return;
        }

        // Clamp crop values in case the rectangle extended beyond the control's bounds during editing.
        // This ensures the percentages are always valid [0, 1].
        Source.CropLeftPct = Math.Clamp(Source.CropLeftPct, 0, 1);
        Source.CropTopPct = Math.Clamp(Source.CropTopPct, 0, 1);
        Source.CropRightPct = Math.Clamp(Source.CropRightPct, 0, 1);
        Source.CropBottomPct = Math.Clamp(Source.CropBottomPct, 0, 1);
        
        var parentCanvas = FindParent<Canvas>(this);
        if (parentCanvas != null)
        {
            // Calculate the AABB of the *visible* (cropped) area.
            var visibleAabb = MainViewModel.GetVisibleAreaAabb(Source);

            if (!visibleAabb.IsEmpty)
            {
                // Determine if there's an overflow and calculate the adjustment needed.
                double dx = 0;
                double dy = 0;

                if (visibleAabb.Left < 0)
                {
                    dx = -visibleAabb.Left;
                }
                else if (visibleAabb.Right > parentCanvas.ActualWidth)
                {
                    dx = parentCanvas.ActualWidth - visibleAabb.Right;
                }

                if (visibleAabb.Top < 0)
                {
                    dy = -visibleAabb.Top;
                }
                else if (visibleAabb.Bottom > parentCanvas.ActualHeight)
                {
                    dy = parentCanvas.ActualHeight - visibleAabb.Bottom;
                }

                // Apply adjustment to the source's main position if needed.
                if (dx != 0 || dy != 0)
                {
                    Source.CanvasX += (int)Math.Round(dx);
                    Source.CanvasY += (int)Math.Round(dy);
                }
            }
        }
        
        // Source properties are accepted. Save the final state.
        var viewModel = App.GetService<MainViewModel>();
        if (viewModel != null)
        {
            viewModel.SaveUndoState();
            viewModel.SaveSourcesCommand.Execute(null);
        }

        ExitCropMode();
    }

    private void CancelCropButton_Click(object sender, RoutedEventArgs e)
    {
        CancelCrop();
    }

    private void CancelCrop()
    {
        // Restore original crop values since we've been updating them live
        if (Source != null)
        {
            Source.CropLeftPct = _cropStartRect.X / ActualWidth;
            Source.CropTopPct = _cropStartRect.Y / ActualHeight;
            Source.CropRightPct = (ActualWidth - _cropStartRect.Right) / ActualWidth;
            Source.CropBottomPct = (ActualHeight - _cropStartRect.Bottom) / ActualHeight;
            Source.CropRotation = (int)Math.Round(_cropActionStartRotation);
        }
        ExitCropMode();
    }

    private void CropCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        var currentPoint = e.GetCurrentPoint(CropCanvas);
        _cropActionStartPointerPosition = currentPoint.Position;
        _cropActionStartBounds = new Rect(Canvas.GetLeft(CropRect), Canvas.GetTop(CropRect), CropRect.Width, CropRect.Height);
        
        var transform = CropRect.RenderTransform as RotateTransform;
        _cropActionStartRotation = transform?.Angle ?? 0;
        
        _cropResizeMode = GetCropResizeMode(currentPoint.Position);

        if (_cropResizeMode != ResizeMode.None)
        {
            CropCanvas.CapturePointer(e.Pointer);
        }
        else
        {
            // Click outside the crop rectangle cancels the edit
            CancelCrop();
        }
    }

    private void CropCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_cropResizeMode == ResizeMode.None)
        {
            var cursor = GetCursor(GetCropResizeMode(e.GetCurrentPoint(CropCanvas).Position));
            this.ProtectedCursor = cursor;
            return;
        }

        var currentPoint = e.GetCurrentPoint(CropCanvas).Position;
        var newBounds = _cropActionStartBounds;

        var deltaX = currentPoint.X - _cropActionStartPointerPosition.X;
        var deltaY = currentPoint.Y - _cropActionStartPointerPosition.Y;
        
        // Check if Shift key is pressed (used by multiple cases)
        var isShiftDown = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift);

        switch (_cropResizeMode)
        {
            case ResizeMode.CropRotate:
                var centerPoint = new Point(_cropActionStartBounds.X + _cropActionStartBounds.Width / 2, _cropActionStartBounds.Y + _cropActionStartBounds.Height / 2);
                var startVector = new Point(_cropActionStartPointerPosition.X - centerPoint.X, _cropActionStartPointerPosition.Y - centerPoint.Y);
                var currentVector = new Point(currentPoint.X - centerPoint.X, currentPoint.Y - centerPoint.Y);

                var startAngle = Math.Atan2(startVector.Y, startVector.X) * (180.0 / Math.PI);
                var currentAngle = Math.Atan2(currentVector.Y, currentVector.X) * (180.0 / Math.PI);

                var angleDelta = currentAngle - startAngle;

                // Normalize the delta to handle the wrap-around from +180 to -180
                if (angleDelta > 180)
                {
                    angleDelta -= 360;
                }
                if (angleDelta < -180)
                {
                    angleDelta += 360;
                }

                var newAngle = _cropActionStartRotation + angleDelta;

                if (isShiftDown)
                {
                    newAngle = Math.Round(newAngle / 15.0) * 15.0; // Snap to 15-degree increments
                }

                UpdateCropVisuals(_cropActionStartBounds, newAngle);
                return;
            case ResizeMode.Left:
            case ResizeMode.Right:
            case ResizeMode.Top:
            case ResizeMode.Bottom:
            case ResizeMode.TopLeft:
            case ResizeMode.TopRight:
            case ResizeMode.BottomLeft:
            case ResizeMode.BottomRight:
                {
                    // Constrain the deltas to prevent going out of bounds
                    var constrainedDeltas = ConstrainResizeDeltas(_cropActionStartBounds, _cropResizeMode, deltaX, deltaY, _cropActionStartRotation);
                    
                    newBounds = ApplyCropResize(
                        _cropActionStartBounds,
                        _cropResizeMode,
                        constrainedDeltas.X,    // constrained delta
                        constrainedDeltas.Y,    // constrained delta
                        _cropActionStartRotation,
                        10,               // minSize
                        isShiftDown);     // preserveAspectRatio
                    break;
                }
            case ResizeMode.Move:
                {
                    // 1. translate in *screen* space
                    double newX = _cropActionStartBounds.X + deltaX;
                    double newY = _cropActionStartBounds.Y + deltaY;

                    // 2. Clamp the movement to keep the crop rectangle inside the control
                    var tempBounds = new Rect(newX, newY, _cropActionStartBounds.Width, _cropActionStartBounds.Height);
                    tempBounds = ClampCropPositionToBounds(tempBounds, _cropActionStartRotation);

                    newBounds.X = tempBounds.X;
                    newBounds.Y = tempBounds.Y;
                }
                break;
        }

        UpdateCropVisuals(newBounds, _cropActionStartRotation);
    }

    private Point ToLocal(double x, double y, double angleDegrees)
    {
        double angleRadians = angleDegrees * Math.PI / 180.0;
        double cos = Math.Cos(angleRadians);
        double sin = Math.Sin(angleRadians);
        return new Point(x * cos + y * sin, -x * sin + y * cos);
    }

    private void CropCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        _cropResizeMode = ResizeMode.None;
        CropCanvas.ReleasePointerCapture(e.Pointer);
        this.ProtectedCursor = null;
    }

    /// <summary>
    /// Resize the crop rectangle while it is rotated, copying the maths used
    /// for the main source-item resize.
    /// </summary>
    private Rect ApplyCropResize(
        Rect startBounds,
        ResizeMode mode,
        double dxScreen,
        double dyScreen,
        double rotationDeg,
        double minSize = 10,
        bool preserveAspectRatio = false)
    {
        // ---- 0. helpers ----
        double θ = rotationDeg * Math.PI / 180.0;
        double c = Math.Cos(θ);
        double s = Math.Sin(θ);
        Point ToLocal(double x, double y) => new(x * c + y * s, -x * s + y * c);
        Point ToScreen(double x, double y) => new(x * c - y * s, x * s + y * c);

        Point δL = ToLocal(dxScreen, dyScreen);

        // ---- 1. which edges move? ----
        bool mL = mode is ResizeMode.Left or ResizeMode.TopLeft or ResizeMode.BottomLeft;
        bool mR = mode is ResizeMode.Right or ResizeMode.TopRight or ResizeMode.BottomRight;
        bool mT = mode is ResizeMode.Top or ResizeMode.TopLeft or ResizeMode.TopRight;
        bool mB = mode is ResizeMode.Bottom or ResizeMode.BottomLeft or ResizeMode.BottomRight;

        double w0 = startBounds.Width;
        double h0 = startBounds.Height;

        // ---- 2. new size in LOCAL space ----
        double w1 = w0 + (mR ? δL.X : 0) - (mL ? δL.X : 0);
        double h1 = h0 + (mB ? δL.Y : 0) - (mT ? δL.Y : 0);
        
        // ---- 2a. Preserve aspect ratio if Shift is held ----
        if (preserveAspectRatio && w0 > 0 && h0 > 0)
        {
            double aspectRatio = w0 / h0;
            bool isCornerResize = (mL || mR) && (mT || mB); // corner resize modes
            
            if (isCornerResize)
            {
                // For corner resize, use the dominant direction to drive both dimensions
                if (Math.Abs(δL.X) >= Math.Abs(δL.Y))
                {
                    // Width-driven resize
                    h1 = w1 / aspectRatio;
                }
                else
                {
                    // Height-driven resize
                    w1 = h1 * aspectRatio;
                }
            }
            else if (mL || mR)
            {
                // Pure horizontal resize - adjust height to maintain aspect ratio
                h1 = w1 / aspectRatio;
            }
            else if (mT || mB)
            {
                // Pure vertical resize - adjust width to maintain aspect ratio
                w1 = h1 * aspectRatio;
            }
        }
        
        w1 = Math.Max(minSize, w1);
        h1 = Math.Max(minSize, h1);

        // ---- 3. anchor stays put ----
        int ax = mL ? +1 : mR ? -1 : 0;   // +1 = right edge is anchor etc.
        int ay = mT ? +1 : mB ? -1 : 0;

        Point anchorL0 = new(ax * w0 / 2, ay * h0 / 2);
        Point anchorL1 = new(ax * w1 / 2, ay * h1 / 2);

        Point centre0 = new(startBounds.X + w0 / 2,
                            startBounds.Y + h0 / 2);
        Point anchorW = new(
            centre0.X + ToScreen(anchorL0.X, anchorL0.Y).X,
            centre0.Y + ToScreen(anchorL0.X, anchorL0.Y).Y);

        Point centre1 = new(
            anchorW.X - ToScreen(anchorL1.X, anchorL1.Y).X,
            anchorW.Y - ToScreen(anchorL1.X, anchorL1.Y).Y);

        double x1 = centre1.X - w1 / 2;
        double y1 = centre1.Y - h1 / 2;

        return new Rect(x1, y1, w1, h1);
    }

    private Point ConstrainResizeDeltas(Rect startBounds, ResizeMode mode, double deltaX, double deltaY, double rotationDegrees)
    {
        // For unrotated rectangles, use simple edge-based constraints
        if (Math.Abs(rotationDegrees) < 0.001)
        {
            double constrainedDeltaX = deltaX;
            double constrainedDeltaY = deltaY;

            // Constrain based on which edges are moving and the control bounds
            switch (mode)
            {
                case ResizeMode.Left:
                case ResizeMode.TopLeft:
                case ResizeMode.BottomLeft:
                    // Left edge moving - constrain to not go past left boundary
                    constrainedDeltaX = Math.Max(deltaX, -startBounds.X);
                    break;
                    
                case ResizeMode.Right:
                case ResizeMode.TopRight:
                case ResizeMode.BottomRight:
                    // Right edge moving - constrain to not go past right boundary
                    constrainedDeltaX = Math.Min(deltaX, ActualWidth - startBounds.Right);
                    break;
            }

            switch (mode)
            {
                case ResizeMode.Top:
                case ResizeMode.TopLeft:
                case ResizeMode.TopRight:
                    // Top edge moving - constrain to not go past top boundary
                    constrainedDeltaY = Math.Max(deltaY, -startBounds.Y);
                    break;
                    
                case ResizeMode.Bottom:
                case ResizeMode.BottomLeft:
                case ResizeMode.BottomRight:
                    // Bottom edge moving - constrain to not go past bottom boundary
                    constrainedDeltaY = Math.Min(deltaY, ActualHeight - startBounds.Bottom);
                    break;
            }

            return new Point(constrainedDeltaX, constrainedDeltaY);
        }

        // For rotated rectangles, we need to check if the resulting AABB would fit
        // This is more complex, so we'll use a binary search approach to find the maximum allowed delta
        var testDeltaX = deltaX;
        var testDeltaY = deltaY;
        
        // Test if the current deltas work
        var testBounds = ApplyCropResize(startBounds, mode, testDeltaX, testDeltaY, rotationDegrees, 10, false);
        var testCenter = new Point(testBounds.X + testBounds.Width / 2, testBounds.Y + testBounds.Height / 2);
        var testAabb = GetRotatedAabb(testCenter, new Size(testBounds.Width, testBounds.Height), rotationDegrees);
        
        if (testAabb.Left >= 0 && testAabb.Top >= 0 && testAabb.Right <= ActualWidth && testAabb.Bottom <= ActualHeight)
        {
            return new Point(testDeltaX, testDeltaY); // Original deltas work fine
        }

        // Binary search to find the maximum allowed deltas
        double minScale = 0.0;
        double maxScale = 1.0;
        double workingScale = 0.0;

        for (int i = 0; i < 10; i++) // 10 iterations should be enough for reasonable precision
        {
            double testScale = (minScale + maxScale) / 2;
            testDeltaX = deltaX * testScale;
            testDeltaY = deltaY * testScale;

            testBounds = ApplyCropResize(startBounds, mode, testDeltaX, testDeltaY, rotationDegrees, 10, false);
            testCenter = new Point(testBounds.X + testBounds.Width / 2, testBounds.Y + testBounds.Height / 2);
            testAabb = GetRotatedAabb(testCenter, new Size(testBounds.Width, testBounds.Height), rotationDegrees);

            if (testAabb.Left >= 0 && testAabb.Top >= 0 && testAabb.Right <= ActualWidth && testAabb.Bottom <= ActualHeight)
            {
                workingScale = testScale;
                minScale = testScale;
            }
            else
            {
                maxScale = testScale;
            }
        }

        return new Point(deltaX * workingScale, deltaY * workingScale);
    }

    private Rect ClampCropPositionToBounds(Rect rect, double rotationDegrees)
    {
        // For rotation = 0, simple clamping
        if (Math.Abs(rotationDegrees) < 0.001)
        {
            double clampedX = Math.Clamp(rect.X, 0, Math.Max(0, ActualWidth - rect.Width));
            double clampedY = Math.Clamp(rect.Y, 0, Math.Max(0, ActualHeight - rect.Height));
            double clampedWidth = Math.Clamp(rect.Width, 10, ActualWidth - clampedX);
            double clampedHeight = Math.Clamp(rect.Height, 10, ActualHeight - clampedY);
            
            return new Rect(clampedX, clampedY, clampedWidth, clampedHeight);
        }

        // For rotated rectangles, calculate the AABB and ensure it fits within bounds
        var center = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        var aabb = GetRotatedAabb(center, new Size(rect.Width, rect.Height), rotationDegrees);
        
        // Check if the AABB is within the control bounds
        var controlBounds = new Rect(0, 0, ActualWidth, ActualHeight);
        var intersection = IntersectRects(aabb, controlBounds);
        
        // If there's no intersection, the rectangle is completely outside - return the original
        if (intersection.IsEmpty)
        {
            return _cropActionStartBounds; // Fallback to original bounds
        }
        
        // If the AABB fits entirely within bounds, return the original rectangle
        if (aabb.Left >= 0 && aabb.Top >= 0 && aabb.Right <= ActualWidth && aabb.Bottom <= ActualHeight)
        {
            return rect;
        }
        
        // Calculate adjustment needed to bring the AABB within bounds
        double deltaX = 0, deltaY = 0;
        
        if (aabb.Left < 0) deltaX = -aabb.Left;
        else if (aabb.Right > ActualWidth) deltaX = ActualWidth - aabb.Right;
        
        if (aabb.Top < 0) deltaY = -aabb.Top;
        else if (aabb.Bottom > ActualHeight) deltaY = ActualHeight - aabb.Bottom;
        
        // Apply the adjustment to the rectangle position
        return new Rect(rect.X + deltaX, rect.Y + deltaY, rect.Width, rect.Height);
    }

    private void UpdateCropVisuals(Rect cropRect, double rotationAngle = 0)
    {
        // Update selection rectangle
        Canvas.SetLeft(CropRect, cropRect.X);
        Canvas.SetTop(CropRect, cropRect.Y);
        CropRect.Width = cropRect.Width;
        CropRect.Height = cropRect.Height;

        // Apply rotation transform to the crop rectangle
        if (CropRect.RenderTransform is not RotateTransform transform)
        {
            transform = new RotateTransform();
            CropRect.RenderTransform = transform;
        }
        
        transform.Angle = rotationAngle;
        transform.CenterX = cropRect.Width / 2;
        transform.CenterY = cropRect.Height / 2;

        // Helper function to rotate a point around the crop rectangle center
        Point RotatePoint(double x, double y)
        {
            if (Math.Abs(rotationAngle) < 0.001) return new Point(x, y);
            
            var centerX = cropRect.X + cropRect.Width / 2;
            var centerY = cropRect.Y + cropRect.Height / 2;
            var angleRad = rotationAngle * Math.PI / 180.0;
            var cos = Math.Cos(angleRad);
            var sin = Math.Sin(angleRad);
            
            var translatedX = x - centerX;
            var translatedY = y - centerY;
            
            var rotatedX = translatedX * cos - translatedY * sin;
            var rotatedY = translatedX * sin + translatedY * cos;
            
            return new Point(rotatedX + centerX, rotatedY + centerY);
        }

        // Update the crop shade rectangles in real-time during editing
        // Convert crop rect to percentages and temporarily update Source properties for shading calculation
        var tempCropLeftPct = cropRect.X / ActualWidth;
        var tempCropTopPct = cropRect.Y / ActualHeight;
        var tempCropRightPct = (ActualWidth - cropRect.Right) / ActualWidth;
        var tempCropBottomPct = (ActualHeight - cropRect.Bottom) / ActualHeight;
        var tempCropRotation = rotationAngle;

        // Apply shading using the temporary crop values
        UpdateCropShadingWithValues(tempCropLeftPct, tempCropTopPct, tempCropRightPct, tempCropBottomPct, tempCropRotation);
        
        // Update Source properties for live panel sync (only during active cropping)
        if (_isCropping && Source != null)
        {
            Source.CropLeftPct = tempCropLeftPct;
            Source.CropTopPct = tempCropTopPct;
            Source.CropRightPct = tempCropRightPct;
            Source.CropBottomPct = tempCropBottomPct;
            Source.CropRotation = (int)Math.Round(tempCropRotation);
        }

        // Update handles with rotation
        const double handleOffset = 5; // half of handle size
        
        var tlPoint = RotatePoint(cropRect.Left, cropRect.Top);
        Canvas.SetLeft(CropHandleTL, tlPoint.X - handleOffset);
        Canvas.SetTop(CropHandleTL, tlPoint.Y - handleOffset);

        var tPoint = RotatePoint(cropRect.Left + cropRect.Width / 2, cropRect.Top);
        Canvas.SetLeft(CropHandleT, tPoint.X - handleOffset);
        Canvas.SetTop(CropHandleT, tPoint.Y - handleOffset);

        var trPoint = RotatePoint(cropRect.Right, cropRect.Top);
        Canvas.SetLeft(CropHandleTR, trPoint.X - handleOffset);
        Canvas.SetTop(CropHandleTR, trPoint.Y - handleOffset);
        
        var lPoint = RotatePoint(cropRect.Left, cropRect.Top + cropRect.Height / 2);
        Canvas.SetLeft(CropHandleL, lPoint.X - handleOffset);
        Canvas.SetTop(CropHandleL, lPoint.Y - handleOffset);

        var rPoint = RotatePoint(cropRect.Right, cropRect.Top + cropRect.Height / 2);
        Canvas.SetLeft(CropHandleR, rPoint.X - handleOffset);
        Canvas.SetTop(CropHandleR, rPoint.Y - handleOffset);

        var blPoint = RotatePoint(cropRect.Left, cropRect.Bottom);
        Canvas.SetLeft(CropHandleBL, blPoint.X - handleOffset);
        Canvas.SetTop(CropHandleBL, blPoint.Y - handleOffset);

        var bPoint = RotatePoint(cropRect.Left + cropRect.Width / 2, cropRect.Bottom);
        Canvas.SetLeft(CropHandleB, bPoint.X - handleOffset);
        Canvas.SetTop(CropHandleB, bPoint.Y - handleOffset);

        var brPoint = RotatePoint(cropRect.Right, cropRect.Bottom);
        Canvas.SetLeft(CropHandleBR, brPoint.X - handleOffset);
        Canvas.SetTop(CropHandleBR, brPoint.Y - handleOffset);

        // Update crop rotation handle
        var rotationHandlePoint = RotatePoint(cropRect.X + cropRect.Width / 2, cropRect.Y - 30);
        Canvas.SetLeft(CropRotationHandle, rotationHandlePoint.X - (CropRotationHandle.Width / 2));
        Canvas.SetTop(CropRotationHandle, rotationHandlePoint.Y - (CropRotationHandle.Height / 2));

        var lineTopPoint = RotatePoint(cropRect.X + cropRect.Width / 2, cropRect.Y);
        CropRotationHandleLine.X1 = rotationHandlePoint.X;
        CropRotationHandleLine.Y1 = rotationHandlePoint.Y + 8; // Start from handle edge
        CropRotationHandleLine.X2 = lineTopPoint.X;
        CropRotationHandleLine.Y2 = lineTopPoint.Y; // End at top of crop rect

        // Update buttons (position them away from rotation handle)
        var buttonX = cropRect.Left + cropRect.Width / 2 - CropActionsPanel.ActualWidth / 2;
        var buttonY = Math.Min(cropRect.Top - CropActionsPanel.ActualHeight - 5, rotationHandlePoint.Y - 50); // Avoid overlap with rotation handle
        Canvas.SetLeft(CropActionsPanel, buttonX);
        Canvas.SetTop(CropActionsPanel, buttonY);
    }
    
    private ResizeMode GetCropResizeMode(Point point)
    {
        // For cropping we want the actual rotated coordinates
        return GetCropResizeModeCore(point);
    }

    private ResizeMode GetCropResizeModeCore(Point point)
    {
        const int handleSize = 10;
        var cropRect = new Rect(Canvas.GetLeft(CropRect), Canvas.GetTop(CropRect), CropRect.Width, CropRect.Height);
        
        // Get current rotation angle
        var transform = CropRect.RenderTransform as RotateTransform;
        var rotationAngle = transform?.Angle ?? 0;

        // Helper function to rotate a point around the crop rectangle center
        Point RotatePoint(double x, double y)
        {
            if (Math.Abs(rotationAngle) < 0.001) return new Point(x, y);
            
            var centerX = cropRect.X + cropRect.Width / 2;
            var centerY = cropRect.Y + cropRect.Height / 2;
            var angleRad = rotationAngle * Math.PI / 180.0;
            var cos = Math.Cos(angleRad);
            var sin = Math.Sin(angleRad);
            
            var translatedX = x - centerX;
            var translatedY = y - centerY;
            
            var rotatedX = translatedX * cos - translatedY * sin;
            var rotatedY = translatedX * sin + translatedY * cos;
            
            return new Point(rotatedX + centerX, rotatedY + centerY);
        }

        // Check rotation handle first
        var rotationHandlePoint = RotatePoint(cropRect.X + cropRect.Width / 2, cropRect.Y - 30);
        var rotationHandleRect = new Rect(rotationHandlePoint.X - 8, rotationHandlePoint.Y - 8, 16, 16);
        if (rotationHandleRect.Contains(point))
        {
            return ResizeMode.CropRotate;
        }

        // Check resize handles with rotation
        var tlPoint = RotatePoint(cropRect.Left, cropRect.Top);
        var tPoint = RotatePoint(cropRect.Left + cropRect.Width / 2, cropRect.Top);
        var trPoint = RotatePoint(cropRect.Right, cropRect.Top);
        var lPoint = RotatePoint(cropRect.Left, cropRect.Top + cropRect.Height / 2);
        var rPoint = RotatePoint(cropRect.Right, cropRect.Top + cropRect.Height / 2);
        var blPoint = RotatePoint(cropRect.Left, cropRect.Bottom);
        var bPoint = RotatePoint(cropRect.Left + cropRect.Width / 2, cropRect.Bottom);
        var brPoint = RotatePoint(cropRect.Right, cropRect.Bottom);

        var tl = new Rect(tlPoint.X - handleSize / 2, tlPoint.Y - handleSize / 2, handleSize, handleSize);
        var t = new Rect(tPoint.X - handleSize / 2, tPoint.Y - handleSize / 2, handleSize, handleSize);
        var tr = new Rect(trPoint.X - handleSize / 2, trPoint.Y - handleSize / 2, handleSize, handleSize);
        var l = new Rect(lPoint.X - handleSize / 2, lPoint.Y - handleSize / 2, handleSize, handleSize);
        var r = new Rect(rPoint.X - handleSize / 2, rPoint.Y - handleSize / 2, handleSize, handleSize);
        var bl = new Rect(blPoint.X - handleSize / 2, blPoint.Y - handleSize / 2, handleSize, handleSize);
        var b = new Rect(bPoint.X - handleSize / 2, bPoint.Y - handleSize / 2, handleSize, handleSize);
        var br = new Rect(brPoint.X - handleSize / 2, brPoint.Y - handleSize / 2, handleSize, handleSize);

        if (tl.Contains(point)) return ResizeMode.TopLeft;
        if (tr.Contains(point)) return ResizeMode.TopRight;
        if (bl.Contains(point)) return ResizeMode.BottomLeft;
        if (br.Contains(point)) return ResizeMode.BottomRight;
        if (t.Contains(point)) return ResizeMode.Top;
        if (b.Contains(point)) return ResizeMode.Bottom;
        if (l.Contains(point)) return ResizeMode.Left;
        if (r.Contains(point)) return ResizeMode.Right;
        
        // For checking if point is inside the crop rect, we need to inverse transform the point
        // to check against the unrotated rectangle
        if (Math.Abs(rotationAngle) < 0.001)
        {
            if (cropRect.Contains(point))
            {
                return ResizeMode.Move;
            }
        }
        else
        {
            // Transform point back to unrotated space for hit testing
            var centerX = cropRect.X + cropRect.Width / 2;
            var centerY = cropRect.Y + cropRect.Height / 2;
            var angleRad = -rotationAngle * Math.PI / 180.0; // Inverse rotation
            var cos = Math.Cos(angleRad);
            var sin = Math.Sin(angleRad);
            
            var translatedX = point.X - centerX;
            var translatedY = point.Y - centerY;
            
            var unrotatedX = translatedX * cos - translatedY * sin + centerX;
            var unrotatedY = translatedX * sin + translatedY * cos + centerY;
            
            if (cropRect.Contains(new Point(unrotatedX, unrotatedY)))
            {
                return ResizeMode.Move;
            }
        }

        return ResizeMode.None;
    }

    private ResizeMode GetResizeMode(Point point)
    {
        // Apply rotation compensation
        var unrotatedPoint = ToUnrotated(point);
        return GetResizeModeCore(unrotatedPoint);
    }

    private ResizeMode GetResizeModeCore(Point point)
    {
        if (_isCropping) return ResizeMode.None;

        const int cornerSize = 12; // Larger activation area for corners
        const int edgeMargin = 8; // Activation margin for edges
        const int rotationMargin = 20; // Extended area around corners for rotation

        // Check for rotation first - explicit rotation handle
        var handleBounds = new Rect(Canvas.GetLeft(RotationHandle), Canvas.GetTop(RotationHandle), RotationHandle.Width, RotationHandle.Height);
        if (handleBounds.Contains(point))
        {
            return ResizeMode.Rotate;
        }

        // Check for rotation in extended corner areas but outside the immediate corner resize areas
        var extendedCorners = new[]
        {
            new Rect(-rotationMargin, -rotationMargin, cornerSize + rotationMargin, cornerSize + rotationMargin), // TopLeft extended
            new Rect(this.ActualWidth - cornerSize, -rotationMargin, cornerSize + rotationMargin, cornerSize + rotationMargin), // TopRight extended
            new Rect(-rotationMargin, this.ActualHeight - cornerSize, cornerSize + rotationMargin, cornerSize + rotationMargin), // BottomLeft extended
            new Rect(this.ActualWidth - cornerSize, this.ActualHeight - cornerSize, cornerSize + rotationMargin, cornerSize + rotationMargin) // BottomRight extended
        };

        var immediateCorners = new[]
        {
            new Rect(0, 0, cornerSize, cornerSize), // TopLeft
            new Rect(this.ActualWidth - cornerSize, 0, cornerSize, cornerSize), // TopRight
            new Rect(0, this.ActualHeight - cornerSize, cornerSize, cornerSize), // BottomLeft
            new Rect(this.ActualWidth - cornerSize, this.ActualHeight - cornerSize, cornerSize, cornerSize) // BottomRight
        };

        // Check corners for resizing first (higher priority)
        if (immediateCorners[0].Contains(point)) return ResizeMode.TopLeft;
        if (immediateCorners[1].Contains(point)) return ResizeMode.TopRight;
        if (immediateCorners[2].Contains(point)) return ResizeMode.BottomLeft;
        if (immediateCorners[3].Contains(point)) return ResizeMode.BottomRight;

        // Check extended corner areas for rotation (if not in immediate corner)
        for (int i = 0; i < extendedCorners.Length; i++)
        {
            if (extendedCorners[i].Contains(point) && !immediateCorners[i].Contains(point))
            {
                return ResizeMode.Rotate;
            }
        }
        
        // Check edges
        if (new Rect(0, 0, this.ActualWidth, edgeMargin).Contains(point)) return ResizeMode.Top;
        if (new Rect(0, this.ActualHeight - edgeMargin, this.ActualWidth, edgeMargin).Contains(point)) return ResizeMode.Bottom;
        if (new Rect(0, 0, edgeMargin, this.ActualHeight).Contains(point)) return ResizeMode.Left;
        if (new Rect(this.ActualWidth - edgeMargin, 0, edgeMargin, this.ActualHeight).Contains(point)) return ResizeMode.Right;
        
        return ResizeMode.None;
    }

    private InputCursor GetCursor(ResizeMode resizeMode)
    {
        InputSystemCursorShape cursorShape;
        
        switch (resizeMode)
        {
            case ResizeMode.Rotate:
            case ResizeMode.CropRotate:
                return InputSystemCursor.Create(InputSystemCursorShape.SizeAll); // No specific rotation cursor available, use SizeAll
            case ResizeMode.Move:
                cursorShape = InputSystemCursorShape.SizeAll;
                break;
            case ResizeMode.TopLeft:
            case ResizeMode.TopRight:
            case ResizeMode.BottomLeft:
            case ResizeMode.BottomRight:
            case ResizeMode.Top:
            case ResizeMode.Bottom:
            case ResizeMode.Left:
            case ResizeMode.Right:
                cursorShape = GetRotatedCursorShape(resizeMode);
                break;
            default:
                cursorShape = InputSystemCursorShape.Arrow;
                break;
        }
        return InputSystemCursor.Create(cursorShape);
    }
    
    private InputSystemCursorShape GetRotatedCursorShape(ResizeMode resizeMode)
    {
        // Get the current rotation angle
        var rotation = Source?.Rotation ?? 0;
        
        // Normalize rotation to 0-360 range
        rotation = ((rotation % 360) + 360) % 360;
        
        // Determine the effective direction after rotation
        // We use 8 directions, so every 45 degrees shifts the cursor
        var rotationSteps = (int)Math.Round(rotation / 45.0) % 8;
        
        // Map resize modes to direction indices (0 = N, 1 = NE, 2 = E, etc.)
        var directionIndex = resizeMode switch
        {
            ResizeMode.Top => 0,           // North
            ResizeMode.TopRight => 1,     // Northeast  
            ResizeMode.Right => 2,        // East
            ResizeMode.BottomRight => 3,  // Southeast
            ResizeMode.Bottom => 4,       // South
            ResizeMode.BottomLeft => 5,   // Southwest
            ResizeMode.Left => 6,         // West
            ResizeMode.TopLeft => 7,      // Northwest
            _ => 0
        };
        
        // Apply rotation offset
        var rotatedDirection = (directionIndex + rotationSteps) % 8;
        
        // Map back to cursor shapes
        return rotatedDirection switch
        {
            0 => InputSystemCursorShape.SizeNorthSouth,        // North-South
            1 => InputSystemCursorShape.SizeNortheastSouthwest, // Northeast-Southwest
            2 => InputSystemCursorShape.SizeWestEast,          // East-West
            3 => InputSystemCursorShape.SizeNorthwestSoutheast, // Southeast-Northwest
            4 => InputSystemCursorShape.SizeNorthSouth,        // South-North
            5 => InputSystemCursorShape.SizeNortheastSouthwest, // Southwest-Northeast
            6 => InputSystemCursorShape.SizeWestEast,          // West-East
            7 => InputSystemCursorShape.SizeNorthwestSoutheast, // Northwest-Southeast
            _ => InputSystemCursorShape.SizeAll
        };
    }

    private void UpdateDisplay(SourceItem source)
    {
        if (source == null) return;

        DisplayNameText.Text = source.DisplayName;
        // TypeIcon glyph is automatically updated through binding
        
        // Handle website sources
        if (source.Type == SourceType.Website && !string.IsNullOrEmpty(source.WebsiteUrl))
        {
            ShowWebsitePreview(source.WebsiteUrl);
        }
        else
        {
            HideWebsitePreview();
        }
    }
    
    private async void ShowWebsitePreview(string url)
    {
        try
        {
            // Hide regular content and show WebView
            ContentBorder.Visibility = Visibility.Collapsed;
            PreviewBorder.Visibility = Visibility.Collapsed;
            WebsiteBorder.Visibility = Visibility.Visible;
            
            // Ensure WebView
            await WebsitePreview.EnsureCoreWebView2Async();
            
            if (Source != null)
            {
                // Apply user agent (must be set before navigation)
                if (!string.IsNullOrEmpty(Source.WebsiteUserAgent))
                {
                    WebsitePreview.CoreWebView2.Settings.UserAgent = Source.WebsiteUserAgent;
                }
                
                // Hook NavigationCompleted once to apply zoom after page load
                WebsitePreview.CoreWebView2.NavigationCompleted -= WebView_NavigationCompleted;
                WebsitePreview.CoreWebView2.NavigationCompleted += WebView_NavigationCompleted;

                // Setup auto-refresh timer
                SetupWebsiteRefreshTimer();
            }
            
            WebsitePreview.CoreWebView2.Navigate(url);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading website preview: {ex.Message}");
            HideWebsitePreview();
        }
    }

    private void WebView_NavigationCompleted(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
    {
        _ = SetPageZoomAsync();
    }
    
    private void HideWebsitePreview()
    {
        // Stop refresh timer
        _websiteRefreshTimer?.Stop();
        _websiteRefreshTimer = null;
        
        WebsiteBorder.Visibility = Visibility.Collapsed;
        ContentBorder.Visibility = Visibility.Visible;
        
        // Show preview image if live preview is enabled
        if (Source?.IsLivePreviewEnabled == true && Source.Type != SourceType.Website)
        {
            PreviewBorder.Visibility = Visibility.Visible;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Source != null)
        {
            var mainViewModel = App.GetService<MainViewModel>();
            mainViewModel?.RegisterDraggableSourceControl(Source, this);
            
            UpdateDisplay(Source);
            UpdatePosition();
            UpdateCropShading();
            SetupWebsiteRefreshTimer();
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
        
        UpdateShadeClip();
        // Don't call UpdateRootClip() - we don't want to clip handles and crop controls
    }

    private void UpdateShadeClip()
    {
        if (ActualWidth == 0 || ActualHeight == 0) return;

        CropShadePath.Clip = new RectangleGeometry
        {
            Rect         = new Rect(0, 0, ActualWidth, ActualHeight),
        };
    }

    

    private double GetControlCornerRadius()
    {
        // Try to get the corner radius from the static resource
        if (Application.Current.Resources.TryGetValue("ControlCornerRadius", out var resource))
        {
            if (resource is CornerRadius cornerRadius)
            {
                return cornerRadius.TopLeft; // Assume uniform radius
            }
        }
        
        // Fallback to a reasonable default
        return 4.0;
    }

    private PathGeometry CreateRoundedRectangleGeometry(double x, double y, double width, double height, double cornerRadius)
    {
        var pathGeometry = new PathGeometry();
        var pathFigure = new PathFigure
        {
            StartPoint = new Point(x + cornerRadius, y),
            IsClosed = true
        };

        // Top edge
        pathFigure.Segments.Add(new LineSegment { Point = new Point(x + width - cornerRadius, y) });
        
        // Top-right corner
        pathFigure.Segments.Add(new ArcSegment
        {
            Point = new Point(x + width, y + cornerRadius),
            Size = new Size(cornerRadius, cornerRadius),
            SweepDirection = SweepDirection.Clockwise
        });
        
        // Right edge
        pathFigure.Segments.Add(new LineSegment { Point = new Point(x + width, y + height - cornerRadius) });
        
        // Bottom-right corner
        pathFigure.Segments.Add(new ArcSegment
        {
            Point = new Point(x + width - cornerRadius, y + height),
            Size = new Size(cornerRadius, cornerRadius),
            SweepDirection = SweepDirection.Clockwise
        });
        
        // Bottom edge
        pathFigure.Segments.Add(new LineSegment { Point = new Point(x + cornerRadius, y + height) });
        
        // Bottom-left corner
        pathFigure.Segments.Add(new ArcSegment
        {
            Point = new Point(x, y + height - cornerRadius),
            Size = new Size(cornerRadius, cornerRadius),
            SweepDirection = SweepDirection.Clockwise
        });
        
        // Left edge
        pathFigure.Segments.Add(new LineSegment { Point = new Point(x, y + cornerRadius) });
        
        // Top-left corner
        pathFigure.Segments.Add(new ArcSegment
        {
            Point = new Point(x + cornerRadius, y),
            Size = new Size(cornerRadius, cornerRadius),
            SweepDirection = SweepDirection.Clockwise
        });

        pathGeometry.Figures.Add(pathFigure);
        return pathGeometry;
    }

    public void SetSelected(bool selected)
    {
        _isSelected = selected;
        VisualStateManager.GoToState(this, selected ? "Selected" : "Normal", true);
    }

    private void CropActionsPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // When the panel's size is finally determined, re-run the visual update
        // to position it correctly. This solves the initial centering problem.
        if (_isCropping)
        {
            var transform = CropRect.RenderTransform as RotateTransform;
            UpdateCropVisuals(_cropStartRect, transform?.Angle ?? 0);
        }
    }

    private void SetupWebsiteRefreshTimer()
    {
        // Stop existing timer
        _websiteRefreshTimer?.Stop();
        _websiteRefreshTimer = null;
        
        // Create new timer if refresh interval is set
        if (Source != null && Source.WebsiteRefreshInterval > 0)
        {
            _websiteRefreshTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            _websiteRefreshTimer.Interval = TimeSpan.FromSeconds(Source.WebsiteRefreshInterval);
            _websiteRefreshTimer.Tick += (sender, e) =>
            {
                try
                {
                                    if (WebsiteBorder.Visibility == Visibility.Visible && 
                    WebsitePreview.CoreWebView2 != null && 
                    !string.IsNullOrEmpty(Source?.WebsiteUrl))
                    {
                        WebsitePreview.CoreWebView2.Reload();
                        Debug.WriteLine($"🔄 Auto-refreshed website: {Source.Name}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error during auto-refresh: {ex.Message}");
                }
            };
            _websiteRefreshTimer.Start();
            Debug.WriteLine($"⏰ Auto-refresh timer started for {Source.Name}: {Source.WebsiteRefreshInterval} seconds");
        }
    }

    private async Task SetPageZoomAsync()
    {
        if (Source == null || WebsitePreview.CoreWebView2 == null) return;
        try
        {
            // Try native ZoomFactor property via reflection (not available in all SDK versions)
            var zoomProp = WebsitePreview.CoreWebView2.GetType().GetProperty("ZoomFactor");
            if (zoomProp != null && zoomProp.CanWrite)
            {
                zoomProp.SetValue(WebsitePreview.CoreWebView2, Source.WebsiteZoom);
                return;
            }
            // Fallback to CSS zoom on both html and body elements
            var zoomCss = Source.WebsiteZoom.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            string js = $"document.documentElement.style.zoom='{zoomCss}'; document.body.style.zoom='{zoomCss}';";
            await WebsitePreview.CoreWebView2.ExecuteScriptAsync(js);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SetPageZoom error: {ex.Message}");
        }
    }

    public async Task<byte[]?> CaptureWebViewFrameAsync()
    {
        if (WebsitePreview?.CoreWebView2 == null) return null;

        try
        {
            using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await WebsitePreview.CoreWebView2.CapturePreviewAsync(
                Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, ms);

            // Convert to JPEG for consistency with other sources
            ms.Seek(0);
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ms);
            
            using var jpeg = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId, jpeg);
                
            var pixels = await decoder.GetPixelDataAsync();
            encoder.SetPixelData(
                decoder.BitmapPixelFormat,
                decoder.BitmapAlphaMode,
                decoder.PixelWidth,
                decoder.PixelHeight,
                decoder.DpiX,
                decoder.DpiY,
                pixels.DetachPixelData());
                
            await encoder.FlushAsync();
            
            jpeg.Seek(0);
            var buffer = new byte[jpeg.Size];
            await jpeg.ReadAsync(buffer.AsBuffer(), (uint)jpeg.Size,
                Windows.Storage.Streams.InputStreamOptions.None);
                
            return buffer;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error capturing WebView frame: {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// Call this method when the source is being deleted to ensure immediate WebView cleanup
    /// </summary>
    public void CleanupOnDelete()
    {
        // Stop website refresh timer immediately
        if (_websiteRefreshTimer != null)
        {
            _websiteRefreshTimer.Stop();
            _websiteRefreshTimer = null;
        }
        
        // Dispose WebView resources immediately
        DisposeWebViewResources();
    }
} 