using Better_SignalRGB_Screen_Capture.Models;

namespace BetterSignalRGB.ViewModelTests;

internal static class SceneViewModelTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        await using var context = new TestContext();
        var vm = context.ViewModel;
        await Task.WhenAll(vm.Initialization, vm.ScenesInitialization);
        check(vm.IsSceneLibraryReady && vm.SceneProfiles.Count == 0, "The scene library initializes without starting capture");
        var source = new SourceItem
        {
            Name = "Desktop", Type = SourceType.Monitor, MonitorDeviceId = "test-display",
            CanvasX = 30, CanvasY = 20, CanvasWidth = 120, CanvasHeight = 80, IsLocked = true,
            Rotation = 12, IsMirroredHorizontally = true
        };
        source.SetCrop(.1, .2, .3, .1, 28);
        vm.Sources.Add(source);
        vm.SceneName = "Desktop";
        await vm.SaveNewSceneCommand.ExecuteAsync(null);
        var desktop = vm.SelectedScene!;
        source.CanvasX = 45;
        var camera = new SourceItem { Name = "Camera", Type = SourceType.Webcam, WebcamDeviceId = "test-camera", CanvasX = 170 };
        vm.Sources.Add(camera);
        vm.SceneName = "Gaming";
        await vm.SaveNewSceneCommand.ExecuteAsync(null);
        var gaming = vm.SelectedScene!;
        check(desktop.Sources[0].CanvasX == 30 && gaming.Sources.Count == 2, "Saved scene snapshots do not track later canvas edits");
        vm.SelectedScene = desktop;
        await vm.LoadSceneCommand.ExecuteAsync(null);
        check(vm.Sources.Count == 1 && vm.Sources[0].Id == source.Id && vm.Sources[0].CanvasX == 30,
            "Loading replaces the complete composition and preserves source identity");
        check(vm.Sources[0].IsLocked && vm.Sources[0].CropRotation == 28 && vm.Sources[0].IsMirroredHorizontally,
            "Loading preserves lock, rotated crop and mirrors");
        check(!vm.IsRecording && !context.Http.IsStreaming && context.Capture.StartCounts.IsEmpty,
            "Loading a scene while stopped never starts capture");
        check(vm.SelectedSources.Count == 0 && vm.Sources.All(item => !item.IsSelected && item.IsLivePreviewEnabled == vm.IsPreviewing),
            "Loading clears selection and respects the current global preview setting");
        await vm.UndoCommand.ExecuteAsync(null);
        check(vm.Sources.Count == 2 && vm.Sources[0].CanvasX == 45 && vm.Sources[1].Id == camera.Id,
            "One undo restores the entire canvas that existed before loading");

        await vm.ToggleRecordingCommand.ExecuteAsync(null);
        vm.SelectedScene = desktop;
        await vm.LoadSceneCommand.ExecuteAsync(null);
        check(vm.IsRecording && !vm.IsPaused && context.Http.IsStreaming && context.Https.IsRunning,
            "Loading while recording preserves recording and server state");
        check(context.Capture.Active.Count == 1 && context.Capture.Active.ContainsKey(source.Id)
            && context.Capture.StartCounts[source.Id] == 2 && context.Capture.StartCounts[camera.Id] == 1,
            "Scene loading stops old sources and starts each replacement once");

        await vm.TogglePauseCommand.ExecuteAsync(null);
        var starts = context.Capture.StartCounts.Values.Sum();
        vm.SelectedScene = gaming;
        await vm.LoadSceneCommand.ExecuteAsync(null);
        check(vm.IsPaused && vm.IsRecording && context.Capture.Active.IsEmpty && !context.Http.IsStreaming,
            "Loading while paused preserves pause without running hidden captures");
        check(context.Capture.StartCounts.Values.Sum() == starts, "A paused scene switch starts no capture");
        await vm.TogglePauseCommand.ExecuteAsync(null);
        check(context.Capture.Active.Count == 2 && context.Capture.StartCounts[camera.Id] == 2,
            "Resuming starts the newly selected scene, including its webcam source");

        context.SceneStorage.FailWrites = true;
        vm.SceneName = "Rename failure";
        await vm.RenameSceneCommand.ExecuteAsync(null);
        check(vm.SelectedScene!.Name == "Gaming" && vm.SceneStatus!.Contains("failed", StringComparison.OrdinalIgnoreCase),
            "Failed scene writes leave the UI selection intact and expose the error");
        context.SceneStorage.FailWrites = false;
        await vm.ExportSceneCommand.ExecuteAsync(null);
        check(context.SceneFiles.ExportedJson!.Contains("BetterSignalRGB.Scene"), "Export uses the selected saved scene through the picker abstraction");
        var ids = vm.Sources.Select(item => item.Id).ToArray();
        context.SceneFiles.ImportJson = "{broken";
        await vm.ImportSceneCommand.ExecuteAsync(null);
        check(vm.SceneProfiles.Count == 2 && vm.Sources.Select(item => item.Id).SequenceEqual(ids),
            "Invalid imports leave both library and active capture composition unchanged");
        context.SceneFiles.ImportJson = context.SceneFiles.ExportedJson;
        await vm.ImportSceneCommand.ExecuteAsync(null);
        check(vm.SceneProfiles.Count == 3 && vm.SelectedScene!.Name == "Gaming (2)", "Valid imports add a new named scene without overwriting one");
        check(vm.Sources.Select(item => item.Id).SequenceEqual(ids), "Import alone never changes the active canvas");
        await vm.DeleteSceneCommand.ExecuteAsync(null);
        check(vm.SceneProfiles.Count == 2 && vm.Sources.Select(item => item.Id).SequenceEqual(ids),
            "Deleting a saved scene preserves the active canvas");

        context.Capture.StartGate = new AsyncGate();
        vm.SelectedScene = desktop;
        var loading = vm.LoadSceneCommand.ExecuteAsync(null);
        await context.Capture.StartGate.Entered;
        check(!vm.IsCanvasEditable && !vm.SaveNewSceneCommand.CanExecute(null) && !vm.LoadSceneCommand.CanExecute(null)
            && !vm.UndoCommand.CanExecute(null), "A scene switch holds the shared editing guard through asynchronous restart");
        context.Capture.StartGate.Release();
        await loading;
        context.Capture.StartGate = null;
        check(vm.IsCanvasEditable && context.Capture.Active.Count == 1, "A completed scene switch restores editing with one active source");
    }
}
