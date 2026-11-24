using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Waypoint.Core.Data;
using Waypoint.Core.Recording;
using Waypoint.Core.Transcription;
using Forms = System.Windows.Forms;

namespace Waypoint.App;

/// <summary>
/// The PC lecture recorder: the start question, the sound devices, the indicator at the top of the window, the
/// Ctrl+Alt+M mark key, the warnings, and handing the finished recording to transcription. Recording only starts
/// from the user choosing it. Everything here runs on the window's thread except the sound itself.
/// </summary>
public sealed class LectureRecorder : IDisposable
{
    public const string MarkKeyName = "Ctrl+Alt+M";

    private const string InputPreference = "record_input";
    private const string KeepAudioPreference = "record_keep_audio";

    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(5);

    private readonly Recorder _core;
    private readonly WaypointDatabase _database;
    private readonly TranscriptionQueue _queue;
    private readonly ItemsWindow _window;
    private readonly string _dataDirectory;
    private readonly Func<string, string, Task> _notify;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private AudioCapture? _capture;
    private GlobalHotkey? _markKey;
    private bool _markKeyWorks;
    private DateTime _lastCheck = DateTime.MinValue;
    private string? _lastMarked;

    /// <param name="notify">The app's usual alert path, for warnings while recording.</param>
    public LectureRecorder(Recorder core, WaypointDatabase database, TranscriptionQueue queue, ItemsWindow window, string dataDirectory, Func<string, string, Task> notify)
    {
        _core = core;
        _database = database;
        _queue = queue;
        _window = window;
        _dataDirectory = dataDirectory;
        _notify = notify;
        _tick.Tick += (_, _) => OnTick();
        window.StopRecording = Stop;
        window.MarkRecording = text => Mark(text);
        queue.Changed += _ => window.Dispatcher.BeginInvoke(CleanUp);
    }

    /// <summary>Raised when recording starts or stops, for the tray.</summary>
    public event Action? StateChanged;

    /// <summary>Raised every few seconds while recording, for the tray's hover text.</summary>
    public event Action? TrayTextChanged;

    public bool IsRecording => _core.IsRecording;

    /// <summary>The tray's hover text while recording.</summary>
    public string TrayText => $"Waypoint: recording {Recorder.Clock(_core.Elapsed)}{(_core.Warnings.Count > 0 ? ". Check the warning" : "")}";

    /// <summary>Shows the start question, with the last choices filled in. Nothing records unless Start recording is chosen.</summary>
    public void AskToStart()
    {
        if (IsRecording)
        {
            _window.ShowNotes();
            return;
        }

        _window.ShowNotes();
        var ask = new RecordWindow(_window, _core.TitleFor(DateTime.Now), LastInput, _database.GetPreference(KeepAudioPreference) == "yes");
        if (ask.ShowDialog() == true)
        {
            _database.SetPreference(InputPreference, ask.Input.ToString());
            _database.SetPreference(KeepAudioPreference, ask.KeepTheAudio ? "yes" : "no");
            StartNow(ask.Input, ask.KeepTheAudio);
        }
    }

    /// <summary>From Record on the class start card, which is itself the user's yes. Uses the last choices.</summary>
    public void StartFromCard()
    {
        _window.ShowNotes();
        StartNow(LastInput, _database.GetPreference(KeepAudioPreference) == "yes");
    }

    public void Stop()
    {
        if (!IsRecording)
        {
            return;
        }

        StopDevices();
        RecordingInfo? stopped;
        try
        {
            stopped = _core.Stop();
        }
        catch (RecordingNotFinishedException exception)
        {
            _window.ShowReply($"The recording stopped, but Waypoint couldn't finish saving its file. {Waypoint.Core.PlainProblem.Report(exception.InnerException ?? exception)} What was saved is offered as notes the next time Waypoint starts.");
            return;
        }
        finally
        {
            _window.HideRecording();
            StateChanged?.Invoke();
        }

        if (stopped is null)
        {
            _window.ShowReply("Nothing was recorded, so there's nothing to make notes from.");
            return;
        }

        HandOff(stopped);
    }

