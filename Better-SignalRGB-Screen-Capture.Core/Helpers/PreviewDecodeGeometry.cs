namespace Better_SignalRGB_Screen_Capture.Core.Helpers;

/// <summary>Preview decoding follows physical display size without exceeding the received image.</summary>
public static class PreviewDecodeGeometry
{
    public static int DecodeHeight(int encodedWidth, int encodedHeight, double logicalWidth, double logicalHeight,
        double xBasisX, double xBasisY, double yBasisX, double yBasisY, double dpi)
    {
        if (encodedWidth < 1 || encodedHeight < 1) return 1;
        // Map encoded image pixels to the root visual, accounting for non-uniform
        // stretching before rotation. The largest singular value preserves all axes.
        var a = xBasisX * logicalWidth / encodedWidth;
        var b = xBasisY * logicalWidth / encodedWidth;
        var c = yBasisX * logicalHeight / encodedHeight;
        var d = yBasisY * logicalHeight / encodedHeight;
        var x = a * a + b * b;
        var y = c * c + d * d;
        var cross = a * c + b * d;
        var scale = Math.Sqrt((x + y + Math.Sqrt((x - y) * (x - y) + 4 * cross * cross)) / 2) * dpi;
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        var budget = Math.Min(1d, Math.Min((double)CaptureGeometry.MaximumDimension / encodedWidth,
            (double)CaptureGeometry.MaximumDimension / encodedHeight));
        budget = Math.Min(budget, Math.Sqrt((double)CaptureGeometry.MaximumPixels / ((double)encodedWidth * encodedHeight)));
        var limit = Math.Max(1, (int)Math.Floor(encodedHeight * budget));
        return Math.Clamp((int)Math.Ceiling(encodedHeight * Math.Min(1, scale)), 1, limit);
    }

    /// <summary>Read JPEG frame metadata only; skip segments without decoding pixels or allocating buffers.</summary>
    public static bool TryGetJpegSize(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = height = 0;
        if (bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8) return false;
        var offset = 2;
        while (offset < bytes.Length)
        {
            if (bytes[offset++] != 0xff) return false;
            while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
            if (offset >= bytes.Length) return false;
            var marker = bytes[offset++];
            if (marker is 0xda or 0xd9) return false;
            if (marker is 0x01 or >= 0xd0 and <= 0xd8) continue;
            if (offset + 2 > bytes.Length) return false;
            var length = (bytes[offset] << 8) | bytes[offset + 1];
            if (length < 2 || length > bytes.Length - offset) return false;
            if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc))
            {
                if (length < 8) return false;
                height = (bytes[offset + 3] << 8) | bytes[offset + 4];
                width = (bytes[offset + 5] << 8) | bytes[offset + 6];
                return width > 0 && height > 0;
            }
            offset += length;
        }
        return false;
    }
}
