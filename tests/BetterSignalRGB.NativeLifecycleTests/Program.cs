using Better_SignalRGB_Screen_Capture.Services.NativeOutput;

var assertions = 0;
void Check(bool condition, string message)
{ assertions++; if (!condition) throw new InvalidOperationException(message); }
async Task FailsAsync(Func<Task> operation, string message)
{
    try { await operation(); }
    catch (IOException) { Check(true, message); return; }
    throw new InvalidOperationException(message);
}

// Launch can resume after tray shutdown while startup was awaiting capture.
var preferences = new LocalSettings { Enabled = true };
var output = new NativeOutputService(); var api = new NativeApiServer(); var control = new NativeControlService();
using (var integration = new NativeIntegrationService(preferences, output, api, control, "synthetic-profile"))
{
    await integration.StopAsync();
    await integration.InitializeAsync();
    Check(preferences.Reads == 0 && output.Starts == 0 && api.Starts == 0,
        "A late launch initialization cannot reopen the native listener after shutdown");
}

// A failed API stop must not strand a lease, and deeper cleanup must still run
// when the control or output boundary independently fails.
foreach (var shutdown in new[] { false, true })
foreach (var failedBoundary in new[] { "api", "control", "output", "api+control" })
{
    preferences = new(); output = new(); api = new(); control = new();
    using var integration = new NativeIntegrationService(preferences, output, api, control, "synthetic-profile");
    await integration.SetEnabledAsync(true);
    var connection = NativeConnectionFile.Latest!;
    if (failedBoundary.Contains("api")) api.StopError = new IOException("Synthetic listener shutdown failure");
    if (failedBoundary.Contains("control"))
    {
        if (shutdown) control.StopError = new IOException("Synthetic control shutdown failure");
        else control.ReleaseError = new IOException("Synthetic lease release failure");
    }
    if (failedBoundary == "output") output.StopError = new IOException("Synthetic output shutdown failure");
    await FailsAsync(() => shutdown ? integration.StopAsync() : integration.SetEnabledAsync(false),
        $"{failedBoundary}: a shutdown error remains visible to the caller");
    Check(api.Stops == 1, $"{failedBoundary}: API cleanup is attempted");
    Check(shutdown ? control.Stops == 1 : control.Releases == 1,
        $"{failedBoundary}: lease cleanup is attempted even when the API fails");
    Check(output.Stops == 1 && !integration.Enabled,
        $"{failedBoundary}: output cleanup is attempted even when control fails");
    Check(connection.Disposed, $"{failedBoundary}: connection ownership is always released");
}

// A partially started listener can fail after output was created. Every rollback
// boundary must run, even if another rollback boundary also fails.
foreach (var rollbackFailure in new[] { "none", "output", "api", "output+api" })
{
    preferences = new(); output = new(); api = new(); control = new();
    api.StartError = new IOException("Synthetic listener startup failure");
    if (rollbackFailure.Contains("output")) output.StopError = new IOException("Synthetic output rollback failure");
    if (rollbackFailure.Contains("api")) api.StopError = new IOException("Synthetic listener rollback failure");
    using var integration = new NativeIntegrationService(preferences, output, api, control, "synthetic-profile");
    await FailsAsync(() => integration.SetEnabledAsync(true), $"{rollbackFailure}: failed activation remains visible");
    Check(output.Stops == 1 && api.Stops == 1, $"{rollbackFailure}: all startup rollback boundaries are attempted");
    Check(NativeConnectionFile.Latest!.Disposed, $"{rollbackFailure}: startup rollback releases connection ownership");
    Check(!preferences.Enabled && !integration.Enabled, $"{rollbackFailure}: failed activation is not persisted as enabled");
}

// An error deleting a descriptor must not leave its released ownership object
// stored for a later Dispose to operate on again.
foreach (var shutdown in new[] { false, true })
{
    preferences = new(); output = new(); api = new(); control = new();
    using var integration = new NativeIntegrationService(preferences, output, api, control, "synthetic-profile");
    await integration.SetEnabledAsync(true);
    var connection = NativeConnectionFile.Latest!;
    connection.DisposeError = new IOException("Synthetic descriptor deletion failure");
    await FailsAsync(() => shutdown ? integration.StopAsync() : integration.SetEnabledAsync(false),
        "Descriptor cleanup failure remains visible");
    integration.Dispose();
    Check(connection.DisposeCalls == 1, "A failed descriptor cleanup clears the retained ownership before a later Dispose");
}

preferences = new(); output = new(); api = new(); control = new();
using (var integration = new NativeIntegrationService(preferences, output, api, control, "synthetic-profile"))
{
    await integration.SetEnabledAsync(true);
    await integration.SetEnabledAsync(false);
    await integration.SetEnabledAsync(true);
    Check(control.Releases == 1 && control.Stops == 0 && output.Starts == 2 && api.Starts == 2,
        "A normal disable releases the lease without permanently stopping the integration");
    Check(preferences.Enabled && integration.Enabled, "Re-enabling persists and starts the integration normally");
    await integration.StopAsync();
}
Console.WriteLine($"PASS: {assertions} native lifecycle assertions; no listeners, files, or UI opened.");
