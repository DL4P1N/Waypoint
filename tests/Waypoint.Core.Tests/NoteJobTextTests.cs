using Waypoint.Core.Notes;

namespace Waypoint.Core.Tests;

/// <summary>
/// Writing notes where it runs on this PC, or for Discord, ends the same way as on the server's jobs: a failure
/// says plainly why and that it can be tried again, and never shows the exception's own text.
/// </summary>
public sealed class NoteJobTextTests
{
    [Fact]
    public async Task Notes_that_break_off_say_writing_notes_failed_and_why()
    {
        var outcome = await NoteJobText.GuardNotes(() => throw new TimeoutException(@"Timed out reading C:\Users\sam\week 2.md"));

        Assert.Equal(NoteWriteStatus.Failed, outcome.Status);
        Assert.StartsWith("Writing notes failed: It took too long.", outcome.Message);
        Assert.Contains("try again", outcome.Message);
        Assert.DoesNotContain("sam", outcome.Message);
    }

    [Fact]
    public async Task Notes_stopped_on_purpose_say_they_were_stopped()
    {
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();

        var outcome = await NoteJobText.GuardNotes(() => throw new OperationCanceledException(stop.Token), stop.Token);

        Assert.Equal(NoteWriteStatus.Failed, outcome.Status);
        Assert.Contains("stopped", outcome.Message);
    }

    [Fact]
    public async Task Notes_that_finish_come_back_as_they_are()
    {
        var written = new NoteWriteOutcome(NoteWriteStatus.Written, "Notes written for Chemistry 110.");

        Assert.Same(written, await NoteJobText.GuardNotes(() => Task.FromResult(written)));
    }

    [Fact]
    public async Task Practice_questions_that_break_off_say_why()
    {
        var outcome = await NoteJobText.GuardStudy(() => throw new InvalidOperationException("Not logged in · Please run /login"));

        Assert.Equal(StudyStatus.Failed, outcome.Status);
        Assert.StartsWith("Writing practice questions failed: Claude isn't signed in.", outcome.Message);
    }
}
