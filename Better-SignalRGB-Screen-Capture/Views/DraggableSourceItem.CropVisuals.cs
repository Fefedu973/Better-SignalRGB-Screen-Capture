using Better_SignalRGB_Screen_Capture.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using ResizeMode = Better_SignalRGB_Screen_Capture.Helpers.CanvasHandle;

namespace Better_SignalRGB_Screen_Capture.Views;

public sealed partial class DraggableSourceItem
{
    private (double width, double height, double left, double top, double right, double bottom, double rotation)? _lastShade;

    private void UpdateCropShadingWithValues(double left, double top, double right, double bottom, double rotation)
    {
        var shade = (ActualWidth, ActualHeight, left, top, right, bottom, rotation);
        if (_lastShade == shade || Source == null || ActualWidth <= 0 || ActualHeight <= 0) return;
        _lastShade = shade;
        var visible = SourceGeometry.GetVisiblePolygon(Source, 0, 0, ActualWidth, ActualHeight, 0);
        var hole = new PathGeometry();
        if (visible.Count >= 3)
        {
            var figure = new PathFigure { StartPoint = visible[0], IsClosed = true, IsFilled = true };
            for (var index = 1; index < visible.Count; index++) figure.Segments.Add(new LineSegment { Point = visible[index] });
            hole.Figures.Add(figure);
        }
        var shadeGeometry = new GeometryGroup { FillRule = FillRule.EvenOdd };
        shadeGeometry.Children.Add(new RectangleGeometry { Rect = new Rect(0, 0, ActualWidth, ActualHeight) });
        shadeGeometry.Children.Add(hole);
        CropShadePath.Data = shadeGeometry;
    }

    private void UpdateCropShading()
    {
        if (Source == null) return;

        UpdateCropShadingWithValues(Source.CropLeftPct, Source.CropTopPct, Source.CropRightPct, Source.CropBottomPct, Source.CropRotation);
    }

    private (FrameworkElement Element, ResizeMode Handle)[]? _cropHandles;
    private (FrameworkElement Element, ResizeMode Handle)[] CropHandles => _cropHandles ??=
    [
        (CropHandleTL, ResizeMode.TopLeft), (CropHandleTR, ResizeMode.TopRight),
        (CropHandleBL, ResizeMode.BottomLeft), (CropHandleBR, ResizeMode.BottomRight),
        (CropHandleT, ResizeMode.Top), (CropHandleB, ResizeMode.Bottom),
        (CropHandleL, ResizeMode.Left), (CropHandleR, ResizeMode.Right)
    ];

    private void UpdateCropVisuals(Rect cropRect, double rotationAngle = 0, bool updateSource = true)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        cropRect = CanvasInteractionGeometry.ConstrainCrop(cropRect, new Size(ActualWidth, ActualHeight));
        rotationAngle = Math.Round(rotationAngle);
        CanvasInteractionVisuals.Place(CropRect, cropRect);
        if (CropRect.RenderTransform is not RotateTransform transform)
            CropRect.RenderTransform = transform = new RotateTransform();
        transform.Angle = rotationAngle;
        transform.CenterX = cropRect.Width / 2; transform.CenterY = cropRect.Height / 2;
        var left = cropRect.Left / ActualWidth; var top = cropRect.Top / ActualHeight;
        var right = (ActualWidth - cropRect.Right) / ActualWidth;
        var bottom = (ActualHeight - cropRect.Bottom) / ActualHeight;
        if (updateSource && _cropSource is { } source)
        {
            _updatingCropProperties = true;
            try { source.SetCrop(left, top, right, bottom, (int)Math.Round(rotationAngle)); }
            finally { _updatingCropProperties = false; }
        }
        UpdateCropShadingWithValues(left, top, right, bottom, rotationAngle);
        foreach (var (element, handle) in CropHandles)
        {
            var point = CanvasInteractionGeometry.HandlePosition(cropRect, handle, rotationAngle);
            element.Width = Math.Min(10, cropRect.Width / 3); element.Height = Math.Min(10, cropRect.Height / 3);
            Canvas.SetLeft(element, point.X - element.Width / 2); Canvas.SetTop(element, point.Y - element.Height / 2);
        }
        var pivot = CanvasInteractionGeometry.Center(cropRect);
        var rotationPoint = CanvasInteractionGeometry.RotationHandlePosition(cropRect, rotationAngle);
        var lineEnd = CanvasInteractionGeometry.Rotate(new Point(pivot.X, cropRect.Top), pivot, rotationAngle);
        Canvas.SetLeft(CropRotationHandle, rotationPoint.X - CropRotationHandle.Width / 2);
        Canvas.SetTop(CropRotationHandle, rotationPoint.Y - CropRotationHandle.Height / 2);
        CropRotationHandleLine.X1 = rotationPoint.X; CropRotationHandleLine.Y1 = rotationPoint.Y;
        CropRotationHandleLine.X2 = lineEnd.X; CropRotationHandleLine.Y2 = lineEnd.Y;
        var actions = KeepAffordanceInsideCanvas(new Rect(pivot.X - CropActionsPanel.ActualWidth / 2,
            Math.Min(cropRect.Top - CropActionsPanel.ActualHeight - 5, rotationPoint.Y - 50),
            CropActionsPanel.ActualWidth, CropActionsPanel.ActualHeight));
        Canvas.SetLeft(CropActionsPanel, actions.X); Canvas.SetTop(CropActionsPanel, actions.Y);
    }

    private ResizeMode GetCropResizeMode(Point point) => CanvasInteractionVisuals.Bounds(CropRotationHandle).Contains(point)
        ? ResizeMode.CropRotate : CanvasInteractionGeometry.HitTestCrop(point,
            CanvasInteractionVisuals.Bounds(CropRect), (CropRect.RenderTransform as RotateTransform)?.Angle ?? 0, includeRotationHandle: false);

    private void CropActionsPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // When the panel's size is finally determined, re-run the visual update
        // to position it correctly. This solves the initial centering problem.
        if (_isCropping)
        {
            var transform = CropRect.RenderTransform as RotateTransform;
            UpdateCropVisuals(new Rect(Canvas.GetLeft(CropRect), Canvas.GetTop(CropRect),
                CropRect.Width, CropRect.Height), transform?.Angle ?? 0, updateSource: false);
        }
    }
}
