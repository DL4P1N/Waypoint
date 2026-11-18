using System.Buffers.Binary;
using System.IO;
using NAudio.Wave;

namespace Waypoint.App.Tests;

/// <summary>
/// A Discord voice message is Opus in an Ogg file, which Windows can't open as it is. It's moved into WebM and read
/// with Windows' own Opus decoder, so nothing else is installed.
/// </summary>
public sealed class VoiceMessageAudioTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-voice-audio-{Guid.NewGuid():N}");

    public VoiceMessageAudioTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task A_discord_voice_message_becomes_the_wav_whisper_reads()
    {
        // Two seconds of Opus silence frames, 20 ms each, the kind Discord itself sends.
        var ogg = Path.Combine(_folder, "voice-message.ogg");
        File.WriteAllBytes(ogg, OggOpusFile(Enumerable.Repeat(new byte[] { 0xF8, 0xFF, 0xFE }, 100)));
        var wav = Path.Combine(_folder, "voice.part.wav");

        var length = await new MediaFoundationAudio().ConvertAsync(ogg, wav, CancellationToken.None);

        Assert.InRange(length.TotalSeconds, 1.8, 2.1);
        using (var reader = new WaveFileReader(wav))
        {
            Assert.Equal(MediaFoundationAudio.SampleRate, reader.WaveFormat.SampleRate);
            Assert.Equal(1, reader.WaveFormat.Channels);
        }

        // The WebM copy made along the way is gone.
        Assert.Equal([wav], Directory.EnumerateFiles(_folder).Where(path => path != ogg));
    }

    /// <summary>OpusHead alone on the first page, then the tags, then the packets, 50 to a page.</summary>
    private static byte[] OggOpusFile(IEnumerable<byte[]> packets)
    {
        var file = new MemoryStream();
        var sequence = 0;
        Page(file, sequence++, [[.. "OpusHead"u8, 1, 1, 0x38, 0x01, 0x80, 0xBB, 0, 0, 0, 0, 0]]);
        Page(file, sequence++, [[.. "OpusTags"u8, 0, 0, 0, 0, 0, 0, 0, 0]]);
        foreach (var page in packets.Chunk(50))
        {
            Page(file, sequence++, page);
        }

        return file.ToArray();
    }

    private static void Page(Stream file, int sequence, IReadOnlyList<byte[]> packets)
    {
        var header = new byte[27];
        "OggS"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(14), 4321);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(18), sequence);
        header[26] = (byte)packets.Count;
        file.Write(header);
        file.Write(packets.Select(packet => (byte)packet.Length).ToArray());
        foreach (var packet in packets)
        {
            file.Write(packet);
        }
    }
}
