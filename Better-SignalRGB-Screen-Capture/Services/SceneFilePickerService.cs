using System.Text;
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Better_SignalRGB_Screen_Capture.Services;

public sealed class SceneFilePickerService : ISceneFilePickerService
{
    public async Task<string?> ImportAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".json");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSingleFileAsync();
        if (file == null) return null;
        using var stream = await file.OpenStreamForReadAsync();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int count;
        while ((count = await stream.ReadAsync(chunk)) != 0)
        {
            if (buffer.Length + count > SceneLibraryService.MaxFileBytes)
                throw new InvalidDataException("Choose a scene JSON file smaller than 2 MB.");
            await buffer.WriteAsync(chunk.AsMemory(0, count));
        }
        try { return new UTF8Encoding(false, true).GetString(buffer.ToArray()).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("Scene files must use UTF-8 text.", ex); }
    }

    public async Task<bool> ExportAsync(string name, string json)
    {
        var safeName = new string(name.Where(character => !Path.GetInvalidFileNameChars().Contains(character)).ToArray());
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = "Scene - " + safeName.Trim().TrimEnd('.')
        };
        picker.FileTypeChoices.Add("Scene JSON", new List<string> { ".json" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSaveFileAsync();
        if (file == null) return false;
        await FileIO.WriteTextAsync(file, json);
        return true;
    }
}
