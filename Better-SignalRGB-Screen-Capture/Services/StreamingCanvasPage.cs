namespace Better_SignalRGB_Screen_Capture.Services;

internal static class StreamingCanvasPage
{
    private static readonly Lazy<string> Page = new(() => Read("html").Replace(
        "<!--WEB_OUTPUT_SCRIPT-->", "<script>" + Read("js") + "</script>", StringComparison.Ordinal));

    public static string Html => Page.Value;

    public static string CreatePreviewHtml(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.IsAbsoluteUri || !baseUri.IsLoopback || baseUri.Scheme is not ("http" or "https"))
            throw new ArgumentException("The preview requires an absolute loopback HTTP address.", nameof(baseUri));
        var address = System.Net.WebUtility.HtmlEncode(baseUri.AbsoluteUri);
        return Html.Replace("<head>", $"<head><base href=\"{address}\"><script>window.__outputPreview=true;</script>", StringComparison.Ordinal);
    }

    private static string Read(string extension)
    {
        using var stream = typeof(StreamingCanvasPage).Assembly.GetManifestResourceStream("BetterSignalRGB.WebOutput." + extension)
            ?? throw new InvalidOperationException("The embedded web output resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
