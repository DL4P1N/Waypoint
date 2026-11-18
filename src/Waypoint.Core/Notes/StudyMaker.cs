using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Notes;

public enum StudyStatus
{
    Made,

    /// <summary>The study note has changes of the user's own that making it again would replace, so it waits for a yes.</summary>
    NeedsConfirm,

    /// <summary>The study note was typed in while the questions were being written. Nothing was replaced.</summary>
    ChangedWhileWriting,

    /// <summary>The exam's class has no notes folder, or the exam has no class.</summary>
    NoFolder,
    NothingToStudy,
    Failed,
    Gone,
}

/// <summary>What came of making practice questions. Markdown is set when they were written, Before when they replaced text, for undo.</summary>
public sealed record StudyOutcome(StudyStatus Status, string Message, long? NoteId = null, string? Markdown = null, NoteBodyState? Before = null);

/// <summary>
/// Makes "Study: exam" notes of practice questions from a class folder's notes. The note is saved in the class
/// folder as generated text, with the same protection as written notes: making it again never replaces changes
/// the user made without a yes, and text typed while Haiku was working is never replaced at all.
/// </summary>
public sealed class StudyMaker(WaypointDatabase database, IClaudeRunner runner)
{
    private const string FolderLinksKey = "study_folder_links";

    /// <summary>
    /// The notes folder to study from: one picked for this exam before, if it's still there, else the exam's class
    /// folder, if the class has one.
    /// </summary>
    public long? FolderFor(StudyExam exam)
    {
        if (FolderLinks().TryGetValue(LinkKey(exam), out var linked) && database.GetNoteFolder(linked) is not null)
        {
            return linked;
        }

        return string.IsNullOrWhiteSpace(exam.Course) ? null : database.FindClassFolder(exam.Course);
    }

    /// <summary>Remembers the folder picked for an exam that has no class folder, so it's used next time without asking.</summary>
    public void LinkFolder(StudyExam exam, long folderId)
    {
        var links = FolderLinks();
        links[LinkKey(exam)] = folderId;
        database.SetPreference(FolderLinksKey, System.Text.Json.JsonSerializer.Serialize(links));
    }

    /// <summary>The To do item when there is one, so a renamed copy elsewhere doesn't share it. Otherwise the title.</summary>
    private static string LinkKey(StudyExam exam) =>
        exam.ItemRef is { Length: > 0 } itemRef ? itemRef : $"title:{exam.Title.Trim().ToLowerInvariant()}";

