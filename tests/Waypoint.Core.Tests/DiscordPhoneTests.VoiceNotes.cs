using Waypoint.Core.Background;
using Waypoint.Core.Capture;
using Waypoint.Core.Discord;
using Waypoint.Core.Transcription;

namespace Waypoint.Core.Tests;

/// <summary>Voice notes sent in the capture channel, with speech to text faked so Whisper never runs.</summary>
public sealed partial class DiscordPhoneTests
{
    private static readonly DateTimeOffset SentFromPhone = new(2026, 11, 9, 9, 40, 0, TimeSpan.FromHours(-7));

    private string VoiceFolder => Path.Combine(_folder, VoiceNotes.FolderName);

    private sealed class FakeVoice : IVoiceTranscriber
    {
        public bool IsReady { get; set; } = true;

        public VoiceText Next { get; set; } = new(VoiceOutcome.Heard, "Buy stamps on Friday", TimeSpan.FromSeconds(6));

        public List<string> Read { get; } = [];

        public Task<VoiceText> TranscribeAsync(string audioPath, string prompt, CancellationToken cancellationToken)
        {
            lock (Read)
            {
                Read.Add(audioPath);
            }

            Assert.True(File.Exists(audioPath));
            return Task.FromResult(Next);
        }
    }

    private sealed class OneTaskSorter : ICaptureSorter
    {
        public Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<CaptureResult?>(new CaptureResult([new SortedItem(ItemKind.Task, "Buy stamps", null, null)]));
    }

    private WaypointWorker VoiceWorker(IVoiceTranscriber? voice, ICaptureSorter? sorter = null)
    {
        var settings = new WaypointSettings(Discord: new DiscordSettings("token", "owner", "capture", "alerts", "budget", null, null));
        var worker = new WaypointWorker(_database, settings, _folder, new Host(), exception => throw exception, new WorkerParts(
            _ => new NoTokens(),
            Clock: () => _now,
            Http: () => new HttpClient(_discord),
            CaptureSorter: sorter ?? new NullSorter(),
            ConnectGateway: false,
            Voice: voice))
        {
            RetryWait = _ => Task.CompletedTask,
        };
        worker.StartDiscord();
        return worker;
    }

    private static DiscordMessage VoiceMessage(string id, string name = "voice-message.ogg", string type = "audio/ogg", double? seconds = 6.5) =>
        new(id, "", SentFromPhone, [new DiscordAttachment($"a{id}", name, 2048, $"https://cdn.discordapp.com/attachments/1/2/{name}", type, seconds)]);

    private List<string> RepliesTo(string id) =>
        _discord.Of(HttpMethod.Post).Where(post => IsReplyTo(post, id)).Select(post => post.Body).ToList();

    private static bool IsReplyTo((HttpMethod Method, string Url, string Body) request, string id) =>
        request.Method == HttpMethod.Post && request.Url.Contains("/channels/capture/messages") && request.Body.Contains($"\"message_id\":\"{id}\"");

    /// <summary>Finishes once that many replies to the message have gone out.</summary>
    private Task Replied(string id, int count) => _discord.Made(request => IsReplyTo(request, id), count);

    /// <summary>The download and reading run in the background, so this waits for them.</summary>
    private static async Task Eventually(Func<bool> condition) => Assert.True(await Waits.Until(condition));

    /// <summary>
    /// What's kept for good. Saying a voice note is waiting rewrites its details through a short-lived .part file,
    /// which can still be there for a moment after the reply is seen.
    /// </summary>
    private List<string> KeptFiles() =>
        Directory.EnumerateFiles(VoiceFolder).Where(path => !path.EndsWith(".part", StringComparison.Ordinal)).ToList();

    private bool NothingKept => !Directory.Exists(VoiceFolder) || !Directory.EnumerateFileSystemEntries(VoiceFolder).Any();

