using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Waypoint.App;

/// <summary>
/// The note editor, formatted like a word processor: headings, bold, lists, checklists, quotes, links, and code show
/// as formatting, with no Markdown marks. Each line of the note is one paragraph, and the text in and out is
/// Markdown. A line nobody changed is written back exactly as it was loaded, and anything the editor can't format,
/// such as a table, is kept as a protected block of its Markdown. Undo works on the Markdown, a typing burst at a time.
/// </summary>
public sealed partial class MarkdownBox : RichTextBox
{
    internal const double BaseSize = 14;
    internal static readonly FontFamily TextFont = new("Segoe UI Variable Text, Segoe UI");
    internal static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas, Courier New");
    private static readonly TimeSpan UndoPause = TimeSpan.FromMilliseconds(800);
    private const int MaxUndo = 200;
    private const double IndentStep = 24;

    private readonly List<Step> _undo = [];
    private readonly List<Step> _redo = [];
    private string _text = "";
    private string _loaded = "";
    private string _loadedJoined = "";
    private string _original = "";
    private string _originalJoined = "";
    private IReadOnlySet<string> _keptLinks = new HashSet<string>();
    private string _newline = "\r\n";
    private int _internal;
    private DateTime _lastEdit = DateTime.MinValue;
    private bool _newStep = true;
    private string _burstBase = "";
    private int _burstBlocks;
    private TextPointer? _caretBefore;
    private Caret? _changeCaret;

    /// <summary>
    /// One undo step: the Markdown from <see cref="Start"/> that was <see cref="Old"/> and became <see cref="New"/>.
    /// <see cref="Space"/> is set when a typed space made the line a list or heading, so undoing it shows the space again.
    /// <see cref="ByText"/> is set for a step carried over from Show Markdown, whose caret goes to the end of what changed.
    /// </summary>
    private sealed record Step(int Start, string Old, string New, Caret Before, Caret After, bool Space = false, bool ByText = false);

    /// <summary>A caret as the block it's in and how many characters into that block's text.</summary>
    private readonly record struct Caret(int Block, int Offset);

    public MarkdownBox()
    {
        IsUndoEnabled = false;
        AcceptsTab = true;
        AcceptsReturn = true;
        IsDocumentEnabled = true;
        FontFamily = TextFont;
        FontSize = BaseSize;
        Document = NewDocument();
        Document.Blocks.Add(Build(new ParsedBlock(TextInfo(""), [])));
        DataObject.AddPastingHandler(this, OnPasting);
        DataObject.AddCopyingHandler(this, OnCopying);
        TextChanged += OnChanged;
        SelectionChanged += OnSelectionChanged;
        PreviewKeyDown += OnKey;
        PreviewTextInput += OnTextInput;
        PreviewMouseLeftButtonDown += OnMouseDown;
        WatchCheckBoxes();
        AddCommands();
    }

    /// <summary>The text changed by typing, pasting, formatting, or undo. Loading a note never raises it.</summary>
    public event EventHandler? TextEdited;

    /// <summary>The caret moved or the formatting under it changed, so a toolbar can show what's on.</summary>
    public event EventHandler? FormatChanged;

    /// <summary>
    /// The note as Markdown. Setting it replaces everything and clears undo. While the note reads the same as when it
    /// was set, it's exactly what was set, down to mixed line endings, so a note nobody changed is never rewritten.
    /// </summary>
    public string Text
    {
        get => _text == _loadedJoined ? _loaded : _text == _originalJoined ? _original : _text;
        set
        {
            var text = value ?? "";
            Open(text, kept: null);
            _original = text;
            _originalJoined = _text;
        }
    }

    /// <summary>
    /// Loads <paramref name="text"/> and clears undo. Links other than web and mail addresses stay only if they're in
    /// <paramref name="kept"/>; with none given, every link in the text is kept, since it's the note as it was saved.
    /// </summary>
    private void Open(string text, IReadOnlySet<string>? kept)
    {
        _newline = text.Contains("\r\n", StringComparison.Ordinal) || !text.Contains('\n') ? "\r\n" : "\n";
        _undo.Clear();
        _redo.Clear();
        _newStep = true;
        var dropped = LoadAll(text, kept);
        _loaded = dropped ? _text : text;
        _loadedJoined = _text;
        CaretPosition = Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
        FormatChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The undo and redo so far, as whole texts, for Show Markdown to carry on with.</summary>
    internal EditHistory History
    {
        get
        {
            var undo = new List<string>();
            var state = _text;
            for (var i = _undo.Count - 1; i >= 0 && Holds(state, _undo[i].Start, _undo[i].New); i--)
            {
                state = state[.._undo[i].Start] + _undo[i].Old + state[(_undo[i].Start + _undo[i].New.Length)..];
                undo.Add(state);
            }

            var redo = new List<string>();
            state = _text;
            for (var i = _redo.Count - 1; i >= 0 && Holds(state, _redo[i].Start, _redo[i].Old); i--)
            {
                state = state[.._redo[i].Start] + _redo[i].New + state[(_redo[i].Start + _redo[i].Old.Length)..];
                redo.Add(state);
            }

            undo.Reverse();
            redo.Reverse();
            return new EditHistory(undo, redo, _original, _originalJoined);
        }
    }

    private static bool Holds(string text, int start, string part) =>
        start + part.Length <= text.Length && string.CompareOrdinal(text, start, part, 0, part.Length) == 0;

    /// <summary>
    /// Back from Show Markdown: takes over the note's text and its undo and redo, so Ctrl+Z goes on undoing changes
    /// made there. A link typed there that isn't a web or mail address becomes its text; links the note was opened
    /// with stay as they are.
    /// </summary>
    internal void Switch(string text, EditHistory history)
    {
        var kept = OtherLinks(MarkdownLines.Parse(history.Original));
        Open(text, kept);
        _original = history.Original;
        _originalJoined = history.OriginalJoined;
        var next = _text;
        for (var i = history.Undo.Count - 1; i >= 0; i--)
        {
            _undo.Insert(0, Diff(history.Undo[i], next, default, default) with { ByText = true });
            next = history.Undo[i];
        }

        var previous = _text;
        for (var i = history.Redo.Count - 1; i >= 0; i--)
        {
            _redo.Insert(0, Diff(previous, history.Redo[i], default, default) with { ByText = true });
            previous = history.Redo[i];
        }
    }

    public new bool CanUndo => _undo.Count > 0;

    public new bool CanRedo => _redo.Count > 0;

    /// <summary>
    /// Where the caret is, counted in characters of <see cref="Text"/>. Formatting marks make it close rather than
    /// exact inside formatted lines; at the end of the note it's the end of the text.
    /// </summary>
    public int CaretIndex
    {
        get
        {
            var caret = CaretPosition;
            if (caret.CompareTo(Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward)) >= 0)
            {
                return _text.Length;
            }

            var top = TopOf(caret);
            var at = 0;
            foreach (var (block, start, markdown) in Lines())
            {
                if (block == top)
                {
                    var para = TextPara(block);
                    var visible = para is null ? 0 : VisibleLength(para);
                    var offset = para is null ? 0 : CharOffset(para, caret);
                    return Math.Min(_text.Length, start + Math.Max(0, markdown.Length - visible) + offset);
                }

                at = start;
            }

            return Math.Min(at, _text.Length);
        }
        set
        {
            var target = Math.Clamp(value, 0, _text.Length);
            if (target >= _text.Length)
            {
                CaretPosition = Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
                return;
            }

            foreach (var (block, start, markdown) in Lines())
            {
                if (target <= start + markdown.Length)
                {
                    if (TextPara(block) is { } para)
                    {
                        var visible = VisibleLength(para);
                        var offset = Math.Clamp(target - start - Math.Max(0, markdown.Length - visible), 0, visible);
                        CaretPosition = PointerAt(para, offset);
                    }
                    else
                    {
                        CaretPosition = block.ElementEnd.GetInsertionPosition(LogicalDirection.Forward);
                    }

                    return;
                }
            }
        }
    }

