using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using Waypoint.App;

namespace Waypoint.App.Tests;

public sealed partial class MarkdownBoxTests
{
    private static void Press(MarkdownBox box, Key key, ModifierKeys modifiers = ModifierKeys.None) =>
        Assert.True(box.HandleKey(key, modifiers), $"{modifiers}+{key} wasn't used by the editor");

    private static string Md(MarkdownBox box) => Lf(box.Text);

    private static void AtEnd(MarkdownBox box) => box.CaretPosition = box.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);

    /// <summary>What the clipboard holds for HTML: the fragment inside a header that says where it starts and ends.</summary>
    private static DataObject Clip(string? html = null, string? text = null)
    {
        var data = new DataObject();
        if (html is not null)
        {
            const string open = "<html><body><!--StartFragment-->";
            const string close = "<!--EndFragment--></body></html>";
            var header = "Version:0.9\r\nStartHTML:0000000000\r\nEndHTML:0000000000\r\nStartFragment:0000000000\r\nEndFragment:0000000000\r\n";
            var offset = Encoding.UTF8.GetByteCount(header);
            static string Pad(int value) => value.ToString("D10");
            var start = offset + Encoding.UTF8.GetByteCount(open);
            var end = start + Encoding.UTF8.GetByteCount(html);
            var total = end + Encoding.UTF8.GetByteCount(close);
            data.SetData(DataFormats.Html, $"Version:0.9\r\nStartHTML:{Pad(offset)}\r\nEndHTML:{Pad(total)}\r\nStartFragment:{Pad(start)}\r\nEndFragment:{Pad(end)}\r\n{open}{html}{close}");
        }

        if (text is not null)
        {
            data.SetData(DataFormats.UnicodeText, text);
        }

        return data;
    }

    [Fact]
    public void Headings_bold_and_lists_show_as_formatting_without_their_marks() => Ui.Run(ui =>
    {
        var box = Open(ui, "# Title\n\nSome **bold** and *italic* and ~~gone~~ and `code`.\n\n- one\n- two\n\n1. first\n\n> quoted\n");
        var shown = new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text;

        Assert.DoesNotContain("#", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("**", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("~~", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("`", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("- one", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("> ", shown, StringComparison.Ordinal);
        Assert.True(box.Document.Blocks.OfType<Paragraph>().First().FontSize > MarkdownBox.BaseSize);
        Assert.Contains(box.Document.Blocks, block => block is List { MarkerStyle: TextMarkerStyle.Disc });
        Assert.Contains(box.Document.Blocks, block => block is List { MarkerStyle: TextMarkerStyle.Decimal });
    });

    [Fact]
    public void A_link_shows_its_text_and_ctrl_click_opens_its_address() => Ui.Run(ui =>
    {
        var box = Open(ui, "See [the docs](https://example.com/docs) now.");
        var link = box.Document.Blocks.OfType<Paragraph>().First().Inlines.OfType<Hyperlink>().Single();
        Assert.Equal("the docs", new TextRange(link.ContentStart, link.ContentEnd).Text);

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

        Assert.Equal(["https://example.com/docs"], opened);
    });

    [Fact]
    public void Bold_and_italic_toggle_on_a_selection_and_save_as_Markdown() => Ui.Run(ui =>
    {
        var box = Open(ui, "make bold here");
        Select(box, 0, 5, 9);

        EditingCommands.ToggleBold.Execute(null, box);
        Assert.Equal("make **bold** here", Md(box));
        Assert.True(box.CurrentFormat().Bold);

        EditingCommands.ToggleBold.Execute(null, box);
        Assert.Equal("make bold here", Md(box));

        Select(box, 0, 0, 4);
        EditingCommands.ToggleItalic.Execute(null, box);
        Assert.Equal("*make* bold here", Md(box));
    });

    [Fact]
    public void Style_keys_make_headings_lists_and_normal_text() => Ui.Run(ui =>
    {
        var box = Open(ui, "alpha\nbeta");
        Caret(box, 0, 2);

        Press(box, Key.D2, ModifierKeys.Control | ModifierKeys.Alt);
        Assert.Equal("## alpha\nbeta", Md(box));
        Press(box, Key.D0, ModifierKeys.Control | ModifierKeys.Alt);
        Assert.Equal("alpha\nbeta", Md(box));
        Press(box, Key.D1, ModifierKeys.Control | ModifierKeys.Alt);
        Assert.StartsWith("# alpha", Md(box), StringComparison.Ordinal);
        Press(box, Key.D3, ModifierKeys.Control | ModifierKeys.Alt);
        Assert.StartsWith("### alpha", Md(box), StringComparison.Ordinal);
        Press(box, Key.D0, ModifierKeys.Control | ModifierKeys.Alt);

        Press(box, Key.D8, ModifierKeys.Control | ModifierKeys.Shift);
        Assert.Equal("- alpha\n\nbeta", Md(box));
        Press(box, Key.D7, ModifierKeys.Control | ModifierKeys.Shift);
        Assert.Equal("1. alpha\n\nbeta", Md(box));
        Press(box, Key.D7, ModifierKeys.Control | ModifierKeys.Shift);
        Assert.Equal("alpha\nbeta", Md(box));
    });

    [Fact]
    public void Enter_continues_a_list_and_an_empty_item_ends_it() => Ui.Run(ui =>
    {
        var box = Open(ui, "- one");
        AtEnd(box);

        Press(box, Key.Enter);
        Type(box, "two");
        Assert.Equal("- one\n- two", Md(box));

        Press(box, Key.Enter);
        Press(box, Key.Enter);
        Type(box, "after");
        Assert.Equal("- one\n- two\n\nafter", Md(box));
    });

    [Fact]
    public void Enter_in_a_checklist_makes_another_unchecked_box() => Ui.Run(ui =>
    {
        var box = Open(ui, "- [x] done");
        AtEnd(box);

        Press(box, Key.Enter);
        Type(box, "next");

        Assert.Equal("- [x] done\n- [ ] next", Md(box));
    });

    [Fact]
    public void Tab_and_shift_tab_indent_and_outdent_and_backspace_takes_the_bullet_first() => Ui.Run(ui =>
    {
        var box = Open(ui, "- one\n- two");
        Caret(box, 1, 3);

        Press(box, Key.Tab);
        Assert.Equal("- one\n  - two", Md(box));
        Press(box, Key.Tab, ModifierKeys.Shift);
        Assert.Equal("- one\n- two", Md(box));

        Caret(box, 1, 0);
        Press(box, Key.Back);
        Assert.Equal("- one\n\ntwo", Md(box));
    });

    [Fact]
    public void Typing_the_usual_marks_formats_as_you_go() => Ui.Run(ui =>
    {
        var box = Open(ui, "");

        Type(box, "# Big");
        Assert.Equal("# Big", Md(box));
        Press(box, Key.Enter);
        Type(box, "- item");
        Assert.Equal("# Big\n- item", Md(box));
        Press(box, Key.Enter);
        Press(box, Key.Enter);
        Type(box, "1. first");
        Assert.EndsWith("1. first", Md(box), StringComparison.Ordinal);
        Press(box, Key.Enter);
        Press(box, Key.Enter);
        Type(box, "[] todo");
        Assert.EndsWith("- [ ] todo", Md(box), StringComparison.Ordinal);
        Press(box, Key.Enter);
        Press(box, Key.Enter);
        Type(box, "a **bold**");
        Assert.EndsWith("a **bold**", Md(box), StringComparison.Ordinal);
        Assert.Contains(box.Document.Blocks.SelectMany(Paragraphs).Last().Inlines.OfType<Run>(), run => run.FontWeight == FontWeights.Bold && run.Text == "bold");
        Assert.DoesNotContain("**", new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text, StringComparison.Ordinal);
    });

    [Fact]
    public void One_undo_takes_back_a_typing_burst_and_redo_puts_it_back() => Ui.Run(ui =>
    {
        var box = Open(ui, "start");
        AtEnd(box);

        Type(box, " one two three");
        Assert.Equal("start one two three", Md(box));
        ApplicationCommands.Undo.Execute(null, box);
        Assert.Equal("start", Md(box));
        ApplicationCommands.Redo.Execute(null, box);
        Assert.Equal("start one two three", Md(box));
        Press(box, Key.D7, ModifierKeys.Control | ModifierKeys.Shift);
        Assert.Equal("1. start one two three", Md(box));
        ApplicationCommands.Undo.Execute(null, box);
        Assert.Equal("start one two three", Md(box));
    });

    [Fact]
    public void Ctrl_Enter_ticks_a_checklist_box_and_a_table_beside_it_is_kept_whole() => Ui.Run(ui =>
    {
        var box = Open(ui, "- [ ] a\n\n| A | B |\n| - | - |\n| 1 | 2 |\n\nend");

        Caret(box, 0, 0);
        Press(box, Key.Enter, ModifierKeys.Control);

        Assert.StartsWith("- [x] a", Md(box), StringComparison.Ordinal);
        Assert.Contains("| A | B |\n| - | - |\n| 1 | 2 |", Md(box), StringComparison.Ordinal);
        Assert.EndsWith("end", Md(box), StringComparison.Ordinal);
    });

    [Fact]
    public void Pasting_from_Word_or_the_web_keeps_bold_italic_headings_lists_and_links_and_drops_the_rest() => Ui.Run(ui =>
    {
        var box = Open(ui, "");
        var html = "<h2 style=\"color:red\">Plan</h2><p style=\"font-family:Calibri;font-size:11pt\">Do <b>this</b> and <i>that</i>, see <a href=\"https://example.com/x\">here</a>.</p><ul><li>one</li><li>two</li></ul>";

        box.Paste(Clip(html, "plain version"), plain: false);

        Assert.Contains("## Plan", Md(box), StringComparison.Ordinal);
        Assert.Contains("Do **this** and *that*, see [here](https://example.com/x).", Md(box), StringComparison.Ordinal);
        Assert.Contains("- one", Md(box), StringComparison.Ordinal);
        Assert.DoesNotContain("color", Md(box), StringComparison.Ordinal);
        Assert.DoesNotContain("Calibri", Md(box), StringComparison.Ordinal);
        Assert.DoesNotContain("<", Md(box), StringComparison.Ordinal);
    });

    [Fact]
    public void Plain_paste_puts_the_text_in_unformatted() => Ui.Run(ui =>
    {
        var box = Open(ui, "");

        box.Paste(Clip("<b>bold</b>", "# not a heading **at all**"), plain: true);

        Assert.Equal("\\# not a heading \\*\\*at all\\*\\*", Md(box));
    });

    [Fact]
    public void A_big_transcript_note_stays_quick_to_edit() => Ui.Run(ui =>
    {
        static string Transcript(int count) => string.Join("\r\n", Enumerable.Range(0, count).Select(i => $"[{i / 3600:00}:{i / 60 % 60:00}:{i % 60:00}] This is line {i} of the lecture, about **cells** and membranes."));
        var small = new MarkdownBox();
        var big = new MarkdownBox();
        ui.Host(new System.Windows.Controls.Grid { Children = { small, big } });
        small.Text = Transcript(100);
        big.Text = Transcript(10_000);
        ui.Pump();
        AtEnd(small);
        AtEnd(big);
        ui.Pump();

        // The first few keys pay for laying the notes out.
        Type(small, new string('x', 10));
        Type(big, new string('x', 10));

        // Keys go to the two notes in turn, so whatever else the PC is doing weighs on both alike. What's compared is
        // this thread's own processor time, which doesn't count the time it waits while other programs run.
        const int typed = 41;
        var writtenBefore = big.BlocksWritten;
        var smallCost = new List<ulong>();
        var bigCost = new List<ulong>();
        var bigTime = new List<double>();
        for (var i = 0; i < typed; i++)
        {
            var start = ThreadCycles();
            Type(small, "x");
            smallCost.Add(ThreadCycles() - start);

            var watch = Stopwatch.StartNew();
            start = ThreadCycles();
            Type(big, "x");
            bigCost.Add(ThreadCycles() - start);
            bigTime.Add(watch.Elapsed.TotalMilliseconds);
        }

        static T Median<T>(List<T> values) => values.Order().ElementAt(values.Count / 2);
        var written = (big.BlocksWritten - writtenBefore) / (double)typed;
        var ratio = Median(bigCost) / (double)Math.Max(1UL, Median(smallCost));
        Assert.EndsWith(new string('x', typed + 10), Md(big), StringComparison.Ordinal);

        // A key writes again only the line it's on. Reading the whole note again for each key would write all 10,000.
        Assert.True(written <= 2, $"Typing wrote {written:0.0} lines a key on a 10,000 line note");

        // Joining the lines makes a key cost a little more on a long note, about 7 to 18 times the 100 line note's
        // here. Work that grows faster with the note, like going over every line for each key, costs 50 times or more.
        Assert.True(ratio < 30, $"A key cost {ratio:0.0} times as much on a 10,000 line note as on a 100 line one");

        // Only a loose check that nothing is badly slow, wide enough for a PC busy with other work.
        Assert.True(Median(bigTime) < 1_000, $"Typing took {Median(bigTime):0.0} ms a key on a 10,000 line note");
    });

    /// <summary>The processor cycles this thread has used, which don't count time spent waiting while other programs run.</summary>
    private static ulong ThreadCycles()
    {
        Assert.True(QueryThreadCycleTime(GetCurrentThread(), out var cycles));
        return cycles;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool QueryThreadCycleTime(nint thread, out ulong cycles);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [Fact]
    public void Ctrl_K_makes_a_link_of_the_selection_and_only_from_a_safe_address() => Ui.Run(ui =>
    {
        var box = Open(ui, "read the docs now");
        box.AskForLink = _ => "example.com/docs";
        Select(box, 0, 9, 13);

        Press(box, Key.K, ModifierKeys.Control);

        Assert.Equal("read the [docs](https://example.com/docs) now", Md(box));
        Assert.Contains("docs", new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text, StringComparison.Ordinal);
        Assert.DoesNotContain("](", new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text, StringComparison.Ordinal);

        Caret(box, 0, 11);
        Assert.True(box.CurrentFormat().Link);
        box.AskForLink = _ => "";
        Press(box, Key.K, ModifierKeys.Control);
        Assert.Equal("read the docs now", Md(box));

        box.AskForLink = _ => "javascript:alert(1)";
        Select(box, 0, 9, 13);
        Press(box, Key.K, ModifierKeys.Control);
        Assert.DoesNotContain("javascript", Md(box), StringComparison.Ordinal);
    });

    [Fact]
    public void Code_strikethrough_and_underline_save_as_Markdown_and_round_trip() => Ui.Run(ui =>
    {
        var box = Open(ui, "one two three four");
        Select(box, 0, 0, 3);
        NoteCommands.Code.Execute(null, box);
        Select(box, 0, 4, 7);
        NoteCommands.Strikethrough.Execute(null, box);
        Select(box, 0, 8, 13);
        EditingCommands.ToggleUnderline.Execute(null, box);

        Assert.Equal("`one` ~~two~~ <u>three</u> four", Md(box));
        var again = Open(ui, box.Text);
        Assert.Equal(box.Text, again.Text);
        Assert.True(MarkdownBox.AllRuns(again.Document.Blocks.OfType<Paragraph>().First().Inlines).Any(run => MarkdownBox.IsCode(run.FontFamily) && run.Text == "one"));
    });

    [Fact]
    public void Nested_lists_checklists_and_quotes_are_made_with_keys_and_typing() => Ui.Run(ui =>
    {
        var box = Open(ui, "");

        Type(box, "- a");
        Press(box, Key.Enter);
        Press(box, Key.Tab);
        Type(box, "b");
        Press(box, Key.Enter);
        Press(box, Key.Tab);
        Type(box, "c");
        Press(box, Key.Enter);
        Press(box, Key.Tab, ModifierKeys.Shift);
        Press(box, Key.Tab, ModifierKeys.Shift);
        Type(box, "d");

        Assert.Equal("- a\n  - b\n    - c\n- d", Md(box));
        Press(box, Key.D9, ModifierKeys.Control | ModifierKeys.Shift);
        Assert.EndsWith("- [ ] d", Md(box), StringComparison.Ordinal);
        Press(box, Key.Enter);
        Press(box, Key.Enter);
        Type(box, "> quoted");
        Assert.EndsWith("> quoted", Md(box), StringComparison.Ordinal);
        Assert.Contains(box.Document.Blocks.OfType<Paragraph>(), paragraph => paragraph.BorderThickness.Left > 0);
    });

    [Fact]
    public void Typing_star_space_and_the_other_marks_formats_and_undo_puts_the_marks_back() => Ui.Run(ui =>
    {
        var box = Open(ui, "");

        Type(box, "* item");
        Assert.Equal("- item", Md(box));
        Press(box, Key.Enter);
        Press(box, Key.Enter);
        Type(box, "an *emphasised*");
        Assert.Equal("- item\n\nan *emphasised*", Md(box));
        Press(box, Key.Enter);
        Type(box, "~~gone~~");
        Press(box, Key.Enter);
        Type(box, "`code`");
        Assert.EndsWith("an *emphasised*\n~~gone~~\n`code`", Md(box), StringComparison.Ordinal);
        Assert.DoesNotContain("*", new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text, StringComparison.Ordinal);
    });

    [Fact]
    public void Pasting_HTML_with_an_image_leaves_the_image_out_and_pasting_inside_a_line_joins_it() => Ui.Run(ui =>
    {
        var box = Open(ui, "before after");
        Caret(box, 0, 7);

        box.Paste(Clip("<p>X <b>bold</b><img src=\"a.png\"></p>", "X bold"), plain: false);

        Assert.Equal("before X **bold**after", Md(box));
    });

    [Fact]
    public void The_toolbar_shows_what_is_on_at_the_caret_and_its_buttons_act_on_the_note() => Ui.Run(ui =>
    {
        var box = ui.Host(new MarkdownBox());
        var toolbar = ui.Host(new NoteToolbar());
        toolbar.Attach(box);
        box.Text = "## Plan\n**bold** words\n- item";
        ui.Pump();
        ui.Focus(box);
        var checkedOf = (string name) => UiTest.Named<System.Windows.Controls.Primitives.ToggleButton>(toolbar, name).IsChecked == true;

        Caret(box, 0, 2);
        Assert.Equal("Heading 2", toolbar.StyleName);
        Caret(box, 1, 1);
        Assert.Equal("Normal", toolbar.StyleName);
        Assert.True(checkedOf("BoldButton"));
        Assert.False(checkedOf("ItalicButton"));
        Caret(box, 2, 2);
        Assert.True(checkedOf("BulletsButton"));
        Assert.False(checkedOf("NumbersButton"));

        Caret(box, 1, 8);
        Select(box, 1, 0, 4);
        toolbar.Choose(1);
        Assert.StartsWith("## Plan\n# **bold** words", Md(box), StringComparison.Ordinal);
        var italic = UiTest.Named<System.Windows.Controls.Primitives.ToggleButton>(toolbar, "ItalicButton");
        italic.CommandTarget = box;
        italic.Command.Execute(null);
        Assert.Contains("***bold***", Md(box), StringComparison.Ordinal);
    });

    [Fact]
    public void Every_toolbar_button_has_a_name_and_a_tooltip_and_uses_only_the_apps_brushes() => Ui.Run(ui =>
    {
        var toolbar = ui.Host(new NoteToolbar());
        ui.Pump();
        var buttons = new List<System.Windows.Controls.Primitives.ButtonBase>();
        void Walk(DependencyObject parent)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            {
                if (child is System.Windows.Controls.Primitives.ButtonBase button)
                {
                    buttons.Add(button);
                }

                Walk(child);
            }
        }

        Walk(toolbar);

        Assert.Equal(14, buttons.Count);
        Assert.All(buttons, button =>
        {
            Assert.False(string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(button)));
            Assert.NotNull(button.ToolTip);
            Assert.False(button.Focusable);
        });
        var xaml = System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Waypoint.App", "NoteToolbar.xaml"));
        Assert.DoesNotContain("#", xaml.Replace("&#x", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("CornerRadius", xaml, StringComparison.Ordinal);

        // Buttons that only do something aren't read out as on or off.
        Assert.All(new[] { "UndoButton", "RedoButton", "ClearButton" }, name => Assert.IsNotAssignableFrom<System.Windows.Controls.Primitives.ToggleButton>(UiTest.Named<System.Windows.Controls.Primitives.ButtonBase>(toolbar, name)));
        Assert.Contains("▾", UiTest.Descendants<System.Windows.Controls.TextBlock>(UiTest.Named<System.Windows.Controls.Primitives.ToggleButton>(toolbar, "StyleButton")).Select(text => text.Text));
    });

    [Fact]
    public void Alt_F10_moves_into_the_toolbar_where_arrows_move_and_esc_or_a_press_goes_back_to_the_note() => Ui.Run(ui =>
    {
        var box = new MarkdownBox { Height = 200 };
        var toolbar = new NoteToolbar();
        var panel = new System.Windows.Controls.StackPanel();
        panel.Children.Add(toolbar);
        panel.Children.Add(box);
        ui.Host(panel);
        toolbar.Attach(box);
        box.Text = "some words";
        ui.Pump();
        ui.Focus(box);
        Select(box, 0, 5, 10);
        System.Windows.Controls.Primitives.ButtonBase Button(string name) => UiTest.Named<System.Windows.Controls.Primitives.ButtonBase>(toolbar, name);

        Assert.False(toolbar.HandleNoteKey(Key.F10, ModifierKeys.None));
        Assert.True(toolbar.HandleNoteKey(Key.F10, ModifierKeys.Alt));
        Assert.True(Button("StyleButton").IsKeyboardFocused);
        Assert.True(toolbar.HandleToolbarKey(Key.Right));
        Assert.True(Button("BoldButton").IsKeyboardFocused);
        Assert.True(toolbar.HandleToolbarKey(Key.Left));
        Assert.True(toolbar.HandleToolbarKey(Key.Left));
        Assert.False(Button("StyleButton").IsKeyboardFocused);
        Assert.True(toolbar.HandleToolbarKey(Key.Home));
        Assert.True(Button("StyleButton").IsKeyboardFocused);

        Assert.True(toolbar.HandleToolbarKey(Key.Escape));
        ui.Pump();
        Assert.True(box.IsKeyboardFocused);
        Assert.False(Button("BoldButton").Focusable);

        toolbar.HandleNoteKey(Key.F10, ModifierKeys.Alt);
        toolbar.HandleToolbarKey(Key.Right);
        toolbar.HandleToolbarKey(Key.Right);
        var italic = Button("ItalicButton");
        Assert.True(italic.IsKeyboardFocused);
        ui.Press(italic, Key.Enter);
        ui.Pump();

        Assert.Equal("some *words*", Md(box));
        Assert.True(box.IsKeyboardFocused);
    });
}
