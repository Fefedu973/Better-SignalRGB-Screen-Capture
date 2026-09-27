using System.Text.Json.Nodes;
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.Services;
using Newtonsoft.Json;

namespace BetterSignalRGB.RegressionTests;

internal static class SceneLibraryTests
{
    public static async Task RunAsync()
    {
        var storage = new Storage();
        var service = new SceneLibraryService(storage);
        await Task.WhenAll(service.InitializeAsync(), service.InitializeAsync());
        Assert.Equal(1, storage.Reads, "Scene library initializes once under concurrent requests");
        var website = new SceneSourceSnapshot
        {
            Id = Guid.NewGuid(), Name = "Browser / café", Type = SourceType.Website,
            WebsiteUrl = "https://example.com/path?q=one", WebsiteZoom = 1.75, WebsiteRefreshInterval = 31,
            WebsiteUserAgent = "Scene-Test-Agent", WebsiteWidth = 1280, WebsiteHeight = 720,
            WebsiteNavigationState = "{\"scrollY\":42}", CanvasX = -11, CanvasY = 23,
            CanvasWidth = 146, CanvasHeight = 91, Rotation = 27, CropRotation = -34,
            CropLeftPct = .1, CropRightPct = .2, CropTopPct = .05, CropBottomPct = .25,
            Opacity = .63, IsMirroredHorizontally = true, IsMirroredVertically = true, IsLocked = true
        };
        var region = website with { Id = Guid.NewGuid(), Name = "Region", Type = SourceType.Region, Region = new(-1920, -24, 1080, 720) };
        var monitor = website with { Id = Guid.NewGuid(), Type = SourceType.Monitor, MonitorDeviceId = "display-id-1" };
        var window = website with { Id = Guid.NewGuid(), Type = SourceType.Process, ProcessId = 1234, ProcessPath = "C:\\Apps\\Example.exe", WindowHandle = 123456, WindowTitle = "Second document — Example" };
        var webcam = website with { Id = Guid.NewGuid(), Type = SourceType.Webcam, WebcamDeviceId = "camera-device-id", WebcamFormatId = "00000000-0000-0000-0000-000000000001:1920x1080@30" };
        var wallpaper = website with { Id = Guid.NewGuid(), Name = "Wallpaper Engine", Type = SourceType.WallpaperEngine, MonitorDeviceId = "display-wallpaper-2" };
        SceneSourceSnapshot[] sources = [website, region, monitor, window, webcam, wallpaper];
        Assert.Equal(4, (int)SourceType.Website, "Existing saved source type values are unchanged");
        Assert.Equal(5, (int)SourceType.WallpaperEngine, "Wallpaper Engine appends a distinct persisted source type");
        var wallpaperSource = wallpaper.Restore();
        Assert.Equal("display-wallpaper-2", wallpaperSource.DeviceId, "Wallpaper capture identity is the selected monitor, not a renderer-specific window");
        Assert.True(wallpaperSource.DisplaySubtitle.Contains("display-wallpaper-2"), "Wallpaper source shows its chosen monitor");
        wallpaperSource.Name = string.Empty;
        Assert.Equal("Wallpaper Engine", wallpaperSource.DisplayName, "Unnamed wallpaper sources have an explicit fallback label");
        foreach (var source in sources)
        {
            var restored = source.Restore();
            Assert.Equal(source, SceneSourceSnapshot.Capture(restored), "Typed source snapshots preserve all source configuration fields");
            Assert.True(!restored.IsSelected, "Scene snapshots exclude runtime selection");
            Assert.Equal(source, SceneSourceSnapshot.Capture(restored.Clone(preserveId: true)), "History and copies retain exact window and camera identities");
        }
        var saved = await service.SaveNewAsync("  Desktop  ", sources);
        sources[0] = website with { CanvasX = 200 };
        Assert.Equal("Desktop", saved.Name, "Scene names are trimmed");
        Assert.Equal(-11, saved.Sources[0].CanvasX, "Saving detaches the caller's source list");
        var json = service.Export(saved.Id);
        var exported = JsonNode.Parse(json)!;
        Assert.Equal("BetterSignalRGB.Scene", exported["format"]!.GetValue<string>(), "Exports identify the scene format");
        Assert.Equal(1, exported["version"]!.GetValue<int>(), "Exports identify the schema version");
        Assert.True(!json.Contains("isSelected") && !json.Contains("isRecording"), "Exports contain no transient UI or recording state");
        var imported = await service.ImportAsync(json);
        Assert.True(imported.Id != saved.Id, "Import creates a distinct library identity");
        Assert.Equal("Desktop (2)", imported.Name, "Import avoids overwriting a same-named scene");
        Assert.True(saved.Sources.SequenceEqual(imported.Sources), "JSON roundtrip preserves source identities and layer order");
        Assert.Equal(SourceType.WallpaperEngine, imported.Sources[^1].Type, "Scene import recognizes the generic Wallpaper Engine source");
        Assert.Equal("display-wallpaper-2", imported.Sources[^1].MonitorDeviceId, "Imported Wallpaper Engine sources retain monitor selection");

        var renamed = await service.RenameAsync(imported.Id, "Gaming");
        Assert.Equal(imported.Id, renamed.Id, "Renaming preserves scene identity");
        var replacement = await service.ReplaceAsync(renamed.Id, [region]);
        Assert.True(replacement.Sources.Count == 1 && replacement.Sources[0] == region, "Replacing updates the complete saved composition");
        var reopened = new SceneLibraryService(storage);
        await reopened.InitializeAsync();
        Assert.Equal(2, reopened.Profiles.Count, "Real JSON-backed storage roundtrips the library");
        Assert.Equal(region, reopened.Profiles[1].Sources[0], "Stored WinRT region coordinates survive as explicit JSON numbers");

        await RejectAsync(() => service.SaveNewAsync("desktop", [website]));
        await RejectAsync(() => service.SaveNewAsync("\n", [website]));
        await RejectAsync(() => service.SaveNewAsync("Duplicate IDs", [website, website]));
        await RejectAsync(() => service.SaveNewAsync("Invalid numeric", [website with { Opacity = double.NaN }]));
        await RejectAsync(() => service.SaveNewAsync("Missing device", [monitor with { MonitorDeviceId = null }]));
        await RejectAsync(() => service.SaveNewAsync("Missing wallpaper monitor", [wallpaper with { MonitorDeviceId = null }]));
        await RejectAsync(() => service.SaveNewAsync("Invalid handle", [window with { WindowHandle = -1 }]));
        await RejectAsync(() => service.SaveNewAsync("Invalid URL", [website with { WebsiteUrl = "javascript:alert(1)" }]));
        await RejectAsync(() => service.SaveNewAsync("Invalid crop", [website with { CropLeftPct = .9, CropRightPct = .8 }]));
        await RejectAsync(() => service.SaveNewAsync("Too many", Enumerable.Range(0, 129).Select(_ => website with { Id = Guid.NewGuid() })));
        await RejectAsync(() => service.ImportAsync(new string(' ', SceneLibraryService.MaxFileBytes + 1)));
        await RejectAsync(() => service.ImportAsync("{broken"));
        foreach (var mutation in new Action<JsonNode>[]
        {
            node => node["version"] = 2,
            node => node["canvasWidth"] = 800,
            node => node["scene"]!["sources"]![0]!["canvasWidth"] = 0,
            node => node["scene"]!["sources"]![0]!["type"] = "Unknown",
            node => node["scene"]!["sources"]![0]!["unexpected"] = true,
            node => node.AsObject().Remove("version")
        })
        {
            var invalid = JsonNode.Parse(json)!;
            mutation(invalid);
            await RejectAsync(() => service.ImportAsync(invalid.ToJsonString()));
        }
        Assert.Equal(2, service.Profiles.Count, "Rejected imports and edits never mutate the library");

        var priorJson = storage.Json;
        storage.FailWrite = true;
        await RejectAsync(() => service.RenameAsync(saved.Id, "Rejected rename"));
        await RejectAsync(() => service.DeleteAsync(saved.Id));
        await RejectAsync(() => service.ReplaceAsync(saved.Id, []));
        Assert.Equal(priorJson, storage.Json, "Failed library saves preserve durable contents");
        Assert.Equal("Desktop", service.Profiles[0].Name, "Failed saves preserve the in-memory library");
        storage.FailWrite = false;
        await service.DeleteAsync(saved.Id);
        Assert.Equal(1, service.Profiles.Count, "A confirmed deletion removes only its selected scene");
        Assert.Equal(replacement.Id, service.Profiles[0].Id, "Deletion preserves other scenes");

        var corrupt = new Storage { Json = "{\"Version\":99,\"Scenes\":[]}" };
        var unreadable = new SceneLibraryService(corrupt);
        await RejectAsync(unreadable.InitializeAsync);
        await RejectAsync(() => unreadable.SaveNewAsync("Do not overwrite", [website]));
        Assert.Equal(0, corrupt.Writes, "An unreadable existing library is never silently overwritten");

        var localLibrary = new SceneLibraryService(new Storage());
        const string fileAddress = "file:///C:/My%20media/%C3%A9cran%20%231.webm";
        var localScene = await localLibrary.SaveNewAsync("Local media", [website with { WebsiteUrl = fileAddress, WebsiteNavigationState = fileAddress }]);
        var importedLocal = await localLibrary.ImportAsync(localLibrary.Export(localScene.Id));
        Assert.Equal(fileAddress, importedLocal.Sources[0].WebsiteUrl, "Scenes preserve escaped local media addresses through export/import");
        Assert.Equal(fileAddress, importedLocal.Sources[0].Restore().WebsiteUrl, "Restoring local media uses the original file address");
    }

    private static async Task RejectAsync(Func<Task> action)
    {
        try { await action(); Assert.True(false, "Invalid or failed scene operation must be rejected"); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException && ex.Message != "Invalid or failed scene operation must be rejected")
        { Assert.True(true, "Scene operation reports its validation or storage failure"); }
    }

    private sealed class Storage : ISceneLibraryStorage
    {
        public string? Json;
        public bool FailWrite;
        public int Reads, Writes;
        public async Task<SceneLibraryDocument?> ReadAsync()
        {
            Reads++;
            await Task.Yield();
            return Json == null ? null : JsonConvert.DeserializeObject<SceneLibraryDocument>(Json);
        }
        public Task WriteAsync(SceneLibraryDocument document)
        {
            if (FailWrite) throw new IOException("Simulated scene save failure");
            Json = JsonConvert.SerializeObject(document);
            Writes++;
            return Task.CompletedTask;
        }
    }
}
