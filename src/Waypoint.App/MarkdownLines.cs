using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Waypoint.App;

/// <summary>What one line of the formatted editor is. Code, raw, and answer blocks can cover several lines of Markdown.</summary>
internal enum LineKind
{
    Text,
    Heading,
    Item,

    /// <summary>A line inside a list item that isn't its first, written indented under it.</summary>
    More,
    Quote,
    Code,

    /// <summary>Markdown the editor can't show as formatting, such as a table or HTML. Kept exactly as it was.</summary>
    Raw,
    Rule,

    /// <summary>A practice answer quote, hidden until Show answer is clicked.</summary>
    Answer,
}

internal enum ItemType
{
    Bullet,
    Number,
    Task,
}

internal readonly record struct InlineStyle(bool Bold, bool Italic, bool Underline, bool Strike, bool Code, string? Link, string? Title)
{
    public bool HasEmphasis => Bold || Italic || Underline || Strike;
}

internal sealed record Segment(string Text, InlineStyle Style);

/// <summary>
/// The editor's record of a line: what kind it is, and the Markdown it was loaded from. A line that still writes the
/// same as when it was loaded writes its original Markdown, so opening and saving a note never changes it.
/// </summary>
internal sealed class LineInfo
{
    public LineKind Kind;
    public int Level;
    public ItemType Type;
    public bool Checked;
    public string Bullet = "-";
    public char Delimiter = '.';
    public int Number = 1;

    /// <summary>The number shown, counted like Markdown counts, which can differ from the one typed.</summary>
    public int Shown = 1;

    /// <summary>The line holds Markdown the editor can't format, so its text is written back as it is, without escaping.</summary>
    public bool Verbatim;

    /// <summary>
    /// The line was loaded right under a table, HTML, or a hidden answer, with no blank line between, and Markdown read
    /// them apart. It's the line's shape at that time: once the line is a different kind, Markdown may not read them apart.
    /// </summary>
    public string? JoinsRaw;

    /// <summary>
    /// A table or HTML loaded right under a line, with no blank line between: that line's shape at that time. A line
    /// put above it later gets a blank line between, so Markdown doesn't read the two together.
    /// </summary>
    public string? RawUnder;

    public string? Fence;
    public string? CloseFence;

    /// <summary>The Markdown this came from, exactly.</summary>
    public string? Source;

    /// <summary>What this would have been written as when it was loaded. While it still writes that, <see cref="Source"/> is used.</summary>
    public string? Fresh;

    /// <summary>The loaded prefix (indent, marker, quote mark), kept while the line's shape is the same.</summary>
    public string? Prefix;
    public string? LoadedShape;

    /// <summary>The spaces before an item's marker, valid while <see cref="IndentLevel"/> is its level.</summary>
    public string? Indent;
    public int IndentLevel = -1;

    public string? Markdown;
    public bool Dirty = true;

    public string Shape => $"{Kind}|{Level}|{Type}|{Checked}|{Number}|{Bullet}|{Delimiter}";

    public bool IsTextLike => Kind is LineKind.Text or LineKind.Heading or LineKind.Item or LineKind.More or LineKind.Quote;

    public bool IsAtomic => Kind is LineKind.Raw or LineKind.Rule or LineKind.Answer;

    /// <summary>A new line of the same kind, as Enter makes: an unchecked item at the same level, with no Markdown yet.</summary>
    public LineInfo Next() => new()
    {
        Kind = Kind,
        Level = Level,
        Type = Type,
        Bullet = Bullet,
        Delimiter = Delimiter,
        Number = Number + 1,
        Indent = Indent,
        IndentLevel = IndentLevel,
    };

    public static LineInfo Of(LineKind kind, int level = 0) => new() { Kind = kind, Level = level };
}

/// <summary>A line or block as parsed: its record, its formatted text, and for code its plain text.</summary>
internal sealed record ParsedBlock(LineInfo Info, List<Segment> Segments, string Text = "");

/// <summary>
/// Markdown in and out for the formatted note editor, line by line, without WPF. Markdig decides what each line is;
/// each line is then one paragraph in the editor. Writing back produces conventional Markdown, and anything the
/// editor can't format is kept as raw text.
/// </summary>
internal static partial class MarkdownLines
{
    public static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .Build();

    // Parsing

    public static List<ParsedBlock> Parse(string text, bool hideAnswers = true)
    {
        text = text.Replace("\r\n", "\n");
        try
        {
            return ParseWhole(text, hideAnswers);
        }
        catch (ArgumentException)
        {
            // Markdig gives up on one enormous paragraph full of emphasis, such as a long transcript with a bold
            // word on every line. Each line is its own paragraph in the editor anyway, so it's read in pieces.
            return ParseInPieces(text.Split('\n'), hideAnswers, 400);
        }
    }