    public void ScrollToCaret()
    {
        if (CaretPosition.Paragraph is { } paragraph)
        {
            paragraph.BringIntoView();
        }
    }

    // Loading

    private FlowDocument NewDocument()
    {
        var document = new FlowDocument { PagePadding = new Thickness(0), FontFamily = TextFont, FontSize = BaseSize, Background = Brushes.Transparent };
        document.SetResourceReference(FlowDocument.ForegroundProperty, "PrimaryText");
        return document;
    }

    private static LineInfo TextInfo(string source)
    {
        var info = LineInfo.Of(LineKind.Text);
        info.Source = source;
        info.Prefix = source;
        info.LoadedShape = info.Shape;
        info.Fresh = source;
        info.Markdown = source;
        info.Dirty = false;
        return info;
    }

    /// <summary>Builds the document from <paramref name="text"/>. True when a link was left out as not kept.</summary>
    private bool LoadAll(string text, IReadOnlySet<string>? kept)
    {
        _internal++;
        try
        {
            var document = NewDocument();
            var blocks = MarkdownLines.Parse(text);
            var dropped = false;
            if (kept is null)
            {
                _keptLinks = OtherLinks(blocks);
            }
            else
            {
                _keptLinks = kept;
                blocks = WebLinksOnly(blocks, kept, out dropped);
            }

            foreach (var parsed in blocks)
            {
                document.Blocks.Add(Build(parsed));
            }

            if (document.Blocks.Count == 0)
            {
                document.Blocks.Add(Build(new ParsedBlock(TextInfo(""), [])));
            }

            Document = document;
            Renumber(Document.Blocks.FirstBlock, Document.Blocks.LastBlock, write: false);
            _text = Join();
            _burstBase = _text;
            return dropped;
        }
        finally
        {
            _internal--;
        }
    }

    /// <summary>
    /// Puts <paramref name="text"/> in, rebuilding only the blocks between the first and last that differ, so undo in
    /// a long note doesn't rebuild all of it.
    /// </summary>
    private void Reload(string text)
    {
        _internal++;
        try
        {
            var parsed = WebLinksOnly(MarkdownLines.Parse(text), _keptLinks, out _);
            var existing = Document.Blocks.ToList();
            var same = (Block block, ParsedBlock incoming) => Info(block).Kind == incoming.Info.Kind && MarkdownOf(block) == incoming.Info.Source;
            var head = 0;
            while (head < existing.Count && head < parsed.Count && same(existing[head], parsed[head]))
            {
                head++;
            }

            var tail = 0;
            while (tail < existing.Count - head && tail < parsed.Count - head && same(existing[^(tail + 1)], parsed[^(tail + 1)]))
            {
                tail++;
            }

            var after = head > 0 ? existing[head - 1] : null;
            for (var i = head; i < existing.Count - tail; i++)
            {
                Document.Blocks.Remove(existing[i]);
            }

            foreach (var incoming in parsed.Skip(head).Take(parsed.Count - head - tail))
            {
                var block = Build(incoming);
                if (after is null)
                {
                    if (Document.Blocks.FirstBlock is { } first)
                    {
                        Document.Blocks.InsertBefore(first, block);
                    }
                    else
                    {
                        Document.Blocks.Add(block);
                    }
                }
                else
                {
                    Document.Blocks.InsertAfter(after, block);
                }

                after = block;
            }

            if (Document.Blocks.Count == 0)
            {
                Document.Blocks.Add(Build(new ParsedBlock(TextInfo(""), [])));
            }

            Renumber(Document.Blocks.FirstBlock, Document.Blocks.LastBlock, write: false);
            _text = Join();
        }
        finally
        {
            _internal--;
        }
    }

    internal static LineInfo Info(Block block)
    {
        if (block.Tag is LineInfo info)
        {
            return info;
        }

        if (TextPara(block)?.Tag is LineInfo inner)
        {
            block.Tag = inner;
            return inner;
        }

        var made = LineInfo.Of(LineKind.Text);
        block.Tag = made;
        return made;
    }

