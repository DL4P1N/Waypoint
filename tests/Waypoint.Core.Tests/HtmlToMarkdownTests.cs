using Waypoint.Core.Notes;

namespace Waypoint.Core.Tests;

public sealed class HtmlToMarkdownTests
{
    // ------------------------------------------------------------ helpers for realistic fixtures

    /// <summary>Wraps a fragment the way Chrome/Edge put it on the Windows clipboard (CF_HTML).</summary>
    private static string CfHtml(string fragment, string before = "", string after = "") =>
        "Version:0.9\r\nStartHTML:0000000105\r\nEndHTML:0000000999\r\nStartFragment:0000000141\r\nEndFragment:0000000963\r\n" +
        "<html>\r\n<body>\r\n" + before + "<!--StartFragment-->" + fragment + "<!--EndFragment-->" + after + "\r\n</body>\r\n</html>";

    /// <summary>A Google Docs text run: every run carries its full style.</summary>
    private static string DocsSpan(string text, string weight = "400", string fontStyle = "normal", string decoration = "none", string font = "Arial,sans-serif", string color = "#000000") =>
        $"<span style=\"font-size:11pt;font-family:{font};color:{color};background-color:transparent;font-weight:{weight};font-style:{fontStyle};" +
        $"font-variant:normal;text-decoration:{decoration};-webkit-text-decoration-skip:none;text-decoration-skip-ink:none;vertical-align:baseline;white-space:pre;white-space:pre-wrap;\">{text}</span>";

    private static string DocsP(string spans) =>
        $"<p dir=\"ltr\" style=\"line-height:1.38;margin-top:0pt;margin-bottom:0pt;\">{spans}</p>";

    private static string DocsLi(int level, string spans, string extra = "") =>
        $"<li dir=\"ltr\" style=\"list-style-type:disc;font-size:11pt;font-family:Arial,sans-serif;color:#000000;background-color:transparent;font-weight:400;font-style:normal;" +
        $"font-variant:normal;text-decoration:none;vertical-align:baseline;white-space:pre;\" aria-level=\"{level}\"{extra}>" +
        $"<p dir=\"ltr\" style=\"line-height:1.38;margin-top:0pt;margin-bottom:0pt;\" role=\"presentation\">{spans}</p></li>";

    private const string DocsUl = "<ul style=\"margin-top:0;margin-bottom:0;padding-inline-start:48px;\">";
    private const string DocsOl = "<ol style=\"margin-top:0;margin-bottom:0;padding-inline-start:48px;\">";

    /// <summary>Google Docs wraps the whole copied fragment in a bold tag that is explicitly not bold.</summary>
    private static string Docs(string body) =>
        CfHtml("<meta charset=\"utf-8\"><b style=\"font-weight:normal;\" id=\"docs-internal-guid-1a2b3c4d-7fff-8a9b-cdef-0123456789ab\">" + body + "</b>");

    private static string Convert(string html) => HtmlToMarkdown.FromClipboard(html);

    // ------------------------------------------------------------ clipboard envelope

    [Fact]
    public void CF_HTML_uses_only_the_fragment_between_the_comments()
    {
        var html = CfHtml("<p>Hello <b>world</b></p>", before: "<p>outside before</p>", after: "<p>outside after</p>");

        Assert.Equal("Hello **world**", Convert(html));
    }

