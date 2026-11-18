using System.Globalization;
using Waypoint.Core.Budget;
using Waypoint.Core.Calendar;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;
using Waypoint.Core.Email;
using Waypoint.Core.Files;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Background;

/// <summary>Reading the owner's messages and files in the capture channel, by the live connection or by polling.</summary>
public sealed partial class WaypointWorker
{
    public async Task PollDiscordAsync(bool force = false)
    {
        if (Discord is not { } discord)
        {
            return;
        }

        if (_connectGateway)
        {
            DiscordHealth.Connection(_gateway?.IsConnected == true, _clock());
        }

        // Views keep their own pace, so they're checked even when polling isn't needed.
        _ = RefreshDiscordViewsAsync();
        if (!force && _gateway is { IsConnected: true, HasMessages: true } && discord.LastPolled is { } polled && DateTimeOffset.Now - polled < DiscordLivePollInterval)
        {
            return;
        }

        await discord.PollAsync();
        foreach (var action in await discord.ReadAlertActionsAsync())
        {
            await discord.ActOnAlertAsync(action);
        }

        _host.Changed();
    }

    /// <summary>
    /// One of the owner's messages in the capture channel. Its text and each file are captured separately, each
    /// under the message's ID, so a message handed over again after a crash or a close saves nothing twice. The
    /// message is marked finished once its text is saved and every file is dealt with. Until then it's handed over
    /// again later, such as when Waypoint closed before its files could be read.
    /// </summary>
    public void OnDiscordMessage(DiscordMessage message)
    {
        // A reply to a payment app card answers that card. It's never sorted, so it can't become a note or a task.
        // Answering it again only says it's already settled.
        if (message.ReplyTo is { } card && message.Text.Trim().Length > 0 && _database.FindRepaymentForCard(card, message.ReplyToText) is { } repayment)
        {
            AnswerPaymentAppReply(message, card, repayment);
        }
        else if (Captures.CaptureOnce(DiscordClientId(message.Id), message.Text, message.SentAt, CaptureOrigin.Discord) is { } id)
        {
            _discordReplies[id] = new DiscordReplyTo(message.Id);
        }

        if (message.Attachments.Count == 0)
        {
            _database.FinishDiscordMessage(message.Id, _clock());
        }

        foreach (var attachment in message.Attachments)
        {
            _ = ReadDiscordFileOnceAsync(message, attachment);
        }

        _ = ProcessDiscordAsync();
    }

    /// <summary>What a capture from a Discord message, or one of its files, is saved under.</summary>
    internal static string DiscordClientId(string messageId, string? attachmentId = null) =>
        attachmentId is null ? $"discord:{messageId}" : $"discord:{messageId}:{attachmentId}";

    /// <summary>The message a capture came from, by what it was saved under, for one whose reply was waiting when Waypoint closed.</summary>
    private string? DiscordMessageOf(long captureId) =>
        _database.GetCaptureClientId(captureId) is { } clientId && clientId.Split(':') is ["discord", { Length: > 0 } messageId, ..] ? messageId : null;

    /// <summary>Marks one file on a message as dealt with, finishing the message once all of them are.</summary>
    private void FinishDiscordFile(DiscordMessage message, DiscordAttachment attachment) =>
        _database.FinishDiscordPart(message.Id, attachment.Id, message.Attachments.Select(file => file.Id).ToList(), _clock());

    /// <summary>A file not already dealt with the last time the message was handed over.</summary>
    private async Task ReadDiscordFileOnceAsync(DiscordMessage message, DiscordAttachment attachment)
    {
        if (!StartDiscordFile(message, attachment))
        {
            return;
        }

        try
        {
            if (_database.IsDiscordPartDone(message.Id, attachment.Id))
            {
                return;
            }

            await ReadDiscordFileAsync(message, attachment, () => FinishDiscordFile(message, attachment));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Not marked as dealt with, so the message is handed over again later.
            _logError(exception);
        }
        finally
        {
            _discordFilesWorking.TryRemove((message.Id, attachment.Id), out _);
        }
    }

    // Files on Discord messages being read right now, so one handed over again meanwhile isn't read a second time.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Message, string File), bool> _discordFilesWorking = new();

    /// <summary>False when the file is already being read here.</summary>
    private bool StartDiscordFile(DiscordMessage message, DiscordAttachment attachment) =>
        _discordFilesWorking.TryAdd((message.Id, attachment.Id), true);

