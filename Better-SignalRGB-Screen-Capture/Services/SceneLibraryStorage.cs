using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Core.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Helpers;
using Better_SignalRGB_Screen_Capture.Models;
using Microsoft.Extensions.Options;
using Windows.Storage;

namespace Better_SignalRGB_Screen_Capture.Services;

/// <summary>App-owned library file; user-selected import/export files use the picker service.</summary>
public sealed class SceneLibraryStorage : ISceneLibraryStorage
{
    private readonly IFileService _files;
    private readonly string _folder;
    private const string FileName = "SceneLibrary.json";

    public SceneLibraryStorage(IFileService files, IOptions<LocalSettingsOptions> options)
    {
        _files = files;
        var root = RuntimeHelper.IsMSIX ? ApplicationData.Current.LocalFolder.Path
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                options.Value.ApplicationDataFolder ?? "Better-SignalRGB-Screen-Capture/ApplicationData");
        _folder = Path.Combine(root, "Scenes");
    }

    public Task<SceneLibraryDocument?> ReadAsync() => Task.Run(() =>
        _files.Read<SceneLibraryDocument>(_folder, FileName))!;

    public Task WriteAsync(SceneLibraryDocument document) => Task.Run(() => _files.Save(_folder, FileName, document));
}