    private static List<ParsedBlock> ParseInPieces(string[] lines, bool hideAnswers, int size)
    {
        var blocks = new List<ParsedBlock>();
        for (var at = 0; at < lines.Length;)
        {
            var end = Math.Min(lines.Length, at + size);
            if (end < lines.Length)
            {
                // A piece ends at a blank line when there's one near the end, so a paragraph or list isn't cut.
                for (var i = end - 1; i > at + size / 2; i--)
                {
                    if (lines[i].Trim().Length == 0)
                    {
                        end = i + 1;
                        break;
                    }
                }
            }

            var piece = lines[at..end];
            try
            {
                blocks.AddRange(ParseWhole(string.Join("\n", piece), hideAnswers));
            }
            catch (ArgumentException) when (size > 1)
            {
                blocks.AddRange(ParseInPieces(piece, hideAnswers, Math.Max(1, size / 4)));
            }

            at = end;
        }

        return blocks;
    }

    private static List<ParsedBlock> ParseWhole(string text, bool hideAnswers)
    {
        var lines = text.Split('\n');
        var starts = new int[lines.Length];
        for (int i = 1, at = 0; i < lines.Length; i++)
        {
            at += lines[i - 1].Length + 1;
            starts[i] = at;
        }

        var parser = new Parser(text, lines, starts, hideAnswers);
        parser.Walk(Markdown.Parse(text, Pipeline));
        return parser.Build();
    }

    private sealed class Parser(string text, string[] lines, int[] starts, bool hideAnswers)
    {
        private readonly ParsedBlock?[] _at = new ParsedBlock?[lines.Length];
        private readonly int[] _span = new int[lines.Length];

        public void Walk(Markdig.Syntax.MarkdownDocument document)
        {
            foreach (var block in document)
            {
                Top(block);
            }
        }

        private int LineOf(int offset)
        {
            var index = Array.BinarySearch(starts, Math.Clamp(offset, 0, Math.Max(0, text.Length)));
            return index >= 0 ? index : ~index - 1;
        }

        private (int First, int Last) Lines(Block block)
        {
            var first = Math.Clamp(block.Line, 0, lines.Length - 1);
            var last = block.Span.End >= block.Span.Start && block.Span.End >= 0 ? LineOf(block.Span.End) : first;
            return (first, Math.Clamp(Math.Max(first, last), first, lines.Length - 1));
        }

        private void Put(int line, ParsedBlock parsed, int count = 1)
        {
            _at[line] = parsed;
            _span[line] = count;
            for (var i = line + 1; i < line + count && i < lines.Length; i++)
            {
                _at[i] = null;
                _span[i] = -1;
            }
        }

        private void Raw(Block block)
        {
            var (first, last) = Lines(block);
            RawLines(first, last);
        }

        private void RawLines(int first, int last)
        {
            var source = string.Join("\n", lines[first..(last + 1)]);
            Put(first, new ParsedBlock(new LineInfo { Kind = LineKind.Raw, Source = source }, [], source), last - first + 1);
        }

        private void Top(Block block)
        {
            switch (block)
            {
                case HeadingBlock { IsSetext: false } heading:
                    Paragraph(heading.Inline, heading.Line, heading.Line, _ => new LineInfo { Kind = LineKind.Heading, Level = heading.Level }, HeadingPrefix());
                    break;
                case ParagraphBlock paragraph:
                    var (first, last) = Lines(paragraph);
                    Paragraph(paragraph.Inline, first, last, _ => LineInfo.Of(LineKind.Text), Spaces());
                    break;
                case ListBlock list:
                    List(list, 0);
                    break;
                case QuoteBlock quote:
                    Quote(quote);
                    break;
                case FencedCodeBlock fenced:
                    Code(fenced);
                    break;
                case ThematicBreakBlock rule:
                    var line = Lines(rule).First;
                    Put(line, new ParsedBlock(new LineInfo { Kind = LineKind.Rule, Source = lines[line] }, []));
                    break;
                default:
                    Raw(block);
                    break;
            }
        }

        private void Code(FencedCodeBlock fenced)
        {
            var (first, last) = Lines(fenced);
            var closed = fenced.ClosingFencedCharCount > 0 && last > first;
            var body = closed ? lines[(first + 1)..last] : lines[(first + 1)..(last + 1)];
            var info = new LineInfo
            {
                Kind = LineKind.Code,
                Fence = lines[first],
                CloseFence = closed ? lines[last] : null,
                Source = string.Join("\n", lines[first..(last + 1)]),
            };
            Put(first, new ParsedBlock(info, [], string.Join("\n", body)), last - first + 1);
        }

