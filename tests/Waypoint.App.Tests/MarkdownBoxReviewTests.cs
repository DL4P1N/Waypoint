using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using Waypoint.App;

namespace Waypoint.App.Tests;

/// <summary>Notes that are unusual on disk, and edits that must change only what was edited.</summary>
public sealed partial class MarkdownBoxTests
{
    public static IEnumerable<object[]> OddNotes() =>
    [
        ["a\r\nb\nc\r\n"],
        ["a\rb\r\nc"],
        ["1) one\n2) two\n\n5. five\n6. six\n"],
        ["﻿# Title\n"],
        ["  # indented heading\n"],
        ["# Closed heading ##\n"],
        ["#\n"],
        ["-\n"],
        ["1.\n"],
        [">\n"],
        ["```\nunclosed code\n"],
        ["~~~\nx\n~~~~\n"],
        ["<!-- a comment -->\n\ntext\n"],
        ["***\n- - -\n___\n"],
        ["a\\\nb\n"],
        ["&nbsp;&amp; &copy;\n"],
        ["line with nbsp here\n"],
        ["\t- tab bullet\n"],
        ["- a\n- b\n\n\n- c\n"],
        ["[a]: https://example.com\n\nsee [a] and [b][a]\n"],
        ["Text\n- list right after text\n"],
        ["> quote\ncontinued lazily\n"],
        ["> - list in quote\n"],
        ["- item\n  > quote in item\n"],
        ["<div>\nhtml block\n</div>\n"],
        ["Inline <span>html</span> here\n"],
        ["A [<u>underlined link</u>](https://example.com) and [**bold link**](https://example.com/b)\n"],
        ["*a\nb*\n"],
        ["\\# not heading\n\\- not bullet\n"],
        ["- [ ] \n- [x]\n"],
        ["    \n\t\n"],
        ["word\u0000null\n"],
        ["Emoji \U0001F600 and ä é 中文\n"],
        ["Very long line " + new string('x', 5000) + "\n"],
    ];

    [Theory]
    [MemberData(nameof(OddNotes))]
    [InlineData("1. a\n1. b\n1. c\n")]
    public void An_odd_note_nobody_changed_comes_back_exactly_as_it_was(string note) => Ui.Run(ui =>
    {
        var box = Open(ui, note);

        Assert.Equal(note, box.Text);
    });

    /// <summary>A line that writes the same as when it was loaded writes its original Markdown, on every kind of line.</summary>
    [Theory]
    [MemberData(nameof(Corpus))]
    [MemberData(nameof(OddNotes))]
    public void Typing_a_letter_and_deleting_it_on_any_line_leaves_the_note_as_it_was(string note) => Ui.Run(ui =>
    {
        var box = Open(ui, note);
        var lines = box.Document.Blocks.SelectMany(Paragraphs).Count();
        for (var line = 0; line < lines; line++)
        {
            var paragraph = box.Document.Blocks.SelectMany(Paragraphs).ElementAt(line);
            if (MarkdownBox.Info(MarkdownBox.TopOf(paragraph.ContentStart)).Kind is LineKind.Code)
            {
                continue;
            }

            box.CaretPosition = paragraph.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
            Type(box, "x");
            EditingCommands.Backspace.Execute(null, box);
            Assert.Equal(note, box.Text);
        }
    });

    [Theory]
    [InlineData("- ", "\\- x")]
    [InlineData("## ", "\\## x")]
    [InlineData("1. ", "1\\. x")]
    public void Undo_right_after_a_typing_shortcut_gives_back_the_typed_marks_with_the_caret_after_them(string typed, string expected) => Ui.Run(ui =>
    {
        var box = Open(ui, "");
        Type(box, typed);
        Assert.NotEqual("", Md(box));

        ApplicationCommands.Undo.Execute(null, box);
        Assert.Equal(typed, new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text.TrimEnd('\r', '\n'));
        Type(box, "x");

        Assert.Equal(expected, Md(box));
    });

