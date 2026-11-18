using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Notes;

namespace Waypoint.App;

/// <summary>
/// Study from notes. "Study for an exam" on a class folder, or "Study for this exam" on To do, gathers the
/// folder's notes and has Haiku write practice questions, saved as a generated "Study: exam" note in the folder.
/// Making it again never replaces the user's changes without a yes. The call runs in the background.
/// </summary>
public partial class NotesView
{
    private const string StudyMethod = "study";
    private const string FirstQuestionAction = "Answer question 1 in your study note, then open its answer to check";

    private readonly HashSet<string> _studying = [];
    private bool _studyConnected;

    /// <summary>Raised with a reply for the chat, such as when a study session starts.</summary>
    public event Action<string>? Replied;

    /// <summary>Turns on study from notes and keeping audio per note. Needs <see cref="Connect"/> first.</summary>
    public void ConnectStudy(INoteAudio audio)
    {
        _studyConnected = true;
        _audio = audio;
    }

    /// <summary>Studying goes through the notes, wherever they're kept, once it's connected.</summary>
    private INotesData? _study => _studyConnected && _notes.CanStudy ? _notes : null;

    /// <summary>For an exam on To do. It needs a class with a notes folder to study from.</summary>
    public void StudyForExam(StoredItem item)
    {
        if (_study is not { } study)
        {
            Bar.Show("Studying from notes isn't available right now.");
            return;
        }

        var exam = StudyMaker.ExamOf(item);
        if (study.StudyFolderFor(exam) is { } folderId)
        {
            StudyFor(exam, folderId);
            return;
        }

        // No class, or no folder for it yet. Any folder of notes can be studied from, and the pick is remembered.
        ShowFolders();
        var why = string.IsNullOrWhiteSpace(exam.Course)
            ? $"{exam.Title} isn't linked to a class."
            : $"There's no notes folder for {exam.Course} yet.";
        if (_folders.Count == 0)
        {
            Bar.Show($"{why} Make a folder with + and add notes to it, then study from there.");
            return;
        }

        Bar.Show($"{why} Pick a folder of notes to study from, and it's used for this exam from now on.", () => PickStudyFolder(exam), action: "Pick a folder...");
        PickStudyFolder(exam);
    }

    /// <summary>Folders to study from for an exam with no class folder. The one picked is remembered for that exam.</summary>
    private void PickStudyFolder(StudyExam exam)
    {
        if (_study is not { } study)
        {
            return;
        }

        RowMenu.Open(ListTitle, System.Windows.Controls.Primitives.PlacementMode.Bottom, StudyFolderChoices(exam, study), () => { });
    }

    private IEnumerable<RowMenuEntry> StudyFolderChoices(StudyExam exam, INotesData study) =>
        _folders.Select(found => new RowMenuEntry(
            string.Concat(Enumerable.Repeat(Indent, found.Depth)) + found.Folder.Name,
            () =>
            {
                study.LinkStudyFolder(exam, found.Folder.Id);
                StudyFor(exam, found.Folder.Id);
            }));

    /// <summary>The class folder's right-click entry. One open exam is studied for directly, several are picked from.</summary>
    private IEnumerable<RowMenuEntry> StudyMenu(NoteFolder folder)
    {
        if (_study is not { } study || string.IsNullOrWhiteSpace(folder.Course))
        {
            yield break;
        }

        var exams = study.ExamsFor(folder.Course, DateOnly.FromDateTime(DateTime.Now));
        if (exams.Count == 0)
        {
            yield return new RowMenuEntry("Study for an exam", () => StudyFor(new StudyExam($"{folder.Course} exam", folder.Course), folder.Id));
        }
        else if (exams.Count == 1)
        {
            yield return new RowMenuEntry("Study for an exam", () => StudyFor(StudyMaker.ExamOf(exams[0]), folder.Id));
        }
        else
        {
            yield return new RowMenuEntry("Study for an exam", Children: exams
                .Select(exam => new RowMenuEntry(ExamLabel(exam), () => StudyFor(StudyMaker.ExamOf(exam), folder.Id)))
                .ToList());
        }
    }

