using System.Drawing;
using System.Globalization;
using Better_SignalRGB_Screen_Capture.Models;


namespace Better_SignalRGB_Screen_Capture.Services;

/// <summary>Immutable layout copied on the UI thread before network/render workers use it.</summary>
internal sealed record StreamingSourceSnapshot(
    Guid Id, string Type, string WebsiteUrl, int CanvasX, int CanvasY,
    int CanvasWidth, int CanvasHeight, int Rotation, double Opacity,
    bool IsMirroredHorizontally, bool IsMirroredVertically,
    double CropLeftPct, double CropTopPct, double CropRightPct, double CropBottomPct,
    int CropRotation, int ZIndex)
{
    public static StreamingSourceSnapshot FromSource(SourceItem source, int zIndex) => new(
        source.Id, source.Type.ToString(), source.WebsiteUrl ?? string.Empty,
        source.CanvasX, source.CanvasY, source.CanvasWidth, source.CanvasHeight,
        source.Rotation, source.Opacity, source.IsMirroredHorizontally, source.IsMirroredVertically,
        source.CropLeftPct, source.CropTopPct, source.CropRightPct, source.CropBottomPct,
        source.CropRotation, zIndex);

    // The crop is a mask rotated about its own centre; the image itself is never rotated by CropRotation.
    public PointF[] GetCropPolygon()
    {
        var left = CanvasWidth * CropLeftPct;
        var top = CanvasHeight * CropTopPct;
        var width = Math.Max(0, CanvasWidth * (1 - CropLeftPct - CropRightPct));
        var height = Math.Max(0, CanvasHeight * (1 - CropTopPct - CropBottomPct));
        var centerX = left + width / 2;
        var centerY = top + height / 2;
        var radians = CropRotation * Math.PI / 180;
        var sin = Math.Sin(radians);
        var cos = Math.Cos(radians);
        return new[] { (-width / 2, -height / 2), (width / 2, -height / 2),
            (width / 2, height / 2), (-width / 2, height / 2) }
            .Select(p => new PointF((float)(centerX + p.Item1 * cos - p.Item2 * sin),
                (float)(centerY + p.Item1 * sin + p.Item2 * cos)))
            // The preview mirrors its media, not its mask. Undo the outer reflection for the clip polygon.
            .Select(p => new PointF(IsMirroredHorizontally ? CanvasWidth - p.X : p.X,
                IsMirroredVertically ? CanvasHeight - p.Y : p.Y)).ToArray();
    }

    public string OuterStyle => FormattableString.Invariant(
        $"position:absolute;left:{CanvasX}px;top:{CanvasY}px;width:{CanvasWidth}px;height:{CanvasHeight}px;opacity:{Opacity};z-index:{ZIndex};transform-origin:center center;transform:rotate({Rotation}deg) scale({(IsMirroredHorizontally ? -1 : 1)},{(IsMirroredVertically ? -1 : 1)})");

    public string CropStyle => "position:relative;width:100%;height:100%;overflow:hidden;clip-path:polygon(" +
        string.Join(",", GetCropPolygon().Select(p => FormattableString.Invariant(
            $"{p.X / Math.Max(1, CanvasWidth) * 100}% {p.Y / Math.Max(1, CanvasHeight) * 100}%"))) + ")";
}
