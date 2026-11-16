using Waypoint.Core.Theme;

namespace Waypoint.Core.Tests;

public sealed class HeadingsTests
{
    [Theory]
    [InlineData("THIS WEEK", "This week")]
    [InlineData("WHAT'S HELPING", "What's helping")]
    [InlineData("SPENT IN OCTOBER SO FAR", "Spent in October so far")]
    [InlineData("CARDS ON NOV 30", "Cards on Nov 30")]
    [InlineData("NET WORTH, LAST 90 DAYS", "Net worth, last 90 days")]
    public void Capitals_become_sentence_case(string heading, string shown) =>
        Assert.Equal(shown, Headings.Sentence(heading));

    [Theory]
    [InlineData("Today")]
    [InlineData("Wednesday, Nov 12")]
    [InlineData("No recent purchase fits. Type which one it was, or choose Not a repayment.")]
    [InlineData("")]
    public void Text_already_in_words_is_left_alone(string heading) =>
        Assert.Equal(heading, Headings.Sentence(heading));
}
