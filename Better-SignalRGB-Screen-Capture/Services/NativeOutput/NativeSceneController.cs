using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.ViewModels;

namespace Better_SignalRGB_Screen_Capture.Services.NativeOutput;

/// <summary>Late UI resolution avoids MainViewModel/compositor/native-output dependency cycles.</summary>
public sealed class NativeSceneController(Func<MainViewModel> viewModel, INativeControlDispatcher dispatcher) : INativeSceneController, IDisposable
{
    private MainViewModel? _observed;
    private bool _disposed;
    public event EventHandler<NativeSceneState>? StateChanged;

    private MainViewModel GetViewModel()
    {
        if (_disposed) throw new NativeControlException("unavailable", "Native scene control has been disposed.");
        var vm = viewModel();
        if (_observed == null)
        {
            _observed = vm;
            vm.NativeSceneStateChanged += OnSceneChanged;
        }
        return vm;
    }
    private void OnSceneChanged(object? sender, NativeSceneState state) => StateChanged?.Invoke(this, state);
    public void Dispose()
    {
        _disposed = true;
        if (_observed != null) _observed.NativeSceneStateChanged -= OnSceneChanged;
        _observed = null;
    }

    public Task<NativeSceneState> GetStateAsync(CancellationToken cancellationToken = default) =>
        dispatcher.InvokeAsync(async () => { var vm = GetViewModel(); await vm.Initialization; return vm.NativeState; }, cancellationToken);
    public Task<IReadOnlyList<NativeSceneInfo>> ListScenesAsync(CancellationToken cancellationToken = default) =>
        dispatcher.InvokeAsync(() => GetViewModel().GetNativeScenesAsync(), cancellationToken);
    public Task<NativeSceneState> BeginTemporaryControlAsync(Guid leaseId, CancellationToken cancellationToken = default) =>
        dispatcher.InvokeAsync(() => GetViewModel().BeginNativeControlAsync(leaseId), cancellationToken);
    public Task<NativeSceneState> LoadSceneAsync(Guid leaseId, Guid sceneId, CancellationToken cancellationToken = default) =>
        dispatcher.InvokeAsync(() => GetViewModel().LoadNativeSceneAsync(leaseId, sceneId), cancellationToken);
    public Task<NativeSceneState> EndTemporaryControlAsync(Guid leaseId, bool restorePreviousScene, CancellationToken cancellationToken = default) =>
        dispatcher.InvokeAsync(() => GetViewModel().EndNativeControlAsync(leaseId, restorePreviousScene), cancellationToken);
}

/// <summary>Cancellation can prevent queued work, but never abandons an already started scene transition.</summary>
public sealed class NativeControlDispatcher(Func<Action, bool> enqueue, Func<bool> hasThreadAccess) : INativeControlDispatcher
{
    public Task<T> InvokeAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<T>(cancellationToken);
        if (hasThreadAccess()) return operation();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var phase = 0;
        var registration = cancellationToken.Register(() =>
        {
            if (Interlocked.CompareExchange(ref phase, 2, 0) == 0) completion.TrySetCanceled(cancellationToken);
        });
        if (!enqueue(async () =>
        {
            if (Interlocked.CompareExchange(ref phase, 1, 0) != 0) return;
            registration.Dispose();
            try { completion.TrySetResult(await operation()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        })) completion.TrySetException(new NativeControlException("unavailable", "The application dispatcher is shutting down."));
        _ = completion.Task.ContinueWith(_ => registration.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return completion.Task;
    }
}