    [Fact]
    public async Task A_voice_note_is_captured_as_its_words_at_the_time_it_was_sent_and_its_audio_is_let_go()
    {
        var voice = new FakeVoice();
        var worker = VoiceWorker(voice);

        worker.OnDiscordMessage(VoiceMessage("v1"));
        await Eventually(() => RepliesTo("v1").Count == 1);

        var capture = Assert.Single(_database.GetPendingCaptures());
        Assert.Equal("Buy stamps on Friday", capture.Text);
        Assert.Equal(SentFromPhone, capture.CreatedAt);
        Assert.Equal(CaptureOrigin.Discord, capture.Origin);

        // Sorting isn't working in this test, so the reply says it's kept, with what was heard.
        var reply = Assert.Single(RepliesTo("v1"));
        Assert.Contains("Sorting isn", reply);
        Assert.Contains("Heard", reply);
        Assert.Contains("Buy stamps on Friday", reply);
        Assert.StartsWith(VoiceFolder, Assert.Single(voice.Read));
        Assert.True(NothingKept);
    }

    [Fact]
    public async Task The_answer_to_a_voice_note_shows_what_was_heard()
    {
        var worker = VoiceWorker(new FakeVoice(), new OneTaskSorter());
        worker.Captures.Sorted += outcome => worker.ReplyInDiscord(outcome, "Added: Buy stamps");

        worker.OnDiscordMessage(VoiceMessage("v2"));
        await Eventually(() => RepliesTo("v2").Count == 1);

        var reply = Assert.Single(RepliesTo("v2"));
        Assert.Contains("Added: Buy stamps", reply);
        Assert.Contains("\"name\":\"Heard\"", reply);
        Assert.Contains("Buy stamps on Friday", reply);
        Assert.Contains(_database.GetOpenItems(), item => item.Title == "Buy stamps");
    }

    [Fact]
    public async Task Before_speech_to_text_is_set_up_a_voice_note_is_kept_said_once_and_read_once_it_is()
    {
        var voice = new FakeVoice { IsReady = false };
        var worker = VoiceWorker(voice);

        var told = Replied("v3", 1);
        worker.OnDiscordMessage(VoiceMessage("v3"));
        await told;

        // The reply goes out while the note's first read still has its turn, and the note is marked as told just
        // after. Trying again waits for that turn, so once it's back the first read has finished, the files are as
        // it left them, and, still not set up, it says nothing more.
        await worker.ReadHeldVoiceNotesAsync();
        var reply = Assert.Single(RepliesTo("v3"));
        Assert.Contains("Voice note kept", reply);
        Assert.Contains("set up on the PC", reply);
        Assert.DoesNotContain("send", reply, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_database.GetPendingCaptures());
        Assert.Empty(voice.Read);

        // Kept in the data folder, not the temp folder, with which message it answers.
        Assert.Equal(2, KeptFiles().Count);

        // Its words are captured during the read, and the reply showing them is sent in the background after.
        var heard = Replied("v3", 2);
        voice.IsReady = true;
        await worker.ReadHeldVoiceNotesAsync();
        await heard;

        var capture = Assert.Single(_database.GetPendingCaptures());
        Assert.Equal("Buy stamps on Friday", capture.Text);
        Assert.Equal(SentFromPhone, capture.CreatedAt);
        Assert.Contains("Buy stamps on Friday", RepliesTo("v3")[1]);
        Assert.True(NothingKept);
    }

    [Fact]
    public async Task Kept_voice_notes_are_read_when_Discord_connects_again()
    {
        var voice = new FakeVoice { IsReady = false };
        var worker = VoiceWorker(voice);
        worker.OnDiscordMessage(VoiceMessage("v4"));
        await Eventually(() => RepliesTo("v4").Count == 1);

        voice.IsReady = true;
        worker.StopDiscord();
        worker.StartDiscord();

        await Eventually(() => _database.GetPendingCaptures().Count == 1);
        await Eventually(() => NothingKept);
    }

    [Fact]
    public async Task On_the_server_a_voice_note_is_kept_and_the_reply_says_so_plainly_once()
    {
        var worker = VoiceWorker(voice: null);

        worker.OnDiscordMessage(VoiceMessage("v5"));
        await Eventually(() => RepliesTo("v5").Count == 1);
        await worker.ReadHeldVoiceNotesAsync();

        var reply = Assert.Single(RepliesTo("v5"));
        Assert.Contains("Voice note kept", reply);
        Assert.Contains("server", reply);
        Assert.Empty(_database.GetPendingCaptures());
        Assert.False(NothingKept);
    }

