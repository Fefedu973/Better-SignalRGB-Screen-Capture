#nullable enable
using System.Diagnostics.CodeAnalysis;

namespace Better_SignalRGB_Screen_Capture.Core.Helpers;

public static class CanvasSourceValidation
{
    public static bool TryGetWebsiteUri(string? address, [NotNullWhen(true)] out Uri? uri)
    {
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out uri)) return false;
        return uri.IsFile
            ? !string.IsNullOrWhiteSpace(uri.LocalPath)
            : (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && !string.IsNullOrEmpty(uri.Host);
    }

    public static string? ValidateWebsite(string? url, double width, double height, double refreshInterval)
    {
        if (!TryGetWebsiteUri(url, out _))
            return "Enter an http://, https:// or file:/// address, or choose a local file.";
        if (!double.IsFinite(width) || width < 320 || width > 7680 ||
            !double.IsFinite(height) || height < 240 || height > 4320)
            return "Enter a viewport width from 320 to 7680 and a height from 240 to 4320 pixels.";
        if (!double.IsFinite(refreshInterval) || refreshInterval < 0 || refreshInterval > 3600)
            return "Enter a refresh interval from 0 to 3600 seconds (0 disables refresh).";
        return null;
    }
}
