using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Threading;
using System.Threading.Tasks;
using Waypoint.Core;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Notes;
using Waypoint.Core.Transcription;

namespace Waypoint.App;

/// <summary>Writing notes from a note's sources: asking before replacing, finishing a write, undoing it, and the status line and Stop.</summary>
public partial class NotesView
{
    private bool _stoppedForRestore;

    /// <summary>
    /// Before a restore: stops notes being written, and waits up to <paramref name="limit"/> for them and practice
    /// questions under way to end. None starts after, and what they'd show or ask at the end is skipped, since
    /// Waypoint starts again. True once none is left.
    /// </summary>
    public async Task<bool> StopWritingAsync(TimeSpan limit)
    {
        _stoppedForRestore = true;
        foreach (var job in _jobs.Values)
        {
            job.Stop.Cancel();
        }

        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (_jobs.Count > 0 || _studying.Count > 0)
        {
            if (waited.Elapsed >= limit)
            {
                return false;
            }

            await Task.Delay(50);
        }

        return true;
    }

    /// <summary>The user said yes before this is called, or there was nothing of theirs to replace.</summary>
    private async Task WriteNotesAsync(long noteId, string? request)
    {
        if (_maker is not { } maker || _jobs.ContainsKey(noteId) || _stoppedForRestore)
        {
            return;
        }

        var job = new WriteJob(DateTime.Now, request);
        _jobs[noteId] = job;
        _failures.Remove(noteId);
        _clock.Start();
        if (_draft.Id == noteId)
        {
            ShowNoteExtras();
        }

        NoteWriteOutcome outcome;
        try
        {
            await _writeTurn.WaitAsync(job.Stop.Token);
            try
            {
                job.StartedAt = DateTime.Now;
                ShowStatus();
                outcome = await Task.Run(() => NoteJobText.GuardNotes(() => maker.WriteNotesAsync(noteId, request, replaceEdits: true, DateTime.Now, job.Stop.Token), job.Stop.Token));
            }
            finally
            {
                _writeTurn.Release();
            }
        }
        catch (OperationCanceledException)
        {
            outcome = new NoteWriteOutcome(NoteWriteStatus.Failed, "Stopped before the notes were written. The files are saved in Sources, so you can try again.");
        }
        finally
        {
            _jobs.Remove(noteId);
            job.Stop.Dispose();
            if (_jobs.Count == 0 && !_transcribing.Values.Any(found => found.IsActive))
            {
                _clock.Stop();
            }
        }

        if (!_stoppedForRestore)
        {
            await Finish(noteId, outcome);
        }
    }

    private async Task Finish(long noteId, NoteWriteOutcome outcome)
    {
        var title = (await Work.Read(() => _notes.GetNote(noteId)))?.Title ?? "The note";
        switch (outcome.Status)
        {
            case NoteWriteStatus.Written:
                var before = outcome.Before;
                Action? undo = before is not null && !string.IsNullOrWhiteSpace(before.Body)
                    ? () => RestoreBody(noteId, before)
                    : null;
                Bar.Show($"{title}: {outcome.Message}", undo);
                break;

            case NoteWriteStatus.ChangedWhileWriting when outcome.Notes is { } notes && _maker is { } maker:
                if (_draft.Id == noteId)
                {
                    Flush();
                }

                var ask = new NoteConfirmWindow(
                    Window.GetWindow(this),
                    $"{title} changed while new notes were being written. Use the new notes?",
                    "Keep my version leaves your text as it is. The files stay in Sources, so you can rewrite later.",
                    "Use the new notes",
                    title: "New notes are ready");
                if (ask.ShowDialog() == true)
                {
                    await Finish(noteId, await Work.Run(() => maker.SaveWrittenAnyway(noteId, notes, DateTime.Now)));
                    return;
                }

                Bar.Show($"Kept your version of {title}.");
                break;

            case NoteWriteStatus.Failed:
                _failures[noteId] = outcome.Message;
                if (_draft.Id != noteId)
                {
                    Bar.Show($"{title}: {outcome.Message}");
                }

                break;

            default:
                Bar.Show(outcome.Message);
                break;
        }

        ReloadIfOpen(noteId);
        ShowSoon(force: true);
    }

    /// <summary>Undo for notes written over a note: its text from before goes back, after anything saved ahead of it.</summary>
    private async void RestoreBody(long noteId, NoteBodyState before)
    {
        await Work.Run(() => _notes.RestoreNoteBody(noteId, before, DateTime.Now));
        ReloadIfOpen(noteId);
        ShowSoon(force: true);
    }

