using Better_SignalRGB_Screen_Capture.Models;

namespace Better_SignalRGB_Screen_Capture.Services.NativeOutput;

/// <summary>
/// Versioned rendering data for one effective native state. Transport generation/sequence
/// binding belongs to the publication envelope; it must never be inferred from frame age.
/// Coordinates use top-left origin, +X right, +Y down, and clockwise positive rotation.
/// </summary>
internal sealed record NativeRenderMetadata(
    int Version, string Schema, long StateRevision, int CanvasWidth, int CanvasHeight,
    int OutputWidth, int OutputHeight, SignalRgbEffectSettings EffectiveSettings,
    IReadOnlyList<NativeSourceGeometry> Sources)
{
    public const int LogicalWidth = 320;
    public const int LogicalHeight = 200;

    public static NativeRenderMetadata Create(long stateRevision, IReadOnlyList<StreamingSourceSnapshot> layout,
        IReadOnlySet<Guid> activeFrameIds, int outputWidth, int outputHeight, SignalRgbEffectSettings effective)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(activeFrameIds);
        ArgumentNullException.ThrowIfNull(effective);
        if (stateRevision < 0) throw new ArgumentOutOfRangeException(nameof(stateRevision));
        if (outputWidth is < 1 or > 8192 || outputHeight is < 1 or > 8192 || (long)outputWidth * outputHeight * 4 > 64 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(outputWidth));
        if (layout.Count > 128 || layout.Select(source => source.Id).Distinct().Count() != layout.Count)
            throw new ArgumentException("Native layouts require at most 128 unique source IDs.", nameof(layout));
        var settings = effective.Normalize();
        var sources = layout.Select((source, index) => (Source: source, Index: index))
            .OrderBy(item => item.Source.ZIndex).ThenBy(item => item.Index)
            .Select((item, order) => Build(item.Source, order, activeFrameIds.Contains(item.Source.Id), settings)).ToArray();
        return new(1, "better.native-rendering", stateRevision, LogicalWidth, LogicalHeight,
            outputWidth, outputHeight, settings, Array.AsReadOnly(sources));
    }

    private static NativeSourceGeometry Build(StreamingSourceSnapshot source, int order, bool hasFrame, SignalRgbEffectSettings settings)
    {
        if (source.CanvasWidth is < 1 or > 7680 || source.CanvasHeight is < 1 or > 4320 ||
            !double.IsFinite(source.Opacity) || source.Opacity is < 0 or > 1 ||
            new[] { source.CropLeftPct, source.CropTopPct, source.CropRightPct, source.CropBottomPct }
                .Any(value => !double.IsFinite(value) || value is < 0 or > 1) ||
            source.CropLeftPct + source.CropRightPct > 1 || source.CropTopPct + source.CropBottomPct > 1)
            throw new ArgumentException("Source geometry is not normalized.", nameof(source));

        var width = (double)source.CanvasWidth;
        var height = (double)source.CanvasHeight;
        var cropX = width * source.CropLeftPct;
        var cropY = height * source.CropTopPct;
        var cropWidth = width * (1 - source.CropLeftPct - source.CropRightPct);
        var cropHeight = height * (1 - source.CropTopPct - source.CropBottomPct);
        var cropCenter = new NativePoint(cropX + cropWidth / 2, cropY + cropHeight / 2);
        NativePoint[] crop = [new(cropX, cropY), new(cropX + cropWidth, cropY),
            new(cropX + cropWidth, cropY + cropHeight), new(cropX, cropY + cropHeight)];
        crop = crop.Select(point => Rotate(point, cropCenter, source.CropRotation)).ToArray();

        // Mirrors apply to media, never to the independent crop mask. Snapshot CSS applies
        // an inverse mirror to its clip to obtain this same geometry; do not mirror it twice.
        var localCoverage = Clip(crop, 0, 0, width, height);
        var center = new NativePoint(width / 2, height / 2);
        var canvasCoverage = Clip(localCoverage.Select(point =>
        {
            var rotated = Rotate(point, center, source.Rotation);
            return new NativePoint(rotated.X + source.CanvasX, rotated.Y + source.CanvasY);
        }).ToArray(), 0, 0, LogicalWidth, LogicalHeight);

        // Clip the source composition to its capture canvas BEFORE global picture placement.
        var placedCoverage = Clip(canvasCoverage.Select(point => new NativePoint(
            settings.ScreenX + point.X * settings.ScreenWidth / LogicalWidth,
            settings.ScreenY + point.Y * settings.ScreenHeight / LogicalHeight)).ToArray(), 0, 0, LogicalWidth, LogicalHeight);
        var angle = source.Rotation * Math.PI / 180;
        var sin = Math.Sin(angle); var cos = Math.Cos(angle);
        var sx = source.IsMirroredHorizontally ? -1 : 1;
        var sy = source.IsMirroredVertically ? -1 : 1;
        var transform = new NativeAffine(cos * sx, sin * sx, -sin * sy, cos * sy,
            source.CanvasX + width / 2 - cos * sx * width / 2 + sin * sy * height / 2,
            source.CanvasY + height / 2 - sin * sx * width / 2 - cos * sy * height / 2);
        return new(source.Id, source.Type, order, source.ZIndex, hasFrame,
            hasFrame && source.Opacity > 0 && canvasCoverage.Length >= 3,
            source.CanvasX, source.CanvasY, source.CanvasWidth, source.CanvasHeight,
            source.Rotation, source.Opacity, source.IsMirroredHorizontally, source.IsMirroredVertically,
            source.CropLeftPct, source.CropTopPct, source.CropRightPct, source.CropBottomPct, source.CropRotation,
            transform, Array.AsReadOnly(crop), Array.AsReadOnly(canvasCoverage), Array.AsReadOnly(placedCoverage));
    }

    private static NativePoint Rotate(NativePoint point, NativePoint center, double degrees)
    {
        var radians = degrees * Math.PI / 180; var sin = Math.Sin(radians); var cos = Math.Cos(radians);
        return new(center.X + (point.X - center.X) * cos - (point.Y - center.Y) * sin,
            center.Y + (point.X - center.X) * sin + (point.Y - center.Y) * cos);
    }

    private static NativePoint[] Clip(NativePoint[] polygon, double left, double top, double right, double bottom)
    {
        foreach (var edge in new[] { (Axis:0, Bound:left, Minimum:true), (Axis:0, Bound:right, Minimum:false),
            (Axis:1, Bound:top, Minimum:true), (Axis:1, Bound:bottom, Minimum:false) })
        {
            if (polygon.Length == 0) return [];
            var result = new List<NativePoint>(polygon.Length + 1);
            double Coordinate(NativePoint point) => edge.Axis == 0 ? point.X : point.Y;
            bool Inside(NativePoint point) => edge.Minimum ? Coordinate(point) >= edge.Bound : Coordinate(point) <= edge.Bound;
            var previous = polygon[^1]; var previousInside = Inside(previous);
            foreach (var current in polygon)
            {
                var currentInside = Inside(current);
                if (currentInside != previousInside)
                {
                    var fraction = (edge.Bound - Coordinate(previous)) / (Coordinate(current) - Coordinate(previous));
                    result.Add(new(previous.X + fraction * (current.X - previous.X), previous.Y + fraction * (current.Y - previous.Y)));
                }
                if (currentInside) result.Add(current);
                previous = current; previousInside = currentInside;
            }
            polygon = result.ToArray();
        }
        var cleaned = new List<NativePoint>(polygon.Length);
        foreach (var point in polygon)
            if (cleaned.Count == 0 || DistanceSquared(cleaned[^1], point) > 1e-18) cleaned.Add(point);
        if (cleaned.Count > 1 && DistanceSquared(cleaned[0], cleaned[^1]) <= 1e-18) cleaned.RemoveAt(cleaned.Count - 1);
        var area = 0d;
        for (var i = 0; i < cleaned.Count; i++)
        { var a = cleaned[i]; var b = cleaned[(i + 1) % cleaned.Count]; area += a.X * b.Y - b.X * a.Y; }
        return Math.Abs(area) < 1e-9 ? [] : cleaned.ToArray();
    }

    private static double DistanceSquared(NativePoint a, NativePoint b) => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);
}

internal sealed record NativeSourceGeometry(Guid Id, string Type, int Order, int ZIndex, bool HasFrame, bool Contributes,
    int CanvasX, int CanvasY, int CanvasWidth, int CanvasHeight, int Rotation, double Opacity,
    bool IsMirroredHorizontally, bool IsMirroredVertically,
    double CropLeftPct, double CropTopPct, double CropRightPct, double CropBottomPct, int CropRotation,
    NativeAffine CanvasFromImage, IReadOnlyList<NativePoint> CropPolygonLocal,
    IReadOnlyList<NativePoint> CoveragePolygon, IReadOnlyList<NativePoint> PlacedCoveragePolygon);

internal readonly record struct NativePoint(double X, double Y);

/// <summary>x' = M11*x + M21*y + Dx; y' = M12*x + M22*y + Dy. Input is source-sized local image coordinates.</summary>
internal readonly record struct NativeAffine(double M11, double M12, double M21, double M22, double Dx, double Dy);