    /// <summary>The class folder's Study button: one exam goes straight to its questions, several are picked from.</summary>
    private void OnStudyButtonClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedFolder is not { } folder || StudyMenu(folder.Folder).FirstOrDefault() is not { } entry)
        {
            return;
        }

        if (entry.Run is { } run)
        {
            run();
        }
        else if (entry.Children is { } exams)
        {
            RowMenu.Open(StudyButton, System.Windows.Controls.Primitives.PlacementMode.Bottom, exams, () => { });
        }
    }

    private static string ExamLabel(StoredItem exam) =>
        exam.DueDate is { } date ? $"{exam.Title}, {date.ToString("ddd MMM d", CultureInfo.CurrentCulture)}" : exam.Title;

    private static string StudyKey(long folderId, string examTitle) => $"{folderId}\n{examTitle.Trim().ToLowerInvariant()}";

    /// <summary>Asks before replacing changes, then writes in the background and opens the note when it's done.</summary>
    private async void StudyFor(StudyExam exam, long folderId, bool replaceEdits = false)
    {
        if (_study is not { } study)
        {
            return;
        }

        var key = StudyKey(folderId, exam.Title);
        if (!_studying.Add(key))
        {
            Bar.Show($"Practice questions for {exam.Title} are already being written.");
            return;
        }

        StudyOutcome outcome;
        try
        {
            Flush();
            var title = StudyGuide.Title(exam.Title);
            if (!replaceEdits && study.FindStudyNote(folderId, exam.Title) is { } existing
                && _notes.GetNoteGeneration(existing) is { EditedSince: true } generation)
            {
                var ask = new NoteConfirmWindow(
                    Window.GetWindow(this),
                    $"Replace your changes to {title}?",
                    generation.WasGenerated
                        ? "You changed this note after its questions were made. New questions replace your changes. You can undo right after."
                        : "This note already has your own text. New questions replace it. You can undo right after.",
                    "Make new questions",
                    title: "Replace your changes");
                if (ask.ShowDialog() != true)
                {
                    Bar.Show($"Kept your version of {title}.");
                    return;
                }

                replaceEdits = true;
            }

            Bar.Show($"Writing practice questions for {exam.Title} with Claude. This can take a minute, and you can keep using Waypoint.");
            ShowStatus();
            var allowed = replaceEdits;
            outcome = await Task.Run(() => NoteJobText.GuardStudy(() => study.MakeStudyAsync(exam, folderId, allowed, DateTime.Now, CancellationToken.None)));
        }
        finally
        {
            _studying.Remove(key);
        }

        FinishStudy(exam, folderId, outcome);
    }

    private void FinishStudy(StudyExam exam, long folderId, StudyOutcome outcome)
    {
        switch (outcome.Status)
        {
            case StudyStatus.Made when outcome.NoteId is { } noteId:
                var before = outcome.Before;
                Action? undo = before is not null && !string.IsNullOrWhiteSpace(before.Body)
                    ? () =>
                    {
                        _notes.RestoreNoteBody(noteId, before, DateTime.Now);
                        ReloadIfOpen(noteId);
                        Refresh(force: true);
                    }
                    : null;

                // Opens the new questions unless another note is being typed in.
                if (_draft.Id == noteId || !Editor.IsKeyboardFocusWithin)
                {
                    Flush();
                    Column.Select(EntryKey(folderId));
                    ShowList(force: true);
                    Open(_notes.GetNote(noteId));
                }

                Bar.Show(outcome.Message, undo);
                break;

            case StudyStatus.NeedsConfirm:
                StudyFor(exam, folderId);
                return;

            case StudyStatus.ChangedWhileWriting when outcome.NoteId is { } changedId && outcome.Markdown is { } markdown && _study is { } study:
                if (_draft.Id == changedId)
                {
                    Flush();
                }

                var title = _notes.GetNote(changedId)?.Title ?? StudyGuide.Title(exam.Title);
                var ask = new NoteConfirmWindow(
                    Window.GetWindow(this),
                    $"{title} changed while new questions were being written. Use the new questions?",
                    "Keep my version leaves your text as it is.",
                    "Use the new questions",
                    title: "New questions are ready");
                if (ask.ShowDialog() == true)
                {
                    FinishStudy(exam, folderId, study.SaveStudyAnyway(changedId, markdown, DateTime.Now));
                    return;
                }

                Bar.Show($"Kept your version of {title}.");
                break;

            default:
                Bar.Show(outcome.Message);
                break;
        }

        if (outcome.NoteId is { } id)
        {
            ReloadIfOpen(id);
        }

        Refresh(force: true);
    }

    /// <summary>The open note's exam, when it's a study note that was made from notes.</summary>
    private StudyExam? OpenStudyExam(long noteId) =>
        _study is { } study && _sourceCount == 0 && _notes.GetNoteGeneration(noteId) is { WasGenerated: true }
            ? study.ExamForNote(noteId, DateOnly.FromDateTime(DateTime.Now))
            : null;

    /// <summary>The status line for a study note. Null when the open note isn't one.</summary>
    private bool ShowStudyStatus(long noteId)
    {
        if (OpenStudyExam(noteId) is not { } exam || _notes.GetNote(noteId)?.FolderId is not { } folderId || _notes.GetNoteGeneration(noteId) is not { GeneratedAt: { } at } generation)
        {
            return false;
        }

        StopButton.Visibility = Visibility.Collapsed;
        if (_studying.Contains(StudyKey(folderId, exam.Title)))
        {
            StatusText.Text = "Writing new practice questions with Claude. This can take a minute.";
            StatusButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            var text = $"Practice questions written by Claude from the notes in this folder on {at.ToString("MMM d 'at' h:mm tt", CultureInfo.CurrentCulture)}. Check the answers against your notes.";
            if (generation.EditedSince)
            {
                text += " You've changed it since.";
            }

            StatusText.Text = text;
            StatusButton.Content = "Make new questions";
            StatusButton.Visibility = Visibility.Visible;
        }

        StatusPanel.Visibility = Visibility.Visible;
        return true;
    }

    /// <summary>True when the status button was for a study note and has been handled.</summary>
    private bool StudyStatusClicked(long noteId)
    {
        if (OpenStudyExam(noteId) is not { } exam || _notes.GetNote(noteId)?.FolderId is not { } folderId)
        {
            return false;
        }

        StudyFor(exam, folderId);
        return true;
    }

    private void ShowStudyActions(long? noteId)
    {
        var exam = noteId is { } id ? OpenStudyExam(id) : null;
        StudyStartButton.Visibility = exam?.ItemRef is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>One concrete first step, through the usual session, so it shows on the session card like any other start.</summary>
    private void OnStudyStartClicked(object sender, RoutedEventArgs e)
    {
        if (_draft.Id is not { } noteId || OpenStudyExam(noteId) is not { ItemRef: { } itemRef })
        {
            return;
        }

        var reply = _notes.StartActivation(itemRef, FirstQuestionAction, StudyMethod, DateTime.Now).Text;
        Bar.Show(reply);
        Replied?.Invoke(reply);
    }
}
