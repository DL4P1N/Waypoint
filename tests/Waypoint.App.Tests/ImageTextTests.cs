using System.IO;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Waypoint.App;
using Windows.Media.Ocr;

namespace Waypoint.App.Tests;

/// <summary>Drawing PDF pages with Windows and reading them back, on this PC's own text recognition.</summary>
public sealed class ImageTextTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-scan-{Guid.NewGuid():N}.pdf");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void A_pdf_page_is_drawn_and_its_words_recognized()
    {
        if (OcrEngine.TryCreateFromUserProfileLanguages() is null)
        {
            // Without a reader for the profile's language there's nothing to check.
            return;
        }

        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(PageSize.Letter);
        builder.AddPage(PageSize.Letter).AddText("Mitosis exam Friday", 28, new PdfPoint(72, 600), font);
        File.WriteAllBytes(_path, builder.Build());

        var text = ImageText.ReadPdfPage(_path, 1);

        Assert.Contains("Mitosis", text);
        Assert.Null(ImageText.ReadPdfPage(_path, 5));
    }
}