        private void Quote(QuoteBlock quote)
        {
            var (first, last) = Lines(quote);
            if (hideAnswers && IsAnswer(quote))
            {
                var source = string.Join("\n", lines[first..(last + 1)]);
                Put(first, new ParsedBlock(new LineInfo { Kind = LineKind.Answer, Source = source }, [], source), last - first + 1);
                return;
            }

            if (!quote.All(child => child is ParagraphBlock))
            {
                RawLines(first, last);
                return;
            }

            // Lines between its paragraphs are empty quote lines, ">" on their own.
            for (var i = first; i <= last; i++)
            {
                var prefix = QuotePrefix().Match(lines[i]);
                Put(i, Line(new LineInfo { Kind = LineKind.Quote }, lines[i], prefix.Success ? prefix.Length : 0, [new Segment(lines[i][(prefix.Success ? prefix.Length : 0)..], default)], verbatim: false));
            }

            foreach (var paragraph in quote.OfType<ParagraphBlock>())
            {
                var (pFirst, pLast) = Lines(paragraph);
                Paragraph(paragraph.Inline, pFirst, pLast, _ => LineInfo.Of(LineKind.Quote), QuotePrefix());
            }
        }

        private static bool IsAnswer(QuoteBlock quote) =>
            quote.FirstOrDefault() is ParagraphBlock { Inline.FirstChild: EmphasisInline { FirstChild: LiteralInline literal } }
            && literal.Content.ToString().StartsWith("Answer", StringComparison.OrdinalIgnoreCase);

        private void List(ListBlock list, int level)
        {
            foreach (var item in list.OfType<ListItemBlock>())
            {
                var info = new LineInfo
                {
                    Kind = LineKind.Item,
                    Level = level,
                    Type = list.IsOrdered ? ItemType.Number : ItemType.Bullet,
                    Bullet = list.IsOrdered ? "-" : list.BulletType.ToString(),
                    Delimiter = list.IsOrdered ? list.OrderedDelimiter : '.',
                    Number = list.IsOrdered ? Math.Max(0, item.Order) : 1,
                };

                var line = Math.Clamp(item.Line, 0, lines.Length - 1);
                info.Indent = Spaces().Match(lines[line]).Value;
                info.IndentLevel = level;
                if (item.Count == 0)
                {
                    var marker = ItemPrefix().Match(lines[line]);
                    Put(line, Line(info, lines[line], marker.Success ? marker.Length : lines[line].Length, [], verbatim: false));
                    continue;
                }

                if (item[0] is not ParagraphBlock first || first.Line != item.Line || level > 8)
                {
                    Raw(item);
                    continue;
                }

                if (first.Inline?.FirstChild is TaskList task)
                {
                    info.Type = ItemType.Task;
                    info.Checked = task.Checked;
                    info.Bullet = list.IsOrdered ? "-" : list.BulletType.ToString();
                    info.Number = 1;
                    if (list.IsOrdered)
                    {
                        Raw(item);
                        continue;
                    }
                }

                var (pFirst, pLast) = Lines(first);
                Paragraph(first.Inline, pFirst, pLast, index => index == 0 ? info : LineInfo.Of(LineKind.More, level), index => index == 0 ? (info.Type == ItemType.Task ? TaskPrefix() : ItemPrefix()) : Spaces());

                foreach (var child in item.Skip(1))
                {
                    switch (child)
                    {
                        case ParagraphBlock more:
                            var (mFirst, mLast) = Lines(more);
                            Paragraph(more.Inline, mFirst, mLast, _ => LineInfo.Of(LineKind.More, level), Spaces());
                            break;
                        case ListBlock nested:
                            List(nested, level + 1);
                            break;
                        default:
                            Raw(child);
                            break;
                    }
                }
            }
        }

        private void Paragraph(ContainerInline? inline, int first, int last, Func<int, LineInfo> info, Regex prefix) =>
            Paragraph(inline, first, last, info, _ => prefix);

        /// <summary>
        /// Splits a paragraph's inlines at its line breaks, one group per line. Formatting that runs across a line break
        /// can't be split, so those lines keep their Markdown as text.
        /// </summary>
        private void Paragraph(ContainerInline? inline, int first, int last, Func<int, LineInfo> info, Func<int, Regex> prefix)
        {
            var groups = new List<List<Inline>> { new() };
            var nestedBreak = false;
            for (var child = inline?.FirstChild; child is not null; child = child.NextSibling)
            {
                if (child is LineBreakInline)
                {
                    groups.Add([]);
                }
                else
                {
                    nestedBreak |= child is ContainerInline container && container.FindDescendants<LineBreakInline>().Any();
                    groups[^1].Add(child);
                }
            }

            var count = last - first + 1;
            var verbatim = nestedBreak || groups.Count != count;
            for (var i = 0; i < count; i++)
            {
                var line = lines[first + i];
                var match = prefix(i).Match(line);
                var start = match.Success ? match.Length : 0;
                var segments = new List<Segment>();
                var lineVerbatim = verbatim;
                if (!lineVerbatim)
                {
                    var group = groups[i];
                    if (group.FirstOrDefault() is TaskList)
                    {
                        group = group.Skip(1).ToList();
                    }

                    lineVerbatim |= !Sequence(group, default, segments);

                    if (segments.Count > 0 && segments[0].Style == default)
                    {
                        segments[0] = segments[0] with { Text = segments[0].Text.TrimStart() };
                    }
                }

                if (lineVerbatim)
                {
                    // A lone \r shows as a space, as the editor reads it back, so the line still writes as it was loaded.
                    segments = [new Segment(line[start..].Replace('\r', ' '), default)];
                }

                Put(first + i, Line(info(i), line, start, segments, lineVerbatim));
            }
        }

