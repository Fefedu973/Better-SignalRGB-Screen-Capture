using Better_SignalRGB_Screen_Capture.Models;

namespace Better_SignalRGB_Screen_Capture.Helpers;

internal enum LayerMove { Front, Back, Forward, Backward }

/// <summary>Index zero is the foreground. A selection always keeps its relative layer order.</summary>
internal static class SourceLayerOrdering
{
    public static SourceItem[] Reorder(IReadOnlyList<SourceItem> sources, IReadOnlySet<SourceItem> selected, LayerMove move)
    {
        if (move == LayerMove.Front)
            return sources.Where(selected.Contains).Concat(sources.Where(source => !selected.Contains(source))).ToArray();
        if (move == LayerMove.Back)
            return sources.Where(source => !selected.Contains(source)).Concat(sources.Where(selected.Contains)).ToArray();

        var result = sources.ToArray();
        if (move == LayerMove.Forward)
        {
            for (var index = 1; index < result.Length; index++)
                if (selected.Contains(result[index]) && !selected.Contains(result[index - 1]))
                    (result[index - 1], result[index]) = (result[index], result[index - 1]);
        }
        else
        {
            for (var index = result.Length - 2; index >= 0; index--)
                if (selected.Contains(result[index]) && !selected.Contains(result[index + 1]))
                    (result[index], result[index + 1]) = (result[index + 1], result[index]);
        }
        return result;
    }
}
