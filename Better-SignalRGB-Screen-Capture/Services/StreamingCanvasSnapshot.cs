using Better_SignalRGB_Screen_Capture.ViewModels;
namespace Better_SignalRGB_Screen_Capture.Services;
internal static class StreamingCanvasSnapshot
{
    public const int Width = 320;
    public const int Height = 200;

    public static Task<StreamingSourceSnapshot[]> CaptureAsync(CancellationToken cancellationToken)
    {
        var dispatcher = App.MainWindow.DispatcherQueue;
        StreamingSourceSnapshot[] Capture()
        {
            var sources = App.GetService<MainViewModel>().Sources;
            return sources.Select((source, index) => StreamingSourceSnapshot.FromSource(source, sources.Count - index - 1))
                .Reverse().ToArray();
        }
        if (dispatcher.HasThreadAccess)
            return Task.FromResult(Capture());

        var completion = new TaskCompletionSource<StreamingSourceSnapshot[]>(TaskCreationOptions.RunContinuationsAsynchronously);
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