    /// <summary>
    /// The editor is read only while a note's notes are written, so nothing typed is waiting to be saved here. In
    /// server mode it's read after anything saved ahead of it, and opened again unless something was typed meanwhile.
    /// </summary>
    private async void ReloadIfOpen(long noteId)
    {
        if (_draft.Id != noteId)
        {
            return;
        }

        if (_draft.IsDirty)
        {
            Flush();
        }

        var (title, body) = (_draft.Title, _draft.Body);
        var stored = await Work.Read(() => _notes.GetNote(noteId));
        if (stored is not null && _draft.Id == noteId && _draft.Title == title && _draft.Body == body)
        {
            Open(WithPendingSave(stored), keepMode: true);
        }
    }

    /// <summary>
    /// The status line, the change box, the suggestions, and Sources for the open note. In server mode, outside a
    /// drawing of the page, they're drawn at once from what was kept and again once read, if the note is still open.
    /// </summary>
    private void ShowNoteExtras() => ShowForOpenNote(DrawNoteExtras);

    /// <summary>
    /// Draws something of the open note's. Inside a drawing of the page, or on this PC, that's now. Otherwise in server
    /// mode it's drawn from kept reads and again after a read, and that later drawing is dropped if another note opened.
    /// </summary>
    private void ShowForOpenNote(Action draw)
    {
        if (Work.RunsNow || Work.Drawing)
        {
            draw();
            return;
        }

        var id = _draft.Id;
        Work.Show(() =>
        {
            if (_draft.Id == id)
            {
                draw();
            }
        });
    }

    private void DrawNoteExtras()
    {
        var id = _draft.Id;
        var opened = id != _extrasFor;
        _extrasFor = id;
        if (id is not { } noteId || _maker is null)
        {
            StatusPanel.Visibility = Visibility.Collapsed;
            ChangePanel.Visibility = Visibility.Collapsed;
            SuggestionsPanel.Visibility = Visibility.Collapsed;
            SourcesSection.Visibility = Visibility.Collapsed;
            SourcesSection.IsExpanded = false;
            AttachButton.Visibility = _maker is null ? Visibility.Collapsed : Visibility.Visible;
            TitleBox.IsReadOnly = BodyBox.IsReadOnly = SourceBox.IsReadOnly = false;
            _sourceCount = 0;
            _sourcesShown = "";
            ShowNoteActions(null);
            return;
        }

        var sources = _notes.GetNoteSources(noteId);
        _sourceCount = sources.Count;
        var writing = _jobs.ContainsKey(noteId) || TranscriptionFor(noteId) is { Status: TranscriptionStatus.Writing };
        TitleBox.IsReadOnly = BodyBox.IsReadOnly = SourceBox.IsReadOnly = writing;
        AttachButton.Visibility = Visibility.Visible;
        AttachButton.IsEnabled = !writing;

        DrawStatus();
        ChangePanel.Visibility = sources.Count > 0 && !writing && _notes.GetNoteGeneration(noteId)?.WasGenerated == true ? Visibility.Visible : Visibility.Collapsed;

        List<SuggestionRow> suggestions = writing ? [] : _notes.GetNoteSuggestions(noteId).Select(found => new SuggestionRow(found)).ToList();
        SuggestionsList.ItemsSource = suggestions;
        SuggestionsPanel.Visibility = suggestions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Sources start closed every time a note opens, so only the notes show.
        if (opened)
        {
            SourcesSection.IsExpanded = false;
        }

        var shown = string.Join('\n', sources.Select(source => source.Id).Prepend(noteId));
        if (shown != _sourcesShown)
        {
            _sourcesShown = shown;
            SourcesList.ItemsSource = SourceRows(noteId, sources);
        }

        SourcesHeader.Text = sources.Count == 1 ? "Sources (1 file)" : $"Sources ({sources.Count} files)";
        SourcesSection.Visibility = sources.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowNoteActions(noteId);
    }

    /// <summary>Cheap enough to run every second while notes are written, so the waiting time shows. Read like the extras in server mode.</summary>
    private void ShowStatus() => ShowForOpenNote(DrawStatus);

