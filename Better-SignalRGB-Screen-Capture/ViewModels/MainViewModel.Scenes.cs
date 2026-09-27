using System.Collections.ObjectModel;
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Better_SignalRGB_Screen_Capture.ViewModels;

public partial class MainViewModel
{
    private readonly ISceneLibraryService _sceneLibrary;
    private readonly ISceneFilePickerService _sceneFiles;
    public ObservableCollection<SceneProfile> SceneProfiles { get; } = new();
    public Task ScenesInitialization { get; private set; } = Task.CompletedTask;
    [ObservableProperty] private SceneProfile? selectedScene;
    [ObservableProperty] private string sceneName = "New scene";
    [ObservableProperty] private string? sceneStatus;
    [ObservableProperty] private bool isSceneBusy;
    [ObservableProperty] private bool isSceneLibraryReady;

    public bool CanManageScenes => IsSceneLibraryReady && !IsSceneBusy && !_shuttingDown;
    public bool CanRetrySceneLibrary => !IsSceneLibraryReady && !IsSceneBusy && !_shuttingDown;
    public Task FlushScenesAsync() => _sceneLibrary.FlushAsync();
    public bool CanSaveNewScene => CanManageScenes && IsCanvasEditable && !string.IsNullOrWhiteSpace(SceneName);
    public bool CanUseSelectedScene => CanManageScenes && SelectedScene != null;
    public bool CanLoadScene => CanUseSelectedScene && IsCanvasEditable;
    public bool CanRenameScene => CanUseSelectedScene && !string.IsNullOrWhiteSpace(SceneName);

    partial void OnSelectedSceneChanged(SceneProfile? value)
    {
        if (value != null) SceneName = value.Name;
        NotifySceneCommands();
    }
    partial void OnSceneNameChanged(string value) => NotifySceneCommands();
    partial void OnIsSceneBusyChanged(bool value) => NotifySceneCommands();
    partial void OnIsSceneLibraryReadyChanged(bool value) => NotifySceneCommands();

    private void InitializeScenes()
    {
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(IsCanvasEditable)) NotifySceneCommands();
        };
        ScenesInitialization = LoadSceneLibraryAsync();
    }

    private void NotifySceneCommands()
    {
        OnPropertyChanged(nameof(CanManageScenes));
        OnPropertyChanged(nameof(CanUseSelectedScene));
        LoadSceneLibraryCommand.NotifyCanExecuteChanged();
        SaveNewSceneCommand.NotifyCanExecuteChanged(); ReplaceSceneCommand.NotifyCanExecuteChanged();
        LoadSceneCommand.NotifyCanExecuteChanged(); RenameSceneCommand.NotifyCanExecuteChanged();
        DeleteSceneCommand.NotifyCanExecuteChanged(); ImportSceneCommand.NotifyCanExecuteChanged();
        ExportSceneCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRetrySceneLibrary))]
    private async Task LoadSceneLibraryAsync()
    {
        if (IsSceneBusy || _shuttingDown) return;
        IsSceneBusy = true;
        try
        {
            await _sceneLibrary.InitializeAsync();
            RefreshSceneList(SelectedScene?.Id);
            IsSceneLibraryReady = true;
            SceneStatus = null;
        }
        catch (Exception ex) { SceneError("Could not load scene library", ex); }
        finally { IsSceneBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanSaveNewScene))]
    private Task SaveNewSceneAsync() => RunSceneActionAsync(async () =>
    {
        var sources = Sources.Select(SceneSourceSnapshot.Capture).ToArray();
        var saved = await _sceneLibrary.SaveNewAsync(SceneName, sources);
        RefreshSceneList(saved.Id);
        SceneStatus = $"Saved '{saved.Name}'.";
    });

    [RelayCommand(CanExecute = nameof(CanLoadScene))]
    private Task ReplaceSceneAsync() => RunSceneActionAsync(async () =>
    {
        if (SelectedScene is not { } selected) return;
        var saved = await _sceneLibrary.ReplaceAsync(selected.Id, Sources.Select(SceneSourceSnapshot.Capture).ToArray());
        RefreshSceneList(saved.Id);
        SceneStatus = $"Updated '{saved.Name}' with the current sources and layout.";
    });

    [RelayCommand(CanExecute = nameof(CanLoadScene))]
    private Task LoadSceneAsync() => RunSceneActionAsync(async () =>
    {
        if (SelectedScene is not { } selected) return;
        await Initialization;
        if (!IsCanvasEditable) return;
        // RestoreState owns the edit scope and capture stop/restart sequence used by undo.
        // Source IDs remain stable; runtime selection and preview state are re-established there.
        var sources = selected.Sources.Select(source => source.Restore()).ToArray();
        SaveUndoState();
        await RestoreState(sources);
        SceneStatus = $"Loaded '{selected.Name}'. Undo restores the previous canvas.";
    });

    [RelayCommand(CanExecute = nameof(CanRenameScene))]
    private Task RenameSceneAsync() => RunSceneActionAsync(async () =>
    {
        if (SelectedScene is not { } selected) return;
        var renamed = await _sceneLibrary.RenameAsync(selected.Id, SceneName);
        RefreshSceneList(renamed.Id);
        SceneStatus = $"Renamed scene to '{renamed.Name}'.";
    });

    [RelayCommand(CanExecute = nameof(CanUseSelectedScene))]
    private Task DeleteSceneAsync() => RunSceneActionAsync(async () =>
    {
        if (SelectedScene is not { } selected) return;
        await _sceneLibrary.DeleteAsync(selected.Id);
        RefreshSceneList(null);
        SceneStatus = $"Deleted saved scene '{selected.Name}'. The current canvas is unchanged.";
    });

    [RelayCommand(CanExecute = nameof(CanManageScenes))]
    private Task ImportSceneAsync() => RunSceneActionAsync(async () =>
    {
        var json = await _sceneFiles.ImportAsync();
        if (json == null || _shuttingDown) return;
        var imported = await _sceneLibrary.ImportAsync(json);
        RefreshSceneList(imported.Id);
        SceneStatus = $"Imported '{imported.Name}'. Select Load to use it.";
    });

    [RelayCommand(CanExecute = nameof(CanUseSelectedScene))]
    private Task ExportSceneAsync() => RunSceneActionAsync(async () =>
    {
        if (SelectedScene is not { } selected) return;
        var json = _sceneLibrary.Export(selected.Id);
        if (await _sceneFiles.ExportAsync(selected.Name, json)) SceneStatus = $"Exported '{selected.Name}'.";
    });

    private async Task RunSceneActionAsync(Func<Task> action)
    {
        if (!CanManageScenes) return;
        IsSceneBusy = true;
        SceneStatus = null;
        try { await action(); }
        catch (Exception ex) { SceneError("Could not complete scene operation", ex); }
        finally { IsSceneBusy = false; }
    }

    private void RefreshSceneList(Guid? selectedId)
    {
        SceneProfiles.Clear();
        foreach (var scene in _sceneLibrary.Profiles) SceneProfiles.Add(scene);
        SelectedScene = SceneProfiles.FirstOrDefault(scene => scene.Id == selectedId) ?? SceneProfiles.FirstOrDefault();
    }

    private void SceneError(string message, Exception exception) => StatusMessage = SceneStatus = $"{message}: {exception.Message}";
}
