using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Waypoint.Core.Notes;

namespace Waypoint.App;

public sealed partial class MarkdownBox
{
    /// <summary>What a copy from this editor carries besides its text, so pasting it back keeps every mark.</summary>
    internal const string MarkdownFormat = "Waypoint.Markdown";

    /// <summary>
    /// Pasting keeps bold, italic, headings, lists, and links from Word, Google Docs, and web pages, and drops
    /// colors, fonts, sizes, and images. Plain text is read as Markdown. Ctrl+Shift+V pastes it as plain text.
    /// </summary>
    private void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        e.CancelCommand();
        if (IsReadOnly)
        {
            return;
        }

        var data = e.SourceDataObject;
        Dispatcher.BeginInvoke(() => Paste(data, plain: false));
    }

    private void PastePlain()
    {
        try
        {
            if (Clipboard.GetDataObject() is { } data)
            {
                Paste(data, plain: true);
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The clipboard is busy with another program. Nothing is pasted.
        }
    }

    /// <summary>Pastes <paramref name="data"/> at the caret, over the selection. Used by paste, drop, and tests.</summary>
    internal void Paste(IDataObject data, bool plain)
    {
        if (IsReadOnly)
        {
            return;
        }

        if (!plain && PasteAsLink(Read(data, DataFormats.UnicodeText) ?? Read(data, DataFormats.Text)))
        {
            return;
        }

        var blocks = plain ? PlainBlocks(data) : RichBlocks(data);
        if (blocks is { Count: > 0 })
        {
            Insert(blocks);
        }
    }

    private static string? Read(IDataObject data, string format)
    {
        try
        {
            return data.GetDataPresent(format) ? data.GetData(format) as string : null;
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or OutOfMemoryException or InvalidOperationException)
        {
            return null;
        }
    }

    private static List<ParsedBlock>? PlainBlocks(IDataObject data)
    {
        if ((Read(data, DataFormats.UnicodeText) ?? Read(data, DataFormats.Text)) is not { } text)
        {
            return null;
        }

        return text.Replace("\r\n", "\n").Split('\n').Select(line => new ParsedBlock(LineInfo.Of(LineKind.Text), line.Length == 0 ? [] : [new Segment(line, default)])).ToList();
    }

    private static List<ParsedBlock>? RichBlocks(IDataObject data)
    {
        if (Read(data, MarkdownFormat) is { } markdown)
        {
            return WebLinksOnly(MarkdownLines.Parse(markdown));
        }

        if (Read(data, DataFormats.Html) is { } html && HtmlToMarkdown.FromClipboard(html) is { Length: > 0 } fromHtml)
        {
            return WebLinksOnly(MarkdownLines.Parse(fromHtml));
        }

        if (Read(data, DataFormats.Rtf) is { } rtf && FromRtf(rtf) is { Count: > 0 } fromRtf)
        {
            return fromRtf;
        }

        return (Read(data, DataFormats.UnicodeText) ?? Read(data, DataFormats.Text)) is { } text ? WebLinksOnly(MarkdownLines.Parse(text)) : null;
    }

    /// <summary>
    /// Pasted Markdown keeps only web and mail links. Any other link, such as javascript: or file:, becomes its text,
    /// and the line is written again without it.
    /// </summary>
    internal static List<ParsedBlock> WebLinksOnly(List<ParsedBlock> blocks) => WebLinksOnly(blocks, null, out _);

    /// <summary>The same, keeping the other links in <paramref name="kept"/>. <paramref name="dropped"/> says whether any went.</summary>
    internal static List<ParsedBlock> WebLinksOnly(List<ParsedBlock> blocks, IReadOnlySet<string>? kept, out bool dropped)
    {
        bool Drops(Segment segment) => segment.Style.Link is { } url && !IsSafeLink(url) && kept?.Contains(url) != true;
        dropped = false;
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            if (block.Segments.Any(Drops))
            {
                dropped = true;
                block.Info.Dirty = true;
                blocks[i] = block with
                {
                    Segments = block.Segments.Select(segment => Drops(segment) ? segment with { Style = segment.Style with { Link = null, Title = null } } : segment).ToList(),
                };
            }
        }

        return blocks;
    }

    /// <summary>The links that aren't web or mail addresses, such as file: links a note was written with elsewhere.</summary>
    internal static HashSet<string> OtherLinks(IEnumerable<ParsedBlock> blocks) =>
        blocks.SelectMany(block => block.Segments).Select(segment => segment.Style.Link).OfType<string>().Where(url => !IsSafeLink(url)).ToHashSet();

    /// <summary>
    /// Inserts lines at the caret. The first and last line join the text around the caret when they're plain text;
    /// the rest go in as lines of their own.
    /// </summary>
    internal void Insert(List<ParsedBlock> blocks) => Change(() =>
    {
        if (!Selection.IsEmpty)
        {
            Selection.Text = "";
            Tidy();
        }

        var caret = CaretPosition;
        if (TopOrNull(caret) is not { } top)
        {
            return;
        }

        if (TextPara(top) is not { } para)
        {
            var line = NewLine(LineInfo.Of(LineKind.Text));
            Document.Blocks.InsertAfter(top, line);
            top = line;
            para = line;
            caret = line.ContentStart;
        }

        var info = Info(top);
        if (info.Kind == LineKind.Code)
        {
            var offset = CharOffset(para, caret);
            var code = string.Join("\n", blocks.Select(block => block.Info.Kind == LineKind.Code ? block.Text : string.Concat(block.Segments.Select(segment => segment.Text))));
            InsertCode(para, caret, code);
            Touch(top);
            CaretPosition = PointerAt(para, offset + code.Length);
            return;
        }

        if (info.Kind is LineKind.Item or LineKind.Quote && blocks.Count > 1 && blocks.All(block => block.Info.Kind == LineKind.Text))
        {
            // Plain lines pasted into a list item or a quote each become an item or a quote line, as in a word
            // processor. The first and last join the line the caret was in, as they do anywhere else.
            blocks = blocks.Where(block => block.Segments.Count > 0)
                .Select(block => block with { Info = info.Kind == LineKind.Quote ? LineInfo.Of(LineKind.Quote) : info.Next() })
                .ToList();
            if (blocks.Count == 0)
            {
                return;
            }

            blocks[0] = blocks[0] with { Info = LineInfo.Of(LineKind.Text) };
            blocks[^1] = blocks[^1] with { Info = LineInfo.Of(LineKind.Text) };
        }

        if (blocks.Count == 1 && blocks[0].Info.Kind == LineKind.Text)
        {
            var at = CharOffset(para, caret);
            var length = InsertSegments(para, caret, blocks[0].Segments);
            Touch(top);
            CaretPosition = PointerAt(para, at + length);
            return;
        }

        // Split the line at the caret, and put the lines in between.
        Unthemed(para);
        var tailStart = (caret.IsAtInsertionPosition ? caret : caret.GetInsertionPosition(LogicalDirection.Forward)).InsertParagraphBreak();
        var tail = tailStart.Paragraph!;
        tail.Tag = info.Kind == LineKind.Item ? info.Next() : info.Kind is LineKind.Quote or LineKind.More ? LineInfo.Of(info.Kind, info.Level) : LineInfo.Of(LineKind.Text);
        if (top is List)
        {
            Canonical(top, out _);
        }
        else
        {
            Restyle(tail);
        }

        var headTop = TopOf(para.ContentStart);
        var tailTop = TopOf(tail.ContentStart);
        Touch(headTop);
        Touch(tailTop);
        var from = 0;
        var to = blocks.Count;
        if (blocks[0].Info.Kind == LineKind.Text)
        {
            InsertSegments(para, para.ContentEnd, blocks[0].Segments);
            from = 1;
        }

        var tailOffset = 0;
        if (to > from && blocks[^1].Info.Kind == LineKind.Text)
        {
            tailOffset = InsertSegments(tail, tail.ContentStart, blocks[^1].Segments);
            to--;
        }

        Block anchor = headTop;
        Block? lastMade = null;
        foreach (var block in blocks.Skip(from).Take(to - from))
        {
            var made = Build(block);
            Document.Blocks.InsertAfter(anchor, made);
            _touched.Add(made);
            anchor = made;
            lastMade = made;
        }

        if (from == 0 && VisibleLength(para) == 0 && Info(headTop).Kind == LineKind.Text && headTop.NextBlock is not null)
        {
            Document.Blocks.Remove(headTop);
        }

        var tailUsed = to < blocks.Count;
        if (!tailUsed && VisibleLength(tail) == 0 && Info(tailTop).Kind == LineKind.Text && tailTop.NextBlock is not null && lastMade is not null && TextPara(lastMade) is not null)
        {
            Document.Blocks.Remove(tailTop);
            CaretPosition = TextPara(lastMade)!.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
            return;
        }

        CaretPosition = tailUsed || lastMade is null || TextPara(lastMade) is null
            ? PointerAt(tail, tailOffset)
            : TextPara(lastMade)!.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
    });

    private static void InsertCode(Paragraph para, TextPointer at, string code)
    {
        var position = at;
        var first = true;
        foreach (var line in code.Split('\n'))
        {
            if (!first)
            {
                position = position.InsertLineBreak();
            }

            if (line.Length > 0)
            {
                var offset = CharOffset(para, position);
                position.InsertTextInRun(line);
                position = PointerAt(para, offset + line.Length);
            }

            first = false;
        }
    }

    /// <summary>Inserts formatted text into a line and returns how many characters went in.</summary>
    private static int InsertSegments(Paragraph para, TextPointer at, IReadOnlyList<Segment> segments)
    {
        var text = string.Concat(segments.Select(segment => segment.Text));
        if (text.Length == 0)
        {
            return 0;
        }

        var offset = CharOffset(para, at);
        var position = at.IsAtInsertionPosition ? at : at.GetInsertionPosition(LogicalDirection.Forward);
        position.InsertTextInRun(text);
        var range = new TextRange(PointerAt(para, offset), PointerAt(para, offset + text.Length));
        range.ApplyPropertyValue(Inline.BaselineAlignmentProperty, BaselineAlignment.Top);
        foreach (var run in AllRuns(para.Inlines).Where(run => run.ContentStart.CompareTo(range.Start) >= 0 && run.ContentEnd.CompareTo(range.End) <= 0).ToList())
        {
            run.ClearValue(TextElement.FontWeightProperty);
            run.ClearValue(TextElement.FontStyleProperty);
            run.ClearValue(Inline.TextDecorationsProperty);
            run.ClearValue(TextElement.FontFamilyProperty);
            run.ClearValue(TextElement.FontSizeProperty);
            run.ClearValue(TextElement.BackgroundProperty);
            run.ClearValue(TextElement.ForegroundProperty);
        }

        var at2 = offset;
        var links = new List<(int Start, int End, string Url, string? Title)>();
        foreach (var segment in segments)
        {
            var start = at2;
            at2 += segment.Text.Length;
            if (segment.Style.HasEmphasis || segment.Style.Code)
            {
                var part = new TextRange(PointerAt(para, start), PointerAt(para, at2));
                part.ApplyPropertyValue(Inline.BaselineAlignmentProperty, BaselineAlignment.Bottom);
                foreach (var run in AllRuns(para.Inlines).Where(run => run.ContentStart.CompareTo(part.Start) >= 0 && run.ContentEnd.CompareTo(part.End) <= 0).ToList())
                {
                    ApplyStyle(run, segment.Style with { Link = null });
                    run.ClearValue(Inline.BaselineAlignmentProperty);
                }
            }

            if (segment.Style.Link is { } url)
            {
                if (links.Count > 0 && links[^1].End == start && links[^1].Url == url)
                {
                    links[^1] = links[^1] with { End = at2 };
                }
                else
                {
                    links.Add((start, at2, url, segment.Style.Title));
                }
            }
        }

        foreach (var run in AllRuns(para.Inlines).Where(run => run.ContentStart.CompareTo(range.Start) >= 0 && run.ContentEnd.CompareTo(range.End) <= 0).ToList())
        {
            run.ClearValue(Inline.BaselineAlignmentProperty);
        }

        foreach (var (start, end, url, title) in links)
        {
            try
            {
                var link = new Hyperlink(PointerAt(para, start), PointerAt(para, end)) { Tag = url, ToolTip = $"{title ?? url}\nCtrl+click to open" };
                link.SetResourceReference(TextElement.ForegroundProperty, "Accent");
                if (title is not null)
                {
                    link.SetValue(TitleProperty, title);
                }
            }
            catch (InvalidOperationException)
            {
                // Inside another link, so the text stays without a link of its own.
            }
        }

        return text.Length;
    }

    /// <summary>
    /// RTF, as WordPad and some programs copy, read through WPF's own reader: bold, italic, underline,
    /// strikethrough, links, and lists stay. Fonts, colors, sizes, and pictures don't.
    /// </summary>
    internal static List<ParsedBlock> FromRtf(string rtf)
    {
        var document = new FlowDocument();
        try
        {
            using var stream = new MemoryStream(Encoding.Latin1.GetBytes(rtf));
            new TextRange(document.ContentStart, document.ContentEnd).Load(stream, DataFormats.Rtf);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException or System.Windows.Markup.XamlParseException)
        {
            return [];
        }

        var blocks = new List<ParsedBlock>();
        FromFlow(document.Blocks, -1, blocks);
        while (blocks.Count > 0 && blocks[^1].Info.Kind == LineKind.Text && blocks[^1].Segments.Count == 0)
        {
            blocks.RemoveAt(blocks.Count - 1);
        }

        return blocks;
    }

    private static void FromFlow(IEnumerable<Block> source, int level, List<ParsedBlock> into)
    {
        foreach (var block in source)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    into.Add(new ParsedBlock(LineInfo.Of(level < 0 ? LineKind.Text : LineKind.More, Math.Max(0, level)), SafeLinks(Segments(paragraph))));
                    break;
                case List list:
                    var numbered = list.MarkerStyle is TextMarkerStyle.Decimal or TextMarkerStyle.LowerLatin or TextMarkerStyle.UpperLatin or TextMarkerStyle.LowerRoman or TextMarkerStyle.UpperRoman;
                    var number = Math.Max(1, list.StartIndex);
                    foreach (var item in list.ListItems)
                    {
                        var first = true;
                        foreach (var child in item.Blocks)
                        {
                            if (first && child is Paragraph lead)
                            {
                                var info = LineInfo.Of(LineKind.Item, level + 1);
                                info.Type = numbered ? ItemType.Number : ItemType.Bullet;
                                info.Number = number++;
                                into.Add(new ParsedBlock(info, SafeLinks(Segments(lead))));
                            }
                            else
                            {
                                FromFlow([child], level + 1, into);
                            }

                            first = false;
                        }
                    }

                    break;
                case Section section:
                    FromFlow(section.Blocks, level, into);
                    break;
                case Table table:
                    foreach (var row in table.RowGroups.SelectMany(group => group.Rows))
                    {
                        var cells = row.Cells.Select(cell => string.Join(" ", cell.Blocks.OfType<Paragraph>().Select(p => new TextRange(p.ContentStart, p.ContentEnd).Text.Trim())));
                        into.Add(new ParsedBlock(LineInfo.Of(LineKind.Text), [new Segment(string.Join(" | ", cells), default)]));
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Only web and mail links are kept from pasted text. Others stay as their text. A link's underline comes from
    /// the program it was copied from, so it isn't kept as underline.
    /// </summary>
    private static List<Segment> SafeLinks(List<Segment> segments) =>
        segments.Select(segment => segment.Style.Link is { } url
            ? IsSafeLink(url) ? segment with { Style = segment.Style with { Underline = false } } : segment with { Style = segment.Style with { Link = null, Title = null } }
            : segment).ToList();

    /// <summary>A copy carries the selection's Markdown too, so pasting it here keeps everything.</summary>
    private void OnCopying(object sender, DataObjectCopyingEventArgs e)
    {
        try
        {
            if (SelectionMarkdown() is { Length: > 0 } markdown)
            {
                e.DataObject.SetData(MarkdownFormat, markdown);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // The plain copy still goes through.
        }
    }

    internal string SelectionMarkdown()
    {
        var start = Selection.Start;
        var end = Selection.End;
        if (TopOrNull(start) is not { } first || TopOrNull(end) is not { } last)
        {
            return "";
        }

        if (first == last && TextPara(first) is { } only && Info(first).Kind != LineKind.Code)
        {
            return MarkdownLines.WriteInlines(Segments(only, start, end), Info(first).Verbatim, MarkdownLines.LineStart.Text);
        }

        var lines = new List<string>();
        Block? previous = null;
        string? previousMarkdown = null;
        for (var block = first; block is not null; block = block.NextBlock)
        {
            var markdown = MarkdownOf(block);
            if ((block == first || block == last) && TextPara(block) is { } para && Info(block).Kind != LineKind.Code)
            {
                var info = Info(block);
                markdown = MarkdownLines.Write(info, Segments(para, block == first ? start : null, block == last ? end : null), "", level => ParentItem(block, level));
            }

            if (previous is not null && Blank(previous, previousMarkdown!, block, markdown))
            {
                lines.Add("");
            }

            lines.Add(markdown);
            previous = block;
            previousMarkdown = markdown;
            if (block == last)
            {
                break;
            }
        }

        return string.Join("\n", lines);
    }
}
