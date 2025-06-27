using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.ViewModels;

namespace Better_SignalRGB_Screen_Capture.Views;

public sealed partial class GroupSelectionControl : UserControl
{
    public IReadOnlyList<SourceItem> SelectedSources { get; private set; } = new List<SourceItem>();
    public event EventHandler<Point>? DragDelta;
    public event EventHandler? DragStarted;

    private bool _isDragging;
    private bool _isResizing;
    private ResizeMode _resizeMode = ResizeMode.None;
    private double _groupRotation;

    // Group transform state
    private Point _actionStartPointerPosition;
    private Rect _actionStartBounds;
    private double _actionStartRotation;
    private List<SourceItemState> _itemStates = new();

    private enum ResizeMode { None, TopLeft, TopRight, BottomLeft, BottomRight, Top, Bottom, Left, Right, Rotate, Move }

    private class SourceItemState
    {
        public SourceItem Source { get; set; } = null!;
        public Point RelativePosition { get; set; }
        public Size Size { get; set; }
        public double Rotation { get; set; }
        public Point OriginalPosition { get; set; }
        public Size OriginalSize { get; set; }
        public double OriginalRotation { get; set; }
    }

    public GroupSelectionControl()
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

    public void UpdateSelection(IEnumerable<SourceItem> selectedSources)
    {
        SelectedSources = selectedSources.ToList();
        
        if (SelectedSources.Count < 2)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        Visibility = Visibility.Visible;
        UpdateGroupBounds();
        UpdateInfoText();
    }

    private void UpdateInfoText()
    {
        GroupInfoText.Text = $"{SelectedSources.Count} items selected";
    }

    private void UpdateGroupBounds()
    {
        if (SelectedSources.Count < 2) return;

        // Reset rotation when bounds are recalculated for a new selection
        _groupRotation = 0;
        RenderTransform = null;

        // Calculate the bounding box of all selected items
        Rect groupBounds = Rect.Empty;
        foreach (var source in SelectedSources)
        {
            var itemCenter = new Point(source.CanvasX + source.CanvasWidth / 2.0, source.CanvasY + source.CanvasHeight / 2.0);
            var itemSize = new Size(source.CanvasWidth, source.CanvasHeight);
            var itemAabb = GetRotatedAabb(itemCenter, itemSize, source.Rotation);

            if (groupBounds.IsEmpty)
            {
                groupBounds = itemAabb;
            }
            else
            {
                groupBounds.Union(itemAabb);
            }
        }

        // Position and size the group control
        Canvas.SetLeft(this, groupBounds.X);
        Canvas.SetTop(this, groupBounds.Y);
        Width = groupBounds.Width;
        Height = groupBounds.Height;

        // Position rotation handle
        var handleX = groupBounds.Width / 2.0;
        Canvas.SetLeft(RotationHandle, handleX - (RotationHandle.Width / 2));
        Canvas.SetTop(RotationHandle, -40); // 40px above the group

        RotationHandleLine.X1 = handleX;
        RotationHandleLine.Y1 = -24; // Start from handle bottom
        RotationHandleLine.X2 = handleX;
        RotationHandleLine.Y2 = 0; // End at top of group

        // Store relative positions of items within the group
        _itemStates.Clear();
        var groupCenter = new Point(groupBounds.X + groupBounds.Width / 2, groupBounds.Y + groupBounds.Height / 2);
        
        foreach (var source in SelectedSources)
        {
            var itemCenter = new Point(source.CanvasX + source.CanvasWidth / 2.0, source.CanvasY + source.CanvasHeight / 2.0);
            var relativePos = new Point(itemCenter.X - groupCenter.X, itemCenter.Y - groupCenter.Y);
            
            _itemStates.Add(new SourceItemState
            {
                Source = source,
                RelativePosition = relativePos,
                Size = new Size(source.CanvasWidth, source.CanvasHeight),
                Rotation = source.Rotation,
                OriginalPosition = new Point(source.CanvasX, source.CanvasY),
                OriginalSize = new Size(source.CanvasWidth, source.CanvasHeight),
                OriginalRotation = source.Rotation
            });
        }
    }

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

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var parent = this.Parent as FrameworkElement;
        if (parent == null) return;

