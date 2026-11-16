using System.ComponentModel;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Waypoint.Core.Files;
using Waypoint.Core.Transcription;

namespace Waypoint.Core.Tests;

/// <summary>Text recognition on the server, through tesseract and pdftoppm, with a fake in place of both programs.</summary>
public sealed class TextRecognitionTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-ocr-{Guid.NewGuid()}");

    public TextRecognitionTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>
    /// Acts like tesseract and pdftoppm: each writes the file it would, and anything left out of Installed
    /// can't be started. Every call is kept.
    /// </summary>
    private sealed class FakePrograms(params string[] installed) : IProcessRunner
    {
        public List<ProcessSpec> Calls { get; } = [];

        public HashSet<string> Installed { get; } = [.. installed];

        public string Text { get; set; } = "Quiz on Friday";

        public int PdfExitCode { get; set; }

        public Task<ProcessResult> RunAsync(ProcessSpec spec, Action<string> onLine, CancellationToken cancellationToken)
        {
            lock (Calls)
            {
                Calls.Add(spec);
            }

            if (!Installed.Contains(spec.FileName))
            {
                throw new Win32Exception(2, "No such file or directory");
            }

            if (spec.Arguments is ["--version"] or ["-v"])
            {
                return Task.FromResult(new ProcessResult(0, ""));
            }

            if (spec.FileName == TesseractTextRecognizer.PdfProgram)
            {
                if (PdfExitCode == 0)
                {
                    File.WriteAllBytes(spec.Arguments[^1] + ".png", Png(2318, 3000));
                }

                return Task.FromResult(new ProcessResult(PdfExitCode, PdfExitCode == 0 ? "" : "Wrong page range given"));
            }

            File.WriteAllText(spec.Arguments[1] + ".txt", Text + "\n");
            return Task.FromResult(new ProcessResult(0, ""));
        }
    }

    private TesseractTextRecognizer Recognizer(FakePrograms programs) => new(programs, workFolder: Path.Combine(_folder, "work"));

    private string WriteImage(int width = 1200, int height = 900)
    {
        var path = Path.Combine(_folder, "board.png");
        File.WriteAllBytes(path, Png(width, height));
        return path;
    }

    /// <summary>The start of a PNG, as far as its size. That's all that's read before tesseract is asked.</summary>
    private static byte[] Png(int width, int height)
    {
        var bytes = new byte[33];
        byte[] start = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R'];
        start.CopyTo(bytes, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
        return bytes;
    }

    /// <summary>A PDF with typed text where there's a string and a blank page, like a scan, where it's null.</summary>
    private string WritePdf(params string?[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pages)
        {
            var page = builder.AddPage(PageSize.Letter);
            if (text is not null)
            {
                page.AddText(text, 12, new PdfPoint(72, 700), font);
            }
        }

        var path = Path.Combine(_folder, "scan.pdf");
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    [Fact]
    public void A_picture_is_read_by_tesseract_and_its_files_are_cleared()
    {
        var programs = new FakePrograms(TesseractTextRecognizer.TesseractProgram);

        var file = TextRecognition.ReadFile(WriteImage(), Recognizer(programs));

        Assert.Equal("Quiz on Friday", file.Text);
        var read = programs.Calls.Last();
        Assert.Equal(TesseractTextRecognizer.TesseractProgram, read.FileName);
        Assert.EndsWith("board.png", read.Arguments[0]);
        Assert.Equal(["-l", "eng"], read.Arguments.Skip(2));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(_folder, "work")));
    }

    [Fact]
    public void Without_tesseract_the_reply_says_so_and_it_is_only_looked_for_once()
    {
        var programs = new FakePrograms();
        var recognizer = Recognizer(programs);

        var first = TextRecognition.ReadFile(WriteImage(), recognizer);
        var second = TextRecognition.ReadFile(WriteImage(), recognizer);

        Assert.Null(first.Text);
        Assert.Contains("tesseract-ocr", first.Problem);
        Assert.Contains("tesseract-ocr", second.Problem);
        Assert.Single(programs.Calls);
    }

    [Fact]
    public void Tesseract_installed_later_is_found_within_a_few_minutes_without_a_restart()
    {
        var now = new DateTime(2026, 11, 9, 10, 0, 0);
        var programs = new FakePrograms();
        var recognizer = new TesseractTextRecognizer(programs, workFolder: Path.Combine(_folder, "work"), clock: () => now);

        Assert.NotNull(recognizer.Unavailable);
        programs.Installed.Add(TesseractTextRecognizer.TesseractProgram);

        // Still the answer from a moment ago, so a batch of pictures doesn't look for it each time.
        now = now.AddMinutes(1);
        Assert.NotNull(recognizer.Unavailable);

        now = now.AddMinutes(5);
        Assert.Null(recognizer.Unavailable);
        Assert.Equal("Quiz on Friday", TextRecognition.ReadFile(WriteImage(), recognizer).Text);
    }

    [Fact]
    public void Only_scanned_pages_are_drawn_and_read_numbered_from_one()
    {
        var programs = new FakePrograms(TesseractTextRecognizer.TesseractProgram, TesseractTextRecognizer.PdfProgram) { Text = "Chapter 4 notes" };

        var file = TextRecognition.ReadFile(WritePdf("Typed page", null), Recognizer(programs));

        Assert.Contains("Typed page", file.Text);
        Assert.Contains("Chapter 4 notes", file.Text);
        var draw = Assert.Single(programs.Calls, call => call.FileName == TesseractTextRecognizer.PdfProgram && call.Arguments.Count > 1);
        Assert.Equal(["-f", "2", "-l", "2"], draw.Arguments.Take(4));
        Assert.Contains("-singlefile", draw.Arguments);
        Assert.Equal("3000", draw.Arguments[draw.Arguments.ToList().IndexOf("-scale-to") + 1]);
        Assert.DoesNotContain("-r", draw.Arguments);
    }

    [Theory]
    [InlineData(20_000, 100)]
    [InlineData(100, 12_001)]
    [InlineData(10_000, 10_000)]
    public void A_picture_too_big_to_decode_safely_is_never_handed_to_tesseract(int width, int height)
    {
        var programs = new FakePrograms(TesseractTextRecognizer.TesseractProgram);

        Assert.Null(Recognizer(programs).ReadImage(WriteImage(width, height)));
        Assert.DoesNotContain(programs.Calls, call => call.Arguments.Count > 1);
    }

    [Fact]
    public void A_picture_whose_size_cant_be_told_is_never_handed_to_tesseract()
    {
        var programs = new FakePrograms(TesseractTextRecognizer.TesseractProgram);
        var path = Path.Combine(_folder, "odd.png");
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);

        Assert.Null(Recognizer(programs).ReadImage(path));
        Assert.DoesNotContain(programs.Calls, call => call.Arguments.Count > 1);
    }

    [Fact]
    public void Sizes_are_read_from_each_kind_of_picture_header()
    {
        Assert.Equal((640, 480), ImageSize.Read([(byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 0x80, 0x02, 0xE0, 0x01]));
        byte[] bmp = new byte[26];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), 300);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), -200);
        Assert.Equal((300, 200), ImageSize.Read(bmp));
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x0B, 0xB8, 0x0F, 0xA0, 0x03, 0, 0, 0, 0];
        Assert.Equal((4000, 3000), ImageSize.Read(jpeg));
        byte[] tiff = [(byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 2, 0, 0, 1, 3, 0, 1, 0, 0, 0, 0x20, 0x03, 0, 0, 1, 1, 4, 0, 1, 0, 0, 0, 0x58, 0x02, 0, 0];
        Assert.Equal((800, 600), ImageSize.Read(tiff));
        Assert.Null(ImageSize.Read("just some text"u8));
    }

    [Fact]
    public void Without_pdftoppm_a_scan_has_no_text_but_typed_pages_still_come_through()
    {
        var programs = new FakePrograms(TesseractTextRecognizer.TesseractProgram);
        var recognizer = Recognizer(programs);

        Assert.Equal("Typed page", TextRecognition.ReadFile(WritePdf("Typed page", null), recognizer).Text);
        Assert.True(TextRecognition.ReadFile(WritePdf(null, null), recognizer).NoText);
        Assert.Single(programs.Calls, call => call.FileName == TesseractTextRecognizer.PdfProgram);
    }

    [Fact]
    public void A_page_that_cannot_be_drawn_is_left_blank()
    {
        var programs = new FakePrograms(TesseractTextRecognizer.TesseractProgram, TesseractTextRecognizer.PdfProgram) { PdfExitCode = 99 };

        Assert.Null(Recognizer(programs).ReadPdfPage(WritePdf([null]), 0));
        Assert.Null(Recognizer(programs).ReadPdfPage(WritePdf([null]), -1));
    }

    /// <summary>Only runs where tesseract and pdftoppm are really installed, such as the server.</summary>
    [RealTesseractFact]
    public void The_real_programs_read_a_drawn_page()
    {
        var recognizer = new TesseractTextRecognizer(new ChildProcessRunner(), workFolder: Path.Combine(_folder, "real"));

        var text = recognizer.ReadPdfPage(WritePdf("Waypoint reads this page"), 0);

        Assert.Contains("reads this page", text);
    }
}

/// <summary>Skips unless both programs start, so the tests pass on a PC without them.</summary>
public sealed class RealTesseractFactAttribute : FactAttribute
{
    public RealTesseractFactAttribute()
    {
        if (!Starts(TesseractTextRecognizer.TesseractProgram, "--version") || !Starts(TesseractTextRecognizer.PdfProgram, "-v"))
        {
            Skip = "tesseract or pdftoppm isn't installed here.";
        }
    }

    private static bool Starts(string program, string flag)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(program, flag) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            process?.WaitForExit(10_000);
            return process is { HasExited: true, ExitCode: 0 };
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
