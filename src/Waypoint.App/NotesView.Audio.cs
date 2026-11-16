using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Waypoint.Core.Notes;
using Waypoint.Core.Transcription;

namespace Waypoint.App;

/// <summary>
/// A transcript in Sources, line by line with times. When the note's audio was kept, clicking a line plays from
/// that moment. When it wasn't, the times still show, and the transcript says once why they can't be played.
/// Keeping or deleting the audio is a choice on the note.
/// </summary>
public partial class NotesView
{
    private const string KeepAudioText = "Keep this note's audio";
    private const string DeleteAudioText = "Delete this note's audio";

    private readonly DispatcherTimer _playbackClock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private INoteAudio? _audio;
    private TranscriptPlayer? _player;
    private SourceRow? _playingRow;
    private long? _playingNote;
    private bool _audioBusy;

    /// <summary>The rows for Sources. A transcript with times shows as lines, and the reason it can't play is given once.</summary>
    private List<SourceRow> SourceRows(long noteId, IReadOnlyList<NoteSource> sources)
    {
        var recordings = _audio?.For(noteId) ?? [];
        var explained = false;
        var rows = new List<SourceRow>();
        foreach (var source in sources)
        {
            var recording = recordings.FirstOrDefault(found => found.SourceId == source.Id);
            string? why = null;
            if (recording is { Lines.Count: > 0, IsKept: false } && !explained)
            {
                explained = true;
                why = recording.CanKeep
                    ? $"This note's audio wasn't kept, so the lines can't be played. Choose {KeepAudioText} below to play them."
                    : "This note's audio wasn't kept, and the original recording isn't on this PC anymore, so the lines can't be played.";
            }

            rows.Add(new SourceRow(source, recording, why));
        }

        return rows;
    }

    /// <summary>The note-level buttons for audio and studying. Playing stops when another note opens.</summary>
    private void ShowNoteActions(long? noteId)
    {
        if (noteId != _playingNote)
        {
            StopPlayback();
        }

        var recordings = noteId is { } id && _audio is { } audio ? audio.For(id) : [];
        if (recordings.Any(recording => recording.IsKept))
        {
            AudioButton.Content = DeleteAudioText;
            AudioButton.ToolTip = "Delete Waypoint's copy of the recording. The transcript and notes stay.";
            AudioButton.Visibility = Visibility.Visible;
        }
        else if (recordings.Any(recording => recording.CanKeep))
        {
            AudioButton.Content = KeepAudioText;
            AudioButton.ToolTip = "Keep a copy of the recording, so clicking a transcript line plays that moment";
            AudioButton.Visibility = Visibility.Visible;
        }
        else
        {
            AudioButton.Visibility = Visibility.Collapsed;
        }

        AudioButton.IsEnabled = !_audioBusy;
        ShowStudyActions(noteId);
    }

    /// <summary>Asks before a note's kept audio is deleted, titled for that, with keeping it as the default.</summary>
    public static NoteConfirmWindow AskDeleteAudio(Window? owner) => new(
        owner,
        "Delete this note's audio?",
        "The transcript and notes stay. Transcript lines won't play anymore. Your own copy of the recording, wherever you imported it from, isn't touched.",
        "Delete audio",
        "Keep the audio",
        "Delete audio");

    private async void OnAudioClicked(object sender, RoutedEventArgs e)
    {
        if (_draft.Id is not { } noteId || _audio is not { } audio || _audioBusy)
        {
            return;
        }

        NoteAudioResult result;
        if (Equals(AudioButton.Content, DeleteAudioText))
        {
            if (AskDeleteAudio(Window.GetWindow(this)).ShowDialog() != true)
            {
                return;
            }

            StopPlayback();
            result = audio.Delete(noteId);
        }
        else
        {
            _audioBusy = true;
            AudioButton.IsEnabled = false;
            Bar.Show("Keeping this note's audio...");
            try
            {
                result = await Task.Run(() => audio.KeepAsync(noteId));
            }
            catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException)
            {
                result = new NoteAudioResult(false, $"The audio couldn't be kept. {Waypoint.Core.PlainProblem.Report(exception)} Try again in a moment.");
            }
            finally
            {
                _audioBusy = false;
            }
        }

