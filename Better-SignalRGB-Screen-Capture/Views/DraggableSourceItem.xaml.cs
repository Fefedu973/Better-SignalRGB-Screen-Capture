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

    private enum ResizeMode { None, TopLeft, TopRight, BottomLeft, BottomRight, Top, Bottom, Left, Right, Rotate, Move }

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

        // Top & bottom stripes
        CropShadeT.Width = CropShadeB.Width = ActualWidth;
        CropShadeT.Height = top;
        Canvas.SetTop(CropShadeB, ActualHeight - bottom);
        CropShadeB.Height = bottom;

        // Left & right stripes
        CropShadeL.Height = CropShadeR.Height = ActualHeight - top - bottom;
        CropShadeL.Width = left;
        Canvas.SetLeft(CropShadeL, 0);
        CropShadeR.Width = right;
        Canvas.SetLeft(CropShadeR, ActualWidth - right);
        Canvas.SetTop(CropShadeL, top);
        Canvas.SetTop(CropShadeR, top);
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

            // Check if the new rotation would cause the rectangle to overflow the canvas
            var size = new Size(_actionStartBounds.Width, _actionStartBounds.Height);
            var rotatedAabb = GetRotatedAabb(centerPoint, size, newAngle);

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

            // 4.  Limits for this handle
            double maxDxRight  = parent.ActualWidth  - (_actionStartBounds.Right);
            double maxDyBottom = parent.ActualHeight - (_actionStartBounds.Bottom);
            double maxDxLeft   = _actionStartBounds.Left;
            double maxDyTop    = _actionStartBounds.Top;

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

            // For rotated rectangles, check the oriented bounding box
            if (Source.Rotation != 0)
            {
                var newCenter = new Point(newX + _actionStartBounds.Width / 2, newY + _actionStartBounds.Height / 2);
                var size = new Size(_actionStartBounds.Width, _actionStartBounds.Height);
                var rotatedAabb = GetRotatedAabb(newCenter, size, Source.Rotation);

                // Clamp the AABB to canvas bounds
                var clampedX = Math.Max(0, rotatedAabb.X);
                var clampedY = Math.Max(0, rotatedAabb.Y);
                var clampedWidth = Math.Max(0, Math.Min(rotatedAabb.Width, parent.ActualWidth - clampedX));
                var clampedHeight = Math.Max(0, Math.Min(rotatedAabb.Height, parent.ActualHeight - clampedY));
                
                var clampedAabb = new Rect(clampedX, clampedY, clampedWidth, clampedHeight);

                // If clamping changed the AABB, back-solve the position
                if (Math.Abs(clampedAabb.X - rotatedAabb.X) > 0.1 || Math.Abs(clampedAabb.Y - rotatedAabb.Y) > 0.1 ||
                    Math.Abs(clampedAabb.Width - rotatedAabb.Width) > 0.1 || Math.Abs(clampedAabb.Height - rotatedAabb.Height) > 0.1)
                {
                    // Calculate the new center from the clamped AABB
                    var clampedCenter = new Point(clampedAabb.X + clampedAabb.Width / 2, clampedAabb.Y + clampedAabb.Height / 2);
                    newX = clampedCenter.X - _actionStartBounds.Width / 2;
                    newY = clampedCenter.Y - _actionStartBounds.Height / 2;
                }
            }
            else
            {
                // For non-rotated rectangles, use simple axis-aligned bounds
                newX = Math.Max(0, Math.Min(newX, parent.ActualWidth - _actionStartBounds.Width));
                newY = Math.Max(0, Math.Min(newY, parent.ActualHeight - _actionStartBounds.Height));
            }
            
            if(Source != null)
            {
                Source.CanvasX = (int)newX;
                Source.CanvasY = (int)newY;
            }
            DragDelta?.Invoke(this, new Point(newX - _actionStartBounds.X, newY - _actionStartBounds.Y));
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
        if (keepRatio)
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
        Rect aabb = GetRotatedAabb(centre1, new Size(newW, newH), Source.Rotation);
        
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
           App.GetService<MainViewModel>()?.SaveSourcesCommand.Execute(null);
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
                    viewModel.Sources.Move(index, index - 1);
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
                    viewModel.Sources.Move(index, index + 1);
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

        UpdateCropVisuals(_cropStartRect);
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
        
        const double minSize = 10;

        switch (_cropResizeMode)
        {
            case ResizeMode.Left:
                newBounds.X = Math.Clamp(_cropActionStartBounds.Left + deltaX, 0, _cropActionStartBounds.Right - minSize);
                newBounds.Width = _cropActionStartBounds.Right - newBounds.X;
                break;
            case ResizeMode.Right:
                newBounds.Width = Math.Clamp(_cropActionStartBounds.Width + deltaX, minSize, ActualWidth - _cropActionStartBounds.X);
                break;
            case ResizeMode.Top:
                newBounds.Y = Math.Clamp(_cropActionStartBounds.Top + deltaY, 0, _cropActionStartBounds.Bottom - minSize);
                newBounds.Height = _cropActionStartBounds.Bottom - newBounds.Y;
                break;
            case ResizeMode.Bottom:
                newBounds.Height = Math.Clamp(_cropActionStartBounds.Height + deltaY, minSize, ActualHeight - _cropActionStartBounds.Y);
                break;
            case ResizeMode.TopLeft:
                newBounds.X = Math.Clamp(_cropActionStartBounds.Left + deltaX, 0, _cropActionStartBounds.Right - minSize);
                newBounds.Width = _cropActionStartBounds.Right - newBounds.X;
                newBounds.Y = Math.Clamp(_cropActionStartBounds.Top + deltaY, 0, _cropActionStartBounds.Bottom - minSize);
                newBounds.Height = _cropActionStartBounds.Bottom - newBounds.Y;
                break;
            case ResizeMode.TopRight:
                newBounds.Width = Math.Clamp(_cropActionStartBounds.Width + deltaX, minSize, ActualWidth - _cropActionStartBounds.X);
                newBounds.Y = Math.Clamp(_cropActionStartBounds.Top + deltaY, 0, _cropActionStartBounds.Bottom - minSize);
                newBounds.Height = _cropActionStartBounds.Bottom - newBounds.Y;
                break;
            case ResizeMode.BottomLeft:
                newBounds.X = Math.Clamp(_cropActionStartBounds.Left + deltaX, 0, _cropActionStartBounds.Right - minSize);
                newBounds.Width = _cropActionStartBounds.Right - newBounds.X;
                newBounds.Height = Math.Clamp(_cropActionStartBounds.Height + deltaY, minSize, ActualHeight - _cropActionStartBounds.Y);
                break;
            case ResizeMode.BottomRight:
                newBounds.Width = Math.Clamp(_cropActionStartBounds.Width + deltaX, minSize, ActualWidth - _cropActionStartBounds.X);
                newBounds.Height = Math.Clamp(_cropActionStartBounds.Height + deltaY, minSize, ActualHeight - _cropActionStartBounds.Y);
                break;
            case ResizeMode.Move:
                newBounds.X = Math.Clamp(_cropActionStartBounds.X + deltaX, 0, ActualWidth - _cropActionStartBounds.Width);
                newBounds.Y = Math.Clamp(_cropActionStartBounds.Y + deltaY, 0, ActualHeight - _cropActionStartBounds.Height);
                break;
        }

        UpdateCropVisuals(newBounds);
    }

    private void CropCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        _cropResizeMode = ResizeMode.None;
        CropCanvas.ReleasePointerCapture(e.Pointer);
        this.ProtectedCursor = null;
    }

    private void UpdateCropVisuals(Rect cropRect)
    {
        // Update selection rectangle
        Canvas.SetLeft(CropRect, cropRect.X);
        Canvas.SetTop(CropRect, cropRect.Y);
        CropRect.Width = cropRect.Width;
        CropRect.Height = cropRect.Height;

        // Update overlay
        CropOverlayT.Height = cropRect.Top;
        CropOverlayT.Width = ActualWidth;

        CropOverlayB.Height = Math.Max(0, ActualHeight - cropRect.Bottom);
        CropOverlayB.Width = ActualWidth;
        Canvas.SetTop(CropOverlayB, cropRect.Bottom);

        CropOverlayL.Width = cropRect.Left;
        Canvas.SetTop(CropOverlayL, cropRect.Top);
        CropOverlayL.Height = cropRect.Height;

        CropOverlayR.Width = Math.Max(0, ActualWidth - cropRect.Right);
        Canvas.SetLeft(CropOverlayR, cropRect.Right);
        Canvas.SetTop(CropOverlayR, cropRect.Top);
        CropOverlayR.Height = cropRect.Height;

        // Update handles
        const double handleOffset = 5; // half of handle size
        Canvas.SetLeft(CropHandleTL, cropRect.Left - handleOffset);
        Canvas.SetTop(CropHandleTL, cropRect.Top - handleOffset);

        Canvas.SetLeft(CropHandleT, cropRect.Left + cropRect.Width / 2 - handleOffset);
        Canvas.SetTop(CropHandleT, cropRect.Top - handleOffset);

        Canvas.SetLeft(CropHandleTR, cropRect.Right - handleOffset);
        Canvas.SetTop(CropHandleTR, cropRect.Top - handleOffset);
        
        Canvas.SetLeft(CropHandleL, cropRect.Left - handleOffset);
        Canvas.SetTop(CropHandleL, cropRect.Top + cropRect.Height / 2 - handleOffset);

        Canvas.SetLeft(CropHandleR, cropRect.Right - handleOffset);
        Canvas.SetTop(CropHandleR, cropRect.Top + cropRect.Height / 2 - handleOffset);

        Canvas.SetLeft(CropHandleBL, cropRect.Left - handleOffset);
        Canvas.SetTop(CropHandleBL, cropRect.Bottom - handleOffset);

        Canvas.SetLeft(CropHandleB, cropRect.Left + cropRect.Width / 2 - handleOffset);
        Canvas.SetTop(CropHandleB, cropRect.Bottom - handleOffset);

        Canvas.SetLeft(CropHandleBR, cropRect.Right - handleOffset);
        Canvas.SetTop(CropHandleBR, cropRect.Bottom - handleOffset);

        // Update buttons
        Canvas.SetLeft(CropActionsPanel, cropRect.Left + cropRect.Width / 2 - CropActionsPanel.ActualWidth / 2);
        Canvas.SetTop(CropActionsPanel, cropRect.Top - CropActionsPanel.ActualHeight - 5);
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

        var tl = new Rect(cropRect.Left - handleSize / 2, cropRect.Top - handleSize / 2, handleSize, handleSize);
        var t = new Rect(cropRect.Left + cropRect.Width / 2 - handleSize / 2, cropRect.Top - handleSize / 2, handleSize, handleSize);
        var tr = new Rect(cropRect.Right - handleSize / 2, cropRect.Top - handleSize / 2, handleSize, handleSize);
        var l = new Rect(cropRect.Left - handleSize / 2, cropRect.Top + cropRect.Height/2 - handleSize/2, handleSize, handleSize);
        var r = new Rect(cropRect.Right - handleSize / 2, cropRect.Top + cropRect.Height / 2 - handleSize / 2, handleSize, handleSize);
        var bl = new Rect(cropRect.Left - handleSize / 2, cropRect.Bottom - handleSize / 2, handleSize, handleSize);
        var b = new Rect(cropRect.Left + cropRect.Width / 2 - handleSize / 2, cropRect.Bottom - handleSize / 2, handleSize, handleSize);
        var br = new Rect(cropRect.Right - handleSize / 2, cropRect.Bottom - handleSize / 2, handleSize, handleSize);

        if (tl.Contains(point)) return ResizeMode.TopLeft;
        if (tr.Contains(point)) return ResizeMode.TopRight;
        if (bl.Contains(point)) return ResizeMode.BottomLeft;
        if (br.Contains(point)) return ResizeMode.BottomRight;
        if (t.Contains(point)) return ResizeMode.Top;
        if (b.Contains(point)) return ResizeMode.Bottom;
        if (l.Contains(point)) return ResizeMode.Left;
        if (r.Contains(point)) return ResizeMode.Right;
        
        if (cropRect.Contains(point))
        {
            return ResizeMode.Move;
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
    }

    public void SetSelected(bool selected)
    {
        _isSelected = selected;
        VisualStateManager.GoToState(this, selected ? "Selected" : "Normal", true);
    }
} 