    [Fact]
    public void Undo_of_a_command_puts_the_caret_back_where_it_was_before_the_command() => Ui.Run(ui =>
    {
        var box = Open(ui, "- one two");
        Caret(box, 0, 3);
        Press(box, Key.D8, ModifierKeys.Control | ModifierKeys.Shift);
        Assert.Equal("one two", Md(box));

        ApplicationCommands.Undo.Execute(null, box);
        Type(box, "!");

        Assert.Equal("- one! two", Md(box));
    });

    [Fact]
    public void Editing_a_numbered_list_keeps_the_numbers_of_a_list_after_it() => Ui.Run(ui =>
    {
        var box = Open(ui, "1) one\n2) two\n\n5. five\n6. six");
        Caret(box, 1, 3);

        Type(box, "!");

        Assert.Equal("1) one\n2) two!\n\n5. five\n6. six", Md(box));
    });

    [Fact]
    public void Pasting_a_web_address_over_selected_text_makes_the_text_a_link() => Ui.Run(ui =>
    {
        var box = Open(ui, "read the docs now");
        Select(box, 0, 9, 13);

        box.Paste(Clip(text: " https://example.com/docs "), plain: false);
        Assert.Equal("read the [docs](https://example.com/docs) now", Md(box));

        // Anything that isn't one web or mail address pastes as usual, and plain paste always does.
        Select(box, 0, 0, 4);
        box.Paste(Clip(text: "javascript:alert(1)"), plain: false);
        Assert.StartsWith("javascript:alert(1) the", Md(box), StringComparison.Ordinal);
        Select(box, 0, 0, 10);
        box.Paste(Clip(text: "https://example.com"), plain: true);
        Assert.StartsWith("https://example.com:alert(1) the", Md(box), StringComparison.Ordinal);
    });

