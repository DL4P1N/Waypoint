namespace Waypoint.App.Tests;

/// <summary>A problem nothing else caught is said once in a plain line, and again only when it's a different one.</summary>
public sealed class UnexpectedProblemsTests
{
    private static Exception Thrown(Func<Exception> make)
    {
        try
        {
            throw make();
        }
        catch (Exception caught)
        {
            return caught;
        }
    }

    [Fact]
    public void The_same_problem_is_said_once_and_a_different_one_is_said_too()
    {
        var problems = new UnexpectedProblems();

        var first = problems.FirstTime(Thrown(() => new InvalidOperationException("The list moved under it")));
        var again = problems.FirstTime(Thrown(() => new InvalidOperationException("The list moved under it")));
        var other = problems.FirstTime(Thrown(() => new ArgumentException("No such row")));

        Assert.Equal("Something unexpected went wrong. Waypoint kept going, and the detail is in errors.log.", first);
        Assert.Null(again);
        Assert.NotNull(other);
    }

    [Fact]
    public void The_line_is_plain_and_never_shows_the_technical_detail()
    {
        var line = new UnexpectedProblems().FirstTime(Thrown(() => new NullReferenceException("Object reference not set")));

        Assert.DoesNotContain("Object reference", line);
        Assert.DoesNotContain("Exception", line);
        Assert.DoesNotContain("—", line);
    }
}
