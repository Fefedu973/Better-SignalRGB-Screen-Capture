using Better_SignalRGB_Screen_Capture.Models;
using Windows.Graphics;

namespace BetterSignalRGB.ViewModelTests;

internal static partial class Program
{
    private static int _assertions;
    private static void Check(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("pipeline diagnostics counters and session races", () => PipelineDiagnosticsTests.RunAsync(Check)),
            ("settings initialization", SettingsInitializationAsync),
            ("high quality output and capture transitions", () => OutputQualityTests.RunAsync(Check)),
            ("scene library and capture transitions", () => SceneViewModelTests.RunAsync(Check)),
            ("native scene leases and manual precedence", () => NativeControlTests.RunAsync(Check)),
            ("source editing and exact device identity", () => SourceEditingTests.RunAsync(Check)),
            ("partial settings failure", OptionalSettingFailureAsync),
            ("record pause refresh stop", CaptureLifecycleAsync),
            ("shutdown during native start", ShutdownDuringStartAsync),
            ("stop during source add", StopDuringAddAsync),
            ("delete during native start", DeleteDuringStartAsync),
            ("busy paste preserves undo history", BusyPasteUndoAsync),
            ("runtime and server errors", CaptureErrorsAsync),
            ("undo redo region and selection", UndoRedoAsync),
            ("history and scene restore startup failures", () => HistoryRestartFailureTests.RunAsync(Check)),
            ("multi selection layers", LayerOrderingAsync),
            ("source locking and unlocked copies", SourceLocksAsync),
            ("mirrors preserve placement and crop", MirrorGeometryAsync),
            ("invalid and unchanged numeric edits", NumericEditGuardsAsync),
            ("rejected geometry preserves redo", RejectedGeometryAsync),
            ("cropped and grouped keyboard movement", NudgeSelectionAsync),
            ("detached debounced persistence", PersistenceAsync),
            ("copy paste and source cleanup", CopyPasteCleanupAsync),
            ("source availability retry and cancellation", AvailabilityAsync)
        };
        var failed = 0;
        foreach (var (name, run) in tests)
        {
            try { await run(); Console.WriteLine($"PASS: {name}"); }
            catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL: {name}: {ex}"); }
        }
        Console.WriteLine($"{(failed == 0 ? "PASS" : "FAIL")}: {_assertions} production MainViewModel assertions; {failed} failed scenarios.");
        return failed == 0 ? 0 : 1;
    }

    private static async Task SettingsInitializationAsync()
    {
        var saved = new SourceItem { Name = "Saved", IsSelected = true, IsLivePreviewEnabled = false };
        var settings = new TestSettings();
        settings.Values["PreviewFps"] = 24;
        settings.Values["IsPreviewing"] = true;
        settings.Values["WaitForSourceAvailability"] = false;
        settings.Values["SavedSources"] = new[] { saved, saved.Clone(preserveId: true), new SourceItem { Id = Guid.Empty } };
        await using var context = new TestContext(settings);
        var vm = context.ViewModel;
        await vm.Initialization;
        Check(vm.Sources.Count == 1 && vm.Sources[0].Id == saved.Id, "Loading rejects duplicate and empty source IDs");
        Check(vm.PreviewFps == 24 && context.Capture.FrameRate == 24 && !vm.WaitForSourceAvailability, "Saved capture preferences initialize the real view-model");
        Check(vm.IsPreviewing && vm.Sources[0].IsLivePreviewEnabled, "Global preview preference is applied to loaded sources");
        Check(!vm.Sources[0].IsSelected && vm.SelectedSources.Count == 0, "Saved selection never creates a stale inspector selection");
        Check(settings.Writes.IsEmpty && context.Capture.StartCounts.IsEmpty, "Initialization neither overwrites preferences nor starts capture");
        Check(context.Composite.Size == (320, 200), "The initialized compositor uses the editor's logical canvas dimensions");
    }

    private static async Task OptionalSettingFailureAsync()
    {
        var settings = new TestSettings();
        settings.Values["SavedSources"] = new[] { new SourceItem { Name = "Preserved" } };
        settings.ReadFailures.Add("WaitForSourceAvailability");
        await using var context = new TestContext(settings);
        await context.ViewModel.Initialization;
        Check(context.ViewModel.Sources.Count == 1, "A failed optional preference read must not hide otherwise valid saved sources");
        Check(context.ViewModel.StatusMessage?.Contains("WaitForSourceAvailability") == true, "The failed preference remains visible to the user");
        Check(settings.Writes.IsEmpty, "A partial read failure never overwrites existing settings");
    }

    private static async Task CaptureLifecycleAsync()
    {
        var settings = new TestSettings();
        settings.Values["StreamingPort"] = 8091;
        settings.Values["HttpsPort"] = 8491;
        await using var context = new TestContext(settings);
        var vm = context.ViewModel;
        await vm.Initialization;
        var source = new SourceItem { Name = "Lifecycle" }; vm.Sources.Add(source);
        await vm.ToggleRecordingCommand.ExecuteAsync(null);
        Check(vm.IsRecording && vm.CanPause && !vm.IsPaused && !vm.IsRecordingLoading, "Recording command publishes consistent state after startup");
        Check(context.Capture.IsCapturing(source) && context.Http.IsStreaming && context.Https.IsRunning, "Recording starts both servers and each configured source");
        Check(context.Http.Port == 8091 && context.Https.Port == 8491 && vm.StreamingUrl?.StartsWith("https:") == true, "Saved server ports and HTTPS URL take effect");
        await vm.TogglePauseCommand.ExecuteAsync(null);
        Check(vm.IsRecording && vm.IsPaused && vm.CanPause && context.Capture.Active.IsEmpty, "Pause retains recording intent and stops capture resources");
        Check(!context.Http.IsStreaming && !context.Https.IsRunning, "Pause releases HTTP and HTTPS streams");
        await vm.TogglePauseCommand.ExecuteAsync(null);
        Check(!vm.IsPaused && context.Capture.IsCapturing(source), "Resume restarts the selected sources");
        source.CanvasWidth += 2;
        Check(vm.NeedsRefresh, "A changed source capture resolution requests a refresh");
        await vm.RefreshStreamCommand.ExecuteAsync(null);
        Check(!vm.NeedsRefresh && vm.IsRecording && !vm.IsRecordingLoading && context.Capture.StartCounts[source.Id] == 3,
            "Refresh stops/restarts capture and clears its pending state");
        await vm.ToggleRecordingCommand.ExecuteAsync(null);
        Check(!vm.IsRecording && !vm.IsPaused && !vm.CanPause && vm.StreamingUrl == null && context.Capture.Active.IsEmpty,
            "Stop clears recording and URL state");
    }

    private static async Task ShutdownDuringStartAsync()
    {
        await using var context = new TestContext();
        var vm = context.ViewModel; await vm.Initialization;
        vm.Sources.Add(new SourceItem());
        var gate = context.Capture.StartGate = new AsyncGate();
        var start = vm.ToggleRecordingCommand.ExecuteAsync(null);
        await gate.Entered;
        var stop = vm.ShutdownAsync();
        Check(!stop.IsCompleted, "Shutdown waits for an in-flight native start");
        gate.Release();
        await Task.WhenAll(start, stop).WaitAsync(TimeSpan.FromSeconds(3));
        Check(context.Capture.Active.IsEmpty && !context.Http.IsStreaming && !context.Https.IsRunning,
            "Shutdown wins over a late native startup completion");
        var starts = context.Capture.StartCounts.Values.Sum();
        await vm.StartAllCapturesAsync();
        Check(context.Capture.StartCounts.Values.Sum() == starts, "A shut-down view-model cannot start another capture session");
    }

    private static async Task StopDuringAddAsync()
    {
        await using var context = new TestContext();
        var vm = context.ViewModel; await vm.Initialization;
        await vm.ToggleRecordingCommand.ExecuteAsync(null);
        var late = new SourceItem { Name = "Late source" };
        var gate = context.Capture.StartGate = new AsyncGate();
        var add = vm.AddSourceCommand.ExecuteAsync(late);
        await gate.Entered;
        var stop = vm.ToggleRecordingCommand.ExecuteAsync(null);
        gate.Release();
        await Task.WhenAll(add, stop).WaitAsync(TimeSpan.FromSeconds(3));
        Check(vm.Sources.Contains(late) && !vm.IsRecording, "Stopping while adding preserves the configured source but stops recording");
        Check(context.Capture.Active.IsEmpty && !context.Http.IsStreaming && !context.Https.IsRunning,
            "A late added source cannot resurrect capture after Stop");
        Check(context.Calls.Contains($"capture:stop:{late.Id}"), "Late source startup is explicitly cleaned up");
    }

    private static async Task DeleteDuringStartAsync()
    {
        await using var context = new TestContext();
        var vm = context.ViewModel; await vm.Initialization;
        await vm.ToggleRecordingCommand.ExecuteAsync(null);
        var late = new SourceItem { Name = "Deleted while starting" };
        var gate = context.Capture.StartGate = new AsyncGate();
        var add = vm.AddSourceCommand.ExecuteAsync(late);
        await gate.Entered;
        await vm.DeleteSourceCommand.ExecuteAsync(late);
        Check(vm.Sources.Contains(late) && vm.IsEditingSources, "Deletion is ignored while an add owns the source-edit transaction");
        gate.Release();
        await add.WaitAsync(TimeSpan.FromSeconds(3));
        await vm.DeleteSourceCommand.ExecuteAsync(late);
        Check(!vm.Sources.Contains(late) && !context.Capture.IsCapturing(late), "Deletion after startup cannot leave an orphan recorder");
        Check(context.Http.Removed.Contains(late.Id) && context.Https.Removed.Contains(late.Id), "Deletion informs both source-stream servers");
        var invalidations = context.Composite.Invalidations;
        late.CanvasX++;
        Check(context.Composite.Invalidations == invalidations, "Deleted sources no longer retain view-model property subscriptions");
    }

    private static async Task BusyPasteUndoAsync()
    {
        await using var context = new TestContext();
        var vm = context.ViewModel; await vm.Initialization;
        var first = new SourceItem { Name = "Original A" };
        var second = new SourceItem { Name = "Original B" };
        vm.Sources.Add(first); vm.Sources.Add(second); vm.UpdateSelectedSources([first]);
        vm.SelectedSourceName = "Edited A";
        vm.UpdateSelectedSources([first, second]); vm.CopySourceCommand.Execute(null);
        await vm.ToggleRecordingCommand.ExecuteAsync(null);
        Check(vm.CanUndo && vm.UndoCommand.CanExecute(null), "An earlier edit is undoable before paste starts");
        var propertyNotifications = new List<bool>();
        var commandNotifications = new List<bool>();
        vm.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(vm.CanUndo)) propertyNotifications.Add(vm.CanUndo); };
        vm.UndoCommand.CanExecuteChanged += (_, _) => commandNotifications.Add(vm.UndoCommand.CanExecute(null));
        var gate = context.Capture.StartGate = new AsyncGate();
        var paste = vm.PasteSourceCommand.ExecuteAsync(null);
        await gate.Entered;
        Check(vm.IsPasting && vm.IsEditingSources && !vm.IsCanvasEditable && !vm.CanUndo && !vm.PasteSourceCommand.CanExecute(null),
            "A blocked pasted-source startup disables overlapping source mutations and history commands");
        Check(vm.Sources.Count == 3, "The fake native gate deterministically pauses paste between its two new sources");
        await vm.UndoCommand.ExecuteAsync(null);
        vm.SelectedSourceOpacity = 0.4;
        Check(vm.Sources.Count == 3 && first.Name == "Edited A" && first.Opacity == 1,
            "An invoked Undo and inspector edit cannot mutate a partially completed paste");
        gate.Release(); await paste.WaitAsync(TimeSpan.FromSeconds(3));
        Check(vm.Sources.Count == 4 && vm.SelectedSources.Count == 2 && !vm.IsEditingSources && !vm.IsPasting && vm.CanUndo,
            "Paste completes as one consistent operation after the native gate opens");
        Check(propertyNotifications.Contains(false) && propertyNotifications.Last() && commandNotifications.Contains(false) && commandNotifications.Last(),
            "Both CanUndo bindings and generated command notifications reflect busy transitions");
        await vm.UndoCommand.ExecuteAsync(null);
        Check(vm.Sources.Count == 2 && vm.Sources[0].Name == "Edited A" && context.Capture.Active.Count == 2,
            "The first later Undo removes the whole paste, including its capture sessions");
        await vm.UndoCommand.ExecuteAsync(null);
        Check(vm.Sources.Count == 2 && vm.Sources[0].Name == "Original A",
            "The attempted busy Undo never consumed the earlier history entry");
    }

    private static async Task CaptureErrorsAsync()
    {
        await using (var context = new TestContext())
        {
            var vm = context.ViewModel; await vm.Initialization;
            var source = new SourceItem { Name = "Test device" }; vm.Sources.Add(source);
            context.Https.StartFailure = new IOException("TLS unavailable");
            await vm.ToggleRecordingCommand.ExecuteAsync(null);
            Check(vm.IsRecording && context.Http.IsStreaming && context.Capture.IsCapturing(source), "HTTPS failure leaves working HTTP capture available");
            Check(vm.StatusMessage?.Contains("TLS unavailable") == true, "HTTPS startup failure is visible");
            context.Capture.Fail(source, "Device disconnected");
            Check(vm.StatusMessage?.Contains("Test device") == true && vm.StatusMessage.Contains("Device disconnected"), "CaptureFailed updates the user's status message");
        }
        await using (var context = new TestContext())
        {
            var vm = context.ViewModel; await vm.Initialization;
            vm.Sources.Add(new SourceItem());
            context.Http.StartFailure = new IOException("Port occupied");
            await vm.ToggleRecordingCommand.ExecuteAsync(null);
            Check(!vm.IsRecording && !vm.CanPause && !vm.IsRecordingLoading && context.Capture.Active.IsEmpty && !context.Https.IsRunning,
                "Primary HTTP failure rolls back startup and command state");
            Check(vm.StatusMessage?.Contains("Port occupied") == true, "Primary server failure is visible");
        }
    }

    private static async Task UndoRedoAsync()
    {
        await using var context = new TestContext();
        var vm = context.ViewModel; await vm.Initialization;
        vm.IsPreviewing = true;
        var source = new SourceItem { Type = SourceType.Region, Name = "Region", RegionBounds = new RectInt32(-120, 35, 100, 50), CanvasX = 20 };
        vm.Sources.Add(source); vm.UpdateSelectedSources([source]);
        vm.SaveUndoState();
        source.RegionBounds = new RectInt32(10, -80, 200, 90); source.CanvasX = 70;
        await vm.SaveSourcesAsync();
        await vm.ToggleRecordingCommand.ExecuteAsync(null);
        await vm.UndoCommand.ExecuteAsync(null);
        var restored = vm.Sources.Single();
        Check(restored.Id == source.Id && restored.RegionBounds?.X == -120 && restored.RegionBounds?.Y == 35 && restored.RegionBounds?.Width == 100,
            "Undo restores negative screen-region coordinates and persistent source identity");
        Check(restored.CanvasX == 20 && !ReferenceEquals(restored, source), "Undo restores a detached pre-edit source snapshot");
        Check(vm.SelectedSources.Count == 0 && !restored.IsSelected && restored.IsLivePreviewEnabled, "Undo clears stale selection and reapplies global preview state");
        Check(vm.IsRecording && context.Capture.Active[source.Id] == restored && !vm.IsRecordingLoading,
            "Undo while recording replaces the native session with the restored source");
        await vm.RedoCommand.ExecuteAsync(null);
        Check(vm.Sources.Single().RegionBounds?.Y == -80 && vm.Sources.Single().CanvasX == 70, "Redo restores the edited region and canvas geometry");
        Check(vm.CanUndo && !vm.CanRedo, "Undo/redo command availability reflects the restored history");
    }

    private static async Task LayerOrderingAsync()
    {
        await using var context = new TestContext();
        var vm = context.ViewModel; await vm.Initialization;
        var sources = "ABCD".Select(name => new SourceItem { Name = name.ToString() }).ToArray();
        void Arrange(params int[] selected)
        {
            vm.Sources.Clear(); foreach (var source in sources) vm.Sources.Add(source);
            vm.UpdateSelectedSources(selected.Select(index => sources[index]));
        }
        string Order() => string.Concat(vm.Sources.Select(source => source.Name));
        Arrange(0, 1); vm.BringForwardCommand.Execute(null);
        Check(Order() == "ABCD", "A multi-selection touching the foreground boundary keeps its relative order");
        Arrange(2, 3); vm.SendBackwardCommand.Execute(null);
        Check(Order() == "ABCD", "A multi-selection touching the background boundary keeps its relative order");
        Arrange(1, 2); vm.BringForwardCommand.Execute(null);
        Check(Order() == "BCAD", "A contiguous selection moves forward as an ordered block");
        Arrange(1, 2); vm.SendBackwardCommand.Execute(null);
        Check(Order() == "ADBC", "A contiguous selection moves backward as an ordered block");
        Arrange(0, 2); vm.SendToBackCommand.Execute(null);
        Check(Order() == "BDAC", "Sending nonadjacent selected sources back preserves both groups' order");
        Arrange(1, 3); vm.BringToFrontCommand.Execute(null);
        Check(Order() == "BDAC" && vm.SelectedSources.Count == 2, "Bringing a selection to the front preserves selection and relative order");
    }

    private static double[] Geometry(SourceItem source) =>
        [source.CanvasX, source.CanvasY, source.CanvasWidth, source.CanvasHeight, source.Rotation,
         source.CropLeftPct, source.CropTopPct, source.CropRightPct, source.CropBottomPct, source.CropRotation];

    private static async Task MirrorGeometryAsync()
    {
        await using var context = new TestContext();
        var vm = context.ViewModel; await vm.Initialization;
        var first = new SourceItem { CanvasX = 35, CanvasY = 40, CanvasWidth = 100, CanvasHeight = 60,
            Rotation = 23, CropLeftPct = 0.2, CropTopPct = 0.1, CropRightPct = 0.3, CropBottomPct = 0.15, CropRotation = -17 };
        var second = new SourceItem { CanvasX = 170, CanvasY = 90, CanvasWidth = 80, CanvasHeight = 30,
            Rotation = -15, CropLeftPct = 0.05, CropTopPct = 0.2, CropRightPct = 0.1, CropBottomPct = 0.25, CropRotation = 11 };
        var firstGeometry = Geometry(first); var secondGeometry = Geometry(second);
        vm.Sources.Add(first); vm.Sources.Add(second); vm.UpdateSelectedSources([first]);
        await vm.ToggleFlipHorizontalCommand.ExecuteAsync(null);
        Check(first.IsMirroredHorizontally && !first.IsMirroredVertically && Geometry(first).SequenceEqual(firstGeometry),
            "Horizontal mirror changes only source content, preserving a rotated asymmetric crop and placement");
        await vm.ToggleFlipVerticalCommand.ExecuteAsync(null);
        Check(first.IsMirroredVertically && Geometry(first).SequenceEqual(firstGeometry),
            "Vertical mirror also keeps the source's crop mask and transform fixed");
        vm.UpdateSelectedSources([first, second]);
        await vm.ToggleFlipHorizontalCommand.ExecuteAsync(null);
        await vm.ToggleFlipVerticalCommand.ExecuteAsync(null);
        Check(first.IsMirroredHorizontally && second.IsMirroredHorizontally && first.IsMirroredVertically && second.IsMirroredVertically,
            "A mixed multi-selection converges to one enabled mirror state for both axes");
        Check(Geometry(first).SequenceEqual(firstGeometry) && Geometry(second).SequenceEqual(secondGeometry),
            "Mirroring a multi-selection preserves relative positions, rotations, dimensions and crop masks");
        await vm.ToggleFlipHorizontalCommand.ExecuteAsync(null);
        Check(!first.IsMirroredHorizontally && !second.IsMirroredHorizontally && first.IsMirroredVertically && second.IsMirroredVertically,
            "Toggling a fully mirrored selection disables that axis without touching the other");
    }

    private static async Task NumericEditGuardsAsync()
    {
        await using var context = new TestContext();
        var vm = context.ViewModel; await vm.Initialization;
        var source = new SourceItem { CanvasX = 20, CanvasY = 30, CanvasWidth = 100, CanvasHeight = 80,
            Opacity = 0.5, CropLeftPct = 0.1, CropRightPct = 0.2, CropTopPct = 0.15, CropBottomPct = 0.05 };
        vm.Sources.Add(source); vm.UpdateSelectedSources([source]);
        var geometry = Geometry(source);
        vm.SelectedSourceOpacity = double.NaN; vm.SelectedSourceCropLeftPct = double.NaN;
        vm.SelectedSourceCropRightPct = double.PositiveInfinity; vm.SelectedSourceCropTopPct = double.NegativeInfinity;
        vm.SelectedSourceCropBottomPct = double.NaN; vm.SelectedSourceCropRotation = double.NaN;
        vm.SelectedSourceX = double.NaN; vm.SelectedSourceY = double.PositiveInfinity;
        vm.SelectedSourceWidth = double.NaN; vm.SelectedSourceHeight = double.NegativeInfinity;
        Check(Geometry(source).SequenceEqual(geometry) && source.Opacity == 0.5 && !vm.CanUndo,
            "Nonfinite inspector values cannot corrupt geometry, opacity or undo history");
        vm.SelectedSourceName = source.Name; vm.SelectedSourceOpacity = source.Opacity;
        vm.SelectedSourceCropLeftPct = 10; vm.SelectedSourceCropRightPct = 20;
        vm.SelectedSourceCropTopPct = 15; vm.SelectedSourceCropBottomPct = 5;
        vm.SelectedSourceCropRotation = source.CropRotation; vm.SelectedSourceRotation = source.Rotation;
        vm.SelectedSourceX = source.CanvasX; vm.SelectedSourceY = source.CanvasY;
        vm.SelectedSourceWidth = source.CanvasWidth; vm.SelectedSourceHeight = source.CanvasHeight;
        Check(!vm.CanUndo && context.Settings.Writes.IsEmpty, "Reassigning unchanged inspector values neither creates undo entries nor writes settings");
        vm.SelectedSourceOpacity = 5;
        Check(source.Opacity == 1 && vm.CanUndo, "A finite opacity edit clamps to its valid range and remains undoable");
        await vm.UndoCommand.ExecuteAsync(null);
        Check(vm.Sources.Single().Opacity == 0.5 && !vm.CanUndo, "One real numeric edit requires exactly one Undo");
    }

    private static async Task RejectedGeometryAsync()
    {
        await using var context = new TestContext();
        var vm = context.ViewModel; await vm.Initialization;
        var source = new SourceItem { Name = "Original", CanvasX = 20, CanvasY = 30, CanvasWidth = 100, CanvasHeight = 80 };
        vm.Sources.Add(source); vm.UpdateSelectedSources([source]);
        vm.SelectedSourceName = "Edited";
        await vm.UndoCommand.ExecuteAsync(null);
        source = vm.Sources.Single(); vm.UpdateSelectedSources([source]);
        var geometry = Geometry(source);
        while (context.Settings.Writes.TryDequeue(out _)) { }
        vm.SelectedSourceWidth = 7680; vm.SelectedSourceHeight = 4320;
        vm.SelectedSourceX = 500; vm.SelectedSourceY = -500;
        Check(Geometry(source).SequenceEqual(geometry), "Out-of-canvas dimensions and positions are rejected");
        Check(!vm.CanUndo && vm.CanRedo, "Rejected geometric edits preserve the available Redo and do not create empty Undo entries");
        await vm.RedoCommand.ExecuteAsync(null);
        Check(vm.Sources.Single().Name == "Edited", "The previous real edit can still be redone after invalid geometry attempts");
    }

    private static async Task NudgeSelectionAsync()
    {
        await using (var context = new TestContext())
        {
            var vm = context.ViewModel; await vm.Initialization;
            var cropped = new SourceItem { CanvasX = -15, CanvasY = 10, CanvasWidth = 100, CanvasHeight = 50, CropLeftPct = 0.2 };
            vm.Sources.Add(cropped); vm.UpdateSelectedSources([cropped]);
            await vm.NudgeSelectionAsync(-10, 0);
            Check(cropped.CanvasX == -20 && cropped.CanvasY == 10,
                "Keyboard movement uses the visible crop edge and allows an off-canvas uncropped source origin");
            await vm.UndoCommand.ExecuteAsync(null);
            Check(vm.Sources.Single().CanvasX == -15, "A clamped keyboard movement restores its original geometry with one Undo");
        }
        await using (var context = new TestContext())
        {
            var vm = context.ViewModel; await vm.Initialization;
            var left = new SourceItem { CanvasX = 100, CanvasY = 30, CanvasWidth = 50, CanvasHeight = 40 };
            var right = new SourceItem { CanvasX = 265, CanvasY = 70, CanvasWidth = 50, CanvasHeight = 40 };
            vm.Sources.Add(left); vm.Sources.Add(right); vm.UpdateSelectedSources([left, right]);
            await vm.NudgeSelectionAsync(10, -10);
            Check(left.CanvasX == 105 && right.CanvasX == 270 && left.CanvasY == 20 && right.CanvasY == 60,
                "The whole group receives the same canvas-clamped movement and preserves spacing");
        }
        await using (var context = new TestContext())
        {
            var vm = context.ViewModel; await vm.Initialization;
            var edge = new SourceItem { CanvasX = 270, CanvasY = 0, CanvasWidth = 50, CanvasHeight = 40 };
            vm.Sources.Add(edge); vm.UpdateSelectedSources([edge]);
            await vm.NudgeSelectionAsync(10, -10);
            Check(edge.CanvasX == 270 && edge.CanvasY == 0 && !vm.CanUndo && context.Settings.Writes.IsEmpty,
                "A movement blocked at both edges causes no geometry, history or persistence change");
        }
    }

    private static async Task PersistenceAsync()
    {
        await using var context = new TestContext();
        var vm = context.ViewModel; await vm.Initialization;
        var source = new SourceItem { Name = "Before" }; vm.Sources.Add(source);
        var gate = context.Settings.SourceSaveGate = new AsyncGate();
        var save = vm.SaveSourcesAsync();
        await gate.Entered;
        source.Name = "Edited during write";
        gate.Release(); await save;
        Check(context.Settings.LastSources?.Single().Name == "Before" && context.Settings.LastSources.Single().Id == source.Id,
            "Saving uses a detached snapshot that cannot change while I/O awaits");
        context.Settings.SourceSaveGate = null;
        while (context.Settings.Writes.TryDequeue(out _)) { }
        source.CanvasX = 10; var one = vm.SaveSourcesAsync();
        source.CanvasX = 20; var two = vm.SaveSourcesAsync();
        source.CanvasX = 30; var three = vm.SaveSourcesAsync();
        await Task.WhenAll(one, two, three);
        Check(context.Settings.Writes.Count(write => write.Key == "SavedSources") == 1 && context.Settings.LastSources?.Single().CanvasX == 30,
            "Rapid edits debounce to one write containing the newest snapshot");
        context.Settings.WriteFailures.Add("SavedSources");
        await vm.SaveSourcesAsync();
        Check(vm.StatusMessage?.Contains("Could not save sources") == true, "Persistence failures reach the status message without an unhandled task");
    }

    private static async Task CopyPasteCleanupAsync()
    {
        await using var context = new TestContext();
        var vm = context.ViewModel; await vm.Initialization;
        var source = new SourceItem { Name = "Website", Type = SourceType.Website, WebsiteUrl = "https://example.invalid/", CropLeftPct = 0.2, CanvasX = 30 };
        vm.Sources.Add(source); vm.UpdateSelectedSources([source]);
        vm.CopySourceCommand.Execute(null);
        source.Name = "Changed after copy";
        await vm.PasteSourceCommand.ExecuteAsync(null);
        var pasted = vm.Sources.Last();
        Check(pasted.Id != source.Id && pasted.Name == "Website" && pasted.CropLeftPct == 0.2 && pasted.CanvasX == 30,
            "Paste uses the detached copied state with a new identity and preserved crop/position");
        Check(vm.SelectedSources.Count == 1 && vm.SelectedSources[0] == pasted && !source.IsSelected && !vm.IsPasting,
            "Paste selects the new source and finishes its busy state");
        await vm.DeleteSourceCommand.ExecuteAsync(pasted);
        Check(context.Composite.Removed.Contains(pasted.Id) && context.Http.Removed.Contains(pasted.Id) && context.Https.Removed.Contains(pasted.Id),
            "Website deletion removes its source from each compositor and streaming service");
        Check(vm.SelectedSources.Count == 0 && !pasted.IsSelected, "Deleting the selected source clears inspector selection");
    }

    private static async Task AvailabilityAsync()
    {
        await using var context = new TestContext();
        var vm = context.ViewModel; await vm.Initialization;
        var source = new SourceItem { Name = "Initially absent" }; vm.Sources.Add(source);
        context.Capture.StartFailures[source.Id] = new IOException("Source unavailable");
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Capture.Started = item => { if (item.Id == source.Id) retried.TrySetResult(); };
        await vm.ToggleRecordingCommand.ExecuteAsync(null);
        Check(vm.IsRecording && !context.Capture.IsCapturing(source) && vm.StatusMessage?.Contains("Source unavailable") == true,
            "An unavailable source reports its failure while keeping source discovery active");
        context.Capture.StartFailures.TryRemove(source.Id, out _);
        await retried.Task.WaitAsync(TimeSpan.FromSeconds(7));
        Check(context.Capture.StartCounts[source.Id] == 2 && context.Capture.IsCapturing(source),
            "The production availability loop retries a source when it becomes available");
        await vm.ToggleRecordingCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(1));
        Check(context.Capture.Active.IsEmpty && !context.Http.IsStreaming && !context.Https.IsRunning,
            "Stopping cancels the availability delay immediately and leaves no background recorder");
    }
}
