using System.ComponentModel;
using System.Globalization;
using Waypoint.Core.Transcription;

namespace Waypoint.Core.Files;

/// <summary>
/// Finds the text in pictures and scanned PDF pages. The PC app uses Windows' own reader and the server uses
/// tesseract, so this is all the file reading needs to know about either.
/// </summary>
public interface ITextRecognizer
{
    /// <summary>The text in a picture. Null when it can't be read at all.</summary>
    string? ReadImage(string path);

    /// <summary>The text on one PDF page, numbered from zero. Null when the page can't be drawn or read.</summary>
    string? ReadPdfPage(string path, int index);

    /// <summary>Why nothing can be read here, such as a missing program, or null when it's ready.</summary>
    string? Unavailable { get; }
}

public static class TextRecognition
{
    /// <summary>
    /// Reads a file with text recognition for pictures and scanned pages. When a picture comes back empty because
    /// recognition can't run here, the reply says so, rather than suggesting the picture had no text.
    /// </summary>
    public static FileText ReadFile(string path, ITextRecognizer? recognizer)
    {
        var file = FileReader.Read(path, recognizer is null ? null : recognizer.ReadImage, recognizer is null ? null : recognizer.ReadPdfPage);
        return file.NoText && recognizer?.Unavailable is { } why
            ? file with { Problem = $"I couldn't read the text in {file.Name}, because {why} Type what matters from it and I'll take it from there." }
            : file;
    }
}

/// <summary>
/// Text recognition on Linux through the tesseract program, with poppler's pdftoppm drawing scanned PDF pages
/// first. Both run as child processes with their arguments passed one by one, and write to a temporary folder
/// that's cleared after each page. A missing program is reported, never thrown, and looked for again every few
/// minutes, so installing it works without a restart.
/// </summary>
public sealed class TesseractTextRecognizer(IProcessRunner runner, string language = "eng", string? workFolder = null, Func<DateTime>? clock = null) : ITextRecognizer
{
    public const string TesseractProgram = "tesseract";
    public const string PdfProgram = "pdftoppm";

    /// <summary>
    /// The longest side a scanned page is drawn at, in pixels. A letter page is about 2200 by 1700 at 200 dots an inch,
    /// so print still reads, and a page that claims to be huge is still drawn this small.
    /// </summary>
    public const int PdfLongestSide = 3000;

    /// <summary>The largest picture handed to tesseract, which decodes all of it: a 48 megapixel phone photo fits.</summary>
    public const long MaxImagePixels = 50_000_000;

    public const int MaxImageSide = 12_000;

    private static readonly TimeSpan PageTimeout = TimeSpan.FromMinutes(1);

    private readonly string _folder = workFolder ?? Path.Combine(Path.GetTempPath(), "Waypoint", "ocr");
    private readonly object _lock = new();
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);
    private bool? _hasTesseract;
    private bool? _hasPdfToPpm;
    private DateTime _lookedForTesseract;
    private DateTime _lookedForPdfToPpm;

    /// <summary>How long a missing program is taken as still missing before it's looked for again.</summary>
    public static readonly TimeSpan RecheckAfter = TimeSpan.FromMinutes(5);

    public string? Unavailable
    {
        get
        {
            lock (_lock)
            {
                _hasTesseract = Found(_hasTesseract, ref _lookedForTesseract, () => Works(TesseractProgram));
                return _hasTesseract == true ? null : "text recognition isn't installed here. On the server, run: sudo apt install tesseract-ocr.";
            }
        }
    }

    /// <summary>A program that was found stays found. One that wasn't is looked for again once RecheckAfter has passed.</summary>
    private bool Found(bool? known, ref DateTime lookedAt, Func<bool> look)
    {
        var now = _clock();
        if (known == true || (known == false && now - lookedAt < RecheckAfter))
        {
            return known.Value;
        }

        lookedAt = now;
        return look();
    }

    public string? ReadImage(string path)
    {
        if (Unavailable is not null)
        {
            return null;
        }

        var work = NewWorkFolder();
        try
        {
            return Recognize(path, work);
        }
        finally
        {
            Clear(work);
        }
    }

    public string? ReadPdfPage(string path, int index)
    {
        if (index < 0 || Unavailable is not null)
        {
            return null;
        }

        lock (_lock)
        {
            _hasPdfToPpm = Found(_hasPdfToPpm, ref _lookedForPdfToPpm, () => Works(PdfProgram, "-v"));
            if (_hasPdfToPpm != true)
            {
                return null;
            }
        }

        var work = NewWorkFolder();
        try
        {
            // pdftoppm numbers pages from one, and -singlefile keeps the name exactly as given.
            var page = (index + 1).ToString(CultureInfo.InvariantCulture);
            var picture = Path.Combine(work, "page");
            var drawn = Run(PdfProgram, ["-f", page, "-l", page, "-scale-to", PdfLongestSide.ToString(CultureInfo.InvariantCulture), "-png", "-singlefile", path, picture], work);
            return drawn == 0 && File.Exists(picture + ".png") ? Recognize(picture + ".png", work) : null;
        }
        finally
        {
            Clear(work);
        }
    }

    /// <summary>Whether a picture's header gives a size tesseract can safely decode. One whose size can't be told isn't.</summary>
    public static bool IsSafeSize(string picture) =>
        ImageSize.Read(picture) is { } size
        && size.Width <= MaxImageSide && size.Height <= MaxImageSide && (long)size.Width * size.Height <= MaxImagePixels;

    /// <summary>tesseract writes its text beside the base name given, with .txt added.</summary>
    private string? Recognize(string picture, string work)
    {
        if (!IsSafeSize(picture))
        {
            return null;
        }

        var output = Path.Combine(work, "text");
        if (Run(TesseractProgram, [picture, output, "-l", language], work) != 0 || !File.Exists(output + ".txt"))
        {
            return null;
        }

        return File.ReadAllText(output + ".txt").Trim();
    }

    /// <summary>True if the program starts and ends cleanly when asked for its version.</summary>
    private bool Works(string program, string versionFlag = "--version") => Run(program, [versionFlag], Path.GetTempPath()) == 0;

    /// <summary>The exit code, or null if the program couldn't be started or took too long.</summary>
    private int? Run(string program, IReadOnlyList<string> arguments, string folder)
    {
        using var timeout = new CancellationTokenSource(PageTimeout);
        try
        {
            return runner.RunAsync(new ProcessSpec(program, arguments, folder), _ => { }, timeout.Token).GetAwaiter().GetResult().ExitCode;
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException or OperationCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    private string NewWorkFolder()
    {
        var work = Path.Combine(_folder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        return work;
    }

    private static void Clear(string work)
    {
        try
        {
            Directory.Delete(work, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The system clears its temp folder in the end.
        }
    }
}