        Bar.Show(result.Message);
        if (_draft.Id == noteId)
        {
            _sourcesShown = "";
            ShowNoteExtras();
        }
    }

    private void OnLineClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LineRow { Playable: true } line || line.Owner.AudioPath is not { } path)
        {
            return;
        }

        if (_player is null)
        {
            _player = new TranscriptPlayer();
            _player.Ended += () => Dispatcher.BeginInvoke(StopPlayback);
            _playbackClock.Tick += (_, _) => ShowPlayback();
        }

        try
        {
            _player.PlayFrom(path, length => TranscriptLines.SeekTarget(line.Line, length));
        }
        catch (Exception exception) when (exception is COMException or System.IO.IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            StopPlayback();
            Bar.Show($"The audio couldn't be played. {Waypoint.Core.PlainProblem.Report(exception)} The transcript is still here to read.");
            return;
        }

        if (_playingRow is { } previous && previous != line.Owner)
        {
            previous.MarkCurrent(-1);
        }

        _playingRow = line.Owner;
        _playingNote = _draft.Id;
        _playbackClock.Start();
        ShowPlayback();
    }

    private void OnPlayPauseClicked(object sender, RoutedEventArgs e)
    {
        if (_player is not { IsOpen: true } player)
        {
            return;
        }

        if (player.IsPlaying)
        {
            player.Pause();
        }
        else
        {
            player.Resume();
        }

        ShowPlayback();
    }

    private void OnStopPlaybackClicked(object sender, RoutedEventArgs e) => StopPlayback();

    private void ShowPlayback()
    {
        if (_player is not { IsOpen: true } player)
        {
            PlaybackBar.Visibility = Visibility.Collapsed;
            return;
        }

        var at = TranscriptLines.Stamp(player.Position);
        PlaybackText.Text = player.IsPlaying ? $"Playing at {at}" : $"Paused at {at}";
        PlayPauseButton.Content = player.IsPlaying ? "Pause" : "Play";
        PlaybackBar.Visibility = Visibility.Visible;
        _playingRow?.MarkCurrent(TranscriptLines.LineAt(_playingRow.Times, player.Position));
    }

    private void StopPlayback()
    {
        _player?.Stop();
        _playbackClock.Stop();
        _playingRow?.MarkCurrent(-1);
        _playingRow = null;
        _playingNote = null;
        PlaybackBar.Visibility = Visibility.Collapsed;
    }

    private sealed class SourceRow
    {
        private readonly NoteSource _source;

        public SourceRow(NoteSource source, NoteRecording? recording, string? linesNote)
        {
            _source = source;
            AudioPath = recording?.KeptPath;
            Times = recording?.Lines ?? [];
            Lines = Times.Select(line => new LineRow(this, line, AudioPath is not null)).ToList();
            LinesNote = linesNote;
        }

        public string Heading => $"{_source.Name}  ·  {NoteSourceKind.Label(_source.Kind)}  ·  added {_source.CreatedAt.ToString("MMM d", CultureInfo.CurrentCulture)}";

        public string Text => _source.Text;

        public string? AudioPath { get; }

        public IReadOnlyList<TranscriptLine> Times { get; }

        public IReadOnlyList<LineRow> Lines { get; }

        public string? LinesNote { get; }

        public Visibility TextVisibility => Lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        public Visibility LinesVisibility => Lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        public Visibility LinesNoteVisibility => LinesNote is null ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>What a screen reader says for the row, rather than the class name.</summary>
        public override string ToString() => Heading;

        public void MarkCurrent(int index)
        {
            for (var i = 0; i < Lines.Count; i++)
            {
                Lines[i].IsCurrent = i == index;
            }
        }
    }

    private sealed class LineRow(SourceRow owner, TranscriptLine line, bool playable) : INotifyPropertyChanged
    {
        private bool _current;

        public event PropertyChangedEventHandler? PropertyChanged;

        public SourceRow Owner => owner;

        public TranscriptLine Line => line;

        public bool Playable => playable;

        public string Stamp => line.Stamp;

        public string Text => line.Text;

        public string? Hint => playable ? $"Play from {line.Stamp}" : null;

        public string Name => playable ? $"Play from {line.Stamp}: {line.Text}" : $"{line.Stamp}: {line.Text}";

        /// <summary>What a screen reader says for the row, rather than the class name.</summary>
        public override string ToString() => Name;

        public bool IsCurrent
        {
            get => _current;
            set
            {
                if (_current != value)
                {
                    _current = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
                }
            }
        }
    }
}
