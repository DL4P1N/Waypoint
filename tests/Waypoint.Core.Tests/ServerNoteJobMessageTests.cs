using System.Net;
using Waypoint.Core.Notes;
using Waypoint.Core.Remote;

namespace Waypoint.Core.Tests;

/// <summary>
/// What the PC says when the server's notes job ends without notes. A job that failed says why and that it can be
/// tried again. A job the server lost, as after a restart, says so instead, since nothing went wrong in it.
/// </summary>
public sealed class ServerNoteJobMessageTests
{
    /// <summary>Starts every job as "job-1" and answers checks on it with <paramref name="state"/>, as JSON.</summary>
    private sealed class JobServer(string state) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Method == HttpMethod.Get && path == "/api/notes/jobs/job-1" ? state : """{"id":"job-1"}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private static ServerNotesData Notes(string state) =>
        new(new ServerClient(new HttpClient(new JobServer(state)) { BaseAddress = new Uri("http://server/") }, "made-up-token"))
        {
            JobPollInterval = TimeSpan.FromMilliseconds(1),
        };

    private const string Failed = """{"done":true,"gone":false,"outcome":"Failed","problem":"Claude isn't signed in."}""";
    private const string Lost = """{"done":false,"gone":true,"outcome":"Lost"}""";
    private const string LostBefore = """{"done":false,"gone":true}""";
    private const string Cancelled = """{"done":true,"gone":false,"outcome":"Cancelled"}""";

    [Fact]
    public async Task A_failed_job_says_writing_notes_failed_and_why()
    {
        var outcome = await Notes(Failed).WriteNotesAsync(7, null, replaceEdits: true, DateTime.Now);

        Assert.Equal(NoteWriteStatus.Failed, outcome.Status);
        Assert.StartsWith("Writing notes failed: Claude isn't signed in.", outcome.Message);
        Assert.Contains("try again", outcome.Message);
        Assert.DoesNotContain("restart", outcome.Message);
    }

    [Fact]
    public async Task A_failed_job_without_a_reason_still_says_it_failed()
    {
        var outcome = await Notes("""{"done":true,"outcome":"Failed"}""").WriteNotesAsync(7, null, replaceEdits: true, DateTime.Now);

        Assert.StartsWith("Writing notes failed: Something unexpected went wrong.", outcome.Message);
    }

    [Theory]
    [InlineData(Lost)]
    [InlineData(LostBefore)]
    public async Task A_lost_job_says_the_server_restarted(string state)
    {
        var outcome = await Notes(state).WriteNotesAsync(7, null, replaceEdits: true, DateTime.Now);

        Assert.Equal(NoteWriteStatus.Failed, outcome.Status);
        Assert.Contains("restarted", outcome.Message);
        Assert.DoesNotContain("Writing notes failed", outcome.Message);
        Assert.Contains("try again", outcome.Message);
    }

    [Fact]
    public async Task A_cancelled_job_says_it_was_stopped()
    {
        var outcome = await Notes(Cancelled).WriteNotesAsync(7, null, replaceEdits: true, DateTime.Now);

        Assert.Equal(NoteWriteStatus.Failed, outcome.Status);
        Assert.Contains("stopped", outcome.Message);
    }

    [Fact]
    public async Task Practice_questions_tell_failed_from_lost_too()
    {
        var exam = new StudyExam("Midterm", "BIO 101");

        var failed = await Notes(Failed).MakeStudyAsync(exam, 3, false, DateTime.Now);
        var lost = await Notes(Lost).MakeStudyAsync(exam, 3, false, DateTime.Now);

        Assert.Equal(StudyStatus.Failed, failed.Status);
        Assert.StartsWith("Writing practice questions failed: Claude isn't signed in.", failed.Message);
        Assert.Equal(StudyStatus.Failed, lost.Status);
        Assert.Contains("restarted", lost.Message);
        Assert.DoesNotContain("failed:", lost.Message);
    }
}
