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

    private async Task<bool> RestoreState(SourceItem[] state, bool temporary = false, Guid? activeSceneId = null)
    {
        using var edit = BeginSourceEdit();
        if (edit == null) return false;
        if (!temporary) MarkNativeManualChange();
        _nativeSceneLoading = true;
        PublishNativeState();
        IsRecordingLoading = true;
        _isUndoRedoOperation = true;
        try
        {
            var restart = _captureRequested && IsRecording && !IsPaused;
            var stopRevision = _nativeStopRevision;
            await StopAllCapturesAsync(sceneTransition: true);
            Sources.Clear();
            SelectedSources.Clear();
            foreach (var source in state)
            {
                source.IsSelected = false;
                source.IsLivePreviewEnabled = IsPreviewing;
                Sources.Add(source);
            }
            _activeNativeSceneId = activeSceneId.HasValue && _sceneLibrary.Profiles.Any(scene => scene.Id == activeSceneId) ? activeSceneId : null;
            _nativeStateRevision++;
            // A restored layout remains valid even if a stream port becomes unavailable.
            // Persist it independently from capture so the next launch restores this state.
            await SaveSourcesAsync();
            if (restart && stopRevision == _nativeStopRevision && !_shuttingDown)
            {
                try { await StartAllCapturesAsync(); }
                catch
                {
                    // StartAllCaptures has already rolled back the native sessions and servers.
                    IsRecording = CanPause = IsPaused = false;
                    throw;
                }
            }
            return true;
        }
        finally
        {
            _isUndoRedoOperation = false;
            IsRecordingLoading = false;
            _nativeSceneLoading = false;
            PublishNativeState();
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
