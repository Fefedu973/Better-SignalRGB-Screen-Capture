using Better_SignalRGB_Screen_Capture.Contracts.Services;

namespace Better_SignalRGB_Screen_Capture.Services.NativeOutput;

/// <summary>Owns the optional integration independently of HTTP viewers and capture play/pause.</summary>
internal sealed class NativeIntegrationService(ILocalSettingsService settings, NativeOutputService output,
    NativeApiServer api, NativeControlService control, string profileDirectory) : IDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private NativeConnectionFile? _connection;
    private bool _initialized, _stopped;
    public bool Enabled => output.Status.Enabled;
    public string? Error { get; private set; }
    public string StatusText => Error ?? (Enabled ? output.Status.Status switch
    {
        "ready" => "Native image available for OpenRGB.",
        "paused" => "Capture paused. The last image remains available.",
        "stopped" => "Capture stopped. Scene control remains available.",
        "waiting_for_sources" => "Waiting for a source image. Check source availability.",
        "waiting_for_frame" => "Preparing the native image…",
        "scene_loading" => "Loading the selected scene…",
        _ => output.Status.Error ?? "Native output is starting."
    } : "Native OpenRGB output is off.");
    public string ConnectionPath => Path.Combine(profileDirectory, "NativeOutput", "connection.json");

    public async Task InitializeAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_initialized || _stopped) return;
            _initialized = true;
            if (await settings.ReadSettingAsync<bool?>("NativeOutputEnabled") == true)
                await EnableCoreAsync();
        }
        catch (Exception ex) { Error = $"Native output could not start: {ex.Message}"; }
        finally { _lifecycle.Release(); }
    }
    public async Task SetEnabledAsync(bool enabled)
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_stopped) throw new InvalidOperationException("Application is shutting down.");
            Error = null;
            if (enabled && !Enabled) await EnableCoreAsync();
            else if (!enabled && Enabled) await DisableCoreAsync();
            // Only this explicit UI preference is persistent; leases never change it.
            await settings.SaveSettingAsync("NativeOutputEnabled", enabled);
        }
        catch (Exception ex) { Error = $"Could not change native output: {ex.Message}"; throw; }
        finally { _lifecycle.Release(); }
    }
    private async Task EnableCoreAsync()
    {
        var connection = new NativeConnectionFile(profileDirectory);
        try
        {
            await control.GetStatusAsync();
            output.Start(connection.ChannelPrefix);
            await api.StartAsync(connection);
            _connection = connection;
        }
        catch
        {
            try { await output.StopAsync(); }
            finally
            {
                try { await api.StopAsync(); }
                finally { connection.Dispose(); }
            }
            throw;
        }
    }
    private async Task DisableCoreAsync()
    {
        try
        {
            try { await api.StopAsync(); }
            finally { await control.ReleaseForDisableAsync(); }
        }
        finally
        {
            try { await output.StopAsync(); }
            finally { CloseConnection(); }
        }
    }
    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            _stopped = true;
            try
            {
                try { await api.StopAsync(); }
                finally { await control.StopAsync(); }
            }
            finally
            {
                try { await output.StopAsync(); }
                finally { CloseConnection(); }
            }
        }
        finally { _lifecycle.Release(); }
    }
    private void CloseConnection()
    {
        var connection = _connection;
        _connection = null;
        connection?.Dispose();
    }
    public void Dispose()
    {
        _stopped = true;
        output.Dispose();
        CloseConnection();
    }
}