    /// <summary>The paragraph holding a block's text: the block itself, or the one inside a list item. Null for protected blocks.</summary>
    internal static Paragraph? TextPara(Block block) => block switch
    {
        Paragraph paragraph => paragraph,
        List list => list.ListItems.FirstListItem?.Blocks.FirstBlock as Paragraph,
        _ => null,
    };

    internal static Block TopOf(TextPointer pointer)
    {
        DependencyObject? element = pointer.Parent;
        Block? found = null;
        while (element is TextElement text)
        {
            if (text is Block block)
            {
                found = block;
            }

            element = LogicalTreeHelper.GetParent(text);
        }

        return found ?? pointer.Paragraph ?? (Block?)null ?? throw new InvalidOperationException("No block at the caret.");
    }

    private Block? TopOrNull(TextPointer pointer)
    {
        DependencyObject? element = pointer.Parent;
        Block? found = null;
        while (element is TextElement text)
        {
            if (text is Block block)
            {
                found = block;
            }

            element = LogicalTreeHelper.GetParent(text);
        }

        if (found is null)
        {
            // Between blocks or at the very end: the block just before.
            var near = pointer.GetInsertionPosition(LogicalDirection.Backward);
            return near.Paragraph is { } paragraph ? TopOf(near) : Document.Blocks.LastBlock;
        }

        return found;
    }

    // Building blocks

    internal Block Build(ParsedBlock parsed)
    {
        var info = parsed.Info;
        switch (info.Kind)
        {
            case LineKind.Raw:
            case LineKind.Rule:
            case LineKind.Answer:
                return Protected(info);
            case LineKind.Code:
                var code = new Paragraph { Tag = info };
                var first = true;
                foreach (var line in parsed.Text.Split('\n'))
                {
                    if (!first)
                    {
                        code.Inlines.Add(new LineBreak());
                    }

                    code.Inlines.Add(new Run(line));
                    first = false;
                }

                Restyle(code);
                return code;
        }

        var paragraph = new Paragraph { Tag = info };
        AddInlines(paragraph.Inlines, parsed.Segments);
        if (info.Kind == LineKind.Item)
        {
            var list = new List(new ListItem(paragraph)) { Tag = info };
            Restyle(list);
            return list;
        }

        Restyle(paragraph);
        return paragraph;
    }

    internal static void AddInlines(InlineCollection inlines, IReadOnlyList<Segment> segments)
    {
        Hyperlink? link = null;
        foreach (var segment in segments)
        {
            var run = new Run(segment.Text);
            ApplyStyle(run, segment.Style);
            if (segment.Style.Link is { } url)
            {
                if (link is null || (string?)link.Tag != url)
                {
                    link = NewLink(url, segment.Style.Title);
                    inlines.Add(link);
                }

                link.Inlines.Add(run);
            }
            else
            {
                link = null;
                inlines.Add(run);
            }
        }
    }

    internal static Hyperlink NewLink(string url, string? title = null)
    {
        var link = new Hyperlink { Tag = url, ToolTip = $"{title ?? url}\nCtrl+click to open" };
        link.SetResourceReference(TextElement.ForegroundProperty, "Accent");
        if (title is not null)
        {
            link.SetValue(TitleProperty, title);
        }

        return link;
    }

    /// <summary>A link's title, from Markdown like [text](url "title"), kept so it's written back.</summary>
    private static readonly DependencyProperty TitleProperty = DependencyProperty.RegisterAttached("LinkTitle", typeof(string), typeof(MarkdownBox));

    internal static void ApplyStyle(Run run, InlineStyle style)
    {
        if (style.Bold)
        {
            run.FontWeight = FontWeights.Bold;
        }

        if (style.Italic)
        {
            run.FontStyle = FontStyles.Italic;
        }

        if (style.Underline || style.Strike)
        {
            run.TextDecorations = Decorations(style.Underline, style.Strike);
        }

        if (style.Code)
        {
            MakeCode(run);
        }
    }

    internal static TextDecorationCollection? Decorations(bool underline, bool strike)
    {
        if (!underline && !strike)
        {
            return null;
        }

        var decorations = new TextDecorationCollection();
        if (underline)
        {
            decorations.Add(TextDecorations.Underline);
        }

        if (strike)
        {
            decorations.Add(TextDecorations.Strikethrough);
        }

        return decorations;
    }

    internal static void MakeCode(TextElement element)
    {
        element.FontFamily = CodeFont;
        element.FontSize = 13;
        element.SetResourceReference(TextElement.BackgroundProperty, "Surface");
    }

    /// <summary>A rule, a hidden answer, or Markdown the editor can't format. Typing can't change it; Show Markdown can.</summary>
    private Block Protected(LineInfo info)
    {
        UIElement content;
        switch (info.Kind)
        {
            case LineKind.Rule:
                var rule = new Border { Height = 1, Margin = new Thickness(0, 9, 0, 9) };
                rule.SetResourceReference(Border.BackgroundProperty, "Rule");
                AutomationPropertiesHelper(rule, "Horizontal line");
                content = rule;
                break;
            case LineKind.Answer:
                var show = new Button
                {
                    Content = "Show answer",
                    Cursor = Cursors.Hand,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(0, 2, 0, 2),
                    Focusable = false,
                    ToolTip = "Show the answer under this question",
                };
                show.SetResourceReference(StyleProperty, "LinkButton");
                show.SetResourceReference(Control.ForegroundProperty, "Accent");
                content = show;
                break;
            default:
                var text = new TextBlock
                {
                    Text = info.Source ?? "",
                    FontFamily = CodeFont,
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    Padding = new Thickness(10, 6, 10, 6),
                    ToolTip = "Kept as Markdown, since it can't be shown formatted here. Edit it with Show Markdown (Ctrl+E).",
                };
                text.SetResourceReference(TextBlock.BackgroundProperty, "Surface");
                text.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
                AutomationPropertiesHelper(text, "Markdown kept as it is");
                content = text;
                break;
        }

        var block = new BlockUIContainer(content) { Tag = info, Margin = new Thickness(0, 2, 0, 2) };
        if (content is Button button)
        {
            button.Click += (_, _) => Reveal(block);
        }

        return block;
    }