    private Dictionary<string, long> FolderLinks()
    {
        try
        {
            return database.GetPreference(FolderLinksKey) is { Length: > 0 } json
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, long>>(json) ?? []
                : [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>The exam's study note in a folder, if it was made before.</summary>
    public long? FindStudyNote(long folderId, string examTitle)
    {
        var title = StudyGuide.Title(examTitle);
        return database.GetNotesInFolder(folderId)
            .Where(note => string.Equals(note.Title.Trim(), title, StringComparison.OrdinalIgnoreCase))
            .Select(note => (long?)note.Id)
            .LastOrDefault();
    }

    /// <summary>Open exams for a class, soonest first, from today on.</summary>
    public IReadOnlyList<StoredItem> ExamsFor(string course, DateOnly today) =>
        database.GetOpenItems()
            .Where(item => item.Kind == ItemKind.Exam && item.ParentRef is null && (item.DueDate is null || item.DueDate >= today)
                && string.Equals(item.Course?.Trim(), course.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();

    public static StudyExam ExamOf(StoredItem item) =>
        new(item.Title.Trim(), item.Course, item.DueDate, ItemRef.Format(item));

    /// <summary>The exam a study note is for, read from its title and folder, with the To do item when it's still open.</summary>
    public StudyExam? ExamForNote(long noteId, DateOnly today)
    {
        if (database.GetNote(noteId) is not { } note || StudyGuide.ExamTitleOf(note.Title) is not { } title)
        {
            return null;
        }

        var course = database.GetNoteCourse(noteId);
        var item = course is null ? null : ExamsFor(course, today).FirstOrDefault(found => string.Equals(found.Title.Trim(), title, StringComparison.OrdinalIgnoreCase));
        return item is null ? new StudyExam(title, course) : ExamOf(item);
    }

    /// <summary>
    /// Writes practice questions for the exam from the folder's notes and saves them as the exam's study note,
    /// making it the first time. If the user changed it since it was last made, nothing happens until
    /// <paramref name="replaceEdits"/> says yes.
    /// </summary>
    public async Task<StudyOutcome> MakeAsync(StudyExam exam, long folderId, bool replaceEdits, DateTime now, CancellationToken cancellationToken = default)
    {
        if (database.GetNoteFolder(folderId) is not { } folder)
        {
            return new StudyOutcome(StudyStatus.NoFolder, "That notes folder is gone.");
        }

        var title = StudyGuide.Title(exam.Title);
        var existing = FindStudyNote(folderId, exam.Title);
        var expectedBody = "";
        if (existing is { } id)
        {
            if (database.GetNoteGeneration(id) is { EditedSince: true } generation && !replaceEdits)
            {
                return new StudyOutcome(StudyStatus.NeedsConfirm, generation.WasGenerated
                    ? $"You changed {title} after it was made. Making it again replaces your changes."
                    : $"{title} already has your own text. Making questions replaces it.", id);
            }

            expectedBody = database.GetNote(id)?.Body ?? "";
        }

        var material = StudyGuide.Gather(database, folderId, existing);
        if (material.Notes.Count == 0)
        {
            return new StudyOutcome(StudyStatus.NothingToStudy, $"There are no notes in {folder.Name} to study from yet.", existing);
        }

        var asked = StudyGuide.QuestionCount(material.Notes.Count);
        var input = StudyGuide.BuildInput(exam, material, now);
        StudyQuestions questions;
        try
        {
            var json = await runner.RunAsync(StudyGuide.SystemPrompt, StudyGuide.Schema, input, cancellationToken);
            questions = StudyGuide.ParseResult(json);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var failed = exception is OperationCanceledException ? NoteJobText.StudyStopped() : NoteJobText.StudyFailed(PlainProblem.Report(exception));
            return failed with { NoteId = existing };
        }

        questions = await TopUpAsync(input, questions, asked, cancellationToken);

        var markdown = StudyGuide.ToMarkdown(exam, material, questions, folder.Name);
        var noteId = existing ?? database.CreateNote(title, "", now, folderId);
        var outcome = Save(noteId, markdown, expectedBody, now);
        if (outcome.Status == StudyStatus.Made)
        {
            var count = questions.Questions.Count;
            var message = $"Made {title} with {(count == 1 ? "1 practice question" : $"{count} practice questions")}.";
            if (count < asked)
            {
                message += $" Claude wrote {count} of the {asked} asked for. Choose Make new questions to try for more.";
            }

            if (material.Cut)
            {
                message += " The notes were long, so only part of them was used.";
            }

            return outcome with { Message = message };
        }

        return outcome;
    }

    /// <summary>
    /// Haiku tends to write fewer questions than asked for, so a short answer is asked about once more for the rest.
    /// If that fails or adds nothing, the first questions are kept as they were.
    /// </summary>
    private async Task<StudyQuestions> TopUpAsync(string input, StudyQuestions questions, int asked, CancellationToken cancellationToken)
    {
        if (!StudyGuide.IsShort(questions, asked))
        {
            return questions;
        }

        try
        {
            var json = await runner.RunAsync(StudyGuide.SystemPrompt, StudyGuide.Schema, StudyGuide.BuildFollowUp(input, questions, asked), cancellationToken);
            return StudyGuide.Merge(questions, StudyGuide.ParseResult(json), asked);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return questions;
        }
    }

    /// <summary>Saves questions written while the study note was being typed in, after the user said to replace it.</summary>
    public StudyOutcome SaveAnyway(long noteId, string markdown, DateTime now) =>
        database.GetNote(noteId) is { } note ? Save(noteId, markdown, note.Body, now) : new StudyOutcome(StudyStatus.Gone, "That note is gone.");

    private StudyOutcome Save(long noteId, string markdown, string expectedBody, DateTime now)
    {
        var saved = database.SaveGeneratedNotes(noteId, markdown, expectedBody, now);
        return saved.Result switch
        {
            GeneratedSaveResult.Gone => new StudyOutcome(StudyStatus.Gone, "That note is gone."),
            GeneratedSaveResult.Changed => new StudyOutcome(StudyStatus.ChangedWhileWriting, "The note changed while the questions were being written, so nothing was replaced yet.", noteId, markdown),
            _ => new StudyOutcome(StudyStatus.Made, "Made the practice questions.", noteId, markdown, saved.Before),
        };
    }
}
