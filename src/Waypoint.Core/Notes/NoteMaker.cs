using Waypoint.Core.Data;
using Waypoint.Core.Files;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Notes;

public enum NoteWriteStatus
{
    Written,

    /// <summary>The note has text of the user's own that writing would replace, so it waits for a yes.</summary>
    NeedsConfirm,

    /// <summary>The note was typed in while the notes were being written. Nothing was replaced.</summary>
    ChangedWhileWriting,
    Failed,
    NothingToWriteFrom,
    Gone,
}

/// <summary>What came of writing notes. Before is the text they replaced, for undo.</summary>
public sealed record NoteWriteOutcome(NoteWriteStatus Status, string Message, WrittenNotes? Notes = null, NoteBodyState? Before = null);

/// <summary>Where a new note goes. Course is set when it was linked to a class, and Title is the class title it gets.</summary>
public sealed record NotePlacement(long? FolderId, string? Course = null, string? Title = null);

/// <summary>A note made from a file, or why none was made.</summary>
public sealed record StartedNote(long NoteId, string Title, long? FolderId, string? Course, string? Problem = null);

/// <summary>
/// Makes class notes from files. The file's text is saved as a source on the note before Haiku is asked
/// anything, so a failed or slow call loses nothing and can be tried again. Notes are always written from the
/// sources, never from the note's own text, and text the user changed is only replaced after they say yes.
/// Dates and tasks found are stored as suggestions and never added on their own.
/// </summary>
public sealed class NoteMaker(WaypointDatabase database, INoteWriter writer, ClassSchedule? schedule)
{
    /// <summary>
    /// Where a new typed note goes. A folder the user picked wins, and Unfiled counts as picked. Otherwise a note
    /// started during class goes in that class's folder, titled with the course and date. A note started during class
    /// inside that class's own folder gets the class title too. Anything else is left untitled.
    /// </summary>
    public NotePlacement PlaceNewNote(long? pickedFolderId, bool folderPicked, DateTime now)
    {
        var current = ClassLinking.CurrentClass(schedule, now);
        if (folderPicked)
        {
            return current is not null && pickedFolderId is { } picked && database.FindClassFolder(current.Meeting.Course) == picked
                ? new NotePlacement(picked, current.Meeting.Course, ClassLinking.Title(current.Meeting.Course, now))
                : new NotePlacement(pickedFolderId);
        }

        return current is not null
            ? new NotePlacement(null, current.Meeting.Course, ClassLinking.Title(current.Meeting.Course, now))
            : new NotePlacement(null);
    }

    /// <summary>The folder for a placement, making the class folder the first time a note goes in it.</summary>
    public long? FolderFor(NotePlacement placement, DateTime now) =>
        placement.FolderId ?? (placement.Course is { } course && database.CreateClassFolder(course, now) is { Problem: null } made ? made.Id : null);

    /// <summary>
    /// Makes a note from a file and saves its text as the note's first source. The note goes in the folder the
    /// user picked, else the class the file names, else the class in session, else Unfiled. Writing the notes
    /// is a separate step, <see cref="WriteAsync"/>.
    /// </summary>
    public StartedNote StartFromFile(FileText file, long? pickedFolderId, bool folderPicked, DateTime now, string? keptPath = null)
    {
        if (Unreadable(file) is { } problem)
        {
            return new StartedNote(0, file.Name, null, null, problem);
        }

        var (folder, course) = Place(file.Name, file.Text, pickedFolderId, folderPicked, now);
        var title = TitleFor(file.Name);
        var id = database.CreateNote(title, "", now, folder);
        database.AddNoteSource(id, NoteSourceKind.ForFile(file.Name), file.Name, file.Text!, now, keptPath);
        return new StartedNote(id, title, folder, course);
    }

