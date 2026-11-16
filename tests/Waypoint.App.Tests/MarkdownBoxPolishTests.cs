using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using Waypoint.App;

namespace Waypoint.App.Tests;

/// <summary>Word-like details: Enter before a table, the caret's formatting, and links pasted as Markdown.</summary>
public sealed partial class MarkdownBoxTests
{
    [Theory]
    [InlineData("| t |\n| - |\n| 1 |", "")]
    [InlineData("before\n\n| t |\n| - |\n| 1 |", "before\n\n")]
    [InlineData("<div>\nhtml\n</div>", "")]
    public void Enter_at_the_start_of_a_table_puts_the_new_line_above_it(string note, string before) => Ui.Run(ui =>
    {
        var box = Open(ui, note);
        var table = box.Document.Blocks.Single(block => block is BlockUIContainer);
        box.CaretPosition = table.ContentStart;

        Press(box, Key.Enter);
        Type(box, "above");

        Assert.Equal(before + "above\n\n" + note[before.Length..], Md(box));
        Assert.IsType<BlockUIContainer>(box.Document.Blocks.LastBlock);
    });

    [Fact]
    public void Enter_at_the_very_start_of_a_note_that_begins_with_a_table_puts_the_line_above() => Ui.Run(ui =>
    {
        var box = Open(ui, "| t |\n| - |\n| 1 |\n\nafter");
        box.CaretPosition = box.Document.ContentStart;

        Press(box, Key.Enter);
        Type(box, "above");

        Assert.Equal("above\n\n| t |\n| - |\n| 1 |\n\nafter", Md(box));
    });

