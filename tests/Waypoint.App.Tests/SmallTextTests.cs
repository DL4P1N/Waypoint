using System.IO;
using System.Text.RegularExpressions;

namespace Waypoint.App.Tests;

public sealed class SmallTextTests
{
    private static string Source(string file) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Waypoint.App", file));

    [Theory]
    [InlineData("FeedbackPrompt.cs")]
    [InlineData("ReminderWindow.xaml")]
    [InlineData("ReminderWindow.xaml.cs")]
    [InlineData("WeekView.xaml")]
    [InlineData("WeekView.xaml.cs")]
    public void Cards_and_the_week_use_text_no_smaller_than_12(string file)
    {
        var sizes = Regex.Matches(Source(file), @"FontSize\s*=\s*""?(\d+(\.\d+)?)").Select(match => double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));

        Assert.All(sizes, size => Assert.True(size >= 12, $"{file} has text at {size}"));
    }
}