    private static void AutomationPropertiesHelper(UIElement element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);

    /// <summary>Show answer swaps the hidden answer for its quote lines, which can then be edited like any quote.</summary>
    private void Reveal(BlockUIContainer block)
    {
        var source = Info(block).Source ?? "";
        _internal++;
        try
        {
            var after = block;
            Block? last = null;
            foreach (var parsed in MarkdownLines.Parse(source, hideAnswers: false))
            {
                var built = Build(parsed);
                Document.Blocks.InsertAfter(last ?? after, built);
                last = built;
            }

            Document.Blocks.Remove(block);
            _text = Join();
        }
        finally
        {
            _internal--;
        }
    }

    /// <summary>
    /// Takes a line's theme brushes off before it's split. WPF copies them to the new line in a state that can't be
    /// cleared later; the line is styled again right after, which sets them fresh.
    /// </summary>
    private static void Unthemed(Paragraph paragraph)
    {
        paragraph.ClearValue(TextElement.ForegroundProperty);
        paragraph.ClearValue(TextElement.BackgroundProperty);
        paragraph.ClearValue(Block.BorderBrushProperty);
    }

    /// <summary>Sizes and spacing for a block's kind. Called whenever a block is made or changes kind.</summary>
    internal static void Restyle(Block top)
    {
        var info = Info(top);
        if (TextPara(top) is not { } paragraph)
        {
            return;
        }

        paragraph.Tag = info;
        top.Tag = info;
        paragraph.Margin = new Thickness(0);
        paragraph.Padding = new Thickness(0);
        paragraph.FontSize = BaseSize;
        paragraph.FontWeight = FontWeights.Normal;
        paragraph.FontFamily = TextFont;
        paragraph.LineHeight = 22;
        paragraph.BorderThickness = new Thickness(0);
        paragraph.ClearValue(Block.BorderBrushProperty);
        paragraph.ClearValue(TextElement.ForegroundProperty);
        paragraph.ClearValue(TextElement.BackgroundProperty);

        switch (info.Kind)
        {
            case LineKind.Heading:
                paragraph.FontSize = info.Level switch
                {
                    1 => 24,
                    2 => 20,
                    3 => 17,
                    _ => 15,
                };
                paragraph.LineHeight = paragraph.FontSize * 1.4;
                paragraph.FontWeight = FontWeights.SemiBold;
                break;
            case LineKind.Item when top is List list:
                list.MarkerStyle = info.Type switch
                {
                    ItemType.Number => TextMarkerStyle.Decimal,
                    // Its box is drawn as ☐ or ☑ (MarkdownBox.CheckBoxes.cs), since a list's squares read as bullets.
                    ItemType.Task => TextMarkerStyle.None,
                    _ => (info.Level % 3) switch
                    {
                        0 => TextMarkerStyle.Disc,
                        1 => TextMarkerStyle.Circle,
                        _ => TextMarkerStyle.Square,
                    },
                };
                list.StartIndex = Math.Max(1, info.Shown);
                list.Margin = new Thickness(info.Level * IndentStep, 0, 0, 0);
                list.Padding = new Thickness(IndentStep, 0, 0, 0);
                list.MarkerOffset = 8;
                if (info.Type == ItemType.Task && info.Checked)
                {
                    paragraph.SetResourceReference(TextElement.ForegroundProperty, "SecondaryText");
                }

                break;
            case LineKind.More:
                paragraph.Margin = new Thickness((Math.Max(0, info.Level) + 1) * IndentStep, 0, 0, 0);
                break;
            case LineKind.Quote:
                paragraph.BorderThickness = new Thickness(3, 0, 0, 0);
                paragraph.SetResourceReference(Block.BorderBrushProperty, "Rule");
                paragraph.Padding = new Thickness(12, 0, 0, 0);
                paragraph.SetResourceReference(TextElement.ForegroundProperty, "SecondaryText");
                break;
            case LineKind.Code:
                paragraph.FontFamily = CodeFont;
                paragraph.FontSize = 13;
                paragraph.LineHeight = 19;
                paragraph.Padding = new Thickness(10, 6, 10, 6);
                paragraph.Margin = new Thickness(0, 2, 0, 2);
                paragraph.SetResourceReference(TextElement.BackgroundProperty, "Surface");
                break;
        }
    }

    // Reading the document back as Markdown

    /// <summary>Each block with where its Markdown starts in <see cref="Text"/>.</summary>
    private IEnumerable<(Block Block, int Start, string Markdown)> Lines()
    {
        var at = 0;
        Block? previous = null;
        string? previousMarkdown = null;
        for (var block = Document.Blocks.FirstBlock; block is not null; block = block.NextBlock)
        {
            var markdown = MarkdownOf(block);
            if (previous is not null)
            {
                at += _newline.Length;
                if (Blank(previous, previousMarkdown!, block, markdown))
                {
                    at += _newline.Length;
                }
            }

            var written = markdown.Contains('\n') && _newline == "\r\n" ? markdown.Replace("\n", "\r\n", StringComparison.Ordinal) : markdown;
            yield return (block, at, written);
            at += written.Length;
            previous = block;
            previousMarkdown = markdown;
        }
    }

    private static bool Blank(Block before, string beforeMarkdown, Block after, string afterMarkdown) =>
        beforeMarkdown.Trim().Length > 0 && afterMarkdown.Trim().Length > 0 
        && (MarkdownLines.NeedsBlank(Info(before), Info(after), afterMarkdown) || MarkdownLines.NeedsBlankAbove(Info(before), Info(after)));

