using System.Windows.Documents;
using Waypoint.App;

namespace Waypoint.App.Tests;

public sealed class MarkdownDocumentTests
{
    // MarkdownDocument is internal to the app, so it's reached the same way the other tests reach private parts.
    private static FlowDocument Render(string markdown) =>
        (FlowDocument)typeof(NotesView).Assembly.GetType("Waypoint.App.MarkdownDocument")!.GetMethod("Render")!.Invoke(null, [markdown])!;

    [Fact]
    public void Each_typed_line_stays_on_its_own_line() => Ui.Run(_ =>
    {
        var document = Render("Fix tab order\r\nMake accents gray\r\nLowest point doesn't work");

        var paragraph = Assert.IsType<Paragraph>(Assert.Single(document.Blocks));
        Assert.Equal(2, paragraph.Inlines.OfType<LineBreak>().Count());
        Assert.Equal(["Fix tab order", "Make accents gray", "Lowest point doesn't work"], paragraph.Inlines.OfType<Run>().Select(run => run.Text));
    });
}