    [Fact]
    public async Task A_voice_note_that_failed_is_kept_and_read_on_the_next_try()
    {
        var voice = new FakeVoice { Next = new VoiceText(VoiceOutcome.Failed) };
        var worker = VoiceWorker(voice);

        worker.OnDiscordMessage(VoiceMessage("v6"));
        await Eventually(() => RepliesTo("v6").Count == 1);
        Assert.Contains("tried again", RepliesTo("v6")[0]);
        Assert.False(NothingKept);

        // A second failure says nothing more.
        await worker.ReadHeldVoiceNotesAsync();
        Assert.Single(RepliesTo("v6"));

        voice.Next = new VoiceText(VoiceOutcome.Heard, "Call the landlord about the heater", TimeSpan.FromSeconds(4));
        await worker.ReadHeldVoiceNotesAsync();

        Assert.Equal("Call the landlord about the heater", Assert.Single(_database.GetPendingCaptures()).Text);
        Assert.True(NothingKept);
    }

    [Fact]
    public async Task Audio_over_five_minutes_is_a_recording_and_is_not_downloaded_when_discord_gives_its_length()
    {
        var voice = new FakeVoice();
        var worker = VoiceWorker(voice);

        await worker.Discord!.HandleEventAsync("MESSAGE_CREATE", Json("""
            {"id":"v7","channel_id":"capture","content":"","author":{"id":"owner"},"attachments":[{"id":"a7","filename":"voice-message.ogg","size":4096,"url":"https://cdn.discordapp.com/attachments/1/2/voice-message.ogg","content_type":"audio/ogg","duration_secs":412.5}]}
            """));
        await Eventually(() => RepliesTo("v7").Count == 1);

        Assert.Contains("Recording not read", RepliesTo("v7")[0]);
        Assert.Contains("over 5 minutes", RepliesTo("v7")[0]);
        Assert.DoesNotContain(_discord.Of(HttpMethod.Get), request => request.Url.Contains("voice-message.ogg"));
        Assert.Empty(voice.Read);
        Assert.True(NothingKept);
    }

    [Fact]
    public async Task A_long_audio_file_found_long_when_read_is_a_recording_and_is_let_go()
    {
        var voice = new FakeVoice { Next = new VoiceText(VoiceOutcome.TooLong, Length: TimeSpan.FromMinutes(40)) };
        var worker = VoiceWorker(voice);

        worker.OnDiscordMessage(VoiceMessage("v8", "seminar.mp3", "audio/mpeg", seconds: null));
        await Eventually(() => RepliesTo("v8").Count == 1);

        Assert.Contains("over 5 minutes", RepliesTo("v8")[0]);
        Assert.Empty(_database.GetPendingCaptures());
        Assert.True(NothingKept);
    }

    [Fact]
    public async Task A_video_sent_in_capture_still_gets_the_recording_reply()
    {
        var voice = new FakeVoice();
        var worker = VoiceWorker(voice);

        worker.OnDiscordMessage(VoiceMessage("v9", "clip.mp4", "video/mp4", seconds: null));
        await Eventually(() => RepliesTo("v9").Count == 1);

        Assert.Contains("be sent from Discord yet", RepliesTo("v9")[0]);
        Assert.Empty(voice.Read);
    }

    [Fact]
    public async Task A_voice_note_with_a_strange_name_stays_inside_the_voice_notes_folder()
    {
        var voice = new FakeVoice { IsReady = false };
        var worker = VoiceWorker(voice);

        worker.OnDiscordMessage(new DiscordMessage("v10", "", SentFromPhone, [new DiscordAttachment("../a10", "..\\..\\memo", 2048, "https://cdn.discordapp.com/attachments/1/2/memo", "audio/ogg")]));
        await Eventually(() => RepliesTo("v10").Count == 1);

        var kept = KeptFiles();
        Assert.Equal(2, kept.Count);
        Assert.All(kept, path => Assert.Equal(VoiceFolder, Path.GetDirectoryName(path)));
        Assert.Contains(kept, path => path.EndsWith(".audio", StringComparison.Ordinal));
    }
}