    private string Join()
    {
        var text = new StringBuilder(_text.Length + 64);
        Block? previous = null;
        string? previousMarkdown = null;
        for (var block = Document.Blocks.FirstBlock; block is not null; block = block.NextBlock)
        {
            var markdown = MarkdownOf(block);
            if (previous is not null)
            {
                text.Append(_newline);
                if (Blank(previous, previousMarkdown!, block, markdown))
                {
                    text.Append(_newline);
                }
            }

            text.Append(markdown.Contains('\n') && _newline == "\r\n" ? markdown.Replace("\n", "\r\n", StringComparison.Ordinal) : markdown);
            previous = block;
            previousMarkdown = markdown;
        }

        return text.ToString();
    }

    /// <summary>A block's Markdown, written again only if it changed since it was last written.</summary>
    private string MarkdownOf(Block block)
    {
        var info = Info(block);
        if (!info.Dirty && info.Markdown is not null)
        {
            return info.Markdown;
        }

        var para = TextPara(block);
        var written = MarkdownLines.Write(info, para is null ? [] : Segments(para), para is null ? "" : CodeText(para), level => ParentItem(block, level));
        info.Markdown = info.Source is not null && written == info.Fresh ? info.Source : written;
        info.Dirty = false;
        return info.Markdown;
    }

    private static LineInfo? ParentItem(Block block, int level)
    {
        for (var previous = block.PreviousBlock; previous is not null; previous = previous.PreviousBlock)
        {
            var info = Info(previous);
            if (info.Kind == LineKind.Item && info.Level == level)
            {
                return info;
            }

            if (info.Kind is not (LineKind.Item or LineKind.More) && !(info.Kind == LineKind.Text && TextPara(previous) is { } empty && VisibleLength(empty) == 0))
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>The formatted text of a paragraph, or of the part between <paramref name="from"/> and <paramref name="to"/>.</summary>
    internal static List<Segment> Segments(Paragraph paragraph, TextPointer? from = null, TextPointer? to = null)
    {
        var segments = new List<Segment>();
        Collect(paragraph.Inlines, paragraph, from, to, segments);
        return MarkdownLines.Merge(segments);
    }

    private static void Collect(InlineCollection inlines, Paragraph paragraph, TextPointer? from, TextPointer? to, List<Segment> into)
    {
        foreach (var inline in inlines)
        {
            if (to is not null && inline.ContentStart.CompareTo(to) >= 0)
            {
                return;
            }

            if (from is not null && inline.ContentEnd.CompareTo(from) <= 0)
            {
                continue;
            }

            switch (inline)
            {
                case Run run:
                    var text = run.Text;
                    if (from is not null || to is not null)
                    {
                        var start = from is not null && from.CompareTo(run.ContentStart) > 0 ? run.ContentStart.GetOffsetToPosition(from) : 0;
                        var end = to is not null && to.CompareTo(run.ContentEnd) < 0 ? run.ContentStart.GetOffsetToPosition(to) : text.Length;
                        text = text[Math.Clamp(start, 0, text.Length)..Math.Clamp(end, Math.Clamp(start, 0, text.Length), text.Length)];
                    }

                    // A lone \r, from an old file, is a line break in Markdown, so it's kept as a space between words.
                    into.Add(new Segment(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace('\n', ' '), StyleOf(run, paragraph)));
                    break;
                case LineBreak:
                    into.Add(new Segment(" ", default));
                    break;
                case Span span:
                    Collect(span.Inlines, paragraph, from, to, into);
                    break;
            }
        }
    }

    internal static InlineStyle StyleOf(Run run, Paragraph paragraph)
    {
        var underline = false;
        var strike = false;
        string? link = null;
        string? title = null;
        for (DependencyObject? element = run; element is Inline inline; element = LogicalTreeHelper.GetParent(inline))
        {
            // A link draws its own underline. Only an underline set on the text inside it is the note's.
            foreach (var decoration in inline is Hyperlink ? [] : inline.TextDecorations ?? [])
            {
                underline |= decoration.Location == TextDecorationLocation.Underline;
                strike |= decoration.Location == TextDecorationLocation.Strikethrough;
            }

            if (inline is Hyperlink hyperlink && link is null)
            {
                link = hyperlink.Tag as string ?? hyperlink.NavigateUri?.OriginalString;
                title = hyperlink.GetValue(TitleProperty) as string;
            }
        }

        var code = IsCode(run.FontFamily) && !IsCode(paragraph.FontFamily);

        return new InlineStyle(
            Bold: !code && run.FontWeight.ToOpenTypeWeight() >= 700,
            Italic: !code && run.FontStyle != FontStyles.Normal,
            Underline: !code && underline,
            Strike: !code && strike,
            Code: code,
            Link: link,
            Title: link is null ? null : title);
    }

    internal static bool IsCode(FontFamily? family)
    {
        var source = family?.Source ?? "";
        return source.Contains("Mono", StringComparison.OrdinalIgnoreCase) || source.Contains("Consolas", StringComparison.OrdinalIgnoreCase)
            || source.Contains("Courier", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A code block's text, with its line breaks as new lines.</summary>
    internal static string CodeText(Paragraph paragraph)
    {
        var text = new StringBuilder();
        void Walk(InlineCollection inlines)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case Run run:
                        text.Append(run.Text.Replace("\r\n", "\n"));
                        break;
                    case LineBreak:
                        text.Append('\n');
                        break;
                    case Span span:
                        Walk(span.Inlines);
                        break;
                }
            }
        }

        Walk(paragraph.Inlines);
        return text.ToString();
    }

    // Characters within a paragraph

    internal static int VisibleLength(Paragraph paragraph) => CharOffset(paragraph, paragraph.ContentEnd);

    /// <summary>How many characters of text come before <paramref name="target"/> in the paragraph. A line break counts as one.</summary>
    internal static int CharOffset(Paragraph paragraph, TextPointer target)
    {
        var count = 0;
        var pointer = paragraph.ContentStart;
        if (target.CompareTo(pointer) <= 0)
        {
            return 0;
        }

        while (pointer is not null && pointer.CompareTo(target) < 0 && pointer.CompareTo(paragraph.ContentEnd) < 0)
        {
            var context = pointer.GetPointerContext(LogicalDirection.Forward);
            if (context == TextPointerContext.Text)
            {
                var length = pointer.GetTextRunLength(LogicalDirection.Forward);
                var distance = pointer.GetOffsetToPosition(target);
                if (distance <= length)
                {
                    return count + distance;
                }

                count += length;
                pointer = pointer.GetPositionAtOffset(length);
            }
            else
            {
                if (context == TextPointerContext.ElementStart && pointer.GetAdjacentElement(LogicalDirection.Forward) is LineBreak)
                {
                    count++;
                }

                pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
            }
        }

        return count;
    }

    internal static TextPointer PointerAt(Paragraph paragraph, int offset)
    {
        var pointer = paragraph.ContentStart;
        while (pointer is not null && pointer.CompareTo(paragraph.ContentEnd) < 0)
        {
            var context = pointer.GetPointerContext(LogicalDirection.Forward);
            if (context == TextPointerContext.Text)
            {
                var length = pointer.GetTextRunLength(LogicalDirection.Forward);
                if (offset <= length)
                {
                    return pointer.GetPositionAtOffset(offset)!;
                }

                offset -= length;
                pointer = pointer.GetPositionAtOffset(length);
            }
            else
            {
                if (context == TextPointerContext.ElementStart && pointer.GetAdjacentElement(LogicalDirection.Forward) is LineBreak lineBreak)
                {
                    if (offset == 0)
                    {
                        return pointer.GetInsertionPosition(LogicalDirection.Backward);
                    }

                    offset--;
                    pointer = lineBreak.ElementEnd;
                    continue;
                }

                pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
            }
        }

        return paragraph.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
    }

    private Caret Capture(TextPointer pointer)
    {
        var top = TopOrNull(pointer);
        var index = 0;
        for (var block = Document.Blocks.FirstBlock; block is not null && block != top; block = block.NextBlock)
        {
            index++;
        }

        var para = top is null ? null : TextPara(top);
        return new Caret(index, para is null ? 0 : CharOffset(para, pointer));
    }

    private TextPointer Place(Caret caret)
    {
        var block = Document.Blocks.ElementAtOrDefault(caret.Block) ?? Document.Blocks.LastBlock;
        if (block is null)
        {
            return Document.ContentEnd;
        }

        return TextPara(block) is { } para ? PointerAt(para, caret.Offset) : block.ElementEnd.GetInsertionPosition(LogicalDirection.Forward);
    }

    // Changes and undo

    private void OnSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_internal == 0)
        {
            FollowCharacterBefore();
            _caretBefore = Selection.Start.GetPositionAtOffset(0, LogicalDirection.Backward);
        }

        FormatChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// As in Word, the caret has the formatting of the character before it, or at the start of a line the one after
    /// it. WPF can take it from the other side of a formatting edge (after a space it always looks ahead), so what
    /// differs is set for the next typing, the way Ctrl+B with nothing selected does. The toolbar and what's typed
    /// next then agree. A move clears it again.
    /// </summary>
    private void FollowCharacterBefore()
    {
        if (!Selection.IsEmpty || IsReadOnly || CaretPosition is not { Paragraph: { } para } caret || TopOrNull(caret) is not { } top
            || TextPara(top) != para || Info(top) is not { IsTextLike: true } info || info.Kind == LineKind.Code)
        {
            return;
        }

        var offset = CharOffset(para, caret);
        if (VisibleLength(para) == 0 || PointerAt(para, offset > 0 ? offset : 1).Parent is not Run run || run.Text.Length == 0)
        {
            return;
        }

        var want = StyleOf(run, para);
        var now = CurrentFormat();
        if (want.Code != now.Code)
        {
            Selection.ApplyPropertyValue(TextElement.FontFamilyProperty, want.Code ? CodeFont : TextFont);
            Selection.ApplyPropertyValue(TextElement.FontSizeProperty, want.Code ? 13.0 : BaseSize);
        }

        if (want.Bold != now.Bold && !want.Code)
        {
            Selection.ApplyPropertyValue(TextElement.FontWeightProperty, want.Bold ? FontWeights.Bold : FontWeights.Normal);
        }

        if (want.Italic != now.Italic && !want.Code)
        {
            Selection.ApplyPropertyValue(TextElement.FontStyleProperty, want.Italic ? FontStyles.Italic : FontStyles.Normal);
        }

        if ((want.Underline != now.Underline || want.Strike != now.Strike) && !want.Code)
        {
            Selection.ApplyPropertyValue(Inline.TextDecorationsProperty, Decorations(want.Underline, want.Strike) ?? new TextDecorationCollection());
        }
    }

    private void OnChanged(object sender, TextChangedEventArgs e)
    {
        foreach (var change in e.Changes)
        {
            MarkChanged(change.Offset, change.AddedLength);
        }

        if (_internal > 0)
        {
            return;
        }

        _internal++;
        try
        {
            Tidy();
        }
        finally
        {
            _internal--;
        }

        Commit(newStep: false);
    }

    private readonly HashSet<Block> _touched = [];

    private void MarkChanged(int offset, int length)
    {
        var start = Document.ContentStart.GetPositionAtOffset(Math.Max(0, offset)) ?? Document.ContentEnd;
        var end = Document.ContentStart.GetPositionAtOffset(Math.Max(0, offset + length)) ?? Document.ContentEnd;
        var first = TopOrNull(start);
        var last = TopOrNull(end);
        for (var block = first; block is not null; block = block.NextBlock)
        {
            Touch(block);
            if (block == last)
            {
                break;
            }
        }
    }

    internal void Touch(Block block)
    {
        Info(block).Dirty = true;
        _touched.Add(block);
    }

