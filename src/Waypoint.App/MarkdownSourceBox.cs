using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Waypoint.App;

/// <summary>
/// The note's Markdown text, for Show Markdown. Markdown is typed straight in and styled as it's typed, in the same font as the formatted
/// note, so clicking into a note never changes how it looks. Headings are larger, bold, italic, and code show as
/// such, and the marks that make them stay in place, dimmed. Each line is one paragraph, and the text in and out
/// is plain Markdown. Undo works on the text, since restyling would otherwise fill the undo list.
/// </summary>
public sealed partial class MarkdownSourceBox : RichTextBox
{
    private const double BaseSize = 14;
    private static readonly FontFamily TextFont = new("Segoe UI Variable Text, Segoe UI");
    private static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas, Courier New");
    private static readonly TimeSpan UndoPause = TimeSpan.FromMilliseconds(800);
    private const int MaxUndo = 200;

    private readonly List<(string Text, int Caret)> _undo = [];
    private readonly List<(string Text, int Caret)> _redo = [];
    private string _text = "";
    private string _loaded = "";
    private string _loadedJoined = "";
    private string _original = "";
    private string _originalJoined = "";
    private string _newline = "\r\n";
    private bool _styling;
    private bool _stylePending;
    private DateTime _lastEdit = DateTime.MinValue;

