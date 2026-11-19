using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
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

    /// <summary>
    /// How many asks for studying are on their way to the server in server mode. A press meanwhile does nothing, so a
    /// study note is never asked for twice. On this PC an ask is done before the next press can come.
    /// </summary>
    private int _studyAsks;

    private bool _studyAsking => _studyAsks > 0;

    /// <summary>The note whose status line is a study note's, with Make new questions, as last drawn.</summary>
    private long? _studyStatusFor;

    /// <summary>Counts each time a different note opens, or none, so a late answer can tell the user moved on.</summary>
    private int _opens;

    /// <summary>Counts each time the page is left.</summary>
    private int _leaves;

    private (int Opens, int Leaves) StudyPlace => (_opens, _leaves);

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

    /// <summary>
    /// Reads, or with <paramref name="change"/> changes, for studying through the page's queue, so in server mode the
    /// window never waits on it. Stayed is false when another note or page was opened meanwhile, so the answer is dropped.
    /// </summary>
    private async Task<(bool Stayed, T Answer)> StudyAsk<T>(Func<T> ask, bool change = false, [CallerMemberName] string what = "")
    {
        var at = StudyPlace;
        _studyAsks++;
        try
        {
            var answer = change ? await Work.Run(ask, what) : await Work.Read(ask, what);
            return (StudyPlace == at, answer);
        }
        finally
        {
            _studyAsks--;
        }
    }

    /// <summary>For an exam on To do. It needs a class with a notes folder to study from.</summary>
    public async void StudyForExam(StoredItem item)
    {
        if (_studyAsking)
        {
            return;
        }

        if (_study is not { } study)
        {
            Bar.Show("Studying from notes isn't available right now.");
            return;
        }

        var exam = StudyMaker.ExamOf(item);
        var (stayed, found) = await StudyAsk(() => study.StudyFolderFor(exam));
        if (!stayed)
        {
            return;
        }

        if (found is { } folderId)
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
            () => LinkThenStudy(exam, study, found.Folder.Id)));

    /// <summary>The folder picked is remembered for the exam, then studied from, unless the user moved on meanwhile.</summary>
    private async void LinkThenStudy(StudyExam exam, INotesData study, long folderId)
    {
        if (_studyAsking)
        {
            return;
        }

        var (stayed, _) = await StudyAsk(
            () =>
            {
                study.LinkStudyFolder(exam, folderId);
                return true;
            },
            change: true);
        if (stayed)
        {
            StudyFor(exam, folderId);
        }
    }

    /// <summary>Whether a folder can be studied from: it's a class folder, and studying is on.</summary>
    private bool CanStudyFrom(NoteFolder folder) => _study is not null && !string.IsNullOrWhiteSpace(folder.Course);

    /// <summary>
    /// The class folder's right-click entry. One open exam is studied for directly, several are picked from. In
    /// server mode the exams are read once it's chosen, so the menu never waits on the server.
    /// </summary>
    private IEnumerable<RowMenuEntry> StudyMenu(NoteFolder folder)
    {
        if (!CanStudyFrom(folder) || _study is not { } study)
        {
            yield break;
        }

        if (!Work.RunsNow)
        {
            yield return new RowMenuEntry("Study for an exam", () => StudyFromFolder(folder, study));
            yield break;
        }

        yield return StudyEntry(folder, study.ExamsFor(folder.Course!, DateOnly.FromDateTime(DateTime.Now)));
    }

    private RowMenuEntry StudyEntry(NoteFolder folder, IReadOnlyList<StoredItem> exams) => exams.Count switch
    {
        0 => new RowMenuEntry("Study for an exam", () => StudyFor(new StudyExam($"{folder.Course} exam", folder.Course), folder.Id)),
        1 => new RowMenuEntry("Study for an exam", () => StudyFor(StudyMaker.ExamOf(exams[0]), folder.Id)),
        _ => new RowMenuEntry("Study for an exam", Children: exams
            .Select(exam => new RowMenuEntry(ExamLabel(exam), () => StudyFor(StudyMaker.ExamOf(exam), folder.Id)))
            .ToList()),
    };

    /// <summary>In server mode: the folder's open exams are read, then studied for as the menu would have.</summary>
    private async void StudyFromFolder(NoteFolder folder, INotesData study)
    {
        if (_studyAsking)
        {
            return;
        }

        var (stayed, exams) = await StudyAsk(() => study.ExamsFor(folder.Course!, DateOnly.FromDateTime(DateTime.Now)));
        if (stayed)
        {
            RunStudyEntry(StudyEntry(folder, exams));
        }
    }

    /// <summary>The class folder's Study button: one exam goes straight to its questions, several are picked from.</summary>
    private void OnStudyButtonClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedFolder is not { } folder || StudyMenu(folder.Folder).FirstOrDefault() is not { } entry)
        {
            return;
        }

        RunStudyEntry(entry);
    }

    private void RunStudyEntry(RowMenuEntry entry)
    {
        if (entry.Run is { } run)
        {
            run();
        }
        else if (entry.Children is { } exams)
        {
            FrameworkElement under = StudyButton.IsVisible ? StudyButton : ListTitle;
            RowMenu.Open(under, System.Windows.Controls.Primitives.PlacementMode.Bottom, exams, () => { });
        }
    }

    private static string ExamLabel(StoredItem exam) =>
        exam.DueDate is { } date ? $"{exam.Title}, {date.ToString("ddd MMM d", CultureInfo.CurrentCulture)}" : exam.Title;

    private static string StudyKey(long folderId, string examTitle) => $"{folderId}\n{examTitle.Trim().ToLowerInvariant()}";

    /// <summary>
    /// Asks before replacing changes, then writes in the background and opens the note when it's done. A press while
    /// another waits on the server does nothing, but asking again once the server wants a yes always goes ahead.
    /// </summary>
    private async void StudyFor(StudyExam exam, long folderId, bool replaceEdits = false, bool askedAgain = false)
    {
        if ((_studyAsking && !askedAgain) || _study is not { } study || _stoppedForRestore)
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
            var (stayed, found) = replaceEdits
                ? (true, null)
                : await StudyAsk(() => study.FindStudyNote(folderId, exam.Title) is { } existing ? _notes.GetNoteGeneration(existing) : null);
            if (!stayed)
            {
                return;
            }

            if (found is { EditedSince: true } generation)
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

        if (!_stoppedForRestore)
        {
            await FinishStudy(exam, folderId, outcome);
        }
    }

    private async Task FinishStudy(StudyExam exam, long folderId, StudyOutcome outcome)
    {
        switch (outcome.Status)
        {
            case StudyStatus.Made when outcome.NoteId is { } noteId:
                var before = outcome.Before;
                Action? undo = before is not null && !string.IsNullOrWhiteSpace(before.Body)
                    ? () => RestoreBody(noteId, before)
                    : null;

                // Opens the new questions unless another note is being typed in.
                if (_draft.Id == noteId || !Editor.IsKeyboardFocusWithin)
                {
                    Flush();
                    Column.Select(EntryKey(folderId));
                    Work.Show(() => ShowList(force: true), () => ShowList());
                    var opens = _opens;
                    var note = await Work.Read(() => _notes.GetNote(noteId));

                    // In server mode it isn't opened over a note opened, or typed in, while it was read.
                    if (_draft.Id == noteId || (_opens == opens && !Editor.IsKeyboardFocusWithin))
                    {
                        Flush();
                        Open(note);
                    }
                }

                Bar.Show(outcome.Message, undo);
                break;

            case StudyStatus.NeedsConfirm:
                StudyFor(exam, folderId, askedAgain: true);
                return;

            case StudyStatus.ChangedWhileWriting when outcome.NoteId is { } changedId && outcome.Markdown is { } markdown && _study is { } study:
                if (_draft.Id == changedId)
                {
                    Flush();
                }

                var title = (await Work.Read(() => _notes.GetNote(changedId)))?.Title ?? StudyGuide.Title(exam.Title);

                // In server mode anything typed in it while its title was read is saved before the question.
                if (!Work.RunsNow && _draft.Id == changedId)
                {
                    Flush();
                }
                var ask = new NoteConfirmWindow(
                    Window.GetWindow(this),
                    $"{title} changed while new questions were being written. Use the new questions?",
                    "Keep my version leaves your text as it is.",
                    "Use the new questions",
                    title: "New questions are ready");
                if (ask.ShowDialog() == true)
                {
                    await FinishStudy(exam, folderId, await Work.Run(() => study.SaveStudyAnyway(changedId, markdown, DateTime.Now)));
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

        ShowSoon(force: true);
    }

    /// <summary>The open note's exam, when it's a study note that was made from notes.</summary>
    private StudyExam? OpenStudyExam(long noteId) => OpenStudyExam(noteId, _sourceCount);

    /// <summary>The same, with the note's count of sources given, so it can be read off the window's thread.</summary>
    private StudyExam? OpenStudyExam(long noteId, int sourceCount) =>
        _study is { } study && sourceCount == 0 && _notes.GetNoteGeneration(noteId) is { WasGenerated: true }
            ? study.ExamForNote(noteId, DateOnly.FromDateTime(DateTime.Now))
            : null;

    /// <summary>The status line for a study note. Null when the open note isn't one.</summary>
    private bool ShowStudyStatus(long noteId)
    {
        if (OpenStudyExam(noteId) is not { } exam || _notes.GetNote(noteId)?.FolderId is not { } folderId || _notes.GetNoteGeneration(noteId) is not { GeneratedAt: { } at } generation)
        {
            return false;
        }

        _studyStatusFor = noteId;
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

    /// <summary>
    /// True when the status button was for a study note and has been handled. It's handled, doing nothing, while
    /// another press waits on the server, or when another note or page was opened while this was read.
    /// </summary>
    private async Task<bool> StudyStatusClicked(long noteId)
    {
        // While another press waits on the server, a study note's button does nothing, and any other note's goes on.
        if (_studyAsking)
        {
            return _studyStatusFor == noteId;
        }

        var sources = _sourceCount;
        var (stayed, found) = await StudyAsk(() =>
            OpenStudyExam(noteId, sources) is { } exam && _notes.GetNote(noteId)?.FolderId is { } folderId ? (exam, folderId) : ((StudyExam Exam, long FolderId)?)null);
        if (!stayed)
        {
            return true;
        }

        if (found is not { } study)
        {
            return false;
        }

        StudyFor(study.Exam, study.FolderId);
        return true;
    }

    private void ShowStudyActions(long? noteId)
    {
        var exam = noteId is { } id ? OpenStudyExam(id) : null;
        StudyStartButton.Visibility = exam?.ItemRef is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>One concrete first step, through the usual session, so it shows on the session card like any other start.</summary>
    private async void OnStudyStartClicked(object sender, RoutedEventArgs e)
    {
        if (_studyAsking || _draft.Id is not { } noteId)
        {
            return;
        }

        // Both asks count as one press on its way, so another press can't come in between them.
        _studyAsks++;
        try
        {
            var sources = _sourceCount;
            var (stayed, itemRef) = await StudyAsk(() => OpenStudyExam(noteId, sources)?.ItemRef);
            if (!stayed || itemRef is null)
            {
                return;
            }

            // The session has started once this is back, so the chat says so even if the user moved on meanwhile.
            var (still, reply) = await StudyAsk(() => _notes.StartActivation(itemRef, FirstQuestionAction, StudyMethod, DateTime.Now).Text, change: true);
            if (still)
            {
                Bar.Show(reply);
            }

            Replied?.Invoke(reply);
        }
        finally
        {
            _studyAsks--;
        }
    }
}
