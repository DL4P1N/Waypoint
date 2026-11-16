using System.Buffers.Binary;

namespace Waypoint.Core.Files;

/// <summary>
/// The width and height a picture says it has, read from its header without decoding it. A small file can claim to
/// be enormous, so this is checked before a picture is handed to a program that would decode all of it.
/// </summary>
public static class ImageSize
{
    private const int HeaderBytes = 64 * 1024;

    /// <summary>Width and height, or null when the format isn't known or the header doesn't make sense.</summary>
    public static (int Width, int Height)? Read(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[(int)Math.Min(HeaderBytes, stream.Length)];
            stream.ReadExactly(buffer);
            return Read(buffer);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return null;
        }
    }

    public static (int Width, int Height)? Read(ReadOnlySpan<byte> data)
    {
        var size = Png(data) ?? Gif(data) ?? Bmp(data) ?? Jpeg(data) ?? WebP(data) ?? Tiff(data);
        return size is (> 0, > 0) ? size : null;
    }

    private static (int, int)? Png(ReadOnlySpan<byte> data) =>
        data.Length >= 24 && data[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) && data[12..16].SequenceEqual("IHDR"u8)
            ? (Clamp(BinaryPrimitives.ReadUInt32BigEndian(data[16..])), Clamp(BinaryPrimitives.ReadUInt32BigEndian(data[20..])))
            : null;

    private static (int, int)? Gif(ReadOnlySpan<byte> data) =>
        data.Length >= 10 && (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8))
            ? (BinaryPrimitives.ReadUInt16LittleEndian(data[6..]), BinaryPrimitives.ReadUInt16LittleEndian(data[8..]))
            : null;

    private static (int, int)? Bmp(ReadOnlySpan<byte> data) =>
        data.Length >= 26 && data[0] == 'B' && data[1] == 'M'
            ? (Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data[18..])), Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data[22..])))
            : null;

    /// <summary>Walks the markers to the first start-of-frame, which holds the size.</summary>
    private static (int, int)? Jpeg(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            return null;
        }

        var at = 2;
        while (at + 9 < data.Length)
        {
            if (data[at] != 0xFF)
            {
                return null;
            }

            var marker = data[at + 1];
            if (marker == 0xFF)
            {
                at++;
                continue;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 2)..]);
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                return (BinaryPrimitives.ReadUInt16BigEndian(data[(at + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(data[(at + 5)..]));
            }

            if (length < 2)
            {
                return null;
            }

            at += 2 + length;
        }

        return null;
    }

    private static (int, int)? WebP(ReadOnlySpan<byte> data)
    {
        if (data.Length < 30 || !data[..4].SequenceEqual("RIFF"u8) || !data[8..12].SequenceEqual("WEBP"u8))
        {
            return null;
        }

        var chunk = data[12..16];
        if (chunk.SequenceEqual("VP8X"u8))
        {
            return (1 + (data[24] | data[25] << 8 | data[26] << 16), 1 + (data[27] | data[28] << 8 | data[29] << 16));
        }

        if (chunk.SequenceEqual("VP8L"u8) && data[20] == 0x2F)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(data[21..]);
            return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
        }

        if (chunk.SequenceEqual("VP8 "u8))
        {
            return (BinaryPrimitives.ReadUInt16LittleEndian(data[26..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(data[28..]) & 0x3FFF);
        }

        return null;
    }

    /// <summary>The first image's ImageWidth and ImageLength tags.</summary>
    private static (int, int)? Tiff(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
        {
            return null;
        }

        var little = data[0] == 'I' && data[1] == 'I';
        if (!little && !(data[0] == 'M' && data[1] == 'M'))
        {
            return null;
        }

        ushort U16(ReadOnlySpan<byte> at) => little ? BinaryPrimitives.ReadUInt16LittleEndian(at) : BinaryPrimitives.ReadUInt16BigEndian(at);
        uint U32(ReadOnlySpan<byte> at) => little ? BinaryPrimitives.ReadUInt32LittleEndian(at) : BinaryPrimitives.ReadUInt32BigEndian(at);

        if (U16(data[2..]) != 42)
        {
            return null;
        }

        var directory = U32(data[4..]);
        if (directory > data.Length - 2)
        {
            return null;
        }

        var start = (int)directory;
        var count = U16(data[start..]);
        int? width = null, height = null;
        for (var i = 0; i < count; i++)
        {
            var entry = start + 2 + (i * 12);
            if (entry + 12 > data.Length)
            {
                break;
            }

            var tag = U16(data[entry..]);
            var type = U16(data[(entry + 2)..]);
            var value = type == 3 ? U16(data[(entry + 8)..]) : Clamp(U32(data[(entry + 8)..]));
            if (tag == 256)
            {
                width = value;
            }
            else if (tag == 257)
            {
                height = value;
            }
        }

        return width is { } w && height is { } h ? (w, h) : null;
    }

    private static int Clamp(uint value) => value > int.MaxValue ? int.MaxValue : (int)value;
}
