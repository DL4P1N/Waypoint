using System.Text.Json;
using Waypoint.Core.Capture;
using Waypoint.Core.Discord;
using Waypoint.Core.Files;
using Waypoint.Core.Transcription;

namespace Waypoint.Core.Background;

/// <summary>
/// Voice notes sent in the capture channel. Each is kept in the data folder first, then turned into text on this
/// machine and captured exactly as if its words had been typed there, with the answer showing what was heard. One
/// that can't be read yet, because speech to text isn't set up, this host has none, or it failed, stays kept and
/// is read later: when Discord connects again, when another voice note comes, or when the host says speech to text
/// is ready. The audio is deleted once its words are captured.
/// </summary>
public sealed partial class WaypointWorker
{
    private readonly IVoiceTranscriber? _voice;

    // One voice note at a time, so a note arriving while held ones are read isn't read twice.
    private readonly SemaphoreSlim _voiceTurn = new(1, 1);
    private CancellationTokenSource _voiceStop = new();

    private string VoiceFolder => Path.Combine(_dataDirectory, VoiceNotes.FolderName);

    /// <summary>
    /// Which message a kept voice note answers, when it was sent, whether it was already told it's waiting, and which
    /// of the message's files it is. Notes kept before the file was saved with them have none.
    /// </summary>
    private sealed record HeldVoiceNote(string? MessageId, DateTimeOffset SentAt, string Name, bool Told, string? AttachmentId = null);

