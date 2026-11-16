using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using Waypoint.App;

namespace Waypoint.App.Tests;

public sealed class MarkdownSourceBoxTests
{
    private static List<Paragraph> Lines(MarkdownSourceBox box) => box.Document.Blocks.OfType<Paragraph>().ToList();

    [Fact]
    public void Text_comes_back_exactly_as_it_went_in() => Ui.Run(ui =>
    {
        var box = ui.Host(new MarkdownSourceBox());

        box.Text = "# Title\r\nmilk\r\n\r\n- eggs";
        Assert.Equal("# Title\r\nmilk\r\n\r\n- eggs", box.Text);

        box.Text = "one\ntwo";
        Assert.Equal("one\ntwo", box.Text);
    });

    [Fact]
    public void Headings_bold_and_code_are_styled_with_their_marks_left_in() => Ui.Run(ui =>
    {
        var box = ui.Host(new MarkdownSourceBox());
        box.Text = "# Results\nThe **sample** turned `blue`";
        ui.Pump();

        var lines = Lines(box);
        Assert.Equal(24, lines[0].FontSize);
        Assert.Equal(FontWeights.SemiBold, lines[0].FontWeight);
        var runs = lines[1].Inlines.OfType<Run>().ToList();
        Assert.Equal(["The ", "**", "sample", "**", " turned ", "`", "blue", "`"], runs.Select(run => run.Text));
        Assert.Equal(FontWeights.Bold, runs[2].FontWeight);
        Assert.Equal("The **sample** turned `blue`", new TextRange(lines[1].ContentStart, lines[1].ContentEnd).Text);
    });

    [Fact]
    public void Typing_keeps_the_caret_where_it_was_after_restyling() => Ui.Run(ui =>
    {
        var box = ui.Host(new MarkdownSourceBox());
        box.Text = "plain text";
        ui.Focus(box);
        box.CaretIndex = 5;

        box.CaretPosition.InsertTextInRun("**");
        ui.Pump();

        Assert.Equal("plain** text", box.Text);
        Assert.Equal(5, box.CaretIndex);
    });

    [Fact]
    public void Undo_puts_the_text_back_rather_than_the_styling() => Ui.Run(ui =>
    {
        var box = ui.Host(new MarkdownSourceBox());
        box.Text = "milk";
        ui.Focus(box);
        box.CaretIndex = 4;
        var edits = 0;
        box.TextEdited += (_, _) => edits++;

        box.CaretPosition.InsertTextInRun(" and **eggs**");
        ui.Pump();
        Assert.Equal("milk and **eggs**", box.Text);

        ApplicationCommands.Undo.Execute(null, box);
        ui.Pump();

        Assert.Equal("milk", box.Text);
        Assert.Equal(2, edits);
    });

    [Fact]
    public void Ctrl_b_wraps_the_selection_in_markdown() => Ui.Run(ui =>
    {
        var box = ui.Host(new MarkdownSourceBox());
        box.Text = "buy eggs";
        ui.Focus(box);
        box.Selection.Select(box.Document.ContentStart.GetPositionAtOffset(0)!, box.Document.ContentStart.GetPositionAtOffset(0)!);
        box.CaretIndex = 4;
        var start = box.CaretPosition;
        box.CaretIndex = 8;
        box.Selection.Select(start, box.CaretPosition);

        EditingCommands.ToggleBold.Execute(null, box);
        ui.Pump();

        Assert.Equal("buy **eggs**", box.Text);
    });

    [Fact]
    public void Splitting_joining_and_pasting_lines_keeps_the_text_right() => Ui.Run(ui =>
    {
        var box = ui.Host(new MarkdownSourceBox());
        box.Text = "one\ntwo\nthree";
        ui.Focus(box);

        box.CaretIndex = 2;
        EditingCommands.EnterParagraphBreak.Execute(null, box);
        ui.Pump();
        Assert.Equal("on\ne\ntwo\nthree", box.Text);

        box.CaretIndex = 5;
        EditingCommands.Backspace.Execute(null, box);
        ui.Pump();
        Assert.Equal("on\netwo\nthree", box.Text);

        box.CaretIndex = box.Text.Length;
        box.Selection.Text = "# a\nb";
        ui.Pump();
        Assert.Equal("on\netwo\nthree# a\nb", box.Text);

        box.Selection.Select(Lines(box)[0].ContentStart, Lines(box)[2].ContentEnd);
        box.Selection.Text = "x";
        ui.Pump();
        Assert.Equal("x\nb", box.Text);
    });

    [Fact]
    public void Typing_in_a_long_transcript_stays_quick() => Ui.Run(ui =>
    {
        var lines = Enumerable.Range(0, 2_000).Select(i => $"[{i / 3600:00}:{i / 60 % 60:00}:{i % 60:00}] This is line {i} of the lecture, about **cells** and membranes.");
        var box = ui.Host(new MarkdownSourceBox());
        box.Text = string.Join("\r\n", lines);
        ui.Pump();
        ui.Focus(box);
        box.CaretIndex = box.Text.Length / 2;

        void Key()
        {
            box.Selection.Text = "x";
            box.Selection.Select(box.Selection.End, box.Selection.End);
            ui.Pump();
        }

        Key();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        const int typed = 20;
        for (var i = 0; i < typed; i++)
        {
            Key();
        }

        var perKey = watch.Elapsed.TotalMilliseconds / typed;

        // Reading every line again for each key took about 300 ms a key here.
        Assert.Contains(new string('x', typed + 1), box.Text, StringComparison.Ordinal);
        Assert.True(perKey < 120, $"Typing took {perKey:0.0} ms a key in Show Markdown on a 2,000 line note");
    });

    [Fact]
    public void A_note_with_mixed_line_endings_comes_back_exactly_and_ctrl_b_at_its_end_works() => Ui.Run(ui =>
    {
        var box = ui.Host(new MarkdownSourceBox());
        box.Text = "a\nb\r\nc";
        Assert.Equal("a\nb\r\nc", box.Text);
        ui.Focus(box);

        box.CaretIndex = int.MaxValue;
        EditingCommands.ToggleBold.Execute(null, box);
        ui.Pump();

        Assert.Equal("a\r\nb\r\nc****", box.Text);
    });
}