    /// <summary>
    /// Makes the note a recording turns into, before there's a transcript, so it shows in the list while the
    /// recording is transcribed. It's filed the same way as a file: the folder picked, else a class the file name
    /// names, else the class in session, else Unfiled.
    /// </summary>
    public StartedNote StartForRecording(string fileName, long? pickedFolderId, bool folderPicked, DateTime now, string? title = null)
    {
        var name = Path.GetFileName(fileName);
        var (folder, course) = Place(name, null, pickedFolderId, folderPicked, now);
        title ??= TitleFor(name);
        return new StartedNote(database.CreateNote(title, "", now, folder), title, folder, course);
    }

    /// <summary>
    /// Saves a transcript on its note, exactly as Whisper wrote it, with its timestamps. The notes are written from
    /// it the same way as from any file, with <see cref="WriteAsync"/>.
    /// </summary>
    public StartedNote AttachTranscript(long noteId, string recordingName, string transcript, string? timestampsJson, DateTime now, string? keptAudioPath = null, string? clientId = null)
    {
        if (database.GetNote(noteId) is not { } note)
        {
            return new StartedNote(noteId, recordingName, null, null, "That note is gone.");
        }

        if (string.IsNullOrWhiteSpace(transcript))
        {
            return new StartedNote(noteId, note.Title, note.FolderId, null, $"No speech was found in {recordingName}.");
        }

        database.AddTranscriptSource(noteId, $"Transcript of {recordingName}", transcript, timestampsJson, now, keptAudioPath, clientId);
        return new StartedNote(noteId, note.Title, note.FolderId, database.GetNoteCourse(noteId));
    }

    private (long? Folder, string? Course) Place(string name, string? text, long? pickedFolderId, bool folderPicked, DateTime now)
    {
        if (folderPicked)
        {
            return (pickedFolderId, pickedFolderId is { } picked ? database.GetNoteFolder(picked)?.Course : null);
        }

        var course = ClassLinking.NamedCourse(name, text, database.GetClassNames())
            ?? ClassLinking.CurrentClass(schedule, now)?.Meeting.Course;
        return (FolderFor(new NotePlacement(null, course), now), course);
    }

    private static string TitleFor(string name) => Path.GetFileNameWithoutExtension(name) is { Length: > 0 } stem ? stem : name;

    /// <summary>Adds a file to a note as another source. The notes aren't rewritten until asked.</summary>
    public StartedNote AttachFile(long noteId, FileText file, DateTime now, string? keptPath = null)
    {
        if (Unreadable(file) is { } problem)
        {
            return new StartedNote(noteId, file.Name, null, null, problem);
        }

        if (database.GetNote(noteId) is not { } note)
        {
            return new StartedNote(noteId, file.Name, null, null, "That note is gone.");
        }

        database.AddNoteSource(noteId, NoteSourceKind.ForFile(file.Name), file.Name, file.Text!, now, keptPath);
        return new StartedNote(noteId, note.Title, note.FolderId, database.GetNoteCourse(noteId));
    }

