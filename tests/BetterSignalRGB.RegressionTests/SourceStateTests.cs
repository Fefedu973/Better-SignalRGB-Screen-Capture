using System.Collections.ObjectModel;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.Core.Services;
using Windows.Graphics;
using Better_SignalRGB_Screen_Capture.Helpers;

namespace BetterSignalRGB.RegressionTests;

internal static class SourceStateTests
{
    public static void Run()
    {
        var source = new SourceItem
        {
            Type = SourceType.Region,
            RegionBounds = new RectInt32(-1920, 120, 1600, 900),
            CanvasX = 20, CanvasY = 30, CanvasWidth = 100, CanvasHeight = 60,
            IsLivePreviewEnabled = false,
            WebsiteZoom = 2, WebsiteRefreshInterval = 120,
            IsSelected = true
        };
        var clone = source.Clone();
        Assert.True(clone.Id != source.Id, "A pasted source needs a fresh identity");
        Assert.True(!clone.IsSelected && !clone.IsLivePreviewEnabled, "Clone preserves preview but clears selection");
        Assert.Equal(source.RegionBounds, clone.RegionBounds, "Negative monitor coordinates survive clone");
        var bounds = SourceGeometry.GetVisibleAreaAabb(source);
        Assert.Near(20, bounds.X, 1e-9, "Source bounds preserve logical position");
        source.SetCrop(.2, 0, 0, 0, 0);
        source.IsMirroredHorizontally = true;
        bounds = SourceGeometry.GetVisibleAreaAabb(source);
        Assert.Near(40, bounds.X, 1e-9, "Mirroring content does not move the crop mask");
        Assert.Near(80, bounds.Width, 1e-9, "Crop removes only its own edge");
        var rotated = SourceGeometry.GetVisibleAreaAabb(source, 20, 30, 100, 60, 90);
        Assert.Near(60, rotated.Width, 1e-6, "Rotated source swaps cropped bounding dimensions");
        Assert.Near(80, rotated.Height, 1e-6, "Rotated source preserves crop height");
        source.SetCrop(0, 0, 0, 0, 0);
        var sources = new ObservableCollection<SourceItem> { source };
        var history = new UndoRedoManager();
        history.SaveState(sources);
        source.CanvasX = 90;
        source.RegionBounds = new RectInt32(0, 0, 200, 100);
        var previous = history.Undo(sources)!;
        Assert.Equal(20, previous[0].CanvasX, "Undo restores the pre-edit position");
        Assert.Equal(-1920, previous[0].RegionBounds!.Value.X, "Undo preserves RectInt32 fields");
        Assert.Equal(source.Id, previous[0].Id, "Undo preserves capture identity");
        Assert.Equal(120, previous[0].WebsiteRefreshInterval, "Undo includes source configuration");
        var next = history.Redo(new ObservableCollection<SourceItem>(previous))!;
        Assert.Equal(90, next[0].CanvasX, "Redo restores edited state");
        history.SaveState(sources);
        Assert.True(!history.CanRedo, "A new edit invalidates redo");
        history.Clear();
        for (var index = 0; index < 100; index++) { source.CanvasX = index; history.SaveState(sources); }
        var undos = 0;
        while (history.Undo(sources) != null) undos++;
        Assert.Equal(50, undos, "History is bounded to 50 states");

        source.CanvasWidth = -10; source.CanvasHeight = 0;
        Assert.Equal(1, source.CanvasWidth, "Invalid source widths cannot reach capture");
        Assert.Equal(1, source.CanvasHeight, "Invalid source heights cannot reach capture");
        source.Opacity = double.NaN;
        source.WebsiteZoom = double.PositiveInfinity;
        source.CropLeftPct = .9; source.CropRightPct = .9;
        source.CropTopPct = double.NaN;
        Assert.Near(1, source.Opacity, 0, "NaN opacity is sanitized");
        Assert.Near(1, source.WebsiteZoom, 0, "Infinite zoom is sanitized");
        Assert.True(source.CropLeftPct + source.CropRightPct <= .99, "Crop leaves a nonempty area");
        Assert.Near(0, source.CropTopPct, 0, "NaN crop is sanitized");
        source.SetCrop(.1, .1, .8, .1, 20);
        source.SetCrop(.8, .1, .1, .1, 20);
        Assert.Near(.8, source.CropLeftPct, 1e-9, "Atomic crop movement ignores the previous opposite edge");
        Assert.Near(.1, source.CropRightPct, 1e-9, "Atomic crop retains the new opposite edge");
        source.CropRotation = 181;
        Assert.Equal(-179, source.CropRotation, "Crop rotation crosses 180 degrees without sticking");
        source.SetCrop(.1, .1, .1, .1, -181);
        Assert.Equal(179, source.CropRotation, "Atomic crop rotation wraps through minus 180 degrees");
        source.Rotation = int.MinValue;
        Assert.True(source.Rotation >= 0 && source.Rotation < 360, "Rotation normalization handles integer extremes");

        var directory = Path.Combine(Path.GetTempPath(), "BetterSignalRGB-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var files = new FileService();
            files.Save(directory, "settings.json", new Dictionary<string, int> { ["revision"] = 1 });
            files.Save(directory, "settings.json", new Dictionary<string, int> { ["revision"] = 2 });
            Assert.Equal(2, files.Read<Dictionary<string, int>>(directory, "settings.json")["revision"], "Atomic settings replacement is readable");
            Assert.Equal(1, Directory.GetFiles(directory).Length, "No temporary files remain after save");
            // Concurrent replacement must always leave one complete document.
            Parallel.For(0, 20, revision => files.Save(directory, "settings.json", new Dictionary<string, int> { ["revision"] = revision }));
            Assert.True(files.Read<Dictionary<string, int>>(directory, "settings.json").ContainsKey("revision"), "Concurrent writes leave valid JSON");
        }
        finally
        {
            // Only files made in this unique test directory are removed.
            foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

}
