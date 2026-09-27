using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;

namespace Better_SignalRGB_Screen_Capture.Helpers;

/// <summary>Clip media in source space before any image mirror transform.</summary>
internal sealed class CanvasVisualClip : IDisposable
{
    private readonly Visual _visual;
    private CanvasGeometry? _geometry;
    private CompositionPathGeometry? _path;
    private CompositionGeometricClip? _clip;

    public CanvasVisualClip(UIElement element) => _visual = ElementCompositionPreview.GetElementVisual(element);

    public void Set(IReadOnlyList<Point>? polygon)
    {
        _visual.Clip = null;
        _clip?.Dispose(); _path?.Dispose(); _geometry?.Dispose();
        _clip = null; _path = null; _geometry = null;
        if (polygon == null) return;
        _geometry = CanvasGeometry.CreatePolygon(CanvasDevice.GetSharedDevice(), polygon.Select(point => new Vector2((float)point.X, (float)point.Y)).ToArray());
        _path = _visual.Compositor.CreatePathGeometry(new CompositionPath(_geometry));
        _clip = _visual.Compositor.CreateGeometricClip(_path);
        _visual.Clip = _clip;
    }

    public void Dispose() => Set(null);
}
