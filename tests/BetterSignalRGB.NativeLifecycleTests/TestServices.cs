using Better_SignalRGB_Screen_Capture.Contracts.Services;

namespace Better_SignalRGB_Screen_Capture.Services.NativeOutput;

// Link the production lifecycle owner, replacing only OS/service boundaries so
// shutdown failures can be injected without opening a listener or a user profile.
internal sealed class NativeConnectionFile : IDisposable
{
    public NativeConnectionFile(string profileDirectory)
    { ChannelPrefix = profileDirectory; Latest = this; }
    public static NativeConnectionFile? Latest { get; private set; }
    public string ChannelPrefix { get; }
    public bool Disposed { get; private set; }
    public int DisposeCalls { get; private set; }
    public Exception? DisposeError { get; set; }
    public void Dispose()
    {
        DisposeCalls++; Disposed = true;
        if (DisposeError != null) throw DisposeError;
    }
}

internal sealed class NativeOutputService : IDisposable
{
    public sealed record OutputStatus(bool Enabled, string Status, string? Error);
    public OutputStatus Status { get; private set; } = new(false, "disabled", null);
    public int Starts { get; private set; }
    public int Stops { get; private set; }
    public Exception? StopError { get; set; }
    public void Start(string channelPrefix) { Starts++; Status = new(true, "ready", null); }
    public Task StopAsync()
    {
        Stops++; Status = new(false, "disabled", null);
        return StopError == null ? Task.CompletedTask : Task.FromException(StopError);
    }
    public void Dispose() { }
}

internal sealed class NativeApiServer
{
    public int Starts { get; private set; }
    public int Stops { get; private set; }
    public Exception? StartError { get; set; }
    public Exception? StopError { get; set; }
    public Task StartAsync(NativeConnectionFile connection)
    { Starts++; return StartError == null ? Task.CompletedTask : Task.FromException(StartError); }
    public Task StopAsync()
    { Stops++; return StopError == null ? Task.CompletedTask : Task.FromException(StopError); }
}

internal sealed class NativeControlService
{
    public int Releases { get; private set; }
    public int Stops { get; private set; }
    public Exception? ReleaseError { get; set; }
    public Exception? StopError { get; set; }
    public Task GetStatusAsync() => Task.CompletedTask;
    public Task ReleaseForDisableAsync()
    { Releases++; return ReleaseError == null ? Task.CompletedTask : Task.FromException(ReleaseError); }
    public Task StopAsync()
    { Stops++; return StopError == null ? Task.CompletedTask : Task.FromException(StopError); }
}

internal sealed class LocalSettings : ILocalSettingsService
{
    public bool Enabled { get; set; }
    public int Reads { get; private set; }
    public Task<T?> ReadSettingAsync<T>(string key)
    { Reads++; return Task.FromResult((T?)(object)Enabled); }
    public Task SaveSettingAsync<T>(string key, T value)
    { Enabled = (bool)(object)value!; return Task.CompletedTask; }
}
