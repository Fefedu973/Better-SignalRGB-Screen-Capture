using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.ViewModels;

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
        var h = size.Width * sin + size.Height * cos;

        return new Rect(
            center.X - w / 2,
            center.Y - h / 2,
            w, h);
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
        
        // Set up event handlers
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
        RightTapped += OnRightTapped;
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
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
                        ExitCropMode();
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
                    break;

                case nameof(SourceItem.DisplayName):
                case nameof(SourceItem.Type):
                    UpdateDisplay(Source);
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

    private void UpdateCropShading()
    {
        if (Source == null) return;

        double left = ActualWidth * Source.CropLeftPct;
        double top = ActualHeight * Source.CropTopPct;
        double right = ActualWidth * Source.CropRightPct;
        double bottom = ActualHeight * Source.CropBottomPct;

        // Check if we have crop rotation
        if (Math.Abs(Source.CropRotation) > 0.001)
        {
            // For rotated crops, position the shade rectangles with rotation transforms
            var centerX = ActualWidth / 2;
            var centerY = ActualHeight / 2;
            
            // Top shade
            CropShadeT.Width = ActualWidth * 2;
            CropShadeT.Height = Math.Max(0, centerY - (ActualHeight / 2 - top) + ActualHeight);
            Canvas.SetLeft(CropShadeT, -ActualWidth / 2);
            Canvas.SetTop(CropShadeT, -ActualHeight);
            
            var topTransform = new RotateTransform();
            topTransform.Angle = Source.CropRotation;
            topTransform.CenterX = ActualWidth / 2 + centerX;
            topTransform.CenterY = ActualHeight + centerY;
            CropShadeT.RenderTransform = topTransform;
            
            // Bottom shade
            CropShadeB.Width = ActualWidth * 2;
            CropShadeB.Height = Math.Max(0, centerY - (ActualHeight / 2 - bottom) + ActualHeight);
            Canvas.SetLeft(CropShadeB, -ActualWidth / 2);
            Canvas.SetTop(CropShadeB, ActualHeight - bottom);
            
            var bottomTransform = new RotateTransform();
            bottomTransform.Angle = Source.CropRotation;
            bottomTransform.CenterX = ActualWidth / 2 + centerX;
            bottomTransform.CenterY = centerY - (ActualHeight - bottom);
            CropShadeB.RenderTransform = bottomTransform;
            
            // Left shade
            CropShadeL.Width = Math.Max(0, centerX - (ActualWidth / 2 - left) + ActualWidth);
            CropShadeL.Height = ActualHeight - top - bottom;
            Canvas.SetLeft(CropShadeL, -ActualWidth);
            Canvas.SetTop(CropShadeL, top);
            
            var leftTransform = new RotateTransform();
            leftTransform.Angle = Source.CropRotation;
            leftTransform.CenterX = ActualWidth + centerX;
            leftTransform.CenterY = (ActualHeight - top - bottom) / 2;
            CropShadeL.RenderTransform = leftTransform;
            
            // Right shade
            CropShadeR.Width = Math.Max(0, centerX - (ActualWidth / 2 - right) + ActualWidth);
            CropShadeR.Height = ActualHeight - top - bottom;
            Canvas.SetLeft(CropShadeR, ActualWidth - right);
            Canvas.SetTop(CropShadeR, top);
            
            var rightTransform = new RotateTransform();
            rightTransform.Angle = Source.CropRotation;
            rightTransform.CenterX = centerX - (ActualWidth - right);
            rightTransform.CenterY = (ActualHeight - top - bottom) / 2;
            CropShadeR.RenderTransform = rightTransform;
        }
        else
        {
            // No rotation - use simple positioning
            CropShadeT.Width = CropShadeB.Width = ActualWidth;
            CropShadeT.Height = top;
            Canvas.SetLeft(CropShadeT, 0);
            Canvas.SetTop(CropShadeT, 0);
            CropShadeT.RenderTransform = null;
            
            Canvas.SetTop(CropShadeB, ActualHeight - bottom);
            CropShadeB.Height = bottom;
            Canvas.SetLeft(CropShadeB, 0);
            CropShadeB.RenderTransform = null;

            // Left & right stripes
            CropShadeL.Height = CropShadeR.Height = ActualHeight - top - bottom;
            CropShadeL.Width = left;
            Canvas.SetLeft(CropShadeL, 0);
            Canvas.SetTop(CropShadeL, top);
            CropShadeL.RenderTransform = null;
            
            CropShadeR.Width = right;
            Canvas.SetLeft(CropShadeR, ActualWidth - right);
            Canvas.SetTop(CropShadeR, top);
            CropShadeR.RenderTransform = null;
        }
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

        // Always select the item when starting a resize operation
        if (_resizeMode != ResizeMode.None)
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
            var newAngle = _actionStartRotation + angleDelta;
            
            if (isShiftDown)
            {
                newAngle = Math.Round(newAngle / 15.0) * 15.0; // Snap to 15-degree increments
            }

            // Check if the new rotation would cause the effective (cropped) rectangle to overflow the canvas
            var effectiveWidth = _actionStartBounds.Width * (1 - Source.CropLeftPct - Source.CropRightPct);
            var effectiveHeight = _actionStartBounds.Height * (1 - Source.CropTopPct - Source.CropBottomPct);
            var effectiveSize = new Size(Math.Max(10, effectiveWidth), Math.Max(10, effectiveHeight));
            var rotatedAabb = GetRotatedAabb(centerPoint, effectiveSize, newAngle);

            // If the rotated AABB would overflow, don't apply the rotation
            if (rotatedAabb.Left >= 0 && rotatedAabb.Top >= 0 && 
                rotatedAabb.Right <= parent.ActualWidth && rotatedAabb.Bottom <= parent.ActualHeight)
            {
                Source.Rotation = (int)Math.Round(newAngle);
            }
            // If overflow would occur, keep the current rotation (no update)
        }
        else if (_isResizing)
        {
            // 1.  Raw deltas
            var rawDx = currentPoint.X - _actionStartPointerPosition.X;
            var rawDy = currentPoint.Y - _actionStartPointerPosition.Y;

            // 2.  Minimum sizes (respect the XAML minimums)
            double minW = Math.Max(this.MinWidth,  10);
            double minH = Math.Max(this.MinHeight, 10);

            // 3.  Convenience aliases
            double w = _actionStartBounds.Width;
            double h = _actionStartBounds.Height;

            // 4.  Limits for this handle - considering cropped dimensions
            // Calculate effective bounds based on crop
            var effectiveLeft = _actionStartBounds.Left + _actionStartBounds.Width * Source.CropLeftPct;
            var effectiveTop = _actionStartBounds.Top + _actionStartBounds.Height * Source.CropTopPct;
            var effectiveRight = _actionStartBounds.Right - _actionStartBounds.Width * Source.CropRightPct;
            var effectiveBottom = _actionStartBounds.Bottom - _actionStartBounds.Height * Source.CropBottomPct;
            
            // For non-rotated rectangles, use effective bounds for limits
            double maxDxRight, maxDyBottom, maxDxLeft, maxDyTop;
            if (Math.Abs(Source.Rotation) < 0.001 && Math.Abs(Source.CropRotation) < 0.001)
            {
                // Non-rotated: Allow the full rectangle to extend beyond canvas
                // as long as the effective (cropped) area stays within bounds
                
                // For right/bottom expansion: limit is based on effective edge reaching canvas edge
                maxDxRight = parent.ActualWidth - effectiveRight + (_actionStartBounds.Width * Source.CropRightPct);
                maxDyBottom = parent.ActualHeight - effectiveBottom + (_actionStartBounds.Height * Source.CropBottomPct);
                
                // For left/top movement: allow negative positions as long as effective area is visible
                maxDxLeft = effectiveLeft - (_actionStartBounds.Width * Source.CropLeftPct);
                maxDyTop = effectiveTop - (_actionStartBounds.Height * Source.CropTopPct);
            }
            else
            {
                // For rotated rectangles, we need more complex calculations
                // For now, allow more freedom and let ApplyResize handle the final bounds check
                maxDxRight = parent.ActualWidth;
                maxDyBottom = parent.ActualHeight;
                maxDxLeft = _actionStartBounds.Left + _actionStartBounds.Width - minW;
                maxDyTop = _actionStartBounds.Top + _actionStartBounds.Height - minH;
            }

            double maxShrinkX = w - minW;   // positive
            double maxShrinkY = h - minH;   // positive

            // 5.  Clamp deltas depending on the handle
            double dx = rawDx, dy = rawDy;

            switch (_resizeMode)
            {
                case ResizeMode.Right:
                    dx = Math.Clamp(dx, -maxShrinkX, maxDxRight);
                    break;
                case ResizeMode.Left:
                    dx = Math.Clamp(dx, -maxDxLeft,  maxShrinkX);
                    break;
                case ResizeMode.Bottom:
                    dy = Math.Clamp(dy, -maxShrinkY, maxDyBottom);
                    break;
                case ResizeMode.Top:
                    dy = Math.Clamp(dy, -maxDyTop,  maxShrinkY);
                    break;
                case ResizeMode.BottomRight:
                    dx = Math.Clamp(dx, -maxShrinkX, maxDxRight);
                    dy = Math.Clamp(dy, -maxShrinkY, maxDyBottom);
                    break;
                case ResizeMode.BottomLeft:
                    dx = Math.Clamp(dx, -maxDxLeft,  maxShrinkX);
                    dy = Math.Clamp(dy, -maxShrinkY, maxDyBottom);
                    break;
                case ResizeMode.TopRight:
                    dx = Math.Clamp(dx, -maxShrinkX, maxDxRight);
                    dy = Math.Clamp(dy, -maxDyTop,  maxShrinkY);
                    break;
                case ResizeMode.TopLeft:
                    dx = Math.Clamp(dx, -maxDxLeft,  maxShrinkX);
                    dy = Math.Clamp(dy, -maxDyTop,  maxShrinkY);
                    break;
            }
            
            ApplyResize(dx, dy, isShiftDown);
        }
        else if (_isDragging)
        {
            var deltaX = currentPoint.X - _actionStartPointerPosition.X;
            var deltaY = currentPoint.Y - _actionStartPointerPosition.Y;
            var newX = _actionStartBounds.X + deltaX;
            var newY = _actionStartBounds.Y + deltaY;

            // Effective (visible) geometry
            var w = _actionStartBounds.Width;
            var h = _actionStartBounds.Height;
            var effW = w * (1 - Source.CropLeftPct - Source.CropRightPct);
            var effH = h * (1 - Source.CropTopPct - Source.CropBottomPct);

            if (effW <= 0 || effH <= 0) // Fully cropped, allow any move
            {
                Source.CanvasX = (int)Math.Round(newX);
                Source.CanvasY = (int)Math.Round(newY);
                return;
            }

            // Mimic the logic from MainViewModel.FitsInCanvas which is known to work for manual entry.
            // This ensures consistent behavior between dragging and direct coordinate input.

            // 1. Offset of the visible part inside the control (unrotated)
            var offX = w * Source.CropLeftPct;
            var offY = h * Source.CropTopPct;

            // 2. Center of the visible part at its proposed new canvas position
            var centre = new Point(newX + offX + effW / 2, newY + offY + effH / 2);

            // 3. AABB of the visible part, using summed rotation (same as manual entry check)
            var aabb = GetRotatedAabb(centre, new Size(effW, effH), Source.Rotation + Source.CropRotation);

            // 4. Clamp the AABB's top-left corner to the canvas boundaries
            var parentW = parent.ActualWidth;
            var parentH = parent.ActualHeight;
            
            var clampedAabbX = Math.Max(0, Math.Min(aabb.X, parentW - aabb.Width));
            var clampedAabbY = Math.Max(0, Math.Min(aabb.Y, parentH - aabb.Height));

            // 5. Calculate the adjustment needed and apply it to the control's position
            var adjustX = clampedAabbX - aabb.X;
            var adjustY = clampedAabbY - aabb.Y;
            
            var finalNewX = newX + adjustX;
            var finalNewY = newY + adjustY;

            if(Source != null)
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
        Point anchorWorld = new(
            centre0.X + ToScreen(anchorLocal0.X, anchorLocal0.Y).X,
            centre0.Y + ToScreen(anchorLocal0.X, anchorLocal0.Y).Y);

        // new centre so that anchorWorld stays put
        Point centre1 = new(
            anchorWorld.X - ToScreen(anchorLocal1.X, anchorLocal1.Y).X,
            anchorWorld.Y - ToScreen(anchorLocal1.X, anchorLocal1.Y).Y);

        double newX = centre1.X - newW / 2.0;
        double newY = centre1.Y - newH / 2.0;

        // ---------- 5.  Check if the oriented box would overflow the canvas -------
        // Use effective (cropped) dimensions for overflow checking
        var effectiveNewW = newW * (1 - Source.CropLeftPct - Source.CropRightPct);
        var effectiveNewH = newH * (1 - Source.CropTopPct - Source.CropBottomPct);
        var effectiveSize = new Size(Math.Max(10, effectiveNewW), Math.Max(10, effectiveNewH));
        
        Rect aabb = GetRotatedAabb(centre1, effectiveSize, Source.Rotation);
        
        // If it would overflow, don't apply the change
        if (aabb.Left < 0 || aabb.Top < 0 || 
            aabb.Right > canvas.ActualWidth || 
            aabb.Bottom > canvas.ActualHeight)
        {
            return;
        }

        // ---------- 6.  Commit ---------------------------------------------------
        Source.CanvasX      = (int)Math.Round(newX);
        Source.CanvasY      = (int)Math.Round(newY);
        Source.CanvasWidth  = (int)Math.Round(newW);
        Source.CanvasHeight = (int)Math.Round(newH);
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
        var layerSubMenu = new MenuFlyoutSubItem { Text = "Layer", Icon = new FontIcon { Glyph = "\uE8FD" } };
        BuildLayerMenuItems(layerSubMenu, viewModel);
        menuFlyout.Items.Add(layerSubMenu);

        menuFlyout.Items.Add(new MenuFlyoutSeparator());

        var flipHorizontalItem = new MenuFlyoutItem { Text = "Flip Horizontally", Icon = new FontIcon { Glyph = "\uE7F7" } };
        flipHorizontalItem.Click += async (s, a) => 
        {
            if (viewModel?.SelectedSources.Any() != true) return;
            var targetState = !viewModel.SelectedSources[0].IsMirroredHorizontally;
            foreach(var source in viewModel.SelectedSources) source.IsMirroredHorizontally = targetState;
            await viewModel.SaveSourcesAsync();
        };
        var flipVerticalItem = new MenuFlyoutItem { Text = "Flip Vertically", Icon = new FontIcon { Glyph = "\uE7F8" } };
        flipVerticalItem.Click += async (s, a) => 
        {
            if (viewModel?.SelectedSources.Any() != true) return;
            var targetState = !viewModel.SelectedSources[0].IsMirroredVertically;
            foreach(var source in viewModel.SelectedSources) source.IsMirroredVertically = targetState;
            await viewModel.SaveSourcesAsync();
        };
        menuFlyout.Items.Add(flipHorizontalItem);
        menuFlyout.Items.Add(flipVerticalItem);

        menuFlyout.Items.Add(new MenuFlyoutSeparator());

        var centerItem = new MenuFlyoutItem { Text = "Center", Icon = new FontIcon { Glyph = "\uE843" } };
        centerItem.Click += CenterMenuItem_Click;
        menuFlyout.Items.Add(centerItem);

        menuFlyout.Items.Add(new MenuFlyoutSeparator());
        
        var cropItem = new MenuFlyoutItem { Text = "Crop", Icon = new FontIcon { Glyph = "\uE7A4" } };
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

        var alignSubMenu = new MenuFlyoutSubItem { Text = "Align", Icon = new FontIcon { Glyph = "\uE834" } };
        BuildAlignMenuItems(alignSubMenu, viewModel);
        menuFlyout.Items.Add(alignSubMenu);

        var centerItem = new MenuFlyoutItem { Text = "Center", Icon = new FontIcon { Glyph = "\uE843" } };
        centerItem.Click += CenterMenuItem_Click;
        menuFlyout.Items.Add(centerItem);

        menuFlyout.Items.Add(new MenuFlyoutSeparator());
        
        var layerSubMenu = new MenuFlyoutSubItem { Text = "Layer", Icon = new FontIcon { Glyph = "\uE8FD" } };
        BuildLayerMenuItems(layerSubMenu, viewModel);
        menuFlyout.Items.Add(layerSubMenu);

        menuFlyout.Items.Add(new MenuFlyoutSeparator());

        var flipHorizontalItem = new MenuFlyoutItem { Text = "Flip Horizontally", Icon = new FontIcon { Glyph = "\uE7F7" } };
        flipHorizontalItem.Click += async (s, a) => 
        {
            if (viewModel?.SelectedSources.Any() != true) return;
            var targetState = !viewModel.SelectedSources[0].IsMirroredHorizontally;
            foreach (var source in viewModel.SelectedSources) source.IsMirroredHorizontally = targetState;
            await viewModel.SaveSourcesAsync();
        };
        var flipVerticalItem = new MenuFlyoutItem { Text = "Flip Vertically", Icon = new FontIcon { Glyph = "\uE7F8" } };
        flipVerticalItem.Click += async (s, a) =>
        {
            if (viewModel?.SelectedSources.Any() != true) return;
            var targetState = !viewModel.SelectedSources[0].IsMirroredVertically;
            foreach (var source in viewModel.SelectedSources) source.IsMirroredVertically = targetState;
            await viewModel.SaveSourcesAsync();
        };
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
        var alignLeft = new MenuFlyoutItem { Text = "Align Left", Command = viewModel.AlignLeftCommand };
        var alignCenter = new MenuFlyoutItem { Text = "Align Center", Command = viewModel.AlignCenterCommand };
        var alignRight = new MenuFlyoutItem { Text = "Align Right", Command = viewModel.AlignRightCommand };
        alignSubMenu.Items.Add(alignLeft);
        alignSubMenu.Items.Add(alignCenter);
        alignSubMenu.Items.Add(alignRight);

        alignSubMenu.Items.Add(new MenuFlyoutSeparator());

        var alignTop = new MenuFlyoutItem { Text = "Align Top", Command = viewModel.AlignTopCommand };
        var alignMiddle = new MenuFlyoutItem { Text = "Align Middle", Command = viewModel.AlignMiddleCommand };
        var alignBottom = new MenuFlyoutItem { Text = "Align Bottom", Command = viewModel.AlignBottomCommand };
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
        var cropRect = new Rect(Canvas.GetLeft(CropRect), Canvas.GetTop(CropRect), CropRect.Width, CropRect.Height);

        // Convert pixels to percentages
        Source.CropLeftPct = cropRect.X / ActualWidth;
        Source.CropTopPct = cropRect.Y / ActualHeight;
        Source.CropRightPct = (ActualWidth - cropRect.Right) / ActualWidth;
        Source.CropBottomPct = (ActualHeight - cropRect.Bottom) / ActualHeight;

        // Apply current rotation to the crop
        var transform = CropRect.RenderTransform as RotateTransform;
        if (transform != null)
        {
            Source.CropRotation = (int)Math.Round(transform.Angle);
        }

        App.GetService<MainViewModel>()?.SaveSourcesCommand.Execute(null);

        ExitCropMode();
    }

    private void CancelCropButton_Click(object sender, RoutedEventArgs e)
    {
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

        // convert delta into the crop-rect's local (rotated) space
        var localΔ = ToLocal(deltaX, deltaY, _cropActionStartRotation);

        const double minSize = 10;

        switch (_cropResizeMode)
        {
            case ResizeMode.CropRotate:
                var centerPoint = new Point(_cropActionStartBounds.X + _cropActionStartBounds.Width / 2, _cropActionStartBounds.Y + _cropActionStartBounds.Height / 2);
                var startVector = new Point(_cropActionStartPointerPosition.X - centerPoint.X, _cropActionStartPointerPosition.Y - centerPoint.Y);
                var currentVector = new Point(currentPoint.X - centerPoint.X, currentPoint.Y - centerPoint.Y);

                var startAngle = Math.Atan2(startVector.Y, startVector.X) * (180.0 / Math.PI);
                var currentAngle = Math.Atan2(currentVector.Y, currentVector.X) * (180.0 / Math.PI);

                var angleDelta = currentAngle - startAngle;
                var newAngle = _cropActionStartRotation + angleDelta;

                var isShiftDown = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift);
                if (isShiftDown)
                {
                    newAngle = Math.Round(newAngle / 15.0) * 15.0; // Snap to 15-degree increments
                }

                UpdateCropVisuals(_cropActionStartBounds, newAngle);
                return;
            case ResizeMode.Left:
                newBounds.X = Math.Clamp(_cropActionStartBounds.Left + localΔ.X, 0, _cropActionStartBounds.Right - minSize);
                newBounds.Width = _cropActionStartBounds.Right - newBounds.X;
                break;
            case ResizeMode.Right:
                newBounds.Width = Math.Clamp(_cropActionStartBounds.Width + localΔ.X, minSize, ActualWidth - newBounds.Left);
                break;
            case ResizeMode.Top:
                newBounds.Y = Math.Clamp(_cropActionStartBounds.Top + localΔ.Y, 0, _cropActionStartBounds.Bottom - minSize);
                newBounds.Height = _cropActionStartBounds.Bottom - newBounds.Y;
                break;
            case ResizeMode.Bottom:
                newBounds.Height = Math.Clamp(_cropActionStartBounds.Height + localΔ.Y, minSize, ActualHeight - newBounds.Top);
                break;
            case ResizeMode.TopLeft:
                newBounds.X = Math.Clamp(_cropActionStartBounds.Left + localΔ.X, 0, _cropActionStartBounds.Right - minSize);
                newBounds.Width = _cropActionStartBounds.Right - newBounds.X;
                newBounds.Y = Math.Clamp(_cropActionStartBounds.Top + localΔ.Y, 0, _cropActionStartBounds.Bottom - minSize);
                newBounds.Height = _cropActionStartBounds.Bottom - newBounds.Y;
                break;
            case ResizeMode.TopRight:
                newBounds.Width = Math.Clamp(_cropActionStartBounds.Width + localΔ.X, minSize, ActualWidth - newBounds.Left);
                newBounds.Y = Math.Clamp(_cropActionStartBounds.Top + localΔ.Y, 0, _cropActionStartBounds.Bottom - minSize);
                newBounds.Height = _cropActionStartBounds.Bottom - newBounds.Y;
                break;
            case ResizeMode.BottomLeft:
                newBounds.X = Math.Clamp(_cropActionStartBounds.Left + localΔ.X, 0, _cropActionStartBounds.Right - minSize);
                newBounds.Width = _cropActionStartBounds.Right - newBounds.X;
                newBounds.Height = Math.Clamp(_cropActionStartBounds.Height + localΔ.Y, minSize, ActualHeight - newBounds.Top);
                break;
            case ResizeMode.BottomRight:
                newBounds.Width = Math.Clamp(_cropActionStartBounds.Width + localΔ.X, minSize, ActualWidth - newBounds.Left);
                newBounds.Height = Math.Clamp(_cropActionStartBounds.Height + localΔ.Y, minSize, ActualHeight - newBounds.Top);
                break;
            case ResizeMode.Move:
                newBounds.X = Math.Clamp(_cropActionStartBounds.X + localΔ.X, 0, ActualWidth - _cropActionStartBounds.Width);
                newBounds.Y = Math.Clamp(_cropActionStartBounds.Y + localΔ.Y, 0, ActualHeight - _cropActionStartBounds.Height);
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

        // Update overlay - now handle both rotated and non-rotated cases
        if (Math.Abs(rotationAngle) < 0.001)
        {
            // No rotation - use simple axis-aligned overlays
            CropOverlayT.Height = cropRect.Top;
            CropOverlayT.Width = ActualWidth;
            Canvas.SetLeft(CropOverlayT, 0);
            Canvas.SetTop(CropOverlayT, 0);
            CropOverlayT.Visibility = Visibility.Visible;
            CropOverlayT.RenderTransform = null;
            CropOverlayT.Clip = null; // Clear any clipping
            CropOverlayT.Opacity = 1.0; // Full opacity for non-rotated

            CropOverlayB.Height = Math.Max(0, ActualHeight - cropRect.Bottom);
            CropOverlayB.Width = ActualWidth;
            Canvas.SetLeft(CropOverlayB, 0);
            Canvas.SetTop(CropOverlayB, cropRect.Bottom);
            CropOverlayB.Visibility = Visibility.Visible;
            CropOverlayB.RenderTransform = null;
            CropOverlayB.Opacity = 1.0;

            CropOverlayL.Width = cropRect.Left;
            Canvas.SetLeft(CropOverlayL, 0);
            Canvas.SetTop(CropOverlayL, cropRect.Top);
            CropOverlayL.Height = cropRect.Height;
            CropOverlayL.Visibility = Visibility.Visible;
            CropOverlayL.RenderTransform = null;
            CropOverlayL.Opacity = 1.0;

            CropOverlayR.Width = Math.Max(0, ActualWidth - cropRect.Right);
            Canvas.SetLeft(CropOverlayR, cropRect.Right);
            Canvas.SetTop(CropOverlayR, cropRect.Top);
            CropOverlayR.Height = cropRect.Height;
            CropOverlayR.Visibility = Visibility.Visible;
            CropOverlayR.RenderTransform = null;
            CropOverlayR.Opacity = 1.0;
        }
        else
        {
            // For rotated crops, use a full-screen overlay approach with a proper clip region
            CropOverlayT.Width = ActualWidth;
            CropOverlayT.Height = ActualHeight;
            Canvas.SetLeft(CropOverlayT, 0);
            Canvas.SetTop(CropOverlayT, 0);
            CropOverlayT.Visibility = Visibility.Visible;
            CropOverlayT.RenderTransform = null;
            CropOverlayT.Opacity = 1.0;
            
            // Create a clipping rectangle that represents the crop area (rotated)
            var centerX = cropRect.X + cropRect.Width / 2;
            var centerY = cropRect.Y + cropRect.Height / 2;
            
            // Create a simple rectangular clip - we'll just reduce opacity for rotated crops
            // since WinUI 3 has limitations with complex clipping
            CropOverlayT.Opacity = 0.5; // Semi-transparent for rotated crops
            
            // Hide other overlays when rotated
            CropOverlayB.Visibility = Visibility.Collapsed;
            CropOverlayL.Visibility = Visibility.Collapsed;
            CropOverlayR.Visibility = Visibility.Collapsed;
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
            case ResizeMode.BottomRight:
                cursorShape = InputSystemCursorShape.SizeNorthwestSoutheast;
                break;
            case ResizeMode.TopRight:
            case ResizeMode.BottomLeft:
                cursorShape = InputSystemCursorShape.SizeNortheastSouthwest;
                break;
            case ResizeMode.Top:
            case ResizeMode.Bottom:
                cursorShape = InputSystemCursorShape.SizeNorthSouth;
                break;
            case ResizeMode.Left:
            case ResizeMode.Right:
                cursorShape = InputSystemCursorShape.SizeWestEast;
                break;
            default:
                cursorShape = InputSystemCursorShape.Arrow;
                break;
        }
        return InputSystemCursor.Create(cursorShape);
    }

    private void UpdateDisplay(SourceItem source)
    {
        if (source == null) return;

        DisplayNameText.Text = source.DisplayName;
        TypeText.Text = source.Type.ToString();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Source != null)
        {
            UpdateDisplay(Source);

            // Defer the position refresh to ensure the control has been measured and arranged
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (Source != null) RefreshPosition();
            });
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // This is the most reliable place to update visuals that depend on the final rendered size.
        if (Source != null)
        {
            RefreshPosition();
        }
        
        // Update the clipping rectangle for the crop shading canvas
        if (CropShadingCanvasClip != null)
        {
            CropShadingCanvasClip.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
        }
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
} 