    /// <summary>
    /// Puts edited blocks back in the editor's shape after typing or deleting across them: one paragraph per line,
    /// each list item in its own list, and each with its own record. Then renumbers the lists they're in.
    /// </summary>
    private void Tidy()
    {
        if (_touched.Count == 0)
        {
            return;
        }

        var caretPara = CaretPosition.Paragraph;
        var caretOffset = caretPara is null ? 0 : CharOffset(caretPara, CaretPosition);
        var moved = false;
        var touched = _touched.Where(block => block.Parent is FlowDocument).ToList();
        _touched.Clear();
        Block? first = null;
        Block? last = null;
        foreach (var block in touched)
        {
            var replaced = Canonical(block, out var changed);
            moved |= changed;
            foreach (var made in replaced)
            {
                Info(made).Dirty = true;
                first = first is null || IndexBefore(made, first) ? made : first;
                last = last is null || IndexBefore(last, made) ? made : last;
            }
        }

        var seen = new HashSet<LineInfo>();
        for (var block = first?.PreviousBlock ?? first; block is not null; block = block.NextBlock)
        {
            // A split paragraph can carry its neighbor's record. Each gets its own.
            var info = Info(block);
            if (!seen.Add(info))
            {
                var copy = info.Next();
                copy.Number = info.Number;
                copy.Dirty = true;
                block.Tag = copy;
                if (TextPara(block) is { } para)
                {
                    para.Tag = copy;
                }
            }

            if (block == last?.NextBlock)
            {
                break;
            }
        }

        if (first is not null && last is not null)
        {
            Renumber(first, last, write: true);
        }

        if (moved && caretPara is not null && caretPara.Parent is not null)
        {
            CaretPosition = PointerAt(caretPara, caretOffset);
        }
    }

    private static bool IndexBefore(Block a, Block b)
    {
        return a.ContentStart.CompareTo(b.ContentStart) < 0;
    }

    /// <summary>Replaces a block that isn't in the editor's shape with blocks that are, keeping its paragraphs.</summary>
    private List<Block> Canonical(Block block, out bool changed)
    {
        changed = false;
        switch (block)
        {
            case BlockUIContainer:
                return [block];
            case Paragraph paragraph:
                var info = Info(paragraph);
                if (info.Kind == LineKind.Item)
                {
                    changed = true;
                    return [Wrap(paragraph, block)];
                }

                if (info.IsAtomic)
                {
                    info.Kind = LineKind.Text;
                    info.Source = null;
                    Restyle(paragraph);
                }

                return [block];
            case List list when list.ListItems.Count == 1 && list.ListItems.FirstListItem!.Blocks.Count == 1
                                && list.ListItems.FirstListItem.Blocks.FirstBlock is Paragraph only && only.Tag is LineInfo { Kind: LineKind.Item } item:
                list.Tag = item;
                return [block];
        }

        // Anything else is flattened to its paragraphs, each made a line of its own kind.
        changed = true;
        var paragraphs = new List<Paragraph>();
        Flatten(block, paragraphs);
        var anchor = block;
        var made = new List<Block>();
        LineInfo? previous = block.Tag as LineInfo;
        foreach (var paragraph in paragraphs)
        {
            if (paragraph.Parent is ListItem holder)
            {
                holder.Blocks.Remove(paragraph);
            }
            else if (paragraph.Parent is Section section)
            {
                section.Blocks.Remove(paragraph);
            }
            else if (paragraph.Parent is FlowDocument)
            {
                Document.Blocks.Remove(paragraph);
            }

            if (paragraph.Tag is not LineInfo)
            {
                paragraph.Tag = previous is { Kind: LineKind.Item } ? previous.Next() : LineInfo.Of(LineKind.Text);
            }

            previous = (LineInfo)paragraph.Tag;
            Block top = paragraph;
            if (previous.Kind == LineKind.Item)
            {
                top = new List(new ListItem(paragraph)) { Tag = previous };
            }

            Document.Blocks.InsertAfter(anchor, top);
            Restyle(top);
            anchor = top;
            made.Add(top);
        }

        Document.Blocks.Remove(block);
        if (made.Count == 0)
        {
            var empty = new Paragraph { Tag = LineInfo.Of(LineKind.Text) };
            if (anchor == block)
            {
                Document.Blocks.InsertAfter(block, empty);
            }

            made.Add(empty);
        }

        return made;
    }

    private static void Flatten(Block block, List<Paragraph> into)
    {
        switch (block)
        {
            case Paragraph paragraph:
                into.Add(paragraph);
                break;
            case List list:
                foreach (var item in list.ListItems.ToList())
                {
                    foreach (var child in item.Blocks.ToList())
                    {
                        if (child is Paragraph { Tag: null } bare && list.Tag is LineInfo listInfo)
                        {
                            bare.Tag = into.Count == 0 ? listInfo : listInfo.Next();
                        }

                        Flatten(child, into);
                    }
                }

                break;
            case Section section:
                foreach (var child in section.Blocks.ToList())
                {
                    Flatten(child, into);
                }

                break;
        }
    }

    /// <summary>Puts a list item's paragraph in a list of its own, where the paragraph was.</summary>
    private List Wrap(Paragraph paragraph, Block where)
    {
        var info = Info(paragraph);
        var list = new List { Tag = info };
        if (where.Parent is FlowDocument)
        {
            Document.Blocks.InsertAfter(where, list);
        }

        if (paragraph.Parent is FlowDocument)
        {
            Document.Blocks.Remove(paragraph);
        }
        else if (paragraph.Parent is ListItem holder)
        {
            holder.Blocks.Remove(paragraph);
        }

        list.ListItems.Add(new ListItem(paragraph));
        Restyle(list);
        return list;
    }

