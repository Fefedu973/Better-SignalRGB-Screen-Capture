using System.Buffers.Binary;
using System.Diagnostics;
using Better_SignalRGB_Screen_Capture.Core.Helpers;

var checks = 0;
void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
CaptureColorPolicyTests.Run(Check);
void Near(double expected, double actual, double tolerance, string message) =>
    Check(double.IsFinite(actual) && Math.Abs(expected - actual) <= tolerance, $"{message}: expected {expected}, actual {actual}");
void Throws(Action action, string message)
{
    try { action(); } catch (ArgumentException) { Check(true, message); return; }
    throw new InvalidOperationException(message);
}
static void HalfPixel(Span<byte> bytes, float red, float green, float blue, float alpha = 1)
{
    BinaryPrimitives.WriteUInt16LittleEndian(bytes, BitConverter.HalfToUInt16Bits((Half)red));
    BinaryPrimitives.WriteUInt16LittleEndian(bytes[2..], BitConverter.HalfToUInt16Bits((Half)green));
    BinaryPrimitives.WriteUInt16LittleEndian(bytes[4..], BitConverter.HalfToUInt16Bits((Half)blue));
    BinaryPrimitives.WriteUInt16LittleEndian(bytes[6..], BitConverter.HalfToUInt16Bits((Half)alpha));
}
static double Encode(double linear) => 255 * (linear <= .0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - .055);
static double Decode(double encoded) => encoded <= .04045 ? encoded / 12.92 : Math.Pow((encoded + .055) / 1.055, 2.4);
static double Shoulder(double value) => value <= .75 ? value : .75 + .25 * (value - .75) / (value - .5);
static double Luma((float Red, float Green, float Blue) color) => .2126 * color.Red + .7152 * color.Green + .0722 * color.Blue;

// Independent double-precision oracle for neutral levels and HDR reference-white normalization.
foreach (var nits in new[] { 80d, 160d, 480d })
{
    foreach (var neutral in new[] { 0d, .0001, .0031308, .01, .18, .5, .749, .75, .751, 1, 2, 4, 8, 16, 100 })
    {
        var source = (float)(neutral * nits / 80);
        var mapped = HdrToneMapper.MapLinearRgb(source, source, source, true, nits);
        Near(Shoulder(neutral), mapped.Red, 2e-6, "Neutral HDR shoulder matches independent oracle");
        Near(mapped.Red, mapped.Green, 1e-6, "Neutral HDR stays neutral green");
        Near(mapped.Red, mapped.Blue, 1e-6, "Neutral HDR stays neutral blue");
    }
}
var white = HdrToneMapper.MapLinearRgb(6, 6, 6, true, 480);
Near(.875, white.Red, 1e-6, "480-nit SDR white uses scRGB 6, not scRGB 1");
var kneeBelow = HdrToneMapper.MapLinearRgb(.75f - .0001f, .75f - .0001f, .75f - .0001f, true, 80).Red;
var kneeAt = HdrToneMapper.MapLinearRgb(.75f, .75f, .75f, true, 80).Red;
var kneeAbove = HdrToneMapper.MapLinearRgb(.75f + .0001f, .75f + .0001f, .75f + .0001f, true, 80).Red;
Near(1, (kneeAt - kneeBelow) / .0001, .003, "Shoulder has continuous slope from below");
Near(1, (kneeAbove - kneeAt) / .0001, .003, "Shoulder has continuous slope from above");
var previous = -1f;
for (var i = 0; i <= 2000; i++)
{
    var value = i / 100f;
    var mapped = HdrToneMapper.MapLinearRgb(value, value, value, true, 80).Red;
    Check(mapped >= previous && mapped is >= 0 and <= 1, "Grey ramp remains bounded and monotonic through highlights");
    previous = mapped;
}

