using System.Globalization;
using Waypoint.Core.Data;
using Waypoint.Core.Notes;
using Waypoint.Core.Schedule;
using Waypoint.Core.Transcription;

namespace Waypoint.Core.Recording;

/// <summary>
/// Records a lecture on this PC, only ever after the user asks. The recording is saved in <c>recordings</c> before
/// any sound is written, and the sound goes to disk as it comes in, so a crash or power cut keeps it up to the last
/// flush and the next start finds it, fixes it, and offers to make notes from it. While recording the PC is kept
/// from sleeping. On stop it goes to <see cref="TranscriptionQueue"/> like an imported file, filed under the class
/// that was on when it started. The audio never leaves the PC, and its file is deleted once the transcript is
/// saved unless the user asked to keep it.
/// The sound comes in already mono at 16 kHz, from the audio threads. Everything else is called from one thread.
/// </summary>
public sealed class Recorder
{
    /// <summary>The computer's sound can fall this far behind the microphone before it's taken as silent.</summary>
    public static readonly TimeSpan MaxMixLag = TimeSpan.FromMilliseconds(500);

    /// <summary>With computer audio only, a gap this long with nothing sent is filled with silence, so times stay true.</summary>
    public static readonly TimeSpan SilenceGap = TimeSpan.FromSeconds(1);

    public const int MaxMarkChars = 300;

    private readonly WaypointDatabase _database;
    private readonly string _folder;
    private readonly IKeepAwake _keepAwake;
    private readonly ClassSchedule? _schedule;
    private readonly Func<DateTime> _clock;
    private readonly TimeSpan? _flushEvery;
    private readonly object _lock = new();
    private Session? _session;

    public Recorder(WaypointDatabase database, string dataDirectory, IKeepAwake keepAwake, ClassSchedule? schedule, Func<DateTime>? clock = null, TimeSpan? flushEvery = null)
    {
        _database = database;
        _folder = Path.Combine(dataDirectory, "recordings");
        _keepAwake = keepAwake;
        _schedule = schedule;
        _clock = clock ?? (() => DateTime.Now);
        _flushEvery = flushEvery;
    }

    public bool IsRecording => _session is not null;

    /// <summary>The recording going on now, or null.</summary>
    public RecordingInfo? Current => _session?.Info;

    /// <summary>How much has been recorded. Times for marks and the transcript count from here, not the wall clock.</summary>
    public TimeSpan Elapsed => _session?.Writer.Length ?? TimeSpan.Zero;

    /// <summary>The loudest sound in the last piece of audio, from 0 to 1, for the level meter.</summary>
    public float Level => _session?.Level ?? 0f;

    public IReadOnlyList<RecordingMark> Marks
    {
        get
        {
            lock (_lock)
            {
                return _session?.Marks.ToList() ?? [];
            }
        }
    }

    /// <summary>The problems going on now, for the indicator.</summary>
    public IReadOnlyList<RecordingWarning> Warnings => _session?.Watch.Showing ?? [];

    /// <summary>The title a recording started now gets: the class and date in class, else "Recording" and the date.</summary>
    public string TitleFor(DateTime now) => ClassLinking.CurrentClass(_schedule, now) is { } current
        ? ClassLinking.Title(current.Meeting.Course, now)
        : $"Recording, {now.ToString("ddd MMM d", CultureInfo.InvariantCulture)}";

    /// <summary>Starts recording. Call only when the user asked for it.</summary>
    public RecordingInfo Start(AudioInput input, bool keepAudio)
    {
        lock (_lock)
        {
            if (_session is not null)
            {
                throw new InvalidOperationException("A recording is already going.");
            }

            var now = _clock();
            var title = TitleFor(now);
            var id = _database.AddRecording(input, keepAudio, title, now);
            RecordingWriter writer;
            string path;
            try
            {
                Directory.CreateDirectory(_folder);
                path = FreePath(title, now);
                writer = RecordingWriter.Create(path, _flushEvery);
            }
            catch
            {
                _database.FinishRecording(id, RecordingStatus.Empty, now, 0);
                throw;
            }

            _database.SetRecordingPath(id, path);
            _keepAwake.Hold();
            var mixer = input == AudioInput.Both ? new TwoSourceMixer((int)(MaxMixLag.TotalSeconds * RecordingWriter.SampleRate)) : null;
            _session = new Session(_database.GetRecording(id)!, writer, new RecordingWatch(now, input != AudioInput.ComputerAudio), mixer, now);
            return _session.Info;
        }
    }

