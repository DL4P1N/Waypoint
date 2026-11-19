using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Notes;
using Waypoint.Core.Transcription;

namespace Waypoint.App;

/// <summary>Notes from files: picking or dropping files, making a note from each, and attaching files to the open note.</summary>
public partial class NotesView
{
    // Notes from files

    private void PickFiles(long? attachTo)
    {
        if (_maker is null)
        {
            Bar.Show("Notes from files aren't available right now.");
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = FileFilter,
            Multiselect = attachTo is null,
            Title = attachTo is null ? "Choose files to make notes from" : "Choose a file to add to this note",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        if (attachTo is { } id)
        {
            AttachFiles(id, dialog.FileNames);
        }
        else
        {
            MakeNotesFrom(dialog.FileNames);
        }
    }

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        e.Effects = _maker is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (_maker is not null && Editor.Visibility != Visibility.Visible)
        {
            NothingOpen.Text = "Let go to make notes from this.";
        }
    }

    private void OnFileDragLeave(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && Editor.Visibility != Visibility.Visible && !ShowingSpots)
        {
            NothingOpen.Text = NothingOpenText;
        }
    }

    /// <summary>A file dropped anywhere on the page makes a new note. Adding to an open note is the note's own "Add a file".</summary>
    private void OnFileDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        e.Handled = true;
        OnFileDragLeave(sender, e);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            // Lets the drag finish in Explorer before anything slow starts. Recordings are transcribed, and other files read.
            var recordings = paths.Where(TranscriptionQueue.IsAudioFile).ToList();
            var files = paths.Where(path => !TranscriptionQueue.IsAudioFile(path)).ToList();
            Dispatcher.BeginInvoke(() =>
            {
                if (recordings.Count > 0)
                {
                    ImportAudio(recordings);
                }

                if (files.Count > 0)
                {
                    MakeNotesFrom(files);
                }
            }, DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// One note per file. Each file's text is saved on its note before Haiku is asked anything, so a failed
    /// call loses nothing. The last note made opens while its notes are written.
    /// </summary>
    private async void MakeNotesFrom(IReadOnlyList<string> paths)
    {
        if (_maker is not { } maker)
        {
            Bar.Show("Notes from files aren't available right now.");
            return;
        }

        Flush();
        var picked = SelectedFolder?.Folder.Id;
        var problems = new List<string>();
        var made = new List<StartedNote>();
        Bar.Show(paths.Count == 1 ? $"Reading {System.IO.Path.GetFileName(paths[0])}..." : $"Reading {paths.Count} files...");
        foreach (var path in paths)
        {
            var file = await Task.Run(() => ImageText.ReadFile(path));
            var started = await Work.Run(() => maker.StartFromFile(file, picked, picked is not null, DateTime.Now));
            if (started.Problem is { } problem)
            {
                problems.Add(problem);
                continue;
            }

            made.Add(started);
            _ = WriteNotesAsync(started.NoteId, null);
        }

        if (made.LastOrDefault() is { } last)
        {
            // Anything typed while the files were read is saved before the new note opens.
            Flush();
            var note = await Work.Read(() => _notes.GetNote(last.NoteId));
            Flush();
            Open(note);
        }

        FoldersChanged(refocus: false);
        var lines = (await Work.Read(() => made.Select(started => $"Made {started.Title} in {PlaceName(started.FolderId)}.").ToList())).Concat(problems).ToList();
        if (made.Count > 0)
        {
            lines.Add("The notes are being written now.");
        }

        Bar.Show(string.Join(" ", lines));
    }

    private void OnAttachClicked(object sender, RoutedEventArgs e) => FlushThen(() =>
    {
        if (_draft.Id is { } id)
        {
            PickFiles(id);
        }
        else
        {
            // Nothing typed yet, so there's no note to add to. The file makes one instead.
            PickFiles(null);
        }
    });

    private async void AttachFiles(long noteId, IReadOnlyList<string> paths)
    {
        if (_maker is not { } maker)
        {
            return;
        }

        var lines = new List<string>();
        foreach (var path in paths)
        {
            var file = await Task.Run(() => ImageText.ReadFile(path));
            var attached = await Work.Run(() => maker.AttachFile(noteId, file, DateTime.Now));
            lines.Add(attached.Problem ?? $"Added {file.Name} to Sources.");
        }

        ShowExtrasIfOpen(noteId);
        var generation = await Work.Read(() => _notes.GetNoteGeneration(noteId));
        if (generation is { SourceCount: > 0 } && lines.Any(line => line.StartsWith("Added", StringComparison.Ordinal)))
        {
            lines.Add(generation.WasGenerated ? "Choose Write again to use it in the notes." : "Choose Write notes to make notes from it.");
        }

        Bar.Show(string.Join(" ", lines));
        ShowSoon(force: true);
    }

    /// <summary>The open note's status, sources and suggestions drawn again, if it's this one, from what's read in server mode.</summary>
    private void ShowExtrasIfOpen(long noteId) => Work.Show(() =>
    {
        if (_draft.Id == noteId)
        {
            ShowNoteExtras();
        }
    });

    /// <summary>
    /// Asks before notes replace text the user has in the note. Text they changed after notes were written is
    /// replaced only on a yes. Text typed by hand before any notes were written is kept as a source.
    /// </summary>
    private async Task<bool> ConfirmReplace(long noteId)
    {
        if (await Work.Read(() => _notes.GetNoteGeneration(noteId)) is not { EditedSince: true } generation)
        {
            return true;
        }

        var ask = generation.WasGenerated
            ? new NoteConfirmWindow(
                Window.GetWindow(this),
                "Replace your changes to these notes?",
                "You changed these notes after they were written. New notes are written from the files in Sources, so your changes would be replaced. You can undo right after.",
                "Replace them",
                title: "Replace your changes")
            : new NoteConfirmWindow(
                Window.GetWindow(this),
                "Write notes from the files?",
                "This note already has your own text. It's kept in Sources and used along with the files, so nothing you typed is lost.",
                "Write notes",
                title: "Write notes");
        return ask.ShowDialog() == true;
    }
}
