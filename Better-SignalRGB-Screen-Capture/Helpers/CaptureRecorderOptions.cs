using Better_SignalRGB_Screen_Capture.Core.Helpers;
using System.Drawing;
using System.Runtime.InteropServices;
using ScreenRecorderLib;

namespace Better_SignalRGB_Screen_Capture.Helpers;

/// <summary>The native capture configuration, shared with the Windows integration tests.</summary>
public static class CaptureRecorderOptions
{
    // SRL sometimes reports the sink's finalization HRESULT without its "video encoder"
    // prefix. Derive the localized message for this specific, reproduced MF failure.
    private static readonly string UnexpectedMediaFoundationError =
        (Marshal.GetExceptionForHR(unchecked((int)0xC00D36BB))?.Message ?? string.Empty)
        .Replace(" (0xC00D36BB)", string.Empty, StringComparison.OrdinalIgnoreCase);
    public static RecorderOptions Create(List<RecordingSourceBase> sources, int requestedWidth, int requestedHeight,
        int frameRate, bool isRegion = false, bool isWebcam = false, bool useHardwareEncoding = true)
    {
        var (width, height) = CaptureGeometry.GetCarrierSize(requestedWidth, requestedHeight);
        foreach (var source in sources)
        {
            source.IsVideoCaptureEnabled = true;
            source.IsVideoFramePreviewEnabled = false;
            if (!isRegion)
            {
                source.Stretch = StretchMode.Fill;
                source.OutputSize = new ScreenSize(width, height);
            }
        }
        return new RecorderOptions
        {
            SourceOptions = new SourceOptions { RecordingSources = sources },
            OutputOptions = new OutputOptions
            {
                RecorderMode = RecorderMode.Video,
                Stretch = StretchMode.Fill,
                // The native H.264 carrier needs codec-compatible dimensions. This carrier
                // is discarded; the JPEG preview keeps its independent, bounded size.
                OutputFrameSize = new ScreenSize(width, height),
                // SRL fits preview size uniformly. Read the carrier at its actual size and
                // resize the owned pixels on the JPEG worker to preserve tiny/ultrawide geometry.
                VideoFramePreviewSize = new ScreenSize(width, height),
                IsVideoCaptureEnabled = true,
                IsVideoFramePreviewEnabled = true
            },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Encoder = new H264VideoEncoder { BitrateMode = H264BitrateControlMode.Quality, EncoderProfile = H264Profile.Baseline },
                Framerate = Math.Clamp(frameRate, 1, 60),
                Quality = 35,
                IsHardwareEncodingEnabled = useHardwareEncoding,
                IsLowLatencyEnabled = true,
                IsThrottlingDisabled = false,
                IsFixedFramerate = false,
                IsFragmentedMp4Enabled = true,
                IsMp4FastStartEnabled = false
            },
            AudioOptions = new AudioOptions { IsAudioEnabled = false },
            MouseOptions = new MouseOptions { IsMousePointerEnabled = !isWebcam },
            LogOptions = new LogOptions { IsLogEnabled = false }
        };
    }

    public static bool IsEncoderFailure(string error) =>
        error.Contains("encoder", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("sink writer", StringComparison.OrdinalIgnoreCase) ||
        (UnexpectedMediaFoundationError.Length > 0 && string.Equals(error.Trim(), UnexpectedMediaFoundationError, StringComparison.Ordinal));

    public static List<RecordingSourceBase> CreateRegionSources(Rectangle region)
    {
        if (region.Width <= 0 || region.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(region));
        var displays = CoordinateMapper.FindIntersectingDisplays(region, Recorder.GetDisplays());
        if (displays.Count == 0) throw new InvalidOperationException("No display intersects the requested capture region.");
        return CoordinateMapper.MapRegionToDisplays(region, displays)
            .Select(mapping => (RecordingSourceBase)new DisplayRecordingSource(mapping.Display)
            {
                RecorderApi = RecorderApi.WindowsGraphicsCapture,
                IsCursorCaptureEnabled = true,
                IsBorderRequired = false,
                SourceRect = mapping.SourceRect,
                Position = mapping.Position,
                // Preserve one coordinate system across every display tile, without individual
                // minimum-size upscaling that would introduce overlaps and seams.
                OutputSize = new ScreenSize(mapping.SourceRect.Width, mapping.SourceRect.Height),
                Stretch = StretchMode.None
            }).ToList();
    }
}
