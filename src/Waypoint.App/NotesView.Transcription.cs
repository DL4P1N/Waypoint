using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Threading.Tasks;
using Waypoint.Core;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Notes;
using Waypoint.Core.Remote;
using Waypoint.Core.Transcription;

namespace Waypoint.App;

/// <summary>Notes from recordings, the change box, and the suggestions under a note.</summary>
public partial class NotesView
{
    // Notes from recordings

    private TranscriptionJob? TranscriptionFor(long noteId) => _transcribing.GetValueOrDefault(noteId);

    private void PickAudio()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = AudioFilter,
            Multiselect = true,
            Title = "Choose a recording to make notes from",
        };
        if (FileDialogs.Shared.Show(() => dialog.ShowDialog(Window.GetWindow(this)), false) == true)
        {
            ImportAudio(dialog.FileNames);
        }
    }

    /// <summary>
    /// Asks once, with the "Keep the audio" choice and, the first time, the download. Each recording gets its note
    /// right away, filed like a file would be, and is transcribed in the background one at a time.
    /// </summary>
    public async void ImportAudio(IReadOnlyList<string> paths)
    {
        if (_transcription is not { } queue || _maker is null)
        {
            Bar.Show("Notes from recordings aren't available right now.");
            return;
        }

        var needsDownload = !queue.IsReady;
        var ask = new AudioImportWindow(Window.GetWindow(this), paths.Select(path => System.IO.Path.GetFileName(path)).ToList(), needsDownload ? queue.DownloadBytes : null);
        if (ask.ShowDialog() != true)
        {
            Bar.Show(needsDownload ? "Nothing was downloaded or transcribed." : "Nothing was transcribed.");
            return;
        }

        if (needsDownload)
        {
            queue.AgreeToDownload();
        }

        Flush();
        var picked = SelectedFolder?.Folder.Id;
        var keepAudio = ask.KeepTheAudio;
        var lines = new List<string>();
        TranscriptionJob? last = null;
        foreach (var path in paths)
        {
            if (!System.IO.File.Exists(path))
            {
                lines.Add($"{System.IO.Path.GetFileName(path)} isn't there anymore.");
                continue;
            }

            // Its note is made on the server in server mode, so that's sent off the window's thread.
            var (job, line) = await Work.Run(() =>
            {
                var job = queue.Import(path, keepAudio, picked, picked is not null);
                var note = _notes.GetNote(job.NoteId);
                return (job, $"Made {note?.Title ?? job.Name} in {PlaceName(note?.FolderId)}.");
            });
            last = job;
            lines.Add(line);
        }

        RefreshTranscriptions();
        if (last is not null)
        {
            Flush();
            var lastId = last.NoteId;
            var note = await Work.Read(() => _notes.GetNote(lastId));

            // Anything typed while it was read is saved before it opens.
            Flush();
            Open(note);
            lines.Add(needsDownload ? "Transcribing starts once the download is done." : "It's being transcribed on this PC now.");
        }

        FoldersChanged(refocus: false);
        Bar.Show(string.Join(" ", lines));
    }

    private void OnTranscriptionChanged(TranscriptionChange change)
    {
        var before = change.NoteId is { } id ? TranscriptionFor(id) : null;
        RefreshTranscriptions();
        if (change.NoteId is { } noteId)
        {
            var after = TranscriptionFor(noteId);
            if (after?.Status != before?.Status || change.Outcome is not null)
            {
                // The notes were written, or the job moved to a step that changes what can be done with the note.
                if (after is null || after.Status == TranscriptionStatus.Writing)
                {
                    ReloadIfOpen(noteId);
                }

                ShowExtrasIfOpen(noteId);
                ShowSoon(force: true);
            }
            else if (after?.Status == TranscriptionStatus.Transcribing)
            {
                // Progress only. The list line shows the percent, and the gate skips a rebuild while the list is in use.
                ShowList();
            }
        }

        if (change.Outcome is { } outcome)
        {
            Bar.Show(outcome);
        }
    }

    private void RefreshTranscriptions()
    {
        if (_transcription is null)
        {
            return;
        }

        _transcribing = _transcription?.OpenJobsByNote() ?? new Dictionary<long, TranscriptionJob>();
        if (_transcribing.Values.Any(job => job.IsActive))
        {
            _clock.Start();
        }
        else if (_jobs.Count == 0)
        {
            _clock.Stop();
        }

        ShowStatus();
    }

    private async void OnChangeClicked(object sender, RoutedEventArgs e)
    {
        var request = ChangeBox.Text.Trim();
        if (request.Length == 0)
        {
            ChangeBox.Focus();
            return;
        }

        // Cleared once it's on its way, unless something else was typed there while the server was asked.
        if (await Rewrite(request) && ChangeBox.Text.Trim() == request)
        {
            ChangeBox.Text = "";
        }
    }

    private void OnChangeKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OnChangeClicked(sender, e);
        }
    }

    /// <summary>Writes the open note again from its sources. False if the user kept their version.</summary>
    private async Task<bool> Rewrite(string? request)
    {
        Flush();
        if (_draft.Id is not { } id || _jobs.ContainsKey(id))
        {
            return false;
        }

        if (!await ConfirmReplace(id))
        {
            Bar.Show("Kept your version. Nothing was rewritten.");
            return false;
        }

        _ = WriteNotesAsync(id, request);
        return true;
    }

    private async void OnSuggestionAdded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SuggestionRow row)
        {
            return;
        }

        var added = await Work.Run(() => _notes.AcceptNoteSuggestion(row.Id, DateTime.Now));
        Bar.Show(added.Problem ?? $"Added {row.Title} to To do.");
        ShowNoteExtras();
    }

    private async void OnSuggestionSkipped(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SuggestionRow row)
        {
            await Work.Run(() => _notes.SkipNoteSuggestion(row.Id));
            ShowNoteExtras();
        }
    }
}
