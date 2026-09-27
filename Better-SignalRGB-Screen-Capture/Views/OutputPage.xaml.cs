using System.ComponentModel;
using System.Text.Json;
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Helpers;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.Services;
using Better_SignalRGB_Screen_Capture.ViewModels;
using Microsoft.Extensions.Options;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Globalization.NumberFormatting;
using Windows.Storage;

namespace Better_SignalRGB_Screen_Capture.Views;

/// <summary>The output editor hosts the same renderer as the public webpage, with a private editing bridge.</summary>
public sealed partial class OutputPage : Page
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly MainViewModel _capture;
    private readonly App _application;
    private readonly IncrementNumberRounder? _placementRounder;
    private WebView2? _browser;
    private CancellationTokenSource? _lifetime;
    private SignalRgbEffectSettings? _pendingSettings;
    private bool _ready;
    private bool _pageActive;
    private bool _inlineNavigationPending;
    private string? _trustedDocumentSource;
    private Action? _detachBrowserEvents;
    private int _generation;
    private int _settingsWriterGeneration = -1;

    public OutputViewModel ViewModel { get; }

    public OutputPage()
    {
        _application = (App)Application.Current;
        ViewModel = App.GetService<OutputViewModel>();
        _capture = App.GetService<MainViewModel>();
        InitializeComponent();
        _placementRounder = new IncrementNumberRounder { Increment = .01, RoundingAlgorithm = RoundingAlgorithm.RoundHalfUp };
        var placementFormatter = new DecimalFormatter
        {
            IntegerDigits = 1,
            FractionDigits = 2,
            NumberRounder = _placementRounder
        };
        // Format only the editors' text. The bound values and persisted placement
        // keep the exact subpixel coordinates supplied by a preview gesture.
        PlacementXBox.NumberFormatter = PlacementYBox.NumberFormatter =
            PlacementWidthBox.NumberFormatter = PlacementHeightBox.NumberFormatter = placementFormatter;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void Placement_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!ViewModel.IsEffectSettingsReady || _placementRounder == null || !double.IsFinite(args.NewValue)) return;
        double CurrentValue() => sender.Name switch
        {
            nameof(PlacementXBox) => ViewModel.ScreenX,
            nameof(PlacementYBox) => ViewModel.ScreenY,
            nameof(PlacementWidthBox) => ViewModel.ScreenWidth,
            _ => ViewModel.ScreenHeight
        };
        var current = CurrentValue();
        if (args.NewValue == current) return;
        // NumberBox reparses formatted text on blur/Enter and before a spin step.
        // Restoring this display-only round-trip keeps a drag's precise coordinates;
        // the OneWay binding prevents the control from first overwriting the model.
        // ParseDouble("48.37") and IncrementNumberRounder can differ by one ULP.
        // This tolerance is far below any displayed increment in the 0..320 range.
        if (Math.Abs(args.NewValue - _placementRounder.RoundDouble(current)) > 1e-9)
        {
            switch (sender.Name)
            {
                case nameof(PlacementXBox): ViewModel.ScreenX = args.NewValue; break;
                case nameof(PlacementYBox): ViewModel.ScreenY = args.NewValue; break;
                case nameof(PlacementWidthBox): ViewModel.ScreenWidth = args.NewValue; break;
                case nameof(PlacementHeightBox): ViewModel.ScreenHeight = args.NewValue; break;
            }
        }
        sender.Value = CurrentValue();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_pageActive || _application.IsShuttingDown) return;
        _pageActive = true;
        try
        {
            ViewModel.PreviewSettingsChanged += OnPreviewSettingsChanged;
            _application.ShuttingDown += OnApplicationShuttingDown;
            _capture.PropertyChanged += OnCaptureStateChanged;
            UpdateCaptureState();
            FitPreview();
            await LoadPreviewAsync();
        }
        catch (Exception exception) { ReportPreviewFailure("Could not open the output editor", exception); }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => DeactivatePage();

    private void OnApplicationShuttingDown(object? sender, EventArgs e)
    {
        IsEnabled = false;
        DeactivatePage();
    }

    private void DeactivatePage()
    {
        _pageActive = false;
        _application.ShuttingDown -= OnApplicationShuttingDown;
        ViewModel.PreviewSettingsChanged -= OnPreviewSettingsChanged;
        ViewModel.DeactivateEffectSettings();
        _capture.PropertyChanged -= OnCaptureStateChanged;
        ClosePreview();
    }

    private async Task LoadPreviewAsync()
    {
        WebView2? browser = null;
        CancellationTokenSource? lifetime = null;
        var generation = _generation;
        try
        {
            if (!_pageActive) return;
            ClosePreview();
            generation = _generation;
            lifetime = _lifetime = new CancellationTokenSource();
            browser = _browser = new WebView2();
            BrowserContainer.Children.Add(browser);
            PreviewStatus.Text = "Loading preview…";
            await ViewModel.ActivateEffectSettingsAsync();
            if (!IsCurrent(browser, generation)) return;
            var port = await App.GetService<ILocalSettingsService>().ReadSettingAsync<int?>("StreamingPort") ?? 8080;
            if (!IsCurrent(browser, generation)) return;
            if (port is < 1 or > 65535) throw new InvalidOperationException("The streaming port must be between 1 and 65535.");
            var configuredFolder = App.GetService<IOptions<LocalSettingsOptions>>().Value.ApplicationDataFolder;
            var environmentFolder = Environment.GetEnvironmentVariable("LocalSettingsOptions__ApplicationDataFolder");
            var profile = RuntimeHelper.IsMSIX && string.IsNullOrWhiteSpace(environmentFolder)
                ? ApplicationData.Current.LocalFolder.Path
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    environmentFolder ?? configuredFolder ?? "Better-SignalRGB-Screen-Capture/ApplicationData");
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(profile, "WebViewOutput"), null)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(15), lifetime.Token);
            if (!IsCurrent(browser, generation)) return;
            await browser.EnsureCoreWebView2Async(environment).AsTask().WaitAsync(TimeSpan.FromSeconds(15), lifetime.Token);
            if (!IsCurrent(browser, generation)) { browser.Close(); return; }

            var core = browser.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            // WinRT can project the event sender through a different managed wrapper.
            // Tie callbacks to our owned control and generation, never RCW equality.
            void NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
            {
                if (!IsCurrent(browser, generation)) { args.Cancel = true; return; }
                OnNavigationStarting(sender, args);
            }
            void NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args) =>
                OnNavigationCompleted(browser, generation, args);
            void WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args) =>
                OnWebMessageReceived(browser, generation, args);
            core.NavigationStarting += NavigationStarting;
            core.NavigationCompleted += NavigationCompleted;
            core.WebMessageReceived += WebMessageReceived;
            core.NewWindowRequested += OnNewWindowRequested;
            _detachBrowserEvents = () =>
            {
                core.NavigationStarting -= NavigationStarting;
                core.NavigationCompleted -= NavigationCompleted;
                core.WebMessageReceived -= WebMessageReceived;
                core.NewWindowRequested -= OnNewWindowRequested;
            };
            _inlineNavigationPending = true;
            core.NavigateToString(StreamingCanvasPage.CreatePreviewHtml(new Uri($"http://localhost:{port}/")));
        }
        catch (OperationCanceledException) when (lifetime?.IsCancellationRequested == true) { }
        catch (Exception exception)
        {
            if (_pageActive && generation == _generation)
            {
                ReportPreviewFailure("Could not load the preview", exception);
                ClosePreview();
            }
        }
    }

    private bool IsCurrent(WebView2 browser, int generation) =>
        generation == _generation && ReferenceEquals(_browser, browser);

    private void ClosePreview()
    {
        _generation++;
        _ready = _inlineNavigationPending = false;
        _trustedDocumentSource = null;
        _pendingSettings = null;
        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = null;
        var browser = _browser;
        _browser = null;
        var detach = _detachBrowserEvents;
        _detachBrowserEvents = null;
        if (browser != null)
        {
            try { detach?.Invoke(); }
            catch (Exception exception) { ApplicationErrorLog.Write("Output preview: detach browser events", exception); }
            try { browser.Close(); }
            catch (Exception exception) { ApplicationErrorLog.Write("Output preview: close browser", exception); }
            try { BrowserContainer.Children.Remove(browser); }
            catch (Exception exception) { ApplicationErrorLog.Write("Output preview: remove browser", exception); }
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // The bridge belongs solely to our inline renderer. Links and subsequent
        // top-level navigations must never replace it with a page that can edit settings.
        if (_inlineNavigationPending && (e.Uri == "about:blank" || e.Uri.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase)))
        {
            _inlineNavigationPending = false;
            return;
        }
        e.Cancel = true;
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e) => e.Handled = true;

    private void OnNavigationCompleted(WebView2 browser, int generation, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!IsCurrent(browser, generation)) return;
        try
        {
            var core = browser.CoreWebView2;
            var source = core.Source;
            _ready = e.IsSuccess && IsInlineDocument(source);
            _trustedDocumentSource = _ready ? source : null;
            PreviewStatus.Text = _ready ? string.Empty : $"Could not display the preview: {e.WebErrorStatus}";
            if (_ready) OnPreviewSettingsChanged(ViewModel, ViewModel.PreviewSettings);
        }
        catch (Exception exception) { ReportPreviewFailure("Could not initialize the preview bridge", exception); }
    }

    private void OnPreviewSettingsChanged(object? sender, SignalRgbEffectSettings settings)
    {
        _pendingSettings = settings;
        if (_ready && _browser is { } browser && _settingsWriterGeneration != _generation)
            _ = SendPreviewSettingsAsync(browser, _generation);
    }

    private async Task SendPreviewSettingsAsync(WebView2 browser, int generation)
    {
        _settingsWriterGeneration = generation;
        try
        {
            // One script at a time; slider/drag bursts replace the pending snapshot.
            while (IsCurrent(browser, generation) && _ready && _pendingSettings is { } settings)
            {
                _pendingSettings = null;
                await browser.CoreWebView2.ExecuteScriptAsync($"window.setPreviewSettings({JsonSerializer.Serialize(settings, JsonOptions)});")
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            }
        }
        catch (Exception exception)
        {
            if (IsCurrent(browser, generation))
            {
                // A timed-out native script may still finish. Stop the bridge until
                // Reload instead of applying further edits out of order or silently
                // leaving the latest coalesced snapshot unapplied.
                _ready = false;
                ReportPreviewFailure("Preview updates stopped. Select Reload preview", exception);
            }
        }
        finally
        {
            if (_settingsWriterGeneration == generation) _settingsWriterGeneration = -1;
        }
    }

    private void OnWebMessageReceived(WebView2 browser, int generation, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!_ready || !IsCurrent(browser, generation) || _trustedDocumentSource == null ||
            !string.Equals(e.Source, _trustedDocumentSource, StringComparison.Ordinal) ||
            !string.Equals(browser.CoreWebView2.Source, _trustedDocumentSource, StringComparison.Ordinal)) return;
        try
        {
            var json = e.WebMessageAsJson;
            if (json.Length > 4096) return;
            using var document = JsonDocument.Parse(json);
            var message = document.RootElement;
            if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("type", out var type) ||
                type.GetString() != "placement" || !TryNumber(message, "x", out var x) || !TryNumber(message, "y", out var y) ||
                !TryNumber(message, "width", out var width) || !TryNumber(message, "height", out var height)) return;
            ViewModel.ApplyPlacement(x, y, width, height);
        }
        catch (JsonException) { /* Ignore malformed messages; retain the last validated placement. */ }
        catch (InvalidOperationException) { /* Unexpected JSON types cannot modify settings. */ }
    }

    private static bool TryNumber(JsonElement value, string name, out double number)
    {
        number = 0;
        return value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
            property.TryGetDouble(out number) && double.IsFinite(number);
    }

    private static bool IsInlineDocument(string source) => source == "about:blank" ||
        source.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase);

    private void ReportPreviewFailure(string context, Exception exception)
    {
        ApplicationErrorLog.Write($"Output preview: {context}", exception);
        if (_pageActive) PreviewStatus.Text = $"{context}: {exception.Message}";
    }

    private void PreviewHost_SizeChanged(object sender, SizeChangedEventArgs e) => FitPreview();

    private void FitPreview()
    {
        var aspectRatio = _capture.CanvasOutputWidth / _capture.CanvasOutputHeight;
        var width = Math.Max(1, Math.Min(PreviewHost.ActualWidth, PreviewHost.ActualHeight * aspectRatio));
        PreviewBorder.Width = width;
        PreviewBorder.Height = width / aspectRatio;
    }

    private void OnCaptureStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsHighQuality) or null or "") FitPreview();
        if (e.PropertyName is nameof(MainViewModel.IsRecording) or nameof(MainViewModel.IsPaused) or nameof(MainViewModel.IsRecordingLoading)
            or nameof(MainViewModel.StatusMessage) or null or "") UpdateCaptureState();
    }

    private void UpdateCaptureState()
    {
        StartCaptureButton.Visibility = !_capture.IsRecording || _capture.IsPaused ? Visibility.Visible : Visibility.Collapsed;
        StartCaptureButton.Content = _capture.IsPaused ? "Resume capture" : "Start capture";
        StartCaptureButton.IsEnabled = !_capture.IsRecordingLoading;
        CaptureHint.Text = !_capture.IsRecording
            ? "Capture is stopped. Start capture to see your sources here; opening this editor does not start recording."
            : _capture.IsPaused ? "Capture is paused. Resume it to refresh the preview."
            : "Live preview. The two output switches determine where these settings are applied.";
        if (!string.IsNullOrEmpty(_capture.StatusMessage)) CaptureHint.Text += " " + _capture.StatusMessage;
    }

    private async void StartCapture_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_capture.IsRecordingLoading) return;
            if (_capture.IsPaused) await _capture.TogglePauseCommand.ExecuteAsync(null);
            else if (!_capture.IsRecording) await _capture.ToggleRecordingCommand.ExecuteAsync(null);
        }
        catch (Exception exception) { PreviewStatus.Text = $"Could not start capture: {exception.Message}"; }
    }

    private async void ReloadPreview_Click(object sender, RoutedEventArgs e) => await LoadPreviewAsync();
}
