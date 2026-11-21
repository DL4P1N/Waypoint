using System.Globalization;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Discord;
using Waypoint.Core.Files;

namespace Waypoint.Core.Background;

/// <summary>Slash commands, bank exports sent from Discord, and replies typed there.</summary>
public sealed partial class WaypointWorker
{
    /// <summary>A bank export as text, or null if the file isn't one. Small files only, the same limit as any file.</summary>
    public static string? ReadStatement(string path)
    {
        if (!path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || !File.Exists(path) || new FileInfo(path).Length > FileReader.MaxBytes)
        {
            return null;
        }

        try
        {
            var csv = File.ReadAllText(path);
            return CsvImport.IsStatement(csv) ? csv : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Imports bank exports and says what came of it.</summary>
    public string ImportStatementsReply(IEnumerable<string> statements)
    {
        // Each file is its own import, so one that can't be read doesn't hide the ones that were.
        var results = new List<ImportResult>();
        var problems = new List<string>();
        // The PC reads each file lazily as it's imported, so moving to the next one is inside the try too.
        using var files = statements.GetEnumerator();
        while (true)
        {
            try
            {
                if (!files.MoveNext())
                {
                    break;
                }

                results.Add(_database.ImportStatement(files.Current));
            }
            catch (Exception exception) when (exception is FormatException or IOException or UnauthorizedAccessException)
            {
                problems.Add(exception.Message);
            }
        }

        if (results.Count == 0)
        {
            return $"Couldn't import that file. {string.Join(" ", problems.Distinct())}".TrimEnd();
        }

        var waitsResolved = results.Sum(result => result.WaitsResolved);
        _host.Post(_host.Changed);
        var added = results.Sum(result => result.Added);
        var skipped = results.Sum(result => result.Skipped);
        // Where they went comes first. Ones already counted are only mentioned when there are some.
        var counted = skipped == 1 ? "1 was already counted." : $"{skipped} were already counted.";
        var reply = added > 0
            ? $"Added {added} {(added == 1 ? "purchase" : "purchases")} to Budget.{(skipped > 0 ? $" {counted}" : "")}"
            : skipped > 0
                ? $"Nothing new. {(skipped == 1 ? "That one was" : $"All {skipped} were")} already counted."
                : "There was nothing to add from that file.";
        reply += waitsResolved > 0 ? $" {waitsResolved} from your wait list marked bought." : "";
        return problems.Count == 0
            ? reply
            : $"{reply} {(problems.Count == 1 ? "1 file" : $"{problems.Count} files")} couldn't be imported. {string.Join(" ", problems.Distinct())}";
    }

    /// <summary>Slash commands. They only read or do small things, so they answer straight away without the sorter.</summary>
    public CommandReply OnDiscordCommand(SlashCommand command)
    {
        var now = _clock();
        var today = DateOnly.FromDateTime(now);
        CommandReply reply;
        switch (command.Name)
        {
            case "help":
                reply = new CommandReply(DiscordViews.HelpCard(), true);
                break;
            case "now":
                reply = DescribeNow(now);
                break;
            case "today":
                var safe = _database.GetSafeToSpend(today);
                var attention = BudgetReport.Attention(_database.GetBudgetReport(today));
                var briefing = string.Join("\n", new[] { Briefing.Build(now, _database.GetOpenItems(), Schedule, safe, _database.GetStaleBalances(today)) }.Concat(attention));
                reply = new CommandReply(DiscordText.Notice("Today", briefing));
                break;
            case "budget":
                reply = new CommandReply(BudgetWithPurchases(today));
                break;
            case "afford":
                reply = decimal.TryParse(command.Options.GetValueOrDefault("amount"), NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) && amount > 0
                    ? new CommandReply(DiscordText.Notice("Can I afford it?", _database.CheckAffordable((long)Math.Round(amount * 100, MidpointRounding.AwayFromZero), null, today).Text, CardTone.Money))
                    : new CommandReply(DiscordText.Notice("Can I afford it?", "Try an amount like 65 or 12.50."), true);
                break;
            case "snooze":
                var minutes = int.TryParse(command.Options.GetValueOrDefault("minutes"), out var asked) && asked > 0 ? asked : DefaultSnoozeMinutes;
                var latest = _database.LatestSentReminderMessage();
                var snoozed = _database.ApplyAlertAction(new AlertAction(latest, AlertActionKind.Snooze), now, minutes);

                // The card it was about turns gray too, so its buttons can't act on it a second time.
                if (snoozed is not null && latest is not null && Discord is { } bridge)
                {
                    _ = bridge.MarkHandledAsync(latest, snoozed);
                }

                reply = new CommandReply(DiscordText.Notice(snoozed ?? "No reminder is waiting to be snoozed.", "", CardTone.Finished), true);
                break;
            case "save-spot" or "savespot":
                reply = new CommandReply(DiscordText.Notice(SaveSpotFromDiscord(null, command.Options.GetValueOrDefault("stopped"), command.Options.GetValueOrDefault("next")), "", CardTone.Finished), true);
                break;
            default:
                reply = OnPhoneCommand(command, now) ?? new CommandReply(DiscordViews.HelpCard("That one's gone, so here's what there is"), true);
                break;
        }

        _host.Changed();
        ViewsChanged();
        return reply;
    }

    /// <summary>One next step: the session that's going, or else the most pressing thing, with a Start button.</summary>
    private CommandReply DescribeNow(DateTime now)
    {
        if (_database.GetActiveSession(now) is { Status: SessionStatus.Active } session)
        {
            return new CommandReply(DiscordText.Notice(session.TaskTitle, Activation.JobReply(session.Action)) with { HasSessionButtons = true, CanTrial = session.CanTrial });
        }

        if (Activation.PickTask(_database.WithoutQuietWaiting(_database.GetOpenItems(), DateOnly.FromDateTime(now)), null) is not { } item)
        {
            return new CommandReply(DiscordText.Notice("Nothing's waiting", "There's nothing due to work on right now."));
        }

        var card = DiscordText.Reminder(new DueReminder(item.Id, item, null, null), DateOnly.FromDateTime(now), false);
        return new CommandReply(card with { Title = $"Next: {card.Title}", HasStartNext = true, FeedbackFeature = Feedback.Suggestion, FeedbackRef = ItemRef.Format(item) });
    }

    /// <summary>Replies in Discord to something typed there, so the same reply never shows up in both places.</summary>
    public void ReplyInDiscord(CaptureOutcome outcome, string reply)
    {
        // A button's answer already replaced the card that was pressed.
        if (outcome.CaptureId is { } answered && _answeredOnCard.TryRemove(answered, out _))
        {
            return;
        }

        var card = DiscordText.Reply(reply.Length > 0 ? reply : "Saved.");
        card = outcome.Activation is { } running ? card with { HasSessionButtons = true, CanTrial = running.CanTrial } : card;
        var target = outcome.CaptureId is { } captureId && _discordReplies.TryRemove(captureId, out var found) ? found : null;

        // One saved before a restart still answers under the message it came from.
        target ??= outcome.CaptureId is { } saved && DiscordMessageOf(saved) is { } from ? new DiscordReplyTo(from) : null;

        // A voice note's reply shows what was heard, so a mis-hearing can be spotted.
        if (target?.Heard is { } heard)
        {
            card = card with { Fields = [.. card.Fields, DiscordText.HeardField(heard)] };
        }

        if (Discord is not { } discord)
        {
            return;
        }

        // Tried again if it doesn't go, with the same nonce each time, so a try that went through unheard isn't posted twice.
        var nonce = outcome.CaptureId is { } capture ? $"r{capture}{Hash(reply)[..10]}" : Guid.NewGuid().ToString("N")[..24];
        _ = AlertRetry.RunAsync(async () => (await discord.DeliverReplyAsync(target?.MessageId, card, nonce)).Sent, RetryWait);
    }

    /// <summary>
    /// /save-spot and the Save my spot form on a card. The spot is for what's named, else the session going now. It
    /// saves locally with the same form save as the PC.
    /// </summary>
    private string SaveSpotFromDiscord(string? project, string? stopped, string? next)
    {
        var now = _clock();
        var name = string.IsNullOrWhiteSpace(project) ? _database.GetActiveSession(now)?.TaskTitle : project;
        return _database.SaveSpotByHand(name, stopped, next, null, null, now).Text;
    }

}
