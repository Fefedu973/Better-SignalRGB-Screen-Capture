using System.Drawing;
using ScreenRecorderLib;

namespace Better_SignalRGB_Screen_Capture.Helpers;

/// <summary>One bounded screenshot using the same desktop-to-region mapping as live capture.</summary>
public static class RegionPreviewCapture
{
    public static Task<byte[]> CaptureAsync(Rectangle region, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sources = CaptureRecorderOptions.CreateRegionSources(region);
        foreach (var source in sources.OfType<DisplayRecordingSource>()) source.IsCursorCaptureEnabled = false;
        var scale = Math.Min(1d, Math.Min(400d / region.Width, 200d / region.Height));
        var options = new RecorderOptions
        {
            SourceOptions = new SourceOptions { RecordingSources = sources },
            OutputOptions = new OutputOptions
            {
                RecorderMode = RecorderMode.Screenshot,
                SourceRect = new ScreenRect(0, 0, region.Width, region.Height),
                OutputFrameSize = new ScreenSize(Math.Max(1, (int)Math.Round(region.Width * scale)), Math.Max(1, (int)Math.Round(region.Height * scale))),
                Stretch = StretchMode.Fill
            },
            SnapshotOptions = new SnapshotOptions { SnapshotFormat = ImageFormat.PNG },
            AudioOptions = new AudioOptions { IsAudioEnabled = false },
            MouseOptions = new MouseOptions { IsMousePointerEnabled = false },
            LogOptions = new LogOptions { IsLogEnabled = false }
        };
        using var output = new MemoryStream();
        using var recorder = Recorder.CreateRecorder(options);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        recorder.OnRecordingComplete += (_, _) => completion.TrySetResult();
        recorder.OnRecordingFailed += (_, error) => completion.TrySetException(new IOException(error.Error));
        recorder.Record(output);
        try
        {
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            return output.ToArray();
        }
        finally { recorder.Stop(); }
    }, cancellationToken);
}
