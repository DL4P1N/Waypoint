using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Waypoint.Core.Notes;

/// <summary>
/// Turns rich text pasted from Word, Google Docs and web pages into the Markdown the note editor uses.
/// One line per block, no blank lines between ordinary paragraphs, and the same emphasis, list and
/// link forms the editor's own serializer writes. Uses a small tolerant tokenizer; never throws.
/// </summary>
public static class HtmlToMarkdown
{
    /// <summary>
    /// Converts the Windows clipboard "HTML Format" (CF_HTML: a "Version:0.9 StartHTML:... StartFragment:..." header
    /// then HTML with &lt;!--StartFragment--&gt; / &lt;!--EndFragment--&gt; comments) or plain HTML to Markdown.
    /// Uses only the fragment between the StartFragment/EndFragment comments when present.
    /// Returns "" when nothing usable remains.
    /// </summary>
    public static string FromClipboard(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        try
        {
            var root = new Parser(ExtractFragment(html)).Parse();
            var converter = new Converter();
            converter.WalkChildren(root, default);
            return converter.Finish();
        }
        catch (Exception)
        {
            // A paste must never crash the editor; an unreadable clipboard simply pastes nothing.
            return "";
        }
    }

    private static readonly Regex StartFragmentRx = new(@"<!--\s*StartFragment\s*-->", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex EndFragmentRx = new(@"<!--\s*EndFragment\s*-->", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string ExtractFragment(string html)
    {
        var start = StartFragmentRx.Match(html);
        if (start.Success)
        {
            int from = start.Index + start.Length;
            var end = EndFragmentRx.Match(html, from);
            return end.Success ? html[from..end.Index] : html[from..];
        }
        if (html.TrimStart().StartsWith("Version:", StringComparison.OrdinalIgnoreCase))
        {
            int lt = html.IndexOf('<');
            return lt < 0 ? "" : html[lt..];
        }
        return html;
    }

    // ---------------------------------------------------------------- DOM + tokenizer

    private sealed class Node(string name)
    {
        public readonly string Name = name;          // lower-case tag, or "#text", "#cond", "#root"
        public string? Text;                         // text content, or the condition of a Word conditional block
        public Dictionary<string, string>? Attrs;
        public readonly List<Node> Children = new();

        public string? Attr(string key) => Attrs != null && Attrs.TryGetValue(key, out var v) ? v : null;
    }

    private sealed class Parser(string s)
    {
        private const int MaxDepth = 200;

        private static readonly HashSet<string> VoidTags = new(StringComparer.Ordinal)
        { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr", "basefont", "frame", "keygen" };

        private static readonly HashSet<string> SkippedRawTags = new(StringComparer.Ordinal)
        { "script", "style", "title", "textarea", "xmp", "noscript", "noembed", "noframes", "iframe", "template" };

        private static readonly HashSet<string> ClosesParagraph = new(StringComparer.Ordinal)
        {
            "address", "article", "aside", "blockquote", "center", "details", "dialog", "dir", "div", "dl", "dd", "dt",
            "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hgroup",
            "hr", "li", "main", "menu", "nav", "ol", "p", "pre", "section", "summary", "table", "ul",
        };

        private static readonly HashSet<string> ParagraphScope = new(StringComparer.Ordinal) { "table", "td", "th", "caption", "button", "#cond" };
        private static readonly HashSet<string> ListScope = new(StringComparer.Ordinal) { "ul", "ol", "menu", "table", "td", "th" };
        private static readonly HashSet<string> DefScope = new(StringComparer.Ordinal) { "dl", "table", "td", "th" };
        private static readonly HashSet<string> TableScope = new(StringComparer.Ordinal) { "table" };
        private static readonly HashSet<string> RowScope = new(StringComparer.Ordinal) { "tr", "table" };
        private static readonly HashSet<string> CellScope = new(StringComparer.Ordinal) { "td", "th", "table" };

        private readonly List<Node> _stack = new() { new Node("#root") };
        private readonly StringBuilder _text = new();

        private Node Current => _stack[^1];

        public Node Parse()
        {
            int i = 0, n = s.Length;
            while (i < n)
            {
                char c = s[i];
                if (c != '<') { _text.Append(c); i++; continue; }

                if (string.CompareOrdinal(s, i, "<!--", 0, 4) == 0)
                {
                    FlushText();
                    int end = s.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    string body = (end < 0 ? s[(i + 4)..] : s[(i + 4)..end]).Trim();
                    i = end < 0 ? n : end + 3;
                    // Word's "downlevel-revealed" form: <!--[if !supportLists]--> ... <!--[endif]-->
                    if (body.StartsWith("[if", StringComparison.OrdinalIgnoreCase) && body.EndsWith(']') && !body.Contains('<'))
                        OpenCond(body[1..^1]);
                    else if (body.Equals("[endif]", StringComparison.OrdinalIgnoreCase))
                        CloseCond();
                    continue;
                }

                if (string.CompareOrdinal(s, i, "<![", 0, 3) == 0)
                {
                    FlushText();
                    if (string.Compare(s, i, "<![CDATA[", 0, 9, StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        int cend = s.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                        i = cend < 0 ? n : cend + 3;
                        continue;
                    }
                    int end = s.IndexOf("]>", i + 3, StringComparison.Ordinal);
                    string body = (end < 0 ? s[(i + 3)..] : s[(i + 3)..end]).Trim();
                    i = end < 0 ? n : end + 2;
                    if (body.StartsWith("if", StringComparison.OrdinalIgnoreCase)) OpenCond(body);
                    else if (body.StartsWith("endif", StringComparison.OrdinalIgnoreCase)) CloseCond();
                    continue;
                }

                if (i + 1 < n && (s[i + 1] == '!' || s[i + 1] == '?'))
                {
                    FlushText();
                    int end = s.IndexOf('>', i + 2);
                    i = end < 0 ? n : end + 1;
                    continue;
                }

                if (i + 2 < n && s[i + 1] == '/' && char.IsAsciiLetter(s[i + 2]))
                {
                    FlushText();
                    int j = i + 2;
                    while (j < n && !char.IsWhiteSpace(s[j]) && s[j] != '>' && s[j] != '/') j++;
                    string name = s[(i + 2)..j].ToLowerInvariant();
                    int end = s.IndexOf('>', j);
                    i = end < 0 ? n : end + 1;
                    EndTag(name);
                    continue;
                }

                if (i + 1 < n && char.IsAsciiLetter(s[i + 1]))
                {
                    FlushText();
                    i = StartTag(i);
                    continue;
                }

                _text.Append('<');
                i++;
            }
            FlushText();
            return _stack[0];
        }

        private int StartTag(int i)
        {
            int n = s.Length, j = i + 1;
            while (j < n && !char.IsWhiteSpace(s[j]) && s[j] != '>' && s[j] != '/' && s[j] != '<') j++;
            string name = s[(i + 1)..j].ToLowerInvariant();
            Dictionary<string, string>? attrs = null;
            bool selfClose = false;

            while (j < n)
            {
                while (j < n && char.IsWhiteSpace(s[j])) j++;
                if (j >= n) break;
                char c = s[j];
                if (c == '>') { j++; break; }
                if (c == '<') break; // malformed: a new tag starts before this one closed
                if (c == '/')
                {
                    if (j + 1 < n && s[j + 1] == '>') { selfClose = true; j += 2; break; }
                    j++;
                    continue;
                }

                int nameStart = j;
                while (j < n && !char.IsWhiteSpace(s[j]) && s[j] != '=' && s[j] != '>' && s[j] != '/' && s[j] != '<') j++;
                if (j == nameStart) { j++; continue; }
                string attrName = s[nameStart..j].ToLowerInvariant();
                string value = "";

                int k = j;
                while (k < n && char.IsWhiteSpace(s[k])) k++;
                if (k < n && s[k] == '=')
                {
                    j = k + 1;
                    while (j < n && char.IsWhiteSpace(s[j])) j++;
                    if (j < n && (s[j] == '"' || s[j] == '\''))
                    {
                        char q = s[j];
                        int close = s.IndexOf(q, j + 1);
                        if (close < 0)
                        {
                            // Unterminated quote: end the value at the tag's '>' so the text after it survives.
                            int gt = s.IndexOf('>', j + 1);
                            int stop = gt < 0 ? n : gt;
                            value = s[(j + 1)..stop];
                            j = stop;
                        }
                        else
                        {
                            value = s[(j + 1)..close];
                            j = close + 1;
                        }
                    }
                    else
                    {
                        int vs = j;
                        while (j < n && !char.IsWhiteSpace(s[j]) && s[j] != '>') j++;
                        value = s[vs..j];
                    }
                }

                attrs ??= new Dictionary<string, string>(StringComparer.Ordinal);
                attrs.TryAdd(attrName, WebUtility.HtmlDecode(value));
            }

            if (SkippedRawTags.Contains(name) && !selfClose)
            {
                int close = s.IndexOf("</" + name, j, StringComparison.OrdinalIgnoreCase);
                if (close < 0) return n;
                int gt = s.IndexOf('>', close);
                return gt < 0 ? n : gt + 1;
            }

            OpenElement(name, attrs, selfClose);
            return j;
        }

        private void OpenElement(string name, Dictionary<string, string>? attrs, bool selfClose)
        {
            if (name is "html" or "body")
            {
                if (name == "body") CloseInScope("head", TableScope);
                return;
            }

            if (ClosesParagraph.Contains(name)) CloseInScope("p", ParagraphScope);
            switch (name)
            {
                case "li": CloseInScope("li", ListScope); break;
                case "dt" or "dd": CloseInScope("dt", DefScope); CloseInScope("dd", DefScope); break;
                case "tr": CloseInScope("tr", TableScope); break;
                case "td" or "th": CloseInScope("td", RowScope); CloseInScope("th", RowScope); break;
                case "a": CloseInScope("a", CellScope); break;
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    if (IsHeading(Current.Name)) _stack.RemoveAt(_stack.Count - 1);
                    break;
            }

            var node = new Node(name) { Attrs = attrs };
            Current.Children.Add(node);
            if (!selfClose && !VoidTags.Contains(name) && _stack.Count < MaxDepth) _stack.Add(node);
        }

        private void EndTag(string name)
        {
            if (name is "html" or "body") return;
            if (name == "br") { OpenElement("br", null, true); return; }
            for (int k = _stack.Count - 1; k >= 1; k--)
            {
                if (_stack[k].Name == name || (IsHeading(name) && IsHeading(_stack[k].Name)))
                {
                    _stack.RemoveRange(k, _stack.Count - k);
                    return;
                }
            }
            // A stray end tag (e.g. an extra </p>) is ignored.
        }

        private void CloseInScope(string target, HashSet<string> boundaries)
        {
            for (int k = _stack.Count - 1; k >= 1; k--)
            {
                var name = _stack[k].Name;
                if (name == target) { _stack.RemoveRange(k, _stack.Count - k); return; }
                if (boundaries.Contains(name)) return;
            }
        }

        private void OpenCond(string condition)
        {
            var node = new Node("#cond") { Text = condition };
            Current.Children.Add(node);
            if (_stack.Count < MaxDepth) _stack.Add(node);
        }

        private void CloseCond()
        {
            for (int k = _stack.Count - 1; k >= 1; k--)
            {
                if (_stack[k].Name == "#cond") { _stack.RemoveRange(k, _stack.Count - k); return; }
            }
        }

        private void FlushText()
        {
            if (_text.Length == 0) return;
            string decoded = WebUtility.HtmlDecode(_text.ToString());
            _text.Clear();
            var children = Current.Children;
            if (children.Count > 0 && children[^1].Name == "#text") children[^1].Text += decoded;
            else children.Add(new Node("#text") { Text = decoded });
        }
    }

    private static bool IsHeading(string name) =>
        name.Length == 2 && name[0] == 'h' && name[1] >= '1' && name[1] <= '6';

    // ---------------------------------------------------------------- conversion

    private enum LineKind { Blank, Paragraph, Heading, ListItem, Quote, Code, Rule }

    /// <param name="FirstNotOne">The first item of a numbered list whose number is not 1 (cannot interrupt a paragraph).</param>
    private readonly record struct OutLine(string Text, LineKind Kind, bool FirstNotOne = false);

    [Flags]
    private enum Fmt { None = 0, Bold = 1, Italic = 2, Underline = 4, Strike = 8, Code = 16 }

    private readonly record struct Style(Fmt Fmt, string? Link);

    private readonly record struct Run(string Text, Fmt Fmt, string? Link);

    private sealed class ItemCtx
    {
        public int Indent, Width;
        public string Marker = "";
        public bool FirstNotOne, MarkerEmitted;
    }

    private sealed class ListCtx
    {
        public bool Ordered;
        public int Next, Seen;
        public int? LevelHint;
    }

    /// <summary>Remembers, per nesting level, where the last item sat so children indent to their parent's content column.</summary>
    private sealed class ListTracker
    {
        private const int MaxLevel = 10;
        private readonly List<(int Indent, int Width)?> _levels = new();
        public readonly Dictionary<int, int> WordCounters = new();

        public static int Clamp(int level) => Math.Clamp(level, 1, MaxLevel);

        public int IndentFor(int level)
        {
            if (level <= 1) return 0;
            int idx = level - 2;
            if (idx < _levels.Count && _levels[idx] is { } parent) return parent.Indent + parent.Width;
            return IndentFor(level - 1) + 2;
        }

        public void Record(int level, int indent, int width)
        {
            int idx = level - 1;
            while (_levels.Count <= idx) _levels.Add(null);
            _levels[idx] = (indent, width);
            _levels.RemoveRange(idx + 1, _levels.Count - idx - 1);
        }

        public void Reset()
        {
            _levels.Clear();
            WordCounters.Clear();
        }
    }

    private static readonly HashSet<string> BlockTags = new(StringComparer.Ordinal)
    {
        "address", "article", "aside", "blockquote", "caption", "center", "dd", "details", "dialog", "dir", "div", "dl", "dt",
        "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hgroup", "hr",
        "legend", "li", "main", "menu", "nav", "ol", "p", "pre", "section", "summary", "table", "tbody", "td", "tfoot", "th",
        "thead", "tr", "ul",
    };

    private static readonly HashSet<string> DroppedTags = new(StringComparer.Ordinal)
    {
        "script", "style", "head", "title", "meta", "link", "img", "svg", "picture", "video", "audio", "iframe", "object",
        "embed", "canvas", "noscript", "template", "input", "select", "textarea", "button", "map", "area", "source", "xml",
    };

    private static readonly Regex MsoListRx = new(@"mso-list\s*:\s*([^;""']*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LevelRx = new(@"level\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex OrderedMarkerRx = new(@"^\(?(?:\d+\.)*(\d{1,9}|[A-Za-z]{1,6})[.)]$", RegexOptions.CultureInvariant);
    private static readonly Regex MsoHeadingRx = new(@"^MsoHeading([1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex DocsListLevelRx = new(@"lst-kix_\w+-(\d+)", RegexOptions.CultureInvariant);

    private sealed class Converter
    {
        private readonly List<OutLine> _lines = new();
        private readonly List<ListCtx> _lists = new();
        private readonly List<ItemCtx> _items = new();
        private readonly ListTracker _tracker = new();
        private List<Run> _runs = new();
        private int _quoteDepth, _headingLevel, _flatten, _dropped;

        public void WalkChildren(Node n, Style st)
        {
            foreach (var child in n.Children) Visit(child, st);
        }

        private void Visit(Node n, Style st)
        {
            if (n.Name == "#text") { AddText(n.Text ?? "", st); return; }
            if (n.Name == "#cond")
            {
                // Word's fallback content (list markers, duplicate breaks, image fallbacks) is never shown.
                if (n.Text != null && n.Text.Contains("vml", StringComparison.OrdinalIgnoreCase)) _dropped++;
                return;
            }
            if (IsDropped(n)) { _dropped++; return; }
            if (IsMsoListIgnore(n)) return;

            var s = ApplyStyle(st, n);
            switch (n.Name)
            {
                case "br": BreakLine(); return;
                case "hr": Rule(); return;
                case "pre": Pre(n, s); return;
                case "ul" or "ol" or "menu": List(n, s); return;
                case "li": Item(n, s); return;
                case "blockquote": Quote(n, s); return;
                case "tr": Row(n, s); return;
                case "p": Paragraph(n, s); return;
            }
            if (IsHeading(n.Name)) { Heading(n, s, n.Name[1] - '0'); return; }

            if (BlockTags.Contains(n.Name))
            {
                if (string.Equals(n.Attr("role"), "heading", StringComparison.OrdinalIgnoreCase)) { Paragraph(n, s); return; }
                BlockBoundary();
                WalkChildren(n, s);
                BlockBoundary();
            }
            else
            {
                WalkChildren(n, s);
            }
        }

        // ---------------- blocks

        private void Paragraph(Node n, Style s)
        {
            int heading = HeadingLevelOf(n);
            if (heading > 0) { Heading(n, s, heading); return; }
            if (_flatten > 0) { AddSpace(); WalkChildren(n, s); AddSpace(); return; }

            if (WordListInfo(n) is { } word) { WordItem(n, s, word.Level, word.Marker); return; }

            bool quote = HasClass(n, "MsoQuote") || HasClass(n, "MsoIntenseQuote");
            Flush();
            if (quote) _quoteDepth++;
            int linesBefore = _lines.Count, droppedBefore = _dropped;
            WalkChildren(n, s);
            bool hadText = HasContent();
            Flush();
            // An explicitly empty paragraph (<p><br></p>, <p>&nbsp;</p>, Word's <o:p>&nbsp;</o:p>) is a blank line.
            if (!hadText && _lines.Count == linesBefore && _dropped == droppedBefore) EmitBlank();
            if (quote) _quoteDepth--;
        }

        private void Heading(Node n, Style s, int level)
        {
            if (_flatten > 0) { AddSpace(); WalkChildren(n, s); AddSpace(); return; }
            Flush();
            int saved = _headingLevel;
            _headingLevel = level;
            WalkChildren(n, s);
            Flush();
            _headingLevel = saved;
        }

        private void Quote(Node n, Style s)
        {
            if (_flatten > 0) { AddSpace(); WalkChildren(n, s); AddSpace(); return; }
            Flush();
            _quoteDepth++;
            WalkChildren(n, s);
            Flush();
            _quoteDepth--;
        }

        private void List(Node n, Style s)
        {
            if (_flatten > 0) { AddSpace(); WalkChildren(n, s); AddSpace(); return; }
            Flush();
            bool ordered = n.Name == "ol";
            int start = 1;
            if (ordered && int.TryParse(n.Attr("start"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var st) && st >= 0 && st < 1_000_000_000)
                start = st;
            int? hint = null;
            var docsLevel = DocsListLevelRx.Match(n.Attr("class") ?? "");
            if (docsLevel.Success && int.TryParse(docsLevel.Groups[1].Value, out var dl)) hint = ListTracker.Clamp(dl + 1);

            _lists.Add(new ListCtx { Ordered = ordered, Next = start, LevelHint = hint });
            WalkChildren(n, s);
            Flush();
            _lists.RemoveAt(_lists.Count - 1);
        }

        private void Item(Node n, Style s)
        {
            if (_flatten > 0) { AddSpace(); WalkChildren(n, s); AddSpace(); return; }
            Flush();
            var list = _lists.Count > 0 ? _lists[^1] : null;
            int level = int.TryParse(n.Attr("aria-level"), out var al) ? al : list?.LevelHint ?? Math.Max(1, _lists.Count);
            level = ListTracker.Clamp(level);
            bool ordered = list?.Ordered ?? false;
            int number = 0;
            bool first = list == null || list.Seen == 0;
            if (list != null)
            {
                list.Seen++;
                if (ordered)
                {
                    number = int.TryParse(n.Attr("value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0 && v < 1_000_000_000
                        ? v
                        : list.Next;
                    list.Next = number + 1;
                }
            }

            bool? check = null;
            if (string.Equals(n.Attr("role"), "checkbox", StringComparison.OrdinalIgnoreCase))
                check = string.Equals(n.Attr("aria-checked"), "true", StringComparison.OrdinalIgnoreCase);
            else if (FindCheckbox(n) is { } box)
                check = box.Attr("checked") != null;

            BeginItem(level, ordered, number, check, first && ordered && number != 1);
            WalkChildren(n, s);
            Flush();
            _items.RemoveAt(_items.Count - 1);
        }

        private void WordItem(Node n, Style s, int level, string? marker)
        {
            Flush();
            level = ListTracker.Clamp(level);
            string mk = CleanMarker(marker ?? "");
            var m = OrderedMarkerRx.Match(mk);
            bool ordered = m.Success;
            int number = 0;
            bool first = false;

            foreach (var key in _tracker.WordCounters.Keys.Where(k => k > level).ToList()) _tracker.WordCounters.Remove(key);
            if (ordered)
            {
                string g = m.Groups[1].Value;
                first = !_tracker.WordCounters.TryGetValue(level, out var next);
                number = char.IsAsciiDigit(g[0]) && int.TryParse(g, out var v) ? v : first ? 1 : next;
                _tracker.WordCounters[level] = number + 1;
            }
            else
            {
                _tracker.WordCounters.Remove(level);
            }

            BeginItem(level, ordered, number, null, first && number != 1);
            WalkChildren(n, s);
            Flush();
            _items.RemoveAt(_items.Count - 1);
        }

        private void BeginItem(int level, bool ordered, int number, bool? check, bool firstNotOne)
        {
            string bullet = ordered ? number.ToString(CultureInfo.InvariantCulture) + ". " : "- ";
            string marker = bullet + (check is null ? "" : check.Value ? "[x] " : "[ ] ");
            int indent = _tracker.IndentFor(level);
            _tracker.Record(level, indent, bullet.Length);
            _items.Add(new ItemCtx { Indent = indent, Width = bullet.Length, Marker = marker, FirstNotOne = firstNotOne });
        }

        private void Pre(Node n, Style s)
        {
            var sb = new StringBuilder();
            RawText(n, sb);
            if (_flatten > 0)
            {
                AddText(sb.ToString(), s with { Fmt = s.Fmt | Fmt.Code });
                return;
            }
            Flush();
            string text = RemoveZeroWidth(sb.ToString().Replace("\r\n", "\n").Replace('\r', '\n').Replace(' ', ' '));
            if (text.StartsWith('\n')) text = text[1..];
            text = text.TrimEnd();
            if (text.Trim().Length == 0) return;

            int ticks = LongestRun(text, '`');
            string fence = new('`', Math.Max(3, ticks + 1));
            string prefix = QuotePrefix() + (_items.Count > 0 ? new string(' ', _items[^1].Indent + _items[^1].Width) : "");
            _lines.Add(new OutLine(prefix + fence, LineKind.Code));
            foreach (var line in text.Split('\n')) _lines.Add(new OutLine(prefix + line, LineKind.Code));
            _lines.Add(new OutLine(prefix + fence, LineKind.Code));
            if (_items.Count == 0) _tracker.Reset();
        }

        private void Row(Node n, Style s)
        {
            if (_flatten > 0) { AddSpace(); WalkChildren(n, s); AddSpace(); return; }
            Flush();
            var cells = new List<string>();
            foreach (var child in n.Children)
            {
                if (child.Name is "td" or "th" && !IsDropped(child))
                    cells.Add(RenderIsolated(child, ApplyStyle(s, child)));
            }
            while (cells.Count > 0 && cells[^1].Length == 0) cells.RemoveAt(cells.Count - 1);
            if (cells.Count == 0) return;
            EmitContent(string.Join(" | ", cells));
        }

        private string RenderIsolated(Node n, Style s)
        {
            var saved = _runs;
            _runs = new List<Run>();
            _flatten++;
            try
            {
                WalkChildren(n, s);
                return Inline.Render(_runs, false).Trim();
            }
            finally
            {
                _flatten--;
                _runs = saved;
            }
        }

        private void Rule()
        {
            if (_flatten > 0) { AddSpace(); return; }
            Flush();
            _lines.Add(new OutLine(QuotePrefix() + "---", LineKind.Rule));
            if (_items.Count == 0) _tracker.Reset();
        }

        private void BreakLine()
        {
            if (_flatten > 0) { AddSpace(); return; }
            if (HasContent()) Flush();
            else { _runs.Clear(); EmitBlank(); }
        }

        private void BlockBoundary()
        {
            if (_flatten > 0) AddSpace();
            else Flush();
        }

        private void EmitBlank()
        {
            if (_flatten > 0) { AddSpace(); return; }
            if (_items.Count > 0) return;
            if (_quoteDepth > 0) _lines.Add(new OutLine(QuotePrefix().TrimEnd(), LineKind.Quote));
            else _lines.Add(new OutLine("", LineKind.Blank));
        }

        // ---------------- inline

        private void AddSpace() => AddText(" ", default);

        private void AddText(string raw, Style st)
        {
            string t = NormalizeWhitespace(raw);
            if (t.Length == 0) return;
            if (t[0] == ' ' && EndsWithSpaceOrEmpty())
            {
                t = t.TrimStart(' ');
                if (t.Length == 0) return;
            }
            var f = st.Fmt;
            if (_headingLevel > 0) f &= ~Fmt.Bold;          // headings are already bold
            if (st.Link != null) f &= ~Fmt.Underline;        // underline in a link is just link styling
            _runs.Add(new Run(t, f, st.Link));
        }

        private bool EndsWithSpaceOrEmpty()
        {
            for (int i = _runs.Count - 1; i >= 0; i--)
            {
                if (_runs[i].Text.Length > 0) return _runs[i].Text[^1] == ' ';
            }
            return true;
        }

        private bool HasContent() => _runs.Any(r => r.Text.AsSpan().Trim().Length > 0);

        private void Flush()
        {
            if (_runs.Count == 0) return;
            string text = Inline.Render(_runs, false).Trim();
            _runs = new List<Run>();
            if (text.Length > 0) EmitContent(text);
        }

        private void EmitContent(string text)
        {
            string q = QuotePrefix();
            if (_headingLevel > 0)
            {
                if (text.EndsWith('#')) text = text[..^1] + "\\#"; // keep a trailing # from reading as a closing sequence
                _lines.Add(new OutLine(q + new string('#', Math.Min(_headingLevel, 3)) + " " + text, LineKind.Heading));
                if (_items.Count == 0) _tracker.Reset();
                return;
            }

            text = EscapeLineStart(text);
            if (_items.Count > 0)
            {
                var item = _items[^1];
                var kind = _quoteDepth > 0 ? LineKind.Quote : LineKind.ListItem;
                if (!item.MarkerEmitted)
                {
                    item.MarkerEmitted = true;
                    _lines.Add(new OutLine(q + new string(' ', item.Indent) + item.Marker + text, kind, item.FirstNotOne));
                }
                else
                {
                    _lines.Add(new OutLine(q + new string(' ', item.Indent + item.Width) + text, kind));
                }
                return;
            }

            _lines.Add(new OutLine(q + text, _quoteDepth > 0 ? LineKind.Quote : LineKind.Paragraph));
            _tracker.Reset();
        }

        private string QuotePrefix() => _quoteDepth == 0 ? "" : string.Concat(Enumerable.Repeat("> ", _quoteDepth));

        public string Finish()
        {
            Flush();
            var output = new List<string>();
            OutLine? prev = null;
            bool pendingBlank = false;
            foreach (var line in _lines)
            {
                if (line.Kind == LineKind.Blank)
                {
                    if (prev != null) pendingBlank = true;
                    continue;
                }
                if (prev is { } p && (pendingBlank || NeedsBlank(p, line))) output.Add("");
                output.Add(line.Kind == LineKind.Code ? line.Text : line.Text.TrimEnd());
                pendingBlank = false;
                prev = line;
            }
            return string.Join("\n", output);
        }

        /// <summary>Blank lines Markdown needs so a line is not swallowed as a lazy continuation or setext underline.</summary>
        private static bool NeedsBlank(OutLine prev, OutLine cur) => cur.Kind switch
        {
            LineKind.Paragraph => prev.Kind is LineKind.ListItem or LineKind.Quote,
            LineKind.Rule => prev.Kind == LineKind.Paragraph,
            LineKind.ListItem => cur.FirstNotOne && prev.Kind is LineKind.Paragraph or LineKind.Quote or LineKind.ListItem,
            _ => false,
        };
    }

    // ---------------------------------------------------------------- element helpers

    private static Style ApplyStyle(Style st, Node n)
    {
        var f = st.Fmt;
        var link = st.Link;
        switch (n.Name)
        {
            case "b" or "strong": f |= Fmt.Bold; break;
            case "i" or "em": f |= Fmt.Italic; break;
            case "u" or "ins": f |= Fmt.Underline; break;
            case "s" or "strike" or "del": f |= Fmt.Strike; break;
            case "code" or "tt" or "kbd" or "samp": f |= Fmt.Code; break;
            case "a":
                var href = n.Attr("href");
                if (href != null) link = ResolveHref(href);
                break;
        }

        var style = n.Attr("style");
        if (!string.IsNullOrEmpty(style))
        {
            foreach (var decl in style.Split(';'))
            {
                int colon = decl.IndexOf(':');
                if (colon <= 0) continue;
                string prop = decl[..colon].Trim().ToLowerInvariant();
                string val = decl[(colon + 1)..].Replace("!important", "", StringComparison.OrdinalIgnoreCase).Trim().ToLowerInvariant();
                switch (prop)
                {
                    case "font-weight":
                        if (val is "bold" or "bolder") f |= Fmt.Bold;
                        else if (val is "normal" or "lighter") f &= ~Fmt.Bold;
                        else if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)) f = w >= 600 ? f | Fmt.Bold : f & ~Fmt.Bold;
                        break;
                    case "font-style":
                        if (val.StartsWith("italic", StringComparison.Ordinal) || val.StartsWith("oblique", StringComparison.Ordinal)) f |= Fmt.Italic;
                        else if (val == "normal") f &= ~Fmt.Italic;
                        break;
                    case "text-decoration" or "text-decoration-line":
                        if (val.Contains("underline", StringComparison.Ordinal)) f |= Fmt.Underline;
                        if (val.Contains("line-through", StringComparison.Ordinal)) f |= Fmt.Strike;
                        break;
                    case "font-family":
                        if (IsMonospace(val)) f |= Fmt.Code;
                        break;
                }
            }
        }
        return new Style(f, link);
    }

    private static readonly Regex MonoRx = new(
        @"(^|[\s-])mono(space)?($|[\s-])|sfmono|consolas|courier|cascadia|menlo|monaco|inconsolata|lucida console|lucida sans typewriter|(^|\s)code($|\s)",
        RegexOptions.CultureInvariant);

    private static bool IsMonospace(string fontFamily)
    {
        string first = fontFamily.Split(',')[0].Trim().Trim('"', '\'').Trim();
        return first.Length > 0 && MonoRx.IsMatch(first);
    }

    private static string? ResolveHref(string href)
    {
        string url = RemoveZeroWidth(href).Trim();
        if (url.Length == 0) return null;
        url = UnwrapGoogleRedirect(url);
        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            ? url
            : null;
    }

    private static readonly Regex GoogleRedirectRx = new(@"^https?://(www\.)?google\.[a-z.]+/url\?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string UnwrapGoogleRedirect(string url)
    {
        var m = GoogleRedirectRx.Match(url);
        if (!m.Success) return url;
        foreach (var part in url[m.Length..].Split('&'))
        {
            if (part.StartsWith("q=", StringComparison.Ordinal) || part.StartsWith("url=", StringComparison.Ordinal))
            {
                string real = WebUtility.UrlDecode(part[(part.IndexOf('=') + 1)..]);
                if (real.Length > 0) return real;
            }
        }
        return url;
    }

    private static bool IsDropped(Node n)
    {
        if (DroppedTags.Contains(n.Name)) return true;
        if (n.Name.StartsWith("v:", StringComparison.Ordinal) || n.Name.StartsWith("w:", StringComparison.Ordinal)) return true;
        var style = n.Attr("style");
        if (style == null) return false;
        string compact = style.Replace(" ", "").ToLowerInvariant();
        return compact.Contains("display:none", StringComparison.Ordinal) || compact.Contains("mso-hide:all", StringComparison.Ordinal);
    }

    private static bool IsMsoListIgnore(Node n)
    {
        var style = n.Attr("style");
        return style != null && style.Replace(" ", "").Contains("mso-list:ignore", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> Classes(Node n) =>
        (n.Attr("class") ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static bool HasClass(Node n, string cls) =>
        Classes(n).Any(c => c.Equals(cls, StringComparison.OrdinalIgnoreCase));

    private static int HeadingLevelOf(Node n)
    {
        if (string.Equals(n.Attr("role"), "heading", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(n.Attr("aria-level"), out var lvl) ? Math.Clamp(lvl, 1, 6) : 2;
        foreach (var c in Classes(n))
        {
            if (c.Equals("MsoTitle", StringComparison.OrdinalIgnoreCase)) return 1;
            var m = MsoHeadingRx.Match(c);
            if (m.Success) return Math.Min(6, m.Groups[1].Value[0] - '0');
        }
        return 0;
    }

    /// <summary>Word list paragraphs: level from "mso-list:l0 level2 lfo1", marker from the supportLists block.</summary>
    private static (int Level, string? Marker)? WordListInfo(Node n)
    {
        var m = MsoListRx.Match(n.Attr("style") ?? "");
        bool listClass = Classes(n).Any(c => c.StartsWith("MsoListParagraph", StringComparison.OrdinalIgnoreCase));
        int level = 1;
        if (m.Success)
        {
            string v = m.Groups[1].Value;
            if (v.Contains("none", StringComparison.OrdinalIgnoreCase) || v.Contains("ignore", StringComparison.OrdinalIgnoreCase)) return null;
            var lm = LevelRx.Match(v);
            if (lm.Success && int.TryParse(lm.Groups[1].Value, out var lv)) level = lv;
        }
        else if (!listClass)
        {
            return null;
        }
        string? marker = FindWordMarker(n);
        if (marker == null && !m.Success) return null; // an indented MsoListParagraph that is not actually a list item
        return (level, marker);
    }

    private static string? FindWordMarker(Node n)
    {
        foreach (var c in n.Children)
        {
            if (c.Name == "#text") continue;
            if (c.Name == "#cond" && c.Text != null && c.Text.Contains("supportLists", StringComparison.OrdinalIgnoreCase)) return InnerText(c);
            if (c.Name != "#cond" && IsMsoListIgnore(c)) return InnerText(c);
            if (c.Name is "ul" or "ol" or "p") continue;
            var inner = FindWordMarker(c);
            if (inner != null) return inner;
        }
        return null;
    }

    private static string InnerText(Node n)
    {
        var sb = new StringBuilder();
        void Walk(Node x)
        {
            if (x.Name == "#text") { sb.Append(x.Text); return; }
            foreach (var c in x.Children) Walk(c);
        }
        Walk(n);
        return sb.ToString();
    }

    private static string CleanMarker(string marker) =>
        RemoveZeroWidth(marker).Replace(' ', ' ').Trim();

    private static Node? FindCheckbox(Node n)
    {
        foreach (var c in n.Children)
        {
            if (c.Name is "ul" or "ol" or "#text") continue;
            if (c.Name == "input" && string.Equals(c.Attr("type"), "checkbox", StringComparison.OrdinalIgnoreCase)) return c;
            var inner = FindCheckbox(c);
            if (inner != null) return inner;
        }
        return null;
    }

    private static void RawText(Node n, StringBuilder sb)
    {
        foreach (var c in n.Children)
        {
            if (c.Name == "#text") { sb.Append(c.Text); continue; }
            if (c.Name == "#cond" || IsDropped(c)) continue;
            if (c.Name == "br") { sb.Append('\n'); continue; }
            bool block = BlockTags.Contains(c.Name);
            if (block && sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
            RawText(c, sb);
            if (block && sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
        }
    }

    // ---------------------------------------------------------------- text helpers

    private static bool IsZeroWidth(char c) => c is '​' or '‌' or '‍' or '⁠' or '﻿';

    private static string RemoveZeroWidth(string s)
    {
        if (!s.Any(IsZeroWidth)) return s;
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) if (!IsZeroWidth(c)) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>Drops zero-width characters, turns non-breaking spaces into spaces and collapses whitespace runs.</summary>
    private static string NormalizeWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (IsZeroWidth(c)) continue;
            if (c is ' ' or '\t' or '\n' or '\r' or '\f' or ' ' or ' ')
            {
                if (sb.Length == 0 || sb[^1] != ' ') sb.Append(' ');
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static int LongestRun(string s, char ch)
    {
        int best = 0, cur = 0;
        foreach (char c in s)
        {
            cur = c == ch ? cur + 1 : 0;
            best = Math.Max(best, cur);
        }
        return best;
    }

    private static readonly Regex OrderedStartRx = new(@"^(\d{1,9})([.)])(?=\s|$)", RegexOptions.CultureInvariant);

    /// <summary>Escapes what would make a plain line start a heading, list, quote, fence, or thematic break.</summary>
    private static string EscapeLineStart(string text)
    {
        if (text.Length == 0) return text;
        char c = text[0];
        if (c is '#' or '>') return "\\" + text;
        if (c is '-' or '+' && (text.Length == 1 || text[1] == ' ')) return "\\" + text;
        if (text.StartsWith("~~~", StringComparison.Ordinal)) return "\\" + text;
        if (text.All(ch => ch is '-' or '=' or ' ')) return "\\" + text;
        var m = OrderedStartRx.Match(text);
        if (m.Success) return text.Insert(m.Groups[2].Index, "\\");
        return text;
    }

    // ---------------------------------------------------------------- inline rendering

    private static class Inline
    {
        private static readonly Fmt[] Order = [Fmt.Bold, Fmt.Italic, Fmt.Underline, Fmt.Strike];
        private const Fmt Markable = Fmt.Bold | Fmt.Italic | Fmt.Underline | Fmt.Strike;
        private static readonly Regex EntityRx = new(@"\G(?:[A-Za-z][A-Za-z0-9]{1,31}|#[0-9]{1,7}|#[xX][0-9A-Fa-f]{1,6});", RegexOptions.CultureInvariant);

        private readonly record struct Piece(string Lead, string Core, string Trail, Fmt Fmt);

        private static string Marker(Fmt f) => f switch
        {
            Fmt.Bold => "**",
            Fmt.Italic => "*",
            Fmt.Underline => "++",
            _ => "~~",
        };

        public static string Render(List<Run> input, bool escapeAllBrackets)
        {
            var runs = Normalize(input);
            if (runs.Count == 0) return "";
            string global = string.Concat(runs.Select(r => r.Text));
            var brackets = escapeAllBrackets ? null : LinkBrackets(global);

            var pieces = new List<Piece>();
            int offset = 0;
            for (int i = 0; i < runs.Count;)
            {
                var r = runs[i];
                if (r.Link == null)
                {
                    pieces.Add(TextPiece(r, global, offset, brackets));
                    offset += r.Text.Length;
                    i++;
                    continue;
                }
                var group = new List<Run>();
                while (i < runs.Count && runs[i].Link == r.Link)
                {
                    group.Add(runs[i]);
                    offset += runs[i].Text.Length;
                    i++;
                }
                pieces.Add(LinkPiece(group, r.Link));
            }
            return Assemble(pieces);
        }

        /// <summary>Whitespace-only runs take the previous run's formatting, then identical neighbours merge.</summary>
        private static List<Run> Normalize(List<Run> input)
        {
            var list = new List<Run>();
            foreach (var original in input)
            {
                var r = original;
                if (r.Text.Length == 0) continue;
                if (r.Text.Trim().Length == 0 && list.Count > 0 && list[^1].Link == r.Link) r = r with { Fmt = list[^1].Fmt };
                if (list.Count > 0 && list[^1].Fmt == r.Fmt && list[^1].Link == r.Link)
                {
                    string joined = list[^1].Text + r.Text;
                    list[^1] = list[^1] with { Text = joined.Replace("  ", " ") };
                }
                else
                {
                    list.Add(r);
                }
            }
            return list;
        }

        private static (int Lead, int End) Bounds(string t)
        {
            int a = 0;
            while (a < t.Length && t[a] == ' ') a++;
            int b = t.Length;
            while (b > a && t[b - 1] == ' ') b--;
            return (a, b);
        }

        private static Piece TextPiece(Run r, string global, int offset, HashSet<int>? brackets)
        {
            var (a, b) = Bounds(r.Text);
            string core = a == b
                ? ""
                : (r.Fmt & Fmt.Code) != 0 ? CodeSpan(r.Text[a..b]) : Escape(global, offset + a, b - a, brackets);
            return new Piece(r.Text[..a], core, r.Text[b..], r.Fmt & Markable);
        }

        private static Piece LinkPiece(List<Run> group, string url)
        {
            var common = Markable;
            bool any = false;
            foreach (var r in group)
            {
                if (r.Text.Trim().Length == 0) continue;
                common &= r.Fmt;
                any = true;
            }
            if (!any) common = Fmt.None;

            string plain = string.Concat(group.Select(r => r.Text));
            var (a, b) = Bounds(plain);
            var inner = group.Select(r => r with { Fmt = r.Fmt & ~common, Link = null }).ToList();
            string md = Render(inner, true).Trim();
            if (md.Length == 0) return new Piece(plain, "", "", Fmt.None);

            string text = plain[a..b];
            string core = text == url || text + "/" == url
                ? text
                : "[" + md + "](" + FormatUrl(url) + ")";
            return new Piece(plain[..a], core, plain[b..], common);
        }

        private static string FormatUrl(string url)
        {
            if (url.IndexOfAny([' ', '(', ')', '<', '>']) < 0) return url;
            return "<" + url.Replace("<", "%3C").Replace(">", "%3E") + ">";
        }

        /// <summary>Opens and closes emphasis markers as formatting changes, keeping spaces outside the markers.</summary>
        private static string Assemble(List<Piece> pieces)
        {
            var sb = new StringBuilder();
            var open = new List<Fmt>();
            bool pendingSpace = false;
            foreach (var p in pieces)
            {
                if (p.Core.Length == 0)
                {
                    pendingSpace |= p.Lead.Length + p.Trail.Length > 0;
                    continue;
                }
                pendingSpace |= p.Lead.Length > 0;
                var desired = Order.Where(f => (p.Fmt & f) != 0).ToList();
                int common = 0;
                while (common < open.Count && common < desired.Count && open[common] == desired[common]) common++;
                for (int k = open.Count - 1; k >= common; k--) sb.Append(Marker(open[k]));
                open.RemoveRange(common, open.Count - common);
                if (pendingSpace) sb.Append(' ');
                pendingSpace = false;
                for (int k = common; k < desired.Count; k++)
                {
                    sb.Append(Marker(desired[k]));
                    open.Add(desired[k]);
                }
                sb.Append(p.Core);
                pendingSpace = p.Trail.Length > 0;
            }
            for (int k = open.Count - 1; k >= 0; k--) sb.Append(Marker(open[k]));
            if (pendingSpace) sb.Append(' ');
            return sb.ToString();
        }

        private static string CodeSpan(string code)
        {
            string fence = new('`', LongestRun(code, '`') + 1);
            bool pad = code.StartsWith('`') || code.EndsWith('`');
            return pad ? fence + " " + code + " " + fence : fence + code + fence;
        }

        /// <summary>Positions of [ and ] that would otherwise form a link ("[x](...)" or "[x][...]").</summary>
        private static HashSet<int> LinkBrackets(string g)
        {
            var set = new HashSet<int>();
            var stack = new Stack<int>();
            for (int k = 0; k < g.Length; k++)
            {
                if (g[k] == '[') stack.Push(k);
                else if (g[k] == ']' && stack.Count > 0)
                {
                    int o = stack.Pop();
                    if (k + 1 < g.Length && g[k + 1] is '(' or '[')
                    {
                        set.Add(o);
                        set.Add(k);
                    }
                }
            }
            return set;
        }

        private static string Escape(string g, int start, int length, HashSet<int>? brackets)
        {
            var sb = new StringBuilder(length + 8);
            for (int k = start; k < start + length; k++)
            {
                char c = g[k];
                switch (c)
                {
                    case '\\' or '`' or '*':
                        sb.Append('\\').Append(c);
                        break;
                    case '_':
                        bool inWord = k > 0 && char.IsLetterOrDigit(g[k - 1]) && k + 1 < g.Length && char.IsLetterOrDigit(g[k + 1]);
                        if (!inWord) sb.Append('\\');
                        sb.Append(c);
                        break;
                    case '[' or ']':
                        if (brackets == null || brackets.Contains(k)) sb.Append('\\');
                        sb.Append(c);
                        break;
                    case '<':
                        if (k + 1 < g.Length && (char.IsLetter(g[k + 1]) || g[k + 1] is '/' or '!' or '?')) sb.Append('\\');
                        sb.Append(c);
                        break;
                    case '&':
                        if (EntityRx.IsMatch(g, k + 1)) sb.Append('\\');
                        sb.Append(c);
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