    /// <summary>
    /// Writes the note's text from all of its sources, with <paramref name="request"/> as a change to make. If the
    /// user has text of their own in the note, nothing happens until <paramref name="replaceEdits"/> says yes. Text
    /// typed by hand before any notes were written is kept as a source first, so it's used and never lost.
    /// <paramref name="marks"/> are the moments marked while recording, when they were kept on another machine.
    /// </summary>
    public async Task<NoteWriteOutcome> WriteAsync(long noteId, string? request, bool replaceEdits, DateTime now, CancellationToken cancellationToken = default, IReadOnlyList<Recording.RecordingMark>? marks = null)
    {
        if (database.GetNoteGeneration(noteId) is not { } generation || database.GetNote(noteId) is not { } note)
        {
            return new NoteWriteOutcome(NoteWriteStatus.Gone, "That note is gone.");
        }

        if (generation.SourceCount == 0)
        {
            return new NoteWriteOutcome(NoteWriteStatus.NothingToWriteFrom, "This note has no files to write from. Add one first.");
        }

        if (generation.EditedSince && !replaceEdits)
        {
            return new NoteWriteOutcome(NoteWriteStatus.NeedsConfirm, generation.WasGenerated
                ? "You changed these notes after they were written. Writing them again replaces your changes."
                : "This note already has your own text. It will be kept in Sources and used along with the files.");
        }

        // A retry after a failed call finds the typed text already kept, so it isn't kept twice.
        if (!generation.WasGenerated && !string.IsNullOrWhiteSpace(note.Body)
            && !database.GetNoteSources(noteId).Any(source => source.Kind == NoteSourceKind.Typed && source.Text == note.Body.Trim()))
        {
            database.AddNoteSource(noteId, NoteSourceKind.Typed, $"Typed notes, {now:MMM d}", note.Body.Trim(), now);
        }

        var sources = database.GetNoteSources(noteId);
        WrittenNotes written;
        try
        {
            written = await writer.WriteAsync(sources, new NoteWriteContext(database.GetNoteCourse(noteId), request, now, MarksFor(noteId, sources, marks)), cancellationToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var reason = exception is OperationCanceledException ? "It was stopped." : PlainProblem.Report(exception);
            return new NoteWriteOutcome(NoteWriteStatus.Failed, $"The notes couldn't be written. {reason} The files are saved in Sources, so you can try again.");
        }

        return Save(noteId, written, note.Body, now);
    }

    /// <summary>The moments marked while this note's lecture was recorded, with what was said around each, or null.</summary>
    private string? MarksFor(long noteId, IReadOnlyList<NoteSource> sources, IReadOnlyList<Recording.RecordingMark>? sent)
    {
        var marks = sent is { Count: > 0 } ? sent : database.GetRecordingMarksForNote(noteId);
        if (marks.Count == 0)
        {
            return null;
        }

        var transcript = sources.LastOrDefault(source => source.Kind == NoteSourceKind.Transcript);
        return Recording.MarkNotes.Describe(marks, transcript is null ? null : database.GetSourceTimestamps(transcript.Id));
    }

    /// <summary>Saves notes that were written while the note was being typed in, after the user said to replace it.</summary>
    public NoteWriteOutcome SaveAnyway(long noteId, WrittenNotes written, DateTime now) =>
        database.GetNote(noteId) is { } note ? Save(noteId, written, note.Body, now) : new NoteWriteOutcome(NoteWriteStatus.Gone, "That note is gone.");

    private NoteWriteOutcome Save(long noteId, WrittenNotes written, string expectedBody, DateTime now)
    {
        var saved = database.SaveGeneratedNotes(noteId, written.Markdown, expectedBody, now);
        switch (saved.Result)
        {
            case GeneratedSaveResult.Gone:
                return new NoteWriteOutcome(NoteWriteStatus.Gone, "That note is gone.");
            case GeneratedSaveResult.Changed:
                return new NoteWriteOutcome(NoteWriteStatus.ChangedWhileWriting, "The note changed while the notes were being written, so nothing was replaced yet.", written);
        }

        database.ReplaceNoteSuggestions(noteId, written.Suggestions, now);
        var found = database.GetNoteSuggestions(noteId).Count;
        var message = "Wrote the notes.";
        if (found > 0)
        {
            message += found == 1 ? " Found 1 date or task you might want to add." : $" Found {found} dates or tasks you might want to add.";
        }

        if (written.Cut)
        {
            message += " The files were very long, so only the first part was used.";
        }

        return new NoteWriteOutcome(NoteWriteStatus.Written, message, written, saved.Before);
    }

    /// <summary>A scanned PDF that text recognition couldn't read either is said plainly, and nothing is made from it.</summary>
    private static string? Unreadable(FileText file)
    {
        if (file.Text is { Length: > 0 })
        {
            return null;
        }

        if (file.NoText && NoteSourceKind.ForFile(file.Name) == NoteSourceKind.Pdf)
        {
            return $"{file.Name} has no text I can read. It looks like scanned pages, and text recognition couldn't make them out. Type what matters into a note instead.";
        }

        return file.Problem ?? $"I couldn't find any text in {file.Name}.";
    }
}
