using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using Better_SignalRGB_Screen_Capture.Models;
using ScreenRecorderLib;

namespace Better_SignalRGB_Screen_Capture.Helpers;

/// <summary>Production source selection, also exercised by native capture smoke tests.</summary>
public static class CaptureSourceFactory
{
    public static string GetWebcamFormatId(VideoCaptureFormat format) => FormattableString.Invariant(
        $"{format.VideoFormat:D}:{format.FrameSize.Width}x{format.FrameSize.Height}@{format.Framerate:R}");

    public static List<RecordingSourceBase> Create(SourceItem source) => source.Type switch
    {
        SourceType.Monitor => [CreateDisplay(source)],
        SourceType.Process => [CreateWindow(source)],
        SourceType.Webcam => [CreateWebcam(source)],
        SourceType.Region => CreateRegion(source),
        _ => throw new InvalidOperationException($"Unsupported recording source: {source.Type}.")
    };

    internal static T? FindDevice<T>(IEnumerable<T> devices, string id, string name,
        Func<T, string> getId, Func<T, string> getName) where T : class
    {
        // A known stable ID must never fall through to a different, identically
        // named monitor/camera. Name recovery is only safe for a unique legacy name.
        if (!string.IsNullOrWhiteSpace(id)) return devices.FirstOrDefault(device => string.Equals(getId(device), id, StringComparison.OrdinalIgnoreCase));
        var matches = devices.Where(device => string.Equals(getName(device), name, StringComparison.Ordinal)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static DisplayRecordingSource CreateDisplay(SourceItem source)
    {
        var display = FindDevice(Recorder.GetDisplays(), source.DeviceId, source.Name, display => display.DeviceName, display => display.FriendlyName)
            ?? throw new InvalidOperationException($"Display not found: {source.Name}.");
        return new(display) { RecorderApi = RecorderApi.WindowsGraphicsCapture, IsCursorCaptureEnabled = true, IsBorderRequired = false };
    }

    private static VideoCaptureRecordingSource CreateWebcam(SourceItem source)
    {
        var camera = FindDevice(Recorder.GetSystemVideoCaptureDevices(), source.DeviceId, source.Name, camera => camera.DeviceName, camera => camera.FriendlyName)
            ?? throw new InvalidOperationException($"Webcam not found: {source.Name}.");
        var recordingSource = new VideoCaptureRecordingSource(camera);
        if (!string.IsNullOrWhiteSpace(source.WebcamFormatId))
            recordingSource.CaptureFormat = Recorder.GetSupportedVideoCaptureFormatsForDevice(camera.DeviceName)
                .FirstOrDefault(format => string.Equals(GetWebcamFormatId(format), source.WebcamFormatId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"The selected capture format is no longer available for {source.Name}. Select another format or Auto.");
        return recordingSource;
    }

    private static List<RecordingSourceBase> CreateRegion(SourceItem source)
    {
        if (source.RegionX is not { } x || source.RegionY is not { } y || source.RegionWidth is not > 0 || source.RegionHeight is not > 0)
            throw new InvalidOperationException($"Invalid capture region: {source.Name}.");
        return CaptureRecorderOptions.CreateRegionSources(new Rectangle(x, y, source.RegionWidth.Value, source.RegionHeight.Value));
    }

    private static WindowRecordingSource CreateWindow(SourceItem source)
    {
        var windows = Recorder.GetWindows().ToArray();
        var owners = new Dictionary<int, (string? Name, string? Path)>();
        var candidates = new List<WindowIdentity>();
        foreach (var window in windows)
        {
            if (GetWindowThreadProcessId(window.Handle, out var owner) == 0 || owner > int.MaxValue) continue;
            var id = (int)owner;
            if (!owners.TryGetValue(id, out var processInfo))
            {
                string? name = null, path = null;
                try
                {
                    using var process = Process.GetProcessById(id);
                    name = process.ProcessName;
                    path = process.MainModule?.FileName;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                owners[id] = processInfo = (name, path);
            }
            candidates.Add(new(window.Handle.ToInt64(), id, window.Title, processInfo.Name, processInfo.Path));
        }
        var selected = SelectWindow(candidates, source)
            ?? throw new InvalidOperationException($"Window not found or ambiguous: {source.Name}. Select the exact window again.");
        return new(windows.First(window => window.Handle.ToInt64() == selected.Handle))
            { IsBorderRequired = false, IsCursorCaptureEnabled = true };
    }

    internal sealed record WindowIdentity(long Handle, int ProcessId, string Title, string? ProcessName, string? ExecutablePath);

    internal static WindowIdentity? SelectWindow(IEnumerable<WindowIdentity> windows, SourceItem source)
    {
        var candidates = windows.Where(MatchesPath).ToArray();
        // HWND is the selected surface, not the process's first/main window. Validate its
        // owner before trusting a persisted handle, which Windows may reuse after exit.
        if (source.WindowHandle is { } handle && candidates.FirstOrDefault(window => window.Handle == handle &&
            (source.ProcessId is null || window.ProcessId == source.ProcessId)) is { } exact) return exact;
        bool MatchesTitle(WindowIdentity window) => string.IsNullOrEmpty(source.WindowTitle) ||
            string.Equals(window.Title, source.WindowTitle, StringComparison.Ordinal);
        static WindowIdentity? Unique(IEnumerable<WindowIdentity> matches)
        {
            var firstTwo = matches.Take(2).ToArray();
            return firstTwo.Length == 1 ? firstTwo[0] : null;
        }
        if (source.ProcessId is { } pid && Unique(candidates.Where(window => window.ProcessId == pid && MatchesTitle(window))) is { } sameProcess)
            return sameProcess;
        // Recovery by executable and title must be unique across process instances.
        // Without a saved title, only a single candidate is safe for a legacy source.
        return !string.IsNullOrWhiteSpace(source.ProcessPath) ? Unique(candidates.Where(MatchesTitle)) : null;

        bool MatchesPath(WindowIdentity window)
        {
            if (string.IsNullOrWhiteSpace(source.ProcessPath)) return true;
            try
            {
                return Path.IsPathRooted(source.ProcessPath)
                    ? window.ExecutablePath is not null && string.Equals(Path.GetFullPath(window.ExecutablePath), Path.GetFullPath(source.ProcessPath), StringComparison.OrdinalIgnoreCase)
                    : string.Equals(window.ProcessName, Path.GetFileNameWithoutExtension(source.ProcessPath), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