    /// <summary>Stamps the moment. A typed line goes with it.</summary>
    public void Mark(string? text)
    {
        if (_core.Mark(text) is not { } mark)
        {
            return;
        }

        _lastMarked = mark.Text is { } line
            ? $"Marked {Recorder.Clock(mark.Offset)}: {line}"
            : $"Marked {Recorder.Clock(mark.Offset)} as important";
        ShowIndicator();
    }

    /// <summary>
    /// At start: a recording cut off by a crash or power cut is fixed, and each one not made into notes yet is
    /// offered. No is fine, and it's asked about again next time.
    /// </summary>
    public void OfferUnfinished()
    {
        IReadOnlyList<RecordingInfo> waiting;
        try
        {
            _core.TakeBackStopped();
            waiting = _core.Recover();
            _core.CleanUp();
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var recording in waiting)
        {
            var when = recording.StartedAt.ToString("dddd, MMM d 'at' h:mm tt", CultureInfo.CurrentCulture);
            var minutes = Math.Max(1, (int)Math.Round(recording.Length.TotalMinutes));
            if (NoteConfirmWindow.Ask(
                null,
                "Waypoint recording",
                $"{recording.Title} from {when} was recorded but isn't in your notes yet. Make notes from it now?",
                $"{minutes} {(minutes == 1 ? "minute is" : "minutes are")} saved. If you choose Not now, Waypoint asks again next time it starts.",
                "Make notes",
                "Not now"))
            {
                HandOff(recording);
            }
        }
    }

    public void Dispose()
    {
        _tick.Stop();
        StopDevices();

        // Closing while recording keeps what was recorded. It's offered as notes the next time Waypoint starts.
        _core.Stop();
    }

    private AudioInput LastInput => Enum.TryParse<AudioInput>(_database.GetPreference(InputPreference), out var input) ? input : AudioInput.Microphone;

    private void StartNow(AudioInput input, bool keepAudio)
    {
        if (IsRecording)
        {
            return;
        }

        try
        {
            _core.Start(input, keepAudio);
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException)
        {
            _window.ShowReply($"Recording couldn't start, because Waypoint couldn't save the audio file. {Waypoint.Core.PlainProblem.Report(exception)} Check there's space on the disk, then try again.");
            return;
        }

        try
        {
            _capture = new AudioCapture(
                input,
                samples => _core.WriteMicrophone(samples),
                samples => _core.WriteComputerAudio(samples),
                reason => _window.Dispatcher.BeginInvoke(() => OnInputLost(reason)));
            _capture.Start();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            StopDevices();
            _core.Stop();
            var what = input == AudioInput.ComputerAudio ? "the computer's sound" : "the microphone";
            _window.ShowReply($"Recording couldn't start, because Windows wouldn't open {what}. Check that it's plugged in and allowed in Windows privacy settings.");
            Waypoint.Core.PlainProblem.Report(exception);
            return;
        }

        // Told when the PC sleeps and wakes, so the time asleep is left out of the recording.
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _markKey = new GlobalHotkey(() => Mark(null), GlobalHotkey.VkM);
        _markKeyWorks = _markKey.Register();
        _lastMarked = null;
        _lastCheck = DateTime.MinValue;
        _tick.Start();
        ShowIndicator();
        StateChanged?.Invoke();
    }