foreach (var input in new[] { (4f, 1f, .25f), (.25f, 1f, 4f), (-.2f, 1f, .2f), (4f, -.2f, .1f), (.2f, .5f, .1f) })
{
    var mapped = HdrToneMapper.MapLinearRgb(input.Item1, input.Item2, input.Item3, true, 80);
    var luminance = .2126 * input.Item1 + .7152 * input.Item2 + .0722 * input.Item3;
    Near(Shoulder(luminance), Luma(mapped), 2e-6, "Gamut compression preserves mapped luminance instead of clipping independent channels");
    Check(mapped.Red is >= 0 and <= 1 && mapped.Green is >= 0 and <= 1 && mapped.Blue is >= 0 and <= 1, "Wide-gamut colors fit SDR bounds");
    Check(Math.Sign(input.Item1 - input.Item3) == Math.Sign(mapped.Red - mapped.Blue), "Chroma compression preserves RGB ordering");
}
foreach (var nits in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0, -80, 1e20 })
    Near(.875, HdrToneMapper.MapLinearRgb(1, 1, 1, true, nits).Red, 1e-6, "Invalid white metadata falls back to 80 nits");
foreach (var value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -65504f, -1f, 0f, 65504f })
{
    var mapped = HdrToneMapper.MapLinearRgb(value, .5f, .25f, true, 480);
    Check(float.IsFinite(mapped.Red) && float.IsFinite(mapped.Green) && float.IsFinite(mapped.Blue) &&
        mapped.Red is >= 0 and <= 1 && mapped.Green is >= 0 and <= 1 && mapped.Blue is >= 0 and <= 1, "Non-finite and negative primaries never escape finite SDR bounds");
}

// SDR scRGB already uses reference white 80: HDR settings must not alter it, and its
// linear-to-sRGB transfer must occur once (no extra gamma applied to encoded bytes).
var ramp = new byte[256 * 8];
var rampOutput = new byte[256 * 4];
for (var code = 0; code < 256; code++) HalfPixel(ramp.AsSpan(code * 8), (float)Decode(code / 255d), (float)Decode(code / 255d), (float)Decode(code / 255d));
HdrToneMapper.ConvertRgba16Float(ramp, 256, 1, ramp.Length, rampOutput, false, 480);
for (var code = 0; code < 256; code++)
{
    Near(code, rampOutput[code * 4], 1, "SDR gamma roundtrip preserves original code within FP16/LUT rounding");
    Check(rampOutput[code * 4] == rampOutput[code * 4 + 1] && rampOutput[code * 4] == rampOutput[code * 4 + 2] && rampOutput[code * 4 + 3] == 255,
        "SDR ramp remains neutral and opaque");
}
Near(.9, HdrToneMapper.MapLinearRgb(.9f, .2f, .1f, false, 480).Red, 1e-6, "SDR bypass does not apply shoulder above knee");

foreach (var (width, height, padding) in new[] { (1, 1, 0), (3, 5, 7), (319, 199, 16) })
{
    var stride = width * 8 + padding;
    var source = new byte[(height - 1) * stride + width * 8];
    Array.Fill(source, (byte)0xcc);
    var output = new byte[width * height * 4 + 13]; Array.Fill(output, (byte)0x55);
    for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        HalfPixel(source.AsSpan(y * stride + x * 8), (x & 1) == 0 ? 6 : 0, (y & 1) == 0 ? 0 : 6, 0, float.NaN);
    var before = (byte[])source.Clone();
    HdrToneMapper.ConvertRgba16Float(source, width, height, stride, output, true, 480);
    Check(source.SequenceEqual(before), "Conversion does not mutate or borrow the source buffer");
    Check(output.AsSpan(width * height * 4).IndexOfAnyExcept((byte)0x55) < 0, "Conversion respects exact output bounds");
    for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
    {
        var expected = HdrToneMapper.MapLinearRgb((x & 1) == 0 ? 6 : 0, (y & 1) == 0 ? 0 : 6, 0, true, 480);
        var pixel = output.AsSpan((y * width + x) * 4, 4);
        // Check a bounded set of asymmetric pixels rather than counting every implementation loop.
        if ((x == 0 || x == width - 1) && (y == 0 || y == height - 1))
        {
            Near(Encode(expected.Blue), pixel[0], 1, "Padded odd-size conversion keeps blue channel and row orientation");
            Near(Encode(expected.Green), pixel[1], 1, "Padded odd-size conversion keeps green channel and row orientation");
            Near(Encode(expected.Red), pixel[2], 1, "Padded odd-size conversion keeps red channel and row orientation");
        }
        if (pixel[3] != 255) throw new InvalidOperationException("Every converted alpha must be opaque, irrespective of source alpha.");
    }
    Check(true, "All odd-sized output pixels are opaque");
}
var hdrWhite = new byte[8]; HalfPixel(hdrWhite, 6, 6, 6);
var whiteOutput = new byte[4]; HdrToneMapper.ConvertRgba16Float(hdrWhite, 1, 1, 8, whiteOutput, true, 480);
Check(whiteOutput.SequenceEqual(new byte[] { 240, 240, 240, 255 }), "Normalized HDR SDR white maps to documented sRGB 240 with highlight headroom");

