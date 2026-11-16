using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Waypoint.App;

namespace Waypoint.App.Tests;

/// <summary>The formatted note editor. Keys, typing, and paste are driven straight into it, never as real input.</summary>
public sealed partial class MarkdownBoxTests
{
    private static MarkdownBox Open(UiTest ui, string text)
    {
        var box = ui.Host(new MarkdownBox());
        box.Text = text;
        ui.Pump();
        ui.Focus(box);
        return box;
    }

    /// <summary>Types each character the way the keyboard would: the editor's own handling first, else plain insertion.</summary>
    private static void Type(MarkdownBox box, string text)
    {
        foreach (var ch in text)
        {
            if (ch == '\n')
            {
                Assert.True(box.HandleKey(Key.Enter, ModifierKeys.None));
                continue;
            }

            if (!box.HandleTextInput(ch.ToString()))
            {
                box.Selection.Text = ch.ToString();
                box.Selection.Select(box.Selection.End, box.Selection.End);
            }
        }
    }

    private static void Caret(MarkdownBox box, int line, int column)
    {
        var paragraph = box.Document.Blocks.SelectMany(Paragraphs).ElementAt(line);
        box.CaretPosition = MarkdownBox.PointerAt(paragraph, column);
    }

    private static IEnumerable<Paragraph> Paragraphs(Block block) => block switch
    {
        Paragraph paragraph => [paragraph],
        List list => list.ListItems.SelectMany(item => item.Blocks.SelectMany(Paragraphs)),
        _ => [],
    };

    private static void Select(MarkdownBox box, int line, int from, int to)
    {
        var paragraph = box.Document.Blocks.SelectMany(Paragraphs).ElementAt(line);
        box.Selection.Select(MarkdownBox.PointerAt(paragraph, from), MarkdownBox.PointerAt(paragraph, to));
    }

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    public static IEnumerable<object[]> Corpus() =>
    [
        ["Lecture 4: Enzymes\n\nEnzymes lower the **activation energy** of a reaction.\n\n## Key terms\n\n- Substrate\n- Active site\n  - Induced fit\n  - Lock and key\n- Cofactor\n\n1. Bind\n2. React\n3. Release\n"],
        ["# Groceries\n\n- [ ] milk\n- [x] eggs\n- [ ] bread\n  - [ ] rye\n"],
        ["> The only way out is through.\n\nA plain line after a quote.\n"],
        ["Some *italic*, some **bold**, some ~~gone~~, some `code`, and a [link](https://example.com/a?b=1&c=2).\n"],
        ["Text with a trailing double space  \nnext line in the same paragraph\n"],
        ["| Term | Meaning |\n| --- | --- |\n| ATP | energy |\n| ADP | less energy |\n\nAfter the table.\n"],
        ["```csharp\nvar x = 1;\n\nConsole.WriteLine(x);\n```\n\nAfter the code.\n"],
        ["Before.\n\n---\n\nAfter the rule.\n"],
        ["<details>\n<summary>More</summary>\n\nHidden text.\n\n</details>\n\nVisible.\n"],
        ["A claim.[^1]\n\n[^1]: The footnote text.\n"],
        ["# Practice\n\n1. What is osmosis?\n\n> **Answer:** Water moving across a membrane.\n\n2. Name two organelles.\n"],
        ["## Transcript\n\n[00:00:04] Welcome everyone to the first lecture.\n[00:00:19] Today we talk about cells.\n[00:01:02] First, the membrane.\n"],
        ["**[0:12]** She said the exam covers chapters 1 to 4.\n\n**[0:48]** Office hours move to Thursday.\n"],
        ["Notes\n\n## Sources\n\n- lecture-4.m4a\n- slides.pdf\n\nWritten from the files above.\n"],
        ["Line one\nLine two\nLine three\n"],
        ["Line one\r\n\r\nLine two\r\n- item\r\n- item two\r\n"],
        ["* star bullet\n* another\n\n+ plus bullet\n+ more\n"],
        ["1) paren list\n2) second\n\n5. starts at five\n6. six\n"],
        ["Escapes: 1\\. not a list, \\# not a heading, 2 \\* 3 \\* 4, snake_case_name, a_b_c\n"],
        ["Auto link <https://example.com> and bare https://example.com/x_y_z ok\n"],
        ["Heading *with* **marks**\n=========\n\nSetext two\n---------\n"],
        ["- item\n\n  second paragraph of the item\n- next\n"],
        ["    indented code block\n    more\n\nnormal\n"],
        ["![diagram](a.png) and a line\n"],
        ["Ends without a newline"],
        ["\n\n\nLeading blanks and many\n\n\n\nin between\n"],
        ["Tabs\there and trailing spaces   \n"],
        ["<u>underlined</u> and normal and **<u>both</u>**\n"],
        ["- [ ] task with **bold** and a [link](https://example.com)\n- [x] done `code`\n"],
        ["1. one\n   - nested bullet\n     1. deep number\n2. two\n"],
        [""],
    ];

    [Theory]
    [MemberData(nameof(Corpus))]
    public void A_note_nobody_changed_comes_back_exactly_as_it_was(string note) => Ui.Run(ui =>
    {
        var box = Open(ui, note);

        Assert.Equal(note, box.Text);
    });

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Writing_every_line_again_means_the_same_Markdown_and_settles(string note) => Ui.Run(ui =>
    {
        var again = MarkdownLines.Rewrite(note);

        Assert.Equal(Html(note), Html(again));
        Assert.Equal(again, MarkdownLines.Rewrite(again));
    });

    /// <summary>The app draws every line break as a break, so a hard break and a soft one mean the same here.</summary>
    private static string Html(string markdown) =>
        Markdig.Markdown.ToHtml(markdown.Replace("\r\n", "\n", StringComparison.Ordinal), MarkdownLines.Pipeline).Replace("<br />", "", StringComparison.Ordinal).Trim();
}
