using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Core.Helpers;
using Better_SignalRGB_Screen_Capture.Helpers;
using Better_SignalRGB_Screen_Capture.Models;
using Microsoft.Extensions.Options;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Better_SignalRGB_Screen_Capture.Services;

public sealed class WebsiteCaptureHostFactory(IOptions<LocalSettingsOptions> options) : IWebsiteCaptureHostFactory
{
    public IWebsiteFrameSource Create(SourceItem source, bool highQuality = false)
    {
        var environmentFolder = Environment.GetEnvironmentVariable("LocalSettingsOptions__ApplicationDataFolder");
        var configured = environmentFolder ?? options.Value.ApplicationDataFolder;
        var folder = RuntimeHelper.IsMSIX && string.IsNullOrWhiteSpace(environmentFolder)
            ? ApplicationData.Current.LocalFolder.Path
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                configured ?? "Better-SignalRGB-Screen-Capture/ApplicationData");
        return new WebsiteCaptureHost(source, Path.Combine(folder, "WebViewCapture"), highQuality);
    }
}

/// <summary>
/// A browser owned by capture, independent of the editor's Loaded/Visibility state.
/// Every method runs on the caller's existing UI dispatcher; this class creates no thread.
/// </summary>
public sealed class WebsiteCaptureHost : IWebsiteFrameSource, IDisposable
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly string _url;
    private readonly string? _userAgent;
    private readonly string _userDataFolder;
    private string? _mediaDocument;
    private bool _mediaNavigationPending;
    private readonly int _width;
    private readonly int _height;
    private readonly double _zoom;
    private readonly int _refreshSeconds;
    private readonly CoreWebView2CapturePreviewImageFormat _captureFormat;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _initialization;
    private CoreWebView2Controller? _controller;
    private IntPtr _window;
    private bool _disposed;
    private bool _ready;
    private bool _capturing;
    private ulong _navigationId;
    private int _navigationGeneration;
    private long _navigationStarted;
    private long _lastRefresh;
    private Exception? _failure;

    public WebsiteCaptureHost(SourceItem source, string? userDataFolder = null, bool highQuality = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!CanvasSourceValidation.TryGetWebsiteUri(source.WebsiteUrl, out var uri))
            throw new ArgumentException("Website capture requires an HTTP, HTTPS or file URL.", nameof(source));
        if (uri.IsFile && !File.Exists(uri.LocalPath))
            throw new FileNotFoundException("The local website or media file could not be found.", uri.LocalPath);
        _url = uri.AbsoluteUri;
        _userAgent = source.WebsiteUserAgent;
        _width = Math.Clamp(source.WebsiteWidth, 320, 7680);
        _height = Math.Clamp(source.WebsiteHeight, 240, 4320);
        _zoom = double.IsFinite(source.WebsiteZoom) ? Math.Clamp(source.WebsiteZoom, .25, 4) : 1;
        _refreshSeconds = Math.Clamp(source.WebsiteRefreshInterval, 0, 86400);
        _captureFormat = highQuality ? CoreWebView2CapturePreviewImageFormat.Png : CoreWebView2CapturePreviewImageFormat.Jpeg;
        _userDataFolder = userDataFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Better-SignalRGB-Screen-Capture", "ApplicationData", "WebViewCapture");
    }

    public Task PrepareCaptureAsync()
    {
        CheckThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _initialization ??= InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            // A real visible HWND is required by CapturePreview. It stays outside the
            // virtual desktop, has no taskbar entry and can never activate the user's window.
            _window = CreateWindowEx(0x08000080, "STATIC", string.Empty,
                0x90000000, GetSystemMetrics(76) - _width - 256, GetSystemMetrics(77) - _height - 256,
                _width, _height, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_window == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the website capture surface.");
            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows"
            };
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, _userDataFolder, options)
                .AsTask().WaitAsync(OperationTimeout, _lifetime.Token);
            ObjectDisposedException.ThrowIf(_disposed, this);
            var creation = environment.CreateCoreWebView2ControllerAsync(
                CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)_window.ToInt64())).AsTask();
            CoreWebView2Controller controller;
            try { controller = await creation.WaitAsync(OperationTimeout, _lifetime.Token); }
            catch
            {
                // WinRT creation can finish after a timeout/cancel. Close that controller
                // on this same dispatcher rather than retaining a detached browser process.
                _ = CloseLateControllerAsync(creation);
                throw;
            }
            if (_disposed) { controller.Close(); return; }
            _controller = controller;
            controller.ShouldDetectMonitorScaleChanges = false;
            controller.RasterizationScale = 1;
            controller.Bounds = new Rect(0, 0, _width, _height);
            controller.IsVisible = true;
            var browser = controller.CoreWebView2;
            if (!string.IsNullOrWhiteSpace(_userAgent)) browser.Settings.UserAgent = _userAgent;
            browser.Settings.AreDefaultContextMenusEnabled = false;
            browser.Settings.AreDevToolsEnabled = false;
            browser.Settings.AreDefaultScriptDialogsEnabled = false;
            browser.NewWindowRequested += BlockNewWindow;
            browser.PermissionRequested += DenyInteractivePermission;
            browser.DownloadStarting += CancelDownload;
            browser.NavigationStarting += NavigationStarting;
            browser.NavigationCompleted += NavigationCompleted;
            browser.ProcessFailed += ProcessFailed;
            _navigationStarted = Stopwatch.GetTimestamp();
            if (WebsiteMedia.TryCreateVideoDocument(_url, out var mediaFolder, out var mediaDocument))
            {
                _mediaDocument = mediaDocument;
                browser.SetVirtualHostNameToFolderMapping(WebsiteMedia.VirtualHost, mediaFolder,
                    CoreWebView2HostResourceAccessKind.DenyCors);
                NavigateMediaDocument(browser, mediaDocument);
            }
            else browser.Navigate(_url);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private static async Task CloseLateControllerAsync(Task<CoreWebView2Controller> creation)
    {
        try { (await creation).Close(); }
        catch (Exception exception) { Debug.WriteLine($"Late website controller cleanup: {exception.Message}"); }
    }

    private void NavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (_disposed) return;
        // NavigateToString uses a data URL in some WebView2 runtimes and about:blank
        // in others. Only the navigation we just initiated can use either scheme.
        var ownedMediaNavigation = _mediaNavigationPending &&
            (args.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || args.Uri == "about:blank");
        _mediaNavigationPending = false;
        // Block external application protocols from background pages.
        if (!CanvasSourceValidation.TryGetWebsiteUri(args.Uri, out _) && !ownedMediaNavigation)
        {
            args.Cancel = true;
            _failure = new InvalidOperationException($"The website attempted to navigate to an unsupported URL ({args.Uri.Split(':')[0]}).");
            return;
        }
        _navigationGeneration++;
        _navigationId = args.NavigationId;
        _navigationStarted = Stopwatch.GetTimestamp();
        _ready = false;
        _failure = null;
    }

    private async void NavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        var generation = _navigationGeneration;
        if (_disposed || args.NavigationId != _navigationId) return;
        try
        {
            if (!args.IsSuccess) throw new InvalidOperationException($"Website navigation failed: {args.WebErrorStatus}.");
            if (WebsiteMedia.PreparationScript(sender.Source) is { } mediaScript)
                await sender.ExecuteScriptAsync(mediaScript).AsTask().WaitAsync(OperationTimeout, _lifetime.Token);
            await sender.ExecuteScriptAsync(WebsitePreviewState.ZoomScript(_zoom)).AsTask()
                .WaitAsync(OperationTimeout, _lifetime.Token);
            if (_disposed || generation != _navigationGeneration) return;
            _lastRefresh = Stopwatch.GetTimestamp();
            _ready = true;
        }
        catch (Exception exception)
        {
            if (!_disposed && generation == _navigationGeneration) _failure = exception;
        }
    }

    private void ProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args)
    {
        if (!_disposed) _failure = new InvalidOperationException($"Website browser process failed: {args.ProcessFailedKind}.");
    }

    [SuppressMessage("Reliability", "CA2025:Do not pass disposable objects into unawaited tasks",
        Justification = "The completion observer takes sole ownership of the stream until the native capture callback finishes, including after timeout and controller.Close().")]
    public async Task<byte[]?> CaptureFrameAsync()
    {
        CheckThread();
        if (_failure != null) throw new InvalidOperationException(_failure.Message, _failure);
        if (_disposed) return null;
        if (!_ready)
        {
            if (_navigationStarted != 0 && Stopwatch.GetElapsedTime(_navigationStarted) > TimeSpan.FromSeconds(30))
                throw new TimeoutException("The website did not finish loading within 30 seconds.");
            return null;
        }
        if (_capturing || _controller is not { } controller) return null;
        if (_refreshSeconds > 0 && Stopwatch.GetElapsedTime(_lastRefresh).TotalSeconds >= _refreshSeconds)
        {
            _ready = false;
            _navigationStarted = Stopwatch.GetTimestamp();
            if (_mediaDocument is { } mediaDocument) NavigateMediaDocument(controller.CoreWebView2, mediaDocument);
            else controller.CoreWebView2.Reload();
            return null;
        }
        _capturing = true;
        var generation = _navigationGeneration;
        var stream = new InMemoryRandomAccessStream();
        Task? capture = null;
        try
        {
            capture = controller.CoreWebView2.CapturePreviewAsync(_captureFormat, stream).AsTask();
            await capture.WaitAsync(OperationTimeout, _lifetime.Token);
            if (_disposed || generation != _navigationGeneration) return null;
            if (stream.Size == 0 || stream.Size > 32 * 1024 * 1024)
                throw new InvalidDataException("The website returned an invalid screenshot size.");
            stream.Seek(0);
            var bytes = new byte[checked((int)stream.Size)];
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            if (await reader.LoadAsync((uint)bytes.Length) != bytes.Length)
                throw new IOException("The website screenshot was incomplete.");
            reader.ReadBytes(bytes);
            return _disposed || generation != _navigationGeneration ? null : bytes;
        }
        catch (TimeoutException exception)
        {
            // Stop a stuck browser before allowing another request to use its COM stream.
            _failure = exception;
            Dispose();
            throw;
        }
        catch (OperationCanceledException) when (_disposed) { return null; }
        finally
        {
            _capturing = false;
            // Transfer stream ownership to the completion observer, even on timeout.
            // WebView2 may still be writing until Close delivers its final callback.
            _ = DisposeStreamAfterCaptureAsync(capture ?? Task.CompletedTask, stream);
        }
    }

    private static async Task DisposeStreamAfterCaptureAsync(Task capture, InMemoryRandomAccessStream stream)
    {
        try { await capture; }
        catch (Exception exception) { Debug.WriteLine($"Closed website capture: {exception.Message}"); }
        finally { stream.Dispose(); }
    }

    private static void BlockNewWindow(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args) => args.Handled = true;
    private void NavigateMediaDocument(CoreWebView2 browser, string document)
    {
        _mediaNavigationPending = true;
        browser.NavigateToString(document);
    }
    private static void DenyInteractivePermission(CoreWebView2 sender, CoreWebView2PermissionRequestedEventArgs args) => args.State = CoreWebView2PermissionState.Deny;
    private static void CancelDownload(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs args) => args.Cancel = true;

    public void CaptureStopped() => Dispose();

    public void Dispose()
    {
        CheckThread();
        if (_disposed) return;
        _disposed = true;
        _ready = false;
        _lifetime.Cancel();
        _lifetime.Dispose();
        var controller = _controller;
        _controller = null;
        try { controller?.Close(); }
        finally
        {
            if (_window != IntPtr.Zero) { DestroyWindow(_window); _window = IntPtr.Zero; }
        }
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("Website capture must use its owning UI dispatcher.");
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