    public MarkdownSourceBox()
    {
        IsUndoEnabled = false;
        AcceptsTab = true;
        AcceptsReturn = true;
        FontFamily = TextFont;
        FontSize = BaseSize;
        Document = NewDocument();
        DataObject.AddPastingHandler(this, OnPasting);
        TextChanged += OnChanged;
        PreviewKeyDown += OnKey;

        CommandBindings.Add(new CommandBinding(ApplicationCommands.Undo, (_, _) => Step(_undo, _redo), (_, e) => e.CanExecute = _undo.Count > 0));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Redo, (_, _) => Step(_redo, _undo), (_, e) => e.CanExecute = _redo.Count > 0));
        CommandBindings.Add(new CommandBinding(EditingCommands.ToggleBold, (_, _) => Wrap("**")));
        CommandBindings.Add(new CommandBinding(EditingCommands.ToggleItalic, (_, _) => Wrap("*")));

        // Formatting the editor itself wouldn't be saved, so those commands do nothing here.
        foreach (var command in new[]
        {
            EditingCommands.ToggleUnderline, EditingCommands.AlignLeft, EditingCommands.AlignCenter, EditingCommands.AlignRight,
            EditingCommands.AlignJustify, EditingCommands.IncreaseFontSize, EditingCommands.DecreaseFontSize,
            EditingCommands.ToggleSubscript, EditingCommands.ToggleSuperscript, EditingCommands.ToggleBullets,
            EditingCommands.ToggleNumbering, EditingCommands.IncreaseIndentation, EditingCommands.DecreaseIndentation,
        })
        {
            CommandBindings.Add(new CommandBinding(command, (_, _) => { }, (_, e) => e.CanExecute = false));
        }
    }

    /// <summary>The text changed by typing, pasting, or undo. Restyling never raises it.</summary>
    public event EventHandler? TextEdited;

    /// <summary>
    /// The note as plain Markdown. Setting it replaces everything and clears undo. Lines are counted with one kind of
    /// line ending; while the text is unchanged, it's exactly what was set, mixed line endings and all.
    /// </summary>
    public string Text
    {
        get => _text == _loadedJoined ? _loaded : _text == _originalJoined ? _original : _text;
        set
        {
            var text = value ?? "";
            _newline = text.Contains("\r\n", StringComparison.Ordinal) || !text.Contains('\n') ? "\r\n" : "\n";
            _undo.Clear();
            _redo.Clear();
            var joined = string.Join(_newline, text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));
            Load(joined, joined.Length);
            _loaded = text;
            _loadedJoined = joined;
            _original = text;
            _originalJoined = joined;
        }
    }

    /// <summary>The undo and redo so far, for the formatted note to carry on with.</summary>
    internal EditHistory History =>
        new(_undo.Select(step => step.Text).ToList(), _redo.Select(step => step.Text).ToList(), _original, _originalJoined);

    /// <summary>
    /// Show Markdown: takes over the note's text and its undo and redo from the formatted note, so Ctrl+Z goes on
    /// undoing changes made there.
    /// </summary>
    internal void Switch(string text, EditHistory history)
    {
        Text = text;
        _original = history.Original;
        _originalJoined = history.OriginalJoined;
        var next = _text;
        for (var i = history.Undo.Count - 1; i >= 0; i--)
        {
            _undo.Insert(0, (history.Undo[i], EditHistory.ChangedEnd(next, history.Undo[i])));
            next = history.Undo[i];
        }

        var previous = _text;
        for (var i = history.Redo.Count - 1; i >= 0; i--)
        {
            _redo.Insert(0, (history.Redo[i], EditHistory.ChangedEnd(previous, history.Redo[i])));
            previous = history.Redo[i];
        }

        _lastEdit = DateTime.MinValue;
    }

    /// <summary>Where the caret is, counted in characters of <see cref="Text"/>.</summary>
    public int CaretIndex
    {
        get => OffsetOf(CaretPosition);
        set => CaretPosition = PositionAt(Math.Clamp(value, 0, _text.Length));
    }

    public void ScrollToCaret()
    {
        if (CaretPosition.Paragraph is { } paragraph)
        {
            paragraph.BringIntoView();
        }
    }

    private FlowDocument NewDocument()
    {
        var document = new FlowDocument { PagePadding = new Thickness(0), FontFamily = TextFont, FontSize = BaseSize, Background = Brushes.Transparent };
        document.SetResourceReference(FlowDocument.ForegroundProperty, "PrimaryText");
        return document;
    }

    private void Load(string text, int caret)
    {
        _styling = true;
        try
        {
            var document = NewDocument();
            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            {
                document.Blocks.Add(new Paragraph(new Run(line)) { Tag = new LineState(line, null) });
            }

            Document = document;
            _text = text;
            StyleAll();
            CaretPosition = PositionAt(Math.Clamp(caret, 0, text.Length));
        }
        finally
        {
            _styling = false;
        }
    }

    private IEnumerable<Paragraph> Lines => Document.Blocks.OfType<Paragraph>();

    private string ReadText() => string.Join(_newline, Lines.Select(TextOf));

    private static string LineText(Paragraph paragraph) =>
        new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.Replace("\r\n", "\n").Replace("\n", "");

    /// <summary>
    /// What a line holds and the style it was last given. Reading a line's text from the document is slow, so it's
    /// read once and kept until typing changes that line, which keeps typing quick in a long note.
    /// </summary>
    private sealed record LineState(string? Text, string? Key);

    private static string TextOf(Paragraph paragraph)
    {
        if (paragraph.Tag is LineState { Text: { } known })
        {
            return known;
        }

        var text = LineText(paragraph);
        paragraph.Tag = new LineState(text, (paragraph.Tag as LineState)?.Key);
        return text;
    }

    /// <summary>Forgets the kept text of every line a change touched, including lines a new line was split from.</summary>
    private void Forget(TextChangedEventArgs e)
    {
        foreach (var change in e.Changes)
        {
            var start = Document.ContentStart.GetPositionAtOffset(Math.Max(0, change.Offset - 1)) ?? Document.ContentStart;
            var end = Document.ContentStart.GetPositionAtOffset(change.Offset + change.AddedLength + 1) ?? Document.ContentEnd;
            var first = start.Paragraph ?? start.GetInsertionPosition(LogicalDirection.Backward).Paragraph ?? Lines.FirstOrDefault();
            for (Block? block = first; block is not null; block = block.NextBlock)
            {
                // A new record each time, since a line split in two can share the one it had.
                block.Tag = new LineState(null, null);
                if (block.ContentStart.CompareTo(end) > 0)
                {
                    break;
                }
            }
        }
    }

    private void OnChanged(object sender, TextChangedEventArgs e)
    {
        if (_styling)
        {
            return;
        }

        Forget(e);
        var text = ReadText();
        if (text == _text)
        {
            return;
        }

        // A pause, or a new line, starts a new undo step. Typing in between is one step.
        var now = DateTime.UtcNow;
        if (now - _lastEdit > UndoPause || _undo.Count == 0 || text.Length - _text.Length is > 1 or < -1 || text.Count(c => c == '\n') != _text.Count(c => c == '\n'))
        {
            _undo.Add((_text, Math.Min(CaretIndex, _text.Length)));
            if (_undo.Count > MaxUndo)
            {
                _undo.RemoveAt(0);
            }
        }

        _lastEdit = now;
        _redo.Clear();
        _text = text;
        TextEdited?.Invoke(this, EventArgs.Empty);

        if (!_stylePending)
        {
            _stylePending = true;
            Dispatcher.BeginInvoke(() =>
            {
                _stylePending = false;
                StyleAll();
            }, DispatcherPriority.Normal);
        }
    }

    private void Step(List<(string Text, int Caret)> from, List<(string Text, int Caret)> to)
    {
        if (from.Count == 0)
        {
            return;
        }

        var (text, caret) = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add((_text, CaretIndex));
        Load(text, caret);
        _lastEdit = DateTime.MinValue;
        TextEdited?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Pasted or dropped content comes in as plain text, so nothing invisible ends up in the note.</summary>
    private static void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.SourceDataObject.GetData(DataFormats.UnicodeText) is string text)
        {
            e.DataObject = new DataObject(DataFormats.UnicodeText, text);
        }
        else
        {
            e.CancelCommand();
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        // A line break inside a line would read as two lines, so Shift+Enter starts a new one like Enter.
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            EditingCommands.EnterParagraphBreak.Execute(null, this);
        }
    }

    /// <summary>Ctrl+B and Ctrl+I put Markdown marks around the selection, or around the caret to type between.</summary>
    private void Wrap(string mark)
    {
        var start = Math.Min(OffsetOf(Selection.Start), _text.Length);
        var end = Math.Clamp(OffsetOf(Selection.End), start, _text.Length);
        var text = _text[..start] + mark + _text[start..end] + mark + _text[end..];
        _undo.Add((_text, start));
        _redo.Clear();
        Load(text, end + mark.Length);
        if (start == end)
        {
            CaretIndex = start + mark.Length;
        }

        TextEdited?.Invoke(this, EventArgs.Empty);
    }

    private int OffsetOf(TextPointer pointer)
    {
        var offset = 0;
        foreach (var line in Lines)
        {
            if (pointer.CompareTo(line.ContentEnd) <= 0 && pointer.CompareTo(line.ContentStart) >= 0)
            {
                return offset + new TextRange(line.ContentStart, pointer).Text.Length;
            }

            offset += TextOf(line).Length + _newline.Length;
        }

        return _text.Length;
    }

    private TextPointer PositionAt(int offset)
    {
        foreach (var line in Lines)
        {
            var length = TextOf(line).Length;
            if (offset <= length)
            {
                return InLine(line, offset);
            }

            offset -= length + _newline.Length;
        }

        return Document.ContentEnd;
    }

    private static TextPointer InLine(Paragraph line, int offset)
    {
        var pointer = line.ContentStart;
        while (pointer is not null && pointer.CompareTo(line.ContentEnd) < 0)
        {
            if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var run = pointer.GetTextRunLength(LogicalDirection.Forward);
                if (offset <= run)
                {
                    return pointer.GetPositionAtOffset(offset)!;
                }

                offset -= run;
                pointer = pointer.GetPositionAtOffset(run);
            }
            else
            {
                pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
            }
        }

        return line.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
    }

    /// <summary>Restyles every line whose text, or whether it's inside a code block, changed since it was last styled.</summary>
    private void StyleAll()
    {
        var caret = CaretIndex;
        var moved = false;
        _styling = true;
        try
        {
            var inFence = false;
            foreach (var line in Lines.ToList())
            {
                var text = TextOf(line);
                var fence = FenceLine().IsMatch(text);
                var key = $"{(inFence ? 1 : 0)}{(fence ? 1 : 0)}{text}";
                if ((line.Tag as LineState)?.Key != key)
                {
                    StyleLine(line, text, inFence || fence);
                    line.Tag = new LineState(text, key);
                    moved = true;
                }

                if (fence)
                {
                    inFence = !inFence;
                }
            }
        }
        finally
        {
            _styling = false;
        }

        if (moved)
        {
            _styling = true;
            CaretPosition = PositionAt(Math.Clamp(caret, 0, _text.Length));
            _styling = false;
        }
    }

    private static void StyleLine(Paragraph line, string text, bool code)
    {
        line.Inlines.Clear();
        line.Margin = new Thickness(0);
        line.FontSize = BaseSize;
        line.FontWeight = FontWeights.Normal;
        line.FontFamily = TextFont;
        line.LineHeight = 22;
        line.ClearValue(TextElement.ForegroundProperty);

        if (code)
        {
            line.FontFamily = CodeFont;
            line.FontSize = 13;
            line.Inlines.Add(new Run(text));
            return;
        }

        if (Heading().Match(text) is { Success: true } heading)
        {
            var level = heading.Groups["marks"].Length;
            line.FontSize = level switch
            {
                1 => 24,
                2 => 20,
                3 => 17,
                _ => 15,
            };
            line.LineHeight = line.FontSize * 1.4;
            line.FontWeight = FontWeights.SemiBold;
            line.Inlines.Add(Dim(heading.Groups["marks"].Value + heading.Groups["space"].Value));
            AddInline(line.Inlines, heading.Groups["rest"].Value);
            return;
        }

        if (Quote().Match(text) is { Success: true } quote)
        {
            line.SetResourceReference(TextElement.ForegroundProperty, "SecondaryText");
            line.Inlines.Add(Dim(quote.Groups["mark"].Value));
            AddInline(line.Inlines, quote.Groups["rest"].Value);
            return;
        }

        if (ListItem().Match(text) is { Success: true } item)
        {
            var mark = new Run(item.Groups["mark"].Value);
            mark.SetResourceReference(TextElement.ForegroundProperty, "Accent");
            line.Inlines.Add(mark);
            AddInline(line.Inlines, item.Groups["rest"].Value);
            return;
        }

        AddInline(line.Inlines, text);
    }

    /// <summary>Bold, italic, strikethrough, code, and links within a line, with their marks dimmed.</summary>
    private static void AddInline(InlineCollection inlines, string text)
    {
        var at = 0;
        foreach (Match match in Inline().Matches(text))
        {
            if (match.Index > at)
            {
                inlines.Add(new Run(text[at..match.Index]));
            }

            if (match.Groups["code"].Success)
            {
                inlines.Add(Dim("`"));
                var code = new Run(match.Groups["code"].Value) { FontFamily = CodeFont, FontSize = 13 };
                code.SetResourceReference(TextElement.BackgroundProperty, "Surface");
                inlines.Add(code);
                inlines.Add(Dim("`"));
            }
            else if (match.Groups["bold"].Success)
            {
                var mark = match.Groups["bmark"].Value;
                inlines.Add(Dim(mark));
                inlines.Add(new Run(match.Groups["bold"].Value) { FontWeight = FontWeights.Bold });
                inlines.Add(Dim(mark));
            }
            else if (match.Groups["strike"].Success)
            {
                inlines.Add(Dim("~~"));
                inlines.Add(new Run(match.Groups["strike"].Value) { TextDecorations = TextDecorations.Strikethrough });
                inlines.Add(Dim("~~"));
            }
            else if (match.Groups["italic"].Success)
            {
                var mark = match.Groups["imark"].Value;
                inlines.Add(Dim(mark));
                inlines.Add(new Run(match.Groups["italic"].Value) { FontStyle = FontStyles.Italic });
                inlines.Add(Dim(mark));
            }
            else
            {
                inlines.Add(Dim("["));
                var label = new Run(match.Groups["label"].Value);
                label.SetResourceReference(TextElement.ForegroundProperty, "Accent");
                inlines.Add(label);
                inlines.Add(Dim($"]({match.Groups["url"].Value})"));
            }

            at = match.Index + match.Length;
        }

        if (at < text.Length)
        {
            inlines.Add(new Run(text[at..]));
        }
    }

    private static Run Dim(string text)
    {
        var run = new Run(text);
        run.SetResourceReference(TextElement.ForegroundProperty, "QuietMark");
        return run;
    }

    [GeneratedRegex(@"^(?<marks>#{1,6})(?<space>\s+)(?<rest>.*)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(?<mark>\s*>\s?)(?<rest>.*)$")]
    private static partial Regex Quote();

    [GeneratedRegex(@"^(?<mark>\s*(?:[-*+]|\d+[.)])\s+(?:\[[ xX]\]\s+)?)(?<rest>.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^\s*(```|~~~)")]
    private static partial Regex FenceLine();

    [GeneratedRegex(@"`(?<code>[^`]+)`|(?<bmark>\*\*|__)(?<bold>\S(?:.*?\S)?)\k<bmark>|~~(?<strike>\S(?:.*?\S)?)~~|(?<imark>[*_])(?<italic>[^\s*_](?:[^*_]*?[^\s*_])?)\k<imark>|\[(?<label>[^\]]+)\]\((?<url>[^)\s]+)\)")]
    private static partial Regex Inline();
}
