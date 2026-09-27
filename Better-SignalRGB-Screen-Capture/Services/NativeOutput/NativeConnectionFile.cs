using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Better_SignalRGB_Screen_Capture.Services.NativeOutput;

/// <summary>User-only discovery and credentials. Never served through the browser/streaming API.</summary>
internal sealed class NativeConnectionFile : IDisposable
{
    private readonly FileStream _ownership;
    public string DirectoryPath { get; }
    public string Path => System.IO.Path.Combine(DirectoryPath, "connection.json");
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public string InstanceId { get; } = Guid.NewGuid().ToString("D");
    public string ChannelPrefix { get; }

    public NativeConnectionFile(string profileDirectory)
    {
        DirectoryPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(profileDirectory, "NativeOutput"));
        var identity = WindowsIdentity.GetCurrent();
        using (identity)
        {
            var sid = identity.User ?? throw new IOException("Cannot determine the current Windows user.");
            var directory = new DirectoryInfo(DirectoryPath);
            if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Native output configuration must not be a reparse point.");
            var security = new DirectorySecurity();
            security.SetOwner(sid);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            if (!directory.Exists) FileSystemAclExtensions.Create(directory, security);
            else directory.SetAccessControl(security);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sid.Value + "|" + DirectoryPath.ToUpperInvariant()));
            ChannelPrefix = "BetterCapture-" + Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
        }
        var lockPath = System.IO.Path.Combine(DirectoryPath, "producer.lock");
        RejectReparse(lockPath); RejectReparse(Path);
        // OS handle lifetime also releases ownership after a process crash.
        _ownership = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public void Publish(string baseUrl)
    {
        RejectReparse(Path);
        var temporary = System.IO.Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".tmp");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            apiVersion = 1, baseUrl, authorizationScheme = "Bearer", token = Token,
            instanceId = InstanceId, processId = Environment.ProcessId,
            discoveryPath = "/api/native/v1/discovery"
        });
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { file.Write(bytes); file.Flush(true); }
        File.Move(temporary, Path, overwrite: true);
    }
    private static void RejectReparse(string path)
    {
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Native output configuration must not follow a reparse point.");
    }
    public bool Authenticate(string? authorization)
    {
        if (authorization == null || authorization.Length != Token.Length + 7 || !authorization.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(authorization.AsSpan(7).ToString()), Encoding.ASCII.GetBytes(Token));
    }
    public void Dispose()
    {
        try
        {
            RejectReparse(Path);
            if (File.Exists(Path)) File.Delete(Path);
        }
        finally { _ownership.Dispose(); }
    }
}