        private static ParsedBlock Line(LineInfo info, string line, int prefixLength, List<Segment> segments, bool verbatim)
        {
            info.Source = line;
            info.Prefix = line[..Math.Min(prefixLength, line.Length)];
            info.Verbatim = verbatim;
            return new ParsedBlock(info, Merge(segments));
        }

        /// <summary>False when the inline is something the editor can't format, such as HTML or an image.</summary>
        private static bool Segments(Inline inline, InlineStyle style, List<Segment> into)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    into.Add(new Segment(literal.Content.ToString(), style));
                    return true;
                case HtmlEntityInline entity:
                    into.Add(new Segment(entity.Transcoded.ToString(), style));
                    return true;
                case CodeInline code:
                    into.Add(new Segment(code.Content, style with { Code = true }));
                    return true;
                case AutolinkInline autolink when style.Link is null:
                    into.Add(new Segment(autolink.Url, style with { Link = autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url, Title = null }));
                    return true;
                case LinkInline { IsImage: false } link when style.Link is null && link.Url is not null:
                    return Children(link, style with { Link = link.Url, Title = string.IsNullOrEmpty(link.Title) ? null : link.Title }, into);
                case EmphasisInline emphasis:
                    InlineStyle? styled = (emphasis.DelimiterChar, emphasis.DelimiterCount) switch
                    {
                        ('*' or '_', 1) => style with { Italic = true },
                        ('*' or '_', 2) => style with { Bold = true },
                        ('~', 2) => style with { Strike = true },
                        ('+', 2) => style with { Underline = true },
                        _ => null,
                    };
                    return styled is { } next && Children(emphasis, next, into);
                case LineBreakInline:
                case TaskList:
                case HtmlInline:
                case LinkInline:
                    return false;
                case ContainerInline container:
                    return Children(container, style, into);
                default:
                    return false;
            }
        }

        private static bool IsTag(Inline inline, string tag) => inline is HtmlInline html && string.Equals(html.Tag, tag, StringComparison.OrdinalIgnoreCase);

        private static bool Children(ContainerInline container, InlineStyle style, List<Segment> into)
        {
            var items = new List<Inline>();
            for (var child = container.FirstChild; child is not null; child = child.NextSibling)
            {
                items.Add(child);
            }

            return Sequence(items, style, into);
        }

        /// <summary>
        /// A run of sibling inlines. Underline is written as <u>text</u>, which other Markdown programs show as
        /// underlined too, so the tags are read as a pair here.
        /// </summary>
        private static bool Sequence(List<Inline> items, InlineStyle style, List<Segment> into)
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (IsTag(items[i], "<u>"))
                {
                    var close = items.FindIndex(i + 1, item => IsTag(item, "</u>"));
                    if (close < 0)
                    {
                        return false;
                    }

                    if (!Sequence(items.GetRange(i + 1, close - i - 1), style with { Underline = true }, into))
                    {
                        return false;
                    }

                    i = close;
                    continue;
                }

                if (!Segments(items[i], style, into))
                {
                    return false;
                }
            }

            return true;
        }

        public List<ParsedBlock> Build()
        {
            var blocks = new List<ParsedBlock>();
            for (var i = 0; i < lines.Length; i++)
            {
                if (_span[i] < 0)
                {
                    continue;
                }

                if (_at[i] is { } parsed)
                {
                    blocks.Add(parsed);
                    i += _span[i] - 1;
                }
                else if (lines[i].Trim().Length == 0)
                {
                    var info = LineInfo.Of(LineKind.Text);
                    info.Source = lines[i];
                    info.Prefix = lines[i];
                    blocks.Add(new ParsedBlock(info, []));
                }
                else
                {
                    // Nothing claimed it, so it's kept exactly as it is.
                    RawLines(i, i);
                    blocks.Add(_at[i]!);
                }
            }

            for (var i = 1; i < blocks.Count; i++)
            {
                blocks[i].Info.JoinsRaw = blocks[i - 1].Info.Kind is LineKind.Raw or LineKind.Answer && !IsBlank(blocks[i]) ? blocks[i].Info.Shape : null;
                blocks[i].Info.RawUnder = blocks[i].Info.Kind == LineKind.Raw && !IsBlank(blocks[i - 1]) ? blocks[i - 1].Info.Shape : null;
            }

            // A blank line Markdown needs, such as between a list and the paragraph after it, isn't shown. It's written back.
            // One under a table or HTML stays in view, as it always has.
            for (var i = blocks.Count - 2; i >= 1; i--)
            {
                if (blocks[i].Info is { Kind: LineKind.Text, Source: "" } && !IsBlank(blocks[i - 1]) && !IsBlank(blocks[i + 1])
                    && blocks[i - 1].Info.Kind is not (LineKind.Raw or LineKind.Answer)
                    && NeedsBlank(blocks[i - 1].Info, blocks[i + 1].Info, blocks[i + 1].Info.Source ?? ""))
                {
                    blocks.RemoveAt(i);
                }
            }

            foreach (var block in blocks)
            {
                block.Info.LoadedShape = block.Info.Shape;
            }

            for (var i = 0; i < blocks.Count; i++)
            {
                var index = i;
                var info = blocks[i].Info;
                info.Fresh = Write(info, blocks[i].Segments, blocks[i].Text, level => ParentItem(blocks, index, level));
                info.Markdown = info.Source;
                info.Dirty = false;
            }

            return blocks;
        }

        private static bool IsBlank(ParsedBlock block) => block.Info is { Kind: LineKind.Text } && block.Segments.Count == 0 && (block.Info.Source ?? "").Trim().Length == 0;
    }

    /// <summary>
    /// The note written again from its formatting alone, as if every line had been edited. Used to check that what
    /// the editor writes means the same Markdown as what it read.
    /// </summary>
    internal static string Rewrite(string text)
    {
        var blocks = Parse(text, hideAnswers: false);
        foreach (var block in blocks)
        {
            block.Info.Source = block.Info.Kind is LineKind.Raw ? block.Info.Source : null;
            block.Info.Prefix = null;
            block.Info.LoadedShape = null;
            block.Info.Indent = null;
            block.Info.IndentLevel = -1;
        }

        var lines = new List<string>();
        for (var i = 0; i < blocks.Count; i++)
        {
            var index = i;
            var markdown = Write(blocks[i].Info, blocks[i].Segments, blocks[i].Text, level => ParentItem(blocks, index, level));
            if (i > 0 && lines[^1].Trim().Length > 0 && markdown.Trim().Length > 0 && (NeedsBlank(blocks[i - 1].Info, blocks[i].Info, markdown) || NeedsBlankAbove(blocks[i - 1].Info, blocks[i].Info)))
            {
                lines.Add("");
            }

            lines.Add(markdown);
        }

        return string.Join("\n", lines);
    }

    private static LineInfo? ParentItem(List<ParsedBlock> blocks, int index, int level)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            var info = blocks[i].Info;
            if (info.Kind == LineKind.Item && info.Level == level)
            {
                return info;
            }

            if (info.Kind is not (LineKind.Item or LineKind.More) && !(info.Kind == LineKind.Text && blocks[i].Segments.Count == 0))
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// True where Markdown needs a blank line that the editor doesn't show: a paragraph right after a list or quote
    /// would join it, a --- rule right after text would make it a heading, and a list starting at 2 can't follow text.
    /// A line right after a table or HTML would become part of it, unless it was already there when the note was read.
    /// </summary>
    public static bool NeedsBlank(LineInfo before, LineInfo after, string afterMarkdown)
    {
        if (before.Kind is LineKind.Raw or LineKind.Answer && after.JoinsRaw != after.Shape)
        {
            return true;
        }

        if (after.Kind == LineKind.Text && before.Kind is LineKind.Item or LineKind.More or LineKind.Quote)
        {
            return true;
        }

        if (after.Kind == LineKind.Rule && before.Kind is LineKind.Text or LineKind.Item or LineKind.More or LineKind.Quote
            && afterMarkdown.TrimStart().StartsWith('-'))
        {
            return true;
        }

        return before.Kind == LineKind.Text && after is { Kind: LineKind.Item, Level: 0 }
            && (after.Type == ItemType.Number && after.Number != 1 || ItemPrefix().Match(afterMarkdown).Length >= afterMarkdown.TrimEnd().Length);
    }

    /// <summary>
    /// True where a line sits right above a table or HTML that wasn't under it when the note was read, so Markdown
    /// could read the line as part of it. The editor shows no blank line there; one is written.
    /// </summary>
    public static bool NeedsBlankAbove(LineInfo before, LineInfo after) =>
        after.Kind == LineKind.Raw && before.Kind is not (LineKind.Raw or LineKind.Answer) && after.RawUnder != before.Shape;

    // Writing

    /// <summary>The Markdown for one line or block. <paramref name="parent"/> finds the item a nested item sits under.</summary>
    public static string Write(LineInfo info, IReadOnlyList<Segment> segments, string text, Func<int, LineInfo?> parent)
    {
        switch (info.Kind)
        {
            case LineKind.Raw:
            case LineKind.Answer:
                return info.Source ?? text;
            case LineKind.Rule:
                return info.Source ?? "---";
            case LineKind.Code:
                return WriteCode(info, text);
        }

        var keepPrefix = info.Prefix is not null && info.LoadedShape == info.Shape;
        var prefix = keepPrefix ? info.Prefix! : info.Kind switch
        {
            LineKind.Heading => new string('#', Math.Clamp(info.Level, 1, 6)) + " ",
            LineKind.Item => ItemIndent(info, parent) + Marker(info) + " ",
            LineKind.More => ItemIndent(Math.Max(0, info.Level), parent, more: true),
            LineKind.Quote => "> ",
            _ => "",
        };

        var body = WriteInlines(segments, info.Verbatim, info.Kind is LineKind.Heading ? LineStart.Heading : info.Kind == LineKind.Item ? LineStart.Item : LineStart.Text);
        if (body.Length == 0)
        {
            return info.Kind switch
            {
                LineKind.Quote => keepPrefix ? prefix : ">",
                LineKind.Item when info.Type == ItemType.Task => prefix,
                LineKind.Text => keepPrefix ? prefix : "",
                _ => prefix.TrimEnd(),
            };
        }

        return prefix + body;
    }

    private static string Marker(LineInfo info) => info.Type switch
    {
        ItemType.Number => $"{info.Number}{info.Delimiter}",
        ItemType.Task => $"{(info.Bullet is "*" or "+" ? info.Bullet : "-")} [{(info.Checked ? "x" : " ")}]",
        _ => info.Bullet is "*" or "+" ? info.Bullet : "-",
    };

    private static int MarkerWidth(LineInfo info) => info.Type == ItemType.Number ? $"{info.Number}{info.Delimiter}".Length + 1 : 2;

    private static string ItemIndent(LineInfo info, Func<int, LineInfo?> parent)
    {
        if (info.Indent is not null && info.IndentLevel == info.Level)
        {
            return info.Indent;
        }

        var indent = info.Level <= 0 ? "" : ItemIndent(info.Level - 1, parent, more: true);
        info.Indent = indent;
        info.IndentLevel = info.Level;
        return indent;
    }

    /// <summary>The column a line under the item at <paramref name="level"/> starts at.</summary>
    private static string ItemIndent(int level, Func<int, LineInfo?> parent, bool more)
    {
        if (parent(level) is not { } item)
        {
            return new string(' ', (level + 1) * 2);
        }

        var indent = item.Indent is not null && item.IndentLevel == item.Level ? item.Indent : new string(' ', level * 2);
        return indent + new string(' ', MarkerWidth(item));
    }

    private static string WriteCode(LineInfo info, string text)
    {
        var fence = info.Fence ?? "```";
        var marks = fence.TrimStart();
        var fenceChar = marks.Length > 0 && marks[0] == '~' ? '~' : '`';
        var count = marks.TakeWhile(c => c == fenceChar).Count();
        var longest = text.Split('\n').Select(line => line.TrimStart().TakeWhile(c => c == fenceChar).Count()).DefaultIfEmpty(0).Max();
        if (longest >= count)
        {
            var longer = new string(fenceChar, longest + 1);
            fence = longer + marks[count..];
            return $"{fence}\n{text}\n{longer}";
        }

        var close = info.CloseFence ?? new string(fenceChar, Math.Max(3, count));
        return $"{fence}\n{text}\n{close}";
    }

    public enum LineStart
    {
        None,
        Text,
        Item,
        Heading,
    }

    private enum Mark
    {
        Link,
        Bold,
        Italic,
        Underline,
        Strike,
    }

    /// <summary>
    /// Formatted text as Markdown: ** bold, * italic, <u> underline, ~~ strikethrough, ` code, and [links](url). Marks
    /// nest like tags, spaces stay outside them, and plain text is escaped only where it would otherwise mean something.
    /// </summary>
    public static string WriteInlines(IReadOnlyList<Segment> input, bool verbatim, LineStart lineStart)
    {
        var segments = Peel(Merge(input));
        if (lineStart != LineStart.None && segments.Count > 0 && !segments[0].Style.Code)
        {
            segments[0] = segments[0] with { Text = segments[0].Text.TrimStart() };
        }

        if (!verbatim && segments.Count > 0 && segments[^1] is { Style.Code: false } last)
        {
            segments[^1] = last with { Text = last.Text.TrimEnd() };
        }

        segments.RemoveAll(segment => segment.Text.Length == 0);
        var plain = string.Concat(segments.Select(segment => segment.Text));
        var output = new StringBuilder();
        var open = new List<(Mark Mark, string? Link, string? Title)>();
        var at = 0;
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            var style = segment.Style;
            var wanted = Wanted(style);

            // Keep the longest run of open marks that's still wanted, in order, and close the rest.
            var keep = 0;
            while (keep < open.Count && wanted.Contains(open[keep]))
            {
                keep++;
            }

            for (var i = open.Count - 1; i >= keep; i--)
            {
                output.Append(Close(open[i]));
                open.RemoveAt(i);
            }

            var linkText = style.Link is not null && !open.Any(mark => mark.Mark == Mark.Link);
            if (linkText && Bare(segments, index, output, plain, at) is { } bare)
            {
                output.Append(bare);
                at += segment.Text.Length;
                continue;
            }

            foreach (var mark in wanted.Where(mark => !open.Contains(mark)))
            {
                output.Append(Open(mark.Mark));
                open.Add(mark);
            }

            var before = at > 0 ? plain[at - 1] : ' ';
            var after = at + segment.Text.Length < plain.Length ? plain[at + segment.Text.Length] : ' ';
            if (style.Code)
            {
                output.Append(Code(segment.Text));
            }
            else if (verbatim)
            {
                output.Append(segment.Text);
            }
            else
            {
                var escaped = Escape(segment.Text, before, after, style.Link is not null);
                if (output.Length == 0 && lineStart != LineStart.None)
                {
                    escaped = EscapeLineStart(escaped, lineStart);
                }

                output.Append(escaped);
            }

            at += segment.Text.Length;
        }

        for (var i = open.Count - 1; i >= 0; i--)
        {
            output.Append(Close(open[i]));
        }

        if (lineStart == LineStart.Heading && !verbatim && output.Length > 0 && output[^1] == '#')
        {
            output.Insert(output.Length - 1, '\\');
        }

        return output.ToString();
    }

    /// <summary>A link whose text is its web address is written as the bare address, as typed.</summary>
    private static string? Bare(List<Segment> segments, int index, StringBuilder output, string plain, int at)
    {
        var segment = segments[index];
        var style = segment.Style;
        if (style.HasEmphasis || style.Code || style.Title is not null || segment.Text != style.Link
            || index + 1 < segments.Count && segments[index + 1].Style.Link == style.Link
            || !BareLink().IsMatch(segment.Text))
        {
            return null;
        }

        var before = output.Length == 0 ? ' ' : output[^1];
        var after = at + segment.Text.Length < plain.Length ? plain[at + segment.Text.Length] : ' ';
        return char.IsWhiteSpace(before) && !char.IsLetterOrDigit(after) && after != '/' ? segment.Text : $"<{segment.Text}>";
    }

    private static List<(Mark Mark, string? Link, string? Title)> Wanted(InlineStyle style)
    {
        var marks = new List<(Mark, string?, string?)>();
        if (style.Link is not null)
        {
            marks.Add((Mark.Link, style.Link, style.Title));
        }

        if (style.Bold)
        {
            marks.Add((Mark.Bold, null, null));
        }

        if (style.Italic)
        {
            marks.Add((Mark.Italic, null, null));
        }

        if (style.Underline)
        {
            marks.Add((Mark.Underline, null, null));
        }

        if (style.Strike)
        {
            marks.Add((Mark.Strike, null, null));
        }

        return marks;
    }

    private static string Open(Mark mark) => mark switch
    {
        Mark.Link => "[",
        Mark.Bold => "**",
        Mark.Italic => "*",
        Mark.Underline => "<u>",
        _ => "~~",
    };

    private static string Close((Mark Mark, string? Link, string? Title) mark) => mark.Mark switch
    {
        Mark.Link => $"]({Destination(mark.Link ?? "")}{(mark.Title is null ? "" : $" \"{mark.Title.Replace("\"", "\\\"", StringComparison.Ordinal)}\"")})",
        Mark.Underline => "</u>",
        _ => Open(mark.Mark),
    };

    private static string Destination(string url) =>
        url.Length == 0 || url.Any(c => c is ' ' or '(' or ')' or '<' or '>' || char.IsControl(c))
            ? "<" + url.Replace("<", "%3C", StringComparison.Ordinal).Replace(">", "%3E", StringComparison.Ordinal) + ">"
            : url;

    private static string Code(string text)
    {
        var longest = 0;
        for (int i = 0, run = 0; i < text.Length; i++)
        {
            run = text[i] == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        var fence = new string('`', longest + 1);
        var pad = text.StartsWith('`') || text.EndsWith('`') || (text.StartsWith(' ') && text.EndsWith(' ') && text.Trim().Length > 0) ? " " : "";
        return fence + pad + text + pad + fence;
    }

    /// <summary>Spaces at the edges of bold or italic go outside the marks, since "** x**" isn't bold.</summary>
    private static List<Segment> Peel(List<Segment> segments)
    {
        var peeled = new List<Segment>();
        foreach (var segment in segments)
        {
            if (!segment.Style.HasEmphasis || segment.Style.Code)
            {
                peeled.Add(segment);
                continue;
            }

            var bare = segment.Style with { Bold = false, Italic = false, Underline = false, Strike = false };
            var text = segment.Text;
            var core = text.Trim();
            if (core.Length == 0)
            {
                peeled.Add(new Segment(text, bare));
                continue;
            }

            var lead = text[..(text.Length - text.TrimStart().Length)];
            var trail = text[text.TrimEnd().Length..];
            if (lead.Length > 0)
            {
                peeled.Add(new Segment(lead, bare));
            }

            peeled.Add(new Segment(core, segment.Style));
            if (trail.Length > 0)
            {
                peeled.Add(new Segment(trail, bare));
            }
        }

        return Merge(peeled);
    }

    public static List<Segment> Merge(IEnumerable<Segment> segments)
    {
        var merged = new List<Segment>();
        foreach (var segment in segments)
        {
            if (segment.Text.Length == 0)
            {
                continue;
            }

            if (merged.Count > 0 && merged[^1].Style == segment.Style)
            {
                merged[^1] = merged[^1] with { Text = merged[^1].Text + segment.Text };
            }
            else
            {
                merged.Add(segment);
            }
        }

        return merged;
    }

    /// <summary>Backslashes go only before marks that would otherwise format, so "5 * 3" and snake_case stay as typed.</summary>
    public static string Escape(string text, char before, char after, bool inLink)
    {
        var output = new StringBuilder(text.Length + 4);
        var escapedOpen = new HashSet<int>();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var prev = i > 0 ? text[i - 1] : before;
            var next = i + 1 < text.Length ? text[i + 1] : after;
            var escape = c switch
            {
                '\\' => i + 1 >= text.Length || char.IsAsciiLetterOrDigit(next) is false && next is > ' ' and < (char)127,
                '`' => true,
                '*' => !(char.IsWhiteSpace(prev) && char.IsWhiteSpace(next)),
                '_' => !(char.IsWhiteSpace(prev) && char.IsWhiteSpace(next)) && !(char.IsLetterOrDigit(prev) && char.IsLetterOrDigit(next)),
                '~' => text.Count(x => x == '~') >= 2 || next == '~' || prev == '~',
                '^' => text.Count(x => x == '^') >= 2,
                '=' or '+' => next == c,
                '[' => inLink || OpensLink(text, i),
                ']' => inLink || escapedOpen.Count > 0,
                '<' => char.IsAsciiLetter(next) || next is '/' or '!' or '?',
                '&' => Entity().IsMatch(text, i),
                _ => false,
            };

            if (c == '[' && escape)
            {
                escapedOpen.Add(i);
            }

            if (escape)
            {
                output.Append('\\');
            }

            output.Append(c);
        }

        return output.ToString();
    }

    /// <summary>A [ needs escaping only when its ] is followed by ( or [ or :, which would make a link.</summary>
    private static bool OpensLink(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '[')
            {
                depth++;
            }
            else if (text[i] == ']' && --depth == 0)
            {
                return i + 1 < text.Length && text[i + 1] is '(' or '[' or ':';
            }
        }

        return false;
    }

    /// <summary>Text that starts like a heading, list item, quote, rule, or code fence gets a backslash so it stays text.</summary>
    private static string EscapeLineStart(string text, LineStart where)
    {
        if (text.StartsWith('\\'))
        {
            return text;
        }

        if (where != LineStart.Heading && LooksLikeBlock().IsMatch(text))
        {
            return "\\" + text;
        }

        if (where != LineStart.Heading && OrderedStart().Match(text) is { Success: true } ordered)
        {
            return ordered.Groups[1].Value + "\\" + text[ordered.Groups[1].Length..];
        }

        if (where != LineStart.Heading && RuleOrUnderline().IsMatch(text))
        {
            return "\\" + text;
        }

        return where == LineStart.Item && TaskStart().IsMatch(text) ? "\\" + text : text;
    }

    public static Regex SpacesPrefix => Spaces();

    [GeneratedRegex(@"^[ \t]*")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^ {0,3}#{1,6}(?=[ \t]|$)[ \t]*")]
    private static partial Regex HeadingPrefix();

    [GeneratedRegex(@"^[ \t]*(?:[-*+]|\d{1,9}[.)])(?:[ \t]+|$)")]
    private static partial Regex ItemPrefix();

    [GeneratedRegex(@"^[ \t]*[-*+][ \t]+\[[ xX]\](?:[ \t]+|$)")]
    private static partial Regex TaskPrefix();

    [GeneratedRegex(@"^[ \t]{0,3}>[ \t]?")]
    private static partial Regex QuotePrefix();

    [GeneratedRegex(@"^(?:#{1,6}(?:[ \t]|$)|[-+*](?:[ \t]|$)|>|```|~~~)")]
    private static partial Regex LooksLikeBlock();

    [GeneratedRegex(@"^(\d{1,9})[.)](?:[ \t]|$)")]
    private static partial Regex OrderedStart();

    [GeneratedRegex(@"^[ \t]*([-=*_])(?:[ \t]*\1)*[ \t]*$")]
    private static partial Regex RuleOrUnderline();

    [GeneratedRegex(@"^\[[ xX]\](?:[ \t]|$)")]
    private static partial Regex TaskStart();

    [GeneratedRegex(@"\G&(?:#[0-9]{1,7}|#[xX][0-9a-fA-F]{1,6}|[A-Za-z][A-Za-z0-9]{0,31});")]
    private static partial Regex Entity();

    [GeneratedRegex(@"^(?:https?://|www\.)[^\s<>\[\]()`*_~]*[^\s<>\[\]()`*_~.,:;!?'""]$")]
    private static partial Regex BareLink();
}
