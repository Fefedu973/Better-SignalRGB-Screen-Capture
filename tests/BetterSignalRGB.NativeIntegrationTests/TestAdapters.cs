using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.Services;
using Better_SignalRGB_Screen_Capture.Services.NativeOutput;

namespace Microsoft.UI.Xaml.Media.Imaging { public sealed class BitmapImage { } }
namespace Better_SignalRGB_Screen_Capture.Services
{
    internal static class StreamingCanvasSnapshot
    {
        public const int Width = 320, Height = 200;
        public static Task<StreamingSourceSnapshot[]> CaptureAsync(CancellationToken token) => Task.FromResult(Array.Empty<StreamingSourceSnapshot>());
    }
}
namespace BetterSignalRGB.NativeIntegrationTests
{
    internal sealed class CaptureProducer : ICaptureService
    {
        private readonly ConcurrentDictionary<Guid, byte[]> _frames = new();
        public event EventHandler<SourceFrameEventArgs>? FrameAvailable;
        public event EventHandler<CaptureFailedEventArgs>? CaptureFailed;
        public void Publish(SourceItem source, Color color)
        {
            using var image = new Bitmap(64, 48);
            using (var graphics = Graphics.FromImage(image)) graphics.Clear(color);
            using var stream = new MemoryStream(); image.Save(stream, ImageFormat.Jpeg);
            var jpeg = stream.ToArray(); _frames[source.Id] = jpeg;
            FrameAvailable?.Invoke(this, new SourceFrameEventArgs(source, null) { FrameData = jpeg });
        }
        public void Fail(SourceItem source) { _frames.TryRemove(source.Id, out _); CaptureFailed?.Invoke(this, new(source, "Synthetic source failure")); }
        public Task StartCaptureAsync(SourceItem source) => Task.CompletedTask;
        public Task StopCaptureAsync(SourceItem source) { _frames.TryRemove(source.Id, out _); return Task.CompletedTask; }
        public Task StopAllCapturesAsync() { _frames.Clear(); return Task.CompletedTask; }
        public bool IsCapturing(SourceItem source) => _frames.ContainsKey(source.Id);
        public Task SetFrameRate(int fps) => Task.CompletedTask;
        public Task SetHighQuality(bool enabled) => Task.CompletedTask;
        public byte[]? GetLatestSignalRgbFrame(Guid id) => _frames.GetValueOrDefault(id);
        public byte[]? GetMjpegFrame(Guid id) => _frames.GetValueOrDefault(id);
        public byte[]? GetMjpegFrame() => _frames.Values.FirstOrDefault();
    }
    internal sealed class SavedSettings : ISignalRgbEffectSettingsService
    {
        public SignalRgbEffectSettings Current { get; private set; } = new();
        public event EventHandler<SignalRgbEffectSettings>? Changed;
        public int Writes { get; private set; }
        public Task InitializeAsync() => Task.CompletedTask;
        public Task UpdateAsync(SignalRgbEffectSettings settings) { Writes++; Current = settings.Normalize(); Changed?.Invoke(this, Current); return Task.CompletedTask; }
        public Task ScheduleUpdateAsync(SignalRgbEffectSettings settings) => UpdateAsync(settings);
        public Task FlushAsync() => Task.CompletedTask;
    }
    internal sealed class LocalSettings : ILocalSettingsService
    {
        private readonly ConcurrentDictionary<string, object?> _values = new();
        public Task<T?> ReadSettingAsync<T>(string key) => Task.FromResult(_values.TryGetValue(key, out var value) ? (T?)value : default);
        public Task SaveSettingAsync<T>(string key, T value) { _values[key] = value; return Task.CompletedTask; }
    }
    // This is a deterministic application-context adapter, not a duplicate ViewModel.
    // The real ViewModel's scene/undo/pause/manual-precedence paths have their own linked tests.
    internal sealed class SceneController(CaptureProducer capture) : INativeSceneController
    {
        private readonly object _gate = new();
        public static readonly Guid SceneA = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
        public static readonly Guid SceneB = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");
        public readonly SourceItem Red = new() { CanvasX = 20, CanvasY = 30, CanvasWidth = 100, CanvasHeight = 80, Opacity = .5 };
        public readonly SourceItem Black = new() { CanvasX = 160, CanvasY = 40, CanvasWidth = 60, CanvasHeight = 40 };
        public readonly SourceItem Blue = new() { CanvasX = 20, CanvasY = 30, CanvasWidth = 100, CanvasHeight = 80 };
        private NativeSceneState _state = new(SceneA, "Synthetic A", 0, 1, false, true, false, false, null);
        private NativeSceneState? _saved;
        private Guid? _owner;
        public event EventHandler<NativeSceneState>? StateChanged;
        public NativeSceneState State { get { lock (_gate) return _state; } }
        public StreamingSourceSnapshot[] Layout { get { lock (_gate) return (_state.ActiveSceneId == SceneB ? new[] { Blue } : new[] { Red, Black }).Select(StreamingSourceSnapshot.FromSource).ToArray(); } }
        public void PublishImages() { if (State.ActiveSceneId == SceneB) capture.Publish(Blue, Color.Blue); else { capture.Publish(Red, Color.Red); capture.Publish(Black, Color.Black); } }
        public Task<NativeSceneState> GetStateAsync(CancellationToken token = default) { token.ThrowIfCancellationRequested(); return Task.FromResult(State); }
        public Task<IReadOnlyList<NativeSceneInfo>> ListScenesAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<NativeSceneInfo>>([new(SceneA, "Synthetic A"), new(SceneB, "Synthetic B")]);
        public Task<NativeSceneState> BeginTemporaryControlAsync(Guid id, CancellationToken token = default)
        { lock (_gate) { if (_owner != null) throw new NativeControlException("lease_conflict", "Owned"); _owner = id; _saved = _state; return Task.FromResult(_state); } }
        public Task<NativeSceneState> LoadSceneAsync(Guid id, Guid scene, CancellationToken token = default)
        {
            lock (_gate)
            {
                if (id != _owner) throw new NativeControlException("lease_conflict", "Wrong owner");
                if (scene != SceneA && scene != SceneB) throw new NativeControlException("scene_not_found", "Unknown synthetic scene");
                _state = _state with { ActiveSceneId = scene, ActiveSceneName = scene == SceneA ? "Synthetic A" : "Synthetic B", StateRevision = _state.StateRevision + 1 };
            }
            PublishImages(); StateChanged?.Invoke(this, State); return Task.FromResult(State);
        }
        public Task<NativeSceneState> EndTemporaryControlAsync(Guid id, bool restore, CancellationToken token = default)
        {
            lock (_gate)
            {
                if (_owner == id && restore && _saved != null && _saved.ManualRevision == _state.ManualRevision)
                    _state = _saved with { StateRevision = _state.StateRevision + 1 };
                _saved = null; _owner = null;
            }
            PublishImages(); StateChanged?.Invoke(this, State); return Task.FromResult(State);
        }
        public void SetPlayback(bool recording, bool paused)
        { lock (_gate) _state = _state with { IsRecording = recording, IsPaused = paused, StateRevision = _state.StateRevision + 1 }; StateChanged?.Invoke(this, State); }
    }
    internal sealed class SnapshotGate(SceneController scenes, NativeControlService control)
    {
        private TaskCompletionSource _ready = Ready();
        private static TaskCompletionSource Ready() { var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); ready.SetResult(); return ready; }
        public void Freeze() => _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Thaw() => _ready.TrySetResult();
        public async Task<CompositeRenderSnapshot> ReadAsync(CancellationToken token)
        {
            await _ready.Task.WaitAsync(token);
            var state = control.Current;
            return new(scenes.Layout, new NativeCompositionContext(scenes.State, state.Revision, state.EffectiveSettings));
        }
    }
}