    [Fact]
    public void Ctrl_click_on_a_mail_link_starts_an_email() => Ui.Run(ui =>
    {
        var box = Open(ui, "Write to [me](mailto:me@example.com).");
        var link = box.Document.Blocks.OfType<Paragraph>().First().Inlines.OfType<Hyperlink>().Single();
        var opened = new List<string>();
        var before = MarkdownBox.OpenTarget;
        MarkdownBox.OpenTarget = opened.Add;
        try
        {
            typeof(MarkdownBox).GetMethod("OpenLink", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(box, [link]);
        }
        finally
        {
            MarkdownBox.OpenTarget = before;
        }

        Assert.Equal(["mailto:me@example.com"], opened);
    });

    [Fact]
    public void Pasting_lines_into_a_list_item_makes_each_one_an_item() => Ui.Run(ui =>
    {
        var box = Open(ui, "1. abc\n2. def");
        Caret(box, 0, 1);

        box.Paste(Clip(text: "one\ntwo\n\nthree"), plain: false);

        Assert.Equal("1. aone\n2. two\n3. threebc\n4. def", Md(box));
    });

    [Fact]
    public void Pasting_lines_into_a_quote_keeps_them_in_the_quote() => Ui.Run(ui =>
    {
        var box = Open(ui, "> abc");
        Caret(box, 0, 3);

        box.Paste(Clip(text: "one\ntwo"), plain: true);

        Assert.Equal("> abcone\n> two", Md(box));
    });

    [Fact]
    public void Clear_formatting_takes_the_marks_off_and_keeps_links_and_headings() => Ui.Run(ui =>
    {
        var box = Open(ui, "## A **bold** *it* [<u>link</u>](https://example.com) `code` ~~x~~");
        box.SelectAll();

        Press(box, Key.Space, ModifierKeys.Control);

        Assert.Equal("## A bold it [link](https://example.com) code x", Md(box));
        ApplicationCommands.Undo.Execute(null, box);
        Assert.Equal("## A **bold** *it* [<u>link</u>](https://example.com) `code` ~~x~~", Md(box));

        Select(box, 0, 2, 6);
        NoteCommands.ClearFormatting.Execute(null, box);
        Assert.Equal("## A bold *it* [<u>link</u>](https://example.com) `code` ~~x~~", Md(box));
    });

    [Fact]
    public void Ctrl_brackets_indent_and_outdent_a_list_item() => Ui.Run(ui =>
    {
        var box = Open(ui, "- one\n- two");
        Caret(box, 1, 1);

        Press(box, Key.Oem6, ModifierKeys.Control);
        Assert.Equal("- one\n  - two", Md(box));
        Press(box, Key.Oem4, ModifierKeys.Control);
        Assert.Equal("- one\n- two", Md(box));
    });

    [Theory]
    [InlineData("| t |\n| - |\n| 1 |")]
    [InlineData("<div>\nhtml\n</div>")]
    public void A_line_typed_under_a_table_or_HTML_stays_its_own_line(string note) => Ui.Run(ui =>
    {
        var box = Open(ui, note);
        AtEnd(box);

        Type(box, "after");

        Assert.Equal(note + "\n\nafter", Md(box));
        var again = Open(ui, box.Text);
        Assert.Equal(LineKind.Raw, MarkdownBox.Info(again.Document.Blocks.FirstBlock).Kind);
        Assert.Equal("after", new TextRange(again.Document.Blocks.LastBlock.ContentStart, again.Document.Blocks.LastBlock.ContentEnd).Text);
    });

    [Fact]
    public void Backspace_at_the_start_of_a_line_inside_a_list_item_joins_it_to_the_line_above() => Ui.Run(ui =>
    {
        var box = Open(ui, "- a\n- b\n  more");
        Caret(box, 2, 0);

        Press(box, Key.Back);
        Type(box, "!");

        Assert.Equal("- a\n- b!more", Md(box));
    });

    [Fact]
    public void Making_several_lines_a_list_or_heading_leaves_the_empty_lines_between_them_alone() => Ui.Run(ui =>
    {
        var box = Open(ui, "one\n\ntwo");
        box.SelectAll();

        Press(box, Key.D8, ModifierKeys.Control | ModifierKeys.Shift);
        Assert.Equal("- one\n\n- two", Md(box));
        box.SelectAll();
        Press(box, Key.D8, ModifierKeys.Control | ModifierKeys.Shift);
        Assert.Equal("one\n\ntwo", Md(box));
        box.SelectAll();
        Press(box, Key.D2, ModifierKeys.Control | ModifierKeys.Alt);
        Assert.Equal("## one\n\n## two", Md(box));
    });

    [Fact]
    public void A_list_right_under_a_hidden_answer_stays_out_of_it_when_its_bullet_comes_off() => Ui.Run(ui =>
    {
        var box = Open(ui, "> *Answer:* 42\n- milk");
        Caret(box, 0, 0);

        Press(box, Key.Back);

        Assert.Equal("> *Answer:* 42\n\nmilk", Md(box));
    });

    [Fact]
    public void Backspace_on_a_line_of_a_list_item_under_an_empty_line_takes_only_the_empty_line() => Ui.Run(ui =>
    {
        var box = Open(ui, "- item\n\n  more");
        Caret(box, 2, 0);

        Press(box, Key.Back);

        Assert.Equal("- item\n  more", Md(box));
    });

    [Fact]
    public void Ctrl_U_takes_underline_off_underlined_link_text_and_the_toolbar_shows_it() => Ui.Run(ui =>
    {
        var box = Open(ui, "[<u>link</u>](https://example.com) and [plain](https://example.com)");
        Caret(box, 0, 2);
        Assert.True(box.CurrentFormat().Underline);
        Caret(box, 0, 12);
        Assert.False(box.CurrentFormat().Underline);

        Select(box, 0, 0, 4);
        EditingCommands.ToggleUnderline.Execute(null, box);

        Assert.Equal("[link](https://example.com) and [plain](https://example.com)", Md(box));
    });
}
