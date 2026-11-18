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

    private static string Reason(string? reason) => string.IsNullOrWhiteSpace(reason) ? NoReason : reason.Trim();
}
