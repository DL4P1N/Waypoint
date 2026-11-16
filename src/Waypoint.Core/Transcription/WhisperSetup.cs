using System.IO.Compression;
using System.Security.Cryptography;

namespace Waypoint.Core.Transcription;

/// <summary>A file the one-time setup downloads, pinned to the exact bytes it must have.</summary>
public sealed record WhisperDownload(string FileName, Uri Url, string Sha256, long Size);

/// <summary>What came of the setup. Problem is null when everything is in place.</summary>
public sealed record SetupResult(string? Problem)
{
    public bool Ready => Problem is null;
}

/// <summary>
/// The Whisper model and the whisper.cpp program, downloaded once into the data folder after the user agrees.
/// Each file downloads to a <c>.part</c> name, is checked against its pinned SHA-256, and only then gets its real
/// name, so a file under the real name is always a checked one. A stopped download carries on next time, and one
/// that doesn't match is thrown away and started over.
/// </summary>
public sealed class WhisperSetup
{
    /// <summary>large-v3-turbo from ggerganov/whisper.cpp on Hugging Face.</summary>
    public static readonly WhisperDownload Model = new(
        "ggml-large-v3-turbo.bin",
        new Uri("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo.bin"),
        "1FC70F774D38EB169993AC391EEA357EF47C88757EF72EE5943879B7E8E2BC69",
        1_624_555_275);

    /// <summary>whisper.cpp v1.9.5 with OpenBLAS, from release b5454 on ggml-org/whisper.cpp.</summary>
    public static readonly WhisperDownload Program = new(
        "whisper-blas-bin-x64.zip",
        new Uri("https://github.com/ggml-org/whisper.cpp/releases/download/b5454/whisper-blas-bin-x64.zip"),
        "B1158BD10EDF92C9799149A6FC5D3E58DA3F495241D6FB3423088130E88FFBF3",
        21_715_607);

    public const string CliName = "whisper-cli.exe";

    private readonly IFileDownloader _downloader;
    private readonly WhisperDownload _model;
    private readonly WhisperDownload _program;
    private readonly string _folder;

    /// <summary>Files go in <c>&lt;data&gt;\whisper\</c>. The downloads can be swapped, for tests.</summary>
    public WhisperSetup(string dataDirectory, IFileDownloader downloader, WhisperDownload? model = null, WhisperDownload? program = null)
    {
        _downloader = downloader;
        _model = model ?? Model;
        _program = program ?? Program;
        _folder = Path.Combine(dataDirectory, "whisper");
        ModelPath = Path.Combine(_folder, _model.FileName);
        ProgramFolder = Path.Combine(_folder, "bin");
    }

    /// <summary>Uses files already in place, such as a test copy. Nothing is downloaded.</summary>
    public WhisperSetup(string modelPath, string programFolder)
    {
        _downloader = new NoDownloads();
        _model = Model with { Size = new FileInfo(modelPath).Length };
        _program = Program;
        _folder = Path.GetDirectoryName(Path.GetFullPath(modelPath))!;
        ModelPath = modelPath;
        ProgramFolder = programFolder;
    }

    public string ModelPath { get; }

    public string ProgramFolder { get; }

    public string CliPath => Path.Combine(ProgramFolder, CliName);

    /// <summary>The total download size, for the consent prompt and progress.</summary>
    public long TotalBytes => _model.Size + _program.Size;

    /// <summary>The model is only ever under its real name after its hash was checked, so its size is enough here.</summary>
    public bool IsReady => File.Exists(CliPath) && File.Exists(ModelPath) && new FileInfo(ModelPath).Length == _model.Size;

    public async Task<SetupResult> RunAsync(IProgress<SetupState>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_folder);
        long programDone = File.Exists(CliPath) ? _program.Size : 0;
        long modelDone = File.Exists(ModelPath) ? _model.Size : 0;
        void Report() => progress?.Report(new SetupState(true, programDone + modelDone, TotalBytes));

        try
        {
            // The small one first, so a problem with the connection shows up quickly.
            if (!File.Exists(CliPath))
            {
                var zipPath = Path.Combine(_folder, _program.FileName);
                if (await FetchAsync(_program, zipPath, new Progress<long>(bytes => { programDone = bytes; Report(); }), cancellationToken) is { } problem)
                {
                    return new SetupResult(problem);
                }

                Unpack(zipPath);
                File.Delete(zipPath);
                programDone = _program.Size;
                Report();
            }

            if (!File.Exists(ModelPath))
            {
                if (await FetchAsync(_model, ModelPath, new Progress<long>(bytes => { modelDone = bytes; Report(); }), cancellationToken) is { } problem)
                {
                    return new SetupResult(problem);
                }

                modelDone = _model.Size;
                Report();
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new SetupResult($"The download stopped. {PlainProblem.Report(exception)} What's done so far is kept, so trying again carries on from there.");
        }

        return IsReady ? new SetupResult(null) : new SetupResult("The speech to text files aren't complete. Try the download again.");
    }

    /// <summary>
    /// Downloads to a part file, checks it, and renames it. A part file that came from an earlier try and doesn't
    /// match is thrown away and downloaded once more from the start before giving up.
    /// </summary>
    private async Task<string?> FetchAsync(WhisperDownload download, string finalPath, IProgress<long> progress, CancellationToken cancellationToken)
    {
        var part = finalPath + ".part";
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var resumed = File.Exists(part) && new FileInfo(part).Length > 0;
            if (File.Exists(part) && new FileInfo(part).Length > download.Size)
            {
                File.Delete(part);
                resumed = false;
            }

            if (!File.Exists(part) || new FileInfo(part).Length < download.Size)
            {
                await _downloader.DownloadAsync(download.Url, part, progress, cancellationToken);
            }

            if (await HashAsync(part, cancellationToken) == download.Sha256)
            {
                File.Move(part, finalPath, overwrite: true);
                return null;
            }

            File.Delete(part);
            if (!resumed)
            {
                break;
            }
        }

        return $"The download of {download.FileName} didn't match what was expected, so it was thrown away. Nothing was installed. Try again later.";
    }

    /// <summary>Takes whisper-cli.exe and the libraries it loads. Only file names are used, so nothing lands outside the folder.</summary>
    private void Unpack(string zipPath)
    {
        var staging = ProgramFolder + ".tmp";
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        Directory.CreateDirectory(staging);
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in zip.Entries)
            {
                var name = Path.GetFileName(entry.FullName);
                if (name.Length > 0 && (name.Equals(CliName, StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                {
                    entry.ExtractToFile(Path.Combine(staging, name), overwrite: true);
                }
            }
        }

        if (!File.Exists(Path.Combine(staging, CliName)))
        {
            Directory.Delete(staging, recursive: true);
            throw new InvalidDataException($"{CliName} wasn't in the download");
        }

        if (Directory.Exists(ProgramFolder))
        {
            Directory.Delete(ProgramFolder, recursive: true);
        }

        Directory.Move(staging, ProgramFolder);
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private sealed class NoDownloads : IFileDownloader
    {
        public Task DownloadAsync(Uri url, string partPath, IProgress<long>? progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This setup uses files already in place.");
    }
}
