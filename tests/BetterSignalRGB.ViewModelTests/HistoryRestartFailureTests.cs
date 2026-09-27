using Better_SignalRGB_Screen_Capture.Models;

namespace BetterSignalRGB.ViewModelTests;

internal static class HistoryRestartFailureTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        foreach (var operation in new[] { "Undo", "Redo", "Scene" })
        {
            await using var context = new TestContext();
            var vm = context.ViewModel;
            await Task.WhenAll(vm.Initialization, vm.ScenesInitialization);
            var source = new SourceItem { Name = "Restored layout", MonitorDeviceId = "test-display", CanvasX = 10 };
            vm.Sources.Add(source);
            if (operation == "Scene")
            {
                vm.SceneName = "Original";
                await vm.SaveNewSceneCommand.ExecuteAsync(null);
            }
            vm.SaveUndoState();
            source.CanvasX = 20;
            await vm.SaveSourcesAsync();
            await vm.ToggleRecordingCommand.ExecuteAsync(null);
            if (operation == "Redo") await vm.UndoCommand.ExecuteAsync(null);

            var expectedX = operation == "Redo" ? 20 : 10;
            var failure = new IOException("Port became occupied during layout restore");
            context.Http.NextStartFailure = failure;
            if (operation == "Scene")
            {
                await vm.LoadSceneCommand.ExecuteAsync(null);
                check(vm.SceneStatus?.Contains(failure.Message) == true, "Scene startup failure is reported in its existing status");
            }
            else
            {
                try
                {
                    await (operation == "Undo" ? vm.UndoCommand : vm.RedoCommand).ExecuteAsync(null);
                    check(false, $"{operation} reports a restart failure to the guarded UI action");
                }
                catch (IOException exception)
                {
                    check(ReferenceEquals(failure, exception), $"{operation} preserves the restart error for the UI boundary");
                }
            }

            check(!vm.IsRecording && !vm.CanPause && !vm.IsPaused && !vm.IsRecordingLoading && vm.IsCanvasEditable,
                $"{operation} restart failure publishes stopped state and releases the editor");
            check(context.Capture.Active.IsEmpty && !context.Http.IsStreaming && !context.Https.IsRunning && vm.StreamingUrl == null,
                $"{operation} restart failure leaves no old native sessions or streaming servers");
            check(vm.Sources.Single().CanvasX == expectedX && vm.Sources.Single().Id == source.Id,
                $"{operation} preserves the restored layout despite its capture failure");
            check(context.Settings.LastSources?.Single().CanvasX == expectedX,
                $"{operation} persists the restored layout independently from capture startup");

            await vm.ToggleRecordingCommand.ExecuteAsync(null);
            check(vm.IsRecording && vm.CanPause && context.Http.IsStreaming && context.Capture.IsCapturing(vm.Sources.Single()),
                $"{operation} can start the restored sources after the one-shot port failure clears");
        }
    }
}
