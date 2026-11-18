using System.Buffers.Binary;
using System.Text;

namespace Waypoint.Core.Transcription;

/// <summary>
/// Discord voice messages are Opus audio in an Ogg file. Windows has an Opus decoder but reads Opus only from a
/// WebM file, so this moves the same audio packets, unchanged, into a WebM that Media Foundation can read. Nothing
/// is decoded or encoded here, and nothing outside Windows is needed. The copy is written as it's read, so a long
/// file never has to fit in memory.
/// </summary>
public static class OggOpus
{
    /// <summary>Matroska block times are 16-bit milliseconds from their cluster's, so a cluster stays well short of 32 seconds.</summary>
    private const long ClusterMilliseconds = 20_000;

    private const int OpusRate = 48_000;

    /// <summary>True when the file starts with an Ogg page holding Opus, whatever its name says.</summary>
    public static bool IsOggOpus(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> start = stackalloc byte[36];
            return file.ReadAtLeast(start, start.Length, throwOnEndOfStream: false) == start.Length
                && start[..4].SequenceEqual("OggS"u8) && start[28..36].SequenceEqual("OpusHead"u8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Writes the Opus audio in <paramref name="oggPath"/> to <paramref name="webmPath"/>. Throws InvalidDataException when it isn't Ogg Opus.</summary>
    public static void ToWebm(string oggPath, string webmPath)
    {
        using var input = new FileStream(oggPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var output = new FileStream(webmPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        using var packets = ReadPackets(input).GetEnumerator();
        if (!packets.MoveNext() || packets.Current is not { Length: >= 19 } head || !head.AsSpan(0, 8).SequenceEqual("OpusHead"u8))
        {
            throw new InvalidDataException("This isn't Opus audio in an Ogg file");
        }

        int channels = head[9];
        var preSkip = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(10, 2));

        // The second packet is the tags, which a player doesn't need.
        packets.MoveNext();

        Write(output, Element(0x1A45DFA3, [
            .. Element(0x4286, UInt(1)), .. Element(0x42F7, UInt(1)), .. Element(0x42F2, UInt(4)), .. Element(0x42F3, UInt(8)),
            .. Element(0x4282, "webm"u8), .. Element(0x4287, UInt(4)), .. Element(0x4285, UInt(2))]));

        // The segment's size and the length aren't known until the end, so both are filled in then.
        WriteId(output, 0x18538067);
        var segmentSizeAt = output.Position;
        output.Write(Size(0));
        var segmentStart = output.Position;

        var info = Element(0x1549A966, [
            .. Element(0x2AD7B1, UInt(1_000_000)), .. Element(0x4489, Float(0)),
            .. Element(0x4D80, "Waypoint"u8), .. Element(0x5741, "Waypoint"u8)]);
        var durationAt = output.Position + Find(info, [0x44, 0x89]) + 2 + 8;
        Write(output, info);

        Write(output, Element(0x1654AE6B, Element(0xAE, [
            .. Element(0xD7, UInt(1)), .. Element(0x73C5, UInt(1)), .. Element(0x83, UInt(2)), .. Element(0x86, "A_OPUS"u8),
            .. Element(0x63A2, head), .. Element(0x56AA, UInt((ulong)(preSkip * 1_000_000_000L / OpusRate))), .. Element(0x56BB, UInt(80_000_000)),
            .. Element(0xE1, [.. Element(0xB5, Float(OpusRate)), .. Element(0x9F, UInt((ulong)channels))])])));

        long samples = 0;
        long? clusterAt = null;
        var cluster = new MemoryStream();
        while (packets.MoveNext())
        {
            var packet = packets.Current;
            if (packet.Length == 0)
            {
                continue;
            }

            var at = samples * 1000 / OpusRate;
            if (clusterAt is not { } start || at - start >= ClusterMilliseconds)
            {
                FlushCluster(output, cluster);
                clusterAt = start = at;
                Write(cluster, Element(0xE7, UInt((ulong)start)));
            }

            var block = new byte[4 + packet.Length];
            block[0] = 0x81;
            BinaryPrimitives.WriteInt16BigEndian(block.AsSpan(1, 2), (short)(at - start));
            block[3] = 0x80;
            packet.CopyTo(block, 4);
            Write(cluster, Element(0xA3, block));
            samples += Samples(packet);
        }

        FlushCluster(output, cluster);

        var end = output.Position;
        output.Position = durationAt;
        output.Write(Float(samples * 1000.0 / OpusRate));
        output.Position = segmentSizeAt;
        output.Write(Size(end - segmentStart));
        output.Position = end;
    }

    /// <summary>How many 48 kHz samples an Opus packet holds, from its first byte (RFC 6716, section 3.1).</summary>
    public static int Samples(ReadOnlySpan<byte> packet)
    {
        if (packet.IsEmpty)
        {
            return 0;
        }

        var config = packet[0] >> 3;
        var frame = config switch
        {
            < 12 => (config & 3) switch { 0 => 480, 1 => 960, 2 => 1920, _ => 2880 },
            < 16 => (config & 1) == 0 ? 480 : 960,
            _ => (config & 3) switch { 0 => 120, 1 => 240, 2 => 480, _ => 960 },
        };
        var frames = (packet[0] & 3) switch
        {
            0 => 1,
            1 or 2 => 2,
            _ => packet.Length > 1 ? packet[1] & 0x3F : 0,
        };
        return frame * frames;
    }

    /// <summary>
    /// The packets of the file's first stream, joined across pages. A page from another stream is skipped, and a
    /// file cut off partway ends at the last whole packet.
    /// </summary>
    private static IEnumerable<byte[]> ReadPackets(Stream input)
    {
        var header = new byte[27];
        var lacing = new byte[255];
        var packet = new MemoryStream();
        int? stream = null;
        while (input.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length)
        {
            if (!header.AsSpan(0, 4).SequenceEqual("OggS"u8))
            {
                throw new InvalidDataException("The Ogg file is damaged");
            }

            int count = header[26];
            if (input.ReadAtLeast(lacing.AsSpan(0, count), count, throwOnEndOfStream: false) < count)
            {
                yield break;
            }

            var serial = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(14, 4));
            stream ??= serial;
            var mine = serial == stream;

            // A page that doesn't carry on from the last means a page went missing, so its partial packet goes.
            if (mine && (header[5] & 1) == 0)
            {
                packet.SetLength(0);
            }

            for (var i = 0; i < count; i++)
            {
                var size = lacing[i];
                var piece = new byte[size];
                if (input.ReadAtLeast(piece, size, throwOnEndOfStream: false) < size)
                {
                    yield break;
                }

                if (!mine)
                {
                    continue;
                }

                packet.Write(piece);
                if (size < 255)
                {
                    yield return packet.ToArray();
                    packet.SetLength(0);
                }
            }
        }
    }

    private static void FlushCluster(Stream output, MemoryStream cluster)
    {
        if (cluster.Length > 0)
        {
            Write(output, Element(0x1F43B675, cluster.ToArray()));
            cluster.SetLength(0);
        }
    }

    /// <summary>An EBML element: its ID, its size as an 8-byte number, then its data.</summary>
    private static byte[] Element(uint id, ReadOnlySpan<byte> data)
    {
        var bytes = new MemoryStream();
        WriteId(bytes, id);
        bytes.Write(Size(data.Length));
        bytes.Write(data);
        return bytes.ToArray();
    }

    private static void WriteId(Stream output, uint id)
    {
        var started = false;
        for (var shift = 24; shift >= 0; shift -= 8)
        {
            var part = (byte)(id >> shift);
            if (started || part != 0)
            {
                output.WriteByte(part);
                started = true;
            }
        }
    }

    private static byte[] Size(long size)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, (ulong)size | 1UL << 56);
        return bytes;
    }

    private static byte[] UInt(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Float(double value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(bytes, value);
        return bytes;
    }

    private static void Write(Stream output, byte[] bytes) => output.Write(bytes);

    private static int Find(byte[] data, byte[] pattern) => data.AsSpan().IndexOf(pattern);
}
