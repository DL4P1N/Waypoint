using Waypoint.Core;
using Waypoint.Core.Remote;

namespace Waypoint.Core.Tests;

public sealed class PlainProblemTests
{
    [Fact]
    public void Paths_and_process_output_never_reach_the_sentence()
    {
        var missing = new FileNotFoundException(@"Could not find file 'C:\Users\me\AppData\Local\Waypoint\backups\x.db'.");
        var claude = new InvalidOperationException("Claude Code exited with code 1. Error: stub sorter is offline");
        var locked = new IOException(@"The process cannot access the file 'C:\Users\me\notes.md' because it is being used by another process.");

        Assert.Equal("A file or folder it needed isn't there anymore.", PlainProblem.Describe(missing));
        Assert.Equal("Claude couldn't be reached just now.", PlainProblem.Describe(claude));
        Assert.Equal("A file it needed is open in another program or couldn't be reached.", PlainProblem.Describe(locked));
        Assert.Equal("Something unexpected went wrong.", PlainProblem.Describe(new ArgumentException("bad arg at 0x1234")));
    }

    [Fact]
    public void Messages_written_for_people_are_kept()
    {
        var away = new ServerUnavailableException("Can't reach the server right now. Try again once it's back.");

        Assert.Equal(away.Message, PlainProblem.Describe(away));
    }

    [Fact]
    public void Report_sends_the_detail_to_the_log()
    {
        var logged = new List<Exception>();
        var before = PlainProblem.Log;
        PlainProblem.Log = logged.Add;
        try
        {
            var problem = new UnauthorizedAccessException(@"Access to the path 'C:\secret' is denied.");

            var said = PlainProblem.Report(problem);

            Assert.Same(problem, Assert.Single(logged));
            Assert.DoesNotContain("secret", said);
        }
        finally
        {
            PlainProblem.Log = before;
        }
    }
}
