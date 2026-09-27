namespace Better_SignalRGB_Screen_Capture.Contracts.Services;

public sealed record NativeSceneInfo(Guid Id, string Name);

/// <summary>A UI-owned scene snapshot. A loaded scene can be effective before its first captured frame.</summary>
public sealed record NativeSceneState(Guid? ActiveSceneId, string? ActiveSceneName, long ManualRevision,
    long StateRevision, bool SceneLoading, bool IsRecording, bool IsPaused, bool IsStopping, string? Error);

public sealed class NativeControlException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>All asynchronous methods marshal to the application context before accessing canvas models.</summary>
public interface INativeSceneController
{
    event EventHandler<NativeSceneState>? StateChanged;
    Task<NativeSceneState> GetStateAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NativeSceneInfo>> ListScenesAsync(CancellationToken cancellationToken = default);
    Task<NativeSceneState> BeginTemporaryControlAsync(Guid leaseId, CancellationToken cancellationToken = default);
    Task<NativeSceneState> LoadSceneAsync(Guid leaseId, Guid sceneId, CancellationToken cancellationToken = default);
    Task<NativeSceneState> EndTemporaryControlAsync(Guid leaseId, bool restorePreviousScene, CancellationToken cancellationToken = default);
}

public interface INativeControlDispatcher
{
    Task<T> InvokeAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default);
}
