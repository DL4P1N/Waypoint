namespace Waypoint.Core.Notes;

/// <summary>
/// What the person is told when writing notes or practice questions ends without them, the same in local and server
/// mode. Reasons are plain sentences from <see cref="PlainProblem"/>, never an exception's own text.
/// </summary>
public static class NoteJobText
{
    public const string NoReason = "Something unexpected went wrong.";

    public static NoteWriteOutcome NotesFailed(string? reason) =>
        new(NoteWriteStatus.Failed, $"Writing notes failed: {Reason(reason)} The files are saved in Sources, so you can try again.");

    public static NoteWriteOutcome NotesLost() =>
        new(NoteWriteStatus.Failed, "The notes couldn't be written, because the server restarted while writing them. The files are saved in Sources, so you can try again.");

    public static NoteWriteOutcome NotesStopped() =>
        new(NoteWriteStatus.Failed, "The notes couldn't be written. It was stopped. The files are saved in Sources, so you can try again.");

    public static StudyOutcome StudyFailed(string? reason) =>
        new(StudyStatus.Failed, $"Writing practice questions failed: {Reason(reason)} You can try again.");

    public static StudyOutcome StudyLost() =>
        new(StudyStatus.Failed, "The practice questions couldn't be written, because the server restarted while writing them. You can try again.");

    public static StudyOutcome StudyStopped() =>
        new(StudyStatus.Failed, "The practice questions couldn't be written. It was stopped. You can try again.");

    /// <summary>
    /// Writes notes where an exception would otherwise go unseen, such as on the Notes page in local mode or for
    /// Discord, so a failure ends as it does for the server's jobs. The detail goes to the log.
    /// </summary>
    public static async Task<NoteWriteOutcome> GuardNotes(Func<Task<NoteWriteOutcome>> write, CancellationToken cancellationToken = default)
    {
        try
        {
            return await write();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return NotesStopped();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return NotesFailed(PlainProblem.Report(exception));
        }
    }

    /// <summary>The same for practice questions.</summary>
    public static async Task<StudyOutcome> GuardStudy(Func<Task<StudyOutcome>> make, CancellationToken cancellationToken = default)
    {
        try
        {
            return await make();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StudyStopped();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return StudyFailed(PlainProblem.Report(exception));
        }
    }

    private static string Reason(string? reason) => string.IsNullOrWhiteSpace(reason) ? NoReason : reason.Trim();
}
