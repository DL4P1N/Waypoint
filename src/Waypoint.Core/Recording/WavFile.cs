using System.Buffers.Binary;
using System.Text;

namespace Waypoint.Core.Recording;

/// <summary>What was found when a recording's file was checked and fixed. Usable is false when no audio was saved at all.</summary>
public sealed record WavRepair(bool Usable, long DataBytes, TimeSpan Length);

/// <summary>
/// The plain WAV layout recordings are written in: a 44-byte header, then 16-bit samples. The header holds the
/// length, which is only right after a flush, so a file cut off by a crash or power loss is fixed by working the
/// length out from the file's size.
/// </summary>
public static class WavFile
{
    public const int HeaderBytes = 44;

    public static void WriteHeader(Stream stream, int sampleRate, short channels, short bitsPerSample, long dataBytes)
    {
        var header = new byte[HeaderBytes];
        var blockAlign = (short)(channels * bitsPerSample / 8);
        var data = (uint)Math.Clamp(dataBytes, 0, uint.MaxValue - 36);
        Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), data + 36);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(header, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(header, 12);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22), channels);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), sampleRate * blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32), blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34), bitsPerSample);
        Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), data);
        stream.Position = 0;
        stream.Write(header);
    }

    /// <summary>Only the two length fields, for a flush partway through.</summary>
    public static void WriteLengths(Stream stream, long dataBytes)
    {
        var data = (uint)Math.Clamp(dataBytes, 0, uint.MaxValue - 36);
        Span<byte> field = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(field, data + 36);
        stream.Position = 4;
        stream.Write(field);
        BinaryPrimitives.WriteUInt32LittleEndian(field, data);
        stream.Position = 40;
        stream.Write(field);
    }

    public static WavRepair Repair(string path)
    {
        if (!File.Exists(path))
        {
            return new WavRepair(false, 0, TimeSpan.Zero);
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        return Repair(stream);
    }

    /// <summary>
    /// Makes the header match what's really in the file. A half-written last sample is dropped. A header that was
    /// never written properly is written again in the recorder's own format, since only recordings are fixed here.
    /// </summary>
    public static WavRepair Repair(Stream stream, int sampleRate = RecordingWriter.SampleRate)
    {
        if (stream.Length <= HeaderBytes)
        {
            return new WavRepair(false, 0, TimeSpan.Zero);
        }

        var header = new byte[HeaderBytes];
        stream.Position = 0;
        stream.ReadExactly(header);
        var known = Encoding.ASCII.GetString(header, 0, 4) == "RIFF"
            && Encoding.ASCII.GetString(header, 8, 4) == "WAVE"
            && Encoding.ASCII.GetString(header, 36, 4) == "data";
        var channels = known ? BinaryPrimitives.ReadInt16LittleEndian(header.AsSpan(22)) : (short)1;
        var bits = known ? BinaryPrimitives.ReadInt16LittleEndian(header.AsSpan(34)) : (short)16;
        var rate = known ? BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(24)) : sampleRate;
        if (channels <= 0 || bits <= 0 || rate <= 0)
        {
            (channels, bits, rate, known) = (1, 16, sampleRate, false);
        }

        var block = channels * bits / 8;
        var data = (stream.Length - HeaderBytes) / block * block;
        if (stream.Length != HeaderBytes + data)
        {
            stream.SetLength(HeaderBytes + data);
        }

        if (known)
        {
            WriteLengths(stream, data);
        }
        else
        {
            WriteHeader(stream, rate, channels, bits, data);
        }

        stream.Flush();
        return new WavRepair(data > 0, data, TimeSpan.FromSeconds(data / (double)(rate * block)));
    }
}
