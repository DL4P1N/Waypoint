using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Data;
using Waypoint.Core.Transcription;

namespace Waypoint.Core.Tests;

/// <summary>
/// Voice notes from the phone: Ogg Opus moved into WebM for Windows to read, and Whisper's part, with the converter
/// and whisper-cli faked. The capture channel's side is in <c>DiscordPhoneTests.VoiceNotes.cs</c>.
/// </summary>
public sealed class VoiceNoteTests : IDisposable
{
    private static readonly byte[] Silence = [0xF8, 0xFF, 0xFE];

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-voice-{Guid.NewGuid()}");
    private readonly WaypointDatabase _database;
    private readonly WhisperSetup _ready;
    private readonly FakeConverter _converter = new();
    private readonly FakeWhisper _whisper = new();

    public VoiceNoteTests()
    {
        Directory.CreateDirectory(_folder);
        _database = new WaypointDatabase(Path.Combine(_folder, "waypoint.db"));
        var bin = Path.Combine(_folder, "whisper", "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, WhisperSetup.CliName), "fake");
        var model = Path.Combine(_folder, "whisper", "model.bin");
        File.WriteAllText(model, "model");
        _ready = new WhisperSetup(model, bin);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    // Ogg Opus into WebM

    [Fact]
    public void Ogg_opus_packets_go_into_webm_unchanged_with_the_header_and_times()
    {
        var spanning = new byte[600];
        spanning[0] = 0xF8;
        Random.Shared.NextBytes(spanning.AsSpan(1));
        var ogg = Write("voice-message.ogg", OggFile.Build(channels: 1, segmentsPerPage: 2, Silence, spanning, Silence));

        Assert.True(OggOpus.IsOggOpus(ogg));
        var webm = Path.Combine(_folder, "voice.webm");
        OggOpus.ToWebm(ogg, webm);

        var root = Ebml.Read(File.ReadAllBytes(webm));
        Assert.Equal("webm", System.Text.Encoding.ASCII.GetString(Ebml.Find(root, 0x1A45DFA3, 0x4282)));
        var segment = Ebml.Read(Ebml.Find(root, 0x18538067));
        var track = Ebml.Read(Ebml.Find(segment, 0x1654AE6B, 0xAE));
        Assert.Equal("A_OPUS", System.Text.Encoding.ASCII.GetString(Ebml.Find(track, 0x86)));
        Assert.Equal(OggFile.Head(1), Ebml.Find(track, 0x63A2));
        Assert.Equal(1UL, BinaryPrimitives.ReadUInt64BigEndian(Ebml.Find(track, 0xE1, 0x9F)));
        Assert.Equal(60.0, BinaryPrimitives.ReadDoubleBigEndian(Ebml.Find(segment, 0x1549A966, 0x4489)));

        // The long packet spanned two pages and comes out whole, between the two short ones, 20 ms apart.
        var blocks = Ebml.Blocks(segment);
        Assert.Equal(new long[] { 0, 20, 40 }, blocks.Select(block => block.At));
        Assert.Equal(Silence, blocks[0].Packet);
        Assert.Equal(spanning, blocks[1].Packet);
        Assert.Equal(Silence, blocks[2].Packet);
    }

    [Fact]
    public void A_spanningvoice_note_is_split_into_clusters_and_another_stream_is_skipped()
    {
        var packets = Enumerable.Repeat(Silence, 1_100).ToArray();
        var ogg = Write("long.opus", OggFile.Build(channels: 2, segmentsPerPage: 50, packets, otherStreamPage: true));
        var webm = Path.Combine(_folder, "long.webm");

        OggOpus.ToWebm(ogg, webm);

        var segment = Ebml.Read(Ebml.Find(Ebml.Read(File.ReadAllBytes(webm)), 0x18538067));
        Assert.True(segment.Count(element => element.Id == 0x1F43B675) >= 2);
        var blocks = Ebml.Blocks(segment);
        Assert.Equal(1_100, blocks.Count);
        Assert.Equal(Enumerable.Range(0, 1_100).Select(index => index * 20L), blocks.Select(block => block.At));
        Assert.All(blocks, block => Assert.Equal(Silence, block.Packet));
    }

    [Fact]
    public void Ogg_that_is_not_opus_is_not_taken_for_it()
    {
        var vorbis = Write("song.ogg", [.. "OggS"u8, .. new byte[24], .. "\u0001vorbis"u8, .. new byte[30]]);

        Assert.False(OggOpus.IsOggOpus(vorbis));
        Assert.False(OggOpus.IsOggOpus(Path.Combine(_folder, "missing.ogg")));
        Assert.Throws<InvalidDataException>(() => OggOpus.ToWebm(vorbis, Path.Combine(_folder, "song.webm")));
    }

    [Theory]
    [InlineData(new byte[] { 0xF8, 0xFF, 0xFE }, 960)]
    [InlineData(new byte[] { 0x00 }, 480)]
    [InlineData(new byte[] { 0x10 }, 1920)]
    [InlineData(new byte[] { 0x78 }, 960)]
    [InlineData(new byte[] { 0xE3, 0x03 }, 360)]
    [InlineData(new byte[] { 0x0B }, 0)]
    public void Opus_packet_lengths_come_from_the_first_byte(byte[] packet, int samples) =>
        Assert.Equal(samples, OggOpus.Samples(packet));

    // Which files are voice notes

    [Fact]
    public void Audio_is_a_voice_note_and_video_or_documents_are_not()
    {
        Assert.True(VoiceNotes.IsVoiceNote("voice-message.ogg", "audio/ogg"));
        Assert.True(VoiceNotes.IsVoiceNote("Memo 3.M4A", null));
        Assert.True(VoiceNotes.IsVoiceNote("recording", "audio/mpeg"));
        Assert.False(VoiceNotes.IsVoiceNote("clip.mp4", "video/mp4"));
        Assert.False(VoiceNotes.IsVoiceNote("syllabus.pdf", "application/pdf"));
        Assert.True(TranscriptionQueue.IsAudioFile("voice-message.ogg"));
        Assert.True(TranscriptionQueue.IsAudioFile("talk.opus"));
    }

    [Fact]
    public void Whispers_marks_for_silence_are_dropped_and_lines_joined()
    {
        Assert.Equal("Buy stamps on Friday. And call Dr. Brightwater.", VoiceNotes.CleanText(" Buy stamps on Friday.\n And call Dr. Brightwater.\n[BLANK_AUDIO]\n"));
        Assert.Equal("", VoiceNotes.CleanText("[BLANK_AUDIO]\n (silence) "));
    }

    // Whisper for a voice note

    private WhisperVoiceTranscriber Transcriber(WhisperSetup? setup = null) => new(_database, setup ?? _ready, _converter, _whisper, _folder);

    private string WorkFolder => Path.Combine(_folder, "whisper", "work");

    [Fact]
    public void Speech_to_text_is_only_ready_once_the_user_agreed_and_the_files_are_there()
    {
        var missing = new WhisperSetup(_folder, new NoDownloads());

        Assert.False(Transcriber().IsReady);
        _database.SetPreference(TranscriptionQueue.ConsentPreference, "yes");
        Assert.True(Transcriber().IsReady);
        Assert.False(Transcriber(missing).IsReady);
    }

    [Fact]
    public async Task A_short_voice_note_becomes_its_words_and_the_working_files_go()
    {
        var audio = Write("voice-message.ogg", [1, 2, 3]);
        _converter.Length = TimeSpan.FromSeconds(12);
        _whisper.Transcript = " Remind me to return the library book.\n[BLANK_AUDIO]\n";

        var heard = await Transcriber().TranscribeAsync(audio, "Classes: BIO 140.", CancellationToken.None);

        Assert.Equal(new VoiceText(VoiceOutcome.Heard, "Remind me to return the library book.", TimeSpan.FromSeconds(12)), heard);
        var run = Assert.Single(_whisper.Runs);
        Assert.Contains("Classes: BIO 140.", run.Arguments);
        Assert.Empty(Directory.EnumerateFiles(WorkFolder));
        Assert.True(File.Exists(audio));
    }

    [Fact]
    public async Task Audio_over_five_minutes_is_not_transcribed()
    {
        _converter.Length = TimeSpan.FromMinutes(6);

        var heard = await Transcriber().TranscribeAsync(Write("lecture.m4a", [1]), "", CancellationToken.None);

        Assert.Equal(VoiceOutcome.TooLong, heard.Outcome);
        Assert.Empty(_whisper.Runs);
        Assert.Empty(Directory.EnumerateFiles(WorkFolder));
    }

    [Fact]
    public async Task Audio_windows_cannot_read_is_unreadable_but_a_busy_file_or_whisper_problem_is_tried_again()
    {
        var audio = Write("voice-message.ogg", [1]);

        _converter.Throws = new InvalidDataException("Unsupported format");
        Assert.Equal(VoiceOutcome.Unreadable, (await Transcriber().TranscribeAsync(audio, "", CancellationToken.None)).Outcome);

        _converter.Throws = new IOException("The file is in use.");
        Assert.Equal(VoiceOutcome.Failed, (await Transcriber().TranscribeAsync(audio, "", CancellationToken.None)).Outcome);

        _converter.Throws = null;
        _whisper.ExitCode = 3;
        Assert.Equal(VoiceOutcome.Failed, (await Transcriber().TranscribeAsync(audio, "", CancellationToken.None)).Outcome);

        _whisper.ExitCode = 0;
        _whisper.Transcript = "[BLANK_AUDIO]";
        Assert.Equal(VoiceOutcome.Silent, (await Transcriber().TranscribeAsync(audio, "", CancellationToken.None)).Outcome);
        Assert.Empty(Directory.EnumerateFiles(WorkFolder));
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private sealed class FakeConverter : IAudioConverter
    {
        public TimeSpan Length { get; set; } = TimeSpan.FromSeconds(8);

        public Exception? Throws { get; set; }

        public Task<TimeSpan> ConvertAsync(string inputPath, string wavPath, CancellationToken cancellationToken)
        {
            if (Throws is { } problem)
            {
                throw problem;
            }

            File.WriteAllText(wavPath, "RIFF pretend wav");
            return Task.FromResult(Length);
        }
    }

    /// <summary>Acts like whisper-cli: writes the .txt and .json named by -of.</summary>
    private sealed class FakeWhisper : IProcessRunner
    {
        public List<ProcessSpec> Runs { get; } = [];

        public string Transcript { get; set; } = " Hello.\n";

        public int ExitCode { get; set; }

        public Task<ProcessResult> RunAsync(ProcessSpec spec, Action<string> onLine, CancellationToken cancellationToken)
        {
            Runs.Add(spec);
            if (ExitCode == 0)
            {
                var output = spec.Arguments[spec.Arguments.ToList().IndexOf("-of") + 1];
                File.WriteAllText(output + ".txt", Transcript);
                File.WriteAllText(output + ".json", "{}");
            }

            return Task.FromResult(new ProcessResult(ExitCode, ExitCode == 0 ? "" : "failed to read audio"));
        }
    }

    private sealed class NoDownloads : IFileDownloader
    {
        public Task DownloadAsync(Uri url, string partPath, IProgress<long>? progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Nothing downloads in tests.");
    }

    /// <summary>Writes a small Ogg Opus file: the head, the tags, then the packets, a few lacing values to a page.</summary>
    internal static class OggFile
    {
        public static byte[] Head(int channels) => [.. "OpusHead"u8, 1, (byte)channels, 0x38, 0x01, 0x80, 0xBB, 0, 0, 0, 0, 0];

        public static byte[] Build(int channels, int segmentsPerPage, params byte[][] packets) => Build(channels, segmentsPerPage, packets, false);

        public static byte[] Build(int channels, int segmentsPerPage, byte[][] packets, bool otherStreamPage)
        {
            // Every packet as lacing values: 255s while it goes on, then the rest, which ends it.
            var segments = new List<(byte[] Data, bool EndsPacket)>();
            foreach (var packet in packets.Prepend([.. "OpusTags"u8, 0, 0, 0, 0, 0, 0, 0, 0]).Prepend(Head(channels)))
            {
                var at = 0;
                do
                {
                    var size = Math.Min(255, packet.Length - at);
                    segments.Add((packet[at..(at + size)], size < 255));
                    at += size;
                }
                while (at < packet.Length || segments[^1].Data.Length == 255);
            }

            // The head is alone on the first page, as the format says.
            var file = new MemoryStream();
            Page(file, 1234, 0, false, [segments[0].Data]);
            segments.RemoveAt(0);
            var continued = false;
            var sequence = 1;
            for (var start = 0; start < segments.Count; start += segmentsPerPage)
            {
                var page = segments.Skip(start).Take(segmentsPerPage).ToList();
                Page(file, 1234, sequence++, continued, page.Select(segment => segment.Data).ToList());
                continued = !page[^1].EndsPacket;
                if (otherStreamPage && start == segmentsPerPage)
                {
                    Page(file, 999, 0, false, [[1, 2, 3, 4]]);
                }
            }

            return file.ToArray();
        }

        private static void Page(Stream file, int serial, int sequence, bool continued, IReadOnlyList<byte[]> segments)
        {
            var header = new byte[27];
            "OggS"u8.CopyTo(header);
            header[5] = (byte)(continued ? 1 : 0);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(14), serial);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(18), sequence);
            header[26] = (byte)segments.Count;
            file.Write(header);
            file.Write(segments.Select(segment => (byte)segment.Length).ToArray());
            foreach (var segment in segments)
            {
                file.Write(segment);
            }
        }
    }

    /// <summary>Just enough of an EBML reader to check what was written.</summary>
    private static class Ebml
    {
        public static List<(uint Id, byte[] Data)> Read(byte[] data)
        {
            var elements = new List<(uint, byte[])>();
            var at = 0;
            while (at < data.Length)
            {
                var idLength = Length(data[at]);
                uint id = 0;
                for (var i = 0; i < idLength; i++)
                {
                    id = id << 8 | data[at + i];
                }

                at += idLength;
                var sizeLength = Length(data[at]);
                var size = (long)(data[at] & (0xFF >> sizeLength));
                for (var i = 1; i < sizeLength; i++)
                {
                    size = size << 8 | data[at + i];
                }

                at += sizeLength;
                elements.Add((id, data[at..(at + (int)size)]));
                at += (int)size;
            }

            return elements;
        }

        public static byte[] Find(List<(uint Id, byte[] Data)> elements, params uint[] path)
        {
            var found = elements.Single(element => element.Id == path[0]).Data;
            return path.Length == 1 ? found : Find(Read(found), path[1..]);
        }

        /// <summary>Every block's time in milliseconds, its cluster's plus its own, and its packet.</summary>
        public static List<(long At, byte[] Packet)> Blocks(List<(uint Id, byte[] Data)> segment) =>
            segment.Where(element => element.Id == 0x1F43B675).SelectMany(cluster =>
            {
                var parts = Read(cluster.Data);
                var start = (long)BinaryPrimitives.ReadUInt64BigEndian(parts.Single(part => part.Id == 0xE7).Data);
                return parts.Where(part => part.Id == 0xA3)
                    .Select(block => (start + BinaryPrimitives.ReadInt16BigEndian(block.Data.AsSpan(1, 2)), block.Data[4..]));
            }).ToList();

        private static int Length(byte first) => System.Numerics.BitOperations.LeadingZeroCount((uint)first) - 23;
    }
}
