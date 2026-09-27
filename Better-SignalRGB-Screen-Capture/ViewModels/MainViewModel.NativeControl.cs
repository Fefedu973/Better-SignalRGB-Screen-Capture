using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Models;

namespace Better_SignalRGB_Screen_Capture.ViewModels;

public partial class MainViewModel
{
    private readonly Dictionary<Guid, SceneSourceSnapshot> _nativeTrackedSources = new();
    private Guid? _nativeLeaseId, _activeNativeSceneId, _nativePreviousSceneId;
    private SourceItem[]? _nativePreviousSources;
    private UndoRedoManager.Checkpoint? _nativePreviousHistory;
    private long _nativeManualRevision, _nativeStateRevision, _nativeLeaseManualRevision, _nativeStopRevision;
    private bool _nativeTemporaryScene, _nativeSceneLoading;

    /// <summary>Read only on the UI context, alongside the source snapshots used for the same frame.</summary>
    public NativeSceneState NativeState => new(_activeNativeSceneId,
        _sceneLibrary.Profiles.FirstOrDefault(scene => scene.Id == _activeNativeSceneId)?.Name,
        _nativeManualRevision, _nativeStateRevision, _nativeSceneLoading,
        IsRecording, IsPaused, _shuttingDown || (!_captureRequested && _captureLifecycle.CurrentCount == 0), StatusMessage);
    public event EventHandler<NativeSceneState>? NativeSceneStateChanged;

