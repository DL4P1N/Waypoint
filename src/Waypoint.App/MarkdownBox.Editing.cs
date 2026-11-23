using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Waypoint.Core.Capture;

namespace Waypoint.App;

/// <summary>The formatting the note editor's toolbar shows for the caret or selection.</summary>
public sealed record NoteFormat(bool Bold, bool Italic, bool Underline, bool Strike, bool Code, bool Link, int Heading, string List, bool Quote, bool Editable);

/// <summary>Commands for the note editor and its toolbar, beyond the ones WPF already has for bold, italic, and lists.</summary>
public static class NoteCommands
{
    public static readonly RoutedUICommand Strikethrough = new("Strikethrough", nameof(Strikethrough), typeof(NoteCommands));
    public static readonly RoutedUICommand Code = new("Code", nameof(Code), typeof(NoteCommands));
    public static readonly RoutedUICommand Checklist = new("Checklist", nameof(Checklist), typeof(NoteCommands));
    public static readonly RoutedUICommand Quote = new("Quote", nameof(Quote), typeof(NoteCommands));
    public static readonly RoutedUICommand Link = new("Link", nameof(Link), typeof(NoteCommands));

    /// <summary>The parameter is 0 for normal text, or 1 to 3 for a heading.</summary>
    public static readonly RoutedUICommand Heading = new("Heading", nameof(Heading), typeof(NoteCommands));
    public static readonly RoutedUICommand PastePlain = new("Paste as plain text", nameof(PastePlain), typeof(NoteCommands));
    public static readonly RoutedUICommand ClearFormatting = new("Clear formatting", nameof(ClearFormatting), typeof(NoteCommands));
}

