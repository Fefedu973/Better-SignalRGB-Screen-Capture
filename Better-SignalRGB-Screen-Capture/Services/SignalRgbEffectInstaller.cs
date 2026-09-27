using System.Security.Cryptography;

namespace Better_SignalRGB_Screen_Capture.Services;

public sealed record SignalRgbEffectInstallation(bool BundleAvailable, bool Installed, bool Matches,
    string? Folder, string? BundleRevision, string? InstalledRevision);

/// <summary>Inspects or installs only this application's named effect, on explicit user action.</summary>
public sealed class SignalRgbEffectInstaller
{
    public const string FileName = "Better-SignalRGB-Screen-Capture-Effect.html";
    private readonly string _bundle;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SignalRgbEffectInstaller() : this(Path.Combine(AppContext.BaseDirectory, FileName)) { }
    internal SignalRgbEffectInstaller(string bundledFile) => _bundle = Path.GetFullPath(bundledFile);

    public static string? DetectEffectsFolder(string? savedFolder = null) => DetectEffectsFolder(savedFolder,
        GetDocumentFolders(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            new[] { Environment.GetEnvironmentVariable("OneDrive"), Environment.GetEnvironmentVariable("OneDriveConsumer"),
                Environment.GetEnvironmentVariable("OneDriveCommercial") }.Concat(GetOneDriveAccountFolders())),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VortxEngine"));

    // Only current-user known folders and registered OneDrive roots are candidates.
    // Never recursively scan the profile or a drive looking for similarly named folders.
    internal static string[] GetDocumentFolders(string documents, string userProfile, IEnumerable<string?> oneDriveRoots)
    {
        var folders = new List<string>();
        void Add(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathRooted(folder)) return;
            try { folders.Add(Path.GetFullPath(folder)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
        }
        Add(documents);
        foreach (var root in oneDriveRoots)
            if (!string.IsNullOrWhiteSpace(root)) Add(Path.Combine(root, "Documents"));
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            Add(Path.Combine(userProfile, "Documents"));
            // OneDrive can remain configured on disk even when its environment variables
            // are missing from an application launched before the sync client started.
            Add(Path.Combine(userProfile, "OneDrive", "Documents"));
        }
        return folders.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string?> GetOneDriveAccountFolders()
    {
        var folders = new List<string?>();
        try
        {
            using var accounts = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
            if (accounts == null) return folders;
            foreach (var accountName in accounts.GetSubKeyNames())
            {
                using var account = accounts.OpenSubKey(accountName);
                if (account?.GetValue("UserFolder") is string folder) folders.Add(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return folders;
    }

    internal static string? DetectEffectsFolder(string? savedFolder, IEnumerable<string> documentFolders, string engineFolder)
    {
        if (!string.IsNullOrWhiteSpace(savedFolder) && Directory.Exists(savedFolder)) return Path.GetFullPath(savedFolder);
        var userFolder = documentFolders.Select(folder => Path.Combine(folder, "WhirlwindFX", "Effects"))
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists)
            .OrderByDescending(CustomEffectsScore).FirstOrDefault();
        if (userFolder != null) return userFolder;
        try
        {
            if (Directory.Exists(engineFolder))
            {
                var candidate = Directory.EnumerateDirectories(engineFolder, "app-*")
                    .OrderByDescending(path => Version.TryParse(Path.GetFileName(path)[4..].Split('-')[0], out var version) ? version : new Version())
                    .ThenByDescending(Directory.GetLastWriteTimeUtc)
                    .Select(path => Path.Combine(path, "Signal-x64", "Effects", "Dynamic"))
                    .FirstOrDefault(Directory.Exists);
                if (candidate != null) return candidate;
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static int CustomEffectsScore(string folder)
    {
        if (File.Exists(Path.Combine(folder, FileName))) return 3;
        if (File.Exists(Path.Combine(folder, "better-signalrgb-screen-capture.html")) ||
            File.Exists(Path.Combine(folder, "screen-ambiance-v3.html"))) return 2;
        try { return Directory.EnumerateFiles(folder, "*.html", SearchOption.TopDirectoryOnly).Any() ? 1 : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    public async Task<SignalRgbEffectInstallation> InspectAsync(string? folder)
    {
        var target = string.IsNullOrWhiteSpace(folder) ? null : Path.Combine(Path.GetFullPath(folder), FileName);
        var bundleHash = await HashIfPresentAsync(_bundle).ConfigureAwait(false);
        var installedHash = target == null ? null : await HashIfPresentAsync(target).ConfigureAwait(false);
        return new(bundleHash != null, installedHash != null, bundleHash != null && bundleHash == installedHash,
            target == null ? null : Path.GetDirectoryName(target), bundleHash?[..12], installedHash?[..12]);
    }

    public async Task<string?> InstallAsync(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        await _gate.WaitAsync().ConfigureAwait(false);
        string? temporary = null;
        try
        {
            var destination = Path.GetFullPath(folder);
            if (!Directory.Exists(destination)) throw new DirectoryNotFoundException("Choose an existing SignalRGB effects folder first.");
            var target = Path.Combine(destination, FileName);
            var status = await InspectAsync(destination).ConfigureAwait(false);
            if (!status.BundleAvailable) throw new FileNotFoundException("The matching effect is missing beside the application executable.", _bundle);
            if (status.Matches) return null;
            string? backup = null;
            temporary = Path.Combine(destination, $".{FileName}.{Guid.NewGuid():N}.tmp");
            await using (var source = File.OpenRead(_bundle))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await source.CopyToAsync(output).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            if (File.Exists(target))
            {
                // Keep the previous version outside the .html extension so SignalRGB cannot load it twice.
                backup = target + $".{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.backup";
                File.Copy(target, backup, overwrite: false);
            }
            File.Move(temporary, target, overwrite: true);
            temporary = null;
            return backup;
        }
        finally
        {
            if (temporary != null)
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            _gate.Release();
        }
    }

    private static async Task<string?> HashIfPresentAsync(string path)
    {
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
    }
}
