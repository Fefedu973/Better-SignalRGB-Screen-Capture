using Better_SignalRGB_Screen_Capture.ViewModels;
namespace Better_SignalRGB_Screen_Capture.Services;
internal static class StreamingCanvasSnapshot
{
    public const int Width = 320;
    public const int Height = 200;

    public static Task<StreamingSourceSnapshot[]> CaptureAsync(CancellationToken cancellationToken) =>
        CaptureOnUiAsync(CaptureSources, cancellationToken);

    public static Task<StreamingWebCanvasSnapshot> CaptureWebAsync(CancellationToken cancellationToken) =>
        CaptureOnUiAsync(viewModel => new StreamingWebCanvasSnapshot(CaptureSources(viewModel),
            viewModel.IsHighQuality ? 800 : Width, viewModel.IsHighQuality ? 600 : Height), cancellationToken);

    private static StreamingSourceSnapshot[] CaptureSources(MainViewModel viewModel)
    {
        var sources = viewModel.Sources;
        return sources.Select((source, index) => StreamingSourceSnapshot.FromSource(source, sources.Count - index - 1))
            .Reverse().ToArray();
    }

    private static Task<T> CaptureOnUiAsync<T>(Func<MainViewModel, T> capture, CancellationToken cancellationToken)
    {
        var dispatcher = App.MainWindow.DispatcherQueue;
        T Capture() => capture(App.GetService<MainViewModel>());
        if (dispatcher.HasThreadAccess)
            return Task.FromResult(Capture());

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(() =>
        {
            if (cancellationToken.IsCancellationRequested) { completion.TrySetCanceled(cancellationToken); return; }
            try { completion.TrySetResult(Capture()); }
            catch (Exception exception) { completion.TrySetException(exception); }
        }))
            completion.TrySetException(new InvalidOperationException("The canvas UI is no longer available."));
        return completion.Task.WaitAsync(cancellationToken);
    }
}