    private void InitializeNativeControl()
    {
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(IsRecording) or nameof(IsPaused) or nameof(IsRecordingLoading) or nameof(StatusMessage))
                PublishNativeState();
        };
    }
    private void PublishNativeState() => NativeSceneStateChanged?.Invoke(this, NativeState);
    private void MarkNativeManualChange()
    {
        if (_isInitializing || _isUndoRedoOperation || _nativeSceneLoading) return;
        // The first actual user edit promotes the current composition to user-owned
        // state. Its normal saves must not remain suspended for the rest of a lease.
        _nativeTemporaryScene = false;
        _activeNativeSceneId = null;
        _nativeManualRevision++; _nativeStateRevision++;
        PublishNativeState();
    }
    private void NativeSourceChanged(SourceItem source, string? propertyName)
    {
        // Preview frames/status/selection can change at capture rate. They are not
        // persistent scene edits and must not allocate source snapshots.
        if (!string.IsNullOrEmpty(propertyName) && propertyName is not (
            nameof(SourceItem.Id) or nameof(SourceItem.Name) or nameof(SourceItem.Type) or nameof(SourceItem.MonitorDeviceId)
            or nameof(SourceItem.ProcessId) or nameof(SourceItem.ProcessPath) or nameof(SourceItem.WindowHandle)
            or nameof(SourceItem.WindowTitle) or nameof(SourceItem.RegionBounds) or nameof(SourceItem.WebcamDeviceId)
            or nameof(SourceItem.WebcamFormatId) or nameof(SourceItem.WebsiteUrl) or nameof(SourceItem.WebsiteZoom)
            or nameof(SourceItem.WebsiteRefreshInterval) or nameof(SourceItem.WebsiteUserAgent) or nameof(SourceItem.WebsiteWidth)
            or nameof(SourceItem.WebsiteHeight) or nameof(SourceItem.WebsiteNavigationState) or nameof(SourceItem.CanvasX)
            or nameof(SourceItem.CanvasY) or nameof(SourceItem.CanvasWidth) or nameof(SourceItem.CanvasHeight)
            or nameof(SourceItem.Rotation) or nameof(SourceItem.Opacity) or nameof(SourceItem.CropLeftPct)
            or nameof(SourceItem.CropTopPct) or nameof(SourceItem.CropRightPct) or nameof(SourceItem.CropBottomPct)
            or nameof(SourceItem.CropRotation) or nameof(SourceItem.IsMirroredHorizontally)
            or nameof(SourceItem.IsMirroredVertically) or nameof(SourceItem.IsLocked))) return;
        var snapshot = SceneSourceSnapshot.Capture(source);
        if (_nativeTrackedSources.TryGetValue(source.Id, out var previous) && previous == snapshot) return;
        _nativeTrackedSources[source.Id] = snapshot;
        MarkNativeManualChange();
    }
    private void NativeSourceCollectionChanged()
    {
        _nativeTrackedSources.Clear();
        foreach (var source in Sources) _nativeTrackedSources[source.Id] = SceneSourceSnapshot.Capture(source);
        MarkNativeManualChange();
    }
    private void MarkNativeSceneEffective(Guid? sceneId, bool manual)
    {
        if (manual) _nativeManualRevision++;
        _activeNativeSceneId = sceneId.HasValue && _sceneLibrary.Profiles.Any(scene => scene.Id == sceneId) ? sceneId : null;
        _nativeStateRevision++;
        PublishNativeState();
    }

    public async Task<IReadOnlyList<NativeSceneInfo>> GetNativeScenesAsync()
    {
        await ScenesInitialization;
        if (!IsSceneLibraryReady) throw new NativeControlException("unavailable", "The saved scene library is unavailable.");
        return _sceneLibrary.Profiles.Select(scene => new NativeSceneInfo(scene.Id, scene.Name)).ToArray();
    }
    private void RequireNativeEditable()
    {
        if (_shuttingDown) throw new NativeControlException("unavailable", "The application is shutting down.");
        if (!IsCanvasEditable || IsSceneBusy || _captureLifecycle.CurrentCount == 0)
            throw new NativeControlException("scene_busy", "Wait for the current capture or canvas operation to finish.");
    }
    private void RequireNativeOwner(Guid leaseId)
    {
        if (_nativeLeaseId != leaseId) throw new NativeControlException("lease_conflict", "The request does not own the canvas.");
    }
    public async Task<NativeSceneState> BeginNativeControlAsync(Guid leaseId)
    {
        if (leaseId == Guid.Empty) throw new NativeControlException("invalid_request", "A lease identifier is required.");
        await Initialization; await ScenesInitialization;
        RequireNativeEditable();
        if (_nativeLeaseId != null) throw new NativeControlException("lease_conflict", "The canvas already has a temporary owner.");
        _nativeLeaseId = leaseId; _nativeLeaseManualRevision = _nativeManualRevision;
        _nativePreviousSources = Sources.Select(source => source.Clone(preserveId: true)).ToArray();
        _nativePreviousSceneId = _activeNativeSceneId;
        _nativePreviousHistory = _undoRedoManager.CaptureCheckpoint();
        PublishNativeState(); return NativeState;
    }
    public async Task<NativeSceneState> LoadNativeSceneAsync(Guid leaseId, Guid sceneId)
    {
        RequireNativeOwner(leaseId); RequireNativeEditable();
        if (_nativeManualRevision != _nativeLeaseManualRevision)
            throw new NativeControlException("manual_override", "The user edited the canvas after this lease began.");
        var scene = _sceneLibrary.Profiles.FirstOrDefault(item => item.Id == sceneId)
            ?? throw new NativeControlException("scene_not_found", "The requested saved scene does not exist.");
        // Finish a real user save before suppressing writes from the temporary scene.
        await _pendingSourceSave;
        RequireNativeOwner(leaseId); RequireNativeEditable();
        if (_nativeManualRevision != _nativeLeaseManualRevision)
            throw new NativeControlException("manual_override", "The user edited the canvas while the request was waiting.");
        _nativeTemporaryScene = true;
        SaveUndoState();
        if (!await RestoreState(scene.Sources.Select(source => source.Restore()).ToArray(), temporary: true, activeSceneId: scene.Id))
            throw new NativeControlException("scene_busy", "The canvas became unavailable before loading the scene.");
        return NativeState;
    }
    public async Task<NativeSceneState> EndNativeControlAsync(Guid leaseId, bool restorePreviousScene)
    {
        RequireNativeOwner(leaseId);
        if (!_shuttingDown) RequireNativeEditable();
        var changedManually = _nativeManualRevision != _nativeLeaseManualRevision;
        if (_nativeTemporaryScene && restorePreviousScene && !changedManually && !_shuttingDown)
        {
            if (!await RestoreState(_nativePreviousSources!, temporary: true, activeSceneId: _nativePreviousSceneId))
                throw new NativeControlException("scene_busy", "The previous scene could not be restored while the canvas is busy.");
            _undoRedoManager.RestoreCheckpoint(_nativePreviousHistory!);
        }
        _nativeTemporaryScene = false; _nativeLeaseId = null;
        _nativePreviousSources = null; _nativePreviousHistory = null; _nativePreviousSceneId = null;
        // A final save also covers an explicit saved-scene action during the lease.
        // Normal manual canvas edits already re-enabled persistence immediately.
        if (changedManually && !_shuttingDown) await SaveSourcesAsync();
        PublishNativeState(); return NativeState;
    }
}
