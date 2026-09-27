using Better_SignalRGB_Screen_Capture.Helpers;
using Better_SignalRGB_Screen_Capture.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Better_SignalRGB_Screen_Capture.Views;

public sealed partial class GroupSelectionControl : UserControl
{
    public IReadOnlyList<SourceItem> SelectedSources { get; private set; } = Array.Empty<SourceItem>();
    public event EventHandler? DragStarted;
    public event EventHandler<(double x, double y, double width, double height, double rotation)>? BoundsChanged;
    private double _groupRotation;
    private readonly RotateTransform _groupTransform = new();

    public GroupSelectionControl()
    {
        InitializeComponent();
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCanceled += OnInteractionCanceled;
        PointerCaptureLost += OnInteractionCanceled;
        PointerEntered += OnPointerMoved;
        PointerExited += (_, _) => { if (_gesture == null) ProtectedCursor = null; };
        RightTapped += OnRightTapped;
        Loaded += (_, _) => UpdateGroupBounds(resetRotation: true);
        Unloaded += (_, _) => EndGesture(save: true);
        SizeChanged += (_, _) => UpdateRotationHandle();
    }

    public void UpdateSelection(IEnumerable<SourceItem> selectedSources)
    {
        var updated = selectedSources.ToArray();
        var changed = !SelectedSources.SequenceEqual(updated);
        if (changed) EndGesture(save: false);
        SelectedSources = updated;
        if (updated.Any(source => source.IsLocked)) TryCancelInteraction();
        Visibility = updated.Length >= 2 ? Visibility.Visible : Visibility.Collapsed;
        var uniform = CanvasGroupTransform.RequiresUniformScale(updated.Select(CanvasSourceState.Capture).ToArray(), _groupRotation);
        var locked = updated.Any(source => source.IsLocked);
        GroupInfoText.Text = $"{updated.Length} items selected" + (locked ? " · locked" : uniform ? " · proportional resize" : "");
        foreach (var element in new FrameworkElement[] { TopLeftHandle, TopRightHandle, BottomLeftHandle, BottomRightHandle,
                     TopHandle, BottomHandle, LeftHandle, RightHandle, RotationHandleCanvas })
            element.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        if (_gesture == null) UpdateGroupBounds(changed);
    }

    private void UpdateGroupBounds(bool resetRotation)
    {
        if (SelectedSources.Count < 2) return;
        if (resetRotation) _groupRotation = 0;
        var localBounds = Rect.Empty;
        foreach (var source in SelectedSources)
        {
            var center = new Point(source.CanvasX + source.CanvasWidth / 2d, source.CanvasY + source.CanvasHeight / 2d);
            var localCenter = CanvasInteractionGeometry.Rotate(center, default, -_groupRotation);
            var visible = SourceGeometry.GetVisibleAreaAabb(source, -source.CanvasWidth / 2d, -source.CanvasHeight / 2d,
                source.CanvasWidth, source.CanvasHeight, source.Rotation - _groupRotation);
            if (visible.IsEmpty) continue;
            visible.X += localCenter.X; visible.Y += localCenter.Y;
            if (localBounds.IsEmpty) localBounds = visible; else localBounds.Union(visible);
        }
        if (localBounds.IsEmpty || localBounds.Width <= 0 || localBounds.Height <= 0) return;
        var worldCenter = CanvasInteractionGeometry.Rotate(CanvasInteractionGeometry.Center(localBounds), default, _groupRotation);
        PlaceGroup(new Rect(worldCenter.X - localBounds.Width / 2, worldCenter.Y - localBounds.Height / 2,
            localBounds.Width, localBounds.Height), _groupRotation);
        FireBoundsChangedEvent();
    }

    private void PlaceGroup(Rect bounds, double rotation)
    {
        CanvasInteractionVisuals.Place(this, bounds);
        _groupRotation = rotation;
        _groupTransform.Angle = rotation;
        RenderTransform = _groupTransform;
        RenderTransformOrigin = new Point(0.5, 0.5);
        UpdateRotationHandle();
    }

    private void UpdateRotationHandle()
    {
        if (!double.IsFinite(Width)) return;
        var point = CanvasInteractionGeometry.RotationHandlePosition(new Rect(0, 0, Width, Height));
        Canvas.SetLeft(RotationHandle, point.X - RotationHandle.Width / 2);
        Canvas.SetTop(RotationHandle, point.Y - RotationHandle.Height / 2);
        RotationHandleLine.X1 = RotationHandleLine.X2 = point.X;
        RotationHandleLine.Y1 = point.Y; RotationHandleLine.Y2 = 0;
    }

