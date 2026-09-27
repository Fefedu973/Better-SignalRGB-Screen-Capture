using Better_SignalRGB_Screen_Capture.Models;

namespace BetterSignalRGB.ViewModelTests;

internal static class OutputQualityTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        await using (var context = new TestContext())
        {
            var vm = context.ViewModel;
            await vm.Initialization;
            var source = new SourceItem { CanvasX = 17, CanvasY = 23, CanvasWidth = 119, CanvasHeight = 83,
                Rotation = 29, CropRotation = 43, CropLeftPct = .12, CropBottomPct = .18, IsLocked = true };
            vm.Sources.Add(source);
            var before = SceneSourceSnapshot.Capture(source);
            for (var index = 0; index < 8; index++)
            {
                await vm.SetHighQualityAsync(true);
                check(vm.IsHighQuality && context.Capture.HighQuality && context.Composite.Size == (800, 600), "HQ configures actual capture and composite resolution");
                check(vm.CanvasViewportWidth == 1000 && vm.CanvasViewportHeight == 840, "HQ editor preserves margins around transformed interaction handles");
                await vm.SetHighQualityAsync(false);
            }
            check(before == SceneSourceSnapshot.Capture(source), "Repeated quality toggles preserve every source/crop/rotation/lock field exactly");
            check(context.Capture.Active.IsEmpty && !vm.IsRecording && !vm.CanUndo, "Quality does not start recording or create geometry history");
            await vm.ToggleRecordingCommand.ExecuteAsync(null);
            var httpStarts = context.Calls.Count(call => call == "http:start");
            await vm.SetHighQualityAsync(true);
            check(vm.IsRecording && !vm.IsPaused && context.Capture.IsCapturing(source), "Running sources restart at the requested capture quality");
            check(context.Calls.Count(call => call == "http:start") == httpStarts && context.Http.IsStreaming, "Changing quality preserves connected web output servers");
            await vm.TogglePauseCommand.ExecuteAsync(null);
            await vm.SetHighQualityAsync(false);
            check(vm.IsPaused && vm.IsRecording && context.Capture.Active.IsEmpty, "Changing quality while paused preserves pause intent");
            check(context.Settings.Writes.Any(write => write.Key == "HighQualityWebOutput" && write.Value is true), "Output quality is persisted separately from source geometry");
        }

        var settings = new TestSettings();
        settings.Values["HighQualityWebOutput"] = true;
        await using (var context = new TestContext(settings))
        {
            await context.ViewModel.Initialization;
            check(context.ViewModel.IsHighQuality && context.Capture.HighQuality && context.Composite.Size == (800, 600), "Saved HQ is restored before any source starts");
            check(settings.Writes.IsEmpty && context.Capture.Active.IsEmpty, "Loading HQ does not overwrite settings or start capture");
        }

        await using (var context = new TestContext())
        {
            var vm = context.ViewModel;
            await vm.Initialization;
            var source = new SourceItem(); vm.Sources.Add(source);
            await vm.ToggleRecordingCommand.ExecuteAsync(null);
            context.Settings.WriteFailures.Add("HighQualityWebOutput");
            var starts = context.Capture.StartCounts[source.Id];
            await vm.SetHighQualityAsync(true);
            check(!vm.IsHighQuality && context.Capture.IsCapturing(source) && context.Capture.StartCounts[source.Id] == starts,
                "A failed quality preference save leaves the working capture untouched");
            check(vm.StatusMessage?.Contains("quality") == true && vm.IsCanvasEditable, "Quality failures are visible and release editor state");
            context.Settings.WriteFailures.Clear();
            context.Capture.QualityFailure = new IOException("Quality unavailable");
            await vm.SetHighQualityAsync(true);
            check(!vm.IsHighQuality && !context.Capture.HighQuality && context.Capture.IsCapturing(source), "Rejected quality resumes the original capture mode");
            check(context.Settings.Writes.Last(write => write.Key == "HighQualityWebOutput").Value is false, "Rejected quality restores the saved preference");
        }

        await using (var context = new TestContext())
        {
            var vm = context.ViewModel;
            await vm.Initialization;
            var source = new SourceItem(); vm.Sources.Add(source);
            await vm.ToggleRecordingCommand.ExecuteAsync(null);
            context.Capture.QualityGate = new();
            var change = vm.SetHighQualityAsync(true);
            await context.Capture.QualityGate.Entered;
            check(!vm.IsCanvasEditable, "Quality changes exclude concurrent source edits");
            var stop = vm.StopAllCapturesAsync();
            context.Capture.QualityGate.Release();
            await Task.WhenAll(change, stop);
            check(context.Capture.Active.IsEmpty && context.Capture.StartCounts[source.Id] == 1, "Concurrent stop prevents quality restart from reviving capture");
        }
    }
}