    /// <summary>Sound from the microphone, mono at 16 kHz. From the audio thread.</summary>
    public void WriteMicrophone(ReadOnlySpan<float> samples)
    {
        if (_session is not { } session)
        {
            return;
        }

        session.Watch.Heard(AudioMath.Peak(samples), _clock());
        switch (session.Info.Input)
        {
            case AudioInput.Microphone:
                Write(session, samples);
                break;
            case AudioInput.Both:
                Write(session, session.Mixer!.AddFirst(samples));
                break;
        }
    }

    /// <summary>What the computer plays, mono at 16 kHz. From the audio thread.</summary>
    public void WriteComputerAudio(ReadOnlySpan<float> samples)
    {
        if (_session is not { } session)
        {
            return;
        }

        switch (session.Info.Input)
        {
            case AudioInput.ComputerAudio:
                Write(session, samples);
                break;
            case AudioInput.Both:
                Write(session, session.Mixer!.AddSecond(samples));
                break;
        }
    }

    /// <summary>
    /// Every few seconds while recording. Fills a gap in computer audio with silence, and returns the warnings
    /// raised for the first time, each only once per recording.
    /// </summary>
    public IReadOnlyList<RecordingWarning> Check(PowerReading power, long? freeBytes, bool microphoneMuted = false)
    {
        if (_session is not { } session)
        {
            return [];
        }

        var now = _clock();
        if (session.Info.Input == AudioInput.ComputerAudio)
        {
            var expected = (long)((now - session.StartedAt).TotalSeconds * RecordingWriter.SampleRate);
            var behind = expected - session.Writer.SamplesWritten;
            if (behind > SilenceGap.TotalSeconds * RecordingWriter.SampleRate)
            {
                session.Writer.WriteSilence((int)Math.Min(behind, int.MaxValue));
            }
        }

        return session.Watch.Check(now, power, freeBytes, microphoneMuted);
    }

    /// <summary>The sound device stopped. Returns true the first time, so it's said once.</summary>
    public bool InputLost() => _session?.Watch.InputLost() == true;

    /// <summary>Stamps "this matters", or a line the user typed, at this point in the recording. Saved straight away.</summary>
    public RecordingMark? Mark(string? text = null)
    {
        lock (_lock)
        {
            if (_session is not { } session)
            {
                return null;
            }

            var line = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            if (line is { Length: > MaxMarkChars })
            {
                line = line[..MaxMarkChars];
            }

            var offset = session.Writer.Length;
            var id = _database.AddRecordingMark(session.Info.Id, offset, line, _clock());
            var mark = new RecordingMark(id, session.Info.Id, offset, line);
            session.Marks.Add(mark);
            return mark;
        }
    }

    /// <summary>
    /// Stops, finishes the file, and lets the PC sleep again. Returns the saved recording, waiting to be made into
    /// notes, or null when nothing was recording. A recording with no sound in it is deleted.
    /// </summary>
    public RecordingInfo? Stop()
    {
        lock (_lock)
        {
            if (_session is not { } session)
            {
                return null;
            }

            _session = null;
            try
            {
                if (session.Mixer is { } mixer)
                {
                    session.Writer.Write(mixer.Drain());
                }

                session.Writer.Dispose();
            }
            finally
            {
                _keepAwake.Release();
            }

            var length = session.Writer.Length;
            var empty = length < TimeSpan.FromSeconds(1);
            if (empty)
            {
                Delete(session.Info.Path);
            }

            _database.FinishRecording(session.Info.Id, empty ? RecordingStatus.Empty : RecordingStatus.Stopped, _clock(), length.TotalSeconds);
            return empty ? null : _database.GetRecording(session.Info.Id);
        }
    }

