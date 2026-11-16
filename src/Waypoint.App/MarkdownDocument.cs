using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;
using WpfTable = System.Windows.Documents.Table;
using WpfTableCell = System.Windows.Documents.TableCell;
using WpfTableRow = System.Windows.Documents.TableRow;

namespace Waypoint.App;

/// <summary>
/// Turns a note's Markdown into a FlowDocument. Markdig does the parsing; this only walks its tree.
/// Every color is a resource reference to the theme's brushes, so a theme change recolors an open note.
/// Raw HTML is shown as text, and links only open when they're web or mail links. Every line break is kept.
/// </summary>
internal static class MarkdownDocument
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .DisableHtml()
        .Build();

    private static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas, Courier New");

    /// <summary>Keeps both task boxes in one font, so checked and unchecked are the same size.</summary>
    private static readonly FontFamily SymbolFont = new("Segoe UI Symbol");

    public static FlowDocument Render(string markdown)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0, 0, 8, 0),
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 14,
            LineHeight = 22,
            TextAlignment = TextAlignment.Left,
        };
        document.SetResourceReference(FlowDocument.ForegroundProperty, "PrimaryText");
        document.Background = Brushes.Transparent;

        foreach (var block in Markdown.Parse(markdown ?? "", Pipeline))
        {
            if (Convert(block) is { } converted)
            {
                document.Blocks.Add(converted);
            }
        }

        if (document.Blocks.Count > 0)
        {
            document.Blocks.FirstBlock.Margin = document.Blocks.FirstBlock.Margin with { Top = 0 };
        }

        return document;
    }

    private static WpfBlock? Convert(Markdig.Syntax.Block block) => block switch
    {
        HeadingBlock heading => Heading(heading),
        ParagraphBlock paragraph => Paragraph(paragraph.Inline),
        ListBlock list => List(list),
        QuoteBlock quote => Quote(quote),
        Markdig.Extensions.Tables.Table table => Table(table),
        CodeBlock code => Code(code),
        ThematicBreakBlock => Rule(),
        ContainerBlock container => Section(container),
        LeafBlock leaf => Plain(leaf.Lines.ToString()),
        _ => null,
    };

    private static Paragraph Heading(HeadingBlock heading)
    {
        var paragraph = Paragraph(heading.Inline);
        paragraph.FontSize = heading.Level switch
        {
            1 => 24,
            2 => 20,
            3 => 17,
            _ => 15,
        };
        paragraph.FontWeight = FontWeights.SemiBold;
        paragraph.LineHeight = paragraph.FontSize * 1.3;
        paragraph.Margin = new Thickness(0, heading.Level <= 2 ? 18 : 14, 0, 6);
        return paragraph;
    }

    private static Paragraph Paragraph(ContainerInline? inlines)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
        AddInlines(paragraph.Inlines, inlines);
        return paragraph;
    }

    private static Paragraph Plain(string text) => new(new Run(text)) { Margin = new Thickness(0, 0, 0, 10) };

    private static List List(ListBlock list)
    {
        var shown = new List
        {
            MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(22, 0, 0, 0),
        };

        if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start) && start > 0)
        {
            shown.StartIndex = start;
        }

        foreach (var item in list.OfType<ListItemBlock>())
        {
            var listItem = new ListItem();
            foreach (var child in item)
            {
                if (Convert(child) is { } converted)
                {
                    converted.Margin = converted.Margin with { Bottom = 2 };
                    listItem.Blocks.Add(converted);
                }
            }

            // A task list item shows its box in place of the bullet.
            if (item.FirstOrDefault() is ParagraphBlock { Inline.FirstChild: TaskList })
            {
                shown.MarkerStyle = TextMarkerStyle.None;
            }

            shown.ListItems.Add(listItem);
        }

        return shown;
    }

    private static WpfBlock Quote(QuoteBlock quote)
    {
        var section = Section(quote);
        section.Padding = new Thickness(12, 2, 0, 2);
        section.Margin = new Thickness(0, 0, 0, 10);
        section.BorderThickness = new Thickness(3, 0, 0, 0);
        section.SetResourceReference(WpfBlock.BorderBrushProperty, "Rule");
        section.SetResourceReference(TextElement.ForegroundProperty, "SecondaryText");
        return IsAnswer(quote) ? HiddenAnswer(section) : section;
    }

    /// <summary>Practice answers are saved as "&gt; **Answer:** ..." quotes, and stay hidden until asked for.</summary>
    private static bool IsAnswer(QuoteBlock quote) =>
        quote.FirstOrDefault() is ParagraphBlock { Inline.FirstChild: EmphasisInline { FirstChild: LiteralInline literal } }
        && literal.Content.ToString().StartsWith("Answer", StringComparison.OrdinalIgnoreCase);

    private static Paragraph HiddenAnswer(Section answer)
    {
        var show = new Hyperlink(new Run("Show answer"));
        show.SetResourceReference(TextElement.ForegroundProperty, "Accent");
        var paragraph = new Paragraph(show) { Margin = new Thickness(0, 0, 0, 10) };
        show.Click += (_, _) =>
        {
            paragraph.SiblingBlocks.InsertAfter(paragraph, answer);
            paragraph.SiblingBlocks.Remove(paragraph);
        };
        return paragraph;
    }

    private static Section Section(ContainerBlock container)
    {
        var section = new Section();
        foreach (var child in container)
        {
            if (Convert(child) is { } converted)
            {
                section.Blocks.Add(converted);
            }
        }

        return section;
    }

    private static Paragraph Code(CodeBlock code)
    {
        var paragraph = new Paragraph(new Run(code.Lines.ToString().TrimEnd('\r', '\n')))
        {
            FontFamily = CodeFont,
            FontSize = 13,
            LineHeight = 19,
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 10),
        };
        paragraph.SetResourceReference(TextElement.BackgroundProperty, "Surface");
        return paragraph;
    }

    private static Paragraph Rule()
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 4, 0, 14), BorderThickness = new Thickness(0, 0, 0, 1), FontSize = 2, LineHeight = 2 };
        paragraph.SetResourceReference(WpfBlock.BorderBrushProperty, "Rule");
        return paragraph;
    }

    private static WpfTable Table(Markdig.Extensions.Tables.Table table)
    {
        var shown = new WpfTable { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 10), BorderThickness = new Thickness(1, 1, 0, 0) };
        shown.SetResourceReference(WpfBlock.BorderBrushProperty, "Rule");
        var columns = table.OfType<Markdig.Extensions.Tables.TableRow>().Select(row => row.Count).DefaultIfEmpty(0).Max();
        for (var i = 0; i < columns; i++)
        {
            shown.Columns.Add(new TableColumn());
        }

        var group = new TableRowGroup();
        foreach (var row in table.OfType<Markdig.Extensions.Tables.TableRow>())
        {
            var shownRow = new WpfTableRow();
            if (row.IsHeader)
            {
                shownRow.FontWeight = FontWeights.SemiBold;
                shownRow.SetResourceReference(TextElement.BackgroundProperty, "Surface");
            }

            foreach (var cell in row.OfType<Markdig.Extensions.Tables.TableCell>())
            {
                var shownCell = new WpfTableCell { Padding = new Thickness(8, 4, 8, 4), BorderThickness = new Thickness(0, 0, 1, 1) };
                shownCell.SetResourceReference(WpfTableCell.BorderBrushProperty, "Rule");
                foreach (var child in cell)
                {
                    if (Convert(child) is { } converted)
                    {
                        converted.Margin = new Thickness(0);
                        shownCell.Blocks.Add(converted);
                    }
                }

                shownRow.Cells.Add(shownCell);
            }

            group.Rows.Add(shownRow);
        }

        shown.RowGroups.Add(group);
        return shown;
    }

    private static void AddInlines(InlineCollection target, ContainerInline? container)
    {
        for (var inline = container?.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            if (Convert(inline) is { } converted)
            {
                target.Add(converted);
            }
        }
    }

    private static WpfInline? Convert(Markdig.Syntax.Inlines.Inline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                return new Run(literal.Content.ToString());
            // A line typed on its own stays on its own line, as in most note apps, rather than joining the one above.
            case LineBreakInline:
                return new LineBreak();
            case CodeInline code:
                var run = new Run(code.Content) { FontFamily = CodeFont, FontSize = 13 };
                run.SetResourceReference(TextElement.BackgroundProperty, "Surface");
                return run;
            case TaskList task:
                return new Run(task.Checked ? "☑  " : "☐  ") { FontFamily = SymbolFont };
            case EmphasisInline emphasis:
                return Emphasis(emphasis);
            case LinkInline { IsImage: true } image:
                return new Run(image.FirstChild is null ? image.Url ?? "" : InlineText(image));
            case LinkInline link:
                return Link(link.Url, link);
            case AutolinkInline autolink:
                return Link(autolink.IsEmail ? $"mailto:{autolink.Url}" : autolink.Url, null, autolink.Url);
            case HtmlEntityInline entity:
                return new Run(entity.Transcoded.ToString());
            case HtmlInline html:
                return new Run(html.Tag);
            case ContainerInline container:
                var span = new Span();
                AddInlines(span.Inlines, container);
                return span;
            default:
                return null;
        }
    }

    private static Span Emphasis(EmphasisInline emphasis)
    {
        Span span = emphasis.DelimiterChar switch
        {
            '*' or '_' when emphasis.DelimiterCount >= 2 => new Bold(),
            '*' or '_' => new Italic(),
            _ => new Span(),
        };

        if (emphasis.DelimiterChar == '~' && emphasis.DelimiterCount >= 2)
        {
            span.TextDecorations = TextDecorations.Strikethrough;
        }
        else if (emphasis.DelimiterChar == '=')
        {
            span.SetResourceReference(TextElement.BackgroundProperty, "SurfaceHover");
        }
        else if (emphasis.DelimiterChar == '+')
        {
            span.TextDecorations = TextDecorations.Underline;
        }
        else if (emphasis.DelimiterChar == '^')
        {
            span.BaselineAlignment = BaselineAlignment.Superscript;
        }
        else if (emphasis.DelimiterChar == '~')
        {
            span.BaselineAlignment = BaselineAlignment.Subscript;
        }

        AddInlines(span.Inlines, emphasis);
        return span;
    }

    /// <summary>A link that isn't safe to open stays as plain text, so a note can't launch a program.</summary>
    private static WpfInline Link(string? url, ContainerInline? label, string? text = null)
    {
        var target = Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https" or "mailto" ? parsed : null;
        var span = target is null ? new Span() : new Hyperlink { NavigateUri = target, ToolTip = target.ToString() };
        if (label?.FirstChild is not null)
        {
            AddInlines(span.Inlines, label);
        }
        else
        {
            span.Inlines.Add(new Run(text ?? url ?? ""));
        }

        if (span is Hyperlink hyperlink)
        {
            hyperlink.SetResourceReference(TextElement.ForegroundProperty, "Accent");
            hyperlink.Click += (_, _) => OpenLink(target!);
        }

        return span;
    }

    private static void OpenLink(Uri target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No browser or mail app is set up. The link text is still there to copy.
        }
    }

    private static string InlineText(ContainerInline container)
    {
        var text = new System.Text.StringBuilder();
        for (var inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            text.Append(inline switch
            {
                LiteralInline literal => literal.Content.ToString(),
                CodeInline code => code.Content,
                ContainerInline inner => InlineText(inner),
                _ => "",
            });
        }

        return text.ToString();
    }
}
