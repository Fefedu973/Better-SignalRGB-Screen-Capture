using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using Better_SignalRGB_Screen_Capture.Helpers;

namespace Better_SignalRGB_Screen_Capture.Services;

/// <summary>Owns one hidden worker; only that process is terminated on timeout/cancellation.</summary>
public static class WallpaperCaptureProcess
{
    public static async Task PumpAsync(string monitorDeviceId, int width, int height, int frameRate,
        Action<byte[], int, int> acceptOwnedPixels, CancellationToken cancellationToken)
    {
        WallpaperFrameProtocol.ValidateSize(width, height);
        if (string.IsNullOrEmpty(monitorDeviceId)) throw new InvalidOperationException("Choose a display for Wallpaper Engine capture.");
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("The application executable path is unavailable."))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        using (var parent = Process.GetCurrentProcess())
        {
            foreach (var argument in new[] { WallpaperCaptureWorker.Argument, "--monitor", monitorDeviceId,
                "--width", width.ToString(CultureInfo.InvariantCulture), "--height", height.ToString(CultureInfo.InvariantCulture),
                "--fps", frameRate.ToString(CultureInfo.InvariantCulture), "--parent", Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                "--parent-start", parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) })
                start.ArgumentList.Add(argument);
        }
        await PumpCoreAsync(start, width, height, acceptOwnedPixels, cancellationToken, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    internal static async Task PumpCoreAsync(ProcessStartInfo start, int width, int height, Action<byte[], int, int> acceptOwnedPixels,
        CancellationToken cancellationToken, TimeSpan frameTimeout, Action<int>? workerStarted = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("Could not start the Wallpaper Engine capture worker.");
        using var errorCancellation = new CancellationTokenSource();
        var errorText = ReadErrorAsync(process.StandardError, errorCancellation.Token);
        try
        {
            workerStarted?.Invoke(process.Id);
            var header = new byte[WallpaperFrameProtocol.HeaderLength];
            while (true)
            {
                // Cancel the pipe read itself on expiry; do not leave a timed-out read
                // racing with pooled buffer reuse while the native call is still blocked.
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(frameTimeout);
                byte[] pixels;
                try { pixels = await ReadFrameAsync(process.StandardOutput.BaseStream, header, width, height, deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { throw new TimeoutException("Wallpaper Engine's capture worker did not return a frame within five seconds."); }
                // Ownership is transferred even if the consumer rejects a stopped session.
                acceptOwnedPixels(pixels, width, height);
            }
        }
        catch (EndOfStreamException)
        {
            var error = await errorText.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            throw new IOException(string.IsNullOrWhiteSpace(error) ? "Wallpaper Engine's capture worker ended before returning a complete frame." : error);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { Debug.WriteLine(error.Message); }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception error) when (error is InvalidOperationException or TimeoutException) { Debug.WriteLine(error.Message); }
            // Even if terminating a blocked native process fails, cancel its stderr
            // read rather than leaving an unobserved task waiting on an open pipe.
            errorCancellation.Cancel();
            try { await errorText.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (Exception error) when (error is TimeoutException or IOException or ObjectDisposedException or OperationCanceledException) { Debug.WriteLine(error.Message); }
        }
    }

    internal static async Task<byte[]> ReadFrameAsync(Stream stream, byte[] header, int width, int height, CancellationToken cancellationToken)
    {
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var count = WallpaperFrameProtocol.ReadHeader(header, width, height);
        var pixels = ArrayPool<byte>.Shared.Rent(count);
        try { await stream.ReadExactlyAsync(pixels.AsMemory(0, count), cancellationToken).ConfigureAwait(false); return pixels; }
        catch { ArrayPool<byte>.Shared.Return(pixels); throw; }
    }

    private static async Task<string> ReadErrorAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        // Drain the pipe even if a broken entry point is unexpectedly verbose, but
        // keep at most 1024 characters in the parent process.
        var buffer = new char[256]; var text = new System.Text.StringBuilder(1024);
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            if (text.Length < 1024) text.Append(buffer, 0, Math.Min(read, 1024 - text.Length));
        return text.ToString().Trim();
    }
}
