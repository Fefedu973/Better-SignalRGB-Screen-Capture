using Better_SignalRGB_Screen_Capture.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Better_SignalRGB_Screen_Capture.Views;

public sealed partial class DraggableSourceItem
{
    private Rect _visibleFrame;
    private CanvasVisualClip? _mediaClip;
    private (int Width, int Height, double Left, double Top, double Right, double Bottom, int Rotation, bool Editing)? _selectionShape;
    private (FrameworkElement Element, CanvasHandle Handle)[]? _resizeHandles;
    private (FrameworkElement Element, CanvasHandle Handle)[] ResizeHandles => _resizeHandles ??=
    [
        (TopLeftHandle, CanvasHandle.TopLeft), (TopRightHandle, CanvasHandle.TopRight),
        (BottomLeftHandle, CanvasHandle.BottomLeft), (BottomRightHandle, CanvasHandle.BottomRight),
        (TopHandle, CanvasHandle.Top), (BottomHandle, CanvasHandle.Bottom),
        (LeftHandle, CanvasHandle.Left), (RightHandle, CanvasHandle.Right)
    ];

    private void UpdateSelectionVisuals()
    {
        if (Source == null) return;
        var shape = (Source.CanvasWidth, Source.CanvasHeight, Source.CropLeftPct, Source.CropTopPct,
            Source.CropRightPct, Source.CropBottomPct, Source.CropRotation, _isCropping);
        if (_mediaClip == null || _selectionShape != shape)
        {
            _selectionShape = shape;
            var polygon = SourceGeometry.GetVisiblePolygon(Source, 0, 0, Source.CanvasWidth, Source.CanvasHeight, 0);
            (_mediaClip ??= new CanvasVisualClip(MediaContainer)).Set(_isCropping ? null : polygon);
            CropShadePath.Visibility = _isCropping ? Visibility.Visible : Visibility.Collapsed;
            _visibleFrame = SourceGeometry.Bounds(polygon);
            var path = new PathGeometry();
            if (polygon.Count >= 3)
            {
                var figure = new PathFigure { StartPoint = polygon[0], IsClosed = true, IsFilled = true };
                for (var i = 1; i < polygon.Count; i++) figure.Segments.Add(new LineSegment { Point = polygon[i] });
                path.Figures.Add(figure);
            }
            VisibleHitSurface.Data = path;
        }
        RotationHandleCanvas.Visibility = Source.IsSelected && !_isCropping && !_visibleFrame.IsEmpty
            ? Visibility.Visible : Visibility.Collapsed;
        if (_visibleFrame.IsEmpty) return;
        CanvasInteractionVisuals.Place(SelectionBorder, _visibleFrame);
        CanvasInteractionVisuals.Place(HoverBorder, _visibleFrame);
        foreach (var (element, handle) in ResizeHandles)
        {
            element.Visibility = Source.IsLocked ? Visibility.Collapsed : Visibility.Visible;
            var point = CanvasInteractionGeometry.HandlePosition(_visibleFrame, handle);
            var width = Math.Min(8, _visibleFrame.Width / 3);
            var height = Math.Min(8, _visibleFrame.Height / 3);
            CanvasInteractionVisuals.Place(element, new Rect(point.X - width / 2, point.Y - height / 2, width, height));
        }
        var rotationPoint = CanvasInteractionGeometry.RotationHandlePosition(_visibleFrame);
        RotationHandle.Visibility = RotationHandleLine.Visibility = Source.IsLocked ? Visibility.Collapsed : Visibility.Visible;
        LockBadge.Visibility = Source.IsLocked ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(LockBadge, _visibleFrame.Left + 3); Canvas.SetTop(LockBadge, _visibleFrame.Top + 3);
        Canvas.SetLeft(RotationHandle, rotationPoint.X - 8); Canvas.SetTop(RotationHandle, rotationPoint.Y - 8);
        RotationHandleLine.X1 = rotationPoint.X; RotationHandleLine.Y1 = rotationPoint.Y;
        RotationHandleLine.X2 = _visibleFrame.X + _visibleFrame.Width / 2; RotationHandleLine.Y2 = _visibleFrame.Top;
    }

    private Rect KeepAffordanceInsideCanvas(Rect local)
    {
        if (Source == null || Parent is not FrameworkElement parent) return local;
        var sourceCenter = new Point(Source.CanvasWidth / 2d, Source.CanvasHeight / 2d);
        var center = CanvasInteractionGeometry.Rotate(CanvasInteractionGeometry.Center(local), sourceCenter, Source.Rotation);
        center.X += Source.CanvasX; center.Y += Source.CanvasY;
        var world = SourceGeometry.GetRotatedAabb(center, new Size(local.Width, local.Height), Source.Rotation);
        var offset = CanvasInteractionGeometry.ContainmentOffset(world, new Size(parent.ActualWidth, parent.ActualHeight));
        var localOffset = CanvasInteractionGeometry.Rotate(offset, default, -Source.Rotation);
        local.X += localOffset.X; local.Y += localOffset.Y;
        return local;
    }
}
