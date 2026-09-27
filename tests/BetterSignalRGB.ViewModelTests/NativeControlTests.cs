using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.Services.NativeOutput;

namespace BetterSignalRGB.ViewModelTests;

internal static class NativeControlTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        await SceneRestorationAsync(check);
        await CaptureTransitionsAsync(check);
        await LeaseAndAppearanceAsync(check);
        await DispatcherAsync(check);
    }

    private static async Task SceneRestorationAsync(Action<bool, string> check)
    {
        await using var context = new TestContext();
        var vm = context.ViewModel; await Task.WhenAll(vm.Initialization, vm.ScenesInitialization);
        var original = new SourceItem { Name = "Original", MonitorDeviceId = "synthetic-display", CanvasWidth = 100, CanvasHeight = 60 };
        vm.Sources.Add(original); vm.SaveUndoState(); original.CanvasX = 12;
        await vm.SaveSourcesAsync();
        vm.SceneName = "Original"; await vm.SaveNewSceneCommand.ExecuteAsync(null);
        var previousId = vm.SelectedScene!.Id;
        var requested = await context.SceneLibrary.SaveNewAsync("Temporary", [SceneSourceSnapshot.Capture(new SourceItem { Name = "Temporary", MonitorDeviceId = "synthetic-display", CanvasX = 40 })]);
        vm.SelectedScene = requested;
        check(vm.NativeState.ActiveSceneId == previousId, "Selecting a library row does not pretend that it is the active scene");
        var manual = vm.NativeState.ManualRevision;
        original.IsSelected = true; original.IsLivePreviewEnabled = true;
        check(vm.NativeState.ManualRevision == manual, "Selection and preview flags do not invalidate scene ownership");
        using var settings = new AppearanceSettings();
        using var control = Control(context, settings);
        var available = await control.ListScenesAsync();
        check(available.Select(scene => scene.Id).SequenceEqual(new[] { previousId, requested.Id }), "Native scene listing exposes stable saved IDs");
        var writes = context.Settings.Writes.Count;
        var acquired = await control.AcquireAsync("test", sceneId: requested.Id);
        check(acquired.Scene.ActiveSceneId == requested.Id && vm.Sources.Single().Name == "Temporary", "Scene acknowledgement follows actual source replacement");
        check(!acquired.Scene.SceneLoading && acquired.Scene.StateRevision > 0, "Acknowledgement reports an effective state revision after loading");
        check(!vm.IsRecording && context.Capture.StartCounts.IsEmpty, "A stopped native scene load starts no capture");
        await vm.SaveSourcesAsync();
        check(context.Settings.Writes.Count == writes, "Automatic source persistence cannot save a temporary scene");
        await control.ReleaseAsync(acquired.Lease!.Id);
        check(vm.NativeState.ActiveSceneId == previousId && vm.Sources.Single().Id == original.Id && vm.Sources[0].CanvasX == 12,
            "Release restores the real previous scene and stable source IDs");
        check(context.Settings.Writes.Count == writes, "Temporary load and automatic restoration leave durable preferences untouched");
        await vm.UndoCommand.ExecuteAsync(null);
        check(vm.Sources.Single().CanvasX == 0, "An automatic return restores the pre-lease undo history without a spurious identical state");

        acquired = await control.AcquireAsync("manual-edit", sceneId: requested.Id);
        var sourceId = vm.Sources.Single().Id;
        vm.Sources[0].CanvasX = 63;
        var edited = vm.NativeState;
        check(edited.ActiveSceneId == null && edited.ManualRevision > acquired.Scene.ManualRevision && edited.StateRevision > acquired.Scene.StateRevision,
            "A real canvas edit clears exact scene identity and advances both manual and effective revisions");
        await vm.SaveSourcesAsync();
        writes = context.Settings.Writes.Count;
        var manuallySaved = context.Settings.LastSources!.Single();
        check(manuallySaved.Id == sourceId && manuallySaved.CanvasX == 63,
            "A manual canvas edit becomes durable immediately, without waiting for release or lease expiry");
        await ExpectAsync("manual_override", () => control.SelectSceneAsync(acquired.Lease!.Id, requested.Id), check);
        await control.ReleaseAsync(acquired.Lease!.Id);
        check(vm.Sources.Single().Id == sourceId && vm.Sources[0].CanvasX == 63, "Release never overwrites a manual edit made during the lease");
        check(context.Settings.Writes.Count == writes + 1 && context.Settings.LastSources!.Single().CanvasX == 63,
            "Release keeps the preserved manual canvas durable instead of writing the old scene over it");
        await control.StopAsync();
    }

    private static async Task CaptureTransitionsAsync(Action<bool, string> check)
    {
        await using var context = new TestContext();
        var vm = context.ViewModel; await Task.WhenAll(vm.Initialization, vm.ScenesInitialization);
        var original = new SourceItem { Name = "Running original", MonitorDeviceId = "synthetic-display" }; vm.Sources.Add(original);
        var scene = await context.SceneLibrary.SaveNewAsync("Native target", [SceneSourceSnapshot.Capture(new SourceItem { Name = "Native target", MonitorDeviceId = "synthetic-display" })]);
        using var settings = new AppearanceSettings();
        var clock = new Clock();
        using var control = Control(context, settings, clock);
        await vm.ToggleRecordingCommand.ExecuteAsync(null);
        await vm.TogglePauseCommand.ExecuteAsync(null);
        var starts = context.Capture.StartCounts.Values.Sum();
        var lease = await control.AcquireAsync("paused", sceneId: scene.Id);
        check(vm.IsPaused && vm.IsRecording && context.Capture.StartCounts.Values.Sum() == starts && context.Capture.Active.IsEmpty,
            "Native scene selection preserves pause without opening hidden capture sessions");
        await control.ReleaseAsync(lease.Lease!.Id);
        check(vm.IsPaused && vm.Sources.Single().Id == original.Id, "Temporary restoration preserves the paused intention");
        await vm.TogglePauseCommand.ExecuteAsync(null);
        var gate = context.Capture.StartGate = new AsyncGate();
        var load = control.AcquireAsync("running", sceneId: scene.Id);
        await gate.Entered;
        check(!load.IsCompleted && vm.NativeState.SceneLoading && !vm.IsCanvasEditable,
            "Scene selection waits for capture restart and reports loading while restart is in flight");
        var loadingStatus = await control.GetStatusAsync().WaitAsync(TimeSpan.FromMilliseconds(500));
        check(loadingStatus.Scene.SceneLoading && loadingStatus.Lease != null,
            "Status can observe an in-flight scene command without waiting for that command to finish");
        gate.Release(); var running = await load; context.Capture.StartGate = null;
        check(running.Scene.IsRecording && !running.Scene.IsPaused && context.Capture.Active.Count == 1 && context.Capture.Active.ContainsKey(scene.Sources[0].Id),
            "Running selection starts exactly the target capture before acknowledging it");
        await control.SetOverridesAsync(running.Lease!.Id, new() { Brightness = 19 });

        var stopGate = context.Capture.StopAllGate = new AsyncGate();
        var stop = vm.ToggleRecordingCommand.ExecuteAsync(null); await stopGate.Entered;
        check(vm.NativeState.IsStopping, "Native status distinguishes an in-progress stop from a static frame");
        await ExpectAsync("scene_busy", () => control.SelectSceneAsync(running.Lease!.Id, scene.Id), check);
        await control.ReleaseForDisableAsync();
        check(control.Current.Lease is { Expired: true } && control.Current.EffectiveSettings == settings.Current
            && control.Current.Error?.Contains("pending", StringComparison.Ordinal) == true,
            "Disabling while the canvas is busy revokes overrides and ownership immediately and exposes pending restoration");
        var pending = await control.GetStatusAsync();
        check(pending.Scene.IsStopping && pending.Lease is { Expired: true } && pending.Error != null,
            "Status remains readable while restoration is waiting for a busy capture lifecycle");
        await ExpectAsync("scene_busy", () => control.AcquireAsync("too-early-reenable"), check);
        stopGate.Release(); await stop; context.Capture.StopAllGate = null;
        starts = context.Capture.StartCounts.Values.Sum();
        clock.Advance(TimeSpan.FromSeconds(1));
        for (var attempt = 0; attempt < 100 && control.Current.Lease != null; attempt++) await Task.Delay(10);
        check(control.Current.Lease == null, "The expiry timer finishes a deferred restoration without another client request");
        check(!vm.IsRecording && context.Capture.StartCounts.Values.Sum() == starts && vm.Sources.Single().Id == original.Id,
            "Deferred restoration after an explicit stop restores the scene without restarting capture");

        await vm.ToggleRecordingCommand.ExecuteAsync(null);
        gate = context.Capture.StartGate = new AsyncGate();
        load = control.AcquireAsync("slow-expiring", ttlSeconds: 1, sceneId: scene.Id);
        await gate.Entered; clock.Advance(TimeSpan.FromSeconds(2)); gate.Release();
        await ExpectAsync("lease_expired", () => load, check); context.Capture.StartGate = null;
        check(control.Current.Lease == null && vm.Sources.Single().Id == original.Id && context.Capture.Active.ContainsKey(original.Id),
            "A lease expiring during startup restores the original running scene before rejecting acknowledgement");

        var active = await control.AcquireAsync("shutdown", sceneId: scene.Id);
        await vm.ShutdownAsync();
        await control.StopAsync();
        check(control.Current.Lease == null && context.Capture.Active.IsEmpty,
            "Shutdown releases temporary ownership without restarting capture on a closing dispatcher");
        await ExpectAsync("unavailable", () => control.RenewAsync(active.Lease!.Id), check);
    }

    private static async Task LeaseAndAppearanceAsync(Action<bool, string> check)
    {
        await using var context = new TestContext();
        await Task.WhenAll(context.ViewModel.Initialization, context.ViewModel.ScenesInitialization);
        using var settings = new AppearanceSettings();
        var clock = new Clock(); using var control = Control(context, settings, clock);
        var acquired = await control.AcquireAsync("first", 2, overrides: new() { Ambilight = false, Brightness = 43, ScreenWidth = 250 });
        check(!acquired.EffectiveSettings.Ambilight && acquired.EffectiveSettings.Brightness == 43 && settings.Current.Ambilight && settings.Writes == 0,
            "Temporary appearance is metadata only and never writes the persistent settings service");
        check(!acquired.EffectiveSettings.Enabled && !acquired.EffectiveSettings.WebEnabled,
            "Native metadata overrides preserve independent SignalRGB and webpage activation switches");
        await ExpectAsync("lease_conflict", () => control.AcquireAsync("second"), check);
        await ExpectAsync("lease_conflict", () => control.AcquireAsync("first"), check);
        await ExpectAsync("lease_conflict", () => control.ReleaseAsync(Guid.NewGuid()), check);
        clock.UtcOffset = TimeSpan.FromDays(100);
        check((await control.GetStatusAsync()).Lease!.Id == acquired.Lease!.Id, "Wall-clock changes cannot expire a monotonic lease");
        clock.Advance(TimeSpan.FromSeconds(1.5));
        await control.RenewAsync(acquired.Lease!.Id);
        clock.Advance(TimeSpan.FromSeconds(1.5));
        check((await control.GetStatusAsync()).Lease != null, "Renewal extends the monotonic deadline");
        clock.Advance(TimeSpan.FromSeconds(1));
        for (var attempt = 0; attempt < 100 && control.Current.Lease != null; attempt++) await Task.Delay(10);
        check(control.Current.Lease == null && control.Current.EffectiveSettings == settings.Current,
            "An expired client loses temporary appearance and ownership without polling or sending another request");
        var next = await control.AcquireAsync("next", overrides: new() { Hue = 80 });
        var revision = next.Revision;
        await settings.UpdateAsync(settings.Current with { Hue = -42, AmbilightStyle = "Contours" });
        check(control.Current.Lease!.AppearanceRevoked && control.Current.EffectiveSettings.Hue == -42 && control.Current.Revision > revision,
            "A manual appearance edit immediately revokes temporary overrides and advances the metadata revision");
        await ExpectAsync("manual_override", () => control.SetOverridesAsync(next.Lease!.Id, new() { Hue = 10 }), check);
        await control.ReleaseAsync(next.Lease!.Id);
        check(settings.Writes == 1 && settings.Current.Hue == -42 && control.Current.EffectiveSettings.AmbilightStyle == "Contours",
            "Release preserves manual appearance and performs no restoration write");
        await ExpectAsync("scene_not_found", () => control.AcquireAsync("invalid-scene", sceneId: Guid.NewGuid()), check);
        check(control.Current.Lease == null, "A rejected scene request does not strand an inaccessible lease");
        await ExpectAsync("invalid_request", () => control.AcquireAsync("bad-ttl", 0), check);
        await control.StopAsync();
    }

    private static async Task DispatcherAsync(Action<bool, string> check)
    {
        var queue = new Queue<Action>();
        var dispatcher = new NativeControlDispatcher(action => { queue.Enqueue(action); return true; }, () => false);
        using var cancel = new CancellationTokenSource(); var calls = 0;
        var operation = dispatcher.InvokeAsync(() => { calls++; return Task.FromResult(7); }, cancel.Token);
        cancel.Cancel();
        check(operation.IsCompleted, "Cancellation completes a queued request even if the dispatcher stops pumping");
        queue.Dequeue()();
        try { await operation; check(false, "A canceled queued operation must not run"); }
        catch (OperationCanceledException) { check(calls == 0, "Cancellation before UI dispatch does not mutate the scene"); }
        var gate = new AsyncGate(); using var startedCancel = new CancellationTokenSource();
        operation = dispatcher.InvokeAsync(async () => { await gate.WaitAsync(); return 9; }, startedCancel.Token);
        queue.Dequeue()(); await gate.Entered; startedCancel.Cancel();
        check(!operation.IsCompleted, "Cancellation cannot abandon a scene transition that has already begun");
        gate.Release(); check(await operation == 9, "An already dispatched mutation reports its actual result");
    }

    private static NativeControlService Control(TestContext context, AppearanceSettings settings, Clock? clock = null) =>
        new(new NativeSceneController(() => context.ViewModel, new NativeControlDispatcher(action => { action(); return true; }, () => true)), settings, clock ?? new Clock());

    private static async Task ExpectAsync(string code, Func<Task> action, Action<bool, string> check)
    {
        try { await action(); check(false, $"Expected deterministic native error {code}"); }
        catch (NativeControlException ex) { check(ex.Code == code, $"Native command returns {code}, actual {ex.Code}"); }
    }

    private sealed class AppearanceSettings : ISignalRgbEffectSettingsService, IDisposable
    {
        public SignalRgbEffectSettings Current { get; private set; } = new();
        public int Writes;
        public event EventHandler<SignalRgbEffectSettings>? Changed;
        public Task InitializeAsync() => Task.CompletedTask;
        public Task UpdateAsync(SignalRgbEffectSettings settings)
        { Writes++; Current = settings.Normalize(); Changed?.Invoke(this, Current); return Task.CompletedTask; }
        public Task ScheduleUpdateAsync(SignalRgbEffectSettings settings) => UpdateAsync(settings);
        public Task FlushAsync() => Task.CompletedTask;
        public void Dispose() { }
    }
    private sealed class Clock : TimeProvider
    {
        private long _timestamp;
        private readonly List<TestTimer> _timers = new();
        public TimeSpan UtcOffset;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_timestamp).Add(UtcOffset);
        public void Advance(TimeSpan elapsed)
        {
            _timestamp += elapsed.Ticks;
            foreach (var timer in _timers.ToArray()) timer.Tick();
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new TestTimer(this, callback, state); timer.Change(dueTime, period); _timers.Add(timer); return timer;
        }
        private sealed class TestTimer(Clock clock, TimerCallback callback, object? state) : ITimer
        {
            private long _due = long.MaxValue;
            private TimeSpan _period;
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed) return false;
                _due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._timestamp + dueTime.Ticks;
                _period = period; return true;
            }
            public void Tick()
            {
                if (_disposed || clock._timestamp < _due) return;
                _due = _period == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._timestamp + _period.Ticks;
                callback(state);
            }
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
