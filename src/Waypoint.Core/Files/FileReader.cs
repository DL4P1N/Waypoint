using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Waypoint.Core.Files;

/// <summary>
/// What came of reading a dropped file. Problem says why there's no text, and NoText is set when the file
/// opened fine but held no text, as a scanned PDF does.
/// </summary>
public sealed record FileText(string Name, string? Text, string? Problem = null, bool NoText = false);

/// <summary>Pulls the text out of a dropped file. Nothing here ever opens or runs the file itself.</summary>
public static partial class FileReader
{
    public const long MaxBytes = 10 * 1024 * 1024;
    public const int MaxStoredChars = 200_000;

    /// <summary>Text recognition takes a second or two a page, so a long scan stops here rather than tying the app up.</summary>
    public const int MaxScannedPages = 60;

    /// <summary>
    /// The most time spent recognizing scanned pages in one file, counted from the first scanned page, so reading
    /// the pages before it never uses it up. Pages after it are left blank.
    /// </summary>
    public static readonly TimeSpan ScanBudget = TimeSpan.FromMinutes(5);

    private const long MaxUnpackedBytes = 50 * 1024 * 1024;
    private const int BinaryCheckBytes = 8192;
    private const int FirstCustomNumberFormat = 164;
    private const int FirstDateFormat = 14;
    private const int LastDateFormat = 22;
    private const string Works = "PDFs, Word, Excel, PowerPoint, images with text, and plain text files work.";

    private static readonly XNamespace Word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace Sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Drawing = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Presentation = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace Relationships = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>Placeholders on a notes page that hold the slide picture, its number, or a header, not the speaker's words.</summary>
    private static readonly HashSet<string> NotNotes = new(StringComparer.Ordinal) { "sldImg", "sldNum", "hdr", "ftr", "dt" };
    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp" };

