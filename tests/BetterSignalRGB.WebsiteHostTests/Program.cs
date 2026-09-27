using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new TestApplication();
        });
        return Environment.ExitCode;
    }

    private sealed class TestApplication : Application
    {
        public TestApplication() => DispatcherQueue.GetForCurrentThread().TryEnqueue(async () =>
        {
            try { await RunAsync(); }
            catch (Exception exception) { Console.Error.WriteLine(exception); Environment.ExitCode = 1; }
            finally { Exit(); }
        });
    }

    private static async Task RunAsync()
    {
        // The harness never opens an app window or captures the user's screen.
        // It serves generated content locally and inspects only that content's JPEGs.
        using var server = new SyntheticWebsite();
        var folder = Path.Combine(Path.GetTempPath(), "BetterSignalRGB-WebsiteHost-" + Guid.NewGuid().ToString("N"));
        Console.WriteLine($"Isolated WebView profile: {folder}");
        var source = new SourceItem { Type = SourceType.Website, WebsiteUrl = server.Url, WebsiteWidth = 320,
            WebsiteHeight = 240, WebsiteUserAgent = "BetterSignalRGB-WebsiteHostTest", WebsiteZoom = 1 };
        using (var host = new WebsiteCaptureHost(source, folder))
        {
            await host.PrepareCaptureAsync().WaitAsync(TimeSpan.FromSeconds(20));
            var first = await FrameAsync(host);
            var samples = new List<(Color Canvas, Color Css)>();
            for (var index = 0; index < 6; index++)
            {
                using var bytes = new MemoryStream(index == 0 ? first : await FrameAsync(host));
                using var bitmap = new Bitmap(bytes);
                Check(bitmap.Width == 320 && bitmap.Height == 240, "Capture preserves configured viewport dimensions.");
                samples.Add((bitmap.GetPixel(50, 50), bitmap.GetPixel(250, 50)));
                await Task.Delay(230);
            }
            Check(samples.Select(sample => sample.Canvas.ToArgb()).Distinct().Count() > 2,
                "requestAnimationFrame produces changing captured pixels with no visible application window.");
            Check(samples.Select(sample => sample.Css.ToArgb()).Distinct().Count() > 2,
                "CSS animations keep producing changing captured pixels in the independent render host.");
            Check(server.UserAgent == "BetterSignalRGB-WebsiteHostTest", "Capture uses the selected user-agent.");
            host.CaptureStopped();
            Check(await host.CaptureFrameAsync() == null, "A stopped host cannot publish another frame.");
            var rejected = false;
            try { await host.PrepareCaptureAsync(); } catch (ObjectDisposedException) { rejected = true; }
            Check(rejected, "A disposed browser cannot restart accidentally.");
        }
        using (var host = new WebsiteCaptureHost(source, folder))
        {
            await host.PrepareCaptureAsync();
            Check((await FrameAsync(host)).Length > 100, "A new session reopens the profile after stop.");
        }
        using (var host = new WebsiteCaptureHost(source, folder, highQuality: true))
        {
            await host.PrepareCaptureAsync();
            var lossless = await FrameAsync(host);
            Check(lossless.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
                "HQ website capture returns PNG bytes before the production JPEG encoder.");
            using var data = new MemoryStream(lossless);
            using var bitmap = new Bitmap(data);
            Check(bitmap.Width == 320 && bitmap.Height == 240, "HQ preserves the configured browser viewport.");
        }
        using (var host = new WebsiteCaptureHost(source, folder))
        {
            var preparing = host.PrepareCaptureAsync();
            host.Dispose();
            try { await preparing; } catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException) { }
            Check(await host.CaptureFrameAsync() == null, "Stop during initialization leaves the host inactive.");
        }
        var fixtures = Path.Combine(folder, "generated fixtures");
        Directory.CreateDirectory(fixtures);
        var htmlPath = Path.Combine(fixtures, "animation é # locale.html");
        await File.WriteAllTextAsync(htmlPath, SyntheticWebsite.Html);
        source.WebsiteUrl = new Uri(htmlPath).AbsoluteUri;
        using (var host = new WebsiteCaptureHost(source, folder))
        {
            await host.PrepareCaptureAsync();
            var colors = await SampleColorsAsync(host, 5, 250);
            Check(colors.Distinct().Count() > 2, "A local HTML file with spaces, # and accents renders live animations.");
        }
        var imagePath = Path.Combine(fixtures, "image é # locale.png");
        using (var bitmap = new Bitmap(40, 20))
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Magenta);
            bitmap.Save(imagePath, System.Drawing.Imaging.ImageFormat.Png);
        }
        source.WebsiteUrl = new Uri(imagePath).AbsoluteUri;
        using (var host = new WebsiteCaptureHost(source, folder))
        {
            await host.PrepareCaptureAsync();
            using var data = new MemoryStream(await FrameAsync(host));
            using var bitmap = new Bitmap(data);
            var center = bitmap.GetPixel(160, 120);
            var top = bitmap.GetPixel(160, 10);
            Check(center.R > 230 && center.B > 230 && center.G < 20, "A local PNG with spaces, # and accents appears in the captured pixels.");
            Check(top.R < 20 && top.G < 20 && top.B < 20, "Local media preserves its aspect ratio with black letterboxing.");
        }
        var videoFixture = Environment.GetEnvironmentVariable("BETTERSIGNALRGB_TEST_WEBM")
            ?? Path.GetFullPath("tests/obj/local-media-fixture.webm");
        if (File.Exists(videoFixture))
        {
            var videoPath = Path.Combine(fixtures, "vidéo # locale.webm");
            File.Copy(videoFixture, videoPath);
            source.WebsiteUrl = new Uri(videoPath).AbsoluteUri;
            using var host = new WebsiteCaptureHost(source, folder);
            await host.PrepareCaptureAsync();
            var colors = await SampleColorsAsync(host, 16, 300);
            Check(colors.Take(7).Distinct().Count() > 2, "A local video starts playing automatically.");
            Check(colors.Skip(10).Distinct().Count() > 2, "Local video frames continue changing after its 2.2-second duration, proving looping.");
            host.Dispose();
            source.WebsiteRefreshInterval = 1;
            using var refreshedHost = new WebsiteCaptureHost(source, folder);
            await refreshedHost.PrepareCaptureAsync();
            await FrameAsync(refreshedHost);
            await Task.Delay(1100);
            var refreshedColors = await SampleColorsAsync(refreshedHost, 4, 100);
            Check(refreshedColors.Distinct().Count() > 2, "Automatic refresh rebuilds the local video document and resumes playback.");
            source.WebsiteRefreshInterval = 0;
        }
        else Console.WriteLine("SKIP: local WebM fixture missing; generate it with node tests/GenerateLocalMediaFixture.cjs.");
        source.WebsiteUrl = new Uri(Path.Combine(fixtures, "missing file.html")).AbsoluteUri;
        var missingRejected = false;
        try { using var missing = new WebsiteCaptureHost(source, folder); }
        catch (FileNotFoundException) { missingRejected = true; }
        Check(missingRejected, "A missing local file is reported before starting a browser.");
        Console.WriteLine("PASS: native independent website host: viewport, live rAF/CSS pixels, UA, stop, restart, stop during initialization and local media.");
    }

    private static async Task<int[]> SampleColorsAsync(WebsiteCaptureHost host, int count, int delay)
    {
        var colors = new List<int>();
        for (var index = 0; index < count; index++)
        {
            using var data = new MemoryStream(await FrameAsync(host));
            using var bitmap = new Bitmap(data);
            Check(bitmap.Width == 320 && bitmap.Height == 240, "Local media capture has the configured dimensions.");
            colors.Add(bitmap.GetPixel(50, 50).ToArgb());
            await Task.Delay(delay);
        }
        return colors.ToArray();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }

    private static async Task<byte[]> FrameAsync(WebsiteCaptureHost host)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var frame = await host.CaptureFrameAsync().WaitAsync(timeout.Token);
            if (frame != null) return frame;
            await Task.Delay(30, timeout.Token);
        }
    }

    private sealed class SyntheticWebsite : IDisposable
    {
        public const string Html = """
            <!doctype html><style>html,body{margin:0;background:white}canvas{position:absolute;left:0;top:0}
            #css{position:absolute;left:160px;top:0;width:160px;height:240px;animation:pulse 1.2s linear infinite alternate}
            @keyframes pulse{from{background:rgb(255,10,30)}to{background:rgb(10,220,250)}}</style>
            <canvas id="animated" width="160" height="240"></canvas><div id="css"></div>
            <script>const ctx=animated.getContext('2d');function frame(t){ctx.fillStyle='hsl('+((t/5)%360)+',100%,50%)';ctx.fillRect(0,0,160,240);requestAnimationFrame(frame)}requestAnimationFrame(frame);</script>
            """;
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        public string Url { get; }
        public string? UserAgent { get; private set; }
        public SyntheticWebsite()
        {
            var portProbe = new TcpListener(IPAddress.Loopback, 0);
            portProbe.Start();
            var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
            portProbe.Stop();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _loop = ServeAsync();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync().WaitAsync(_stop.Token);
                    UserAgent = context.Request.UserAgent;
                    var data = Encoding.UTF8.GetBytes(Html);
                    context.Response.ContentType = "text/html; charset=utf-8";
                    context.Response.ContentLength64 = data.Length;
                    await context.Response.OutputStream.WriteAsync(data, _stop.Token);
                    context.Response.Close();
                }
            }
            catch (Exception exception) when (_stop.IsCancellationRequested && exception is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
        }
        public void Dispose() { _stop.Cancel(); _listener.Close(); }
    }
}
