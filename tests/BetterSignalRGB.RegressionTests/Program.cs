namespace BetterSignalRGB.RegressionTests;

internal static class Program
{
    private static async Task<int> Main()
    {
        try
        {
            XamlContractTests.Run();
            SourceStateTests.Run();
            LayerOrderingTests.Run();
            CanvasGeometryTests.Run();
            CropInteractionTests.Run();
            CanvasInteractionTests.Run();
            CanvasSnapTests.Run();
            WebsitePreviewTests.Run();
            PreviewDecodeTests.Run();
            CapturePipelineTests.Run();
            await StreamingPipelineTests.RunAsync();
            await EffectSettingsTests.RunAsync();
            await EffectSettingsQueueTests.RunAsync();
            await LocalSettingsTransactionTests.RunAsync();
            await StartupPreferencesTests.RunAsync();
            await SceneLibraryTests.RunAsync();
            await SignalRgbSetupTests.RunAsync();
            Console.WriteLine($"PASS: {Assert.Count} regression assertions.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}

internal static class Assert
{
    public static int Count { get; private set; }

    public static void True(bool condition, string message)
    {
        Count++;
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Equal<T>(T expected, T actual, string message) =>
        True(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}: expected {expected}, got {actual}");

    public static void Near(double expected, double actual, double tolerance, string message) =>
        True(double.IsFinite(actual) && Math.Abs(expected - actual) <= tolerance, $"{message}: expected {expected}, got {actual}");
}