    [Fact]
    public void Plain_html_without_a_header_converts_and_head_content_is_dropped()
    {
        var html = "<html><head><meta charset=utf-8><title>Page title</title><style>p { color: red; }</style></head>" +
                   "<body><script>alert('x')</script><p>Body text</p></body></html>";

        Assert.Equal("Body text", Convert(html));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<html><body><!--StartFragment--><!--EndFragment--></body></html>")]
    [InlineData("<p><img src=\"x.png\"></p><style>.a{}</style>")]
    public void Nothing_usable_returns_empty(string html)
    {
        Assert.Equal("", Convert(html));
    }

    // ------------------------------------------------------------ Google Docs

    [Fact]
    public void Google_Docs_wrapper_is_not_bold_and_styled_spans_become_emphasis()
    {
        var html = Docs(DocsP(
            DocsSpan("Plain then ") + DocsSpan("bold", weight: "700") + DocsSpan(", ") + DocsSpan("italic", fontStyle: "italic") +
            DocsSpan(" and ") + DocsSpan("both", weight: "700", fontStyle: "italic") + DocsSpan(". ") +
            DocsSpan("under", decoration: "underline") + DocsSpan(" ") + DocsSpan("gone", decoration: "line-through") + DocsSpan(".")));

        Assert.Equal("Plain then **bold**, *italic* and ***both***. ++under++ ~~gone~~.", Convert(html));
    }

    [Fact]
    public void Google_Docs_split_runs_with_the_same_formatting_merge()
    {
        var html = Docs(DocsP(DocsSpan("Hello ", weight: "700") + DocsSpan("big ", weight: "700") + DocsSpan("world", weight: "700") + DocsSpan(" again")));

        Assert.Equal("**Hello big world** again", Convert(html));
    }

    [Fact]
    public void Google_Docs_paragraphs_are_lines_and_br_between_blocks_is_one_blank_line()
    {
        var html = Docs(DocsP(DocsSpan("First")) + DocsP(DocsSpan("Second")) + "<br>" + DocsP(DocsSpan("Third")) + "<br><br>" + DocsP(DocsSpan("Fourth")));

        Assert.Equal("First\nSecond\n\nThird\n\nFourth", Convert(html));
    }

    [Fact]
    public void Google_Docs_headings_drop_their_bold()
    {
        var html = Docs(
            "<h1 dir=\"ltr\" style=\"line-height:1.38;margin-top:20pt;margin-bottom:6pt;\">" + DocsSpan("Project", weight: "400") + "</h1>" +
            "<h2 dir=\"ltr\" style=\"line-height:1.38;margin-top:18pt;margin-bottom:6pt;\">" + DocsSpan("Bold heading", weight: "700") + "</h2>" +
            "<h4 dir=\"ltr\">" + DocsSpan("Deep") + "</h4>" +
            "<p dir=\"ltr\" role=\"heading\" aria-level=\"3\">" + DocsSpan("Aria heading") + "</p>" +
            DocsP(DocsSpan("Body")));

        Assert.Equal("# Project\n## Bold heading\n### Deep\n### Aria heading\nBody", Convert(html));
    }

    [Fact]
    public void Google_Docs_nested_bullet_list_uses_aria_level()
    {
        var html = Docs(
            DocsUl + DocsLi(1, DocsSpan("Fruit")) +
            DocsUl + DocsLi(2, DocsSpan("Apple")) + DocsLi(2, DocsSpan("Banana")) +
            DocsUl + DocsLi(3, DocsSpan("Cavendish")) + "</ul></ul>" +
            DocsLi(1, DocsSpan("Vegetables")) + "</ul>" +
            DocsP(DocsSpan("After the list")));

        Assert.Equal("- Fruit\n  - Apple\n  - Banana\n    - Cavendish\n- Vegetables\n\nAfter the list", Convert(html));
    }

    [Fact]
    public void Google_Docs_flat_lists_with_aria_level_and_no_structural_nesting()
    {
        // Older Docs output: one <ul> per level run, nesting only in aria-level / class lst-kix_...-N.
        var html = Docs(
            "<ul class=\"lst-kix_abc123-0 start\">" + DocsLi(1, DocsSpan("Top")) + "</ul>" +
            "<ul class=\"lst-kix_abc123-1 start\"><li style=\"margin-left: 36pt\">" + DocsSpan("Child") + "</li></ul>" +
            "<ul class=\"lst-kix_abc123-0\">" + DocsLi(1, DocsSpan("Top again")) + "</ul>");

        Assert.Equal("- Top\n  - Child\n- Top again", Convert(html));
    }

    [Fact]
    public void Google_Docs_numbered_list_with_a_nested_list_indents_by_the_marker_width()
    {
        var html = Docs(
            DocsOl + DocsLi(1, DocsSpan("First")) + DocsLi(1, DocsSpan("Second")) +
            DocsOl + DocsLi(2, DocsSpan("Sub one")) + "</ol>" +
            DocsLi(1, DocsSpan("Third")) + "</ol>");

        Assert.Equal("1. First\n2. Second\n   1. Sub one\n3. Third", Convert(html));
    }

    [Fact]
    public void Google_Docs_checklist()
    {
        const string checkbox = "<img src=\"data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABIAAAASCAYAAABWzo5XAAAAAXNSR0IArs4c6QAAAA==\" width=\"17.6px\" height=\"17.6px\" alt=\"unchecked\" aria-roledescription=\"checkbox\" style=\"margin-right:3px;\" />";
        var html = Docs(
            DocsUl +
            DocsLi(1, DocsSpan("Buy milk"), " role=\"checkbox\" aria-checked=\"false\"").Replace("<p dir", checkbox + "<p dir") +
            DocsLi(1, DocsSpan("Call mom"), " role=\"checkbox\" aria-checked=\"true\"").Replace("<p dir", checkbox + "<p dir") +
            "</ul>");

        Assert.Equal("- [ ] Buy milk\n- [x] Call mom", Convert(html));
    }

    [Fact]
    public void Google_Docs_link_is_unwrapped_from_the_google_redirect()
    {
        var html = Docs(DocsP(
            DocsSpan("Read ") +
            "<a href=\"https://www.google.com/url?q=https://example.com/docs?id%3D42%26lang%3Den&amp;sa=D&amp;source=editors&amp;ust=1731500000000000&amp;usg=AOvVaw0abcDEF\" style=\"text-decoration:none;\">" +
            DocsSpan("the docs", decoration: "underline", color: "#1155cc") + "</a>" + DocsSpan(" first.")));

        Assert.Equal("Read [the docs](https://example.com/docs?id=42&lang=en) first.", Convert(html));
    }

    [Fact]
    public void Google_Docs_monospace_font_becomes_inline_code()
    {
        var html = Docs(DocsP(DocsSpan("Run ") + DocsSpan("npm test", font: "'Courier New',monospace") + DocsSpan(" now")));

        Assert.Equal("Run `npm test` now", Convert(html));
    }

    // ------------------------------------------------------------ Microsoft Word

    private const string WordHeader =
        "Version:1.0\r\nStartHTML:0000000105\r\nEndHTML:0000009999\r\nStartFragment:0000001234\r\nEndFragment:0000009876\r\n" +
        "<html xmlns:v=\"urn:schemas-microsoft-com:vml\"\r\nxmlns:o=\"urn:schemas-microsoft-com:office:office\"\r\n" +
        "xmlns:w=\"urn:schemas-microsoft-com:office:word\"\r\nxmlns:m=\"http://schemas.microsoft.com/office/2004/12/omml\"\r\n" +
        "xmlns=\"http://www.w3.org/TR/REC-html40\">\r\n\r\n<head>\r\n<meta http-equiv=Content-Type content=\"text/html; charset=utf-8\">\r\n" +
        "<meta name=ProgId content=Word.Document>\r\n<meta name=Generator content=\"Microsoft Word 15\">\r\n" +
        "<!--[if gte mso 9]><xml>\r\n <o:OfficeDocumentSettings>\r\n  <o:AllowPNG/>\r\n </o:OfficeDocumentSettings>\r\n</xml><![endif]-->\r\n" +
        "<style>\r\n<!--\r\np.MsoNormal, li.MsoNormal, div.MsoNormal\r\n\t{margin:0in;\r\n\tfont-size:11.0pt;\r\n\tfont-family:\"Calibri\",sans-serif;}\r\n-->\r\n</style>\r\n" +
        "</head>\r\n\r\n<body lang=EN-US style='tab-interval:.5in;word-wrap:break-word'>\r\n<!--StartFragment-->\r\n\r\n";

    private const string WordFooter = "\r\n\r\n<!--EndFragment-->\r\n</body>\r\n\r\n</html>\r\n";

    private static string WordBullet(string symbolFont, string symbol, int level, string ind, string cls, string text) =>
        $"<p class={cls} style='margin-left:{ind};mso-add-space:auto;\r\ntext-indent:-.25in;mso-list:l0 level{level} lfo1'><![if !supportLists]><span\r\n" +
        $"style='font-family:{symbolFont};mso-fareast-font-family:{symbolFont};mso-bidi-font-family:{symbolFont}'><span\r\n" +
        $"style='mso-list:Ignore'>{symbol}<span style='font:7.0pt \"Times New Roman\"'>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;\r\n" +
        $"</span></span></span><![endif]><span lang=EN-US>{text}<o:p></o:p></span></p>\r\n\r\n";

    private static string WordNumber(string number, int level, string cls, string text) =>
        $"<p class={cls} style='text-indent:-.25in;mso-list:l1 level{level} lfo2'><![if !supportLists]><span\r\n" +
        $"lang=EN-US><span style='mso-list:Ignore'>{number}<span style='font:7.0pt \"Times New Roman\"'>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;\r\n" +
        $"</span></span></span><![endif]><span lang=EN-US>{text}<o:p></o:p></span></p>\r\n\r\n";

    [Fact]
    public void Word_document_with_headings_lists_formatting_and_images()
    {
        var html = WordHeader +
            "<p class=MsoTitle><span lang=EN-US>Weekly Plan<o:p></o:p></span></p>\r\n\r\n" +
            "<h1><span lang=EN-US>Goals<o:p></o:p></span></h1>\r\n\r\n" +
            "<p class=MsoNormal><span lang=EN-US style='font-family:\"Georgia\",serif;color:#C00000'>Finish the\r\n<b>report</b> by <i>Friday</i>.<o:p></o:p></span></p>\r\n\r\n" +
            "<p class=MsoNormal><span lang=EN-US><o:p>&nbsp;</o:p></span></p>\r\n\r\n" +
            WordBullet("Symbol", "·", 1, ".5in", "MsoListParagraphCxSpFirst", "Draft outline") +
            WordBullet("\"Courier New\"", "o", 2, "1.0in", "MsoListParagraphCxSpMiddle", "Intro section") +
            WordBullet("Symbol", "·", 1, ".5in", "MsoListParagraphCxSpLast", "Send to <b>team</b>") +
            "<p class=MsoNormal><span lang=EN-US>Steps:<o:p></o:p></span></p>\r\n\r\n" +
            WordNumber("1.", 1, "MsoListParagraphCxSpFirst", "Open the file") +
            WordNumber("a.", 2, "MsoListParagraphCxSpMiddle", "Check totals") +
            WordNumber("b.", 2, "MsoListParagraphCxSpMiddle", "Fix typos") +
            WordNumber("2.", 1, "MsoListParagraphCxSpLast", "Save it") +
            "<p class=MsoNormal><span lang=EN-US><!--[if gte vml 1]><v:shape id=\"Picture_x0020_1\"\r\n o:spid=\"_x0000_i1025\" type=\"#_x0000_t75\" style='width:100pt;height:50pt;visibility:visible'>\r\n" +
            " <v:imagedata src=\"file:///C:/Users/x/AppData/Local/Temp/msohtmlclip1/01/clip_image001.png\" o:title=\"\"/>\r\n</v:shape><![endif]--><![if !vml]><img\r\n" +
            "width=133 height=67 src=\"file:///C:/Users/x/AppData/Local/Temp/msohtmlclip1/01/clip_image002.png\" v:shapes=\"Picture_x0020_1\"><![endif]><o:p></o:p></span></p>\r\n\r\n" +
            "<p class=MsoNormal><v:shape id=\"_x0000_s1026\" type=\"#_x0000_t75\"><v:imagedata src=\"clip_image003.png\" o:title=\"\"/></v:shape><span lang=EN-US style='font-size:9.0pt;color:#7F7F7F'>Figure caption<o:p></o:p></span></p>\r\n\r\n" +
            "<h2><span lang=EN-US>Notes<o:p></o:p></span></h2>\r\n\r\n" +
            "<p class=MsoNormal><span lang=EN-US>Use <span style='font-family:Consolas'>git status</span> often.<o:p></o:p></span></p>" +
            WordFooter;

        Assert.Equal(
            "# Weekly Plan\n" +
            "# Goals\n" +
            "Finish the **report** by *Friday*.\n" +
            "\n" +
            "- Draft outline\n" +
            "  - Intro section\n" +
            "- Send to **team**\n" +
            "\n" +
            "Steps:\n" +
            "1. Open the file\n" +
            "   1. Check totals\n" +
            "   2. Fix typos\n" +
            "2. Save it\n" +
            "\n" +
            "Figure caption\n" +
            "## Notes\n" +
            "Use `git status` often.",
            Convert(html));
    }

    [Fact]
    public void Word_heading_paragraph_classes()
    {
        var html = WordHeader +
            "<p class=MsoHeading2><span lang=EN-US>Section<o:p></o:p></span></p>\r\n" +
            "<p class=MsoHeading3><b><span lang=EN-US>Sub<o:p></o:p></span></b></p>\r\n" +
            "<p class=MsoNormal><span lang=EN-US>Text<o:p></o:p></span></p>" + WordFooter;

        Assert.Equal("## Section\n### Sub\nText", Convert(html));
    }

    [Fact]
    public void Word_numbered_list_that_starts_after_one_gets_a_blank_line_after_a_paragraph()
    {
        var html = WordHeader +
            "<p class=MsoNormal><span lang=EN-US>Continuing:<o:p></o:p></span></p>\r\n" +
            WordNumber("3.", 1, "MsoListParagraphCxSpFirst", "Third") +
            WordNumber("4.", 1, "MsoListParagraphCxSpLast", "Fourth") + WordFooter;

        Assert.Equal("Continuing:\n\n3. Third\n4. Fourth", Convert(html));
    }

    // ------------------------------------------------------------ web pages

    [Fact]
    public void Web_page_snippet()
    {
        var html = CfHtml(
            "<h2 id=\"setup\" style=\"font-family: -apple-system, sans-serif;\">Setup &amp; install</h2>\n" +
            "<p>Run the <code>dotnet build</code> command &mdash; it&#39;s fast. See <a href=\"https://learn.microsoft.com/dotnet/\">the docs</a>\n" +
            "   or <a href=\"javascript:void(0)\">click here</a>.</p>\n" +
            "<ul>\n  <li>First\n    <ul>\n      <li>Nested <em>one</em></li>\n    </ul>\n  </li>\n  <li>Second</li>\n</ul>\n" +
            "<ol start=\"3\">\n  <li>Three</li>\n  <li>Four</li>\n</ol>\n" +
            "<pre><code class=\"language-csharp\">var x = 1;\nif (x &lt; 2) { Console.WriteLine(\"*hi*\"); }\n\n// done\n</code></pre>\n" +
            "<blockquote><p>Quoted line one</p><p>Quoted <strong>line</strong> two</p></blockquote>\n" +
            "<p>After the quote</p>\n" +
            "<table><thead><tr><th>Name</th><th>Qty</th></tr></thead><tbody><tr><td>Apples</td><td>3</td></tr><tr><td><p>Pears</p></td><td>10</td></tr></tbody></table>\n" +
            "<hr>\n" +
            "<p>Footer &copy; 2026 &ldquo;quoted&rdquo; &#x2014; <a href=\"/relative/page\">relative</a> <a href=\"#top\">top</a></p>");

        Assert.Equal(
            "## Setup & install\n" +
            "Run the `dotnet build` command \u2014 it's fast. See [the docs](https://learn.microsoft.com/dotnet/) or click here.\n" +
            "- First\n" +
            "  - Nested *one*\n" +
            "- Second\n" +
            "\n" +
            "3. Three\n" +
            "4. Four\n" +
            "```\n" +
            "var x = 1;\n" +
            "if (x < 2) { Console.WriteLine(\"*hi*\"); }\n" +
            "\n" +
            "// done\n" +
            "```\n" +
            "> Quoted line one\n" +
            "> Quoted **line** two\n" +
            "\n" +
            "After the quote\n" +
            "Name | Qty\n" +
            "Apples | 3\n" +
            "Pears | 10\n" +
            "\n" +
            "---\n" +
            "Footer \u00a9 2026 \u201cquoted\u201d \u2014 relative top",
            Convert(html));
    }

    [Fact]
    public void Ordered_list_marker_width_sets_child_indent()
    {
        var html = "<ol start=\"9\"><li>Nine</li><li>Ten<ul><li>child</li></ul></li></ol><ul><li>Dash<ol><li>one</li></ol></li></ul>";

        Assert.Equal("9. Nine\n10. Ten\n    - child\n- Dash\n  1. one", Convert(html));
    }

    [Fact]
    public void Checkbox_inputs_inside_list_items()
    {
        var html = "<ul><li><input type=\"checkbox\" checked disabled> done</li><li><input type=checkbox> todo</li></ul>";

        Assert.Equal("- [x] done\n- [ ] todo", Convert(html));
    }

    [Fact]
    public void Links_bare_urls_mailto_spaces_and_link_underline()
    {
        var html = "<p><a href=\"https://example.com/\">https://example.com</a> " +
                   "<a href=\"mailto:me@example.com\">email me</a> " +
                   "<a href=\"https://example.com/a b(1)\">odd</a> " +
                   "<a href=\"https://a.com\"><u>underlined</u></a> " +
                   "<a href=\"file:///C:/x.txt\">local</a></p>";

        Assert.Equal(
            "https://example.com [email me](mailto:me@example.com) [odd](<https://example.com/a b(1)>) [underlined](https://a.com) local",
            Convert(html));
    }

    [Fact]
    public void Inline_formatting_whitespace_moves_outside_markers()
    {
        Assert.Equal("Say **hello** now", Convert("<p>Say<b> hello </b>now</p>"));
        Assert.Equal("a **b** *c* d", Convert("<p>a <b>b </b><i> c</i> d</p>"));
        Assert.Equal("x y", Convert("<p>x<b>   </b>y</p>"));
    }

    [Fact]
    public void Code_tags_and_backtick_fences()
    {
        Assert.Equal("Press `Ctrl` then ``a`b``", Convert("<p>Press <kbd>Ctrl</kbd> then <tt>a`b</tt></p>"));
        Assert.Equal("````\n```\ncode\n```\n````", Convert("<pre>```\ncode\n```</pre>"));
    }

    [Fact]
    public void Br_separated_lines_and_contenteditable_divs()
    {
        Assert.Equal("line1\nline2\n\nline4", Convert("<div>line1<br>line2<br><br>line4</div>"));
        Assert.Equal("one\n\ntwo", Convert("<div>one</div><div><br></div><div>two</div>"));
    }

    [Fact]
    public void Whitespace_is_collapsed_and_empty_paragraphs_become_single_blank_lines()
    {
        var html = "<p>  Hello\n   world  \t again </p><p>&nbsp;</p><p></p><p><br></p><p>Next&nbsp;&nbsp;line\u200B here</p>\n\n<p>&nbsp;</p>";

        Assert.Equal("Hello world again\n\nNext line here", Convert(html));
    }

    [Fact]
    public void Plain_text_is_escaped_so_it_round_trips()
    {
        var html =
            "<p>5 * 3 = 15</p><p>snake_case and _private</p><p>*literal*</p><p># not a heading</p>" +
            "<p>- not a list</p><p>+ plus</p><p>1. not ordered</p><p>2) nor this</p><p>&gt; not a quote</p>" +
            "<p>a [b](c) and [plain]</p><p>&lt;div&gt; tag and 1 &lt; 2</p><p>---</p><p>===</p>" +
            "<p>back\\slash `tick`</p><p>AT&amp;T &amp;amp;</p>";

        Assert.Equal(
            "5 \\* 3 = 15\n" +
            "snake_case and \\_private\n" +
            "\\*literal\\*\n" +
            "\\# not a heading\n" +
            "\\- not a list\n" +
            "\\+ plus\n" +
            "1\\. not ordered\n" +
            "2\\) nor this\n" +
            "\\> not a quote\n" +
            "a \\[b\\](c) and [plain]\n" +
            "\\<div> tag and 1 < 2\n" +
            "\\---\n" +
            "\\===\n" +
            "back\\\\slash \\`tick\\`\n" +
            "AT&T \\&amp;",
            Convert(html));
    }

    [Fact]
    public void Malformed_html_does_not_throw()
    {
        var html = "<P CLASS=x>Unclosed <B>bold<p>next</p></b></p></p><span lang=EN-US style='color:red'>tail <a href=https://x.com/a>link</a>" +
                   "<div><ul><li>one<li>two</ul></div><table><tr><td>a<td>b<tr><td>c</table><p attr=\"unterminated>broken";

        Assert.Equal("Unclosed **bold**\nnext\ntail [link](https://x.com/a)\n- one\n- two\n\na | b\nc\nbroken", Convert(html));
    }

    [Fact]
    public void Garbage_input_never_throws()
    {
        string[] inputs =
        [
            "<", "<<<>>>", "</p>", "<!--", "<![if", "<a href=", "<p style='", "&#xFFFFFFFF; &bogus; &",
            new string('<', 5000), string.Concat(Enumerable.Repeat("<div><b>", 2000)) + "deep",
            "<ol start=99999999999><li value=-4>x</li></ol>", "<li aria-level=\"999999\">deep item</li>",
        ];
        foreach (var input in inputs)
        {
            var ex = Record.Exception(() => Convert(input));
            Assert.Null(ex);
        }
        Assert.Equal("deep", Convert(string.Concat(Enumerable.Repeat("<div><b style='font-weight:normal'>", 2000)) + "deep"));
    }

    [Fact]
    public void Hr_and_quote_get_blank_lines_only_where_markdown_needs_them()
    {
        Assert.Equal("Para\n\n---\n# Title", Convert("<p>Para</p><hr><h1>Title</h1>"));
        Assert.Equal("# Title\n---", Convert("<h1>Title</h1><hr/>"));
        Assert.Equal("> quoted\n\nplain", Convert("<blockquote>quoted</blockquote>plain"));
    }

    [Fact]
    public void Images_svg_and_hidden_content_are_dropped()
    {
        var html = "<p>Before<img src=\"a.png\" alt=\"alt text\"><svg><text>svg text</text></svg><picture><source srcset=\"x\"><img src=\"y\"></picture>" +
                   "<span style=\"display:none\">hidden</span> after</p>";

        Assert.Equal("Before after", Convert(html));
    }
}
