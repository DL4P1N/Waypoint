using System.Buffers.Binary;
using System.Text;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Data;
using Waypoint.Core.Notes;
using Waypoint.Core.Recording;
using Waypoint.Core.Schedule;
using Waypoint.Core.Transcription;

namespace Waypoint.Core.Tests;

/// <summary>
/// Recording lectures on this PC. No microphone is ever opened here: sound is fed in as numbers, the clock is
/// faked, and the WAV checks run on in-memory streams or files in a temp folder.
/// </summary>
public sealed class RecorderTests : IDisposable
{
    private static readonly DateTime Tuesday = new(2026, 11, 3, 10, 2, 0);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-recorder-{Guid.NewGuid()}");
    private readonly WaypointDatabase _database;
    private readonly NoKeepAwake _awake = new();
    private DateTime _now = Tuesday;

    public RecorderTests()
    {
        Directory.CreateDirectory(_folder);
        _database = new WaypointDatabase(Path.Combine(_folder, "waypoint.db"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    private static ClassSchedule Schedule() => new(
        new DateOnly(2026, 9, 14),
        new DateOnly(2026, 12, 18),
        [new ClassMeeting("CS 101", "Hall 2", [DayOfWeek.Tuesday, DayOfWeek.Thursday], new TimeOnly(10, 0), new TimeOnly(11, 15))],
        []);

    private Recorder NewRecorder(ClassSchedule? schedule = null) =>
        new(_database, _folder, _awake, schedule, () => _now, TimeSpan.FromSeconds(1));

    private static float[] Tone(int samples, float level = 0.5f) =>
        Enumerable.Range(0, samples).Select(index => index % 2 == 0 ? level : -level).ToArray();

    private static float[] Seconds(double seconds, float level = 0.5f) => Tone((int)(seconds * RecordingWriter.SampleRate), level);

    // The WAV file

    [Fact]
    public void The_writer_keeps_the_header_length_right_at_every_flush()
    {
        using var stream = new MemoryStream();
        var writer = new RecordingWriter(stream, TimeSpan.FromSeconds(1));
        WriteInPieces(writer, 2.5);

        // Flushed at 1 s and 2 s, so the header says 2 s while 2.5 s are in the file.
        Assert.Equal(2 * RecordingWriter.BytesPerSecond, DataLength(stream));
        Assert.Equal(WavFile.HeaderBytes + (long)(2.5 * RecordingWriter.BytesPerSecond), stream.Length);

        writer.Flush();
        Assert.Equal((long)(2.5 * RecordingWriter.BytesPerSecond), DataLength(stream));
        Assert.Equal(TimeSpan.FromSeconds(2.5), writer.Length);
    }

    [Fact]
    public void The_header_is_a_16_kHz_mono_16_bit_wav()
    {
        using var stream = new MemoryStream();
        using (var writer = new RecordingWriter(new KeepOpen(stream)))
        {
            writer.Write([1f, -1f, 2f, 0f]);
        }

        var bytes = stream.ToArray();
        Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(bytes, 8, 4));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(20)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(22)));
        Assert.Equal(16_000, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(34)));
        Assert.Equal(8, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40)));
        Assert.Equal(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44)));
        Assert.Equal(-short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(46)));

        // Too loud is clipped, not wrapped around.
        Assert.Equal(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(48)));
    }

    [Fact]
    public void A_file_cut_off_by_a_crash_gets_its_length_back_and_a_half_sample_is_dropped()
    {
        using var stream = new MemoryStream();
        var writer = new RecordingWriter(stream, TimeSpan.FromSeconds(1));
        WriteInPieces(writer, 1.5);

        // The power went off partway through writing a sample, after the last flush.
        stream.SetLength(stream.Length + 1);
        Assert.Equal(RecordingWriter.BytesPerSecond, DataLength(stream));

        var repair = WavFile.Repair(stream);

        Assert.True(repair.Usable);
        Assert.Equal(TimeSpan.FromSeconds(1.5), repair.Length);
        Assert.Equal((long)(1.5 * RecordingWriter.BytesPerSecond), DataLength(stream));
        Assert.Equal(WavFile.HeaderBytes + repair.DataBytes, stream.Length);
        Assert.Equal(repair.DataBytes + 36, BinaryPrimitives.ReadUInt32LittleEndian(stream.ToArray().AsSpan(4)));
    }

    [Fact]
    public void A_header_that_never_got_written_is_written_again_and_an_empty_file_is_not_usable()
    {
        using var garbled = new MemoryStream(new byte[WavFile.HeaderBytes + 3200]);
        var repair = WavFile.Repair(garbled);
        Assert.True(repair.Usable);
        Assert.Equal(TimeSpan.FromSeconds(0.1), repair.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(garbled.ToArray(), 0, 4));

        using var empty = new MemoryStream(new byte[10]);
        Assert.False(WavFile.Repair(empty).Usable);
        Assert.False(WavFile.Repair(Path.Combine(_folder, "missing.wav")).Usable);
    }

    // Sound in

    [Fact]
    public void Captured_sound_is_turned_into_one_channel_whatever_its_format()
    {
        var floats = new byte[16];
        BinaryPrimitives.WriteSingleLittleEndian(floats.AsSpan(0), 0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(floats.AsSpan(4), -0.25f);
        BinaryPrimitives.WriteSingleLittleEndian(floats.AsSpan(8), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(floats.AsSpan(12), 0f);
        Assert.Equal([0.125f, 0.5f], AudioMath.ToMono(floats, new CaptureFormat(48_000, 2, 32, true)));

        var pcm = new byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(0), 16384);
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(2), -32768);
        Assert.Equal([0.5f, -1f], AudioMath.ToMono(pcm, new CaptureFormat(16_000, 1, 16, false)));
    }

    [Fact]
    public void Going_from_48_kHz_to_16_kHz_averages_each_three_samples_even_across_pieces()
    {
        var resampler = new Resampler(48_000, 16_000);
        var first = resampler.Process([0.3f, 0.6f, 0.9f, 0.3f]);
        var second = resampler.Process([0.3f, 0.3f, 1f, 1f, 1f]);

        Assert.Equal(3, first.Length + second.Length);
        Assert.Equal(0.6f, first[0], 5);
        Assert.Equal(0.3f, second[0], 5);
        Assert.Equal(1f, second[1], 5);
    }

    [Fact]
    public void Going_from_44_1_kHz_keeps_the_length_and_a_steady_level()
    {
        var resampler = new Resampler(44_100, 16_000);
        var output = new List<float>();
        for (var piece = 0; piece < 100; piece++)
        {
            output.AddRange(resampler.Process(Enumerable.Repeat(0.25f, 441).ToArray()));
        }

        Assert.InRange(output.Count, 15_999, 16_000);
        Assert.All(output, sample => Assert.Equal(0.25f, sample, 4));
    }

    [Fact]
    public void Two_sources_are_added_together_and_clipped_once_both_have_sent_sound()
    {
        var mixer = new TwoSourceMixer(maxLagSamples: 100);
        Assert.Empty(mixer.AddFirst([0.25f, 0.5f, 0.75f]));

        var mixed = mixer.AddSecond([0.25f, 0.75f]);

        Assert.Equal([0.5f, 1f], mixed);
        Assert.Equal([0.75f], mixer.Drain());
    }

    [Fact]
    public void When_the_computer_plays_nothing_the_microphone_carries_on_alone()
    {
        var mixer = new TwoSourceMixer(maxLagSamples: 4);

        var mixed = mixer.AddFirst(Enumerable.Repeat(0.5f, 10).ToArray());

        // Up to the lag is held back in case the other side catches up. The rest goes out with silence added.
        Assert.Equal(6, mixed.Length);
        Assert.All(mixed, sample => Assert.Equal(0.5f, sample));
        Assert.Equal(4, mixer.Drain().Length);
    }

    // Warnings

    [Fact]
    public void Low_battery_is_said_once_and_only_when_on_battery()
    {
        var watch = new RecordingWatch(Tuesday, listensToMicrophone: true);
        watch.Heard(0.5f, Tuesday);

        Assert.Empty(watch.Check(Tuesday, new PowerReading(false, 10), null));
        Assert.Equal([RecordingWarning.LowBattery], watch.Check(Tuesday, new PowerReading(true, 20), null));
        Assert.Empty(watch.Check(Tuesday, new PowerReading(true, 15), null));
        Assert.Contains(RecordingWarning.LowBattery, watch.Showing);

        // Plugged in, the warning goes from the indicator, and it doesn't come back as a new alert.
        Assert.Empty(watch.Check(Tuesday, new PowerReading(false, 15), null));
        Assert.Empty(watch.Showing);
        Assert.Empty(watch.Check(Tuesday, new PowerReading(true, 12), null));
        Assert.Contains(RecordingWarning.LowBattery, watch.Showing);
    }

    [Fact]
    public void Low_disk_space_is_said_once_below_a_gigabyte()
    {
        var watch = new RecordingWatch(Tuesday, listensToMicrophone: false);

        Assert.Empty(watch.Check(Tuesday, PowerReading.PluggedIn, 5_000_000_000));
        Assert.Equal([RecordingWarning.LowDisk], watch.Check(Tuesday, PowerReading.PluggedIn, 900_000_000));
        Assert.Empty(watch.Check(Tuesday, PowerReading.PluggedIn, 800_000_000));
    }

    [Fact]
    public void A_microphone_silent_for_a_minute_is_said_once_and_clears_when_it_hears_again()
    {
        var watch = new RecordingWatch(Tuesday, listensToMicrophone: true);
        watch.Heard(0.0005f, Tuesday.AddSeconds(30));

        Assert.Empty(watch.Check(Tuesday.AddSeconds(59), PowerReading.PluggedIn, null));
        Assert.Equal([RecordingWarning.SilentMicrophone], watch.Check(Tuesday.AddSeconds(60), PowerReading.PluggedIn, null));

        watch.Heard(0.2f, Tuesday.AddSeconds(70));
        Assert.Empty(watch.Check(Tuesday.AddSeconds(71), PowerReading.PluggedIn, null));
        Assert.Empty(watch.Showing);
        Assert.Empty(watch.Check(Tuesday.AddSeconds(200), PowerReading.PluggedIn, null));
        Assert.Contains(RecordingWarning.SilentMicrophone, watch.Showing);
    }

    [Fact]
    public void Silence_is_normal_for_computer_audio_but_a_muted_or_lost_input_is_said()
    {
        var computer = new RecordingWatch(Tuesday, listensToMicrophone: false);
        Assert.Empty(computer.Check(Tuesday.AddMinutes(10), PowerReading.PluggedIn, null, microphoneMuted: true));

        var microphone = new RecordingWatch(Tuesday, listensToMicrophone: true);
        Assert.Equal([RecordingWarning.MicrophoneMuted], microphone.Check(Tuesday.AddMinutes(5), PowerReading.PluggedIn, null, microphoneMuted: true));
        Assert.True(microphone.InputLost());
        Assert.False(microphone.InputLost());
        Assert.All(Enum.GetValues<RecordingWarning>(), warning => Assert.DoesNotContain("—", RecordingWatch.Describe(warning)));
    }

    // The recorder

    [Fact]
    public void Recording_keeps_the_PC_awake_saves_the_recording_first_and_lets_it_sleep_after()
    {
        var recorder = NewRecorder();
        var started = recorder.Start(AudioInput.Microphone, keepAudio: false);

        Assert.True(_awake.Held);
        Assert.Equal(RecordingStatus.Recording, _database.GetRecording(started.Id)!.Status);
        Assert.True(File.Exists(started.Path));
        Assert.Equal("Recording, Tue Nov 3", started.Title);
        Assert.Throws<InvalidOperationException>(() => recorder.Start(AudioInput.Microphone, false));

        recorder.WriteMicrophone(Seconds(3));
        recorder.WriteComputerAudio(Seconds(5));
        Assert.Equal(TimeSpan.FromSeconds(3), recorder.Elapsed);
        Assert.Equal(0.5f, recorder.Level);

        var stopped = recorder.Stop()!;

        Assert.False(_awake.Held);
        Assert.False(recorder.IsRecording);
        Assert.Equal(RecordingStatus.Stopped, stopped.Status);
        Assert.Equal(3, stopped.DurationSeconds);
        Assert.Equal(TimeSpan.FromSeconds(3), WavFile.Repair(stopped.Path).Length);
        Assert.Null(recorder.Stop());
    }

    [Fact]
    public void Asking_to_record_names_the_class_only_when_one_is_on()
    {
        Assert.Null(NewRecorder().ClassTitleFor(Tuesday));
        Assert.Equal("CS 101, Tue Nov 3", NewRecorder(Schedule()).ClassTitleFor(Tuesday));
    }

    [Fact]
    public void A_recording_started_in_class_is_titled_with_the_course_and_date()
    {
        var recorder = NewRecorder(Schedule());

        var started = recorder.Start(AudioInput.Microphone, keepAudio: true);

        Assert.Equal("CS 101, Tue Nov 3", started.Title);
        Assert.Equal("CS 101 Tue Nov 3 1002.wav", Path.GetFileName(started.Path));
        Assert.True(started.KeepAudio);
        recorder.Stop();
    }

    [Fact]
    public void A_recording_with_no_sound_is_deleted_on_stop()
    {
        var recorder = NewRecorder();
        var started = recorder.Start(AudioInput.Microphone, false);

        Assert.Null(recorder.Stop());

        Assert.False(File.Exists(started.Path));
        Assert.Equal(RecordingStatus.Empty, _database.GetRecording(started.Id)!.Status);
        Assert.False(_awake.Held);
    }

    [Fact]
    public void Marks_are_stamped_at_the_point_reached_in_the_audio_and_saved_straight_away()
    {
        var recorder = NewRecorder();
        var started = recorder.Start(AudioInput.Microphone, false);
        recorder.WriteMicrophone(Seconds(2));
        _now = _now.AddMinutes(5);

        var plain = recorder.Mark()!;
        recorder.WriteMicrophone(Seconds(1.5));
        var typed = recorder.Mark("  know this proof  ")!;

        Assert.Equal(TimeSpan.FromSeconds(2), plain.Offset);
        Assert.Null(plain.Text);
        Assert.Equal(TimeSpan.FromSeconds(3.5), typed.Offset);
        Assert.Equal("know this proof", typed.Text);
        Assert.Equal([plain, typed], _database.GetRecordingMarks(started.Id));
        Assert.Equal(2, recorder.Marks.Count);
        Assert.Equal(Recorder.MaxMarkChars, recorder.Mark(new string('a', 400))!.Text!.Length);

        recorder.Stop();
        Assert.Null(recorder.Mark());
    }

    [Fact]
    public void Both_inputs_are_mixed_into_one_recording()
    {
        var recorder = NewRecorder();
        recorder.Start(AudioInput.Both, false);

        // Each side sends small pieces on its own thread, the way Windows does.
        for (var piece = 0; piece < 10; piece++)
        {
            recorder.WriteMicrophone(Seconds(0.1, 0.25f));
            recorder.WriteComputerAudio(Seconds(0.1, 0.25f));
        }

        var stopped = recorder.Stop()!;

        Assert.Equal(1, stopped.DurationSeconds);
        var bytes = File.ReadAllBytes(stopped.Path);
        Assert.Equal((short)Math.Round(0.5 * short.MaxValue), BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(WavFile.HeaderBytes)));
    }

    [Fact]
    public async Task With_computer_audio_only_a_gap_with_no_sound_sent_is_filled_so_times_stay_true()
    {
        var recorder = NewRecorder();
        recorder.Start(AudioInput.ComputerAudio, false);
        recorder.WriteComputerAudio(Seconds(1));

        _now = _now.AddSeconds(10);
        recorder.Check(PowerReading.PluggedIn, null);
        await recorder.SilenceFilled;

        Assert.Equal(TimeSpan.FromSeconds(10), recorder.Elapsed);
        recorder.Stop();
    }

    [Fact]
    public async Task Time_the_PC_spent_asleep_is_left_out_instead_of_filled_with_silence()
    {
        var recorder = NewRecorder();
        recorder.Start(AudioInput.ComputerAudio, false);
        recorder.WriteComputerAudio(Seconds(1));
        _now = _now.AddSeconds(5);
        recorder.Check(PowerReading.PluggedIn, null);
        await recorder.SilenceFilled;
        Assert.Equal(TimeSpan.FromSeconds(5), recorder.Elapsed);

        // The lid was shut for the night, so no check came, and none of that time is written.
        _now = _now.AddHours(8);
        recorder.Check(PowerReading.PluggedIn, null);
        await recorder.SilenceFilled;
        Assert.Equal(TimeSpan.FromSeconds(5), recorder.Elapsed);

        // Once it's awake again, gaps are filled as before.
        _now = _now.AddSeconds(5);
        recorder.Check(PowerReading.PluggedIn, null);
        var stopped = recorder.Stop()!;

        Assert.Equal(10, stopped.DurationSeconds);
    }

    [Fact]
    public async Task Only_the_time_windows_says_it_slept_is_left_out()
    {
        var recorder = NewRecorder();
        recorder.Start(AudioInput.ComputerAudio, false);
        recorder.WriteComputerAudio(Seconds(1));
        _now = _now.AddSeconds(5);
        recorder.Check(PowerReading.PluggedIn, null);
        await recorder.SilenceFilled;

        // Asleep for two hours, three seconds after that check, and awake again for four before the next one.
        recorder.Suspending(_now.AddSeconds(3));
        recorder.Resumed(_now.AddSeconds(3).AddHours(2));
        _now = _now.AddSeconds(7).AddHours(2);
        recorder.Check(PowerReading.PluggedIn, null);
        var stopped = recorder.Stop()!;

        Assert.Equal(12, stopped.DurationSeconds);
    }

    [Fact]
    public async Task A_long_wait_between_checks_keeps_the_time_that_sound_came_in()
    {
        var recorder = NewRecorder();
        recorder.Start(AudioInput.ComputerAudio, false);
        recorder.WriteComputerAudio(Seconds(1));
        _now = _now.AddSeconds(5);
        recorder.Check(PowerReading.PluggedIn, null);
        await recorder.SilenceFilled;

        // The window was busy for 75 seconds, with 70 of them playing sound, so only 5 can have been asleep.
        recorder.WriteComputerAudio(Seconds(70));
        _now = _now.AddSeconds(75);
        recorder.Check(PowerReading.PluggedIn, null);
        await recorder.SilenceFilled;

        Assert.Equal(TimeSpan.FromSeconds(75), recorder.Elapsed);
        recorder.Stop();
    }

    [Fact]
    public void Waking_up_doesnt_say_the_microphone_heard_nothing()
    {
        var recorder = NewRecorder();
        recorder.Start(AudioInput.Microphone, false);
        recorder.WriteMicrophone(Seconds(1));
        _now = _now.AddSeconds(5);
        Assert.Empty(recorder.Check(PowerReading.PluggedIn, null));

        recorder.Suspending(_now.AddSeconds(1));
        recorder.Resumed(_now.AddHours(3));
        _now = _now.AddHours(3).AddSeconds(5);
        Assert.Empty(recorder.Check(PowerReading.PluggedIn, null));

        // Asleep with no word from Windows, found from the long wait with no sound coming in.
        _now = _now.AddHours(5);
        Assert.Empty(recorder.Check(PowerReading.PluggedIn, null));

        // Still said when it's really quiet while awake, with the microphone sending sound too faint to count.
        recorder.WriteMicrophone(Seconds(70, 0.0001f));
        _now = _now.AddSeconds(70);
        Assert.Equal([RecordingWarning.SilentMicrophone], recorder.Check(PowerReading.PluggedIn, null));
        recorder.Stop();
    }

    [Fact]
    public void Silence_is_only_written_for_what_sound_since_hasnt_already_covered()
    {
        using var stream = new MemoryStream();
        var writer = new RecordingWriter(stream, TimeSpan.FromSeconds(1));
        writer.Write(Seconds(1));
        var gap = RecordingWriter.SampleRate / 2;

        // Sound came in after the check worked out the gap and before the silence went in.
        writer.Write(Seconds(2.8));
        Assert.Equal(0, writer.FillSilenceTo(4L * RecordingWriter.SampleRate, gap));
        Assert.Equal(TimeSpan.FromSeconds(3.8), writer.Length);

        Assert.Equal(2L * RecordingWriter.SampleRate + 3200, writer.FillSilenceTo(6L * RecordingWriter.SampleRate, gap));
        Assert.Equal(TimeSpan.FromSeconds(6), writer.Length);
    }

    [Fact]
    public void A_long_stretch_of_silence_is_written_a_second_at_a_time()
    {
        using var stream = new LargestWrite();
        var writer = new RecordingWriter(stream, TimeSpan.FromSeconds(1));

        writer.WriteSilence(90L * RecordingWriter.SampleRate + 3);
        writer.Write([0.5f]);
        writer.Flush();

        Assert.Equal(90L * RecordingWriter.SampleRate + 4, writer.SamplesWritten);
        Assert.Equal((90L * RecordingWriter.SampleRate + 4) * 2, DataLength(stream));
        Assert.InRange(stream.Largest, 1, RecordingWriter.BytesPerSecond);
        var bytes = stream.ToArray();
        Assert.All(bytes.AsSpan(WavFile.HeaderBytes, bytes.Length - WavFile.HeaderBytes - 2).ToArray(), value => Assert.Equal(0, value));
        Assert.Equal((short)Math.Round(0.5 * short.MaxValue), BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(bytes.Length - 2)));
    }

    [Fact]
    public void A_last_flush_that_fails_still_lets_go_of_the_file_and_says_so()
    {
        var stream = new FailingFlush();
        var writer = new RecordingWriter(stream, TimeSpan.FromSeconds(1));
        writer.Write(Seconds(0.5));
        stream.Fail = true;

        Assert.Throws<IOException>(writer.Dispose);

        Assert.True(stream.Closed);
        writer.Dispose();
        writer.Write(Seconds(0.5));
    }

    [Fact]
    public void A_recording_whose_last_flush_fails_is_still_stopped_with_what_reached_the_disk()
    {
        FailingFile? file = null;
        var recorder = new Recorder(_database, _folder, _awake, null, () => _now, TimeSpan.FromSeconds(1),
            path => new RecordingWriter(file = new FailingFile(path), TimeSpan.FromSeconds(1)));
        var started = recorder.Start(AudioInput.Microphone, false);
        recorder.WriteMicrophone(Seconds(1));
        recorder.WriteMicrophone(Seconds(1));
        recorder.WriteMicrophone(Seconds(0.5));

        // The disk filled up just as it stopped.
        file!.Fail = true;
        var stopped = recorder.Stop()!;

        Assert.False(_awake.Held);
        Assert.False(recorder.IsRecording);
        Assert.Equal(RecordingStatus.Stopped, stopped.Status);
        Assert.Equal(2.5, stopped.DurationSeconds);
        Assert.Equal(TimeSpan.FromSeconds(2.5), WavFile.Repair(started.Path).Length);

        // Not held open, so it can be transcribed, and isn't left recording for the next start to find.
        using (File.Open(started.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
        }

        Assert.Empty(_database.GetRecordingsWithStatus(RecordingStatus.Recording));
    }

    [Fact]
    public async Task A_recording_whose_transcribing_is_stopped_is_offered_again_and_can_be_deleted()
    {
        using var queue = new TranscriptionQueue(_database, new NoteMaker(_database, new MarkReadingWriter(), null), ReadySetup(), new CopyConverter(), new TranscriptWhisper(), _folder, () => _now);
        var recorder = NewRecorder();
        recorder.Start(AudioInput.Microphone, keepAudio: false);
        recorder.WriteMicrophone(Seconds(3));
        var stopped = recorder.Stop()!;
        var job = recorder.HandOff(stopped, queue);

        Assert.Empty(recorder.TakeBackStopped());
        Assert.True(queue.Cancel(job.Id));
        await queue.RunPendingAsync();

        var back = Assert.Single(recorder.TakeBackStopped());
        Assert.Equal(RecordingStatus.Stopped, back.Status);
        Assert.Null(back.JobId);
        Assert.True(File.Exists(back.Path));
        Assert.Empty(recorder.TakeBackStopped());
        Assert.Equal(stopped.Id, Assert.Single(recorder.Recover()).Id);

        Assert.True(recorder.Discard(back.Id));

        Assert.False(File.Exists(back.Path));
        Assert.Empty(recorder.Recover());
        Assert.False(recorder.Discard(back.Id));
    }

    [Fact]
    public void Working_files_left_by_a_copy_cut_off_before_are_swept_when_transcribing_starts_but_kept_copies_stay()
    {
        var audio = Path.Combine(_folder, "audio");
        var work = Path.Combine(_folder, "whisper", "work");
        Directory.CreateDirectory(audio);
        Directory.CreateDirectory(work);
        var old = DateTime.UtcNow.AddHours(-3);
        string Made(string folder, string name, DateTime written)
        {
            var path = Path.Combine(folder, name);
            File.WriteAllText(path, "made up");
            File.SetLastWriteTimeUtc(path, written);
            return path;
        }

        var cutOff = Made(audio, "2026-11-03 1002 lecture.m4a.part", old);
        var copying = Made(audio, "2026-11-03 1100 talk.m4a.part", DateTime.UtcNow);
        var kept = Made(audio, "2026-11-03 1002 week 3.part 2.m4a", old);
        var converting = Made(work, "job-4.part.wav", old);
        var compressing = Made(work, "2026-11-03 1002 lecture.part.m4a", old);
        var converted = Made(work, "job-5.wav", old);

        using (var queue = new TranscriptionQueue(_database, new NoteMaker(_database, new MarkReadingWriter(), null), ReadySetup(), new CopyConverter(), new TranscriptWhisper(), _folder, () => _now))
        {
            queue.Start();
        }

        Assert.False(File.Exists(cutOff));
        Assert.False(File.Exists(converting));
        Assert.False(File.Exists(compressing));
        Assert.True(File.Exists(copying));
        Assert.True(File.Exists(kept));
        Assert.True(File.Exists(converted));
    }

    [Fact]
    public void Stopping_once_the_transcript_is_saved_finishes_the_recording_instead_of_offering_it_again()
    {
        using var queue = new TranscriptionQueue(_database, new NoteMaker(_database, new MarkReadingWriter(), null), ReadySetup(), new CopyConverter(), new TranscriptWhisper(), _folder, () => _now);
        var recorder = NewRecorder();
        recorder.Start(AudioInput.Microphone, keepAudio: false);
        recorder.WriteMicrophone(Seconds(3));
        var stopped = recorder.Stop()!;
        var job = recorder.HandOff(stopped, queue);
        _database.AddTranscriptSource(job.NoteId, "Transcript of the lecture", "Recursion calls itself.", null, _now);
        Assert.True(_database.SaveTranscriptionJob(_database.GetTranscriptionJob(job.Id)! with { Status = TranscriptionStatus.Writing, Progress = 100 }, _now));

        Assert.True(queue.Cancel(job.Id));

        Assert.Equal(TranscriptionStatus.Done, _database.GetTranscriptionJob(job.Id)!.Status);
        Assert.Empty(recorder.TakeBackStopped());
        Assert.Equal(1, recorder.CleanUp());
        Assert.False(File.Exists(stopped.Path));
        Assert.Equal(RecordingStatus.Done, _database.GetRecording(stopped.Id)!.Status);
        Assert.NotNull(_database.GetNote(job.NoteId));
    }

    [Fact]
    public void A_job_stopped_just_as_its_transcript_was_saved_is_cleaned_up_not_offered_again()
    {
        using var queue = new TranscriptionQueue(_database, new NoteMaker(_database, new MarkReadingWriter(), null), ReadySetup(), new CopyConverter(), new TranscriptWhisper(), _folder, () => _now);
        var recorder = NewRecorder();
        recorder.Start(AudioInput.Microphone, keepAudio: false);
        recorder.WriteMicrophone(Seconds(3));
        var stopped = recorder.Stop()!;
        var job = recorder.HandOff(stopped, queue);
        _database.AddTranscriptSource(job.NoteId, "Transcript of the lecture", "Recursion calls itself.", null, _now);
        Assert.True(_database.SaveTranscriptionJob(_database.GetTranscriptionJob(job.Id)! with { Status = TranscriptionStatus.Canceled }, _now));

        Assert.Empty(recorder.TakeBackStopped());

        Assert.False(File.Exists(stopped.Path));
        Assert.Equal(RecordingStatus.Done, _database.GetRecording(stopped.Id)!.Status);
        Assert.Empty(recorder.Recover());
    }

    [Fact]
    public void A_hand_off_that_cant_be_saved_leaves_no_job_or_empty_note_and_the_recording_still_waiting()
    {
        using var queue = new TranscriptionQueue(_database, new NoteMaker(_database, new MarkReadingWriter(), null), ReadySetup(), new CopyConverter(), new TranscriptWhisper(), _folder, () => _now);
        var recorder = NewRecorder();
        recorder.Start(AudioInput.Microphone, keepAudio: false);
        recorder.WriteMicrophone(Seconds(3));
        var stopped = recorder.Stop()!;
        FailWhen($"BEFORE UPDATE OF status ON recordings WHEN NEW.status = '{RecordingStatus.HandedOff}'");

        Assert.ThrowsAny<SqliteException>(() => recorder.HandOff(stopped, queue));

        Assert.Equal(0, Count("transcription_jobs"));
        Assert.Empty(_database.GetNotes());
        Assert.Equal(RecordingStatus.Stopped, _database.GetRecording(stopped.Id)!.Status);
        Assert.Equal(stopped.Id, Assert.Single(recorder.Recover()).Id);
    }

    [Fact]
    public void The_recorder_raises_each_warning_once_and_shows_it_while_it_lasts()
    {
        var recorder = NewRecorder();
        recorder.Start(AudioInput.Microphone, false);
        recorder.WriteMicrophone(Seconds(1));

        Assert.Equal([RecordingWarning.LowBattery], recorder.Check(new PowerReading(true, 9), 10_000_000_000));
        Assert.Empty(recorder.Check(new PowerReading(true, 8), 10_000_000_000));
        Assert.Equal([RecordingWarning.LowBattery], recorder.Warnings);

        recorder.Stop();
        Assert.Empty(recorder.Check(new PowerReading(true, 8), 1));
        Assert.Empty(recorder.Warnings);
    }

    [Fact]
    public void A_recording_left_by_a_crash_is_fixed_on_the_next_start_and_offered()
    {
        // What a crash leaves: the row still says recording, and the header is behind what's in the file.
        var id = _database.AddRecording(AudioInput.Microphone, false, "CS 101, Tue Nov 3", Tuesday);
        var path = Path.Combine(_folder, "recordings", "crashed.wav");
        _database.SetRecordingPath(id, path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite))
        {
            var writer = new RecordingWriter(stream, TimeSpan.FromSeconds(1));
            WriteInPieces(writer, 2.25);

            // No Dispose and no last flush, the same as the process ending suddenly.
            stream.Flush();
            Assert.Equal(2 * RecordingWriter.BytesPerSecond, DataLength(stream));
        }

        var empty = _database.AddRecording(AudioInput.Microphone, false, "Nothing", Tuesday);
        _database.SetRecordingPath(empty, Path.Combine(_folder, "recordings", "never-written.wav"));

        var waiting = NewRecorder().Recover();

        var found = Assert.Single(waiting);
        Assert.Equal(id, found.Id);
        Assert.Equal(RecordingStatus.Stopped, found.Status);
        Assert.Equal(2.25, found.DurationSeconds);
        Assert.Equal(TimeSpan.FromSeconds(2.25), WavFile.Repair(path).Length);
        Assert.Equal(RecordingStatus.Empty, _database.GetRecording(empty)!.Status);

        // Still offered next time until it's made into notes.
        Assert.Single(NewRecorder().Recover());
    }

    [Fact]
    public async Task On_stop_it_goes_to_the_queue_filed_under_the_class_and_the_file_goes_once_the_transcript_is_saved()
    {
        var schedule = Schedule();
        var writer = new MarkReadingWriter();
        var maker = new NoteMaker(_database, writer, schedule);
        var whisper = new TranscriptWhisper();
        using var queue = new TranscriptionQueue(_database, maker, ReadySetup(), new CopyConverter(), whisper, _folder, () => _now);
        var recorder = NewRecorder(schedule);

        recorder.Start(AudioInput.Microphone, keepAudio: false);
        recorder.WriteMicrophone(Seconds(20));
        recorder.Mark();
        recorder.WriteMicrophone(Seconds(5));
        recorder.Mark("the base case matters");

        // Class is over by the time it stops, and the note still goes in the class's folder.
        _now = Tuesday.AddHours(2);
        var stopped = recorder.Stop()!;
        var job = recorder.HandOff(stopped, queue);

        var note = _database.GetNote(job.NoteId)!;
        Assert.Equal("CS 101, Tue Nov 3", note.Title);
        Assert.Equal("CS 101", _database.GetNoteCourse(job.NoteId));
        Assert.Equal(RecordingStatus.HandedOff, _database.GetRecording(stopped.Id)!.Status);
        Assert.Equal(2, _database.GetRecordingMarksForNote(job.NoteId).Count);
        Assert.Empty(recorder.Recover());

        Assert.Equal(0, recorder.CleanUp());
        Assert.True(File.Exists(stopped.Path));

        await queue.RunPendingAsync();

        Assert.Equal(1, recorder.CleanUp());
        Assert.False(File.Exists(stopped.Path));
        Assert.Equal(RecordingStatus.Done, _database.GetRecording(stopped.Id)!.Status);

        var marks = writer.Marks!;
        Assert.Contains("At 0:20, the student marked this as important. Around then the lecture said: \"Recursion calls itself.\"", marks);
        Assert.Contains("At 0:25, the student marked this and wrote: \"the base case matters\".", marks);
    }

    [Fact]
    public void Marks_reach_the_prompt_inside_marks_tags_as_data()
    {
        var context = new NoteWriteContext("CS 101", null, Tuesday, "- At 0:20, the student marked this as important.");

        var input = NoteWriter.BuildInput(context, "<source name=\"a\" kind=\"transcript\">x</source>", null);

        Assert.Contains("<marks>\n- At 0:20, the student marked this as important.\n</marks>", input);
        Assert.Contains("<marks>", NoteWriter.SystemPrompt);
        Assert.DoesNotContain("<marks>", NoteWriter.BuildInput(context with { Marks = null }, "x", null));
    }

    [Fact]
    public void Quotes_for_marks_come_from_whisper_times_and_are_kept_short()
    {
        var json = """
            {"transcription":[
              {"timestamps":{"from":"00:00:00,000","to":"00:00:05,000"},"text":" Way before."},
              {"timestamps":{"from":"00:01:00,000","to":"00:01:05,000"},"text":" Just before."},
              {"offsets":{"from":66000,"to":68000},"text":" Right at it."},
              {"offsets":{"from":90000,"to":95000},"text":" Long after."}
            ]}
            """;
        var lines = MarkNotes.ReadLines(json);

        Assert.Equal("Just before. Right at it.", MarkNotes.Around(lines, TimeSpan.FromSeconds(67)));
        Assert.Empty(MarkNotes.ReadLines("not json"));

        var long_ = new List<(TimeSpan, TimeSpan, string)> { (TimeSpan.Zero, TimeSpan.FromSeconds(10), string.Join(" ", Enumerable.Repeat("word", 200))) };
        Assert.InRange(MarkNotes.Around(long_, TimeSpan.FromSeconds(5)).Length, 300, MarkNotes.MaxQuoteChars);

        var described = MarkNotes.Describe([new RecordingMark(1, 1, TimeSpan.FromSeconds(3725), null)], null)!;
        Assert.Equal("- At 1:02:05, the student marked this as important.", described);
        Assert.Null(MarkNotes.Describe([], json));
    }

    // Asking at class start

    [Fact]
    public void The_class_start_question_is_off_by_default_and_asks_once_per_class_in_its_first_minutes()
    {
        var offer = new ClassStartOffer(Schedule());
        var start = new DateTime(2026, 11, 3, 10, 0, 0);

        Assert.False(ClassStartOffer.IsOn(_database.GetPreference(ClassStartOffer.Preference)));
        Assert.Null(offer.Check(start, turnedOn: false, recording: false));
        Assert.Null(offer.Check(start.AddMinutes(-1), turnedOn: true, recording: false));
        Assert.Null(offer.Check(start.AddMinutes(1), turnedOn: true, recording: true));
        Assert.Equal("CS 101", offer.Check(start.AddMinutes(1), turnedOn: true, recording: false)!.Meeting.Course);
        Assert.Null(offer.Check(start.AddMinutes(2), turnedOn: true, recording: false));
        Assert.Null(new ClassStartOffer(Schedule()).Check(start.AddMinutes(30), turnedOn: true, recording: false));
        Assert.Null(new ClassStartOffer(null).Check(start, turnedOn: true, recording: false));
    }

    [Fact]
    public void Nothing_in_the_recorder_can_close_shut_down_or_send_anything_off_the_PC()
    {
        var source = Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", "..", "src", "Waypoint.Core", "Recording");
        var code = string.Join("\n", Directory.GetFiles(source, "*.cs").Select(File.ReadAllText));

        Assert.Contains("class Recorder", code);
        Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"Process\.|\.Kill\(|LockWorkStation|ExitWindows|Shutdown|HttpClient|WebRequest"), code);
    }

    private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    private static void WriteInPieces(RecordingWriter writer, double seconds, double piece = 0.25)
    {
        for (var done = 0.0; done < seconds - 1e-9; done += piece)
        {
            writer.Write(Seconds(Math.Min(piece, seconds - done)));
        }
    }

    private static long DataLength(Stream stream)
    {
        var header = new byte[WavFile.HeaderBytes];
        var at = stream.Position;
        stream.Position = 0;
        stream.ReadExactly(header);
        stream.Position = at;
        return BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(40));
    }

    private WhisperSetup ReadySetup()
    {
        var bin = Path.Combine(_folder, "whisper", "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, WhisperSetup.CliName), "fake");
        var model = Path.Combine(_folder, "whisper", "model.bin");
        File.WriteAllText(model, "model");
        return new WhisperSetup(model, bin);
    }

    private void FailWhen(string when)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TRIGGER fail_partway {when} BEGIN SELECT RAISE(ABORT, 'made to fail partway'); END";
        command.ExecuteNonQuery();
    }

    private long Count(string table)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>Remembers the most written at once.</summary>
    private sealed class LargestWrite : MemoryStream
    {
        public int Largest { get; private set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Largest = Math.Max(Largest, count);
            base.Write(buffer, offset, count);
        }
    }

    /// <summary>A stream whose flush fails once told to, the way a full disk does.</summary>
    private sealed class FailingFlush : MemoryStream
    {
        public bool Fail { get; set; }

        public bool Closed { get; private set; }

        public override void Flush()
        {
            if (Fail)
            {
                throw new IOException("Made-up full disk.");
            }

            base.Flush();
        }

        protected override void Dispose(bool disposing)
        {
            Closed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>A real file, unbuffered so every write reaches it, whose flush to the disk fails once told to.</summary>
    private sealed class FailingFile(string path) : FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, bufferSize: 0)
    {
        public bool Fail { get; set; }

        public override void Flush(bool flushToDisk)
        {
            if (Fail)
            {
                throw new IOException("Made-up full disk.");
            }

            base.Flush(flushToDisk);
        }
    }

    /// <summary>A stream that stays readable after the writer closes it.</summary>
    private sealed class KeepOpen(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
        }
    }

    private sealed class CopyConverter : IAudioConverter
    {
        public Task<TimeSpan> ConvertAsync(string inputPath, string wavPath, CancellationToken cancellationToken)
        {
            File.Copy(inputPath, wavPath, overwrite: true);
            return Task.FromResult(WavFile.Repair(wavPath).Length);
        }
    }

    private sealed class TranscriptWhisper : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, Action<string> onLine, CancellationToken cancellationToken)
        {
            var output = spec.Arguments[spec.Arguments.ToList().IndexOf("-of") + 1];
            File.WriteAllText(output + ".txt", " Recursion calls itself.\n Every call needs a base case.\n");
            File.WriteAllText(output + ".json", """
                {"transcription":[
                  {"offsets":{"from":15000,"to":19000},"text":" Recursion calls itself."},
                  {"offsets":{"from":60000,"to":64000},"text":" Later."}
                ]}
                """);
            return Task.FromResult(new ProcessResult(0, ""));
        }
    }

    private sealed class MarkReadingWriter : INoteWriter
    {
        public string? Marks { get; private set; }

        public Task<WrittenNotes> WriteAsync(IReadOnlyList<NoteSource> sources, NoteWriteContext context, CancellationToken cancellationToken = default)
        {
            Marks = context.Marks;
            return Task.FromResult(new WrittenNotes("# Notes", []));
        }
    }
}