public sealed partial class MarkdownBox
{
    /// <summary>Opens a link's target. Tests replace it, so nothing is ever opened by a test.</summary>
    internal static Action<string> OpenTarget = target =>
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Nothing is set up to open it. The link is still there to copy.
        }
    };

    /// <summary>
    /// Asks for a link's address, given the current one. Returns the new address, "" to remove the link, or null to
    /// leave it as it was. Tests set it; otherwise a small form asks.
    /// </summary>
    public Func<string, string?>? AskForLink { get; set; }

    private void AddCommands()
    {
        void Bind(ICommand command, Action run, Func<bool>? can = null) =>
            CommandBindings.Add(new CommandBinding(command, (_, e) => { run(); e.Handled = true; }, (_, e) => { e.CanExecute = can?.Invoke() ?? !IsReadOnly; e.Handled = true; }));

        Bind(ApplicationCommands.Undo, () => UndoStep(_undo, _redo, undo: true), () => _undo.Count > 0 && !IsReadOnly);
        Bind(ApplicationCommands.Redo, () => UndoStep(_redo, _undo, undo: false), () => _redo.Count > 0 && !IsReadOnly);
        Bind(EditingCommands.ToggleBold, () => ToggleInline(InlineMark.Bold));
        Bind(EditingCommands.ToggleItalic, () => ToggleInline(InlineMark.Italic));
        Bind(EditingCommands.ToggleUnderline, () => ToggleInline(InlineMark.Underline));
        Bind(NoteCommands.Strikethrough, () => ToggleInline(InlineMark.Strike));
        Bind(NoteCommands.Code, () => ToggleInline(InlineMark.Code));
        Bind(EditingCommands.ToggleBullets, () => ToggleList(ItemType.Bullet));
        Bind(EditingCommands.ToggleNumbering, () => ToggleList(ItemType.Number));
        Bind(NoteCommands.Checklist, () => ToggleList(ItemType.Task));
        Bind(NoteCommands.Quote, ToggleQuote);
        Bind(NoteCommands.Link, EditLink);
        Bind(NoteCommands.PastePlain, PastePlain);
        Bind(NoteCommands.ClearFormatting, ClearFormatting);
        Bind(EditingCommands.IncreaseIndentation, () => Indent(1));
        Bind(EditingCommands.DecreaseIndentation, () => Indent(-1));
        CommandBindings.Add(new CommandBinding(NoteCommands.Heading, (_, e) =>
        {
            SetHeading(e.Parameter switch
            {
                int level => level,
                string text when int.TryParse(text, out var level) => level,
                _ => 0,
            });
            e.Handled = true;
        }, (_, e) => e.CanExecute = !IsReadOnly));

        // Fonts, sizes, and alignment can't be saved in Markdown, so those commands do nothing here.
        foreach (var command in new[]
        {
            EditingCommands.AlignLeft, EditingCommands.AlignCenter, EditingCommands.AlignRight, EditingCommands.AlignJustify,
            EditingCommands.IncreaseFontSize, EditingCommands.DecreaseFontSize, EditingCommands.ToggleSubscript, EditingCommands.ToggleSuperscript,
        })
        {
            CommandBindings.Add(new CommandBinding(command, (_, _) => { }, (_, e) => e.CanExecute = false));
        }
    }

    // What's on at the caret

    public NoteFormat CurrentFormat()
    {
        var weight = Selection.GetPropertyValue(TextElement.FontWeightProperty);
        var style = Selection.GetPropertyValue(TextElement.FontStyleProperty);
        var family = Selection.GetPropertyValue(TextElement.FontFamilyProperty) as FontFamily;
        var underline = false;
        var strike = false;
        if (Selection.GetPropertyValue(Inline.TextDecorationsProperty) is TextDecorationCollection decorations)
        {
            underline = decorations.Any(decoration => decoration.Location == TextDecorationLocation.Underline);
            strike = decorations.Any(decoration => decoration.Location == TextDecorationLocation.Strikethrough);
        }

        var top = TopOrNull(Selection.Start);
        var info = top is null ? LineInfo.Of(LineKind.Text) : Info(top);
        var link = LinkAt(Selection.Start) is not null;
        if (link)
        {
            // A link draws its own underline, so only one set on the text counts.
            underline = Selection.Start.Parent is Run run && TextPara(top!) is { } para && StyleOf(run, para).Underline;
        }

        var code = IsCode(family) && info.Kind != LineKind.Code;
        return new NoteFormat(
            Bold: weight is FontWeight bold && bold.ToOpenTypeWeight() >= 700 && !code,
            Italic: style is FontStyle italic && italic != FontStyles.Normal,
            Underline: underline,
            Strike: strike,
            Code: code,
            Link: link,
            Heading: info.Kind == LineKind.Heading ? info.Level : 0,
            List: info.Kind == LineKind.Item ? info.Type.ToString() : "",
            Quote: info.Kind == LineKind.Quote,
            Editable: !IsReadOnly && info.IsTextLike);
    }

    private static Hyperlink? LinkAt(TextPointer pointer)
    {
        for (DependencyObject? element = pointer.Parent; element is TextElement text; element = LogicalTreeHelper.GetParent(text))
        {
            if (text is Hyperlink link)
            {
                return link;
            }
        }

        return null;
    }

    /// <summary>The blocks the selection covers. One that's only touched at its very start isn't counted.</summary>
    private List<Block> SelectedTops()
    {
        var first = TopOrNull(Selection.Start);
        var last = TopOrNull(Selection.End);
        if (first is null || last is null)
        {
            return [];
        }

        if (!Selection.IsEmpty && last != first && TextPara(last) is { } lastPara && CharOffset(lastPara, Selection.End) == 0)
        {
            last = last.PreviousBlock ?? last;
        }

        var tops = new List<Block>();
        for (var block = first; block is not null; block = block.NextBlock)
        {
            tops.Add(block);
            if (block == last)
            {
                break;
            }
        }

        return tops;
    }

    // Keys

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (HandleKey(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers))
        {
            e.Handled = true;
        }
    }

    /// <summary>The editor's own keys. True when the key was used, so WPF's own handling is skipped.</summary>
    internal bool HandleKey(Key key, ModifierKeys modifiers)
    {
        var e = new KeyResult();
        if (IsReadOnly)
        {
            return false;
        }

        HandleKey(key, modifiers, e);
        return e.Handled;
    }

    private sealed class KeyResult
    {
        public bool Handled;
    }

    private void HandleKey(Key key, ModifierKeys modifiers, KeyResult e)
    {
        switch (key)
        {
            case Key.Enter when modifiers is ModifierKeys.None or ModifierKeys.Shift:
                e.Handled = true;
                Enter(modifiers == ModifierKeys.Shift);
                return;
            case Key.Enter when modifiers == ModifierKeys.Control:
                if (TopOrNull(CaretPosition) is { } task && Info(task) is { Kind: LineKind.Item, Type: ItemType.Task })
                {
                    e.Handled = true;
                    ToggleCheck(task);
                }

                return;
            case Key.Back when modifiers == ModifierKeys.None:
                e.Handled = Backspace();
                return;
            case Key.Delete when modifiers == ModifierKeys.None:
                e.Handled = DeleteForward();
                return;
            case Key.Space when modifiers is ModifierKeys.None or ModifierKeys.Shift:
                // WPF types a space as a key command (EditingCommands.Space on KeyDown), so a typed space never
                // reaches PreviewTextInput. It's offered to the Markdown typing here instead.
                e.Handled = HandleTextInput(" ");
                return;
            case Key.Tab when modifiers is ModifierKeys.None or ModifierKeys.Shift:
                if (SelectedTops().Any(block => Info(block).Kind is LineKind.Item))
                {
                    e.Handled = true;
                    Indent(modifiers == ModifierKeys.Shift ? -1 : 1);
                }
                else if (modifiers == ModifierKeys.Shift)
                {
                    e.Handled = true;
                }

                return;
        }

        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            ItemType? list = key switch
            {
                Key.D7 or Key.NumPad7 => ItemType.Number,
                Key.D8 or Key.NumPad8 => ItemType.Bullet,
                Key.D9 or Key.NumPad9 => ItemType.Task,
                _ => null,
            };

            if (list is { } type)
            {
                e.Handled = true;
                ToggleList(type);
            }
            else if (key == Key.V)
            {
                e.Handled = true;
                PastePlain();
            }
            else if (key == Key.Z)
            {
                e.Handled = true;
                UndoStep(_redo, _undo, undo: false);
            }
            else if (key == Key.X)
            {
                e.Handled = true;
                ToggleInline(InlineMark.Strike);
            }

            return;
        }

        if (modifiers == (ModifierKeys.Control | ModifierKeys.Alt) && key is >= Key.D0 and <= Key.D3 or >= Key.NumPad0 and <= Key.NumPad3)
        {
            e.Handled = true;
            SetHeading(key >= Key.NumPad0 ? key - Key.NumPad0 : key - Key.D0);
            return;
        }

        if (modifiers == ModifierKeys.Control && key == Key.K)
        {
            e.Handled = true;
            EditLink();
        }
        else if (modifiers == ModifierKeys.Control && key is Key.Space or Key.Oem5)
        {
            // Ctrl+Space as in Word, Ctrl+\ as in Google Docs.
            e.Handled = true;
            ClearFormatting();
        }
        else if (modifiers == ModifierKeys.Control && key is Key.Oem6 or Key.Oem4)
        {
            // Ctrl+] and Ctrl+[ indent and outdent, as in Google Docs. They'd change the font size, which isn't kept.
            e.Handled = true;
            Indent(key == Key.Oem6 ? 1 : -1);
        }
        else if (modifiers == (ModifierKeys.Alt | ModifierKeys.Shift) && key == Key.D5)
        {
            e.Handled = true;
            ToggleInline(InlineMark.Strike);
        }
    }

    /// <summary>
    /// Enter continues a list, checklist, or quote, and on an empty item ends it (or steps out one level). After a
    /// heading the next line is normal text. In a code block it's a new line of code; Enter on an empty last line leaves it.
    /// </summary>
    private void Enter(bool shift) => Change(() =>
    {
        if (!Selection.IsEmpty)
        {
            Selection.Text = "";
        }

        var caret = CaretPosition;
        if (TopOrNull(caret) is not { } top)
        {
            return;
        }

        var info = Info(top);
        if (TextPara(top) is not { } para)
        {
            // A table or other kept block: Enter before it makes a line above, as in Word, and after it a line below.
            var line = NewLine(LineInfo.Of(LineKind.Text));
            if (caret.CompareTo(top.ContentEnd) < 0)
            {
                Document.Blocks.InsertBefore(top, line);
            }
            else
            {
                Document.Blocks.InsertAfter(top, line);
            }

            CaretPosition = line.ContentStart;
            return;
        }

        if (info.Kind == LineKind.Code)
        {
            var text = CodeText(para);
            if (!shift && CharOffset(para, caret) == text.Length && text.EndsWith('\n') && para.Inlines.OfType<LineBreak>().LastOrDefault() is { } lastBreak)
            {
                para.Inlines.Remove(lastBreak);
                foreach (var empty in para.Inlines.OfType<Run>().Where(run => run.Text.Length == 0 && run.NextInline is null).ToList())
                {
                    para.Inlines.Remove(empty);
                }

                Touch(top);
                var after = NewLine(LineInfo.Of(LineKind.Text));
                Document.Blocks.InsertAfter(top, after);
                CaretPosition = after.ContentStart;
                return;
            }

            CaretPosition = caret.InsertLineBreak();
            return;
        }

        if (!shift && VisibleLength(para) == 0 && info.Kind is LineKind.Item or LineKind.Quote or LineKind.More)
        {
            Keep(() =>
            {
                if (info is { Kind: LineKind.Item, Level: > 0 })
                {
                    Convert(top, line => line.Level--);
                }
                else
                {
                    Convert(top, line =>
                    {
                        line.Kind = LineKind.Text;
                        line.Level = 0;
                    });
                }
            });
            return;
        }

        var offset = CharOffset(para, caret);
        var length = VisibleLength(para);
        var next = info.Kind switch
        {
            LineKind.Item when shift => LineInfo.Of(LineKind.More, info.Level),
            LineKind.Item => info.Next(),
            LineKind.More => LineInfo.Of(LineKind.More, info.Level),
            LineKind.Heading when offset >= length => LineInfo.Of(LineKind.Text),
            LineKind.Heading => LineInfo.Of(LineKind.Heading, info.Level),
            LineKind.Quote => LineInfo.Of(LineKind.Quote),
            _ => LineInfo.Of(LineKind.Text),
        };

        Unthemed(para);
        var start = (caret.IsAtInsertionPosition ? caret : caret.GetInsertionPosition(LogicalDirection.Forward)).InsertParagraphBreak();
        var made = start.Paragraph ?? throw new InvalidOperationException("The line didn't split.");

        // Enter at the start of a heading leaves a normal line above it, as in a word processor.
        if (info.Kind == LineKind.Heading && offset == 0 && length > 0)
        {
            made.Tag = info;
            para.Tag = LineInfo.Of(LineKind.Text);
        }
        else
        {
            made.Tag = next;
        }

        foreach (var paragraph in new[] { para, made })
        {
            if (paragraph.Parent is FlowDocument)
            {
                Restyle(paragraph);
            }

            Touch(TopOf(paragraph.ContentStart));
        }

        // A new empty line starts plain, even after a line that ended in bold or italic.
        if (VisibleLength(made) == 0)
        {
            foreach (var run in AllRuns(made.Inlines).ToList())
            {
                run.ClearValue(TextElement.FontWeightProperty);
                run.ClearValue(TextElement.FontStyleProperty);
                run.ClearValue(Inline.TextDecorationsProperty);
                run.ClearValue(TextElement.FontFamilyProperty);
                run.ClearValue(TextElement.FontSizeProperty);
                run.ClearValue(TextElement.BackgroundProperty);
            }
        }

        CaretPosition = made.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
    });

    private Paragraph NewLine(LineInfo info)
    {
        var line = new Paragraph { Tag = info };
        Restyle(line);
        _touched.Add(line);
        return line;
    }

    /// <summary>Backspace at the start of a list item or quote takes the bullet or quote off first, keeping the text.</summary>
    private bool Backspace()
    {
        if (!Selection.IsEmpty || TopOrNull(CaretPosition) is not { } top || TextPara(top) is not { } para || CharOffset(para, CaretPosition) != 0)
        {
            return false;
        }

        var info = Info(top);
        if (info.Kind == LineKind.Item)
        {
            Change(() => Keep(() => Convert(top, line =>
            {
                line.Kind = line.Level > 0 ? LineKind.More : LineKind.Text;
                line.Level = Math.Max(0, line.Level - 1);
            })));
            return true;
        }

        if (info.Kind == LineKind.Quote)
        {
            Change(() => Keep(() => Convert(top, line => line.Kind = LineKind.Text)));
            return true;
        }

        if (info.Kind == LineKind.More && top.PreviousBlock is { } above && Info(above).Kind is LineKind.Item or LineKind.More && TextPara(above) is { } aboveText)
        {
            // A line inside a list item joins the line above, as Backspace does anywhere else. WPF would try to
            // outdent it instead, which does nothing here.
            Change(() =>
            {
                var at = VisibleLength(aboveText);
                foreach (var inline in para.Inlines.ToList())
                {
                    para.Inlines.Remove(inline);
                    aboveText.Inlines.Add(inline);
                }

                Document.Blocks.Remove(top);
                Touch(above);
                CaretPosition = PointerAt(aboveText, at);
            });
            return true;
        }

        if (top.PreviousBlock is not { } previous)
        {
            if (info.Kind == LineKind.Heading)
            {
                Change(() => Keep(() => Convert(top, line =>
                {
                    line.Kind = LineKind.Text;
                    line.Level = 0;
                })));
            }

            return true;
        }

        var before = Info(previous);
        if (before.Kind == LineKind.Rule)
        {
            Change(() => Document.Blocks.Remove(previous));
            return true;
        }

        if (before.Kind is LineKind.Raw or LineKind.Answer)
        {
            // Protected, so Backspace doesn't take it. An empty line after it goes, though.
            if (VisibleLength(para) == 0 && top.NextBlock is not null)
            {
                Change(() =>
                {
                    var next = top.NextBlock;
                    Document.Blocks.Remove(top);
                    if (next is not null)
                    {
                        CaretPosition = next.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
                    }
                });
            }

            return true;
        }

        if (info.Kind != LineKind.Text && before.Kind == LineKind.Text && TextPara(previous) is { } empty && VisibleLength(empty) == 0)
        {
            // An empty line above goes without taking this line's heading or code with it.
            Change(() => Keep(() => Document.Blocks.Remove(previous)));
            return true;
        }

        return false;
    }

    private bool DeleteForward()
    {
        if (!Selection.IsEmpty || TopOrNull(CaretPosition) is not { } top || TextPara(top) is not { } para
            || CharOffset(para, CaretPosition) != VisibleLength(para) || top.NextBlock is not { } next)
        {
            return false;
        }

        var after = Info(next);
        if (after.Kind == LineKind.Rule)
        {
            Change(() => Keep(() => Document.Blocks.Remove(next)));
            return true;
        }

        return after.Kind is LineKind.Raw or LineKind.Answer;
    }

    /// <summary>Runs <paramref name="action"/> and puts the selection back where it was, by paragraph and character.</summary>
    private void Keep(Action action)
    {
        var startPara = Selection.Start.Paragraph;
        var endPara = Selection.End.Paragraph;
        var start = startPara is null ? 0 : CharOffset(startPara, Selection.Start);
        var end = endPara is null ? 0 : CharOffset(endPara, Selection.End);
        action();
        if (startPara?.Parent is not null && endPara?.Parent is not null)
        {
            Selection.Select(PointerAt(startPara, start), PointerAt(endPara, end));
        }
    }

    /// <summary>Changes a line's kind, moving its paragraph into or out of a list as needed.</summary>
    private Block Convert(Block top, Action<LineInfo> change)
    {
        var info = Info(top);
        change(info);
        info.Dirty = true;
        if (info.Kind != LineKind.Item)
        {
            info.Checked = false;
        }

        var para = TextPara(top);
        Block result = top;
        if (para is not null && info.Kind == LineKind.Item && top is not List)
        {
            result = Wrap(para, top);
        }
        else if (para is not null && info.Kind != LineKind.Item && top is List list)
        {
            list.ListItems.FirstListItem!.Blocks.Remove(para);
            Document.Blocks.InsertAfter(list, para);
            Document.Blocks.Remove(list);
            para.Tag = info;
            result = para;
        }

        Restyle(result);
        Touch(result);
        return result;
    }

    /// <summary>The selected lines a list, heading, or quote command changes. Across several lines, empty ones are left as they are.</summary>
    private List<Block> TextTops()
    {
        var tops = SelectedTops().Where(block => Info(block).IsTextLike).ToList();
        return tops.Count > 1 ? tops.Where(block => TextPara(block) is { } para && VisibleLength(para) > 0).DefaultIfEmpty(tops[0]).ToList() : tops;
    }

    private void ToggleList(ItemType type)
    {
        var tops = TextTops();
        if (tops.Count == 0)
        {
            return;
        }

        var all = tops.All(block => Info(block) is { Kind: LineKind.Item } item && item.Type == type);
        Change(() => Keep(() =>
        {
            foreach (var top in tops)
            {
                Convert(top, line =>
                {
                    if (all)
                    {
                        line.Kind = LineKind.Text;
                        line.Level = 0;
                        return;
                    }

                    if (line.Kind is not (LineKind.Item or LineKind.More))
                    {
                        line.Level = 0;
                    }

                    line.Kind = LineKind.Item;
                    if (line.Type != type)
                    {
                        line.Checked = false;
                        line.Number = 1;
                    }

                    line.Type = type;
                });
            }
        }));
    }

    private void ToggleQuote()
    {
        var tops = TextTops();
        var all = tops.Count > 0 && tops.All(block => Info(block).Kind == LineKind.Quote);
        Change(() => Keep(() =>
        {
            foreach (var top in tops)
            {
                Convert(top, line =>
                {
                    line.Kind = all ? LineKind.Text : LineKind.Quote;
                    line.Level = 0;
                });
            }
        }));
    }

    /// <summary>Headings 1 to 3, or 0 for normal text. Normal text leaves list items and quotes as they are.</summary>
    private void SetHeading(int level)
    {
        var tops = TextTops();
        Change(() => Keep(() =>
        {
            foreach (var top in tops)
            {
                var info = Info(top);
                if (level == 0 && info.Kind != LineKind.Heading || level > 0 && info.Kind == LineKind.Heading && info.Level == level)
                {
                    continue;
                }

                Convert(top, line =>
                {
                    line.Kind = level == 0 ? LineKind.Text : LineKind.Heading;
                    line.Level = Math.Clamp(level, 0, 6);
                });
            }
        }));
    }

    /// <summary>Tab nests list items one level deeper, under the item above. Shift+Tab steps out, and off the list from the top level.</summary>
    private void Indent(int change)
    {
        var tops = SelectedTops().Where(block => Info(block).Kind is LineKind.Item or LineKind.More).ToList();
        if (tops.Count == 0)
        {
            return;
        }

        Change(() => Keep(() =>
        {
            foreach (var top in tops)
            {
                var info = Info(top);
                if (change > 0)
                {
                    var above = PreviousItem(top);
                    if (above is null || info.Level > above.Level)
                    {
                        continue;
                    }

                    Convert(top, line => line.Level++);
                }
                else if (info.Level > 0)
                {
                    Convert(top, line => line.Level--);
                }
                else if (info.Kind == LineKind.Item)
                {
                    Convert(top, line => line.Kind = LineKind.Text);
                }
            }
        }));
    }

    private static LineInfo? PreviousItem(Block block)
    {
        for (var previous = block.PreviousBlock; previous is not null; previous = previous.PreviousBlock)
        {
            var info = Info(previous);
            if (info.Kind == LineKind.Item)
            {
                return info;
            }

            if (info.Kind != LineKind.More)
            {
                return null;
            }
        }

        return null;
    }

    private void ToggleCheck(Block top) => Change(() =>
    {
        var info = Info(top);
        info.Checked = !info.Checked;
        info.Dirty = true;
        Restyle(top);
        Touch(top);
    });

    // Bold, italic, and the rest

    private enum InlineMark
    {
        Bold,
        Italic,
        Underline,
        Strike,
        Code,
    }

    private static bool Has(Run run, Paragraph paragraph, InlineMark mark)
    {
        var style = StyleOf(run, paragraph);
        return mark switch
        {
            InlineMark.Bold => style.Bold,
            InlineMark.Italic => style.Italic,
            InlineMark.Underline => style.Underline,
            InlineMark.Strike => style.Strike,
            _ => style.Code,
        };
    }

    private static void Set(Run run, Paragraph paragraph, InlineMark mark, bool on)
    {
        var style = StyleOf(run, paragraph);
        switch (mark)
        {
            case InlineMark.Bold when on:
                run.FontWeight = FontWeights.Bold;
                break;
            case InlineMark.Bold:
                run.FontWeight = paragraph.FontWeight.ToOpenTypeWeight() >= 700 ? FontWeights.Normal : paragraph.FontWeight;
                break;
            case InlineMark.Italic when on:
                run.FontStyle = FontStyles.Italic;
                break;
            case InlineMark.Italic:
                run.ClearValue(TextElement.FontStyleProperty);
                break;
            case InlineMark.Underline:
            case InlineMark.Strike:
                var underline = mark == InlineMark.Underline ? on : style.Underline;
                var strike = mark == InlineMark.Strike ? on : style.Strike;
                for (DependencyObject? element = LogicalTreeHelper.GetParent(run); element is Span span && element is not Hyperlink; element = LogicalTreeHelper.GetParent(span))
                {
                    span.ClearValue(Inline.TextDecorationsProperty);
                }

                if (Decorations(underline, strike) is { } decorations)
                {
                    run.TextDecorations = decorations;
                }
                else
                {
                    run.ClearValue(Inline.TextDecorationsProperty);
                }

                break;
            case InlineMark.Code when on:
                run.ClearValue(TextElement.FontWeightProperty);
                run.ClearValue(TextElement.FontStyleProperty);
                run.ClearValue(Inline.TextDecorationsProperty);
                MakeCode(run);
                break;
            case InlineMark.Code:
                run.ClearValue(TextElement.FontFamilyProperty);
                run.ClearValue(TextElement.FontSizeProperty);
                run.ClearValue(TextElement.BackgroundProperty);
                break;
        }
    }

    /// <summary>
    /// Bold, italic, underline, strikethrough, or code on the selection: on for all of it unless all of it already
    /// has it. With nothing selected, it applies to what's typed next.
    /// </summary>
    private void ToggleInline(InlineMark mark)
    {
        if (IsReadOnly || TopOrNull(Selection.Start) is not { } first || Info(first).Kind is LineKind.Code || !Info(first).IsTextLike && Selection.IsEmpty)
        {
            return;
        }

        if (Selection.IsEmpty)
        {
            var on = !CurrentFormatHas(mark);
            switch (mark)
            {
                case InlineMark.Bold:
                    Selection.ApplyPropertyValue(TextElement.FontWeightProperty, on ? FontWeights.Bold : FontWeights.Normal);
                    break;
                case InlineMark.Italic:
                    Selection.ApplyPropertyValue(TextElement.FontStyleProperty, on ? FontStyles.Italic : FontStyles.Normal);
                    break;
                case InlineMark.Underline:
                case InlineMark.Strike:
                    var now = CurrentFormat();
                    Selection.ApplyPropertyValue(Inline.TextDecorationsProperty,
                        Decorations(mark == InlineMark.Underline ? on : now.Underline, mark == InlineMark.Strike ? on : now.Strike) ?? new TextDecorationCollection());
                    break;
                case InlineMark.Code:
                    Selection.ApplyPropertyValue(TextElement.FontFamilyProperty, on ? CodeFont : TextFont);
                    Selection.ApplyPropertyValue(TextElement.FontSizeProperty, on ? 13.0 : BaseSize);
                    break;
            }

            FormatChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var start = Selection.Start;
        var end = Selection.End;
        Change(() => Keep(() =>
        {
            // Splits the runs at the selection's edges, so each run is wholly in or out of it.
            new TextRange(start, end).ApplyPropertyValue(Inline.BaselineAlignmentProperty, BaselineAlignment.Top);
            var runs = RunsIn(start, end);
            var on = !runs.All(entry => Has(entry.Run, entry.Paragraph, mark));
            foreach (var (run, paragraph, top) in runs)
            {
                Set(run, paragraph, mark, on);
                run.ClearValue(Inline.BaselineAlignmentProperty);
                Touch(top);
            }
        }));
    }

    /// <summary>
    /// Takes bold, italic, underline, strikethrough, and code off the selection, keeping its links and the kind of
    /// line it's on. With nothing selected, what's typed next is plain.
    /// </summary>
    private void ClearFormatting()
    {
        if (IsReadOnly || TopOrNull(Selection.Start) is not { } first || Info(first).Kind is LineKind.Code || !Info(first).IsTextLike && Selection.IsEmpty)
        {
            return;
        }

        if (Selection.IsEmpty)
        {
            Selection.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Normal);
            Selection.ApplyPropertyValue(TextElement.FontStyleProperty, FontStyles.Normal);
            Selection.ApplyPropertyValue(Inline.TextDecorationsProperty, new TextDecorationCollection());
            Selection.ApplyPropertyValue(TextElement.FontFamilyProperty, TextFont);
            Selection.ApplyPropertyValue(TextElement.FontSizeProperty, BaseSize);
            FormatChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var start = Selection.Start;
        var end = Selection.End;
        Change(() => Keep(() =>
        {
            new TextRange(start, end).ApplyPropertyValue(Inline.BaselineAlignmentProperty, BaselineAlignment.Top);
            foreach (var (run, paragraph, top) in RunsIn(start, end))
            {
                foreach (var mark in new[] { InlineMark.Code, InlineMark.Bold, InlineMark.Italic, InlineMark.Underline, InlineMark.Strike })
                {
                    Set(run, paragraph, mark, on: false);
                }

                run.ClearValue(Inline.BaselineAlignmentProperty);
                Touch(top);
            }
        }));
    }

    private bool CurrentFormatHas(InlineMark mark)
    {
        var format = CurrentFormat();
        return mark switch
        {
            InlineMark.Bold => format.Bold,
            InlineMark.Italic => format.Italic,
            InlineMark.Underline => format.Underline,
            InlineMark.Strike => format.Strike,
            _ => format.Code,
        };
    }

    private List<(Run Run, Paragraph Paragraph, Block Top)> RunsIn(TextPointer start, TextPointer end)
    {
        var runs = new List<(Run, Paragraph, Block)>();
        var first = TopOrNull(start);
        for (var block = first; block is not null; block = block.NextBlock)
        {
            if (block.ContentStart.CompareTo(end) > 0)
            {
                break;
            }

            if (TextPara(block) is { } paragraph && Info(block).IsTextLike)
            {
                foreach (var run in AllRuns(paragraph.Inlines))
                {
                    if (run.Text.Length > 0 && run.ContentStart.CompareTo(start) >= 0 && run.ContentEnd.CompareTo(end) <= 0)
                    {
                        runs.Add((run, paragraph, block));
                    }
                }
            }
        }

        return runs;
    }

    internal static IEnumerable<Run> AllRuns(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is Run run)
            {
                yield return run;
            }
            else if (inline is Span span)
            {
                foreach (var inner in AllRuns(span.Inlines))
                {
                    yield return inner;
                }
            }
        }
    }

    // Links

    /// <summary>Ctrl+K: adds a link to the selection, or changes or removes the one at the caret.</summary>
    private void EditLink()
    {
        if (IsReadOnly || TopOrNull(Selection.Start) is not { } top || !Info(top).IsTextLike)
        {
            return;
        }

        var link = LinkAt(Selection.Start);
        var current = link?.Tag as string ?? "";
        var answer = (AskForLink ?? AskWithForm)(current);
        if (answer is null)
        {
            return;
        }

        var url = WebAddress(answer);
        if (url.Length > 0 && !IsSafeLink(url))
        {
            // Only web and mail addresses become links, so a note can never hold one that runs something.
            return;
        }

        Change(() =>
        {
            if (link is not null)
            {
                if (url.Length == 0)
                {
                    Unlink(link);
                }
                else
                {
                    link.Tag = url;
                    link.ToolTip = $"{url}\nCtrl+click to open";
                }

                Touch(top);
                return;
            }

            if (url.Length == 0 || TextPara(top) is not { } para)
            {
                return;
            }

            var start = Selection.Start;
            var end = Selection.End;
            if (Selection.IsEmpty || start.Paragraph != end.Paragraph || LinkAt(end) is not null)
            {
                // Nothing selected, or a selection across lines: the address goes in as its own link.
                var offset = CharOffset(para, Selection.IsEmpty ? start : end);
                PointerAt(para, offset).InsertTextInRun(url);
                start = PointerAt(para, offset);
                end = PointerAt(para, offset + url.Length);
            }

            LinkRange(top, start, end, url);
        });
    }

    /// <summary>Only web and mail addresses become links, so a note can never hold one that runs something.</summary>
    internal static bool IsSafeLink(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto";

    private void LinkRange(Block top, TextPointer start, TextPointer end, string url)
    {
        var made = new Hyperlink(start, end) { Tag = url, ToolTip = $"{url}\nCtrl+click to open" };
        made.SetResourceReference(TextElement.ForegroundProperty, "Accent");
        Touch(top);
        CaretPosition = made.ElementEnd.GetInsertionPosition(LogicalDirection.Forward);
    }

    /// <summary>
    /// Pasting a web address over selected text on one line makes that text a link to it, as in Google Docs.
    /// True when it did; otherwise the paste goes in as usual.
    /// </summary>
    private bool PasteAsLink(string? pasted)
    {
        var url = pasted?.Trim() ?? "";
        if (Selection.IsEmpty || url.Length == 0 || url.Any(char.IsWhiteSpace) || !IsSafeLink(url)
            || Selection.Start.Paragraph is not { } para || Selection.End.Paragraph != para
            || TopOrNull(Selection.Start) is not { } top || !Info(top).IsTextLike
            || LinkAt(Selection.Start) is not null || LinkAt(Selection.End) is not null
            || AllRuns(para.Inlines).Any(run => LinkAt(run.ContentStart) is not null && run.ContentStart.CompareTo(Selection.End) < 0 && run.ContentEnd.CompareTo(Selection.Start) > 0))
        {
            return false;
        }

        var start = Selection.Start;
        var end = Selection.End;
        Change(() => LinkRange(top, start, end, url));
        return true;
    }

    private static void Unlink(Hyperlink link)
    {
        var inlines = link.Parent switch
        {
            Paragraph paragraph => paragraph.Inlines,
            Span span => span.Inlines,
            _ => null,
        };

        if (inlines is null)
        {
            return;
        }

        foreach (var child in link.Inlines.ToList())
        {
            link.Inlines.Remove(child);
            inlines.InsertBefore(link, child);
        }

        inlines.Remove(link);
    }

    /// <summary>A typed address gets https:// when it has no scheme, or mailto: when it's an email address.</summary>
    internal static string WebAddress(string typed)
    {
        var text = typed.Trim();
        if (text.Length == 0 || Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme.Length > 1)
        {
            return text;
        }

        return text.Contains('@') && !text.Contains('/') ? "mailto:" + text : "https://" + text;
    }

    private string? AskWithForm(string current)
    {
        if (Window.GetWindow(this) is not { } owner)
        {
            return null;
        }

        var form = new RenameWindow(owner, current, current.Length > 0 ? "Save" : "Add link", "A web address. Leave it empty to remove the link.", "Link");
        System.Windows.Automation.AutomationProperties.SetName(form.NameBox, "Link address");
        var removing = false;
        form.DoneButton.Click += (_, _) =>
        {
            if (form.NewName.Length == 0 && current.Length > 0)
            {
                removing = true;
                form.DialogResult = true;
            }
        };

        return form.ShowDialog() == true ? removing ? "" : form.NewName : null;
    }

    private void OpenLink(Hyperlink link)
    {
        var address = link.Tag as string ?? link.NavigateUri?.OriginalString;
        if (SafeResource.Check(address) is { } target)
        {
            OpenTarget(target);
        }
        else if (address is not null && Uri.TryCreate(address.Trim(), UriKind.Absolute, out var mail) && mail.Scheme == "mailto")
        {
            // A mail link starts an email in the mail app.
            OpenTarget(mail.AbsoluteUri);
        }
    }

    // The mouse

    /// <summary>Ctrl+click opens a link, and a click on a checklist box ticks it.</summary>
    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(this);
        if (GetPositionFromPoint(point, snapToText: true) is not { } pointer)
        {
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && LinkAt(pointer) is { } link)
        {
            e.Handled = true;
            OpenLink(link);
            return;
        }

        if (!IsReadOnly && TopOrNull(pointer) is { } top && Info(top) is { Kind: LineKind.Item, Type: ItemType.Task } && TextPara(top) is { } para && InBox(para, point))
        {
            e.Handled = true;
            ToggleCheck(top);
        }
    }

    internal static bool InBox(Paragraph paragraph, Point point)
    {
        var start = paragraph.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        return !start.IsEmpty && point.X < start.Left - 2 && point.X > start.Left - IndentStep - 6 && point.Y >= start.Top - 3 && point.Y <= start.Bottom + 3;
    }

    // Formatting typed as Markdown

    /// <summary>
    /// Typing Markdown formats it, as in a word processor: "# " makes a heading, "- " or "* " a bullet, "1. " a
    /// numbered item, "[] " a checklist item, "&gt; " a quote, and **bold**, *italic*, ~~strikethrough~~, or `code`
    /// format as the closing mark is typed. Undo right after puts the typed marks back.
    /// </summary>
    private void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (HandleTextInput(e.Text))
        {
            e.Handled = true;
        }
    }

    /// <summary>Typing one character. True when it started formatting, so the character was typed here already.</summary>
    internal bool HandleTextInput(string text)
    {
        var e = new TypedResult(text);
        Typed(e);
        return e.Handled;
    }

    private sealed class TypedResult(string text)
    {
        public string Text { get; } = text;

        public bool Handled { get; set; }
    }

    private void Typed(TypedResult e)
    {
        if (IsReadOnly || !Selection.IsEmpty || e.Text.Length != 1 || TopOrNull(CaretPosition) is not { } top || TextPara(top) is not { } para)
        {
            return;
        }

        var info = Info(top);
        if (!info.IsTextLike)
        {
            return;
        }

        var offset = CharOffset(para, CaretPosition);
        var before = new TextRange(para.ContentStart, CaretPosition).Text;
        if (before.Length != offset)
        {
            return;
        }

        if (e.Text == " " && BlockTrigger(info, before) is { } apply)
        {
            e.Handled = true;
            TypeText(" ");
            _newStep = true;
            var last = _undo.Count > 0 ? _undo[^1] : null;
            Change(() =>
            {
                new TextRange(para.ContentStart, PointerAt(para, offset + 1)).Text = "";
                var made = Convert(top, apply);
                if (TextPara(made) is { } line)
                {
                    CaretPosition = line.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
                }
            });

            if (_undo.Count > 0 && !ReferenceEquals(_undo[^1], last))
            {
                _undo[^1] = _undo[^1] with { Space = true };
            }

            return;
        }

        if (e.Text is "*" or "`" or "~" or "_" && !CurrentFormat().Code && InlineTrigger(before + e.Text) is { } found)
        {
            e.Handled = true;
            TypeText(e.Text);
            _newStep = true;
            var (start, open, content, close, mark) = found;
            Change(() =>
            {
                var end = start + open + content + close;
                new TextRange(PointerAt(para, end - close), PointerAt(para, end)).Text = "";
                new TextRange(PointerAt(para, start), PointerAt(para, start + open)).Text = "";
                var from = PointerAt(para, start);
                var to = PointerAt(para, start + content);
                new TextRange(from, to).ApplyPropertyValue(Inline.BaselineAlignmentProperty, BaselineAlignment.Top);
                foreach (var (run, paragraph, block) in RunsIn(PointerAt(para, start), PointerAt(para, start + content)))
                {
                    Set(run, paragraph, mark, on: true);
                    run.ClearValue(Inline.BaselineAlignmentProperty);
                }

                Touch(top);
                CaretPosition = PointerAt(para, start + content);
            });

            // What's typed next is plain again.
            switch (mark)
            {
                case InlineMark.Bold:
                    Selection.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Normal);
                    break;
                case InlineMark.Italic:
                    Selection.ApplyPropertyValue(TextElement.FontStyleProperty, FontStyles.Normal);
                    break;
                case InlineMark.Strike:
                    Selection.ApplyPropertyValue(Inline.TextDecorationsProperty, new TextDecorationCollection());
                    break;
                case InlineMark.Code:
                    Selection.ApplyPropertyValue(TextElement.FontFamilyProperty, TextFont);
                    Selection.ApplyPropertyValue(TextElement.FontSizeProperty, BaseSize);
                    break;
            }
        }
    }

    /// <summary>Types text at the caret the way the keyboard would, as part of the current typing burst.</summary>
    private void TypeText(string text)
    {
        Selection.Text = text;
        Selection.Select(Selection.End, Selection.End);
    }

    private static Action<LineInfo>? BlockTrigger(LineInfo info, string before)
    {
        if (info.Kind == LineKind.Item && info.Type == ItemType.Bullet && before is "[]" or "[ ]")
        {
            return line => line.Type = ItemType.Task;
        }

        if (info.Kind != LineKind.Text)
        {
            return null;
        }

        return before switch
        {
            "#" or "##" or "###" => line =>
            {
                line.Kind = LineKind.Heading;
                line.Level = before.Length;
            },
            "-" or "*" or "+" => line =>
            {
                line.Kind = LineKind.Item;
                line.Type = ItemType.Bullet;
                line.Level = 0;
            },
            "1." or "1)" => line =>
            {
                line.Kind = LineKind.Item;
                line.Type = ItemType.Number;
                line.Delimiter = before[1];
                line.Number = 1;
                line.Level = 0;
            },
            "[]" or "[ ]" => line =>
            {
                line.Kind = LineKind.Item;
                line.Type = ItemType.Task;
                line.Level = 0;
            },
            ">" => line => line.Kind = LineKind.Quote,
            _ => null,
        };
    }

    private static (int Start, int Open, int Content, int Close, InlineMark Mark)? InlineTrigger(string text)
    {
        foreach (var (pattern, mark, marks) in InlineTriggers)
        {
            if (pattern.Match(text) is { Success: true } match)
            {
                return (match.Index, marks, match.Groups["t"].Length, marks, mark);
            }
        }

        return null;
    }

    private static readonly (Regex Pattern, InlineMark Mark, int Marks)[] InlineTriggers =
    [
        (BoldTrigger(), InlineMark.Bold, 2),
        (StrikeTrigger(), InlineMark.Strike, 2),
        (ItalicStarTrigger(), InlineMark.Italic, 1),
        (ItalicUnderscoreTrigger(), InlineMark.Italic, 1),
        (CodeTrigger(), InlineMark.Code, 1),
    ];

    [GeneratedRegex(@"(?<![*\\])\*\*(?<t>[^*\s](?:[^*]*[^*\s])?)\*\*$")]
    private static partial Regex BoldTrigger();

    [GeneratedRegex(@"(?<![~\\])~~(?<t>[^~\s](?:[^~]*[^~\s])?)~~$")]
    private static partial Regex StrikeTrigger();

    [GeneratedRegex(@"(?<![*\\\w])\*(?<t>[^*\s](?:[^*]*[^*\s])?)\*$")]
    private static partial Regex ItalicStarTrigger();

    [GeneratedRegex(@"(?<![_\\\w])_(?<t>[^_\s](?:[^_]*[^_\s])?)_$")]
    private static partial Regex ItalicUnderscoreTrigger();

    [GeneratedRegex(@"(?<![`\\])`(?<t>[^`]+)`$")]
    private static partial Regex CodeTrigger();
}
