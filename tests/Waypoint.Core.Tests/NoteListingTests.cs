using Waypoint.Core.Capture;
using Waypoint.Core.Notes;

namespace Waypoint.Core.Tests;

/// <summary>The line a list of notes shows under each title, read as words rather than Markdown.</summary>
public sealed class NoteListingTests
{
    [Theory]
    [InlineData("\\# Week 9", "# Week 9")]
    [InlineData("1\\. not a list", "1. not a list")]
    [InlineData("2 \\* 3 \\* 4", "2 * 3 * 4")]
    [InlineData("Some **bold**, *slanted*, ~~gone~~ and `code`", "Some bold, slanted, gone and code")]
    [InlineData("see [the docs](https://example.com/a) and ![a diagram](a.png)", "see the docs and a diagram")]
    [InlineData("<u>lined</u> words", "lined words")]
    [InlineData("snake_case_name and a * b", "snake_case_name and a * b")]
    [InlineData("a \\*\\*not bold\\*\\* line", "a **not bold** line")]
    public void A_line_reads_as_its_words(string markdown, string plain) =>
        Assert.Equal(plain, NoteListing.Plain(markdown));

    [Theory]
    [InlineData("Week 9", "# Week 9\n\n\\# Week 9 is the hard one", "# Week 9 is the hard one")]
    [InlineData("Shopping", "Shopping\n- [ ] milk\n- [x] eggs", "milk")]
    [InlineData("Lab", "## Steps\n1. Mix", "Steps")]
    public void The_preview_is_the_first_line_past_the_title_as_words(string title, string body, string preview) =>
        Assert.Equal(preview, NoteListing.Preview(title, body));

    [Fact]
    public void A_line_the_server_already_shortened_reads_the_same_on_the_pc()
    {
        var slim = NoteListing.Slim(new NoteDetail(1, "Week 9", "Week 9\n\\# tests on **Friday**", null, new DateTime(2026, 11, 3))).Body;

        Assert.Equal("# tests on Friday", NoteListing.Preview("Week 9", slim));
    }
}
