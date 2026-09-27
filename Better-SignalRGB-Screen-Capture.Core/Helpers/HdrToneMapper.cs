using System.Buffers.Binary;

namespace Better_SignalRGB_Screen_Capture.Core.Helpers;

/// <summary>
/// Converts bounded, linear scRGB RGBA16_FLOAT readback into opaque SDR/sRGB BGRA8.
/// Input must still be floating point: already clipped 8-bit captures cannot be repaired.
/// </summary>
public static class HdrToneMapper
{
    public const float ShoulderKnee = .75f;
    public const double ReferenceWhiteNits = 80;
    private const int GammaSteps = 16384;
    private const float MaximumHalf = 65504;
    private static readonly float[] HalfValues = CreateHalfValues();
    private static readonly byte[] Srgb = CreateSrgb();

    /// <remarks>
    /// FP16 samples are little-endian R,G,B,A, with caller-provided source row pitch.
    /// Output rows are tightly packed B,G,R,255. The input alpha is ignored, as the
    /// captured surface is rendered against an opaque background by the capture backend.
    /// Neither buffer is retained or allocated. Use this after bounded GPU downsampling.
    /// For SDR, hdr=false skips HDR exposure/shoulder/gamut processing; linear scRGB
    /// is still encoded to sRGB once. Already gamma-encoded BGRA8 must bypass this method.
    /// </remarks>
    public static void ConvertRgba16Float(ReadOnlySpan<byte> fp16, int width, int height, int stride,
        Span<byte> bgra, bool hdr, double sdrWhiteNits)
    {
        if (width is < 1 or > CaptureGeometry.MaximumDimension) throw new ArgumentOutOfRangeException(nameof(width));
        if (height is < 1 or > CaptureGeometry.MaximumDimension || (long)width * height > CaptureGeometry.MaximumPixels)
            throw new ArgumentOutOfRangeException(nameof(height));
        var rowBytes = checked(width * 8);
        if (stride < rowBytes) throw new ArgumentOutOfRangeException(nameof(stride));
        var requiredInput = (long)(height - 1) * stride + rowBytes;
        if (requiredInput > fp16.Length) throw new ArgumentException("The FP16 source buffer is shorter than its dimensions and stride.", nameof(fp16));
        if ((long)width * height * 4 > bgra.Length) throw new ArgumentException("The BGRA output buffer is too small.", nameof(bgra));
        if (fp16.Overlaps(bgra)) throw new ArgumentException("Source and destination must not overlap.", nameof(bgra));
        var scale = hdr ? WhiteScale(sdrWhiteNits) : 1f;
        var destination = 0;
        for (var y = 0; y < height; y++)
        {
            var row = fp16.Slice(y * stride, rowBytes);
            for (var x = 0; x < rowBytes; x += 8)
            {
                var r = HalfValues[BinaryPrimitives.ReadUInt16LittleEndian(row[x..])] * scale;
                var g = HalfValues[BinaryPrimitives.ReadUInt16LittleEndian(row[(x + 2)..])] * scale;
                var b = HalfValues[BinaryPrimitives.ReadUInt16LittleEndian(row[(x + 4)..])] * scale;
                MapNormalized(ref r, ref g, ref b, hdr);
                bgra[destination++] = EncodeSrgb(b);
                bgra[destination++] = EncodeSrgb(g);
                bgra[destination++] = EncodeSrgb(r);
                bgra[destination++] = 255;
            }
        }
    }

    /// <summary>Pure linear-color reference, also usable by GPU/readback verification fixtures.</summary>
    public static (float Red, float Green, float Blue) MapLinearRgb(float red, float green, float blue,
        bool hdr, double sdrWhiteNits)
    {
        var scale = hdr ? WhiteScale(sdrWhiteNits) : 1f;
        red = Sanitize(red) * scale;
        green = Sanitize(green) * scale;
        blue = Sanitize(blue) * scale;
        MapNormalized(ref red, ref green, ref blue, hdr);
        return (red, green, blue);
    }

    private static float WhiteScale(double nits)
    {
        // Bad/unavailable metadata must not create NaNs, divide by zero or extreme gains.
        // Actual Windows SDR white is 80 * DISPLAYCONFIG_SDR_WHITE_LEVEL / 1000 nits.
        if (!double.IsFinite(nits) || nits is < 1 or > 10000) nits = ReferenceWhiteNits;
        return (float)(ReferenceWhiteNits / nits);
    }

    private static void MapNormalized(ref float red, ref float green, ref float blue, bool hdr)
    {
        if (!hdr)
        {
            red = Math.Clamp(red, 0, 1); green = Math.Clamp(green, 0, 1); blue = Math.Clamp(blue, 0, 1);
            return;
        }
        var luminance = .2126f * red + .7152f * green + .0722f * blue;
        if (luminance <= 0) { red = green = blue = 0; return; }

        // Identity through 0.75, followed by a rational shoulder with matching first
        // derivative at the knee. This is a deterministic SDR rendering choice, not
        // a claim to reproduce an HDR display's proprietary tone mapper. Normalized
        // SDR white (1) becomes 0.875 linear, about 240 sRGB, reserving highlight room.
        var mappedLuminance = luminance;
        if (luminance > ShoulderKnee)
        {
            var excess = luminance - ShoulderKnee;
            var room = 1 - ShoulderKnee;
            mappedLuminance = ShoulderKnee + room * excess / (excess + room);
            var gain = mappedLuminance / luminance;
            red *= gain; green *= gain; blue *= gain;
        }

        // Move out-of-gamut colors toward the neutral of equal luminance. A single
        // chroma factor keeps RGB ordering and luma; independent channel clipping
        // would alter both. Negative scRGB primaries are legitimate wide-gamut input.
        var minimum = Math.Min(red, Math.Min(green, blue));
        var maximum = Math.Max(red, Math.Max(green, blue));
        var chroma = 1f;
        if (minimum < 0) chroma = Math.Min(chroma, mappedLuminance / (mappedLuminance - minimum));
        if (maximum > 1) chroma = Math.Min(chroma, (1 - mappedLuminance) / (maximum - mappedLuminance));
        if (chroma < 1)
        {
            red = mappedLuminance + (red - mappedLuminance) * chroma;
            green = mappedLuminance + (green - mappedLuminance) * chroma;
            blue = mappedLuminance + (blue - mappedLuminance) * chroma;
        }
        red = Math.Clamp(red, 0, 1); green = Math.Clamp(green, 0, 1); blue = Math.Clamp(blue, 0, 1);
    }

    private static float Sanitize(float value) => float.IsNaN(value) ? 0 : Math.Clamp(value, -MaximumHalf, MaximumHalf);
    private static byte EncodeSrgb(float linear) => Srgb[(int)(linear * GammaSteps + .5f)];
    private static float[] CreateHalfValues()
    {
        var values = new float[ushort.MaxValue + 1];
        for (var bits = 0; bits < values.Length; bits++) values[bits] = Sanitize((float)BitConverter.UInt16BitsToHalf((ushort)bits));
        return values;
    }
    private static byte[] CreateSrgb()
    {
        var values = new byte[GammaSteps + 1];
        for (var i = 0; i < values.Length; i++)
        {
            var linear = (double)i / GammaSteps;
            var encoded = linear <= .0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - .055;
            values[i] = (byte)Math.Round(encoded * 255, MidpointRounding.AwayFromZero);
        }
        return values;
    }
}
