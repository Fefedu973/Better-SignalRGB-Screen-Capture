using Better_SignalRGB_Screen_Capture.Helpers;
using Better_SignalRGB_Screen_Capture.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Better_SignalRGB_Screen_Capture.Views;

public sealed partial class MainPage
{
    private Canvas? _snapGuideLayer;
    private readonly List<Line> _snapGuideLines = [];
    public double CanvasZoom => CanvasScrollViewer.ZoomFactor;
    internal Size CanvasSnapThresholds => CanvasSnapGeometry.Thresholds(CanvasZoom, ViewModel.CanvasScaleX, ViewModel.CanvasScaleY);

    internal Rect[] GetSnapReferences(IEnumerable<SourceItem> excluded)
    {
        var selection = excluded.ToHashSet();
        return new[] { new Rect(0, 0, SourceCanvas.Width, SourceCanvas.Height) }
            .Concat(ViewModel.Sources.Where(source => !selection.Contains(source)).Select(SourceGeometry.GetVisibleAreaAabb)).ToArray();
    }

    internal void ShowSnapGuides(IReadOnlyList<CanvasSnapGuide> guides, Rect actualBounds)
    {
        ClearSnapGuides();
        if (!ViewModel.IsSnappingEnabled || guides.Count == 0) return;
        if (_snapGuideLayer == null)
        {
            _snapGuideLayer = new Canvas { IsHitTestVisible = false, Width = SourceCanvas.Width, Height = SourceCanvas.Height };
            Canvas.SetZIndex(_snapGuideLayer, GuideOverlayZIndex);
            SourceCanvas.Children.Add(_snapGuideLayer);
            var brush = Application.Current.Resources.TryGetValue("SystemAccentColorBrush", out var value) && value is Brush accent
                ? accent : new SolidColorBrush(Microsoft.UI.Colors.DeepSkyBlue);
            for (var index = 0; index < 2; index++)
            {
                var line = new Line { IsHitTestVisible = false, Stroke = brush, StrokeDashArray = new DoubleCollection { 4, 3 }, Visibility = Visibility.Collapsed };
                _snapGuideLines.Add(line); _snapGuideLayer.Children.Add(line);
            }
        }
        var lineIndex = 0;
        foreach (var guide in guides.Where(guide => CanvasSnapGeometry.Matches(guide, actualBounds,
                     .75 / (CanvasZoom * (guide.Vertical ? ViewModel.CanvasScaleX : ViewModel.CanvasScaleY)))))
        {
            if (lineIndex == _snapGuideLines.Count) break;
            var line = _snapGuideLines[lineIndex++];
            line.StrokeThickness = 1.5 / (CanvasZoom * (guide.Vertical ? ViewModel.CanvasScaleX : ViewModel.CanvasScaleY));
            line.Visibility = Visibility.Visible;
            line.X1 = guide.Vertical ? guide.Position : 0; line.X2 = guide.Vertical ? guide.Position : SourceCanvas.Width;
            line.Y1 = guide.Vertical ? 0 : guide.Position; line.Y2 = guide.Vertical ? SourceCanvas.Height : guide.Position;
        }
    }

    internal void ClearSnapGuides()
    {
        foreach (var line in _snapGuideLines) line.Visibility = Visibility.Collapsed;
    }
}
