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
        // Update position when canvas properties change
        if (e.PropertyName == nameof(SourceItem.CanvasX) || 
            e.PropertyName == nameof(SourceItem.CanvasY) ||
            e.PropertyName == nameof(SourceItem.CanvasWidth) || 
            e.PropertyName == nameof(SourceItem.CanvasHeight))
        {
            // Use Dispatcher to ensure UI updates happen on UI thread
            DispatcherQueue.TryEnqueue(RefreshPosition);
        }
        else if (e.PropertyName == nameof(SourceItem.IsSelected))
        {
            DispatcherQueue.TryEnqueue(() => SetSelected(Source.IsSelected));
        }
        else if (e.PropertyName == nameof(SourceItem.Rotation))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                RotateTransform.Angle = Source.Rotation;
            });
        }
        else if (e.PropertyName == nameof(SourceItem.CropLeftPct) ||
                 e.PropertyName == nameof(SourceItem.CropTopPct) ||
                 e.PropertyName == nameof(SourceItem.CropRightPct) ||
                 e.PropertyName == nameof(SourceItem.CropBottomPct))
        {
            DispatcherQueue.TryEnqueue(UpdateCropShading);
        }
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

        // ── Selection *after* knowing what we clicked ───────────────────────────
        // Only change selection if we did **not** hit a resize/rotate handle
        var isCtrlDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        var isShiftDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);

        if (_resizeMode == ResizeMode.None)              // plain click, allow normal
            HandleSelection(isCtrlDown || isShiftDown);  // Ctrl / Shift selection
        // else: keep whatever was already selected

        if (_resizeMode != ResizeMode.None)
        {
            this.CapturePointer(e.Pointer);
            ProtectedCursor = GetCursor(_resizeMode);
            if (_resizeMode == ResizeMode.Rotate)
            {
                _isResizing = false; // Ensure resizing logic isn't triggered
                _isDragging = false; // Ensure dragging logic isn't triggered
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

            Source.Rotation = (int)Math.Round(newAngle);
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

            // Confine to parent canvas
            newX = Math.Max(0, Math.Min(newX, parent.ActualWidth - _actionStartBounds.Width));
            newY = Math.Max(0, Math.Min(newY, parent.ActualHeight - _actionStartBounds.Height));
            
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

    private void ApplyResize(double dx, double dy, bool keepRatio)
    {
        if (Source is null || Parent is not FrameworkElement canvas) return;

        // Which edges move?
        bool moveL = _resizeMode is ResizeMode.Left   or ResizeMode.TopLeft    or ResizeMode.BottomLeft;
        bool moveR = _resizeMode is ResizeMode.Right  or ResizeMode.TopRight   or ResizeMode.BottomRight;
        bool moveT = _resizeMode is ResizeMode.Top    or ResizeMode.TopLeft    or ResizeMode.TopRight;
        bool moveB = _resizeMode is ResizeMode.Bottom or ResizeMode.BottomLeft or ResizeMode.BottomRight;

        double startW = _actionStartBounds.Width;
        double startH = _actionStartBounds.Height;
        
        // Prevent division by zero if height is zero
        if (startH == 0) return;
        double ratio  = startW / startH;

        // ---------- 1. raw size before clamping ---------------------------------
        double newW = startW + (moveR ?  dx : 0) - (moveL ? dx : 0);
        double newH = startH + (moveB ?  dy : 0) - (moveT ? dy : 0);

        newW = Math.Max(this.MinWidth,  newW);
        newH = Math.Max(this.MinHeight, newH);

        // ---------- 2. keep aspect ratio ----------------------------------------
        if (keepRatio)
        {
            bool widthDriven =
                (moveL ^ moveR) && !(moveT || moveB)           // pure L / R edge
                || (moveL || moveR) && (moveT || moveB) &&     // corner: take stronger delta
                   Math.Abs(dx) >= Math.Abs(dy);

            if (widthDriven)
                newH = newW / ratio;
            else
                newW = newH * ratio;
        }

        // ---------- 3. anchor opposite edges ------------------------------------
        double newX = _actionStartBounds.X + (moveL ? startW - newW : 0);
        double newY = _actionStartBounds.Y + (moveT ? startH - newH : 0);

        // ---------- 4. clamp to canvas (shrink, never shift anchored edge) ------
        void shrinkToFit()
        {
            // left
            if (newX < 0)
            {
                double overshoot = -newX;
                newW -= overshoot;
                newX  = 0;
            }
            // top
            if (newY < 0)
            {
                double overshoot = -newY;
                newH -= overshoot;
                newY  = 0;
            }
            // right
            if (newX + newW > canvas.ActualWidth)
            {
                double overshoot = newX + newW - canvas.ActualWidth;
                newW -= overshoot;
            }
            // bottom
            if (newY + newH > canvas.ActualHeight)
            {
                double overshoot = newY + newH - canvas.ActualHeight;
                newH -= overshoot;
            }
        }

        shrinkToFit();

        // while ratio-locked we must shrink the *other* axis too
        if (keepRatio)
        {
            // after first pass one axis may have shrunk; fix the other accordingly
            double fixW = newH * ratio;
            double fixH = newW / ratio;

            if (fixW <= newW) newW = fixW;     // width was trimmed
            else              newH = fixH;     // height was trimmed

            shrinkToFit();                     // one more safety pass
        }

        // ── enforce MinWidth/MinHeight again (may have fallen below after ratio) ──
        double minW = Math.Max(this.MinWidth, 10);
        double minH = Math.Max(this.MinHeight, 10);

        if (newW < minW)
        {
            newW = minW;
            if (keepRatio) newH = newW / ratio;
        }
        if (newH < minH)
        {
            newH = minH;
            if (keepRatio) newW = newH * ratio;
        }
        shrinkToFit();   // one last pass in case clamping pushed us out again

        // ---------- 5. commit ----------------------------------------------------
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
        
        HandleSelection(isCtrlDown || isShiftDown);

        var menuFlyout = new MenuFlyout();
        var viewModel = App.GetService<MainViewModel>();

        var editItem = new MenuFlyoutItem { Text = "Edit", Icon = new FontIcon { Glyph = "\uE70F" } };
        editItem.Click += (s, a) => EditRequested?.Invoke(this, a);
        menuFlyout.Items.Add(editItem);

        var copyItem = new MenuFlyoutItem { Text = "Copy", Icon = new FontIcon { Glyph = "\uE8C8" } };
        copyItem.Click += CopyMenuItem_Click;
        menuFlyout.Items.Add(copyItem);

        var pasteItem = new MenuFlyoutItem { Text = "Paste", Icon = new FontIcon { Glyph = "\uE77F" } };
        pasteItem.Click += PasteMenuItem_Click;
        menuFlyout.Items.Add(pasteItem);

        menuFlyout.Items.Add(new MenuFlyoutSeparator());

        var bringToFrontItem = new MenuFlyoutItem { Text = "Bring to Front", Icon = new FontIcon { Glyph = "\uE746" } };
        bringToFrontItem.Command = viewModel.BringToFrontCommand;
        menuFlyout.Items.Add(bringToFrontItem);

        var sendToBackItem = new MenuFlyoutItem { Text = "Send to Back", Icon = new FontIcon { Glyph = "\uE747" } };
        sendToBackItem.Command = viewModel.SendToBackCommand;
        menuFlyout.Items.Add(sendToBackItem);

        menuFlyout.Items.Add(new MenuFlyoutSeparator());
        
        // Alignment options for multi-select
        if (viewModel.IsMultiSelect)
        {
            var alignSubMenu = new MenuFlyoutSubItem { Text = "Align" };
            
            var alignLeft = new MenuFlyoutItem { Text = "Align Left", Command = viewModel.AlignLeftCommand };
            var alignCenter = new MenuFlyoutItem { Text = "Align Center", Command = viewModel.AlignCenterCommand };
            var alignRight = new MenuFlyoutItem { Text = "Align Right", Command = viewModel.AlignRightCommand };
            var alignTop = new MenuFlyoutItem { Text = "Align Top", Command = viewModel.AlignTopCommand };
            var alignMiddle = new MenuFlyoutItem { Text = "Align Middle", Command = viewModel.AlignMiddleCommand };
            var alignBottom = new MenuFlyoutItem { Text = "Align Bottom", Command = viewModel.AlignBottomCommand };
            
            alignSubMenu.Items.Add(alignLeft);
            alignSubMenu.Items.Add(alignCenter);
            alignSubMenu.Items.Add(alignRight);
            alignSubMenu.Items.Add(new MenuFlyoutSeparator());
            alignSubMenu.Items.Add(alignTop);
            alignSubMenu.Items.Add(alignMiddle);
            alignSubMenu.Items.Add(alignBottom);
            
            menuFlyout.Items.Add(alignSubMenu);
            menuFlyout.Items.Add(new MenuFlyoutSeparator());
        }

        var flipHorizontalItem = new MenuFlyoutItem { Text = "Flip Horizontal", Icon = new FontIcon { Glyph = "\uE7A7" } };
        flipHorizontalItem.Click += (s, a) => viewModel.ToggleFlipHorizontalCommand.Execute(null);
        menuFlyout.Items.Add(flipHorizontalItem);

        var flipVerticalItem = new MenuFlyoutItem { Text = "Flip Vertical", Icon = new FontIcon { Glyph = "\uE7A8" } };
        flipVerticalItem.Click += (s, a) => viewModel.ToggleFlipVerticalCommand.Execute(null);
        menuFlyout.Items.Add(flipVerticalItem);

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

        menuFlyout.ShowAt(this, e.GetPosition(this));
        e.Handled = true;
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
        SelectionBorder.Opacity = 0; // Hide selection visuals

        // Convert percentage to pixels for crop editing
        double left = ActualWidth * Source.CropLeftPct;
        double top = ActualHeight * Source.CropTopPct;
        double right = ActualWidth * Source.CropRightPct;
        double bottom = ActualHeight * Source.CropBottomPct;
        
        _cropStartRect = new Rect(left, top,
            Math.Max(0, ActualWidth - left - right),
            Math.Max(0, ActualHeight - top - bottom));

        UpdateCropVisuals(_cropStartRect);
    }

    private void ExitCropMode()
    {
        _isCropping = false;
        CropCanvas.Visibility = Visibility.Collapsed;
        SelectionBorder.Opacity = 1; // Restore selection visuals
        _cropResizeMode = ResizeMode.None;
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
        }
    }

    public void SetSelected(bool selected)
    {
        _isSelected = selected;
        VisualStateManager.GoToState(this, selected ? "Selected" : "Normal", true);
    }
} 