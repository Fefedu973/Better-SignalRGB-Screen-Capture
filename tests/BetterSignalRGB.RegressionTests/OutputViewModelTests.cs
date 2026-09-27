using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.Services;
using Better_SignalRGB_Screen_Capture.ViewModels;

namespace BetterSignalRGB.RegressionTests;

internal static class OutputViewModelTests
{
    public static async Task RunAsync()
    {
        var storage = new Storage();
        var service = new SignalRgbEffectSettingsService(storage);
        var vm = new OutputViewModel(service);
        Assert.True(!vm.IsEffectSettingsReady && !vm.CanEditEffectSettings, "Output editing waits for stored settings");
        storage.ReadReady.SetResult();
        await vm.ActivateEffectSettingsAsync();
        Assert.True(vm.CanEditEffectSettings, "The preview can be edited before either real output is enabled");
        Assert.True(!vm.EffectEnabled && !vm.EffectWebEnabled, "Opening the editor enables no real output");
        Assert.True(vm.EffectAmbilightStyles.SequenceEqual(new[] { "Classic", "Soft", "Contours" }),
            "The halo selector keeps Classic and Soft alongside Contours");
        Assert.True(vm.IsLegacyGlow && !vm.IsContourGlow, "The default style shows legacy halo controls only");

        SignalRgbEffectSettings? preview = null;
        vm.PreviewSettingsChanged += (_, settings) => preview = settings;
        vm.EffectBrightness = 24;
        Assert.Equal(24, preview!.Brightness, "The live preview receives an edit before the persistence debounce");
        Assert.Equal(0, storage.Writes, "Preview updates do not wait for a disk write");
        await FlushAsync(service, vm);
        Assert.Equal(24, service.Current.Brightness, "Preview edits are persisted for later output activation");
        Assert.True(!service.Current.Enabled && !service.Current.WebEnabled, "Persisting preview edits preserves disabled outputs");

        vm.InsetPlacementCommand.Execute(null);
        Assert.Equal(32d, vm.ScreenX, "Inset gives the picture horizontal glow space");
        Assert.Equal(20d, vm.ScreenY, "Inset gives the picture vertical glow space");
        Assert.Equal(256d, vm.ScreenWidth, "Inset keeps 80% of the logical canvas width");
        Assert.Equal(160d, vm.ScreenHeight, "Inset keeps 80% of the logical canvas height");
        vm.ApplyPlacement(14.125, 18.75, 210.5, 110.25);
        Assert.Equal(14.125, preview!.ScreenX, "Drag messages preserve subpixel positions");
        Assert.Equal(210.5, preview.ScreenWidth, "Resize messages preserve subpixel dimensions atomically");
        vm.CenterPlacementCommand.Execute(null);
        Assert.Equal(54.75, vm.ScreenX, "Center uses the current width without rounding");
        Assert.Equal(44.875, vm.ScreenY, "Center uses the current height without rounding");
        vm.ApplyPlacement(500, -8, 256.5, 160.25);
        Assert.Equal(63.5, vm.ScreenX, "Invalid drag coordinates are clamped against the requested size");
        Assert.Equal(0d, vm.ScreenY, "Invalid drag coordinates cannot leave the output");
        var beforeInvalid = vm.PreviewSettings;
        vm.ApplyPlacement(double.NaN, 0, 300, 200);
        vm.ScreenWidth = double.PositiveInfinity;
        vm.EffectHue = double.NaN;
        Assert.Equal(beforeInvalid, vm.PreviewSettings, "Empty or nonfinite UI input cannot mutate the preview or saved state");
        vm.EffectAmbilightStyle = "Soft";
        vm.EffectAmbilightCutoff = 20;
        Assert.True(vm.IsLegacyGlow && !vm.IsContourGlow, "Soft shows the legacy blur and spread controls");
        vm.EffectAmbilightBlur = 17;
        vm.EffectAmbilightSpread = 42;
        vm.EffectAmbilightStyle = "Contours";
        Assert.True(vm.IsContourGlow && !vm.IsLegacyGlow, "Contours replaces legacy controls with its dedicated controls");
        vm.EffectAmbilightEdgeDepth = -1;
        vm.EffectAmbilightEdgeMix = -1;
        vm.EffectAmbilightEdgeReach = -1;
        vm.EffectAmbilightEdgeFade = -1;
        Assert.Equal(1d, vm.EffectAmbilightEdgeDepth, "Contour UI bounds sample depth below");
        Assert.Equal(0d, vm.EffectAmbilightEdgeMix, "Contour UI accepts no smoothing");
        Assert.Equal(1d, vm.EffectAmbilightEdgeReach, "Contour UI bounds reach below");
        Assert.Equal(0d, vm.EffectAmbilightEdgeFade, "Contour UI accepts uniform brightness");
        vm.EffectAmbilightEdgeDepth = 500;
        vm.EffectAmbilightEdgeMix = 500;
        vm.EffectAmbilightEdgeReach = 500;
        vm.EffectAmbilightEdgeFade = 500;
        Assert.Equal(20d, vm.EffectAmbilightEdgeDepth, "Contour UI bounds sample depth above");
        Assert.Equal(30d, vm.EffectAmbilightEdgeMix, "Contour UI bounds smoothing above");
        Assert.Equal(200d, vm.EffectAmbilightEdgeReach, "Contour UI bounds reach above");
        Assert.Equal(100d, vm.EffectAmbilightEdgeFade, "Contour UI bounds fade above");
        var beforeInvalidContour = vm.PreviewSettings;
        vm.EffectAmbilightEdgeDepth = double.NaN;
        vm.EffectAmbilightEdgeMix = double.PositiveInfinity;
        vm.EffectAmbilightEdgeReach = double.NegativeInfinity;
        vm.EffectAmbilightEdgeFade = double.NaN;
        Assert.Equal(beforeInvalidContour, vm.PreviewSettings, "Empty and nonfinite contour controls do not mutate the preview");
        vm.EffectAmbilightEdgeDepth = 7.4;
        vm.EffectAmbilightEdgeMix = 12.6;
        vm.EffectAmbilightEdgeReach = 85.2;
        vm.EffectAmbilightEdgeFade = 66.8;
        Assert.Equal(7, preview!.AmbilightEdgeDepth, "Contour preview uses integer sample depths");
        Assert.Equal(13, preview.AmbilightEdgeMix, "Contour preview uses integer lateral smoothing");
        Assert.Equal(85, preview.AmbilightEdgeReach, "Contour preview uses integer reach");
        Assert.Equal(67, preview.AmbilightEdgeFade, "Contour preview uses integer fade");
        vm.EffectAmbilightStyle = "Soft";
        Assert.Equal(17d, vm.EffectAmbilightBlur, "Switching back to Soft restores its original blur");
        Assert.Equal(42d, vm.EffectAmbilightSpread, "Switching back to Soft restores its original spread");
        vm.EffectAmbilightStyle = "Contours";
        Assert.Equal(7d, vm.EffectAmbilightEdgeDepth, "Switching back to Contours preserves its sample depth");
        Assert.Equal(13d, vm.EffectAmbilightEdgeMix, "Switching back to Contours preserves its smoothing");
        Assert.Equal(85d, vm.EffectAmbilightEdgeReach, "Switching back to Contours preserves its reach");
        Assert.Equal(67d, vm.EffectAmbilightEdgeFade, "Switching back to Contours preserves its fade");
        await FlushAsync(service, vm);
        Assert.Equal(vm.PreviewSettings, storage.Value, "Contour style and controls are persisted as a complete record");
        vm.EffectEnabled = true;
        Assert.True(!vm.EffectWebEnabled, "Activating SignalRGB does not activate the webpage");
        vm.EffectWebEnabled = true;
        vm.EffectEnabled = false;
        Assert.True(vm.EffectWebEnabled && !vm.EffectEnabled, "Web appearance is independently enabled");
        vm.ResetEffectSettingsCommand.Execute(null);
        Assert.Equal(new SignalRgbEffectSettings { WebEnabled = true }, vm.PreviewSettings,
            "Reset clears placement, Contours settings and colors while retaining both output switches");
        Assert.True(vm.IsLegacyGlow && !vm.IsContourGlow, "Reset restores the compatible Classic controls");
        await FlushAsync(service, vm);

        vm.EffectAmbilight = false;
        Assert.True(!vm.CanEditEffectGlow && !vm.CanHideEffectPicture, "Halo-dependent controls track the preview state");
        vm.EffectAmbilight = true;
        vm.EffectAmbilightFullscreen = true;
        Assert.True(vm.CanHideEffectPicture, "Full-area halo makes the hide-picture control available");
        await FlushAsync(service, vm);

        storage.FailSave = true;
        var confirmed = service.Current;
        vm.ScreenWidth = 220;
        try { await service.FlushAsync(); }
        catch (IOException) { }
        await UntilAsync(() => vm.EffectSettingsStatus.Contains("Simulated", StringComparison.Ordinal) && vm.PreviewSettings == confirmed);
        Assert.Equal(confirmed, vm.PreviewSettings, "A failed save rolls the preview back to confirmed settings");
        Assert.Equal(confirmed, service.Current, "Failed edits never change the effective output settings");
        storage.FailSave = false;
        vm.ApplyPlacement(25, 15, 200, 140);
        vm.DeactivateEffectSettings();
        var reopened = new OutputViewModel(service);
        await reopened.ActivateEffectSettingsAsync();
        Assert.Equal(25d, reopened.ScreenX, "Navigation drains the previous editor's pending placement before loading");
        Assert.Equal(200d, reopened.ScreenWidth, "Closing the editor cannot abandon a debounced resize");
        reopened.DeactivateEffectSettings();
        await service.UpdateAsync(service.Current with { Hue = 73 });
        Assert.Equal(0d, reopened.EffectHue, "An inactive output editor releases its service subscription");
        await reopened.ActivateEffectSettingsAsync();
        Assert.Equal(73d, reopened.EffectHue, "Reopening reconciles changes made while the editor was inactive");
        reopened.DeactivateEffectSettings();
    }

    private static async Task FlushAsync(SignalRgbEffectSettingsService service, OutputViewModel vm)
    {
        await service.FlushAsync();
        await UntilAsync(() => vm.PreviewSettings == service.Current);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++) await Task.Delay(10);
        Assert.True(condition(), "The output settings operation completes within its test deadline");
    }

    private sealed class Storage : ILocalSettingsService
    {
        public TaskCompletionSource ReadReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SignalRgbEffectSettings Value = new();
        public int Writes;
        public bool FailSave;
        public async Task<T?> ReadSettingAsync<T>(string key)
        {
            await ReadReady.Task;
            return Value is T value ? value : default;
        }
        public Task SaveSettingAsync<T>(string key, T value)
        {
            if (FailSave) throw new IOException("Simulated output save failure");
            Writes++;
            Value = (SignalRgbEffectSettings)(object)value!;
            return Task.CompletedTask;
        }
    }
}