        _actionStartPointerPosition = e.GetCurrentPoint(parent).Position;
        _actionStartBounds = new Rect(Canvas.GetLeft(this), Canvas.GetTop(this), Width, Height);
        _actionStartRotation = _groupRotation;
        _resizeMode = GetResizeMode(e.GetCurrentPoint(this).Position);

        // Store original states
        foreach (var state in _itemStates)
        {
            state.OriginalPosition = new Point(state.Source.CanvasX, state.Source.CanvasY);
            state.OriginalSize = new Size(state.Source.CanvasWidth, state.Source.CanvasHeight);
            state.OriginalRotation = state.Source.Rotation;
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

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var parent = this.Parent as FrameworkElement;
        if (parent == null) return;
        
        var currentPoint = e.GetCurrentPoint(parent).Position;
        var isShiftDown = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift);

        if (_resizeMode == ResizeMode.Rotate)
        {
            ApplyGroupRotation(currentPoint, isShiftDown);
        }
        else if (_isResizing)
        {
            ApplyGroupResize(currentPoint, isShiftDown);
        }
        else if (_isDragging)
        {
            ApplyGroupMove(currentPoint);
        }
        else
        {
            var resizeMode = GetResizeMode(e.GetCurrentPoint(this).Position);
            ProtectedCursor = GetCursor(resizeMode);
        }
    }

    private void ApplyGroupRotation(Point currentPoint, bool snapToIncrements)
    {
        var centerPoint = new Point(_actionStartBounds.X + _actionStartBounds.Width / 2, _actionStartBounds.Y + _actionStartBounds.Height / 2);
        var startVector = new Point(_actionStartPointerPosition.X - centerPoint.X, _actionStartPointerPosition.Y - centerPoint.Y);
        var currentVector = new Point(currentPoint.X - centerPoint.X, currentPoint.Y - centerPoint.Y);

        var startAngle = Math.Atan2(startVector.Y, startVector.X) * (180.0 / Math.PI);
        var currentAngle = Math.Atan2(currentVector.Y, currentVector.X) * (180.0 / Math.PI);

        var angleDelta = currentAngle - startAngle;
        var newGroupRotation = _actionStartRotation + angleDelta;

        if (snapToIncrements)
        {
            newGroupRotation = Math.Round(newGroupRotation / 15.0) * 15.0; // Snap to 15-degree increments
        }

        // Check if rotation would cause overflow
        if (WouldGroupRotationCauseOverflow(newGroupRotation))
        {
            return; // Don't apply rotation if it overflows
        }

        _groupRotation = newGroupRotation;

        // Apply rotation to the group container itself
        var transform = new RotateTransform { Angle = _groupRotation };
        RenderTransform = transform;
        RenderTransformOrigin = new Point(0.5, 0.5);

        // Apply rotation to individual items
        ApplyGroupRotationToItems(_groupRotation - _actionStartRotation);
    }

    private void ApplyGroupRotationToItems(double totalAngleDelta)
    {
        var centerPoint = new Point(_actionStartBounds.X + _actionStartBounds.Width / 2, _actionStartBounds.Y + _actionStartBounds.Height / 2);
        var angleRad = totalAngleDelta * (Math.PI / 180.0);
        var cos = Math.Cos(angleRad);
        var sin = Math.Sin(angleRad);

        foreach (var state in _itemStates)
        {
            var originalItemCenter = new Point(
                state.OriginalPosition.X + state.OriginalSize.Width / 2,
                state.OriginalPosition.Y + state.OriginalSize.Height / 2);

            var vector = new Point(originalItemCenter.X - centerPoint.X, originalItemCenter.Y - centerPoint.Y);

            var rotatedX = vector.X * cos - vector.Y * sin;
            var rotatedY = vector.X * sin + vector.Y * cos;

            var newItemCenter = new Point(centerPoint.X + rotatedX, centerPoint.Y + rotatedY);

            state.Source.CanvasX = (int)Math.Round(newItemCenter.X - state.OriginalSize.Width / 2);
            state.Source.CanvasY = (int)Math.Round(newItemCenter.Y - state.OriginalSize.Height / 2);
            state.Source.Rotation = (int)(state.OriginalRotation + totalAngleDelta);
        }
    }

    private void ApplyGroupResize(Point currentPoint, bool keepAspect)
    {
        if (SelectedSources.Count == 0) return;

        /* ---------- 0. work in local coords --------------------------- */
        double θ = _actionStartRotation * Math.PI / 180.0;
        double c = Math.Cos(θ);
        double s = Math.Sin(θ);

        Point ToLocal(double x, double y) => new(x * c + y * s, -x * s + y * c);
        Point ToWorld(double x, double y) => new(x * c - y * s, x * s + y * c);

        var raw = new Point(currentPoint.X - _actionStartPointerPosition.X, currentPoint.Y - _actionStartPointerPosition.Y);
        var ΔL = ToLocal(raw.X, raw.Y);

        bool mL = _resizeMode is ResizeMode.Left or ResizeMode.TopLeft or ResizeMode.BottomLeft;
        bool mR = _resizeMode is ResizeMode.Right or ResizeMode.TopRight or ResizeMode.BottomRight;
        bool mT = _resizeMode is ResizeMode.Top or ResizeMode.TopLeft or ResizeMode.TopRight;
        bool mB = _resizeMode is ResizeMode.Bottom or ResizeMode.BottomLeft or ResizeMode.BottomRight;

        double w0 = _actionStartBounds.Width, h0 = _actionStartBounds.Height;
        double w1 = w0 + (mR ? ΔL.X : 0) - (mL ? ΔL.X : 0);
        double h1 = h0 + (mB ? ΔL.Y : 0) - (mT ? ΔL.Y : 0);

        w1 = Math.Max(50, w1);                      // group min-size
        h1 = Math.Max(50, h1);
        if (keepAspect) { double r = w0 / h0; if (Math.Abs(ΔL.X) >= Math.Abs(ΔL.Y)) h1 = w1 / r; else w1 = h1 * r; }

        int ax = mL ? +1 : mR ? -1 : 0;             // anchor (+1 = right, -1 = left)
        int ay = mT ? +1 : mB ? -1 : 0;             // anchor (+1 = bottom,-1 = top)

        Point a0 = new(ax * w0 / 2, ay * h0 / 2);
        Point a1 = new(ax * w1 / 2, ay * h1 / 2);

        Point c0 = new(_actionStartBounds.X + w0 / 2, _actionStartBounds.Y + h0 / 2);
        Point c1 = new(c0.X + ToWorld(a0.X - a1.X, a0.Y - a1.Y).X,
                       c0.Y + ToWorld(a0.X - a1.X, a0.Y - a1.Y).Y);

        /* ---------- 1. bounds-check entire group ---------------------- */
        var parent = Parent as FrameworkElement;
        var aabb = GetRotatedAabb(c1, new Size(w1, h1), _actionStartRotation);
        if (aabb.Left < 0 || aabb.Top < 0 ||
            aabb.Right > parent!.ActualWidth || aabb.Bottom > parent.ActualHeight)
            return;                                // refuse resize that overflows

        /* ---------- 2. commit ---------------------------------------- */
        Canvas.SetLeft(this, c1.X - w1 / 2);
        Canvas.SetTop(this, c1.Y - h1 / 2);
        Width = w1; Height = h1;

        double sx = w1 / w0, sy = h1 / h0;
        foreach (var st in _itemStates)
        {
            var rp = new Point(st.RelativePosition.X * sx, st.RelativePosition.Y * sy);
            var wc = new Point(c1.X + rp.X, c1.Y + rp.Y);

            st.Source.CanvasX = (int)Math.Round(wc.X - st.Size.Width * sx / 2);
            st.Source.CanvasY = (int)Math.Round(wc.Y - st.Size.Height * sy / 2);
            st.Source.CanvasWidth = (int)Math.Round(st.Size.Width * sx);
            st.Source.CanvasHeight = (int)Math.Round(st.Size.Height * sy);
        }
    }

    private Rect CalculateNewGroupBounds(double deltaX, double deltaY, bool keepAspectRatio)
    {
        var newBounds = _actionStartBounds;
        const double minSize = 50;

        // Determine which edges move based on resize mode
        bool mL = _resizeMode is ResizeMode.Left or ResizeMode.TopLeft or ResizeMode.BottomLeft;
        bool mR = _resizeMode is ResizeMode.Right or ResizeMode.TopRight or ResizeMode.BottomRight;
        bool mT = _resizeMode is ResizeMode.Top or ResizeMode.TopLeft or ResizeMode.TopRight;
        bool mB = _resizeMode is ResizeMode.Bottom or ResizeMode.BottomLeft or ResizeMode.BottomRight;

        // Calculate new size
        double newWidth = _actionStartBounds.Width + (mR ? deltaX : 0) - (mL ? deltaX : 0);
        double newHeight = _actionStartBounds.Height + (mB ? deltaY : 0) - (mT ? deltaY : 0);

        // Enforce minimum size
        newWidth = Math.Max(minSize, newWidth);
        newHeight = Math.Max(minSize, newHeight);

        // Keep aspect ratio if not holding Shift
        if (keepAspectRatio)
        {
            double aspectRatio = _actionStartBounds.Width / _actionStartBounds.Height;
            if (Math.Abs(deltaX) >= Math.Abs(deltaY))
                newHeight = newWidth / aspectRatio;
            else
                newWidth = newHeight * aspectRatio;
        }

        // Calculate new position based on which anchor should stay fixed
        var newX = _actionStartBounds.X;
        var newY = _actionStartBounds.Y;

        if (mL) newX = _actionStartBounds.Right - newWidth;
        if (mT) newY = _actionStartBounds.Bottom - newHeight;

        return new Rect(newX, newY, newWidth, newHeight);
    }

    private void ApplyGroupMove(Point currentPoint)
    {
        var deltaX = currentPoint.X - _actionStartPointerPosition.X;
        var deltaY = currentPoint.Y - _actionStartPointerPosition.Y;

        var newGroupX = _actionStartBounds.X + deltaX;
        var newGroupY = _actionStartBounds.Y + deltaY;

        // Check boundaries
        var parent = this.Parent as FrameworkElement;
        if (parent != null)
        {
            newGroupX = Math.Max(0, Math.Min(newGroupX, parent.ActualWidth - _actionStartBounds.Width));
            newGroupY = Math.Max(0, Math.Min(newGroupY, parent.ActualHeight - _actionStartBounds.Height));
        }

        // Calculate actual deltas after boundary clamping
        var actualDeltaX = newGroupX - _actionStartBounds.X;
        var actualDeltaY = newGroupY - _actionStartBounds.Y;

        // Move all items by the delta
        foreach (var state in _itemStates)
        {
            state.Source.CanvasX = (int)Math.Round(state.OriginalPosition.X + actualDeltaX);
            state.Source.CanvasY = (int)Math.Round(state.OriginalPosition.Y + actualDeltaY);
        }

        // Update group position
        Canvas.SetLeft(this, newGroupX);
        Canvas.SetTop(this, newGroupY);

        DragDelta?.Invoke(this, new Point(actualDeltaX, actualDeltaY));
    }

    private bool WouldGroupRotationCauseOverflow(double newRotation)
    {
        var parent = this.Parent as FrameworkElement;
        if (parent == null) return false;

        var groupCenter = new Point(_actionStartBounds.X + _actionStartBounds.Width / 2, _actionStartBounds.Y + _actionStartBounds.Height / 2);
        var groupSize = new Size(_actionStartBounds.Width, _actionStartBounds.Height);
        var rotatedAabb = GetRotatedAabb(groupCenter, groupSize, newRotation);

        return rotatedAabb.Left < 0 || rotatedAabb.Top < 0 || 
               rotatedAabb.Right > parent.ActualWidth || rotatedAabb.Bottom > parent.ActualHeight;
    }

    private bool WouldGroupResizeCauseOverflow(Rect newGroupBounds)
    {
        var parent = this.Parent as FrameworkElement;
        if (parent == null) return false;

        return newGroupBounds.Left < 0 || newGroupBounds.Top < 0 || 
               newGroupBounds.Right > parent.ActualWidth || newGroupBounds.Bottom > parent.ActualHeight;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isDragging = false;
        _isResizing = false;
        _resizeMode = ResizeMode.None;
        this.ReleasePointerCapture(e.Pointer);
        ProtectedCursor = null;

        // Save changes
        var mainPage = FindParent<MainPage>(this);
        if (mainPage?.ViewModel != null)
        {
            _ = mainPage.ViewModel.SaveSourcesAsync();
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
        var mainPage = FindParent<MainPage>(this);
        var viewModel = mainPage?.ViewModel;
        if (viewModel == null) return;

        var menuFlyout = new MenuFlyout();
        BuildGroupContextMenu(menuFlyout, viewModel);
        menuFlyout.ShowAt(this, e.GetPosition(this));
        e.Handled = true;
    }

    private void BuildGroupContextMenu(MenuFlyout menuFlyout, MainViewModel viewModel)
    {
        var copyItem = new MenuFlyoutItem { Text = "Copy", Icon = new FontIcon { Glyph = "\uE8C8" } };
        copyItem.Click += (s, a) => viewModel.CopySourceCommand.Execute(SelectedSources.ToList());
        menuFlyout.Items.Add(copyItem);

        var pasteItem = new MenuFlyoutItem { Text = "Paste", Icon = new FontIcon { Glyph = "\uE77F" }, IsEnabled = viewModel.CanPasteSource() };
        pasteItem.Click += async (s, a) => await viewModel.PasteSourceCommand.ExecuteAsync(null);
        menuFlyout.Items.Add(pasteItem);

        menuFlyout.Items.Add(new MenuFlyoutSeparator());

        var alignSubMenu = new MenuFlyoutSubItem { Text = "Align", Icon = new FontIcon { Glyph = "\uE834" } };
        BuildAlignMenuItems(alignSubMenu, viewModel);
        menuFlyout.Items.Add(alignSubMenu);

        var centerItem = new MenuFlyoutItem { Text = "Center", Icon = new FontIcon { Glyph = "\uE843" } };
        centerItem.Click += async (s, a) => await viewModel.CenterSourceCommand.ExecuteAsync(null);
        menuFlyout.Items.Add(centerItem);

        menuFlyout.Items.Add(new MenuFlyoutSeparator());

        var flipHorizontalItem = new MenuFlyoutItem { Text = "Flip Horizontally", Icon = new FontIcon { Glyph = "\uE7F7" } };
        flipHorizontalItem.Click += async (s, a) => await FlipGroup(true, false, viewModel);
        var flipVerticalItem = new MenuFlyoutItem { Text = "Flip Vertically", Icon = new FontIcon { Glyph = "\uE7F8" } };
        flipVerticalItem.Click += async (s, a) => await FlipGroup(false, true, viewModel);
        menuFlyout.Items.Add(flipHorizontalItem);
        menuFlyout.Items.Add(flipVerticalItem);

        menuFlyout.Items.Add(new MenuFlyoutSeparator());

        var deleteItem = new MenuFlyoutItem { Text = $"Delete {SelectedSources.Count} items", Icon = new FontIcon { Glyph = "\uE74D" } };
        deleteItem.Click += async (s, a) =>
        {
            var dialog = new ContentDialog
            {
                Title = "Delete Multiple Sources",
                Content = $"Are you sure you want to delete these {SelectedSources.Count} selected items?",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                XamlRoot = this.XamlRoot
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await viewModel.DeleteSourceCommand.ExecuteAsync(SelectedSources.ToList());
            }
        };
        menuFlyout.Items.Add(deleteItem);
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

    private async Task FlipGroup(bool horizontal, bool vertical, MainViewModel viewModel)
    {
        var groupCenter = new Point(Canvas.GetLeft(this) + Width / 2, Canvas.GetTop(this) + Height / 2);

        foreach (var source in SelectedSources)
        {
            var itemCenter = new Point(source.CanvasX + source.CanvasWidth / 2.0, source.CanvasY + source.CanvasHeight / 2.0);
            var relativePos = new Point(itemCenter.X - groupCenter.X, itemCenter.Y - groupCenter.Y);

            // Flip position relative to group center
            if (horizontal) relativePos = new Point(-relativePos.X, relativePos.Y);
            if (vertical) relativePos = new Point(relativePos.X, -relativePos.Y);

            var newItemCenter = new Point(groupCenter.X + relativePos.X, groupCenter.Y + relativePos.Y);
            var newItemPos = new Point(newItemCenter.X - source.CanvasWidth / 2, newItemCenter.Y - source.CanvasHeight / 2);

            source.CanvasX = (int)Math.Round(newItemPos.X);
            source.CanvasY = (int)Math.Round(newItemPos.Y);

            // Also flip the individual items
            if (horizontal) source.IsMirroredHorizontally = !source.IsMirroredHorizontally;
            if (vertical) source.IsMirroredVertically = !source.IsMirroredVertically;
        }

        await viewModel.SaveSourcesAsync();
        UpdateGroupBounds();
    }

    private ResizeMode GetResizeMode(Point point)
    {
        const int handleSize = 20; // Larger activation area for group handles
        
        // Transform point to unrotated coordinate space
        var unrotatedPoint = ToUnrotated(point);

        // Check for rotation first - explicit rotation handle
        var handleBounds = new Rect(Canvas.GetLeft(RotationHandle), Canvas.GetTop(RotationHandle), RotationHandle.Width, RotationHandle.Height);
        if (handleBounds.Contains(unrotatedPoint))
        {
            return ResizeMode.Rotate;
        }

        // Check corners
        if (new Rect(0, 0, handleSize, handleSize).Contains(unrotatedPoint)) return ResizeMode.TopLeft;
        if (new Rect(this.ActualWidth - handleSize, 0, handleSize, handleSize).Contains(unrotatedPoint)) return ResizeMode.TopRight;
        if (new Rect(0, this.ActualHeight - handleSize, handleSize, handleSize).Contains(unrotatedPoint)) return ResizeMode.BottomLeft;
        if (new Rect(this.ActualWidth - handleSize, this.ActualHeight - handleSize, handleSize, handleSize).Contains(unrotatedPoint)) return ResizeMode.BottomRight;
        
        // Check edges
        if (new Rect(0, 0, this.ActualWidth, handleSize).Contains(unrotatedPoint)) return ResizeMode.Top;
        if (new Rect(0, this.ActualHeight - handleSize, this.ActualWidth, handleSize).Contains(unrotatedPoint)) return ResizeMode.Bottom;
        if (new Rect(0, 0, handleSize, this.ActualHeight).Contains(unrotatedPoint)) return ResizeMode.Left;
        if (new Rect(this.ActualWidth - handleSize, 0, handleSize, this.ActualHeight).Contains(unrotatedPoint)) return ResizeMode.Right;
        
        return ResizeMode.None;
    }
    
    /// <summary>
    /// Converts a point from rotated coordinate space back to unrotated coordinate space
    /// </summary>
    private Point ToUnrotated(Point p)
    {
        if (RotateTransform == null || Math.Abs(RotateTransform.Angle) < 0.001) return p;
        
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

    private InputCursor GetCursor(ResizeMode resizeMode)
    {
        InputSystemCursorShape cursorShape;
        switch (resizeMode)
        {
            case ResizeMode.Rotate:
                return InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
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

    private T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parent = VisualTreeHelper.GetParent(child);
        if (parent == null) return null;
        if (parent is T parentOfType) return parentOfType;
        return FindParent<T>(parent);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (SelectedSources.Count >= 2)
        {
            UpdateGroupBounds();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SelectedSources.Count >= 2)
        {
            // Update rotation handle position when size changes
            var handleX = e.NewSize.Width / 2.0;
            Canvas.SetLeft(RotationHandle, handleX - (RotationHandle.Width / 2));
            Canvas.SetTop(RotationHandle, -40);

            RotationHandleLine.X1 = handleX;
            RotationHandleLine.Y1 = -24;
            RotationHandleLine.X2 = handleX;
            RotationHandleLine.Y2 = 0;
        }
    }
} 