    /// <summary>Tries every kept voice note again, oldest first. The PC calls this once speech to text is set up.</summary>
    public async Task ReadHeldVoiceNotesAsync()
    {
        if (!Directory.Exists(VoiceFolder))
        {
            return;
        }

        var stop = _voiceStop.Token;
        await _voiceTurn.WaitAsync();
        try
        {
            var held = Directory.EnumerateFiles(VoiceFolder)
                .Where(path => !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                .Select(path => (Audio: path, Note: LoadHeld(path)))
                .OrderBy(found => found.Note.SentAt)
                .ToList();
            foreach (var (audio, note) in held)
            {
                if (stop.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    await ReadHeldAsync(audio, note, stop);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    // Discord stopped or Waypoint is closing. The voice note stays kept for next time.
                    return;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // Kept, so it's tried again next time. The others still get their turn.
                    _logError(exception);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logError(exception);
        }
        finally
        {
            _voiceTurn.Release();
        }
    }

    /// <summary>
    /// Keeps a voice note sent from the phone, then reads it. One Discord already says is over the limit is a
    /// recording and isn't downloaded at all. Once it's kept, or answered, <paramref name="dealtWith"/> is called,
    /// since a kept voice note is read later whatever happens. One already captured isn't kept again.
    /// </summary>
    private async Task ReadVoiceNoteAsync(DiscordBridge discord, DiscordMessage message, DiscordAttachment attachment, Action dealtWith)
    {
        if (_database.HasCaptureClientId(DiscordClientId(message.Id, attachment.Id)))
        {
            dealtWith();
            return;
        }

        if (attachment.DurationSeconds is { } seconds && TimeSpan.FromSeconds(seconds) > VoiceNotes.MaxLength)
        {
            await discord.ReplyAsync(message.Id, RecordingNotRead(attachment.FileName, tooLong: true));
            dealtWith();
            return;
        }

        var audio = VoiceNotePath(attachment);
        var part = audio + ".part";
        try
        {
            if (!File.Exists(audio))
            {
                Directory.CreateDirectory(VoiceFolder);
                if (await discord.DownloadAsync(attachment, part, FileReader.MaxBytes) is { } problem)
                {
                    await discord.ReplyAsync(message.Id, DiscordText.Notice("Voice note not read", problem, CardTone.Attention));
                    dealtWith();
                    return;
                }

                // Its details are saved first, so kept audio always knows which message it answers.
                SaveHeld(audio, new HeldVoiceNote(message.Id, message.SentAt, attachment.FileName, Told: false, attachment.Id));
                File.Move(part, audio, overwrite: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            PlainProblem.Report(exception);
            await discord.ReplyAsync(message.Id, DiscordText.Notice("Voice note not read", $"Couldn't save {attachment.FileName}. Try sending it again.", CardTone.Attention));
            dealtWith();
            return;
        }
        finally
        {
            DeleteQuietly(part);
        }

        dealtWith();
        await ReadHeldVoiceNotesAsync();
    }

    private async Task ReadHeldAsync(string audio, HeldVoiceNote note, CancellationToken stop)
    {
        if (!File.Exists(audio))
        {
            return;
        }

        if (_voice is not { IsReady: true } voice)
        {
            await TellOnceAsync(audio, note, _voice is null
                ? "Speech to text doesn't run on the server, so it's kept there unread."
                : "It will be read once speech to text is set up on the PC, from Import audio... on Notes.");
            return;
        }

        var heard = await voice.TranscribeAsync(audio, VoicePrompt(), stop);
        stop.ThrowIfCancellationRequested();
        switch (heard.Outcome)
        {
            case VoiceOutcome.Heard:
                // Captured first, exactly as typed words would be, and only then let go of. It's saved under its
                // message and file, so the same voice note handed over again after a crash isn't captured twice.
                var captured = note is { MessageId: { } sentIn, AttachmentId: { } file }
                    ? Captures.CaptureOnce(DiscordClientId(sentIn, file), heard.Text, note.SentAt, CaptureOrigin.Discord)
                    : Captures.Capture(heard.Text, note.SentAt, CaptureOrigin.Discord);
                if (captured is { } id && note.MessageId is { } messageId)
                {
                    _discordReplies[id] = new DiscordReplyTo(messageId, Heard: heard.Text);
                }

                Forget(audio);
                _ = ProcessDiscordAsync();
                break;
            case VoiceOutcome.Silent:
                Forget(audio);
                await ReplyToVoiceNoteAsync(note, DiscordText.Notice("Nothing heard", $"No words were heard in {note.Name}."));
                break;
            case VoiceOutcome.TooLong:
                Forget(audio);
                await ReplyToVoiceNoteAsync(note, RecordingNotRead(note.Name, tooLong: true));
                break;
            case VoiceOutcome.Unreadable:
                Forget(audio);
                await ReplyToVoiceNoteAsync(note, DiscordText.Notice("Voice note not read", $"Couldn't read the audio in {note.Name}. It may be a kind of file Windows can't play.", CardTone.Attention));
                break;
            default:
                await TellOnceAsync(audio, note, "Speech to text didn't work this time, so it will be tried again later.");
                break;
        }
    }

    /// <summary>Says once why a voice note is waiting, never again however many times it's tried.</summary>
    private async Task TellOnceAsync(string audio, HeldVoiceNote note, string why)
    {
        if (note.Told || note.MessageId is null || Discord is not { } discord)
        {
            return;
        }

        if (await discord.ReplyAsync(note.MessageId, DiscordText.Notice("Voice note kept", why, CardTone.Attention)) is not null)
        {
            SaveHeld(audio, note with { Told = true });
        }
    }

    private async Task ReplyToVoiceNoteAsync(HeldVoiceNote note, DiscordCard card)
    {
        if (note.MessageId is { } messageId && Discord is { } discord)
        {
            await discord.ReplyAsync(messageId, card);
        }
    }

    /// <summary>The same reply a recording has always had, saying so when it's long rather than a video.</summary>
    private static DiscordCard RecordingNotRead(string name, bool tooLong = false) => DiscordText.Notice("Recording not read", tooLong
        ? $"{name} is over {VoiceNotes.MaxLength.TotalMinutes:0} minutes, so it's a recording, not a voice note. Recordings can't be sent from Discord yet. Add it on the Notes page on the PC."
        : $"Recordings can't be sent from Discord yet, so {name} wasn't read. Add it on the Notes page on the PC.");

    /// <summary>Class names and open assignments, so they're spelled right, the same hint lectures get.</summary>
    private string VoicePrompt()
    {
        var assignments = _database.GetOpenItems()
            .Where(item => item.Kind is ItemKind.Deadline or ItemKind.Exam || item.Kind == ItemKind.Task && item.Course is not null)
            .Select(item => (item.Title, item.Course))
            .Take(60);
        return WhisperPrompt.Build(null, Schedule.Meetings.Select(meeting => meeting.Course), assignments);
    }

    /// <summary>
    /// Named by the attachment, so the same voice note seen twice is kept once, with nothing in the name that could
    /// reach outside the folder. A name without an audio ending gets one, so it's never taken for its own details.
    /// </summary>
    private string VoiceNotePath(DiscordAttachment attachment)
    {
        var name = string.Concat($"{attachment.Id}-{Path.GetFileName(attachment.FileName)}".Select(letter => Path.GetInvalidFileNameChars().Contains(letter) ? '_' : letter));
        if (!VoiceNotes.Extensions.Contains(Path.GetExtension(name)))
        {
            name += ".audio";
        }

        return Path.Combine(VoiceFolder, name);
    }

    /// <summary>A voice note's details, or what can be told from the file alone when they're missing or damaged.</summary>
    private static HeldVoiceNote LoadHeld(string audio)
    {
        try
        {
            if (JsonSerializer.Deserialize<HeldVoiceNote>(File.ReadAllText(audio + ".json")) is { Name: not null } note)
            {
                return note;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return new HeldVoiceNote(null, File.GetLastWriteTime(audio), Path.GetFileName(audio), Told: true);
    }

    private static void SaveHeld(string audio, HeldVoiceNote note)
    {
        var part = audio + ".json.part";
        File.WriteAllText(part, JsonSerializer.Serialize(note));
        File.Move(part, audio + ".json", overwrite: true);
    }

    /// <summary>Its words are captured, so the audio and its details go.</summary>
    private static void Forget(string audio)
    {
        DeleteQuietly(audio);
        DeleteQuietly(audio + ".json");
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left behind, which is rare since nothing has the file open by then.
        }
    }

    /// <summary>Stops a voice note being read, when Discord stops. It stays kept and is read on the next connect.</summary>
    private void StopVoiceNotes() => Interlocked.Exchange(ref _voiceStop, new CancellationTokenSource()).Cancel();
}
