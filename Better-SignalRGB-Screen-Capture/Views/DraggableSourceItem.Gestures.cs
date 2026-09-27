using Better_SignalRGB_Screen_Capture.Helpers;
using Better_SignalRGB_Screen_Capture.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;
using ResizeMode = Better_SignalRGB_Screen_Capture.Helpers.CanvasHandle;

namespace Better_SignalRGB_Screen_Capture.Views;

public sealed partial class DraggableSourceItem
{
    private CanvasGesture? _gesture;
    private CanvasSourceState? _gestureState;
    private MainPage? _snapPage;
    private Rect[] _snapReferences = [];

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isCropping || _gesture != null || Source == null || Parent is not FrameworkElement parent ||
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var handle = GetResizeMode(e.GetCurrentPoint(this).Position);
        var multiSelect = (e.KeyModifiers & (VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift)) != 0;
        if (Source.IsLocked)
        {
            HandleSelection(multiSelect);
            e.Handled = true;
            return;
        }
        if (handle == ResizeMode.None && multiSelect)
        {
            HandleSelection(true);
            if (!Source.IsSelected) { e.Handled = true; return; }
        }
        if (handle != ResizeMode.None && !Source.IsSelected) HandleSelection(multiSelect);
        if (Source.IsSelected && App.GetService<MainViewModel>().SelectedSources.Any(source => source.IsLocked)) { e.Handled = true; return; }
        if (!CapturePointer(e.Pointer)) return;
        _snapPage = FindParent<MainPage>(this);
        _snapReferences = _snapPage?.GetSnapReferences([Source]) ?? [];
        _gestureState = CanvasSourceState.Capture(Source);
        _gesture = new CanvasGesture(e.Pointer.PointerId, handle == ResizeMode.None ? ResizeMode.Move : handle,
            e.GetCurrentPoint(parent).Position, new Rect(Source.CanvasX, Source.CanvasY, Source.CanvasWidth, Source.CanvasHeight), Source.Rotation);
        ProtectedCursor = GetCursor(_gesture.Handle);
        if (_gesture.Handle == ResizeMode.Move) DragStarted?.Invoke(this, EventArgs.Empty);
        FindParent<MainPage>(this)?.Focus(FocusState.Programmatic);
        e.Handled = true;
    }

    private void HandleSelection(bool multiSelect)
    {
        if (Source != null) FindParent<MainPage>(this)?.SelectSourceItem(Source, multiSelect);
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject => CanvasInteractionVisuals.FindParent<T>(child);

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isCropping || Source == null || Source.IsLocked) return;
        if (_gesture is not { } gesture)
        {
            ProtectedCursor = GetCursor(GetResizeMode(e.GetCurrentPoint(this).Position));
            return;
        }
        if (!gesture.Owns(e.Pointer.PointerId) || Parent is not FrameworkElement canvas) return;
        var current = e.GetCurrentPoint(canvas).Position;
        var shift = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift);
        var available = new Size(canvas.ActualWidth, canvas.ActualHeight);
        var snapEnabled = _snapPage?.ViewModel.IsSnappingEnabled == true && !e.KeyModifiers.HasFlag(VirtualKeyModifiers.Menu);
        var threshold = _snapPage?.CanvasSnapThresholds ?? CanvasSnapGeometry.Thresholds(1, 1, 1);
        IReadOnlyList<CanvasSnapGuide> guides = [];
        _snapPage?.ClearSnapGuides();
        if (gesture.Handle == ResizeMode.Rotate)
        {
            var pivotGesture = gesture with { StartBounds = CanvasSourceResize.VisibleFrame(Source, gesture.StartBounds, gesture.StartRotation) };
            var rotation = (int)Math.Round(CanvasInteractionGeometry.PointerRotation(pivotGesture, current, shift));
            var bounds = CanvasSourceResize.Rounded(CanvasSourceResize.Rotate(Source, gesture.StartBounds, gesture.StartRotation, rotation));
            var visible = SourceGeometry.GetVisibleAreaAabb(Source, bounds.X, bounds.Y, bounds.Width, bounds.Height, rotation);
            if (CanvasInteractionGeometry.Fits(visible, available))
            { Source.CanvasX = (int)bounds.X; Source.CanvasY = (int)bounds.Y; Source.Rotation = rotation; }
        }
        else if (gesture.Handle == ResizeMode.Move)
        {
            var x = gesture.StartBounds.X + current.X - gesture.StartPointer.X;
            var y = gesture.StartBounds.Y + current.Y - gesture.StartPointer.Y;
            if (snapEnabled)
            {
                var snap = CanvasSnapGeometry.Move(SourceGeometry.GetVisibleAreaAabb(Source, x, y,
                    Source.CanvasWidth, Source.CanvasHeight, Source.Rotation), _snapReferences, threshold);
                x += snap.Delta.X; y += snap.Delta.Y; guides = snap.Guides;
            }
            var position = CanvasSourceResize.ConstrainPosition(Source, x, y, available);
            Source.CanvasX = (int)position.X; Source.CanvasY = (int)position.Y;
        }
        else
        {
            var delta = new Point(current.X - gesture.StartPointer.X, current.Y - gesture.StartPointer.Y);
            if (snapEnabled)
            {
                var snap = CanvasSnapGeometry.Resize(delta, change =>
                {
                    var target = CanvasSourceResize.Resize(Source, gesture, change, shift);
                    return SourceGeometry.GetVisibleAreaAabb(Source, target.X, target.Y, target.Width, target.Height, gesture.StartRotation);
                }, _snapReferences, threshold);
                delta = snap.Delta; guides = snap.Guides;
            }
            var bounds = CanvasSourceResize.ResizeWithinCanvas(Source, gesture, delta, shift, available);
            Source.CanvasX = (int)bounds.X; Source.CanvasY = (int)bounds.Y;
            Source.CanvasWidth = (int)bounds.Width; Source.CanvasHeight = (int)bounds.Height;
        }
        _snapPage?.ShowSnapGuides(guides, SourceGeometry.GetVisibleAreaAabb(Source));
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_gesture?.Owns(e.Pointer.PointerId) != true) return;
        EndGesture(save: true);
        e.Handled = true;
    }

    private void EndGesture(bool save)
    {
        if (_gesture == null) return;
        var initial = _gestureState;
        var changed = initial?.HasChanged == true;
        if (save && changed)
        {
            var final = CanvasSourceState.Capture(initial!.Source);
            initial.Apply();
            App.GetService<MainViewModel>().SaveUndoState();
            final.Apply();
        }
        _gesture = null;
        _gestureState = null;
        _snapPage?.ClearSnapGuides(); _snapPage = null; _snapReferences = [];
        ReleasePointerCaptures();
        ProtectedCursor = null;
        if (save && changed) _ = App.GetService<MainViewModel>().SaveSourcesAsync();
    }

    public bool TryCancelInteraction()
    {
        if (_isCropping) { CancelCrop(); return true; }
        if (_gesture == null) return false;
        _gestureState?.Apply();
        EndGesture(save: false);
        _ = App.GetService<MainViewModel>().SaveSourcesAsync();
        return true;
    }

    private void OnPointerInteractionCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_gesture?.Owns(e.Pointer.PointerId) == true) { TryCancelInteraction(); e.Handled = true; }
        if (_cropGesture is { } crop && crop.Owns(e.Pointer.PointerId))
        {
            _cropGesture = null;
            UpdateCropVisuals(crop.StartBounds, crop.StartRotation);
        }
        ProtectedCursor = null;
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_gesture == null && !_isCropping) ProtectedCursor = GetCursor(GetResizeMode(e.GetCurrentPoint(this).Position));
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_gesture == null && _cropGesture == null) ProtectedCursor = null;
    }

    private ResizeMode GetResizeMode(Point point)
    {
        if (_isCropping || Source?.IsSelected != true || Source.IsLocked || _visibleFrame.IsEmpty) return ResizeMode.None;
        // This control rotates its RootGrid child. Unlike the group control, the
        // UserControl's pointer coordinate space therefore still needs this inverse.
        var local = CanvasInteractionGeometry.Rotate(point, new Point(ActualWidth / 2, ActualHeight / 2), -(Source?.Rotation ?? 0));
        if (CanvasInteractionVisuals.Bounds(RotationHandle).Contains(local)) return ResizeMode.Rotate;
        foreach (var (element, handle) in ResizeHandles)
            if (CanvasInteractionVisuals.Bounds(element).Contains(local)) return handle;
        var inFrame = new Point(local.X - _visibleFrame.X, local.Y - _visibleFrame.Y);
        return CanvasInteractionGeometry.HitTestFrame(inFrame, new Size(_visibleFrame.Width, _visibleFrame.Height),
            Rect.Empty, 12, 8);
    }

    private InputCursor GetCursor(ResizeMode mode) => CanvasInteractionVisuals.Cursor(mode,
        (Source?.Rotation ?? 0) + (_isCropping ? Source?.CropRotation ?? 0 : 0));
}