    private bool IsDiscordWorking(string messageId) => _discordFilesWorking.Keys.Any(working => working.Message == messageId);

    /// <summary>
    /// A file sent from the phone goes through the same reading as a dropped one, so it can add things but never
    /// change or finish what's already here. The downloaded copy is deleted once it's read. Once its words are
    /// saved, or the owner is told why it wasn't read, <paramref name="dealtWith"/> is called. Without Discord,
    /// such as while Waypoint is closing, it's left for the message to be handed over again.
    /// </summary>
    private async Task ReadDiscordFileAsync(DiscordMessage message, DiscordAttachment attachment, Action dealtWith)
    {
        if (Discord is not { } discord)
        {
            return;
        }

        // A voice note is captured as its words. A video stays a recording, which goes in on the PC.
        if (Transcription.VoiceNotes.IsVoiceNote(attachment.FileName, attachment.ContentType))
        {
            await ReadVoiceNoteAsync(discord, message, attachment, dealtWith);
            return;
        }

        if (Transcription.TranscriptionQueue.IsAudioFile(attachment.FileName))
        {
            await discord.ReplyAsync(message.Id, RecordingNotRead(attachment.FileName));
            dealtWith();
            return;
        }

        // Saved before Waypoint stopped, so there's nothing left to read.
        var clientId = DiscordClientId(message.Id, attachment.Id);
        if (_database.HasCaptureClientId(clientId))
        {
            dealtWith();
            return;
        }

        var folder = Path.Combine(Path.GetTempPath(), "Waypoint", "discord");
        var name = string.Concat(Path.GetFileName(attachment.FileName).Select(letter => Path.GetInvalidFileNameChars().Contains(letter) ? '_' : letter));
        var path = Path.Combine(folder, $"{attachment.Id}-{name}");
        try
        {
            Directory.CreateDirectory(folder);
            if (await discord.DownloadAsync(attachment, path, FileReader.MaxBytes) is { } problem)
            {
                await discord.ReplyAsync(message.Id, DiscordText.Notice("File not read", problem, CardTone.Attention));
                dealtWith();
                return;
            }

            // An import counts each row once, so a bank file read again only says it was already counted.
            if (await Task.Run(() => ReadStatement(path)) is { } statement)
            {
                await discord.ReplyAsync(message.Id, DiscordText.Notice("Bank file", ImportStatementsReply([statement]), CardTone.Money));
                dealtWith();
                return;
            }

            var file = await Task.Run(() => _readFile(path));
            file = file with { Name = Redaction.ApplyToName(attachment.FileName) };
            if (file.Problem is { } unreadable)
            {
                await discord.ReplyAsync(message.Id, DiscordText.Notice("File not read", unreadable, CardTone.Attention));
                dealtWith();
                return;
            }

            if (Captures.CaptureFile(file, CaptureOrigin.Discord, clientId) is { } id)
            {
                _discordReplies[id] = new DiscordReplyTo(message.Id);
            }

            dealtWith();
            await ProcessDiscordAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await discord.ReplyAsync(message.Id, DiscordText.Notice("File not read", $"Couldn't read {attachment.FileName}. Try sending it again.", CardTone.Attention));
            dealtWith();
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The system clears its temp folder in the end.
            }
        }
    }

    /// <summary>
    /// Sorts, then says so once for anything from Discord that couldn't be sorted yet, so a capture is never
    /// silently stuck. Its real answer still comes once sorting works again.
    /// </summary>
    private async Task ProcessDiscordAsync()
    {
        await Captures.ProcessPendingAsync();
        var pending = _database.GetPendingCaptures().Select(capture => capture.Id).ToHashSet();
        foreach (var (id, target) in _discordReplies)
        {
            if (!pending.Contains(id) || target.ToldWaiting || Discord is not { } discord)
            {
                continue;
            }

            _discordReplies[id] = target with { ToldWaiting = true };
            // The error itself is for the log and the PC's status line, and can be long or hold paths.
            var notice = DiscordText.Notice("Saved", "Sorting isn't working right now, so this is kept and will be sorted once it is.", CardTone.Attention);
            await discord.ReplyAsync(target.MessageId, target.Heard is { } heard ? notice with { Fields = [.. notice.Fields, DiscordText.HeardField(heard)] } : notice);
        }
    }

}
