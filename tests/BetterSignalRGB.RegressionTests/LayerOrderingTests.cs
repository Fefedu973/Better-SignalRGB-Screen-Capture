using Better_SignalRGB_Screen_Capture.Helpers;
using Better_SignalRGB_Screen_Capture.Models;

namespace BetterSignalRGB.RegressionTests;

internal static class LayerOrderingTests
{
    public static void Run()
    {
        var sources = "ABCDE".Select(name => new SourceItem { Name = name.ToString() }).ToArray();
        void Check(string selection, LayerMove move, string expected)
        {
            var selected = sources.Where(source => selection.Contains(source.Name!)).ToHashSet();
            var result = SourceLayerOrdering.Reorder(sources, selected, move);
            Assert.Equal(expected, string.Concat(result.Select(source => source.Name)), $"{move}: {selection}");
            Assert.Equal("ABCDE", string.Concat(sources.Select(source => source.Name)), "Reorder leaves its input unchanged");
        }
        Check("AB", LayerMove.Forward, "ABCDE");
        Check("DE", LayerMove.Backward, "ABCDE");
        Check("BC", LayerMove.Forward, "BCADE");
        Check("BC", LayerMove.Backward, "ADBCE");
        Check("BD", LayerMove.Forward, "BADCE");
        Check("BD", LayerMove.Backward, "ACBED");
        Check("BD", LayerMove.Front, "BDACE");
        Check("BD", LayerMove.Back, "ACEBD");
        foreach (var move in Enum.GetValues<LayerMove>())
        {
            Check("ABCDE", move, "ABCDE");
            Check("", move, "ABCDE");
        }
    }
}