    /// <summary>
    /// Types through WPF's own text input, as the keyboard does, so formatting set for the next typing (such as
    /// Ctrl+B with nothing selected) is used. Enter goes to the editor's key handling.
    /// </summary>
    private static void KeyType(UiTest ui, MarkdownBox box, string text)
    {
        foreach (var ch in text)
        {
            if (ch == '\n')
            {
                Press(box, Key.Enter);
                continue;
            }

            var preview = new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, box, ch.ToString())) { RoutedEvent = TextCompositionManager.PreviewTextInputEvent };
            box.RaiseEvent(preview);
            if (!preview.Handled)
            {
                box.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, box, ch.ToString())) { RoutedEvent = TextCompositionManager.TextInputEvent });
            }

            ui.Pump();
        }
    }

    /// <summary>The caret as a click or an arrow key can leave it at a formatting edge: at the start of the run after it.</summary>
    private static void CaretAtStartOfRunAfter(MarkdownBox box, int line, int column)
    {
        var paragraph = box.Document.Blocks.SelectMany(Paragraphs).ElementAt(line);
        var run = paragraph.Inlines.SelectMany(Runs).First(run => run.Text.Length > 0 && MarkdownBox.CharOffset(paragraph, run.ContentStart) == column);
        box.CaretPosition = run.ContentStart.GetPositionAtOffset(0, LogicalDirection.Forward)!;
    }

    private static IEnumerable<Run> Runs(Inline inline) => inline switch
    {
        Run run => [run],
        Span span => span.Inlines.SelectMany(Runs),
        _ => [],
    };

    [Theory]
    [InlineData("Some **bold** text", 9, "BoldButton", true, "Some **bolder** text")]
    [InlineData("Some *slanted* text", 12, "ItalicButton", true, "Some *slanteder* text")]
    [InlineData("Some <u>lined</u> text", 10, "UnderlineButton", true, "Some <u>lineder</u> text")]
    [InlineData("Some ~~gone~~ text", 9, "StrikeButton", true, "Some ~~goneer~~ text")]
    [InlineData("plain **bold**", 6, "BoldButton", false, "plain er**bold**")]
    [InlineData("**Bold** start", 0, "BoldButton", true, "**erBold** start")]
    [InlineData("plain **bold**", 0, "BoldButton", false, "erplain **bold**")]
    public void The_caret_has_the_formatting_of_the_character_before_it_and_typing_continues_it(string note, int column, string button, bool on, string typed) => Ui.Run(ui =>
    {
        var box = Open(ui, note);
        var toolbar = new NoteToolbar();
        toolbar.Attach(box);
        CaretAtStartOfRunAfter(box, 0, column);
        ui.Pump();

        Assert.Equal(on, UiTest.Named<System.Windows.Controls.Primitives.ToggleButton>(toolbar, button).IsChecked);

        KeyType(ui, box, "er");

        Assert.Equal(typed, Md(box));
    });

    [Fact]
    public void After_a_bold_word_and_an_arrow_key_the_toolbar_and_typing_agree() => Ui.Run(ui =>
    {
        var box = Open(ui, "Some **bold** text");
        Caret(box, 0, 10);
        EditingCommands.MoveLeftByCharacter.Execute(null, box);
        ui.Pump();

        Assert.True(box.CurrentFormat().Bold);
        KeyType(ui, box, "!");
        Assert.Equal("Some **bold!** text", Md(box));
    });

    [Fact]
    public void A_typed_bold_shortcut_still_goes_back_to_plain_and_a_new_line_after_bold_starts_plain() => Ui.Run(ui =>
    {
        var box = Open(ui, "");

        KeyType(ui, box, "**bold**");
        Assert.False(box.CurrentFormat().Bold);
        KeyType(ui, box, " more");
        Assert.Equal("**bold** more", Md(box));

        var again = Open(ui, "**bold**");
        AtEnd(again);
        Assert.True(again.CurrentFormat().Bold);
        KeyType(ui, again, "\nnext");
        Assert.Equal("**bold**\nnext", Md(again));
    });

    [Fact]
    public void Ctrl_B_with_nothing_selected_still_makes_the_next_typing_bold_after_plain_or_bold_text() => Ui.Run(ui =>
    {
        var box = Open(ui, "plain **bold**");
        CaretAtStartOfRunAfter(box, 0, 6);
        EditingCommands.ToggleBold.Execute(null, box);
        Assert.True(box.CurrentFormat().Bold);
        KeyType(ui, box, "x");
        Assert.Equal("plain **xbold**", Md(box));

        AtEnd(box);
        EditingCommands.ToggleBold.Execute(null, box);
        Assert.False(box.CurrentFormat().Bold);
        KeyType(ui, box, "y");
        Assert.Equal("plain **xbold**y", Md(box));
    });

    [Fact]
    public void Right_after_a_link_typing_is_not_part_of_the_link() => Ui.Run(ui =>
    {
        var box = Open(ui, "see [docs](https://example.com) now");
        CaretAtStartOfRunAfter(box, 0, 8);
        ui.Pump();

        KeyType(ui, box, "!");

        Assert.Equal("see [docs](https://example.com)! now", Md(box));
    });

    [Theory]
    [InlineData("see [run](javascript:alert(1)) now", "see run now")]
    [InlineData("a [file](file:///C:/notes/a.txt) b", "a file b")]
    [InlineData("<file:///C:/notes/a.txt>", "file:///C:/notes/a.txt")]
    [InlineData("[**bold run**](vbscript:x) [ok](https://example.com) [mail](mailto:a@example.com)", "**bold run** [ok](https://example.com) [mail](mailto:a@example.com)")]
    [InlineData("- [go](ms-settings:display)\n- two", "- go\n- two")]
    public void Markdown_pasted_as_text_keeps_only_web_and_mail_links(string pasted, string expected) => Ui.Run(ui =>
    {
        var box = Open(ui, "");

        box.Paste(Clip(text: pasted), plain: false);

        Assert.Equal(expected, Md(box).TrimEnd('\n'));
    });

    [Fact]
    public void Markdown_copied_from_a_note_with_an_old_file_link_pastes_as_its_text() => Ui.Run(ui =>
    {
        var note = "[report](file:///C:/a.pdf)";
        var box = Open(ui, note);
        Assert.Equal(note, box.Text);
        var data = new DataObject();
        data.SetData(MarkdownBox.MarkdownFormat, note);
        data.SetData(DataFormats.UnicodeText, "report");
        AtEnd(box);
        Type(box, "\n");

        box.Paste(data, plain: false);

        Assert.Equal(note + "\nreport", Md(box));
    });
}
