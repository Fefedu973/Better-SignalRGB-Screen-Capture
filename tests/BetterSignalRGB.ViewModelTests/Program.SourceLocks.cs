using Better_SignalRGB_Screen_Capture.Models;

namespace BetterSignalRGB.ViewModelTests;

internal static partial class Program
{
    private static async Task SourceLocksAsync()
    {
        await using var context = new TestContext();
        var vm = context.ViewModel; await vm.Initialization;
        var locked = new SourceItem { CanvasX = 30, CanvasY = 20, CanvasWidth = 80, CanvasHeight = 60 };
        var other = new SourceItem { CanvasX = 180, CanvasY = 70, CanvasWidth = 50, CanvasHeight = 40 };
        vm.Sources.Add(locked); vm.Sources.Add(other); vm.UpdateSelectedSources([locked]);
        locked.CropLeftPct = .2; // A crop gesture has temporarily changed the displayed mask.
        EventHandler cancelCrop = (_, _) => locked.CropLeftPct = 0;
        vm.CanvasInteractionCancellationRequested += cancelCrop;
        vm.SelectedSourcesLocked = true;
        vm.CanvasInteractionCancellationRequested -= cancelCrop;
        Check(locked.CropLeftPct == 0, "Locking cancels uncommitted canvas interaction before recording history");
        Check(locked.IsLocked && !vm.CanTransformSelection && vm.CanUndo, "Locking is undoable and blocks source transforms");
        await vm.UndoCommand.ExecuteAsync(null);
        locked = vm.Sources.Single(source => source.Id == locked.Id);
        Check(!locked.IsLocked, "Undo restores the unlocked state");
        Check(locked.CropLeftPct == 0, "Undoing the lock does not resurrect a provisional crop");
        await vm.RedoCommand.ExecuteAsync(null);
        locked = vm.Sources.Single(source => source.Id == locked.Id); other = vm.Sources.Single(source => source.Id == other.Id);
        vm.UpdateSelectedSources([locked]);
        Check(locked.IsLocked, "Redo restores the lock from the detached snapshot");
        var original = Geometry(locked);
        vm.SelectedSourceWidth = 140; vm.SelectedSourceHeight = 100;
        vm.SelectedSourceX = 70; vm.SelectedSourceY = 80; vm.SelectedSourceRotation = 45;
        vm.SelectedSourceCropLeftPct = 30; vm.SelectedSourceCropRotation = 40;
        await vm.NudgeSelectionAsync(10, 10);
        await vm.CenterSourceCommand.ExecuteAsync(locked);
        await vm.ToggleFlipHorizontalCommand.ExecuteAsync(null);
        vm.SendToBackCommand.Execute(null);
        Check(Geometry(locked).SequenceEqual(original) && ReferenceEquals(vm.Sources[0], locked),
            "Locked source ignores inspector, nudge, centering, mirror and layer commands");
        vm.SelectedSourceName = "Still editable"; vm.SelectedSourceOpacity = .5;
        Check(locked.Name == "Still editable" && locked.Opacity == .5, "Name and opacity remain editable under a layout lock");

        vm.UpdateSelectedSources([locked, other]);
        var otherGeometry = Geometry(other);
        await vm.NudgeSelectionAsync(5, 5); await vm.AlignLeftCommand.ExecuteAsync(null);
        await vm.CenterSourceCommand.ExecuteAsync(null); await vm.ToggleFlipVerticalCommand.ExecuteAsync(null);
        vm.SelectedSourceWidth = 20; vm.GroupSelectionWidth = 200;
        Check(!vm.CanTransformGroup && Geometry(other).SequenceEqual(otherGeometry) && Geometry(locked).SequenceEqual(original),
            "A mixed locked group blocks transforms for every member");
        Check(!vm.SelectedSourcesLocked && vm.SelectionLockStatus.Contains("Unlock every"), "Mixed locks are explained without pretending every source is locked");
        vm.SelectedSourcesLocked = false;
        Check(!locked.IsLocked && vm.CanTransformGroup, "Unlocking a mixed selection enables group editing");
        locked.IsLocked = true; vm.UpdateSelectedSources([locked]);
        vm.CopySourceCommand.Execute(new List<SourceItem> { locked });
        await vm.PasteSourceCommand.ExecuteAsync(null);
        var copy = vm.SelectedSources.Single();
        Check(copy.Id != locked.Id && !copy.IsLocked && locked.IsLocked, "Copy/paste creates an unlocked copy and preserves the protected original");
        await vm.DeleteSourceCommand.ExecuteAsync(locked);
        Check(!vm.Sources.Contains(locked), "Explicit deletion remains available for locked sources");
        vm.IsSnappingEnabled = false;
        await Task.Delay(30);
        Check(context.Settings.Writes.Any(write => write.Key == "CanvasSnappingEnabled"), "Snap preference is persisted independently of source history");
    }
}
