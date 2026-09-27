using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Better_SignalRGB_Screen_Capture.Views;

public sealed partial class DiagnosticsPanel : UserControl
{
    private readonly IPipelineDiagnosticsService _diagnostics;
    private readonly ISignalRgbConnectionService _connection;
    private readonly Dictionary<Guid, SourceRow> _rows = new();
    private DispatcherQueueTimer? _timer;

    public DiagnosticsPanel()
    {
        InitializeComponent();
        _diagnostics = App.GetService<IPipelineDiagnosticsService>();
        _connection = App.GetService<ISignalRgbConnectionService>();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        Refresh();
        if (_timer is not null) return;
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (_timer is null) return;
        _timer.Stop(); _timer.Tick -= OnTick; _timer = null;
    }

    private void OnTick(DispatcherQueueTimer sender, object args) => Refresh();

    private void Refresh()
    {
        var snapshot = _diagnostics.GetSnapshot();
        var transport = snapshot.SignalRgb;
        var connection = _connection.Snapshot;
        Set(ConnectionText, $"{connection.Message}\nEffect rendering: {connection.RenderFramesPerSecond:F1} frames/s");
        Set(TransportText, $"SignalRGB sender: {(transport.Running ? "running" : "stopped")} · {transport.FramesPerSecond:F1} source updates/s\n" +
            $"Sent {transport.SentFrames:N0} JPEGs · {transport.SentJpegBytes / 1048576d:F1} MiB · mean send {transport.MeanSendMilliseconds:F1} ms · {transport.Errors:N0} errors");
        Set(TransportErrorText, transport.LastError is null ? string.Empty : $"Last send error: {transport.LastError}");
        TransportErrorText.Visibility = transport.LastError is null ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Visibility = snapshot.Sources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var current = snapshot.Sources.Select(source => source.SourceId).ToHashSet();
        foreach (var id in _rows.Keys.Where(id => !current.Contains(id)).ToArray())
        { SourcesPanel.Children.Remove(_rows[id].Panel); _rows.Remove(id); }
        foreach (var source in snapshot.Sources)
        {
            if (!_rows.TryGetValue(source.SourceId, out var row))
            { row = new SourceRow(); _rows.Add(source.SourceId, row); SourcesPanel.Children.Add(row.Panel); }
            row.Update(source);
        }
    }

    private static void Set(TextBlock target, string value) { if (target.Text != value) target.Text = value; }

    private sealed class SourceRow
    {
        public StackPanel Panel { get; } = new() { Spacing = 3 };
        private readonly TextBlock _name = new() { TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _mode = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _rates = new() { TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _counters = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _error = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        public SourceRow()
        {
            foreach (var text in new[] { _name, _mode, _rates, _counters, _error }) Panel.Children.Add(text);
        }
        public void Update(SourceDiagnosticsSnapshot source)
        {
            var mode = source.Encoder switch
            {
                CaptureEncoderKind.Website => "WebView JPEG",
                CaptureEncoderKind.Wallpaper => "Wallpaper Engine desktop surface",
                CaptureEncoderKind.Software => "Software H.264 carrier",
                _ => "Hardware H.264 requested"
            };
            Set(_name, source.Name);
            Set(_mode, $"{source.State} · {mode} · JPEG {source.Width} × {source.Height}");
            Set(_rates, $"Capture {source.CaptureFramesPerSecond:F1}/{source.RequestedFrameRate} fps · sent {source.SentFramesPerSecond:F1}/s · mean processing {source.MeanProcessingMilliseconds:F1} ms");
            Set(_counters, $"Received {source.ReceivedFrames:N0} · produced {source.ProducedFrames:N0} · dropped {source.DroppedFrames:N0} · skipped {source.SkippedCaptures:N0} · errors {source.Errors:N0}");
            Set(_error, source.LastError is null ? string.Empty : $"Last capture error: {source.LastError}");
            _error.Visibility = source.LastError is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