    private async void HandOff(RecordingInfo recording)
    {
        var keep = recording.KeepAudio;
        if (!_queue.IsReady && !_queue.AgreedToDownload)
        {
            _window.ShowNotes();
            var ask = new AudioImportWindow(_window, [recording.Title], _queue.DownloadBytes);
            ask.KeepAudio.IsChecked = keep;
            if (ask.ShowDialog() != true)
            {
                _window.ShowReply("The recording is saved on this PC. Waypoint will ask about making notes from it the next time it starts.");
                return;
            }

            _queue.AgreeToDownload();
            keep = ask.KeepTheAudio;
        }

        try
        {
            // Its note is made on the server in server mode, so that's sent in the Notes page's order, off the window's thread.
            var job = await _window.NotesPage.Work.Run(() => _core.HandOff(recording with { KeepAudio = keep }, _queue));
            _window.ShowNotes();
            _window.NotesPage.ShowRecordedNote(job.NoteId);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Waypoint.Core.PlainProblem.Report(exception);
            _window.ShowReply("The recording is saved, but notes couldn't be started from it yet. Waypoint will ask again next time it starts.");
        }
    }

    private void CleanUp()
    {
        IReadOnlyList<RecordingInfo> takenBack;
        try
        {
            _core.CleanUp();
            takenBack = _core.TakeBackStopped();
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException)
        {
            // Tried again on the next change.
            return;
        }

        // Transcribing a recording was stopped. The recording is still on the PC, so the user hears where it is and
        // can delete it now, or leave it to be asked about again.
        foreach (var recording in takenBack)
        {
            _window.ShowReply(
                $"Transcribing {recording.Title} stopped. The recording is still saved on this PC, and Waypoint will ask about making notes from it the next time it starts.",
                "Delete the recording",
                () => Discard(recording));
        }
    }

    private void Discard(RecordingInfo recording)
    {
        if (!NoteConfirmWindow.Ask(_window, "Waypoint recording", $"Delete the recording of {recording.Title}?", "It can't be brought back after this. Your notes aren't changed.", "Delete it", "Keep it"))
        {
            return;
        }

        _window.ShowReply(_core.Discard(recording.Id)
            ? $"The recording of {recording.Title} is deleted."
            : $"The recording of {recording.Title} couldn't be deleted right now. Waypoint will ask about it the next time it starts.");
    }

    private void OnTick()
    {
        if (!IsRecording)
        {
            _tick.Stop();
            return;
        }

        var now = DateTime.Now;
        if (now - _lastCheck >= CheckEvery)
        {
            _lastCheck = now;
            foreach (var warning in _core.Check(PowerReadings.Battery(), PowerReadings.FreeBytes(_dataDirectory), _capture?.MicrophoneMuted == true))
            {
                _ = _notify("Recording", RecordingWatch.Describe(warning));
            }

            TrayTextChanged?.Invoke();
        }

        ShowIndicator();
    }

    private void OnInputLost(string reason)
    {
        if (IsRecording && _core.InputLost())
        {
            _ = _notify("Recording", RecordingWatch.Describe(RecordingWarning.InputLost));
            ShowIndicator();
        }
    }

    private void ShowIndicator()
    {
        if (!IsRecording)
        {
            return;
        }

        var warning = _core.Warnings.Select(RecordingWatch.Describe).FirstOrDefault();
        var note = _lastMarked ?? (_markKeyWorks
            ? $"Press {MarkKeyName} anywhere to mark a moment that matters. Keep the lid open."
            : $"Choose Mark to flag a moment that matters. {MarkKeyName} is taken by another app. Keep the lid open.");
        _window.ShowRecording(_core.Elapsed, _core.Level, warning, note);
    }

    /// <summary>From Windows' own thread, so it's handed to the window's, with the time it happened.</summary>
    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        var at = DateTime.Now;
        if (e.Mode == Microsoft.Win32.PowerModes.Suspend)
        {
            _window.Dispatcher.BeginInvoke(() => _core.Suspending(at));
        }
        else if (e.Mode == Microsoft.Win32.PowerModes.Resume)
        {
            _window.Dispatcher.BeginInvoke(() => _core.Resumed(at));
        }
    }

    private void StopDevices()
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _tick.Stop();
        _capture?.Dispose();
        _capture = null;
        _markKey?.Dispose();
        _markKey = null;
    }
}
