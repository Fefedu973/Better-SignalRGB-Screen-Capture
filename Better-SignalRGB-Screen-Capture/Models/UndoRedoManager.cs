using System.Collections.ObjectModel;

namespace Better_SignalRGB_Screen_Capture.Models;

/// <summary>Bounded detached snapshots; no JSON encoding on the UI gesture path.</summary>
public sealed class UndoRedoManager
{
    private readonly List<SourceItem[]> _undoStack = new();
    private readonly List<SourceItem[]> _redoStack = new();
    private const int MaxHistorySize = 50;
    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;
    public event EventHandler? CanUndoRedoChanged;

    public void SaveState(ObservableCollection<SourceItem> sources)
    {
        Push(_undoStack, sources);
        _redoStack.Clear();
        CanUndoRedoChanged?.Invoke(this, EventArgs.Empty);
    }

    public SourceItem[]? Undo(ObservableCollection<SourceItem> currentSources) =>
        Restore(_undoStack, _redoStack, currentSources);

    public SourceItem[]? Redo(ObservableCollection<SourceItem> currentSources) =>
        Restore(_redoStack, _undoStack, currentSources);

    private SourceItem[]? Restore(List<SourceItem[]> from, List<SourceItem[]> to, ObservableCollection<SourceItem> current)
    {
        if (from.Count == 0) return null;
        Push(to, current);
        var state = from[^1];
        from.RemoveAt(from.Count - 1);
        CanUndoRedoChanged?.Invoke(this, EventArgs.Empty);
        return state;
    }

    private static void Push(List<SourceItem[]> stack, IEnumerable<SourceItem> sources)
    {
        stack.Add(sources.Select(source => source.Clone(preserveId: true)).ToArray());
        if (stack.Count > MaxHistorySize) stack.RemoveAt(0);
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        CanUndoRedoChanged?.Invoke(this, EventArgs.Empty);
    }
}