    private void DrawStatus()
    {
        _studyStatusFor = null;
        if (_draft.Id is not { } noteId || _maker is null)
        {
            StatusPanel.Visibility = Visibility.Collapsed;
            return;
        }

        StopButton.Visibility = Visibility.Collapsed;
        StatusButton.Visibility = Visibility.Visible;
        var files = _sourceCount == 1 ? "the file" : $"{_sourceCount} files";
        if (!_jobs.ContainsKey(noteId) && TranscriptionFor(noteId) is { } transcribing && _transcription is { } queue)
        {
            StatusText.Text = queue.Describe(transcribing, DateTime.Now);
            var waitingOnDownload = transcribing.Status == TranscriptionStatus.Queued && !queue.IsReady && queue.Setup is not { Running: true };
            StatusButton.Content = "Try again";
            StatusButton.Visibility = transcribing.Status == TranscriptionStatus.Failed || waitingOnDownload ? Visibility.Visible : Visibility.Collapsed;
            StopButton.Content = "Cancel";
            StopButton.Visibility = transcribing.IsActive ? Visibility.Visible : Visibility.Collapsed;
            StatusPanel.Visibility = Visibility.Visible;
            return;
        }

        StopButton.Content = "Stop";
        if (_jobs.TryGetValue(noteId, out var job))
        {
            StopButton.Visibility = Visibility.Visible;
            StatusButton.Visibility = Visibility.Collapsed;
            if (job.StartedAt is not { } started)
            {
                StatusText.Text = "Waiting for other notes to finish first. This note's files are saved.";
            }
            else
            {
                var waited = DateTime.Now - started;
                var doing = job.Request is { } request ? $"Rewriting the notes ({request})" : $"Writing notes from {files}";
                StatusText.Text = $"{doing} with Claude. {(int)waited.TotalMinutes}:{waited.Seconds:00} so far. Long files can take a few minutes. You can read the note, and editing opens again when this is done.";
            }

            StatusPanel.Visibility = Visibility.Visible;
            return;
        }

        var generation = _notes.GetNoteGeneration(noteId);
        if (_failures.TryGetValue(noteId, out var failure))
        {
            StatusText.Text = failure;
            StatusButton.Content = "Try again";
        }
        else if (_sourceCount > 0 && generation is { WasGenerated: false })
        {
            StatusText.Text = $"Notes haven't been written from {files} yet. The text is saved in Sources.";
            StatusButton.Content = "Write notes";
        }
        else if (ShowStudyStatus(noteId))
        {
            return;
        }
        else if (generation is { GeneratedAt: { } at })
        {
            var newer = _notes.GetNoteSources(noteId).Any(source => source.CreatedAt > at);
            var text = $"Written by Claude from {files} in Sources on {at.ToString("MMM d 'at' h:mm tt", CultureInfo.CurrentCulture)}. Check anything important against them.";
            if (generation.EditedSince)
            {
                text += " You've changed it since.";
            }

            if (newer)
            {
                text += " A file was added after that.";
            }

            StatusText.Text = text;
            StatusButton.Content = "Write again";
            StatusButton.Visibility = newer ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            StatusPanel.Visibility = Visibility.Collapsed;
            return;
        }

        StatusPanel.Visibility = Visibility.Visible;
    }

    private async void OnStatusButtonClicked(object sender, RoutedEventArgs e)
    {
        if (_draft.Id is { } id && !_jobs.ContainsKey(id) && TranscriptionFor(id) is { } transcribing && _transcription is { } queue)
        {
            if (transcribing.Status == TranscriptionStatus.Failed)
            {
                queue.Retry(transcribing.Id);
            }
            else
            {
                queue.StartSetup();
            }

            RefreshTranscriptions();
            return;
        }

        if (_draft.Id is { } studyId && await StudyStatusClicked(studyId))
        {
            return;
        }

        await Rewrite(null);
    }

    /// <summary>Asks before a transcription is stopped, titled for that, with keeping it going as the default.</summary>
    public static NoteConfirmWindow AskStopTranscribing(Window? owner, string name) => new(
        owner,
        $"Stop transcribing {name}?",
        "Your file stays where it is, so you can import it again later. Anything you typed in this note is kept.",
        "Stop transcribing",
        "Keep going",
        "Stop transcribing");

    private void OnStopClicked(object sender, RoutedEventArgs e)
    {
        if (_draft.Id is { } id && _jobs.TryGetValue(id, out var job))
        {
            job.Stop.Cancel();
        }
        else if (_draft.Id is { } noteId && TranscriptionFor(noteId) is { IsActive: true } transcribing && _transcription is { } queue)
        {
            if (AskStopTranscribing(Window.GetWindow(this), transcribing.Name).ShowDialog() == true)
            {
                queue.Cancel(transcribing.Id);
                RefreshTranscriptions();
            }
        }
    }
}
