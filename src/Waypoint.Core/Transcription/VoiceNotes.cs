using System.Text.RegularExpressions;
using Waypoint.Core.Data;

namespace Waypoint.Core.Transcription;

/// <summary>What came of turning a voice note into text.</summary>
public enum VoiceOutcome
{
    /// <summary>Words were heard. They're in <see cref="VoiceText.Text"/>.</summary>
    Heard,

    /// <summary>It played, but no words were in it.</summary>
    Silent,

    /// <summary>Longer than <see cref="VoiceNotes.MaxLength"/>, so it's a recording, not a voice note. Nothing was transcribed.</summary>
    TooLong,

    /// <summary>The audio itself can't be read here, so trying again won't help.</summary>
    Unreadable,

    /// <summary>Something went wrong this time. The voice note is kept and tried again later.</summary>
    Failed,
}

public sealed record VoiceText(VoiceOutcome Outcome, string Text = "", TimeSpan Length = default);

/// <summary>
/// Turns a short voice note into text on this machine. The PC uses <see cref="WhisperVoiceTranscriber"/>. A host
/// without one, such as the server, holds voice notes instead. Tests use a fake, so Whisper never runs there.
/// </summary>
public interface IVoiceTranscriber
{
    /// <summary>False until speech to text is set up here: the user agreed and its files are downloaded.</summary>
    bool IsReady { get; }

    Task<VoiceText> TranscribeAsync(string audioPath, string prompt, CancellationToken cancellationToken);
}

/// <summary>Voice notes sent from the phone, which are captured as if their words had been typed.</summary>
public static partial class VoiceNotes
{
    /// <summary>Anything longer is a lecture or a meeting, which belongs in Notes rather than in capture.</summary>
    public static readonly TimeSpan MaxLength = TimeSpan.FromMinutes(5);

    /// <summary>Held voice notes wait here, in the data folder, until their text is captured.</summary>
    public const string FolderName = "voice-notes";

    /// <summary>Audio only. A video sent from the phone isn't taken as a voice note.</summary>
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".ogg", ".opus", ".mp3", ".m4a", ".wav", ".wma", ".aac", ".flac",
    };

    /// <summary>Discord sends a voice message as voice-message.ogg with an audio type.</summary>
    public static bool IsVoiceNote(string fileName, string? contentType) =>
        Extensions.Contains(Path.GetExtension(fileName))
        || contentType is { } type && type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);

    /// <summary>whisper-cli's text as one plain line, without its marks for silence or music such as [BLANK_AUDIO].</summary>
    public static string CleanText(string whisperText) =>
        Spaces().Replace(NonSpeech().Replace(whisperText, " "), " ").Trim();

    [GeneratedRegex(@"\[[A-Z_ ]+\]|\((?:silence|music|inaudible)\)", RegexOptions.IgnoreCase)]
    private static partial Regex NonSpeech();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

/// <summary>
/// Whisper on this PC, set up the same way as for lectures. The audio is converted to the 16 kHz WAV whisper-cli
/// reads, its length checked, then turned into text. The working files are always deleted; the voice note itself
/// is left for the caller.
/// </summary>
public sealed class WhisperVoiceTranscriber(WaypointDatabase database, WhisperSetup setup, IAudioConverter converter, IProcessRunner runner, string dataDirectory) : IVoiceTranscriber
{
    public bool IsReady => database.GetPreference(TranscriptionQueue.ConsentPreference) == "yes" && setup.IsReady;

    public async Task<VoiceText> TranscribeAsync(string audioPath, string prompt, CancellationToken cancellationToken)
    {
        var work = Path.Combine(dataDirectory, "whisper", "work");
        Directory.CreateDirectory(work);
        var stem = Path.Combine(work, $"voice-{Guid.NewGuid():N}");
        var wav = stem + ".wav";
        var output = stem + "-out";
        try
        {
            TimeSpan length;
            try
            {
                length = await converter.ConvertAsync(audioPath, wav, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The file was busy or the disk had a problem, which may not happen next time.
                PlainProblem.Report(exception);
                return new VoiceText(VoiceOutcome.Failed);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                PlainProblem.Report(exception);
                return new VoiceText(VoiceOutcome.Unreadable);
            }

            if (length > VoiceNotes.MaxLength)
            {
                return new VoiceText(VoiceOutcome.TooLong, Length: length);
            }

            var arguments = TranscriptionQueue.WhisperArguments(setup.ModelPath, wav, output, prompt, Math.Clamp(Environment.ProcessorCount / 2, 1, 16));
            var result = await runner.RunAsync(new ProcessSpec(setup.CliPath, arguments, work), _ => { }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.ExitCode != 0 || !File.Exists(output + ".txt"))
            {
                return new VoiceText(VoiceOutcome.Failed, Length: length);
            }

            var text = VoiceNotes.CleanText(await File.ReadAllTextAsync(output + ".txt", cancellationToken));
            return text.Length == 0 ? new VoiceText(VoiceOutcome.Silent, Length: length) : new VoiceText(VoiceOutcome.Heard, text, length);
        }
        finally
        {
            Delete(wav);
            Delete(output + ".txt");
            Delete(output + ".json");
        }
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Only a working copy, left for the next cleanup.
        }
    }
}