    /// <summary>
    /// <paramref name="readImage"/> finds the text in a picture, which takes Windows and so lives in the app.
    /// <paramref name="readScannedPage"/> does the same for one PDF page, numbered from zero, and is only asked
    /// about pages with no text of their own.
    /// </summary>
    public static FileText Read(string path, Func<string, string?>? readImage = null, Func<string, int, string?>? readScannedPage = null, TimeSpan? scanBudget = null)
    {
        var name = Redaction.ApplyToName(Path.GetFileName(path.TrimEnd('\\', '/')));
        if (Directory.Exists(path))
        {
            return new FileText(name, null, $"{name} is a folder. Drop the files inside it instead.");
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxBytes)
            {
                return new FileText(name, null, $"{name} is over {MaxBytes / 1024 / 1024} MB, which is more than I can read.");
            }

            if (info.Length == 0)
            {
                return new FileText(name, null, $"{name} is empty.");
            }

            var type = Path.GetExtension(path).ToLowerInvariant();
            var text = type switch
            {
                ".pdf" => ReadPdf(path, readScannedPage, scanBudget ?? ScanBudget),
                ".docx" => ReadDocx(path),
                ".xlsx" => ReadXlsx(path),
                ".pptx" => ReadPptx(path),
                _ when ImageTypes.Contains(type) => readImage?.Invoke(path) ?? "",
                _ => ReadPlain(path),
            };

            if (text is null)
            {
                return new FileText(name, null, $"I can't read {name}. {Works}");
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return new FileText(name, null, $"I couldn't find any text in {name}, so it may be a scan or a picture. Type what matters from it and I'll take it from there.", NoText: true);
            }

            var clean = Redaction.Apply(text.Trim());
            return new FileText(name, clean.Length > MaxStoredChars ? clean[..MaxStoredChars] : clean);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new FileText(name, null, $"I couldn't open {name}. It may be damaged or password protected.");
        }
    }

    /// <summary>Each page's own text, or, for a scanned page with none, what text recognition finds in it.</summary>
    private static string ReadPdf(string path, Func<string, int, string?>? readScannedPage, TimeSpan budget)
    {
        using var document = PdfDocument.Open(path);
        var pages = new List<string>();
        var scanned = 0;
        System.Diagnostics.Stopwatch? clock = null;
        foreach (var page in document.GetPages())
        {
            var text = ContentOrderTextExtractor.GetText(page);
            if (string.IsNullOrWhiteSpace(text) && readScannedPage is not null && scanned < MaxScannedPages && (clock is null || clock.Elapsed < budget))
            {
                // The budget starts at the first page that needs recognizing, so a slow start never skips them all.
                clock ??= System.Diagnostics.Stopwatch.StartNew();
                scanned++;
                text = ReadScanned(readScannedPage, path, page.Number - 1);
            }

            pages.Add(text);
        }

        return string.Join("\n", pages.Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    /// <summary>A page recognition can't read is left blank, so the rest of the file still comes through.</summary>
    private static string ReadScanned(Func<string, int, string?> readScannedPage, string path, int index)
    {
        try
        {
            return readScannedPage(path, index) ?? "";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return "";
        }
    }

    /// <summary>Null if it isn't text at all, whatever its name says.</summary>
    private static string? ReadPlain(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var hasBom = bytes is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..];
        if (!hasBom && bytes.Take(BinaryCheckBytes).Contains((byte)0))
        {
            return null;
        }

        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string ReadDocx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var document = Load(zip, "word/document.xml") ?? throw new InvalidDataException();
        return string.Join("\n", document.Descendants(Word + "p").Select(paragraph =>
            string.Concat(paragraph.Descendants(Word + "t").Select(run => run.Value))));
    }

    /// <summary>Each slide's text in order, followed by its speaker notes when it has any.</summary>
    private static string ReadPptx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var slides = zip.Entries
            .Where(entry => SlidePath().IsMatch(entry.FullName))
            .OrderBy(entry => int.Parse(SlidePath().Match(entry.FullName).Groups[1].Value, CultureInfo.InvariantCulture))
            .Select(entry => entry.FullName)
            .ToList();

        var parts = new List<string>();
        foreach (var slide in slides)
        {
            if (Load(zip, slide) is { } document)
            {
                parts.AddRange(document.Descendants(Drawing + "t").Select(run => run.Value));
            }

            if (SpeakerNotes(zip, slide) is { Length: > 0 } notes)
            {
                parts.Add($"Speaker notes: {notes}");
            }
        }

        return string.Join("\n", parts);
    }

    /// <summary>The notes page a slide links to, read paragraph by paragraph, without its slide number or picture.</summary>
    private static string? SpeakerNotes(ZipArchive zip, string slide)
    {
        var folder = slide[..(slide.LastIndexOf('/') + 1)];
        var links = Load(zip, $"{folder}_rels/{slide[folder.Length..]}.rels");
        var target = links?.Root?.Elements(Relationships + "Relationship")
            .Where(link => ((string?)link.Attribute("Type"))?.EndsWith("/notesSlide", StringComparison.Ordinal) == true)
            .Select(link => (string?)link.Attribute("Target"))
            .FirstOrDefault(found => !string.IsNullOrEmpty(found));
        if (target is null || Load(zip, PartPath(folder, target)) is not { } notes)
        {
            return null;
        }

        var lines = notes.Descendants(Presentation + "sp")
            .Where(shape => shape.Descendants(Presentation + "ph").FirstOrDefault() is not { } placeholder
                || !NotNotes.Contains((string?)placeholder.Attribute("type") ?? ""))
            .SelectMany(shape => shape.Descendants(Drawing + "p"))
            .Select(paragraph => string.Concat(paragraph.Descendants(Drawing + "t").Select(run => run.Value)).Trim())
            .Where(line => line.Length > 0);
        return string.Join("\n", lines);
    }

    /// <summary>Resolves a link inside an Office file, such as ../notesSlides/notesSlide1.xml, to the part's full name.</summary>
    private static string PartPath(string folder, string target)
    {
        if (target.StartsWith('/'))
        {
            return target.TrimStart('/');
        }

        var parts = folder.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var piece in target.Split('/'))
        {
            if (piece == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            else if (piece is not ("." or ""))
            {
                parts.Add(piece);
            }
        }

        return string.Join('/', parts);
    }

    private static string ReadXlsx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var shared = Load(zip, "xl/sharedStrings.xml")?.Root?.Elements(Sheet + "si")
            .Select(entry => string.Concat(entry.Descendants(Sheet + "t").Select(run => run.Value)))
            .ToList() ?? [];
        var dateStyles = DateStyles(Load(zip, "xl/styles.xml"));

        var lines = new List<string>();
        foreach (var entry in zip.Entries.Where(entry => SheetPath().IsMatch(entry.FullName)).OrderBy(entry => entry.FullName, StringComparer.Ordinal))
        {
            foreach (var row in Load(zip, entry.FullName)?.Descendants(Sheet + "row") ?? [])
            {
                var line = string.Join("\t", row.Elements(Sheet + "c").Select(cell => CellText(cell, shared, dateStyles)).Where(text => text.Length > 0));
                if (line.Length > 0)
                {
                    lines.Add(line);
                }
            }
        }

        return string.Join("\n", lines);
    }

    private static string CellText(XElement cell, List<string> shared, HashSet<int> dateStyles)
    {
        var value = cell.Element(Sheet + "v")?.Value ?? string.Concat(cell.Descendants(Sheet + "t").Select(run => run.Value));
        var type = (string?)cell.Attribute("t");
        if (type == "s")
        {
            return int.TryParse(value, CultureInfo.InvariantCulture, out var index) && index < shared.Count ? shared[index] : "";
        }

        // Excel keeps dates as day counts, which mean nothing once they're out of the sheet.
        return type is null or "n"
            && int.TryParse((string?)cell.Attribute("s"), CultureInfo.InvariantCulture, out var style) && dateStyles.Contains(style)
            && double.TryParse(value, CultureInfo.InvariantCulture, out var days)
                ? DateTime.FromOADate(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : value;
    }

    /// <summary>The cell styles that show dates: the built-in ones, and custom formats with a date in them.</summary>
    private static HashSet<int> DateStyles(XDocument? styles)
    {
        var custom = styles?.Descendants(Sheet + "numFmt")
            .Where(format => DateFormat().IsMatch(QuotedText().Replace((string?)format.Attribute("formatCode") ?? "", "")))
            .Select(format => (int?)format.Attribute("numFmtId") ?? 0)
            .ToHashSet() ?? [];

        var formats = styles?.Root?.Element(Sheet + "cellXfs")?.Elements(Sheet + "xf").Select(style => (int?)style.Attribute("numFmtId") ?? 0).ToList() ?? [];
        return Enumerable.Range(0, formats.Count)
            .Where(index => formats[index] is >= FirstDateFormat and <= LastDateFormat || (formats[index] >= FirstCustomNumberFormat && custom.Contains(formats[index])))
            .ToHashSet();
    }

    /// <summary>Reads a part of an Office file, refusing outside references and anything that unpacks to something huge.</summary>
    private static XDocument? Load(ZipArchive zip, string name)
    {
        if (zip.GetEntry(name) is not { } entry)
        {
            return null;
        }

        if (entry.Length > MaxUnpackedBytes)
        {
            throw new InvalidDataException();
        }

        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader);
    }

    [GeneratedRegex(@"^ppt/slides/slide(\d+)\.xml$")]
    private static partial Regex SlidePath();

    [GeneratedRegex(@"^xl/worksheets/sheet\d+\.xml$")]
    private static partial Regex SheetPath();

    [GeneratedRegex(@"[ymd]", RegexOptions.IgnoreCase)]
    private static partial Regex DateFormat();

    [GeneratedRegex("\"[^\"]*\"|\\[[^\\]]*\\]")]
    private static partial Regex QuotedText();
}
