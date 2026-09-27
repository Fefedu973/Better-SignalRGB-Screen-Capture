using Better_SignalRGB_Screen_Capture.Helpers;
using Better_SignalRGB_Screen_Capture.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;

namespace Better_SignalRGB_Screen_Capture.Views;

public sealed partial class GroupSelectionControl
{
    private CanvasGesture? _gesture;
    private CanvasSourceState[] _gestureItems = [];
    private MainPage? _snapPage;
    private Rect[] _snapReferences = [];

    private CanvasHandle GetResizeMode(Point point) => CanvasInteractionGeometry.HitTestFrame(point,
        new Size(ActualWidth, ActualHeight), CanvasInteractionVisuals.Bounds(RotationHandle), 20, 20);

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (SelectedSources.Any(source => source.IsLocked)) { e.Handled = true; return; }
        if (_gesture != null || SelectedSources.Count < 2 || Parent is not FrameworkElement parent ||
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var handle = GetResizeMode(e.GetCurrentPoint(this).Position);
        if (!CapturePointer(e.Pointer)) return;
        _snapPage = CanvasInteractionVisuals.FindParent<MainPage>(this);
        _snapReferences = _snapPage?.GetSnapReferences(SelectedSources) ?? [];
        _gesture = new CanvasGesture(e.Pointer.PointerId, handle == CanvasHandle.None ? CanvasHandle.Move : handle,
            e.GetCurrentPoint(parent).Position, CanvasInteractionVisuals.Bounds(this), _groupRotation);
        _gestureItems = SelectedSources.Select(CanvasSourceState.Capture).ToArray();
        ProtectedCursor = CanvasInteractionVisuals.Cursor(_gesture.Handle, _groupRotation);
        if (_gesture.Handle == CanvasHandle.Move) DragStarted?.Invoke(this, EventArgs.Empty);
        CanvasInteractionVisuals.FindParent<MainPage>(this)?.Focus(FocusState.Programmatic);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (SelectedSources.Any(source => source.IsLocked)) { ProtectedCursor = null; return; }
        if (_gesture is not { } gesture)
        {
            ProtectedCursor = CanvasInteractionVisuals.Cursor(GetResizeMode(e.GetCurrentPoint(this).Position), _groupRotation);
            return;
        }
        if (!gesture.Owns(e.Pointer.PointerId) || Parent is not FrameworkElement parent) return;
        var current = e.GetCurrentPoint(parent).Position;
        var shift = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift);
        var target = gesture.StartBounds;
        var rotation = gesture.StartRotation;
        var snapEnabled = _snapPage?.ViewModel.IsSnappingEnabled == true && !e.KeyModifiers.HasFlag(VirtualKeyModifiers.Menu);
        var threshold = _snapPage?.CanvasSnapThresholds ?? CanvasSnapGeometry.Thresholds(1, 1, 1);
        IReadOnlyList<CanvasSnapGuide> guides = [];
        _snapPage?.ClearSnapGuides();
        Rect VisibleBounds(Rect group)
        {
            var visible = Rect.Empty;
            foreach (var state in _gestureItems)
            {
                var item = CanvasGroupTransform.Calculate(state, gesture.StartBounds, gesture.StartRotation, group, gesture.StartRotation).VisibleBounds;
                if (visible.IsEmpty) visible = item; else visible.Union(item);
            }
            return visible;
        }
        switch (gesture.Handle)
        {
            case CanvasHandle.Rotate:
                rotation = CanvasInteractionGeometry.PointerRotation(gesture, current, shift);
                break;
            case CanvasHandle.Move:
                target.X += current.X - gesture.StartPointer.X;
                target.Y += current.Y - gesture.StartPointer.Y;
                if (snapEnabled)
                {
                    var snap = CanvasSnapGeometry.Move(VisibleBounds(target), _snapReferences, threshold);
                    target.X += snap.Delta.X; target.Y += snap.Delta.Y; guides = snap.Guides;
                }
                break;
            default:
                var minimum = CanvasGroupTransform.MinimumSize(_gestureItems, gesture.StartBounds, gesture.StartRotation);
                Rect Resize(Point delta) => CanvasInteractionGeometry.Resize(gesture.StartBounds, delta,
                    gesture.StartRotation, gesture.Handle, minimum.Width, minimum.Height,
                    shift || CanvasGroupTransform.RequiresUniformScale(_gestureItems, gesture.StartRotation));
                var delta = new Point(current.X - gesture.StartPointer.X, current.Y - gesture.StartPointer.Y);
                if (snapEnabled)
                {
                    var snap = CanvasSnapGeometry.Resize(delta, change => VisibleBounds(Resize(change)), _snapReferences, threshold);
                    delta = snap.Delta; guides = snap.Guides;
                }
                target = CanvasGroupTransform.ResizeWithinCanvas(_gestureItems, gesture, delta, shift,
                    new Size(parent.ActualWidth, parent.ActualHeight));
                break;
        }
        TryApplyTransform(_gestureItems, gesture.StartBounds, gesture.StartRotation, target, rotation, gesture.Handle == CanvasHandle.Move);
        var actual = Rect.Empty;
        foreach (var source in SelectedSources)
        { var visible = SourceGeometry.GetVisibleAreaAabb(source); if (actual.IsEmpty) actual = visible; else actual.Union(visible); }
        _snapPage?.ShowSnapGuides(guides, actual);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_gesture?.Owns(e.Pointer.PointerId) != true) return;
        EndGesture(save: true);
        e.Handled = true;
    }

    private void OnInteractionCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_gesture is not { } gesture || !gesture.Owns(e.Pointer.PointerId)) return;
        TryCancelInteraction();
        e.Handled = true;
    }

    public bool TryCancelInteraction()
    {
        if (_gesture is not { } gesture) return false;
        foreach (var state in _gestureItems) state.Apply();
        PlaceGroup(gesture.StartBounds, gesture.StartRotation);
        EndGesture(save: false);
        FireBoundsChangedEvent();
        _ = App.GetService<MainViewModel>().SaveSourcesAsync();
        return true;
    }

    private void EndGesture(bool save)
    {
        if (_gesture == null) return;
        var changed = _gestureItems.Any(state => state.HasChanged);
        if (save && changed)
        {
            var final = _gestureItems.Select(state => CanvasSourceState.Capture(state.Source)).ToArray();
            foreach (var state in _gestureItems) state.Apply();
            App.GetService<MainViewModel>().SaveUndoState();
            foreach (var state in final) state.Apply();
        }
        _gesture = null;
        _gestureItems = [];
        _snapPage?.ClearSnapGuides(); _snapPage = null; _snapReferences = [];
        ReleasePointerCaptures();
        ProtectedCursor = null;
        if (save && changed)
        {
            UpdateGroupBounds(resetRotation: false);
            _ = App.GetService<MainViewModel>().SaveSourcesAsync();
        }
    }
}
