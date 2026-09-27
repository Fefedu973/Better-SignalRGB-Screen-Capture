using System.Drawing;
using Better_SignalRGB_Screen_Capture.Core.Helpers;

namespace BetterSignalRGB.RegressionTests;

internal static class CanvasGeometryTests
{
    public static void Run()
    {
        Assert.True(CanvasSourceValidation.ValidateWebsite("https://example.com/path", 1920, 1080, 0) == null,
            "Valid web source accepted");
        foreach (var url in new[] { "", "example.com", "javascript:alert(1)", "data:text/html,hello", "ftp://example.com/file" })
            Assert.True(CanvasSourceValidation.ValidateWebsite(url, 1920, 1080, 0) != null,
                "Invalid web source scheme rejected");
        const string localUrl = "file:///C:/My%20media/%C3%A9cran%20%231.html";
        Assert.True(CanvasSourceValidation.ValidateWebsite(localUrl, 1920, 1080, 0) == null,
            "Local HTML file sources are accepted");
        Assert.True(CanvasSourceValidation.TryGetWebsiteUri(localUrl, out var localUri) &&
            localUri.IsFile && localUri.LocalPath.EndsWith("écran #1.html", StringComparison.Ordinal),
            "Local file addresses preserve spaces, Unicode and literal hashes");
        Assert.True(CanvasSourceValidation.ValidateWebsite("http://localhost:8080", double.NaN, 1080, 0) != null,
            "Empty viewport field rejected");
        Assert.True(CanvasSourceValidation.ValidateWebsite("https://example.com", 1920, 1080, double.NaN) != null,
            "Empty refresh field rejected");
        var groupOffset = CanvasGeometry.TransformGroupOffset(10, 20, 90, 90, 2, 1);
        Assert.Near(10, groupOffset.x, .0001, "Rotated group width resize preserves world horizontal offset");
        Assert.Near(40, groupOffset.y, .0001, "Rotated group width resize scales world vertical offset");
        var start = new CanvasBounds(50, 60, 200, 100);
        var side = CanvasGeometry.Resize(start, 40, 1000, 0, CanvasEdges.Right, 50, 40, true);
        Assert.Near(240, side.Width, .0001, "Horizontal ratio handle ignores vertical pointer noise");
        Assert.Near(120, side.Height, .0001, "Ratio is preserved");
        Assert.Near(start.X, side.X, .0001, "Opposite side remains anchored");
        Assert.Near(start.Y + start.Height / 2, side.Y + side.Height / 2, .0001, "Side resize keeps center axis");
        var minimum = CanvasGeometry.Resize(start, -1000, -1000, 0,
            CanvasEdges.Right | CanvasEdges.Bottom, 50, 40, true);
        Assert.Near(80, minimum.Width, .0001, "Minimum height increases width proportionally");
        Assert.Near(40, minimum.Height, .0001, "Minimum ratio height");
        var rotated = CanvasGeometry.Resize(start, 0, 40, 90, CanvasEdges.Right, 50, 40, false);
        Assert.Near(240, rotated.Width, .0001, "90 degree drag maps canvas Y to local width");
        Assert.Near(start.X + start.Width / 2, rotated.X + rotated.Width / 2, .0001, "Rotated center X");
        Assert.Near(start.Y + start.Height / 2 + 20, rotated.Y + rotated.Height / 2, .0001, "Rotated center Y");
        var topLeft = CanvasGeometry.Resize(start, 20, 10, 0,
            CanvasEdges.Top | CanvasEdges.Left, 50, 40, false);
        Assert.Near(start.X + start.Width, topLeft.X + topLeft.Width, .0001, "Opposite corner X anchored");
        Assert.Near(start.Y + start.Height, topLeft.Y + topLeft.Height, .0001, "Opposite corner Y anchored");

        var monitor = new Rectangle(-1920, 0, 1920, 1080);
        Assert.True(CanvasGeometry.TryMapRegion(new Rectangle(-2000, -100, 400, 300), monitor,
            out var source, out var destination, out var overlap), "Partially off-screen region overlaps");
        Assert.Equal(new Rectangle(0, 0, 320, 200), source, "Recorder region stays within monitor");
        Assert.Equal(new Point(80, 100), destination, "Off-screen padding preserves region origin");
        Assert.Equal(new Rectangle(-1920, 0, 320, 200), overlap, "Negative monitor coordinates preserved");

        var region = new Rectangle(-100, 20, 300, 100);
        Assert.True(CanvasGeometry.TryMapRegion(region, monitor, out var leftSource,
            out var leftDestination, out _), "Left display intersects");
        Assert.True(CanvasGeometry.TryMapRegion(region, new Rectangle(0, 0, 2560, 1440),
            out var rightSource, out var rightDestination, out _), "Right display intersects");
        Assert.Equal(100, leftSource.Width, "Left tile width");
        Assert.Equal(200, rightSource.Width, "Right tile width");
        Assert.Equal(leftDestination.X + leftSource.Width, rightDestination.X, "Monitor tiles meet without overlap");
        Assert.True(!CanvasGeometry.TryMapRegion(new Rectangle(0, 0, 0, 50), monitor,
            out _, out _, out _), "Zero-size region rejected");
        Assert.True(!CanvasGeometry.TryMapRegion(new Rectangle(0, 0, 100, 50), monitor,
            out _, out _, out _), "Merely touching monitor edge is not an overlap");

        Assert.Equal(new Rectangle(-1770, -225, 450, 150),
            CanvasGeometry.ToPhysicalRegion(100, 50, 300, 100, 1.5, -1920, -300),
            "DIP selection converts at 150 percent with negative desktop origin");
        var first = CanvasGeometry.ToPhysicalRegion(.2, 0, 1, 10, 1.25, 0, 0);
        var second = CanvasGeometry.ToPhysicalRegion(1.2, 0, 1, 10, 1.25, 0, 0);
        Assert.Equal(first.Right, second.Left, "Fractional DPI rounds shared edges consistently");
        Assert.Equal(Rectangle.Empty, CanvasGeometry.ToPhysicalRegion(0, 0, 100, 100, double.NaN, 0, 0),
            "Invalid rasterization scale rejected");
        Assert.Equal(Rectangle.Empty, CanvasGeometry.ToPhysicalRegion(double.NaN, 0, 100, 100, 1, 0, 0),
            "Invalid coordinates rejected");
    }
}
