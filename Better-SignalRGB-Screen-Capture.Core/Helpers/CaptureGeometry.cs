namespace Better_SignalRGB_Screen_Capture.Core.Helpers;

public static class CaptureGeometry
{
    public const int MaximumDimension = 1920;
    public const int MaximumPixels = 1920 * 1080;
    // The codec rounds each odd axis up by one pixel. Reserve that bounded overhead
    // separately so a valid JPEG size near the pixel budget is never rejected.
    public const int MaximumCarrierPixels = MaximumPixels + 2 * MaximumDimension + 4;

    /// <summary>JPEG dimensions preserve odd and one-pixel sizes, bounded by texture and memory limits.</summary>
    public static (int Width, int Height) GetOutputSize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var scale = Math.Min(1d, Math.Min((double)MaximumDimension / width, (double)MaximumDimension / height));
        scale = Math.Min(scale, Math.Sqrt((double)MaximumPixels / ((double)width * height)));
        return (Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(height * scale)));
    }

    /// <summary>Separate, codec-compatible carrier; it never changes the JPEG or canvas dimensions.</summary>
    public static (int Width, int Height) GetCarrierSize(int width, int height)
    {
        var output = GetOutputSize(width, height);
        return (Math.Max(64, (output.Width + 1) & ~1), Math.Max(64, (output.Height + 1) & ~1));
    }
}
