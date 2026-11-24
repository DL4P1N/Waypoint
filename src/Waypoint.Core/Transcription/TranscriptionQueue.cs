using System.Globalization;
using Waypoint.Core.Data;
using Waypoint.Core.Notes;

namespace Waypoint.Core.Transcription;

/// <summary>
/// Turns imported recordings into class notes, on this PC. Each import makes its note straight away and adds a job
/// to <c>transcription_jobs</c>. Jobs run one at a time in the background: the audio is converted to a 16 kHz WAV,
/// whisper-cli turns it into text at below normal priority, the transcript is saved unchanged as a source on the
/// note, and the notes are written from it the same way as from any file. Audio never leaves the PC. Only the
/// transcript text goes to Haiku. The converted WAV is deleted once the transcript is saved, and the original file
/// is never changed. A copy of the audio is kept only when the user asked for one.
/// </summary>
public sealed class TranscriptionQueue : IDisposable
{
    public const string ConsentPreference = "whisper_download_agreed";

    /// <summary>What Windows Media Foundation can read without anything else installed, and Ogg Opus, which is moved into WebM for it.</summary>
    public static readonly IReadOnlySet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".mp4", ".wav", ".wma", ".aac", ".flac", ".m4v", ".mov", ".wmv", ".3gp", ".ogg", ".opus",
    };

    private readonly WaypointDatabase _database;
    private readonly INotesData _notes;
    private readonly WhisperSetup _setup;
    private readonly IAudioConverter _converter;
    private readonly IProcessRunner _runner;
    private readonly Func<DateTime> _clock;
    private readonly Func<long, CancellationToken, Task<NoteWriteOutcome>> _writeNotes;
    private readonly string _workFolder;
    private readonly string _audioFolder;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _lock = new();
    private (long Id, CancellationTokenSource Stop)? _current;
    private Task? _loop;
    private Task? _setupTask;
    private volatile SetupState? _setupState;

    /// <param name="writeNotes">Writes a note's notes once its transcript is saved. By default that's <see cref="NoteMaker.WriteAsync"/>.</param>
    /// <param name="notes">Where the notes are kept. In server mode that's the server, and jobs stay in this PC's database.</param>
    public TranscriptionQueue(
        WaypointDatabase database,
        NoteMaker maker,
        WhisperSetup setup,
        IAudioConverter converter,
        IProcessRunner runner,
        string dataDirectory,
        Func<DateTime>? clock = null,
        Func<long, CancellationToken, Task<NoteWriteOutcome>>? writeNotes = null,
        INotesData? notes = null)
    {
        _database = database;
        _notes = notes ?? new LocalNotesData(database, maker);
        _setup = setup;
        _converter = converter;
        _runner = runner;
        _clock = clock ?? (() => DateTime.Now);
        _writeNotes = writeNotes ?? WriteNotesAsync;
        _workFolder = Path.Combine(dataDirectory, "whisper", "work");
        _audioFolder = Path.Combine(dataDirectory, "audio");
    }

    /// <summary>Raised from a background thread whenever a job or the download moves on.</summary>
    public event Action<TranscriptionChange>? Changed;

    public bool IsReady => _setup.IsReady;

    public long DownloadBytes => _setup.TotalBytes;

    public bool AgreedToDownload => _database.GetPreference(ConsentPreference) == "yes";

    /// <summary>The download's progress while it runs, or why it stopped. Null when it isn't running and nothing went wrong.</summary>
    public SetupState? Setup => _setupState;

    public long? RunningJobId
    {
        get
        {
            lock (_lock)
            {
                return _current?.Id;
            }
        }
    }

    /// <summary>Unfinished jobs by their note, for the Notes page.</summary>
    public IReadOnlyDictionary<long, TranscriptionJob> OpenJobsByNote() => _database.GetOpenTranscriptionJobsByNote();

    public static bool IsAudioFile(string path) => AudioExtensions.Contains(Path.GetExtension(path));

    /// <summary>Makes the note and queues the recording. Nothing is read or copied here, so it's quick.</summary>
    public TranscriptionJob Import(string path, bool keepAudio, long? pickedFolderId, bool folderPicked)
    {
        var now = _clock();
        var full = Path.GetFullPath(path);
        var started = _notes.StartForRecording(full, pickedFolderId, folderPicked, now);
        return Queue(started.NoteId, full, keepAudio, now, null);
    }

    /// <summary>
    /// Queues a lecture recorded on this PC. It's filed by the class that was on when recording started, and gets
    /// the title the recorder gave it. The file is the recorder's own, which it deletes once the transcript is saved.
    /// <paramref name="alongside"/> is saved in the same write as the job, such as the recorder marking it handed off.
    /// </summary>
    public TranscriptionJob ImportRecording(string path, bool keepAudio, DateTime startedAt, string title, Action<WaypointDatabase, TranscriptionJob>? alongside = null)
    {
        var full = Path.GetFullPath(path);
        var started = _notes.StartForRecording(full, null, false, startedAt, title);
        return Queue(started.NoteId, full, keepAudio, _clock(), alongside);
    }

    /// <summary>
    /// Adds the job for a note just made for it, with anything that goes with it, in one write. If that can't be
    /// saved, the note, still empty, goes too, so an import never leaves a note waiting on a job that isn't there.
    /// The note can live on the server, so it can't be part of the same write.
    /// </summary>
    private TranscriptionJob Queue(long noteId, string full, bool keepAudio, DateTime now, Action<WaypointDatabase, TranscriptionJob>? alongside)
    {
        TranscriptionJob job;
        try
        {
            job = _database.InOneWrite(write =>
            {
                var made = write.GetTranscriptionJob(write.AddTranscriptionJob(noteId, full, keepAudio, now))!;
                alongside?.Invoke(write, made);
                return made;
            });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            DeleteIfEmpty(noteId);
            throw;
        }

        Raise(new TranscriptionChange(job.Id, noteId));
        _wake.Release();
        return job;
    }

    /// <summary>Deletes a note that has nothing in it, typed or attached. Best effort: one that can't be reached now is left.</summary>
    private void DeleteIfEmpty(long noteId)
    {
        try
        {
            if (_notes.GetNote(noteId) is { } note && string.IsNullOrWhiteSpace(note.Body) && _notes.GetNoteSources(noteId).Count == 0)
            {
                _notes.DeleteNote(noteId, _clock());
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            PlainProblem.Report(exception);
        }
    }

    /// <summary>Remembers the yes, so a download cut off by a restart carries on, and starts it.</summary>
    public void AgreeToDownload()
    {
        _database.SetPreference(ConsentPreference, "yes");
        StartSetup();
    }

    /// <summary>Starts the one-time download, only ever after the user agreed. Does nothing while one is running.</summary>
    public void StartSetup()
    {
        if (!AgreedToDownload || IsReady)
        {
            return;
        }

        lock (_lock)
        {
            if (_setupTask is { IsCompleted: false })
            {
                return;
            }

            _setupState = new SetupState(true, 0, _setup.TotalBytes);
            _setupTask = Task.Run(RunSetupAsync);
        }
    }

    /// <summary>Waits for a download started with <see cref="StartSetup"/>, for tests.</summary>
    public Task WaitForSetupAsync()
    {
        lock (_lock)
        {
            return _setupTask ?? Task.CompletedTask;
        }
    }

    /// <summary>Starts the background worker, which carries on with any jobs left from last time.</summary>
    public void Start()
    {
        SweepStaleParts();
        lock (_lock)
        {
            _loop ??= Task.Run(LoopAsync);
        }

        if (!IsReady && _database.GetActiveTranscriptionJobs().Count > 0)
        {
            StartSetup();
        }

        _wake.Release();
    }

    /// <summary>
    /// Stops a job. Whisper is stopped if it's running, the converted audio is deleted, and a note with nothing in
    /// it yet is removed. A note with a transcript or anything typed in it stays.
    /// </summary>
    public bool Cancel(long jobId)
    {
        if (_database.GetTranscriptionJob(jobId) is not { IsActive: true } job)
        {
            return false;
        }

        // Transcribing is already done once it's writing notes, and the transcript is saved on the note. Stopping then
        // only stops the notes, so the job is done, and a recording it came from is cleaned up like any other.
        var transcribed = job.Status == TranscriptionStatus.Writing;
        _database.SaveTranscriptionJob(
            transcribed ? job with { Status = TranscriptionStatus.Done, Error = StoppedWritingText } : job with { Status = TranscriptionStatus.Canceled },
            _clock());
        bool running;
        lock (_lock)
        {
            running = _current?.Id == jobId;
            if (running)
            {
                _current!.Value.Stop.Cancel();
            }
        }

        // A running job cleans up after itself once Whisper has stopped and let go of the files.
        if (!running && !transcribed)
        {
            CleanUpCanceled(jobId);
        }

        Raise(new TranscriptionChange(jobId, job.NoteId, transcribed ? StoppedWritingText : "Stopped transcribing."));
        return true;
    }

    private const string StoppedWritingText = "Stopped writing notes. The transcript is saved in Sources, so notes can be written from it later.";

    /// <summary>Starts a failed job over. A converted WAV that's still there is used again.</summary>
    public bool Retry(long jobId)
    {
        if (_database.GetTranscriptionJob(jobId) is not { Status: TranscriptionStatus.Failed } job)
        {
            return false;
        }

        _database.SaveTranscriptionJob(job with { Status = TranscriptionStatus.Queued, Progress = 0, Error = null }, _clock());
        Raise(new TranscriptionChange(jobId, job.NoteId));
        StartSetup();
        _wake.Release();
        return true;
    }

    /// <summary>Runs jobs, oldest first, until none are left or the program files aren't in place.</summary>
    public async Task RunPendingAsync(CancellationToken cancellationToken = default)
    {
        await _turn.WaitAsync(cancellationToken);
        try
        {
            while (!cancellationToken.IsCancellationRequested
                && _database.GetActiveTranscriptionJobs().FirstOrDefault(job => job.Status == TranscriptionStatus.Writing || IsReady) is { } job)
            {
                await RunJobAsync(job, cancellationToken);
            }
        }
        finally
        {
            _turn.Release();
        }
    }

    /// <summary>The line shown on the note while it's transcribed, in plain words.</summary>
    public string Describe(TranscriptionJob job, DateTime now)
    {
        switch (job.Status)
        {
            case TranscriptionStatus.Failed:
                return $"{job.Error} Choose Try again to start it over.";
            case TranscriptionStatus.Converting:
                return $"Getting the audio from {job.Name} ready to transcribe.";
            case TranscriptionStatus.Transcribing:
                var elapsed = job.StartedAt is { } started ? now - started : TimeSpan.Zero;
                var left = WhisperProgress.Remaining(job.DurationSeconds, job.Progress, elapsed);
                var doing = left is { } time ? $"Transcribing, {WhisperProgress.Words(time)}." : $"Transcribing, {job.Progress}% done.";
                return $"{doing} This happens on this PC, so you can keep working. You can type your own notes here meanwhile.";
            case TranscriptionStatus.Writing:
                return "Transcribed. Writing notes from the transcript with Claude.";
            case TranscriptionStatus.Queued when !IsReady:
                return Setup switch
                {
                    { Running: true } setup => $"Downloading the speech to text files, {setup.Percent}% of {Size(setup.Total)}. This only happens once. Transcribing starts after.",
                    { Problem: { } problem } => problem,
                    _ when AgreedToDownload => "Waiting for the speech to text download to start.",
                    _ => "Waiting for the speech to text download. Choose Try again to start it.",
                };
            case TranscriptionStatus.Queued when RunningJobId is { } running && running != job.Id:
                return "Waiting for another recording to finish first.";
            case TranscriptionStatus.Queued:
                return "Starting soon.";
            default:
                return "";
        }
    }

    /// <summary>A few words for the note list.</summary>
    public static string ListLabel(TranscriptionJob job) => job.Status switch
    {
        TranscriptionStatus.Queued => "Waiting to transcribe",
        TranscriptionStatus.Converting => "Getting audio ready",
        TranscriptionStatus.Transcribing => $"Transcribing {job.Progress}%",
        TranscriptionStatus.Writing => "Writing notes",
        TranscriptionStatus.Failed => "Transcribing stopped",
        _ => "",
    };

    /// <summary>"1.6 GB" or "22 MB".</summary>
    public static string Size(long bytes) => bytes >= 1_000_000_000
        ? $"{(bytes / 1_000_000_000.0).ToString("0.0", CultureInfo.InvariantCulture)} GB"
        : $"{Math.Max(1, (long)Math.Round(bytes / 1_000_000.0))} MB";

    /// <summary>whisper-cli's arguments: plain text and JSON with timestamps out, progress lines on, and the course hints.</summary>
    public static IReadOnlyList<string> WhisperArguments(string modelPath, string wavPath, string outputBase, string prompt, int threads)
    {
        var arguments = new List<string>
        {
            "-m", modelPath,
            "-f", wavPath,
            "-t", threads.ToString(CultureInfo.InvariantCulture),
            "-l", "en",
            "-otxt",
            "-oj",
            "-of", outputBase,
            "-pp",
        };
        if (prompt.Length > 0)
        {
            arguments.Add("--prompt");
            arguments.Add(prompt);
        }

        return arguments;
    }

    /// <summary>The hint for a note: every class, and open deadlines, exams, and class tasks, its own class's first.</summary>
    public string PromptFor(long noteId)
    {
        var course = _notes.GetNoteCourse(noteId);
        var assignments = _notes.GetOpenItems()
            .Where(item => item.Kind is Capture.ItemKind.Deadline or Capture.ItemKind.Exam || item.Kind == Capture.ItemKind.Task && item.Course is not null)
            .Select(item => (item.Title, item.Course))
            .Take(60);
        return WhisperPrompt.Build(course, _notes.GetClassNames(), assignments);
    }

    // Long enough for whisper-cli to be stopped, so it doesn't keep running after Waypoint closes.
    public void Dispose() => Stop(TimeSpan.FromSeconds(3));

    /// <summary>
    /// Stops transcribing and waits up to <paramref name="limit"/> for it to end, as before a restore. True once nothing
    /// is left running, so nothing it would save can land after.
    /// </summary>
    public bool Stop(TimeSpan limit)
    {
        _stopping.Cancel();
        try
        {
            return _loop?.Wait(limit) ?? true;
        }
        catch (AggregateException)
        {
            // It ended, by failing or being stopped, which is all that matters here.
            return true;
        }
    }

    private async Task LoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(_stopping.Token);
                await RunPendingAsync(_stopping.Token);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A database or disk problem shouldn't stop the worker for good. The job is tried again on the next wake.
                Raise(new TranscriptionChange(null, null, $"Transcribing ran into a problem. {PlainProblem.Report(exception)} It will try again on its own."));
            }
        }
    }

    private async Task RunSetupAsync()
    {
        var shown = -1;
        var progress = new InlineProgress<SetupState>(state =>
        {
            _setupState = state;
            if (state.Percent != shown)
            {
                shown = state.Percent;
                Raise(new TranscriptionChange(null, null));
            }
        });

        SetupResult result;
        try
        {
            result = await _setup.RunAsync(progress, _stopping.Token);
        }
        catch (OperationCanceledException)
        {
            _setupState = null;
            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            result = new SetupResult($"The download couldn't finish. {PlainProblem.Report(exception)} Try again later.");
        }

        _setupState = result.Ready ? null : new SetupState(false, 0, _setup.TotalBytes, result.Problem);
        Raise(new TranscriptionChange(null, null, result.Ready ? "Speech to text is set up." : result.Problem));
        if (result.Ready)
        {
            _wake.Release();
        }
    }

    private async Task RunJobAsync(TranscriptionJob job, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_lock)
        {
            _current = (job.Id, stop);
        }

        try
        {
            if (job.Status != TranscriptionStatus.Writing)
            {
                job = await PrepareAudioAsync(job, stop.Token);
                job = await TranscribeAsync(job, stop.Token);
            }

            await WriteAsync(job, stop.Token);
        }
        catch (StopJob)
        {
            // Saved as failed, or canceled, already.
        }
        catch (Exception exception) when (exception is OperationCanceledException or CanceledJob && !cancellationToken.IsCancellationRequested)
        {
            // The user canceled. The worker isn't stopping, so this job is cleaned up and the next one runs.
        }
        catch (OperationCanceledException)
        {
            // Waypoint is closing. The job stays as it was and carries on next time.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Fail(job, $"Something went wrong transcribing {job.Name}. {PlainProblem.Report(exception)} Try it again from Notes.");
        }
        finally
        {
            lock (_lock)
            {
                _current = null;
            }
        }

        if (_database.GetTranscriptionJob(job.Id) is { Status: TranscriptionStatus.Canceled })
        {
            CleanUpCanceled(job.Id);
        }
    }

    private async Task<TranscriptionJob> PrepareAudioAsync(TranscriptionJob job, CancellationToken cancellationToken)
    {
        job = Save(job with { Status = TranscriptionStatus.Converting, Progress = 0, Error = null });
        if (job.WavPath is { } existing && File.Exists(existing))
        {
            return job;
        }

        var source = job.KeptAudioPath is { } kept && File.Exists(kept) ? kept : job.OriginalPath;
        if (!File.Exists(source))
        {
            Fail(job, $"{job.Name} isn't where it was anymore, so it can't be transcribed. Import it again from where it is now.");
            throw new StopJob();
        }

        if (job.KeepAudio && job.KeptAudioPath is null)
        {
            var copy = await CopyAudioAsync(source, cancellationToken);
            job = Save(job with { KeptAudioPath = copy });
            source = copy;
        }

        Directory.CreateDirectory(_workFolder);
        var wav = Path.Combine(_workFolder, $"job-{job.Id}.wav");
        var part = Path.Combine(_workFolder, $"job-{job.Id}.part.wav");
        TimeSpan length;
        try
        {
            length = await _converter.ConvertAsync(source, part, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            Delete(part);
            Fail(job, $"Windows couldn't read the audio in {job.Name}. It may be a kind of file Windows can't play.");
            PlainProblem.Report(exception);
            throw new StopJob();
        }

        File.Move(part, wav, overwrite: true);
        return Save(job with { WavPath = wav, DurationSeconds = length.TotalSeconds });
    }

    private async Task<TranscriptionJob> TranscribeAsync(TranscriptionJob job, CancellationToken cancellationToken)
    {
        job = Save(job with { Status = TranscriptionStatus.Transcribing, Progress = 0, StartedAt = _clock() });
        var outputBase = OutputBase(job.Id);
        DeleteOutputs(job.Id);

        var arguments = WhisperArguments(_setup.ModelPath, job.WavPath!, outputBase, PromptFor(job.NoteId), Math.Clamp(Environment.ProcessorCount / 2, 1, 16));
        var shown = 0;
        var running = job;
        var result = await _runner.RunAsync(new ProcessSpec(_setup.CliPath, arguments, _workFolder), line =>
        {
            if (WhisperProgress.Parse(line) is { } percent && percent > shown)
            {
                shown = percent;
                running = running with { Progress = percent };
                _database.SaveTranscriptionJob(running, _clock());
                Raise(new TranscriptionChange(running.Id, running.NoteId));
            }
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        job = running;

        var textPath = outputBase + ".txt";
        if (result.ExitCode != 0 || !File.Exists(textPath))
        {
            var detail = result.ErrorTail.Trim() is { Length: > 0 } tail ? $" Whisper said: {LastLine(tail)}" : "";
            Fail(job, $"{job.Name} couldn't be turned into text.{detail}");
            throw new StopJob();
        }

        var text = (await File.ReadAllTextAsync(textPath, cancellationToken)).Trim();
        var jsonPath = outputBase + ".json";
        var timestamps = File.Exists(jsonPath) ? await File.ReadAllTextAsync(jsonPath, cancellationToken) : null;

        // The audio goes once the transcript is saved and checked. One that stops well before the end may have
        // missed part, so the audio is kept with the note to play and check against.
        if (!job.KeepAudio && job.KeptAudioPath is null && text.Length > 0 && !TranscriptCheck.Passes(text, timestamps, job.DurationSeconds)
            && File.Exists(job.OriginalPath))
        {
            job = Save(job with { KeptAudioPath = await CopyAudioAsync(job.OriginalPath, cancellationToken) });
        }

        // Saved before anything else, so a failed or slow Haiku call never loses the transcript.
        var attached = await AttachAsync(job, text, timestamps, cancellationToken);
        if (attached.Problem is { } problem)
        {
            if (_notes.GetNote(job.NoteId) is null)
            {
                _database.SaveTranscriptionJob(job with { Status = TranscriptionStatus.Canceled }, _clock());
                throw new CanceledJob();
            }

            Fail(job, $"{problem} Nothing was saved from it.");
            throw new StopJob();
        }

        var wav = job.WavPath;
        job = Save(job with { Status = TranscriptionStatus.Writing, Progress = 100, WavPath = null });
        Delete(wav);
        DeleteOutputs(job.Id);
        return job;
    }

    private async Task WriteAsync(TranscriptionJob job, CancellationToken cancellationToken)
    {
        Raise(new TranscriptionChange(job.Id, job.NoteId));
        var outcome = await _writeNotes(job.NoteId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var title = _notes.GetNote(job.NoteId)?.Title ?? job.Name;
        var written = outcome.Status == NoteWriteStatus.Written;
        Save(job with { Status = TranscriptionStatus.Done, Error = written ? null : outcome.Message });
        var kept = KeptForChecking(job) ? $" {KeptForCheckingText}" : "";
        Raise(new TranscriptionChange(job.Id, job.NoteId, written
            ? $"{title}: transcribed. {outcome.Message}{kept}"
            : $"{title}: transcribed, and the transcript is saved in Sources. {outcome.Message}{kept}"));
    }

    /// <summary>Audio the user didn't ask to keep, kept because the transcript didn't pass the check.</summary>
    private static bool KeptForChecking(TranscriptionJob job) => !job.KeepAudio && job.KeptAudioPath is not null;

    private const string KeptForCheckingText =
        "The transcript seems to stop before the recording ends, so the audio is kept with the note. Play it from the transcript to hear what was missed.";

    /// <summary>Typed text kept as a source is safe to write over, so a fresh note is written without asking. Anything else waits for a yes.</summary>
    private Task<NoteWriteOutcome> WriteNotesAsync(long noteId, CancellationToken cancellationToken) =>
        _notes.WriteNotesAsync(noteId, null, replaceEdits: _notes.GetNoteGeneration(noteId) is { WasGenerated: false }, _clock(), cancellationToken);

    /// <summary>
    /// In server mode the transcript is saved on the server. While it's away the transcript waits here, already
    /// made, and is sent once it's back, so the recording never has to be transcribed again.
    /// </summary>
    private async Task<StartedNote> AttachAsync(TranscriptionJob job, string text, string? timestamps, CancellationToken cancellationToken)
    {
        var told = false;
        while (true)
        {
            try
            {
                // Keyed by the job, so a job picked up again after the app stopped before it moved on keeps one transcript.
                return _notes.AttachTranscript(job.NoteId, job.Name, text, timestamps, _clock(), job.KeptAudioPath, $"transcript-job:{job.Id}:{job.NoteId}");
            }
            catch (Remote.ServerUnavailableException)
            {
                if (!told)
                {
                    told = true;
                    Raise(new TranscriptionChange(job.Id, job.NoteId, $"{job.Name} is transcribed. The transcript is sent to the server when it's back."));
                }

                await Task.Delay(ServerRetryPause, cancellationToken);
            }
        }
    }

    /// <summary>How long to wait before trying the server again with a finished transcript. Tests shorten it.</summary>
    public TimeSpan ServerRetryPause { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Keeps a copy of the audio in the data folder. A WAV, such as a recording, is kept compressed when Windows can,
    /// at about a fifth of the size. Anything else, or a WAV that can't be compressed, is copied as it is.
    /// </summary>
    private async Task<string> CopyAudioAsync(string source, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_audioFolder);
        var stem = $"{_clock():yyyy-MM-dd HHmm} {Path.GetFileNameWithoutExtension(source)}";
        if (string.Equals(Path.GetExtension(source), ".wav", StringComparison.OrdinalIgnoreCase)
            && await CompressAsync(source, stem, cancellationToken) is { } compressed)
        {
            return compressed;
        }

        var extension = Path.GetExtension(source);
        var target = FreeAudioPath(stem, extension);
        var part = target + ".part";
        try
        {
            await using (var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
            await using (var to = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                await from.CopyToAsync(to, cancellationToken);
            }

            File.Move(part, target);
            return target;
        }
        finally
        {
            // A copy cut short, by a full disk or being stopped, is never finished, so it goes.
            Delete(part);
        }
    }

    /// <summary>
    /// Working files left by a copy or conversion cut off when Waypoint last stopped suddenly. Only names that can't
    /// be a kept copy are taken: in the audio folder ones ending in .part, and in the work folder, which holds only
    /// Waypoint's own working files, any part file.
    /// </summary>
    private void SweepStaleParts()
    {
        Platform.PartFiles.SweepStale(_audioFolder, Platform.PartFiles.EndingIn(".part"), Platform.PartFiles.StaleAfter);
        Platform.PartFiles.SweepStale(_workFolder, name => name.Contains(".part", StringComparison.OrdinalIgnoreCase), Platform.PartFiles.StaleAfter);
    }

    /// <summary>A free name for a kept copy, which never ends in .part, so the start-up sweep can't take it.</summary>
    private string FreeAudioPath(string stem, string extension)
    {
        extension = Platform.PartFiles.FinishedExtension(extension);
        var target = Path.Combine(_audioFolder, stem + extension);
        for (var n = 2; File.Exists(target); n++)
        {
            target = Path.Combine(_audioFolder, $"{stem} ({n}){extension}");
        }

        return target;
    }

    /// <summary>The compressed copy's path, or null when it couldn't be made. A part file is never left behind.</summary>
    private async Task<string?> CompressAsync(string source, string stem, CancellationToken cancellationToken)
    {
        var extension = _converter.CompressedExtension;
        var target = FreeAudioPath(stem, extension);

        // The encoder picks the kind of file from its extension, so the part file keeps it. It's made in the work
        // folder, so one left by a crash can't be mistaken for a kept copy whose own name has ".part" in it.
        Directory.CreateDirectory(_workFolder);
        var part = Path.Combine(_workFolder, Path.GetFileNameWithoutExtension(target) + ".part" + extension);
        try
        {
            if (await _converter.CompressAsync(source, part, cancellationToken) && File.Exists(part) && new FileInfo(part).Length > 0)
            {
                File.Move(part, target);
                return target;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            PlainProblem.Report(exception);
        }
        finally
        {
            Delete(part);
        }

        return null;
    }

    private void CleanUpCanceled(long jobId)
    {
        if (_database.GetTranscriptionJob(jobId) is not { } job)
        {
            return;
        }

        Delete(job.WavPath);
        Delete(Path.Combine(_workFolder, $"job-{job.Id}.part.wav"));
        DeleteOutputs(job.Id);
        _database.SaveTranscriptionJob(job with { WavPath = null }, _clock());

        // Nothing of the user's is lost: the note is empty, and their original file is where it always was.
        // In server mode, with the server away, the empty note is left for the user to delete.
        try
        {
            if (_notes.GetNote(job.NoteId) is { } note && string.IsNullOrWhiteSpace(note.Body) && _notes.GetNoteSources(job.NoteId).Count == 0)
            {
                _notes.DeleteNote(job.NoteId, _clock());
                Delete(job.KeptAudioPath);
            }
        }
        catch (Remote.ServerUnavailableException)
        {
        }
    }

    private TranscriptionJob Save(TranscriptionJob job)
    {
        if (!_database.SaveTranscriptionJob(job, _clock()))
        {
            throw new CanceledJob();
        }

        Raise(new TranscriptionChange(job.Id, job.NoteId));
        return job;
    }

    private void Fail(TranscriptionJob job, string message)
    {
        if (_database.SaveTranscriptionJob(job with { Status = TranscriptionStatus.Failed, Error = message }, _clock()))
        {
            Raise(new TranscriptionChange(job.Id, job.NoteId, message));
        }
    }

    private string OutputBase(long jobId) => Path.Combine(_workFolder, $"job-{jobId}-out");

    private void DeleteOutputs(long jobId)
    {
        Delete(OutputBase(jobId) + ".txt");
        Delete(OutputBase(jobId) + ".json");
    }

    private static void Delete(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left for the next cleanup. It's only a working copy.
        }
    }

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? text;

    private void Raise(TranscriptionChange change)
    {
        try
        {
            Changed?.Invoke(change);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Showing the change is the page's job. A problem there mustn't stop transcribing.
        }
    }

    /// <summary>Reports straight away, on the thread that reports, unlike <see cref="Progress{T}"/>.</summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>The job was saved as failed, so there's nothing more to do with it.</summary>
    private sealed class StopJob : Exception;

    /// <summary>The user canceled the job, or its note is gone.</summary>
    private sealed class CanceledJob : Exception;
}