    /// <summary>
    /// Numbers each numbered list from its first item, counting items at the same level, so the numbers shown are
    /// the ones Markdown would show. A change from 1. to 1) starts a new list, as it does in Markdown. With <paramref name="write"/>, an item whose number changed writes the new one.
    /// </summary>
    private void Renumber(Block? from, Block? to, bool write)
    {
        if (from is null || to is null)
        {
            return;
        }

        // Back to where the list starts, and on to where it ends.
        var start = from;
        while (start.PreviousBlock is { } previous && InList(previous))
        {
            start = previous;
        }

        var counters = new List<(ItemType Type, int Number, char Delimiter)?>();
        var passedEnd = false;
        for (var block = start; block is not null; block = block.NextBlock)
        {
            passedEnd |= block == to.NextBlock;
            if (passedEnd && !InList(block))
            {
                break;
            }

            var info = Info(block);
            if (info.Kind != LineKind.Item)
            {
                if (!InList(block))
                {
                    counters.Clear();
                }

                continue;
            }

            var level = Math.Max(0, info.Level);
            while (counters.Count > level + 1)
            {
                counters.RemoveAt(counters.Count - 1);
            }

            while (counters.Count < level + 1)
            {
                counters.Add(null);
            }

            var number = info.Type == ItemType.Number
                ? counters[level] is { Type: ItemType.Number } last && last.Delimiter == info.Delimiter ? last.Number + 1 : Math.Max(0, info.Number)
                : 1;
            counters[level] = (info.Type, number, info.Delimiter);
            if (info.Shown != number || write && info.Type == ItemType.Number && info.Number != number)
            {
                info.Shown = number;
                if (write && info.Type == ItemType.Number && info.Number != number)
                {
                    info.Number = number;
                    info.Dirty = true;
                }

                if (block is List list)
                {
                    list.StartIndex = Math.Max(1, number);
                }
            }
        }
    }

    private static bool InList(Block block)
    {
        var info = Info(block);
        return info.Kind is LineKind.Item or LineKind.More || info.Kind == LineKind.Text && TextPara(block) is { } para && VisibleLength(para) == 0 && info.Source is null or "";
    }

    /// <summary>
    /// Writes the Markdown again after a change and keeps undo. Typing without a pause is one undo step; a pause, a
    /// new line, or a formatting command starts the next.
    /// </summary>
    private void Commit(bool newStep)
    {
        var text = Join();
        if (text == _text)
        {
            FormatChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var now = DateTime.UtcNow;
        var blocks = Document.Blocks.Count;
        var caretAfter = Capture(Selection.Start);
        var startStep = newStep || _newStep || _undo.Count == 0 || now - _lastEdit > UndoPause || blocks != _burstBlocks;
        if (startStep)
        {
            _burstBase = _text;
            var before = _changeCaret ?? (_caretBefore is { } pointer && pointer.IsInSameDocument(Document.ContentStart) ? Capture(pointer) : caretAfter);
            _undo.Add(Diff(_burstBase, text, before, caretAfter));
            if (_undo.Count > MaxUndo)
            {
                _undo.RemoveAt(0);
            }
        }
        else
        {
            _undo[^1] = Diff(_burstBase, text, _undo[^1].Before, caretAfter);
        }

        _newStep = newStep;
        _burstBlocks = blocks;
        _lastEdit = now;
        _redo.Clear();
        _text = text;
        TextEdited?.Invoke(this, EventArgs.Empty);
        FormatChanged?.Invoke(this, EventArgs.Empty);
    }

    private static Step Diff(string before, string after, Caret caretBefore, Caret caretAfter)
    {
        var start = 0;
        var limit = Math.Min(before.Length, after.Length);
        while (start < limit && before[start] == after[start])
        {
            start++;
        }

        var end = 0;
        while (end < limit - start && before[before.Length - 1 - end] == after[after.Length - 1 - end])
        {
            end++;
        }

        return new Step(start, before[start..(before.Length - end)], after[start..(after.Length - end)], caretBefore, caretAfter);
    }

    private void UndoStep(List<Step> from, List<Step> to, bool undo)
    {
        if (from.Count == 0 || IsReadOnly)
        {
            return;
        }

        var step = from[^1];
        from.RemoveAt(from.Count - 1);
        var current = undo ? step.New : step.Old;
        var replacement = undo ? step.Old : step.New;
        if (step.Start + current.Length > _text.Length || string.CompareOrdinal(_text, step.Start, current, 0, current.Length) != 0)
        {
            // The text no longer matches this step, so the steps are dropped rather than applied in the wrong place.
            from.Clear();
            return;
        }

        var text = _text[..step.Start] + replacement + _text[(step.Start + current.Length)..];
        to.Add(step);
        Reload(text);
        _internal++;
        try
        {
            if (step.ByText)
            {
                CaretIndex = step.Start + replacement.Length;
            }
            else
            {
                CaretPosition = Place(undo ? step.Before : step.After);
            }

            if (undo && step.Space && CaretPosition.Paragraph is { } line && CharOffset(line, CaretPosition) == VisibleLength(line))
            {
                // Trailing spaces aren't kept in Markdown, so the space typed before the line was formatted is put back on screen.
                CaretPosition.InsertTextInRun(" ");
                CaretPosition = line.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
            }
        }
        finally
        {
            _internal--;
        }

        _newStep = true;
        _burstBase = _text;
        _caretBefore = Selection.Start.GetPositionAtOffset(0, LogicalDirection.Backward);
        TextEdited?.Invoke(this, EventArgs.Empty);
        FormatChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Runs an edit made by a command rather than by typing, as one undo step. Blocks it touches are marked by
    /// the text changes; <paramref name="action"/> marks any whose kind it changes.
    /// </summary>
    private void Change(Action action)
    {
        if (IsReadOnly)
        {
            return;
        }

        _caretBefore = Selection.Start.GetPositionAtOffset(0, LogicalDirection.Backward);

        // Where the caret was, fixed now: the pointer above moves with the text a command deletes.
        _changeCaret = Capture(Selection.Start);
        _internal++;
        BeginChange();
        try
        {
            action();
        }
        finally
        {
            EndChange();
            try
            {
                Tidy();
            }
            finally
            {
                _internal--;
            }
        }

        try
        {
            Commit(newStep: true);
        }
        finally
        {
            _changeCaret = null;
        }

        _newStep = true;
    }
}
