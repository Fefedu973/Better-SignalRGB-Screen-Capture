using Better_SignalRGB_Screen_Capture.Models;
using CommunityToolkit.Mvvm.Input;

namespace Better_SignalRGB_Screen_Capture.ViewModels;

public partial class MainViewModel
{
    // Undo/Redo properties
    public bool CanUndo => _undoRedoManager.CanUndo && IsCanvasEditable;
    public bool CanRedo => _undoRedoManager.CanRedo && IsCanvasEditable;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        if (!IsCanvasEditable) return;
        var previousState = _undoRedoManager.Undo(Sources);
        if (previousState != null)
        {
            await RestoreState(previousState);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private async Task RedoAsync()
    {
        if (!IsCanvasEditable) return;
        var nextState = _undoRedoManager.Redo(Sources);
        if (nextState != null)
        {
            await RestoreState(nextState);
        }
    }

    private async Task RestoreState(SourceItem[] state)
    {
        using var edit = BeginSourceEdit();
        if (edit == null) return;
        IsRecordingLoading = true;
        _isUndoRedoOperation = true;
        try
        {
            var restart = IsRecording && !IsPaused;
            await StopAllCapturesAsync();
            Sources.Clear();
            SelectedSources.Clear();
            foreach (var source in state)
            {
                source.IsSelected = false;
                source.IsLivePreviewEnabled = IsPreviewing;
                Sources.Add(source);
            }
            // A restored layout remains valid even if a stream port becomes unavailable.
            // Persist it independently from capture so the next launch restores this state.
            await SaveSourcesAsync();
            if (restart)
            {
                try { await StartAllCapturesAsync(); }
                catch
                {
                    // StartAllCaptures has already rolled back the native sessions and servers.
                    IsRecording = CanPause = IsPaused = false;
                    throw;
                }
            }
        }
        finally
        {
            _isUndoRedoOperation = false;
            IsRecordingLoading = false;
        }
    }

    public void SaveUndoState()
    {
        if (!_isUndoRedoOperation)
        {
            _undoRedoManager.SaveState(Sources);
        }
    }
}
