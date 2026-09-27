using Better_SignalRGB_Screen_Capture.Helpers;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using ResizeMode = Better_SignalRGB_Screen_Capture.Helpers.CanvasHandle;

namespace Better_SignalRGB_Screen_Capture.Views;

public sealed partial class DraggableSourceItem
{
    private SourceItem? _cropSource;
    private bool _isCropping => _cropSource != null;
    private bool _updatingCropProperties;
    private CanvasCropState _cropStart;
    private CanvasGesture? _cropGesture;

    private void EnterCropMode()
    {
        if (Source == null || Source.IsLocked || _isCropping || ActualWidth <= 0 || ActualHeight <= 0) return;
        EndGesture(save: true);
        _cropSource = Source;
        _cropStart = CanvasCropState.Capture(Source);
        CropCanvas.Visibility = Visibility.Visible;
        SelectionBorder.Visibility = RotationHandleCanvas.Visibility = Visibility.Collapsed;
        UpdateSelectionVisuals();
        UpdateCropVisuals(new Rect(ActualWidth * Source.CropLeftPct, ActualHeight * Source.CropTopPct,
            ActualWidth * (1 - Source.CropLeftPct - Source.CropRightPct), ActualHeight * (1 - Source.CropTopPct - Source.CropBottomPct)), Source.CropRotation, updateSource: false);
        AcceptCropButton.Focus(FocusState.Programmatic);
    }

    private void ExitCropMode()
    {
        _cropSource = null;
        _cropGesture = null;
        CropCanvas.ReleasePointerCaptures();
        CropCanvas.Visibility = Visibility.Collapsed;
        SelectionBorder.Visibility = Visibility.Visible;
        ProtectedCursor = null;
        _lastShade = null;
        UpdateCropShading();
        UpdateSelectionVisuals();
        if (IsLoaded) FindParent<MainPage>(this)?.Focus(FocusState.Programmatic);
    }

    private void AcceptCropButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cropSource is not { } edited) return;
        var final = CanvasCropState.Capture(edited);
        // Only a committed crop enters history. Build the undo snapshot from the
        // original mask, keeping any unrelated property edits made in the meantime.
        if (final != _cropStart)
        {
            _updatingCropProperties = true;
            try
            {
                _cropStart.Apply(edited);
                App.GetService<MainViewModel>().SaveUndoState();
                final.Apply(edited);
            }
            finally { _updatingCropProperties = false; }
        }
        if (final != _cropStart && _cropSource is { } source && FindParent<Canvas>(this) is { } canvas)
        {
            var visible = SourceGeometry.GetVisibleAreaAabb(source);
            var fit = Math.Min(1, Math.Min(canvas.ActualWidth / visible.Width, canvas.ActualHeight / visible.Height));
            if (fit < 1)
            {
                source.CanvasWidth = Math.Max(1, (int)Math.Floor(source.CanvasWidth * fit));
                source.CanvasHeight = Math.Max(1, (int)Math.Floor(source.CanvasHeight * fit));
            }
            var position = CanvasSourceResize.ConstrainPosition(source, source.CanvasX, source.CanvasY, new Size(canvas.ActualWidth, canvas.ActualHeight));
            source.CanvasX = (int)position.X; source.CanvasY = (int)position.Y;
        }
        ExitCropMode();
        _ = App.GetService<MainViewModel>().SaveSourcesAsync();
    }

    private void CancelCropButton_Click(object sender, RoutedEventArgs e) => CancelCrop();

    private void ResetCropButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isCropping) UpdateCropVisuals(new Rect(0, 0, ActualWidth, ActualHeight), 0);
    }

    private void OnInteractionKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_isCropping && e.Key is VirtualKey.Z or VirtualKey.Y &&
            Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        { CancelCrop(); e.Handled = true; return; }
        if (e.Key == VirtualKey.Escape && _isCropping) { CancelCrop(); e.Handled = true; }
        else if (e.Key == VirtualKey.Enter && _isCropping) { AcceptCropButton_Click(sender, e); e.Handled = true; }
        else if (_isCropping && e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)
        {
            var step = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down) ? 10 : 1;
            var bounds = CanvasInteractionVisuals.Bounds(CropRect);
            bounds.X += e.Key == VirtualKey.Left ? -step : e.Key == VirtualKey.Right ? step : 0;
            bounds.Y += e.Key == VirtualKey.Up ? -step : e.Key == VirtualKey.Down ? step : 0;
            UpdateCropVisuals(bounds, _cropSource!.CropRotation);
            e.Handled = true;
        }
    }

    private void CancelCrop()
    {
        if (_cropSource is not { } source) return;
        _updatingCropProperties = true;
        try { _cropStart.Apply(source); }
        finally { _updatingCropProperties = false; }
        ExitCropMode();
        _ = App.GetService<MainViewModel>().SaveSourcesAsync();
    }

    private void CropCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_isCropping || _cropGesture != null || !e.GetCurrentPoint(CropCanvas).Properties.IsLeftButtonPressed) return;
        for (var element = e.OriginalSource as DependencyObject; element != null && element != CropCanvas; element = VisualTreeHelper.GetParent(element))
            if (element is ButtonBase) return;
        var point = e.GetCurrentPoint(CropCanvas).Position;
        var handle = GetCropResizeMode(point);
        if (handle == ResizeMode.None) CancelCrop();
        else if (CropCanvas.CapturePointer(e.Pointer))
            _cropGesture = new CanvasGesture(e.Pointer.PointerId, handle, point, CanvasInteractionVisuals.Bounds(CropRect),
                (CropRect.RenderTransform as RotateTransform)?.Angle ?? 0);
        e.Handled = true;
    }

    private void CropCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isCropping) return;
        var current = e.GetCurrentPoint(CropCanvas).Position;
        if (_cropGesture is not { } gesture)
        { ProtectedCursor = GetCursor(GetCropResizeMode(current)); return; }
        if (!gesture.Owns(e.Pointer.PointerId)) return;
        var delta = new Point(current.X - gesture.StartPointer.X, current.Y - gesture.StartPointer.Y);
        var shift = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift);
        var bounds = gesture.StartBounds;
        var rotation = gesture.StartRotation;
        if (gesture.Handle == ResizeMode.CropRotate) rotation = CanvasInteractionGeometry.PointerRotation(gesture, current, shift);
        else if (gesture.Handle == ResizeMode.Move)
        {
            bounds.X += delta.X; bounds.Y += delta.Y;
        }
        else bounds = CanvasInteractionGeometry.Resize(gesture.StartBounds, delta, gesture.StartRotation, gesture.Handle,
            ActualWidth * .01, ActualHeight * .01, shift);
        UpdateCropVisuals(bounds, rotation);
        e.Handled = true;
    }

    private void CropCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_cropGesture?.Owns(e.Pointer.PointerId) != true) return;
        _cropGesture = null;
        CropCanvas.ReleasePointerCapture(e.Pointer);
        ProtectedCursor = null;
        e.Handled = true;
    }
}
