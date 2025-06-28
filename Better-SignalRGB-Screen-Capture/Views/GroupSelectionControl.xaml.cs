using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
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
        UpdateGroupBounds(true);
        UpdateInfoText();
    }

    private void UpdateInfoText()
    {
        GroupInfoText.Text = $"{SelectedSources.Count} items selected";
    }

    private void UpdateGroupBounds(bool resetTransform)
    {
        if (SelectedSources.Count < 2) return;

        if (resetTransform)
        {
            _groupRotation = 0;
            RenderTransform = null;
        }

        /* ------------------------------------------------------------
         * ① Collect the extreme local coordinates
         * ------------------------------------------------------------ */
        double θg = _groupRotation * Math.PI / 180.0;
        double cg = Math.Cos(-θg);        // rotate by –θg  (to local space)
        double sg = Math.Sin(-θg);

        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;

        foreach (var s in SelectedSources)
        {
            // world-space centre of this child
            double cx = s.CanvasX + s.CanvasWidth  * .5;
            double cy = s.CanvasY + s.CanvasHeight * .5;

            double θi = s.Rotation * Math.PI / 180.0;
            double ci = Math.Cos(θi);
            double si = Math.Sin(θi);

            // local half-extents
            double hx = s.CanvasWidth  * .5;
            double hy = s.CanvasHeight * .5;

            // 4 corners in child-local space → rotate by θi → translate to world
            for (int dx = -1; dx <= 1; dx += 2)
            for (int dy = -1; dy <= 1; dy += 2)
            {
                double xw =  cx + (dx * hx) * ci - (dy * hy) * si;
                double yw =  cy + (dx * hx) * si + (dy * hy) * ci;

                // world → group-local   (rotate by –θg around origin)
                double xl = xw * cg - yw * sg;
                double yl = xw * sg + yw * cg;

                minX = Math.Min(minX, xl);
                minY = Math.Min(minY, yl);
                maxX = Math.Max(maxX, xl);
                maxY = Math.Max(maxY, yl);
            }
        }

        /* ------------------------------------------------------------
         * ② Size & centre of the group box in local space
         * ------------------------------------------------------------ */
        double w  = maxX - minX;
        double h  = maxY - minY;
        double cxL = (minX + maxX) * .5;
        double cyL = (minY + maxY) * .5;

        /* ------------------------------------------------------------
         * ③ Convert that centre back to world space
         * ------------------------------------------------------------ */
        double cw =  Math.Cos(θg);
        double sw =  Math.Sin(θg);

        double cxW = cxL * cw - cyL * sw;
        double cyW = cxL * sw + cyL * cw;

        /* ------------------------------------------------------------
         * ④ Position / size the control
         * ------------------------------------------------------------ */
        Width  = w;
        Height = h;
        Canvas.SetLeft(this, cxW - w * .5);
        Canvas.SetTop (this, cyW - h * .5);

        // make sure the RenderTransform matches the cached angle
        if (Math.Abs(_groupRotation) > 0.001)
        {
            RenderTransform = new RotateTransform { Angle = _groupRotation };
            RenderTransformOrigin = new Point(0.5, 0.5);
        }

        /* ------------------------------------------------------------
         * ⑤ Re-populate _itemStates (same as before)
         * ------------------------------------------------------------ */
        _itemStates.Clear();
        var groupCenter = new Point(cxW, cyW);

        foreach (var s in SelectedSources)
        {
            var itemCenter = new Point(s.CanvasX + s.CanvasWidth  * .5,
                                       s.CanvasY + s.CanvasHeight * .5);

            _itemStates.Add(new SourceItemState
            {
                Source          = s,
                RelativePosition= new Point(itemCenter.X - groupCenter.X,
                                            itemCenter.Y - groupCenter.Y),
                Size            = new Size(s.CanvasWidth, s.CanvasHeight),
                Rotation        = s.Rotation,
                OriginalPosition= new Point(s.CanvasX, s.CanvasY),
                OriginalSize    = new Size(s.CanvasWidth, s.CanvasHeight),
                OriginalRotation= s.Rotation
            });
        }

        /* ------------------------------------------------------------
         * ⑥ Re-place the rotation handle (unchanged)
         * ------------------------------------------------------------ */
        var handleX = Width / 2.0;
        Canvas.SetLeft(RotationHandle, handleX - RotationHandle.Width / 2);
        Canvas.SetTop (RotationHandle, -40);

        RotationHandleLine.X1 = handleX;  RotationHandleLine.Y1 = -24;
        RotationHandleLine.X2 = handleX;  RotationHandleLine.Y2 =   0;

        ClampInsideParent();    // makes sure programmatic updates also stay inside
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

    /// <summary>
    /// Shifts the group horizontally / vertically so its rotated AABB is fully
    /// contained in the parent canvas. Returns the offset that was applied.
    /// </summary>
    private Point ClampInsideParent()
    {
        if (Parent is not FrameworkElement parent) return new Point(0, 0);

        // current world-space centre of the group
        var centre = new Point(Canvas.GetLeft(this) + Width * .5,
                               Canvas.GetTop(this) + Height * .5);

        // AABB after the current RenderTransform
        var aabb = GetRotatedAabb(centre, new Size(Width, Height), _groupRotation);

        double dx = 0, dy = 0;
        if (aabb.Left < 0) dx = -aabb.Left;
        if (aabb.Right > parent.ActualWidth) dx = parent.ActualWidth - aabb.Right;
        if (aabb.Top < 0) dy = -aabb.Top;
        if (aabb.Bottom > parent.ActualHeight) dy = parent.ActualHeight - aabb.Bottom;

        if (Math.Abs(dx) > 0.001 || Math.Abs(dy) > 0.001)
        {
            Canvas.SetLeft(this, Canvas.GetLeft(this) + dx);
            Canvas.SetTop(this, Canvas.GetTop(this) + dy);

            // move every child by the same offset
            foreach (var st in _itemStates)
            {
                st.Source.CanvasX += (int)Math.Round(dx);
                st.Source.CanvasY += (int)Math.Round(dy);
            }
        }
        return new Point(dx, dy);
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

        ClampInsideParent();
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

    // Returns the new width / height that result from a non-uniform group scale
    // when the child rectangle is rotated by δ degrees relative to the group.
    private static (double w, double h) ScaleSizeRespectingRotation(
            Size original, double sx, double sy, double deltaAngleDeg)
    {
        var δ   = deltaAngleDeg * Math.PI / 180.0;
        var cos = Math.Cos(δ);
        var sin = Math.Sin(δ);

        // length of the child's local-X axis after the group scale
        var scaleW = Math.Sqrt(Math.Pow(sx * cos, 2) + Math.Pow(sy * sin, 2));
        // length of the child's local-Y axis after the group scale
        var scaleH = Math.Sqrt(Math.Pow(sx * sin, 2) + Math.Pow(sy * cos, 2));

        return (original.Width  * scaleW,
                original.Height * scaleH);
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

        // Calculate minimum group size based on the most constrained child item
        const double itemMinWidth = 100;
        const double itemMinHeight = 80;

        double minScaleX = 0;
        if (_itemStates.Any(st => st.OriginalSize.Width > 0))
        {
            minScaleX = _itemStates.Where(st => st.OriginalSize.Width > 0).Max(st => itemMinWidth / st.OriginalSize.Width);
        }

        double minScaleY = 0;
        if (_itemStates.Any(st => st.OriginalSize.Height > 0))
        {
            minScaleY = _itemStates.Where(st => st.OriginalSize.Height > 0).Max(st => itemMinHeight / st.OriginalSize.Height);
        }

        var minGroupW = _actionStartBounds.Width * minScaleX;
        var minGroupH = _actionStartBounds.Height * minScaleY;
        
        if (keepAspect)
        {
            double r = w0 / h0;
            // First, adjust based on dominant pointer movement
            if (Math.Abs(ΔL.X) >= Math.Abs(ΔL.Y))
            {
                h1 = w1 / r;
            }
            else
            {
                w1 = h1 * r;
            }

            // Now, check against minimums and readjust the other dimension if necessary
            if (w1 < minGroupW)
            {
                w1 = minGroupW;
                h1 = w1 / r;
            }
            if (h1 < minGroupH)
            {
                h1 = minGroupH;
                w1 = h1 * r;
            }
        }
        else
        {
            // No aspect ratio lock, just clamp to minimums
            w1 = Math.Max(minGroupW, w1);
            h1 = Math.Max(minGroupH, h1);
        }

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
            // 2-D scale of the rectangle's centre
            var rp = new Point(st.RelativePosition.X * sx, st.RelativePosition.Y * sy);
            var wc = new Point(c1.X + rp.X, c1.Y + rp.Y);

            // scale the rectangle itself, honouring its own rotation
            var (newW, newH) = ScaleSizeRespectingRotation(
                st.Size, sx, sy, st.Rotation - _actionStartRotation);

            st.Source.CanvasX = (int)Math.Round(wc.X - newW / 2);
            st.Source.CanvasY = (int)Math.Round(wc.Y - newH / 2);
            st.Source.CanvasWidth = (int)Math.Round(newW);
            st.Source.CanvasHeight = (int)Math.Round(newH);
        }

        // final safety – if the rotated frame sticks out, slide it back in
        ClampInsideParent();
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

        Canvas.SetLeft(this, newGroupX);
        Canvas.SetTop(this, newGroupY);
        var clamp = ClampInsideParent();   // may adjust X/Y further
        newGroupX = Canvas.GetLeft(this);
        newGroupY = Canvas.GetTop(this);

        // Calculate actual deltas after boundary clamping
        var actualDeltaX = newGroupX - _actionStartBounds.X;
        var actualDeltaY = newGroupY - _actionStartBounds.Y;

        // Move all items by the delta
        foreach (var state in _itemStates)
        {
            state.Source.CanvasX = (int)Math.Round(state.OriginalPosition.X + actualDeltaX);
            state.Source.CanvasY = (int)Math.Round(state.OriginalPosition.Y + actualDeltaY);
        }

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
        var didRotate = _resizeMode == ResizeMode.Rotate;   // ← capture **before** we reset it

        var wasTransforming = _isResizing || _isDragging || didRotate;
        _isDragging = false;
        _isResizing = false;
        _resizeMode = ResizeMode.None;
        this.ReleasePointerCapture(e.Pointer);
        ProtectedCursor = null;

        // --- refresh internal geometry ---
        if (wasTransforming)
        {
            UpdateGroupBounds(false);      // new maths keeps position perfect for both rotation and resize
        }

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

        var alignSubMenu = new MenuFlyoutSubItem { Text = "Align", Icon = new FontIcon { Glyph = "\uE139" } };
        BuildAlignMenuItems(alignSubMenu, viewModel);
        menuFlyout.Items.Add(alignSubMenu);

        var centerItem = new MenuFlyoutItem { Text = "Center", Icon = new FontIcon { Glyph = "\uF58A" } };
        centerItem.Click += async (s, a) => await viewModel.CenterSourceCommand.ExecuteAsync(null);
        menuFlyout.Items.Add(centerItem);

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

        flipHorizontalItem.Click += async (s, a) => await FlipGroup(true, false, viewModel);
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

    private async Task FlipGroup(bool horizontal, bool vertical, MainViewModel viewModel)
    {
        var sourcesToFlip = SelectedSources.ToList();
        if (!sourcesToFlip.Any()) return;

        viewModel.SaveUndoState();

        /* ------------------------------------------------------------
         * 1) Decide the *target* mirror state for the whole group
         *    (opposite of whatever the first item has right now)
         * ------------------------------------------------------------ */
        bool horizTarget = horizontal
            ? !sourcesToFlip[0].IsMirroredHorizontally
            : default;                // value won't be used
        bool vertTarget  = vertical
            ? !sourcesToFlip[0].IsMirroredVertically
            : default;

        /* ------------------------------------------------------------
         * 2) Stable pivot – same as before
         * ------------------------------------------------------------ */
        Rect groupBounds = Rect.Empty;
        foreach (var s in sourcesToFlip)
        {
            var c = new Point(s.CanvasX + s.CanvasWidth * .5,
                              s.CanvasY + s.CanvasHeight * .5);
            var sz = new Size(s.CanvasWidth, s.CanvasHeight);
            var box = GetRotatedAabb(c, sz, s.Rotation);

            if (groupBounds.IsEmpty)
            {
                groupBounds = box;
            }
            else
            {
                groupBounds.Union(box);
            }
        }
        var groupCenter = new Point(groupBounds.X + groupBounds.Width  * .5,
                                    groupBounds.Y + groupBounds.Height * .5);

        /* ------------------------------------------------------------
         * 3) Mirror every card around that pivot, and
         *    set the SAME mirror flag on every one of them
         * ------------------------------------------------------------ */
        foreach (var src in sourcesToFlip)
        {
            if (horizontal)
            {
                src.CanvasX = (int)Math.Round(
                    2 * groupCenter.X - src.CanvasX - src.CanvasWidth);
                src.IsMirroredHorizontally = horizTarget;
            }

            if (vertical)
            {
                src.CanvasY = (int)Math.Round(
                    2 * groupCenter.Y - src.CanvasY - src.CanvasHeight);
                src.IsMirroredVertically = vertTarget;
            }
        }

        await viewModel.SaveSourcesAsync();
        UpdateGroupBounds(true);
    }

    private ResizeMode GetResizeMode(Point point)
    {
        const int handleSize = 20; // Larger activation area for group handles
        
        // Pointer positions returned by GetCurrentPoint(this) are
        // already relative to the element's layout box (i.e. BEFORE
        // RenderTransform).  Never un-rotate them again!
        var p = point;

        // Check for rotation first - explicit rotation handle
        var handleBounds = new Rect(Canvas.GetLeft(RotationHandle),
                                    Canvas.GetTop(RotationHandle),
                                    RotationHandle.Width, RotationHandle.Height);
        if (handleBounds.Contains(p))
        {
            return ResizeMode.Rotate;
        }

        // Check corners
        if (new Rect(0, 0, handleSize, handleSize).Contains(p)) return ResizeMode.TopLeft;
        if (new Rect(this.ActualWidth - handleSize, 0, handleSize, handleSize).Contains(p)) return ResizeMode.TopRight;
        if (new Rect(0, this.ActualHeight - handleSize, handleSize, handleSize).Contains(p)) return ResizeMode.BottomLeft;
        if (new Rect(this.ActualWidth - handleSize, this.ActualHeight - handleSize, handleSize, handleSize).Contains(p)) return ResizeMode.BottomRight;
        
        // Check edges
        if (new Rect(0, 0, this.ActualWidth, handleSize).Contains(p)) return ResizeMode.Top;
        if (new Rect(0, this.ActualHeight - handleSize, this.ActualWidth, handleSize).Contains(p)) return ResizeMode.Bottom;
        if (new Rect(0, 0, handleSize, this.ActualHeight).Contains(p)) return ResizeMode.Left;
        if (new Rect(this.ActualWidth - handleSize, 0, handleSize, this.ActualHeight).Contains(p)) return ResizeMode.Right;
        
        return ResizeMode.None;
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
            UpdateGroupBounds(true);
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