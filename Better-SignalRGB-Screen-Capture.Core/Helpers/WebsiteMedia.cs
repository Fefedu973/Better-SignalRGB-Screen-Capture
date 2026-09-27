#nullable enable
namespace Better_SignalRGB_Screen_Capture.Core.Helpers;

/// <summary>Presentation of local media in the same browser used for web sources.</summary>
public static class WebsiteMedia
{
    public const string VirtualHost = "better-signalrgb-media.invalid";

    public static bool TryCreateVideoDocument(string? address, out string folder, out string html)
    {
        folder = html = string.Empty;
        if (!CanvasSourceValidation.TryGetWebsiteUri(address, out var uri) || !uri.IsFile ||
            !IsVideo(Path.GetExtension(uri.LocalPath).ToLowerInvariant())) return false;
        folder = Path.GetDirectoryName(uri.LocalPath) ?? string.Empty;
        if (folder.Length == 0) return false;
        var mediaUrl = "https://" + VirtualHost + "/" + Uri.EscapeDataString(Path.GetFileName(uri.LocalPath));
        html = "<!doctype html><meta charset='utf-8'>" +
            "<meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; media-src https://" + VirtualHost + "; style-src 'unsafe-inline'\">" +
            "<style>html,body{margin:0;width:100%;height:100%;overflow:hidden;background:black}video{position:fixed;inset:0;width:100vw;height:100vh;object-fit:contain}</style>" +
            "<video muted autoplay loop playsinline preload='auto' src=\"" + System.Net.WebUtility.HtmlEncode(mediaUrl) + "\"></video>";
        return true;
    }

    public static string? PreparationScript(string? address)
    {
        if (!CanvasSourceValidation.TryGetWebsiteUri(address, out var uri) || !uri.IsFile) return null;
        var extension = Path.GetExtension(uri.LocalPath).ToLowerInvariant();
        var video = IsVideo(extension);
        var image = extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".avif";
        if (!video && !image) return null;
        // Never interpolate paths into JavaScript. The browser has already navigated
        // to the properly escaped file URI, and owns its native media document.
        return video ? VideoScript : ImageScript;
    }

    private static bool IsVideo(string extension) => extension is ".mp4" or ".webm" or ".m4v" or ".mov" or ".ogv";

    private const string SharedStyle = "document.documentElement.style.cssText='margin:0;width:100%;height:100%;overflow:hidden;background:black';if(document.body)document.body.style.cssText='margin:0;width:100%;height:100%;overflow:hidden;background:black';";
    private const string MediaStyle = "media.style.cssText='position:fixed;inset:0;width:100vw;height:100vh;max-width:none;max-height:none;object-fit:contain;margin:0;padding:0';";
    private const string ImageScript = "(()=>{const media=document.querySelector('img');if(!media)return;" + SharedStyle + MediaStyle + "})()";
    private const string VideoScript = "(()=>{const media=document.querySelector('video');if(!media)return;" + SharedStyle + MediaStyle +
        "media.controls=false;media.muted=true;media.loop=true;media.autoplay=true;media.playsInline=true;media.play().catch(()=>{});})()";
}
