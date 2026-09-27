using Better_SignalRGB_Screen_Capture.Models;

namespace Better_SignalRGB_Screen_Capture.Contracts.Services;

public interface ISceneLibraryService
{
    IReadOnlyList<SceneProfile> Profiles { get; }
    Task InitializeAsync();
    Task FlushAsync();
    Task<SceneProfile> SaveNewAsync(string name, IEnumerable<SceneSourceSnapshot> sources);
    Task<SceneProfile> ReplaceAsync(Guid id, IEnumerable<SceneSourceSnapshot> sources);
    Task<SceneProfile> RenameAsync(Guid id, string name);
    Task DeleteAsync(Guid id);
    Task<SceneProfile> ImportAsync(string json);
    string Export(Guid id);
}

public interface ISceneLibraryStorage
{
    Task<SceneLibraryDocument?> ReadAsync();
    Task WriteAsync(SceneLibraryDocument document);
}

public interface ISceneFilePickerService
{
    Task<string?> ImportAsync();
    Task<bool> ExportAsync(string name, string json);
}
