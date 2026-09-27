using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Windows.Storage;

namespace Better_SignalRGB_Screen_Capture.Helpers;

/// <summary>Best-effort local crash evidence, available before dependency injection and XAML initialization.</summary>
internal static class ApplicationErrorLog
{
    private static readonly object Gate = new();
    private const int MaximumFileBytes = 256 * 1024;
    private const int MaximumEntryCharacters = 32 * 1024;
    private static string? _folder;

    public static bool Initialize()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Write("AppDomain.UnhandledException",
            args.ExceptionObject as Exception, $"IsTerminating={args.IsTerminating}");
        TaskScheduler.UnobservedTaskException += (_, args) => Write("TaskScheduler.UnobservedTaskException", args.Exception);
        Write("Startup", detail: $"Process={Environment.ProcessId}; Version={typeof(ApplicationErrorLog).Assembly.GetName().Version}; BaseDirectory={AppContext.BaseDirectory}");
        return true;
    }

    public static void Configure(string? configuredDataFolder)
    {
        lock (Gate)
        {
            try { _folder = ResolveFolder(configuredDataFolder); }
            catch (Exception ex) { Debug.WriteLine($"Error log location unavailable: {ex.Message}"); }
        }
    }

    public static void Write(string context, Exception? exception = null, string? detail = null)
    {
        try
        {
            var text = $"{DateTimeOffset.UtcNow:O} [{context}] {detail}{Environment.NewLine}{exception}{Environment.NewLine}";
            if (text.Length > MaximumEntryCharacters) text = text[..MaximumEntryCharacters] + "\n[truncated]\n";
            var bytes = Encoding.UTF8.GetBytes(text);
            lock (Gate)
            {
                _folder ??= ResolveFolder(null);
                Directory.CreateDirectory(_folder);
                var path = Path.Combine(_folder, "ApplicationErrors.log");
                if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > MaximumFileBytes)
                    File.Move(path, Path.Combine(_folder, "ApplicationErrors.previous.log"), overwrite: true);
                using var output = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
        }
        catch (Exception error)
        {
            // Logging must neither replace the original error nor turn a recoverable error into a crash.
            Debug.WriteLine($"Could not write application error log: {error.Message}");
        }
    }

    private static string ResolveFolder(string? configuredDataFolder)
    {
        var environmentFolder = Environment.GetEnvironmentVariable("LocalSettingsOptions__ApplicationDataFolder");
        string dataFolder;
        if (!string.IsNullOrWhiteSpace(environmentFolder))
            dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), environmentFolder);
        else if (RuntimeHelper.IsMSIX)
            dataFolder = ApplicationData.Current.LocalFolder.Path;
        else
        {
            if (string.IsNullOrWhiteSpace(configuredDataFolder))
            {
                var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
                if (File.Exists(settingsPath) && new FileInfo(settingsPath).Length <= 64 * 1024)
                {
                    using var settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
                    if (settings.RootElement.TryGetProperty("LocalSettingsOptions", out var options) &&
                        options.TryGetProperty("ApplicationDataFolder", out var folder) && folder.ValueKind == JsonValueKind.String)
                        configuredDataFolder = folder.GetString();
                }
            }
            dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                configuredDataFolder ?? "Better-SignalRGB-Screen-Capture/ApplicationData");
        }
        return Path.Combine(dataFolder, "Logs");
    }
}
