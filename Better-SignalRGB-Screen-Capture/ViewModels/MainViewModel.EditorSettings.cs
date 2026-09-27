using CommunityToolkit.Mvvm.Input;

namespace Better_SignalRGB_Screen_Capture.ViewModels;

public partial class MainViewModel
{
    public event EventHandler? CanvasInteractionCancellationRequested;
    private const string SnappingSettingsKey = "CanvasSnappingEnabled";
    private bool _isSnappingEnabled = true;

    public bool IsSnappingEnabled
    {
        get => _isSnappingEnabled;
        set
        {
            if (SetProperty(ref _isSnappingEnabled, value) && !_isInitializing) _ = SaveSnappingAsync(value);
        }
    }

    private async Task SaveSnappingAsync(bool enabled)
    {
        try { await _localSettingsService.SaveSettingAsync(SnappingSettingsKey, enabled); }
        catch (Exception exception) { StatusMessage = $"Could not save snapping preference: {exception.Message}"; }
    }

    public bool CanTransformSelection => IsCanvasEditable && SelectedSources.Count > 0 && SelectedSources.All(source => !source.IsLocked);
    public bool CanTransformGroup => CanTransformSelection && IsMultiSelect;
    public bool SelectedSourcesLocked
    {
        get => SelectedSources.Count > 0 && SelectedSources.All(source => source.IsLocked);
        set
        {
            if (!IsCanvasEditable || !SelectedSources.Any(source => source.IsLocked != value)) return;
            // Restore uncommitted crop/gesture geometry before capturing the lock's undo state.
            CanvasInteractionCancellationRequested?.Invoke(this, EventArgs.Empty);
            SaveUndoState();
            foreach (var source in SelectedSources) source.IsLocked = value;
            _ = SaveSourcesAsync();
            NotifySelectionLocks();
        }
    }

    public string SelectionLockStatus => SelectedSources.Any(source => source.IsLocked)
        ? "Layout locked. Unlock every selected source to transform this selection. Copy and delete remain available; pasted copies start unlocked."
        : "Lock protects position, size, rotation, crop and layering.";

    [RelayCommand(CanExecute = nameof(IsSourceSelected))]
    private void ToggleSelectedLock() => SelectedSourcesLocked = !SelectedSourcesLocked;

    private void NotifySelectionLocks()
    {
        OnPropertyChanged(nameof(SelectedSourcesLocked));
        OnPropertyChanged(nameof(SelectionLockStatus));
        OnPropertyChanged(nameof(CanTransformSelection));
        OnPropertyChanged(nameof(CanTransformGroup));
        ToggleSelectedLockCommand.NotifyCanExecuteChanged();
        AlignLeftCommand.NotifyCanExecuteChanged(); AlignRightCommand.NotifyCanExecuteChanged(); AlignCenterCommand.NotifyCanExecuteChanged();
        AlignTopCommand.NotifyCanExecuteChanged(); AlignBottomCommand.NotifyCanExecuteChanged(); AlignMiddleCommand.NotifyCanExecuteChanged();
        BringToFrontCommand.NotifyCanExecuteChanged(); BringForwardCommand.NotifyCanExecuteChanged();
        SendToBackCommand.NotifyCanExecuteChanged(); SendBackwardCommand.NotifyCanExecuteChanged();
    }
}
