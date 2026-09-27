using Better_SignalRGB_Screen_Capture.Models;

namespace BetterSignalRGB.ViewModelTests;

internal static class SourceEditingTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        await using var context = new TestContext();
        var vm = context.ViewModel;
        await vm.Initialization;
        var source = new SourceItem
        {
            Name = "Document", Type = SourceType.Process, ProcessId = 123, ProcessPath = "C:\\Example.exe",
            WindowHandle = 456, WindowTitle = "First document", CanvasX = 42, CanvasWidth = 123, IsLocked = true
        };
        vm.Sources.Add(source);
        var callsBeforeEdit = context.Calls.Count;
        check(!await vm.ApplySourceEditAsync(source, candidate => { candidate.Name = "Rejected"; throw new ArgumentException("Invalid result"); }),
            "A rejected dialog result is reported as a failure");
        check(source.Name == "Document" && !vm.CanUndo && context.Calls.Count == callsBeforeEdit,
            "A rejected result leaves the live model, undo stack and capture untouched");
        check(!await vm.ApplySourceEditAsync(new SourceItem(), candidate => candidate.Name = "Missing"),
            "A dialog for a removed source cannot resurrect it");
        check(await vm.ApplySourceEditAsync(source, candidate => { candidate.WindowHandle = 789; candidate.WindowTitle = "Second document"; }),
            "A stopped source can be edited");
        check(context.Capture.StartCounts.IsEmpty && source.WindowHandle == 789 && source.WindowTitle == "Second document",
            "Editing while stopped preserves the stopped state and exact window selection");
        check(ReferenceEquals(vm.Sources[0], source) && source.CanvasX == 42 && source.CanvasWidth == 123 && source.IsLocked,
            "Editing keeps the source object, layout and lock state");
        await vm.UndoCommand.ExecuteAsync(null);
        source = vm.Sources[0];
        check(source.WindowHandle == 456 && source.WindowTitle == "First document", "One undo restores the previous exact window identity");
        await vm.ToggleRecordingCommand.ExecuteAsync(null);
        var liveMutation = false;
        source.PropertyChanged += (_, _) => liveMutation |= context.Capture.IsCapturing(source);
        string? titleAtStop = null;
        context.Capture.Stopped = stopped => titleAtStop = stopped.WindowTitle;
        check(await vm.ApplySourceEditAsync(source, candidate =>
        {
            candidate.Type = SourceType.Webcam;
            candidate.WebcamDeviceId = "camera";
            candidate.WebcamFormatId = "format-1920x1080";
            candidate.WindowTitle = "Changed";
        }), "An active source can change capture kind and parameters");
        check(titleAtStop == "First document" && !liveMutation, "Native capture is stopped before live configuration setters run");
        check(context.Capture.StartCounts[source.Id] == 2 && context.Capture.IsCapturing(source),
            "An active edited source restarts exactly once");
        check(source.WebcamFormatId == "format-1920x1080" && !vm.NeedsRefresh,
            "A selected camera format takes effect without a second manual refresh");
        context.Capture.Stopped = null;
        await vm.TogglePauseCommand.ExecuteAsync(null);
        await vm.ApplySourceEditAsync(source, candidate => candidate.WebcamFormatId = null);
        check(vm.IsPaused && context.Capture.StartCounts[source.Id] == 2 && !context.Capture.IsCapturing(source),
            "Editing a paused source preserves pause and does not restart capture");
        await vm.TogglePauseCommand.ExecuteAsync(null);
        var startsBeforeStop = context.Capture.StartCounts[source.Id];
        var gate = context.Capture.StopGate = new();
        var updating = vm.ApplySourceEditAsync(source, candidate => candidate.Name = "Edited before stop");
        await gate.Entered;
        check(!vm.IsCanvasEditable && !await vm.ApplySourceEditAsync(source, candidate => candidate.Name = "Concurrent"),
            "An in-flight edit blocks overlapping edits");
        var stopping = vm.ToggleRecordingCommand.ExecuteAsync(null);
        gate.Release();
        await Task.WhenAll(updating, stopping).WaitAsync(TimeSpan.FromSeconds(3));
        check(!vm.IsRecording && !context.Capture.IsCapturing(source) && context.Capture.StartCounts[source.Id] == startsBeforeStop,
            "A stop request during an edit wins over the delayed restart");
        check(source.Name == "Edited before stop" && vm.IsCanvasEditable, "The completed edit remains saved after stop and releases the editor");
        context.Capture.StopGate = null;
    }
}