// Exercise every Half bit pattern through the actual byte decoder, including both
// infinity signs, NaNs/subnormals and negative zero; output is always valid opaque BGRA.
var allHalf = new byte[65536 * 8]; var allHalfOutput = new byte[65536 * 4];
for (var bits = 0; bits <= ushort.MaxValue; bits++)
    for (var component = 0; component < 4; component++) BinaryPrimitives.WriteUInt16LittleEndian(allHalf.AsSpan(bits * 8 + component * 2), (ushort)bits);
HdrToneMapper.ConvertRgba16Float(allHalf, 256, 256, 256 * 8, allHalfOutput, true, 480);
Check(Enumerable.Range(0, 65536).All(i => allHalfOutput[i * 4 + 3] == 255), "All 65536 FP16 bit patterns convert safely");
Check(allHalfOutput.AsSpan(0, 4).SequenceEqual(new byte[] { 0, 0, 0, 255 }), "Zero remains opaque black");

Throws(() => HdrToneMapper.ConvertRgba16Float(new byte[8], 0, 1, 8, new byte[4], true, 80), "Zero dimensions rejected");
Throws(() => HdrToneMapper.ConvertRgba16Float(new byte[8], 1921, 1, 8, new byte[4], true, 80), "Dimensions above bounded capture limit rejected");
Throws(() => HdrToneMapper.ConvertRgba16Float(new byte[8], 1920, 1920, 15360, new byte[4], true, 80), "Pixel budget exceeded rejected before touching buffers");
Throws(() => HdrToneMapper.ConvertRgba16Float(new byte[8], 1, 1, -8, new byte[4], true, 80), "Negative stride rejected");
Throws(() => HdrToneMapper.ConvertRgba16Float(new byte[8], 2, 1, 8, new byte[8], true, 80), "Short stride rejected");
Throws(() => HdrToneMapper.ConvertRgba16Float(new byte[15], 1, 2, 8, new byte[8], true, 80), "Short padded input rejected");
Throws(() => HdrToneMapper.ConvertRgba16Float(new byte[8], 1, 2, int.MaxValue, new byte[8], true, 80), "Stride arithmetic overflow is rejected using widened bounds");
Throws(() => HdrToneMapper.ConvertRgba16Float(new byte[8], 1, 1, 8, new byte[3], true, 80), "Short output rejected");
var overlap = new byte[12];
Throws(() => HdrToneMapper.ConvertRgba16Float(overlap.AsSpan(0, 8), 1, 1, 8, overlap.AsSpan(4, 4), true, 80), "Overlapping source/destination rejected");

foreach (var (width, height) in new[] { (320, 200), (800, 600) })
{
    var source = new byte[width * height * 8]; var output = new byte[width * height * 4];
    for (var i = 0; i < width * height; i++) HalfPixel(source.AsSpan(i * 8), (i % 31) / 2f, (i % 47) / 3f, (i % 67) / 4f);
    for (var i = 0; i < 20; i++) HdrToneMapper.ConvertRgba16Float(source, width, height, width * 8, output, true, 480);
    var samples = new double[60]; var allocations = 0L;
    for (var i = 0; i < samples.Length; i++)
    {
        var before = GC.GetAllocatedBytesForCurrentThread(); var start = Stopwatch.GetTimestamp();
        HdrToneMapper.ConvertRgba16Float(source, width, height, width * 8, output, true, 480);
        samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        allocations += GC.GetAllocatedBytesForCurrentThread() - before;
    }
    Array.Sort(samples);
    Check(allocations == 0, "Warmed-up tone mapping does not allocate per frame");
    Console.WriteLine(FormattableString.Invariant($"HDR benchmark {width}x{height}, CPU only, samples60: p50={samples[30]:F3}ms p95={samples[56]:F3}ms max={samples[^1]:F3}ms allocations={allocations}B; excludes GPU capture/readback/JPEG."));
}
Console.WriteLine($"PASS: {checks} HDR tone mapping assertions, synthetic FP16 only.");