    private void FireBoundsChangedEvent() => BoundsChanged?.Invoke(this, GetGroupBounds());

    public (double x, double y, double width, double height, double rotation) GetGroupBounds() =>
        (Canvas.GetLeft(this), Canvas.GetTop(this), Width, Height, _groupRotation);

    public void SetGroupBounds(double x, double y, double width, double height, double rotation, bool maintainRelativePositions = true)
    {
        if (SelectedSources.Any(source => source.IsLocked) || SelectedSources.Count < 2 || !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) ||
            !double.IsFinite(height) || !double.IsFinite(rotation) || width <= 0 || height <= 0 || Width <= 0 || Height <= 0) return;
        var current = CanvasInteractionVisuals.Bounds(this);
        var target = new Rect(x, y, width, height);
        if (Math.Abs(current.X - x) < .001 && Math.Abs(current.Y - y) < .001 &&
            Math.Abs(current.Width - width) < .001 && Math.Abs(current.Height - height) < .001 && Math.Abs(_groupRotation - rotation) < .001) return;
        if (!maintainRelativePositions) { PlaceGroup(target, rotation); FireBoundsChangedEvent(); return; }
        var states = SelectedSources.Select(CanvasSourceState.Capture).ToArray();
        if (!TryApplyTransform(states, current, _groupRotation, target, rotation, clampPosition: true)) FireBoundsChangedEvent();
    }

    /// <summary>Pointer and property-panel edits share the same rounded candidates and boundary validation.</summary>
    private bool TryApplyTransform(IReadOnlyList<CanvasSourceState> states, Rect original, double originalRotation,
        Rect target, double rotation, bool clampPosition)
    {
        if (SelectedSources.Any(source => source.IsLocked) || Parent is not FrameworkElement parent || original.Width <= 0 || original.Height <= 0) return false;
        var available = new Size(parent.ActualWidth, parent.ActualHeight);
        if (CanvasGroupTransform.RequiresUniformScale(states, originalRotation))
        {
            var sx = target.Width / original.Width; var sy = target.Height / original.Height;
            var scale = Math.Abs(sx - 1) >= Math.Abs(sy - 1) ? sx : sy;
            var center = CanvasInteractionGeometry.Center(target);
            target = new Rect(center.X - original.Width * scale / 2, center.Y - original.Height * scale / 2,
                original.Width * scale, original.Height * scale);
        }
        CanvasSourceChange[] Candidates(Rect bounds) => states.Select(state =>
            CanvasGroupTransform.Calculate(state, original, originalRotation, bounds, rotation)).ToArray();
        bool Fits(CanvasSourceChange[] candidates) => candidates.All(change =>
            CanvasSourceResize.SupportedDimensions(new Rect(change.X, change.Y, change.Width, change.Height)) &&
            CanvasInteractionGeometry.Fits(change.VisibleBounds, available));
        var changes = Candidates(target);
        if (clampPosition)
        {
            var visible = Rect.Empty;
            foreach (var change in changes)
            {
                if (visible.IsEmpty) visible = change.VisibleBounds; else visible.Union(change.VisibleBounds);
            }
            // Constrain the children, not the empty corners of their rotated group frame.
            var minX = Math.Ceiling(-visible.Left); var maxX = Math.Floor(available.Width - visible.Right);
            var minY = Math.Ceiling(-visible.Top); var maxY = Math.Floor(available.Height - visible.Bottom);
            if (minX <= maxX && minY <= maxY)
            {
                target.X += Math.Clamp(0, minX, maxX); target.Y += Math.Clamp(0, minY, maxY);
                changes = Candidates(target);
            }
        }
        if (!Fits(changes))
        {
            if (Math.Abs(rotation - originalRotation) > .001) return false;
            var requested = target;
            target = CanvasInteractionGeometry.FurthestValid(t => new Rect(
                original.X + (requested.X - original.X) * t, original.Y + (requested.Y - original.Y) * t,
                original.Width + (requested.Width - original.Width) * t, original.Height + (requested.Height - original.Height) * t),
                bounds => Fits(Candidates(bounds)));
            changes = Candidates(target);
            if (!Fits(changes)) return false;
        }
        foreach (var change in changes) change.Apply();
        PlaceGroup(target, rotation);
        FireBoundsChangedEvent();
        return true;
    }
}
