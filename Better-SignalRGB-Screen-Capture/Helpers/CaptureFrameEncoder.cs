using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Better_SignalRGB_Screen_Capture.Core.Helpers;

namespace Better_SignalRGB_Screen_Capture.Helpers;

/// <summary>Encodes owned, tightly packed BGRA pixels; contains no UI or native-callback lifetime.</summary>
public static class CaptureFrameEncoder
{
    private static readonly Lazy<ImageCodecInfo> JpegEncoder = new(() =>
        ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid));

    public const long StandardQuality = 65;
    public const long HighQuality = 92;

    /// <summary>Isotropic oversampling preserves detail after any canonical canvas rotation.</summary>
    public static (int Width, int Height) GetPreviewSize(int width, int height, bool highQuality) => highQuality
        ? CaptureGeometry.GetOutputSize((int)Math.Min(int.MaxValue, Math.Max(1L, width) * 3),
            (int)Math.Min(int.MaxValue, Math.Max(1L, height) * 3))
        : CaptureGeometry.GetOutputSize(width, height);

    public static EncoderParameters CreateParameters(long quality = StandardQuality)
    {
        var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, Math.Clamp(quality, 0, 100));
        return parameters;
    }

    /// <summary>Copies the callback-owned GPU rows before the native callback returns.</summary>
    public static void CopyBgraRows(IntPtr data, int width, int height, int stride, byte[] destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (data == IntPtr.Zero) throw new ArgumentException("The native pixel buffer is null.", nameof(data));
        var rowBytes = checked(width * 4);
        if (stride < rowBytes) throw new ArgumentOutOfRangeException(nameof(stride));
        if (destination.Length < checked(rowBytes * height)) throw new ArgumentException("The pixel buffer is too small.", nameof(destination));
        for (var y = 0; y < height; y++)
            Marshal.Copy(IntPtr.Add(data, checked(y * stride)), destination, y * rowBytes, rowBytes);
    }

    public static byte[] EncodeJpeg(byte[] pixels, int width, int height, int outputWidth, int outputHeight,
        MemoryStream output, EncoderParameters parameters)
    {
        if (width < 1 || height < 1 || outputWidth < 1 || outputHeight < 1 || pixels.Length < checked(width * height * 4))
            throw new ArgumentException("Invalid pixel buffer or JPEG dimensions.");
        output.SetLength(0);
        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(width, height, checked(width * 4), PixelFormat.Format32bppRgb, pin.AddrOfPinnedObject());
            if (width == outputWidth && height == outputHeight) bitmap.Save(output, JpegEncoder.Value, parameters);
            else
            {
                // The native codec's carrier can be larger than the requested preview. Resize
                // here with explicit Fill geometry rather than SRL's aspect-preserving preview fit.
                using var resized = new Bitmap(outputWidth, outputHeight, PixelFormat.Format24bppRgb);
                using (var graphics = Graphics.FromImage(resized))
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.InterpolationMode = InterpolationMode.Bilinear;
                    graphics.PixelOffsetMode = PixelOffsetMode.Half;
                    graphics.DrawImage(bitmap, new Rectangle(0, 0, outputWidth, outputHeight), 0, 0, width, height, GraphicsUnit.Pixel);
                }
                resized.Save(output, JpegEncoder.Value, parameters);
            }
            return output.ToArray();
        }
        finally { pin.Free(); }
    }

    public static byte[] ResizeJpeg(byte[] jpeg, int width, int height) =>
        EncodeBrowserFrames(jpeg, (width, height), (width, height), false).Preview;

    /// <summary>Decodes one browser image and produces both fixed per-frame outputs off the UI thread.
    /// HQ browser hosts supply PNG so the detailed JPEG does not inherit a lower-quality JPEG pass.</summary>
    public static (byte[] Preview, byte[] SignalRgb) EncodeBrowserFrames(byte[] image,
        (int Width, int Height) previewSize, (int Width, int Height) signalSize, bool highQuality)
    {
        using var input = new MemoryStream(image, writable: false);
        using var source = Image.FromStream(input);
        if (!highQuality && source.Width == previewSize.Width && source.Height == previewSize.Height && source.RawFormat.Guid == ImageFormat.Jpeg.Guid)
            return (image, image);
        using var output = new MemoryStream();
        using var standardParameters = CreateParameters();
        if (!highQuality)
        {
            var jpeg = EncodeImage(source, previewSize, output, standardParameters);
            return (jpeg, jpeg);
        }
        using var detailedParameters = CreateParameters(HighQuality);
        var preview = EncodeImage(source, previewSize, output, detailedParameters);
        var signal = EncodeImage(source, signalSize, output, standardParameters);
        return (preview, signal);
    }

    private static byte[] EncodeImage(Image source, (int Width, int Height) size, MemoryStream output, EncoderParameters parameters)
    {
        output.SetLength(0);
        if (source.Width == size.Width && source.Height == size.Height)
        {
            source.Save(output, JpegEncoder.Value, parameters);
            return output.ToArray();
        }
        using var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.Bilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(source, new Rectangle(0, 0, size.Width, size.Height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
        }
        bitmap.Save(output, JpegEncoder.Value, parameters);
        return output.ToArray();
    }

    public static byte[] CreateBlackFrame()
    {
        using var bitmap = new Bitmap(320, 200, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.Black);
        using var output = new MemoryStream();
        using var parameters = CreateParameters();
        bitmap.Save(output, JpegEncoder.Value, parameters);
        return output.ToArray();
    }
}
