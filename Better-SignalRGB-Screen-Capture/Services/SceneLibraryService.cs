using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.Core.Helpers;

namespace Better_SignalRGB_Screen_Capture.Services;

public sealed class SceneLibraryService(ISceneLibraryStorage storage) : ISceneLibraryService
{
    public const int MaxFileBytes = 2 * 1024 * 1024;
    public const int MaxSources = 128;
    private const int MaxScenes = 100;
    private readonly SemaphoreSlim _access = new(1, 1);
    private IReadOnlyList<SceneProfile> _profiles = Array.Empty<SceneProfile>();
    private bool _initialized;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<SourceType>(allowIntegerValues: false) }
    };
    public IReadOnlyList<SceneProfile> Profiles => Volatile.Read(ref _profiles);

    public async Task InitializeAsync()
    {
        await _access.WaitAsync();
        try { await InitializeCoreAsync(); }
        finally { _access.Release(); }
    }

    public async Task FlushAsync()
    {
        await _access.WaitAsync();
        _access.Release();
    }

    private async Task InitializeCoreAsync()
    {
        if (_initialized) return;
        var document = await storage.ReadAsync();
        if (document != null)
        {
            if (document.Version != 1 || document.Scenes == null || document.Scenes.Count > MaxScenes)
                throw new InvalidDataException("This scene library has an unsupported format.");
            var scenes = document.Scenes.Select(Validate).ToArray();
            if (scenes.Select(scene => scene.Id).Distinct().Count() != scenes.Length ||
                scenes.Select(scene => scene.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != scenes.Length)
                throw new InvalidDataException("The scene library contains duplicate names or identifiers.");
            _profiles = Array.AsReadOnly(scenes);
        }
        _initialized = true;
    }

    public async Task<SceneProfile> SaveNewAsync(string name, IEnumerable<SceneSourceSnapshot> sources)
    {
        var profile = Validate(new() { Id = Guid.NewGuid(), Name = name, Sources = sources.ToArray() });
        await _access.WaitAsync();
        try
        {
            await InitializeCoreAsync();
            EnsureNewName(profile.Name);
            if (_profiles.Count >= MaxScenes) throw new InvalidOperationException($"The library can hold up to {MaxScenes} scenes.");
            await SaveCoreAsync(_profiles.Append(profile));
            return profile;
        }
        finally { _access.Release(); }
    }

    public async Task<SceneProfile> ReplaceAsync(Guid id, IEnumerable<SceneSourceSnapshot> sources)
    {
        var snapshot = sources.ToArray();
        await _access.WaitAsync();
        try
        {
            await InitializeCoreAsync();
            var replacement = Validate(Find(id) with { Sources = snapshot });
            await SaveCoreAsync(_profiles.Select(scene => scene.Id == id ? replacement : scene));
            return replacement;
        }
        finally { _access.Release(); }
    }

    public async Task<SceneProfile> RenameAsync(Guid id, string name)
    {
        await _access.WaitAsync();
        try
        {
            await InitializeCoreAsync();
            var renamed = Validate(Find(id) with { Name = name });
            EnsureNewName(renamed.Name, id);
            await SaveCoreAsync(_profiles.Select(scene => scene.Id == id ? renamed : scene));
            return renamed;
        }
        finally { _access.Release(); }
    }

    public async Task DeleteAsync(Guid id)
    {
        await _access.WaitAsync();
        try
        {
            await InitializeCoreAsync();
            _ = Find(id);
            await SaveCoreAsync(_profiles.Where(scene => scene.Id != id));
        }
        finally { _access.Release(); }
    }

    public async Task<SceneProfile> ImportAsync(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaxFileBytes)
            throw new InvalidDataException("Choose a non-empty scene JSON file smaller than 2 MB.");
        SceneExportDocument document;
        try { document = JsonSerializer.Deserialize<SceneExportDocument>(json, JsonOptions) ?? throw new JsonException(); }
        catch (JsonException ex) { throw new InvalidDataException("This file is not a valid scene JSON document.", ex); }
        if (document.Format != "BetterSignalRGB.Scene" || document.Version != 1 || document.CanvasWidth != 320 || document.CanvasHeight != 200)
            throw new InvalidDataException("This scene uses an unsupported format, version or canvas size.");
        var imported = Validate(document.Scene ?? throw new InvalidDataException("The file does not contain a scene."));
        await _access.WaitAsync();
        try
        {
            await InitializeCoreAsync();
            if (_profiles.Count >= MaxScenes) throw new InvalidOperationException($"The library can hold up to {MaxScenes} scenes.");
            var name = imported.Name;
            for (var suffix = 2; _profiles.Any(scene => string.Equals(scene.Name, name, StringComparison.OrdinalIgnoreCase)); suffix++)
                name = imported.Name[..Math.Min(imported.Name.Length, 54)] + $" ({suffix})";
            imported = imported with { Id = Guid.NewGuid(), Name = name };
            await SaveCoreAsync(_profiles.Append(imported));
            return imported;
        }
        finally { _access.Release(); }
    }

    public string Export(Guid id)
    {
        var json = JsonSerializer.Serialize(new SceneExportDocument { Scene = Find(id) }, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaxFileBytes) throw new InvalidOperationException("This scene exceeds the 2 MB export limit.");
        return json;
    }

    private SceneProfile Find(Guid id) => _profiles.FirstOrDefault(scene => scene.Id == id)
        ?? throw new InvalidOperationException("This scene is no longer in the library.");

    private void EnsureNewName(string name, Guid? except = null)
    {
        if (_profiles.Any(scene => scene.Id != except && string.Equals(scene.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A scene with this name already exists. Choose another name or replace the selected scene.");
    }

    private async Task SaveCoreAsync(IEnumerable<SceneProfile> scenes)
    {
        var next = Array.AsReadOnly(scenes.ToArray());
        await storage.WriteAsync(new() { Scenes = next });
        Volatile.Write(ref _profiles, next);
    }

    private static SceneProfile Validate(SceneProfile scene)
    {
        if (scene == null || scene.Id == Guid.Empty) throw new InvalidDataException("A scene must have a valid identifier.");
        var name = scene.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 64 || name.Any(char.IsControl))
            throw new InvalidDataException("Scene names must contain 1 to 64 characters without control characters.");
        if (scene.Sources == null || scene.Sources.Count > MaxSources)
            throw new InvalidDataException($"A scene can contain at most {MaxSources} sources.");
        var sources = scene.Sources.ToArray();
        var ids = new HashSet<Guid>();
        foreach (var source in sources)
        {
            if (source == null || source.Id == Guid.Empty || !ids.Add(source.Id)) throw new InvalidDataException("Source identifiers must be unique and non-empty.");
            if (!Enum.IsDefined(source.Type)) throw new InvalidDataException("A scene contains an unknown source type.");
            if (source.Name == null || source.Name.Length > 512 || TooLong(source.MonitorDeviceId, 4096) || TooLong(source.WebcamDeviceId, 4096)
                || TooLong(source.ProcessPath, 4096) || TooLong(source.WindowTitle, 4096) || TooLong(source.WebcamFormatId, 256)
                || TooLong(source.WebsiteUrl, 8192) || TooLong(source.WebsiteUserAgent, 4096)
                || TooLong(source.WebsiteNavigationState, 65536)) throw new InvalidDataException("A source contains an excessively long value.");
            if (source.CanvasWidth is < 1 or > 7680 || source.CanvasHeight is < 1 or > 4320
                || source.CanvasX is < -7680 or > 8000 || source.CanvasY is < -4320 or > 4520
                || source.Rotation is < 0 or >= 360 || source.CropRotation is < -180 or >= 180
                || !Range(source.Opacity, 0, 1) || !Range(source.CropLeftPct, 0, .99) || !Range(source.CropRightPct, 0, .99)
                || !Range(source.CropTopPct, 0, .99) || !Range(source.CropBottomPct, 0, .99)
                || source.CropLeftPct + source.CropRightPct > .99000000001 || source.CropTopPct + source.CropBottomPct > .99000000001)
                throw new InvalidDataException("A source has invalid canvas dimensions, crop, rotation or opacity.");
            if (!Range(source.WebsiteZoom, .25, 4) || source.WebsiteWidth is < 320 or > 7680 || source.WebsiteHeight is < 240 or > 4320
                || source.WebsiteRefreshInterval < 0 || source.WebsiteUserAgent == null || source.ProcessId is <= 0 || source.WindowHandle is <= 0)
                throw new InvalidDataException("A source has invalid capture settings.");
            if ((source.Type is SourceType.Monitor or SourceType.WallpaperEngine) && string.IsNullOrWhiteSpace(source.MonitorDeviceId)
                || source.Type == SourceType.Webcam && string.IsNullOrWhiteSpace(source.WebcamDeviceId)
                || source.Type == SourceType.Process && source.ProcessId == null && string.IsNullOrWhiteSpace(source.ProcessPath)
                || source.Type == SourceType.Region && source.Region == null)
                throw new InvalidDataException("A source is missing its capture device or region.");
            if (source.Region is { } region && (region.Width is < 1 or > 32768 || region.Height is < 1 or > 32768
                || region.X is < -100000 or > 100000 || region.Y is < -100000 or > 100000))
                throw new InvalidDataException("A source contains an invalid screen region.");
            if (source.Type == SourceType.Website && !CanvasSourceValidation.TryGetWebsiteUri(source.WebsiteUrl, out _))
                throw new InvalidDataException("Website sources must use an absolute HTTP, HTTPS or file address.");
        }
        var validated = scene with { Name = name, Sources = Array.AsReadOnly(sources) };
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new SceneExportDocument { Scene = validated }, JsonOptions)) > MaxFileBytes)
            throw new InvalidDataException("This scene exceeds the 2 MB limit. Reduce the number of sources.");
        return validated;
    }

    private static bool Range(double value, double minimum, double maximum) => double.IsFinite(value) && value >= minimum && value <= maximum;
    private static bool TooLong(string? value, int maximum) => value?.Length > maximum;
}