    /// <summary>
    /// At start: recordings left unfinished by a crash are fixed and saved as stopped. Returns every recording
    /// waiting to be made into notes, oldest first, so the user can be asked about each.
    /// </summary>
    public IReadOnlyList<RecordingInfo> Recover()
    {
        foreach (var left in _database.GetRecordingsWithStatus(RecordingStatus.Recording).Where(found => found.Id != _session?.Info.Id))
        {
            WavRepair repair;
            try
            {
                repair = WavFile.Repair(left.Path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Still held by something. Tried again next start.
                continue;
            }

            var usable = repair.Usable && repair.Length >= TimeSpan.FromSeconds(1);
            if (!usable)
            {
                Delete(left.Path);
            }

            _database.FinishRecording(left.Id, usable ? RecordingStatus.Stopped : RecordingStatus.Empty, null, repair.Length.TotalSeconds);
        }

        return _database.GetRecordingsWithStatus(RecordingStatus.Stopped)
            .Where(found => found.Id != _session?.Info.Id && File.Exists(found.Path))
            .ToList();
    }

    /// <summary>Gives a stopped recording to the queue, the same way as an imported file. Its marks go with its note.</summary>
    public TranscriptionJob HandOff(RecordingInfo recording, TranscriptionQueue queue)
    {
        var job = queue.ImportRecording(recording.Path, recording.KeepAudio, recording.StartedAt, recording.Title);
        _database.HandOffRecording(recording.Id, job.Id, job.NoteId);
        return job;
    }

    /// <summary>
    /// Deletes the file of each recording whose transcript is saved. A kept copy was made by the queue before that,
    /// so it stays. A recording whose transcribing failed is kept, so it can be tried again.
    /// </summary>
    public int CleanUp()
    {
        var removed = 0;
        foreach (var recording in _database.GetRecordingsWithStatus(RecordingStatus.HandedOff))
        {
            if (recording.JobId is not { } jobId || _database.GetTranscriptionJob(jobId) is not { Status: TranscriptionStatus.Writing or TranscriptionStatus.Done })
            {
                continue;
            }

            if (Delete(recording.Path))
            {
                _database.SetRecordingStatus(recording.Id, RecordingStatus.Done);
                removed++;
            }
        }

        return removed;
    }

    /// <summary>"12:34", or "1:02:03" past an hour.</summary>
    public static string Clock(TimeSpan time) => time.TotalHours >= 1
        ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
        : $"{(int)time.TotalMinutes}:{time.Seconds:00}";

    private static void Write(Session session, ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        session.Level = AudioMath.Peak(samples);
        session.Writer.Write(samples);
    }

    private string FreePath(string title, DateTime now)
    {
        var safe = new string(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is ',' ? ' ' : c).ToArray());
        var stem = $"{string.Join(' ', safe.Split(' ', StringSplitOptions.RemoveEmptyEntries))} {now.ToString("HHmm", CultureInfo.InvariantCulture)}";
        var path = Path.Combine(_folder, stem + ".wav");
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(_folder, $"{stem} ({n}).wav");
        }

        return path;
    }

    private static bool Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Tried again on the next cleanup.
            return false;
        }
    }

    private sealed class Session(RecordingInfo info, RecordingWriter writer, RecordingWatch watch, TwoSourceMixer? mixer, DateTime startedAt)
    {
        public RecordingInfo Info => info;

        public RecordingWriter Writer => writer;

        public RecordingWatch Watch => watch;

        public TwoSourceMixer? Mixer => mixer;

        public DateTime StartedAt => startedAt;

        public List<RecordingMark> Marks { get; } = [];

        private volatile float _level;

        public float Level
        {
            get => _level;
            set => _level = value;
        }
    }
}
