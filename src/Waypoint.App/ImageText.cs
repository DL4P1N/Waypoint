using System;
using System.Linq;
using Waypoint.Core.Files;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Waypoint.App;

/// <summary>
/// Finds the text in a picture with Windows' own reader. It stays on this PC, and only text
/// comes out, so sensitive numbers can be taken out the same way as in any other file.
/// </summary>
public static class ImageText
{
    /// <summary>Scans are drawn at twice screen size, which is about 200 dots an inch, so small print still reads.</summary>
    private const double PdfScale = 2.0;

    /// <summary>Windows' reader in the shape the worker takes, so files sent from Discord read the same as dropped ones.</summary>
    public static readonly ITextRecognizer Recognizer = new WindowsRecognizer();

    /// <summary>Reads a dropped file, with text recognition for pictures and for scanned PDF pages. Blocks, so call it off the UI thread.</summary>
    public static FileText ReadFile(string path) => TextRecognition.ReadFile(path, Recognizer);

    /// <summary>Blocks, so call it off the UI thread. Null if Windows has no reader for this language or can't decode the picture.</summary>
    public static string? Read(string path)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
        {
            return null;
        }

        var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
        using IRandomAccessStream stream = file.OpenReadAsync().AsTask().GetAwaiter().GetResult();
        return Recognize(engine, stream);
    }

    /// <summary>
    /// Draws one PDF page, numbered from zero, with Windows' own PDF reader and finds the text in it.
    /// Blocks, so call it off the UI thread. Null if Windows has no reader for this language.
    /// </summary>
    public static string? ReadPdfPage(string path, int index)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
        {
            return null;
        }

        var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
        var document = PdfDocument.LoadFromFileAsync(file).AsTask().GetAwaiter().GetResult();
        if (index < 0 || index >= document.PageCount)
        {
            return null;
        }

        using var page = document.GetPage((uint)index);
        var (width, height) = RenderSize(page.Size.Width, page.Size.Height, OcrEngine.MaxImageDimension);
        using var stream = new InMemoryRandomAccessStream();
        var options = new PdfPageRenderOptions { DestinationWidth = width, DestinationHeight = height };
        page.RenderToStreamAsync(stream, options).AsTask().GetAwaiter().GetResult();
        stream.Seek(0);
        return Recognize(engine, stream);
    }

    /// <summary>The page at twice its size, shrunk to fit if that's larger than the reader takes.</summary>
    private static (uint Width, uint Height) RenderSize(double width, double height, uint largest)
    {
        var scale = PdfScale;
        var longest = Math.Max(width, height) * scale;
        if (longest > largest)
        {
            scale *= largest / longest;
        }

        return ((uint)Math.Max(1, Math.Floor(width * scale)), (uint)Math.Max(1, Math.Floor(height * scale)));
    }

    private sealed class WindowsRecognizer : ITextRecognizer
    {
        public string? Unavailable => null;

        public string? ReadImage(string path) => Read(path);

        public string? ReadPdfPage(string path, int index) => ImageText.ReadPdfPage(path, index);
    }

    private static string Recognize(OcrEngine engine, IRandomAccessStream stream)
    {
        var decoder = BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
        using var bitmap = decoder.GetSoftwareBitmapAsync().AsTask().GetAwaiter().GetResult();
        var result = engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();
        return string.Join("\n", result.Lines.Select(line => line.Text));
    }
}
