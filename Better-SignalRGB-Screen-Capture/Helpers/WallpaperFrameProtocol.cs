using System.Buffers.Binary;
using Better_SignalRGB_Screen_Capture.Core.Helpers;

namespace Better_SignalRGB_Screen_Capture.Helpers;

/// <summary>Fixed-size, little-endian frame header followed by packed BGRA pixels. No images touch disk.</summary>
public static class WallpaperFrameProtocol
{
    public const int HeaderLength = 20;
    private const uint Magic = 0x46505742; // BWPF
    public static int ValidateSize(int width, int height)
    {
        if (width < 1 || height < 1 || width > CaptureGeometry.MaximumDimension || height > CaptureGeometry.MaximumDimension ||
            (long)width * height > CaptureGeometry.MaximumPixels) throw new InvalidDataException("Wallpaper frame dimensions exceed the capture budget.");
        return checked(width * height * 4);
    }
    public static void WriteHeader(Span<byte> header, int width, int height)
    {
        if (header.Length != HeaderLength) throw new ArgumentException("Invalid wallpaper header length.");
        var count = ValidateSize(width, height);
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], width);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], height);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], count);
    }
    public static int ReadHeader(ReadOnlySpan<byte> header, int expectedWidth, int expectedHeight)
    {
        if (header.Length != HeaderLength || BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic ||
            BinaryPrimitives.ReadInt32LittleEndian(header[4..]) != 1) throw new InvalidDataException("Invalid wallpaper worker protocol.");
        var width = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        var count = ValidateSize(width, height);
        if (width != expectedWidth || height != expectedHeight || count != BinaryPrimitives.ReadInt32LittleEndian(header[16..]))
            throw new InvalidDataException("Wallpaper worker returned an unexpected frame size.");
        return count;
    }
}
