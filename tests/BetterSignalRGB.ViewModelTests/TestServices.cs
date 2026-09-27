using System.Collections.Concurrent;
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.ViewModels;
using Better_SignalRGB_Screen_Capture.Services;

namespace BetterSignalRGB.ViewModelTests;

internal sealed class AsyncGate
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Entered => _entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
    public Task WaitAsync() { _entered.TrySetResult(); return _released.Task; }
    public void Release() => _released.TrySetResult();
}

internal sealed class TestSettings : ILocalSettingsService
{
    public Dictionary<string, object?> Values { get; } = new();
    public HashSet<string> ReadFailures { get; } = new();
    public HashSet<string> WriteFailures { get; } = new();
    public ConcurrentQueue<(string Key, object? Value)> Writes { get; } = new();
    public AsyncGate? SourceSaveGate;
    public Task<T?> ReadSettingAsync<T>(string key)
    {
        if (ReadFailures.Contains(key)) throw new IOException($"Read failed: {key}");
        return Task.FromResult(Values.TryGetValue(key, out var value) ? (T?)value : default);
    }
    public async Task SaveSettingAsync<T>(string key, T value)
    {
        if (WriteFailures.Contains(key)) throw new IOException($"Write failed: {key}");
        if (key == "SavedSources" && SourceSaveGate is { } gate) await gate.WaitAsync();
        Writes.Enqueue((key, value));
    }
    public SourceItem[]? LastSources => Writes.Where(write => write.Key == "SavedSources").Select(write => write.Value as SourceItem[]).LastOrDefault();
}

internal sealed class TestCapture(ConcurrentQueue<string> calls) : ICaptureService
{
    public ConcurrentDictionary<Guid, SourceItem> Active { get; } = new();
    public ConcurrentDictionary<Guid, int> StartCounts { get; } = new();
    public ConcurrentDictionary<Guid, Exception> StartFailures { get; } = new();
    public AsyncGate? StartGate;
    public AsyncGate? StopGate;
    public Action<SourceItem>? Started;
    public Action<SourceItem>? Stopped;
    public int FrameRate;
    public bool HighQuality;
    public Exception? QualityFailure;
    public AsyncGate? QualityGate;
    public event EventHandler<SourceFrameEventArgs>? FrameAvailable { add { } remove { } }
    public event EventHandler<CaptureFailedEventArgs>? CaptureFailed;
    public async Task StartCaptureAsync(SourceItem source)
    {
        calls.Enqueue($"capture:start:{source.Id}");
        StartCounts.AddOrUpdate(source.Id, 1, (_, count) => count + 1);
        if (StartGate is { } gate) await gate.WaitAsync();
        if (StartFailures.TryGetValue(source.Id, out var error)) throw error;
        Active[source.Id] = source;
        Started?.Invoke(source);
    }
    public async Task StopCaptureAsync(SourceItem source)
    {
        calls.Enqueue($"capture:stop:{source.Id}");
        if (StopGate is { } gate) await gate.WaitAsync();
        Active.TryRemove(source.Id, out _);
        Stopped?.Invoke(source);
    }
    public Task StopAllCapturesAsync()
    { calls.Enqueue("capture:stop-all"); Active.Clear(); return Task.CompletedTask; }
    public bool IsCapturing(SourceItem source) => Active.ContainsKey(source.Id);
    public Task SetFrameRate(int fps) { FrameRate = fps; return Task.CompletedTask; }
    public async Task SetHighQuality(bool enabled)
    {
        calls.Enqueue($"capture:quality:{enabled}");
        if (!Active.IsEmpty) throw new InvalidOperationException("Quality changed before captures stopped");
        if (QualityGate is { } gate) await gate.WaitAsync();
        if (QualityFailure is { } failure) throw failure;
        HighQuality = enabled;
    }
    public byte[]? GetLatestSignalRgbFrame(Guid sourceId) => GetMjpegFrame(sourceId);
    public byte[]? GetMjpegFrame(Guid sourceId) => null;
    public byte[]? GetMjpegFrame() => null;
    public void Fail(SourceItem source, string error)
    { Active.TryRemove(source.Id, out _); CaptureFailed?.Invoke(this, new CaptureFailedEventArgs(source, error)); }
}

