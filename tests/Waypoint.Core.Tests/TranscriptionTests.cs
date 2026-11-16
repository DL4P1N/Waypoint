using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Notes;
using Waypoint.Core.Transcription;

namespace Waypoint.Core.Tests;

/// <summary>
/// Recordings turned into notes. Whisper, the audio converter, the downloader, and Haiku are all faked, so nothing
/// runs or downloads here. The one real run is <see cref="WhisperIntegrationTests"/>, skipped unless asked for.
/// </summary>
public sealed class TranscriptionTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 3, 20, 0, 0);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-transcribe-{Guid.NewGuid()}");
    private readonly string _dbPath;
    private readonly WaypointDatabase _database;
    private readonly FakeWriter _writer = new();
    private readonly FakeConverter _converter = new();
    private readonly FakeWhisper _whisper = new();
    private readonly WhisperSetup _ready;

    public TranscriptionTests()
    {
        Directory.CreateDirectory(_folder);
        _dbPath = Path.Combine(_folder, "waypoint.db");
        _database = new WaypointDatabase(_dbPath);

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

    private NoteMaker Maker() => new(_database, _writer, null);

    private TranscriptionQueue Queue(WhisperSetup? setup = null, IProcessRunner? runner = null, Func<long, CancellationToken, Task<NoteWriteOutcome>>? writeNotes = null) =>
        new(_database, Maker(), setup ?? _ready, _converter, runner ?? _whisper, _folder, () => Now, writeNotes);

    private string Recording(string name = "lecture.m4a")
    {
        var path = Path.Combine(_folder, "recordings", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "pretend audio");
        return path;
    }

    private string WorkFolder => Path.Combine(_folder, "whisper", "work");

    // Importing

    [Fact]
    public void Importing_makes_the_note_straight_away_and_a_saved_job_filed_under_the_class_the_name_gives()
    {
        _database.ScheduleCourses = ["CS 101"];

        var job = Queue().Import(Recording("CS101 lecture 9.m4a"), keepAudio: false, null, folderPicked: false);

        var note = _database.GetNote(job.NoteId)!;
        Assert.Equal("CS101 lecture 9", note.Title);
        Assert.Equal("CS 101", _database.GetNoteCourse(job.NoteId));
        Assert.Equal(TranscriptionStatus.Queued, _database.GetTranscriptionJob(job.Id)!.Status);
        Assert.Empty(_converter.Calls);
    }

    [Fact]
    public void Audio_and_video_files_are_told_apart_from_other_files()
    {
        Assert.True(TranscriptionQueue.IsAudioFile(@"C:\a\talk.MP3"));
        Assert.True(TranscriptionQueue.IsAudioFile("lecture.mp4"));
        Assert.True(TranscriptionQueue.IsAudioFile("memo.wma"));
        Assert.False(TranscriptionQueue.IsAudioFile("slides.pptx"));
        Assert.False(TranscriptionQueue.IsAudioFile("notes.pdf"));
    }

    // The whole path

    [Fact]
    public async Task A_recording_becomes_a_transcript_source_kept_unchanged_and_notes_written_from_it()
    {
        _whisper.Transcript = " Today we cover recursion.\n The midterm is Friday.\n";
        _whisper.Json = """{"transcription":[{"timestamps":{"from":"00:00:00,000","to":"00:00:02,000"},"text":" Today we cover recursion."}]}""";
        _converter.Length = TimeSpan.FromSeconds(2);
        var queue = Queue();
        var original = Recording();
        var job = queue.Import(original, keepAudio: false, null, folderPicked: false);

        await queue.RunPendingAsync();

        var source = Assert.Single(_database.GetNoteSources(job.NoteId));
        Assert.Equal(NoteSourceKind.Transcript, source.Kind);
        Assert.Equal("Today we cover recursion.\n The midterm is Friday.", source.Text);
        Assert.Equal(_whisper.Json, _database.GetSourceTimestamps(source.Id));
        Assert.Null(source.FilePath);
        Assert.Equal("# Notes from the transcript", _database.GetNote(job.NoteId)!.Body);
        Assert.Equal(source.Text, Assert.Single(Assert.Single(_writer.Calls)).Text);

        var done = _database.GetTranscriptionJob(job.Id)!;
        Assert.Equal(TranscriptionStatus.Done, done.Status);
        Assert.Null(done.Error);
        Assert.Null(done.WavPath);
        Assert.Empty(Directory.GetFiles(WorkFolder));
        Assert.False(Directory.Exists(Path.Combine(_folder, "audio")));
        Assert.Equal("pretend audio", File.ReadAllText(original));
    }

    [Fact]
    public async Task A_transcript_that_stops_well_before_the_audio_ends_keeps_the_audio_with_the_note_and_says_so_once()
    {
        _whisper.Json = """{"transcription":[{"timestamps":{"from":"00:00:00,000","to":"00:10:00,000"},"text":" Hello class."}]}""";
        var queue = Queue();
        var outcomes = new List<string>();
        queue.Changed += change =>
        {
            if (change.Outcome is { } outcome)
            {
                outcomes.Add(outcome);
            }
        };
        var original = Recording();
        var job = queue.Import(original, keepAudio: false, null, folderPicked: false);

        await queue.RunPendingAsync();

        var done = _database.GetTranscriptionJob(job.Id)!;
        Assert.Equal(TranscriptionStatus.Done, done.Status);
        Assert.StartsWith(Path.Combine(_folder, "audio"), done.KeptAudioPath);
        Assert.Equal("pretend audio", File.ReadAllText(done.KeptAudioPath!));
        Assert.Equal(done.KeptAudioPath, Assert.Single(_database.GetNoteSources(job.NoteId)).FilePath);
        Assert.Single(outcomes, outcome => outcome.Contains("audio is kept", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Hello class.", 3600, 3500, true)]
    [InlineData("Hello class.", 3600, 2700, true)]
    [InlineData("Hello class.", 3600, 1200, false)]
    [InlineData("   ", 3600, 3600, false)]
    [InlineData("Hello class.", null, 60, true)]
    public void A_transcript_is_checked_by_having_words_and_reaching_most_of_the_audio(string text, int? audioSeconds, int lastSecond, bool passes)
    {
        var json = $$"""{"transcription":[{"timestamps":{"from":"00:00:00,000","to":"00:00:00,000"},"offsets":{"from":0,"to":{{lastSecond * 1000}}},"text":" {{text}}"}]}""";

        Assert.Equal(passes, TranscriptCheck.Passes(text, json, audioSeconds));
    }

    [Fact]
    public async Task Whisper_is_run_with_text_and_json_out_progress_on_and_the_class_hints()
    {
        _database.ScheduleCourses = ["CS 101", "BIO 210"];
        var queue = Queue();
        var job = queue.Import(Recording("CS 101 week 3.mp3"), keepAudio: false, null, folderPicked: false);

        await queue.RunPendingAsync();

        var spec = Assert.Single(_whisper.Runs);
        Assert.Equal(_ready.CliPath, spec.FileName);
        var args = spec.Arguments;
        Assert.Equal(_ready.ModelPath, args[args.ToList().IndexOf("-m") + 1]);
        Assert.Contains("-otxt", args);
        Assert.Contains("-oj", args);
        Assert.Contains("-pp", args);
        Assert.Equal("en", args[args.ToList().IndexOf("-l") + 1]);
        var prompt = args[args.ToList().IndexOf("--prompt") + 1];
        Assert.StartsWith("Classes: CS 101, BIO 210.", prompt);
        Assert.Equal(TranscriptionStatus.Done, _database.GetTranscriptionJob(job.Id)!.Status);
    }

    [Fact]
    public async Task A_kept_wav_recording_is_stored_compressed_and_linked_from_the_transcript()
    {
        _converter.Compresses = true;
        var queue = Queue();
        var original = Recording("Lecture 5.wav");
        var job = queue.Import(original, keepAudio: true, null, folderPicked: false);

        await queue.RunPendingAsync();

        var kept = _database.GetTranscriptionJob(job.Id)!.KeptAudioPath!;
        Assert.StartsWith(Path.Combine(_folder, "audio"), kept);
        Assert.Equal(".mp3", Path.GetExtension(kept));
        Assert.Equal("compressed pretend audio", File.ReadAllText(kept));
        Assert.Equal(kept, Assert.Single(_database.GetNoteSources(job.NoteId)).FilePath);
        Assert.Single(Directory.GetFiles(Path.Combine(_folder, "audio")));
    }

    [Fact]
    public async Task A_wav_that_cant_be_compressed_is_kept_as_it_is()
    {
        var queue = Queue();
        var job = queue.Import(Recording("Lecture 5.wav"), keepAudio: true, null, folderPicked: false);

        await queue.RunPendingAsync();

        var kept = _database.GetTranscriptionJob(job.Id)!.KeptAudioPath!;
        Assert.Equal(".wav", Path.GetExtension(kept));
        Assert.Equal("pretend audio", File.ReadAllText(kept));
        Assert.Single(Directory.GetFiles(Path.Combine(_folder, "audio")));
    }

    [Fact]
    public async Task Keep_the_audio_copies_it_into_the_data_folder_and_links_it_from_the_transcript()
    {
        var queue = Queue();
        var original = Recording("talk.mp3");
        var job = queue.Import(original, keepAudio: true, null, folderPicked: false);

        await queue.RunPendingAsync();

        var kept = _database.GetTranscriptionJob(job.Id)!.KeptAudioPath!;
        Assert.StartsWith(Path.Combine(_folder, "audio"), kept);
        Assert.Equal("pretend audio", File.ReadAllText(kept));
        Assert.True(File.Exists(original));
        Assert.Equal(kept, Assert.Single(_database.GetNoteSources(job.NoteId)).FilePath);
        Assert.Empty(Directory.GetFiles(WorkFolder));
    }

    // Progress

    [Theory]
    [InlineData("whisper_print_progress_callback: progress =  40%", 40)]
    [InlineData("whisper_print_progress_callback: progress = 100%", 100)]
    [InlineData("progress =   5%", 5)]
    public void Progress_lines_are_read(string line, int percent) => Assert.Equal(percent, WhisperProgress.Parse(line));

    [Theory]
    [InlineData("[00:00:01.000 --> 00:00:02.000]   It made progress.")]
    [InlineData("whisper_model_load: loading model")]
    [InlineData("")]
    public void Other_lines_have_no_progress(string line) => Assert.Null(WhisperProgress.Parse(line));

    [Fact]
    public async Task Progress_is_saved_on_the_job_while_whisper_runs()
    {
        var seen = new List<int>();
        var queue = Queue();
        var job = queue.Import(Recording(), keepAudio: false, null, folderPicked: false);
        _whisper.WhileRunning = () => seen.Add(_database.GetTranscriptionJob(job.Id)!.Progress);

        await queue.RunPendingAsync();

        Assert.Equal([10, 55, 100], seen);
    }

    [Fact]
    public void Time_left_uses_the_measured_speed_at_first_and_the_pace_so_far_after()
    {
        // 75 minutes at 0.17 is 12.75 minutes.
        var first = WhisperProgress.Remaining(75 * 60, 0, TimeSpan.Zero)!.Value;
        Assert.Equal(12.75, first.TotalMinutes, 2);

        // 25% done in 4 minutes leaves about 12.
        Assert.Equal(12, WhisperProgress.Remaining(75 * 60, 25, TimeSpan.FromMinutes(4))!.Value.TotalMinutes, 2);
        Assert.Null(WhisperProgress.Remaining(null, 0, TimeSpan.Zero));

        Assert.Equal("about 13 minutes left", WhisperProgress.Words(first));
        Assert.Equal("about 1 minute left", WhisperProgress.Words(TimeSpan.FromSeconds(70)));
        Assert.Equal("less than a minute left", WhisperProgress.Words(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void The_note_says_how_long_is_left_in_plain_words()
    {
        var queue = Queue();
        var job = queue.Import(Recording(), keepAudio: false, null, folderPicked: false) with
        {
            Status = TranscriptionStatus.Transcribing,
            Progress = 50,
            DurationSeconds = 3600,
            StartedAt = Now.AddMinutes(-5),
        };

        var text = queue.Describe(job, Now);

        Assert.StartsWith("Transcribing, about 5 minutes left.", text);
        Assert.DoesNotContain("\u2014", text);
        Assert.Equal("Transcribing 50%", TranscriptionQueue.ListLabel(job));
    }

    // Hints

    [Fact]
    public void The_prompt_lists_classes_then_assignments_with_the_notes_own_class_first()
    {
        var prompt = WhisperPrompt.Build(
            "BIO 210",
            ["CS 101", "BIO 210"],
            [("Homework 4", "CS 101"), ("Lab report \"cells\"", "BIO 210"), ("Homework 4", "CS 101")]);

        Assert.Equal("Classes: BIO 210, CS 101. Assignments: Lab report cells, Homework 4.", prompt);
    }

    [Fact]
    public void The_prompt_is_capped_without_cutting_a_name_in_half()
    {
        var titles = Enumerable.Range(1, 200).Select(n => ($"Problem set number {n}", (string?)"CS 101"));

        var prompt = WhisperPrompt.Build("CS 101", ["CS 101"], titles);

        Assert.True(prompt.Length <= WhisperPrompt.MaxChars, prompt.Length.ToString());
        Assert.EndsWith(".", prompt);
        Assert.Matches(@"Problem set number \d+\.$", prompt);
        Assert.DoesNotContain('\n', prompt);
    }

    [Fact]
    public void An_empty_prompt_leaves_the_prompt_argument_out()
    {
        Assert.Equal("", WhisperPrompt.Build(null, [], []));
        Assert.DoesNotContain("--prompt", TranscriptionQueue.WhisperArguments("m", "w", "o", "", 16));
    }

    [Fact]
    public void The_prompt_uses_open_assignments_that_have_a_class()
    {
        _database.ScheduleCourses = ["CS 101"];
        _database.AddByHand(new HandAddedItem(ItemKind.Deadline, "Homework 4", new DateOnly(2026, 11, 5)), Now);
        _database.AddByHand(new HandAddedItem(ItemKind.Task, "Buy milk", null, null), Now);
        var queue = Queue();
        var job = queue.Import(Recording("CS 101.m4a"), keepAudio: false, null, folderPicked: false);

        var prompt = queue.PromptFor(job.NoteId);

        Assert.Contains("CS 101", prompt);
        Assert.Contains("Homework 4", prompt);
        Assert.DoesNotContain("milk", prompt);
    }

    // Resuming after a restart

    [Fact]
    public async Task A_job_cut_off_by_closing_carries_on_next_time_with_the_audio_already_converted()
    {
        var original = Recording();
        using var closing = new CancellationTokenSource();
        _whisper.WhileRunning = () => closing.Cancel();
        _whisper.HonorCancel = true;
        var first = Queue();
        var job = first.Import(original, keepAudio: false, null, folderPicked: false);

        await first.RunPendingAsync(closing.Token);

        var stopped = _database.GetTranscriptionJob(job.Id)!;
        Assert.Equal(TranscriptionStatus.Transcribing, stopped.Status);
        Assert.True(File.Exists(stopped.WavPath));

        // A fresh start, as after a restart.
        _whisper.WhileRunning = null;
        SqliteConnection.ClearAllPools();
        var again = new TranscriptionQueue(new WaypointDatabase(_dbPath), Maker(), _ready, _converter, _whisper, _folder, () => Now);
        await again.RunPendingAsync();

        Assert.Equal(TranscriptionStatus.Done, _database.GetTranscriptionJob(job.Id)!.Status);
        Assert.Single(_converter.Calls);
        Assert.Equal(2, _whisper.Runs.Count);
        Assert.Single(_database.GetNoteSources(job.NoteId));
    }

    [Fact]
    public async Task A_crash_after_the_transcript_was_attached_but_before_the_job_moved_on_keeps_one_transcript()
    {
        var queue = Queue();
        var job = queue.Import(Recording(), keepAudio: false, null, folderPicked: false);
        await queue.RunPendingAsync();
        Assert.Single(_database.GetNoteSources(job.NoteId));

        // As if the app stopped right after the transcript was saved, before the job said so.
        Directory.CreateDirectory(WorkFolder);
        var wav = Path.Combine(WorkFolder, $"job-{job.Id}.wav");
        File.WriteAllText(wav, "RIFF pretend wav");
        _database.SaveTranscriptionJob(_database.GetTranscriptionJob(job.Id)! with { Status = TranscriptionStatus.Transcribing, WavPath = wav, Progress = 40 }, Now);
        _whisper.Transcript = " Hello class, heard a little differently.\n";

        SqliteConnection.ClearAllPools();
        await new TranscriptionQueue(new WaypointDatabase(_dbPath), Maker(), _ready, _converter, _whisper, _folder, () => Now).RunPendingAsync();

        Assert.Equal(TranscriptionStatus.Done, _database.GetTranscriptionJob(job.Id)!.Status);
        var source = Assert.Single(_database.GetNoteSources(job.NoteId));
        Assert.Equal("Hello class.", source.Text);
    }

    [Fact]
    public async Task A_job_stopped_after_the_transcript_was_saved_only_writes_the_notes_next_time()
    {
        var note = _database.CreateNote("lecture", "", Now);
        _database.AddTranscriptSource(note, "Transcript of lecture.m4a", "We covered loops.", null, Now);
        var id = _database.AddTranscriptionJob(note, Recording(), keepAudio: false, Now);
        _database.SaveTranscriptionJob(_database.GetTranscriptionJob(id)! with { Status = TranscriptionStatus.Writing, Progress = 100 }, Now);

        await Queue().RunPendingAsync();

        Assert.Empty(_whisper.Runs);
        Assert.Empty(_converter.Calls);
        Assert.Equal("# Notes from the transcript", _database.GetNote(note)!.Body);
        Assert.Equal(TranscriptionStatus.Done, _database.GetTranscriptionJob(id)!.Status);
    }

    [Fact]
    public async Task Jobs_run_one_at_a_time_oldest_first()
    {
        var queue = Queue();
        var a = queue.Import(Recording("a.mp3"), false, null, false);
        var b = queue.Import(Recording("b.mp3"), false, null, false);

        await queue.RunPendingAsync();

        Assert.Equal([$"job-{a.Id}.part.wav", $"job-{b.Id}.part.wav"], _converter.Calls.Select(call => Path.GetFileName(call.Wav)));
        Assert.All(new[] { a, b }, job => Assert.Equal(TranscriptionStatus.Done, _database.GetTranscriptionJob(job.Id)!.Status));
    }

    // Canceling and failing

    [Fact]
    public async Task Canceling_stops_whisper_deletes_the_converted_audio_and_removes_the_empty_note()
    {
        var queue = Queue();
        var original = Recording();
        var job = queue.Import(original, keepAudio: false, null, folderPicked: false);
        _whisper.HonorCancel = true;
        _whisper.WhileRunning = () => queue.Cancel(job.Id);

        await queue.RunPendingAsync();

        var canceled = _database.GetTranscriptionJob(job.Id)!;
        Assert.Equal(TranscriptionStatus.Canceled, canceled.Status);
        Assert.Null(canceled.WavPath);
        Assert.Empty(Directory.GetFiles(WorkFolder));
        Assert.Null(_database.GetNote(job.NoteId));
        Assert.True(File.Exists(original));
        Assert.Empty(_writer.Calls);
    }

    [Fact]
    public async Task Canceling_keeps_a_note_the_user_typed_in()
    {
        var queue = Queue();
        var job = queue.Import(Recording(), keepAudio: false, null, folderPicked: false);
        _database.SaveNote(job.NoteId, "lecture", "My own notes", Now);

        Assert.True(queue.Cancel(job.Id));
        await queue.RunPendingAsync();

        Assert.Equal("My own notes", _database.GetNote(job.NoteId)!.Body);
        Assert.Empty(_whisper.Runs);
    }

    [Fact]
    public async Task A_whisper_error_fails_the_job_keeps_the_converted_audio_and_try_again_reuses_it()
    {
        _whisper.ExitCode = 1;
        _whisper.Error = "error: failed to read audio";
        var queue = Queue();
        var job = queue.Import(Recording(), keepAudio: false, null, folderPicked: false);

        await queue.RunPendingAsync();

        var failed = _database.GetTranscriptionJob(job.Id)!;
        Assert.Equal(TranscriptionStatus.Failed, failed.Status);
        Assert.Contains("couldn't be turned into text", failed.Error);
        Assert.Contains("failed to read audio", failed.Error);
        Assert.True(File.Exists(failed.WavPath));
        Assert.Contains("Try again", queue.Describe(failed, Now));

        _whisper.ExitCode = 0;
        Assert.True(queue.Retry(job.Id));
        await queue.RunPendingAsync();

        Assert.Equal(TranscriptionStatus.Done, _database.GetTranscriptionJob(job.Id)!.Status);
        Assert.Single(_converter.Calls);
    }

    [Fact]
    public async Task A_file_that_was_moved_says_so()
    {
        var queue = Queue();
        var path = Recording();
        var job = queue.Import(path, keepAudio: false, null, folderPicked: false);
        File.Delete(path);

        await queue.RunPendingAsync();

        Assert.Contains("isn't where it was anymore", _database.GetTranscriptionJob(job.Id)!.Error);
    }

    [Fact]
    public async Task Audio_windows_cant_read_says_so_plainly()
    {
        _converter.Fails = true;
        var queue = Queue();
        var job = queue.Import(Recording(), keepAudio: false, null, folderPicked: false);

        await queue.RunPendingAsync();

        Assert.StartsWith("Windows couldn't read the audio in lecture.m4a.", _database.GetTranscriptionJob(job.Id)!.Error);
        Assert.Empty(_whisper.Runs);
    }

    [Fact]
    public async Task No_speech_fails_without_saving_an_empty_source()
    {
        _whisper.Transcript = "   \n";
        var queue = Queue();
        var job = queue.Import(Recording(), keepAudio: false, null, folderPicked: false);

        await queue.RunPendingAsync();

        Assert.Contains("No speech was found", _database.GetTranscriptionJob(job.Id)!.Error);
        Assert.Empty(_database.GetNoteSources(job.NoteId));
    }

    // Handing off to the notes

    [Fact]
    public async Task When_haiku_fails_the_transcript_is_still_saved_and_the_job_says_so()
    {
        _writer.Fails = true;
        string? told = null;
        var queue = Queue();
        queue.Changed += change => told = change.Outcome ?? told;
        var job = queue.Import(Recording(), keepAudio: false, null, folderPicked: false);

        await queue.RunPendingAsync();

        var done = _database.GetTranscriptionJob(job.Id)!;
        Assert.Equal(TranscriptionStatus.Done, done.Status);
        Assert.Contains("couldn't be written", done.Error);
        Assert.Single(_database.GetNoteSources(job.NoteId));
        Assert.Contains("transcript is saved in Sources", told);
        Assert.Equal("", _database.GetNote(job.NoteId)!.Body);
    }

    [Fact]
    public async Task Text_typed_while_transcribing_is_kept_as_a_source_and_used_in_the_notes()
    {
        var queue = Queue();
        var job = queue.Import(Recording(), keepAudio: false, null, folderPicked: false);
        _whisper.WhileRunning = () => _database.SaveNote(job.NoteId, "lecture", "Ask about the lab", Now);

        await queue.RunPendingAsync();

        var sources = _database.GetNoteSources(job.NoteId);
        Assert.Equal([NoteSourceKind.Transcript, NoteSourceKind.Typed], sources.Select(source => source.Kind));
        Assert.Equal("Ask about the lab", sources[1].Text);
        Assert.Equal("# Notes from the transcript", _database.GetNote(job.NoteId)!.Body);
    }

    [Fact]
    public async Task The_notes_step_can_be_swapped_and_gets_the_note()
    {
        var handed = new List<long>();
        var queue = Queue(writeNotes: (noteId, _) =>
        {
            handed.Add(noteId);
            return Task.FromResult(new NoteWriteOutcome(NoteWriteStatus.Written, "Wrote the notes."));
        });
        var job = queue.Import(Recording(), keepAudio: false, null, folderPicked: false);

        await queue.RunPendingAsync();

        Assert.Equal([job.NoteId], handed);
    }

    // Waiting on the download

    [Fact]
    public async Task Nothing_runs_or_downloads_until_the_user_agrees()
    {
        var downloader = new FakeDownloader();
        var setup = new WhisperSetup(Path.Combine(_folder, "fresh"), downloader);
        var queue = Queue(setup);
        var job = queue.Import(Recording(), keepAudio: false, null, folderPicked: false);

        queue.StartSetup();
        queue.Start();
        await queue.WaitForSetupAsync();
        await queue.RunPendingAsync();

        Assert.Empty(downloader.Urls);
        Assert.Empty(_converter.Calls);
        Assert.Equal(TranscriptionStatus.Queued, _database.GetTranscriptionJob(job.Id)!.Status);
        Assert.Contains("speech to text download", queue.Describe(job, Now));
        queue.Dispose();
    }

    [Fact]
    public void The_sizes_read_plainly()
    {
        Assert.Equal("1.6 GB", TranscriptionQueue.Size(WhisperSetup.Model.Size + WhisperSetup.Program.Size));
        Assert.Equal("22 MB", TranscriptionQueue.Size(WhisperSetup.Program.Size));
    }

    // The download

    [Fact]
    public async Task A_download_that_doesnt_match_its_hash_is_thrown_away_and_nothing_is_installed()
    {
        var (model, program, _, _) = Downloads();
        var downloader = new FakeDownloader { Content = _ => Encoding.UTF8.GetBytes("tampered!!") };
        var data = Path.Combine(_folder, "data");
        var setup = new WhisperSetup(data, downloader, model, program);

        var result = await setup.RunAsync(null, CancellationToken.None);

        Assert.Contains("didn't match", result.Problem);
        Assert.False(setup.IsReady);
        Assert.Empty(Directory.GetFiles(Path.Combine(data, "whisper")));
        Assert.False(Directory.Exists(setup.ProgramFolder));
    }

    [Fact]
    public async Task A_good_download_is_checked_unpacked_and_ready()
    {
        var (model, program, modelBytes, zipBytes) = Downloads();
        var downloader = new FakeDownloader { Content = url => url == model.Url ? modelBytes : zipBytes };
        var data = Path.Combine(_folder, "data");
        var setup = new WhisperSetup(data, downloader, model, program);
        var seen = new List<SetupState>();

        var result = await setup.RunAsync(new Inline<SetupState>(seen.Add), CancellationToken.None);

        Assert.True(result.Ready, result.Problem);
        Assert.True(setup.IsReady);
        Assert.True(File.Exists(Path.Combine(setup.ProgramFolder, "ggml.dll")));
        Assert.False(File.Exists(Path.Combine(setup.ProgramFolder, "bench.exe")));
        Assert.False(File.Exists(Path.Combine(data, "whisper", program.FileName)));
        Assert.Equal(setup.TotalBytes, seen[^1].Done);
    }

    [Fact]
    public async Task A_stopped_download_keeps_its_part_and_carries_on_from_there()
    {
        var (model, program, modelBytes, zipBytes) = Downloads();
        var data = Path.Combine(_folder, "data");
        Directory.CreateDirectory(Path.Combine(data, "whisper"));
        var downloader = new FakeDownloader { Content = url => url == model.Url ? modelBytes : zipBytes, FailAfter = 3 };
        var setup = new WhisperSetup(data, downloader, model, program);

        var first = await setup.RunAsync(null, CancellationToken.None);

        Assert.Contains("carries on from there", first.Problem);
        Assert.True(File.Exists(Path.Combine(data, "whisper", program.FileName + ".part")));

        downloader.FailAfter = null;
        var second = await setup.RunAsync(null, CancellationToken.None);

        Assert.True(second.Ready, second.Problem);
        Assert.Contains(3L, downloader.ResumedFrom);
    }

    [Fact]
    public async Task A_bad_part_left_from_before_is_started_over_once()
    {
        var (model, program, modelBytes, zipBytes) = Downloads();
        var data = Path.Combine(_folder, "data");
        Directory.CreateDirectory(Path.Combine(data, "whisper"));
        File.WriteAllBytes(Path.Combine(data, "whisper", model.FileName + ".part"), Encoding.UTF8.GetBytes("XXXX"));
        var downloader = new FakeDownloader { Content = url => url == model.Url ? modelBytes : zipBytes };
        var setup = new WhisperSetup(data, downloader, model, program);

        var result = await setup.RunAsync(null, CancellationToken.None);

        Assert.True(result.Ready, result.Problem);
        Assert.Equal(modelBytes, File.ReadAllBytes(setup.ModelPath));
        Assert.Equal(3, downloader.Urls.Count);
    }

    [Fact]
    public void The_pinned_downloads_are_the_benchmarked_files()
    {
        Assert.StartsWith("1FC70F77", WhisperSetup.Model.Sha256);
        Assert.Equal(64, WhisperSetup.Model.Sha256.Length);
        Assert.Equal(64, WhisperSetup.Program.Sha256.Length);
        Assert.Contains("/b5454/whisper-blas-bin-x64.zip", WhisperSetup.Program.Url.AbsoluteUri);
        Assert.EndsWith("ggml-large-v3-turbo.bin", WhisperSetup.Model.Url.AbsoluteUri);
    }

    private static (WhisperDownload Model, WhisperDownload Program, byte[] ModelBytes, byte[] ZipBytes) Downloads()
    {
        var modelBytes = Encoding.UTF8.GetBytes("a tiny model for tests");
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in new[] { "Release/whisper-cli.exe", "Release/ggml.dll", "Release/bench.exe" })
            {
                using var entry = new StreamWriter(archive.CreateEntry(name).Open());
                entry.Write(name);
            }
        }

        var zipBytes = zip.ToArray();
        return (
            new WhisperDownload("model.bin", new Uri("https://example.test/model.bin"), Convert.ToHexString(SHA256.HashData(modelBytes)), modelBytes.Length),
            new WhisperDownload("program.zip", new Uri("https://example.test/program.zip"), Convert.ToHexString(SHA256.HashData(zipBytes)), zipBytes.Length),
            modelBytes,
            zipBytes);
    }

    private sealed class Inline<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class FakeWriter : INoteWriter
    {
        public List<IReadOnlyList<NoteSource>> Calls { get; } = [];

        public bool Fails { get; set; }

        public Task<WrittenNotes> WriteAsync(IReadOnlyList<NoteSource> sources, NoteWriteContext context, CancellationToken cancellationToken = default)
        {
            Calls.Add(sources.Where(source => source.Kind == NoteSourceKind.Transcript).ToList());
            if (Fails)
            {
                throw new InvalidOperationException("Haiku didn't answer.");
            }

            return Task.FromResult(new WrittenNotes("# Notes from the transcript", []));
        }
    }

    private sealed class FakeConverter : IAudioConverter
    {
        public List<(string Input, string Wav)> Calls { get; } = [];

        public bool Fails { get; set; }

        /// <summary>How long the audio is.</summary>
        public TimeSpan Length { get; set; } = TimeSpan.FromMinutes(75);

        /// <summary>When false, compressing fails, as when Windows has no encoder.</summary>
        public bool Compresses { get; set; }

        public Task<bool> CompressAsync(string wavPath, string outputPath, CancellationToken cancellationToken)
        {
            if (!Compresses)
            {
                File.WriteAllText(outputPath, "half written");
                throw new InvalidOperationException("No suitable encoders available");
            }

            File.WriteAllText(outputPath, "compressed " + File.ReadAllText(wavPath));
            return Task.FromResult(true);
        }

        public Task<TimeSpan> ConvertAsync(string inputPath, string wavPath, CancellationToken cancellationToken)
        {
            Calls.Add((inputPath, wavPath));
            if (Fails)
            {
                throw new InvalidDataException("Unsupported format");
            }

            File.WriteAllText(wavPath, "RIFF pretend wav");
            return Task.FromResult(Length);
        }
    }

    /// <summary>Acts like whisper-cli: prints progress, then writes the .txt and .json named by -of.</summary>
    private sealed class FakeWhisper : IProcessRunner
    {
        public List<ProcessSpec> Runs { get; } = [];

        public string Transcript { get; set; } = " Hello class.\n";

        public string? Json { get; set; } = """{"transcription":[]}""";

        public int ExitCode { get; set; }

        public string Error { get; set; } = "";

        /// <summary>Runs once, between the first progress line and the rest.</summary>
        public Action? WhileRunning { get; set; }

        /// <summary>Waits to be canceled after WhileRunning, as the real one would still be working.</summary>
        public bool HonorCancel { get; set; }

        public Task<ProcessResult> RunAsync(ProcessSpec spec, Action<string> onLine, CancellationToken cancellationToken)
        {
            Runs.Add(spec);
            Assert.True(File.Exists(spec.Arguments[spec.Arguments.ToList().IndexOf("-f") + 1]));
            onLine("whisper_model_load: loading model");
            onLine("whisper_print_progress_callback: progress =  10%");
            WhileRunning?.Invoke();
            if (HonorCancel && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            onLine("whisper_print_progress_callback: progress =  55%");
            WhileRunning?.Invoke();
            onLine("whisper_print_progress_callback: progress = 100%");
            WhileRunning?.Invoke();
            if (ExitCode == 0)
            {
                var output = spec.Arguments[spec.Arguments.ToList().IndexOf("-of") + 1];
                File.WriteAllText(output + ".txt", Transcript);
                if (Json is not null)
                {
                    File.WriteAllText(output + ".json", Json);
                }
            }

            return Task.FromResult(new ProcessResult(ExitCode, Error));
        }
    }

    /// <summary>Appends to the part file like the real one, from where it left off. FailAfter stops it partway.</summary>
    private sealed class FakeDownloader : IFileDownloader
    {
        public List<Uri> Urls { get; } = [];

        public List<long> ResumedFrom { get; } = [];

        public Func<Uri, byte[]> Content { get; set; } = _ => [];

        public int? FailAfter { get; set; }

        public Task DownloadAsync(Uri url, string partPath, IProgress<long>? progress, CancellationToken cancellationToken)
        {
            Urls.Add(url);
            var bytes = Content(url);
            var have = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            ResumedFrom.Add(have);
            var rest = bytes.Skip((int)Math.Min(have, bytes.Length)).ToArray();
            if (FailAfter is { } limit)
            {
                using (var file = new FileStream(partPath, FileMode.Append))
                {
                    file.Write(rest, 0, Math.Min(limit, rest.Length));
                }

                throw new IOException("The connection was reset");
            }

            using (var file = new FileStream(partPath, FileMode.Append))
            {
                file.Write(rest);
            }

            progress?.Report(new FileInfo(partPath).Length);
            return Task.CompletedTask;
        }
    }
}

/// <summary>Runs only when WAYPOINT_WHISPER_TEST_DIR names a folder.</summary>
public sealed class WhisperFactAttribute : FactAttribute
{
    public const string Variable = "WAYPOINT_WHISPER_TEST_DIR";

    public WhisperFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
        {
            Skip = $"Set {Variable} to a folder with blas\\Release\\whisper-cli.exe, model\\ggml-large-v3-turbo.bin, and audio\\short.wav to run this.";
        }
    }
}

/// <summary>
/// The real whisper-cli on a short 16 kHz WAV, through the queue, the database, and the notes hand-off, with only
/// Haiku faked. Skipped unless <see cref="WhisperFactAttribute.Variable"/> is set, and it never downloads.
/// </summary>
public sealed class WhisperIntegrationTests
{
    [WhisperFact]
    public async Task A_real_recording_is_transcribed_by_whisper_cli_and_saved_on_its_note()
    {
        var root = Environment.GetEnvironmentVariable(WhisperFactAttribute.Variable)!;
        var data = Path.Combine(Path.GetTempPath(), $"waypoint-whisper-{Guid.NewGuid()}");
        Directory.CreateDirectory(data);
        try
        {
            var database = new WaypointDatabase(Path.Combine(data, "waypoint.db"));
            database.ScheduleCourses = ["CS 210"];
            var setup = new WhisperSetup(Path.Combine(root, "model", "ggml-large-v3-turbo.bin"), Path.Combine(root, "blas", "Release"));
            Assert.True(setup.IsReady);
            var maker = new NoteMaker(database, new EchoWriter(), null);
            var wav = Path.Combine(root, "audio", "short.wav");
            var queue = new TranscriptionQueue(database, maker, setup, new CopyConverter(), new ChildProcessRunner(), data);
            var progress = new List<int>();
            queue.Changed += change =>
            {
                if (change.JobId is { } id && database.GetTranscriptionJob(id) is { Status: TranscriptionStatus.Transcribing } running)
                {
                    progress.Add(running.Progress);
                }
            };

            var job = queue.Import(wav, keepAudio: false, null, folderPicked: false);
            await queue.RunPendingAsync();

            var finished = database.GetTranscriptionJob(job.Id)!;
            Assert.True(finished.Status == TranscriptionStatus.Done, finished.Error);
            var source = Assert.Single(database.GetNoteSources(job.NoteId));
            Assert.Contains("recursion", source.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"transcription\"", database.GetSourceTimestamps(source.Id));
            Assert.Contains(100, progress);
            Assert.StartsWith("# Echo", database.GetNote(job.NoteId)!.Body);
            Assert.Empty(Directory.GetFiles(Path.Combine(data, "whisper", "work")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(data, recursive: true);
        }
    }

    /// <summary>The test WAV is already 16 kHz mono, so it's only copied.</summary>
    private sealed class CopyConverter : IAudioConverter
    {
        public Task<TimeSpan> ConvertAsync(string inputPath, string wavPath, CancellationToken cancellationToken)
        {
            File.Copy(inputPath, wavPath, overwrite: true);
            return Task.FromResult(TimeSpan.FromSeconds((new FileInfo(wavPath).Length - 44) / 32000.0));
        }
    }

    private sealed class EchoWriter : INoteWriter
    {
        public Task<WrittenNotes> WriteAsync(IReadOnlyList<NoteSource> sources, NoteWriteContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WrittenNotes("# Echo\n\n" + sources[0].Text, []));
    }
}