internal sealed class TestHttp(ConcurrentQueue<string> calls) : IMjpegStreamingService
{
    public event EventHandler<string>? StreamingUrlChanged;
    public bool IsStreaming { get; private set; }
    public string? StreamingUrl { get; private set; }
    public int Port;
    public Exception? StartFailure;
    public Exception? NextStartFailure;
    public ConcurrentQueue<Guid> Removed { get; } = new();
    public Task StartStreamingAsync(int port = 8080)
    {
        calls.Enqueue("http:start");
        if (NextStartFailure is { } failure) { NextStartFailure = null; throw failure; }
        if (StartFailure != null) throw StartFailure;
        Port = port; IsStreaming = true; StreamingUrl = $"http://localhost:{port}/canvas/";
        StreamingUrlChanged?.Invoke(this, StreamingUrl); return Task.CompletedTask;
    }
    public Task StopStreamingAsync()
    { calls.Enqueue("http:stop"); IsStreaming = false; StreamingUrl = null; StreamingUrlChanged?.Invoke(this, string.Empty); return Task.CompletedTask; }
    public void UpdateSourceFrame(Guid sourceId, byte[] jpegData) { }
    public Task NotifySourceRemovedAsync(Guid sourceId) { Removed.Enqueue(sourceId); return Task.CompletedTask; }
    public Task ResetCanvasAsync() { calls.Enqueue("http:reset"); return Task.CompletedTask; }
}

internal sealed class TestHttps(ConcurrentQueue<string> calls) : IKestrelApiService
{
    public event EventHandler<string>? StreamingUrlChanged;
    public bool IsRunning { get; private set; }
    public string? StreamingUrl { get; private set; }
    public int Port;
    public Exception? StartFailure;
    public ConcurrentQueue<Guid> Removed { get; } = new();
    public Task StartAsync(int httpsPort = 8443)
    {
        calls.Enqueue("https:start");
        if (StartFailure != null) throw StartFailure;
        Port = httpsPort; IsRunning = true; StreamingUrl = $"https://localhost:{httpsPort}/canvas/";
        StreamingUrlChanged?.Invoke(this, StreamingUrl); return Task.CompletedTask;
    }
    public Task StopAsync()
    { calls.Enqueue("https:stop"); IsRunning = false; StreamingUrl = null; StreamingUrlChanged?.Invoke(this, string.Empty); return Task.CompletedTask; }
    public void RemoveSource(Guid id) => Removed.Enqueue(id);
}

internal sealed class TestComposite : ICompositeFrameService
{
    public event EventHandler<byte[]>? CompositeFrameAvailable { add { } remove { } }
    public int Invalidations;
    public (int Width, int Height) Size;
    public ConcurrentQueue<Guid> Removed { get; } = new();
    public void UpdateSourceFrame(SourceItem source, byte[] frameData) { }
    public void RemoveSource(SourceItem source) => Removed.Enqueue(source.Id);
    public void SetCanvasSize(int width, int height) => Size = (width, height);
    public void InvalidateLayout() => Interlocked.Increment(ref Invalidations);
    public byte[]? GetLatestCompositeFrame() => null;
}

internal sealed class TestContext : IAsyncDisposable
{
    public ConcurrentQueue<string> Calls { get; } = new();
    public TestSettings Settings { get; }
    public TestCapture Capture { get; }
    public TestHttp Http { get; }
    public TestHttps Https { get; }
    public TestComposite Composite { get; } = new();
    public MainViewModel ViewModel { get; }
    public TestSceneStorage SceneStorage { get; } = new();
    public SceneLibraryService SceneLibrary { get; }
    public TestSceneFiles SceneFiles { get; } = new();
    public PipelineDiagnosticsService Diagnostics { get; } = new();
    public TestContext(TestSettings? settings = null)
    {
        Settings = settings ?? new(); Capture = new(Calls); Http = new(Calls); Https = new(Calls);
        SceneLibrary = new(SceneStorage);
        ViewModel = new(Settings, Capture, Http, Composite, Https, SceneLibrary, SceneFiles, Diagnostics);
    }
    public async ValueTask DisposeAsync()
    {
        Capture.StartGate?.Release(); Capture.StopGate?.Release(); Capture.QualityGate?.Release(); Settings.SourceSaveGate?.Release();
        await ViewModel.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(3));
    }
}

internal sealed class TestSceneStorage : ISceneLibraryStorage
{
    public SceneLibraryDocument? Document;
    public bool FailWrites;
    public Task<SceneLibraryDocument?> ReadAsync() => Task.FromResult(Document);
    public Task WriteAsync(SceneLibraryDocument document)
    {
        if (FailWrites) throw new IOException("Scene persistence failed");
        Document = document;
        return Task.CompletedTask;
    }
}

internal sealed class TestSceneFiles : ISceneFilePickerService
{
    public string? ImportJson;
    public string? ExportedJson;
    public Task<string?> ImportAsync() => Task.FromResult(ImportJson);
    public Task<bool> ExportAsync(string name, string json) { ExportedJson = json; return Task.FromResult(true); }
}
