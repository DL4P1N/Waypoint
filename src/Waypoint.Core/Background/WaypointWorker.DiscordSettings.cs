using System.Globalization;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;
using Waypoint.Core.Remote;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Background;

/// <summary>
/// /settings and /order on the phone. Settings change through the same calls as the PC's Settings page,
/// and order through the same calls the PC's drag and drop makes. No token or other secret is ever shown or taken here.
/// </summary>
public sealed partial class WaypointWorker
{
    private const string OrderMenu = DiscordBridge.ViewPrefix + "order";
    private const string OrderAction = "order:";
    private const string SettingsBedtime = "settings-bedtime";
    private const string SettingsHours = "settings-hours";
    private const int MaxConnectionText = 300;

    // ----- Settings -----

    /// <summary>The key settings, with buttons for the simple ones. <paramref name="said"/> is how the last change went.</summary>
    private DiscordCard SettingsCard(string? said)
    {
        var settings = new LocalSettingsData(_database, this);
        var hours = settings.GetWeeklyHours();
        var work = hours.Count == 0
            ? "None set"
            : string.Join("\n", hours.OrderBy(day => ((int)day.Day + 6) % 7).Select(day =>
                $"{CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(day.Day)} {Clock(day.Start)} to {Clock(day.End)}"));

        var bedtime = settings.GetBedtime();
        var sleep = $"Sleep {bedtime.SleepHours.ToString("0.#", CultureInfo.InvariantCulture)} hours, {bedtime.ReadyMinutes.ToString(CultureInfo.InvariantCulture)} minutes to get ready";
        if (bedtime.LatestBedtime is { } latest)
        {
            sleep += $", in bed by {Clock(latest)} at the latest";
        }

        var tonight = settings.DescribeTonight(_clock());
        var fields = new List<CardField>
        {
            new("Work hours", Shorten(work, 1024), false),
            new("Bedtime", Shorten(DiscordText.Escape(tonight.Length > 0 ? $"{sleep}\n{tonight}" : sleep), 1024), false),
            new("Theme", $"{_database.GetTheme().Name}. Change it on the PC, since it's kept there.", false),
        };

        // Only how each connection is doing. Tokens, links, and sign-ins are never shown, and neither is an error's
        // own text, which is meant for the log and can hold addresses or codes.
        fields.AddRange(DescribeConnections().Select(connection => new CardField(DiscordText.Escape(connection.Name), Shorten(DiscordText.Escape(PhoneState(connection.State)), MaxConnectionText), false)));

        return new DiscordCard("Settings", said is null ? null : DiscordText.Escape(said), fields.Take(25).ToList(), CardTone.Reminder)
        {
            Actions =
            [
                new CardAction("Change bedtime", SettingsBedtime, ButtonRole.Other),
                new CardAction("Change work hours", SettingsHours, ButtonRole.Other),
            ],
        };
    }

    /// <summary>A connection's state as the phone shows it: the Settings page's words, without any error's own text.</summary>
    private static string PhoneState(string state)
    {
        var cut = state.IndexOf(LastTryFailed, StringComparison.Ordinal);
        var shown = cut < 0 ? state : state[..cut] + " The last try didn't work.";
        return shown.Replace(" below.", " on the PC's Settings page.", StringComparison.Ordinal);
    }

    private static string Clock(TimeOnly time) => time.ToString("h:mm tt", CultureInfo.InvariantCulture);

    private CardForm BedtimeForm()
    {
        var bedtime = BedtimeSchedule.Settings;
        return new CardForm(SettingsBedtime, "Bedtime",
        [
            new FormBox("sleep", "Hours of sleep", Required: true, Value: bedtime.SleepHours.ToString("0.#", CultureInfo.InvariantCulture), MaxLength: 5),
            new FormBox("ready", "Minutes to get ready for bed", Required: true, Value: bedtime.ReadyMinutes.ToString(CultureInfo.InvariantCulture), MaxLength: 4),
            new FormBox("latest", "Latest bedtime", Value: bedtime.LatestBedtime is { } latest ? Clock(latest) : null, MaxLength: 10, Placeholder: "Like 1:00 AM, or leave empty"),
        ]);
    }

    private static CardForm WorkHoursForm() => new(SettingsHours, "Work hours",
    [
        new FormBox("days", "Days", Required: true, MaxLength: 100, Placeholder: "Like Mon, Wed, Fri"),
        new FormBox("hours", "Hours, or off to clear those days", Required: true, MaxLength: 40, Placeholder: "Like 9am-5pm"),
    ]);

    /// <summary>A settings form was sent. Null when the form isn't one of these.</summary>
    private CommandReply? OnSettingsForm(string id, IReadOnlyDictionary<string, string> values)
    {
        string? Box(string name) => values.GetValueOrDefault(name)?.Trim() is { Length: > 0 } value ? value : null;
        var settings = new LocalSettingsData(_database, this);
        string said;
        if (id == SettingsBedtime)
        {
            if (!double.TryParse(Box("sleep"), NumberStyles.Float, CultureInfo.InvariantCulture, out var sleep) || !double.IsFinite(sleep) || !int.TryParse(Box("ready"), NumberStyles.None, CultureInfo.InvariantCulture, out var ready))
            {
                return new CommandReply(SettingsCard("Give the hours of sleep as a number like 8, and the minutes to get ready as a number like 45."), true);
            }

            string? latest = null;
            if (Box("latest") is { } typed)
            {
                if (TypedDates.ParseTime(typed) is not { } time)
                {
                    return new CommandReply(SettingsCard("Try a latest bedtime like 1:00 AM."), true);
                }

                latest = BedtimeChoices.FormatTime(time);
            }

            // Everything else stays as it was chosen on the PC.
            var choices = _database.GetBedtimeChoices() with { SleepHours = sleep, ReadyMinutes = ready, LatestBedtime = latest };
            said = settings.SetBedtimeChoices(choices) ?? "Bedtime saved.";
        }
        else if (id == SettingsHours)
        {
            var days = (Box("days") ?? "").Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var hours = Box("hours") ?? "";
            if (string.Equals(hours, "off", StringComparison.OrdinalIgnoreCase))
            {
                said = settings.RemoveWeeklyHours(days).Text;
            }
            else
            {
                // Read the way the Settings page reads its two time boxes, then saved the same way.
                var ends = hours.Split(['-', '–'], 2, StringSplitOptions.TrimEntries);
                if (ends.Length != 2 && hours.Split(" to ", 2, StringSplitOptions.TrimEntries) is { Length: 2 } worded)
                {
                    ends = worded;
                }

                if (ends.Length != 2 || TypedDates.ParseTime(ends[0]) is not { } start || TypedDates.ParseTime(ends[1]) is not { } end)
                {
                    return new CommandReply(SettingsCard("Try hours like 9am-5pm, or off to clear those days."), true);
                }

                said = settings.SetWeeklyHours(days, $"{start.ToString("HH:mm", CultureInfo.InvariantCulture)}-{end.ToString("HH:mm", CultureInfo.InvariantCulture)}").Text;
            }
        }
        else
        {
            return null;
        }

        _host.Changed();
        return new CommandReply(SettingsCard(said), true);
    }

    // ----- Order -----

    private sealed record Orderable(string Key, string Name, int Depth);

    private static string OrderLabel(string what) => what switch
    {
        "filters" => "To do filters",
        "folders" => "Note folders",
        _ => "Lists",
    };

    /// <summary>What can be put in order, in its order now. Shopping always stays first, so it isn't offered.</summary>
    private List<Orderable> Orderables(string what) => what switch
    {
        "filters" => _database.GetToDoFilters().Select(filter => new Orderable(filter.Id.ToString(CultureInfo.InvariantCulture), filter.Name, 0)).ToList(),
        "folders" => FolderOrder(_database.GetNoteFolders(), 0).ToList(),
        _ => _database.GetListNames().Where(name => !string.Equals(name, WaypointDatabase.ShoppingList, StringComparison.OrdinalIgnoreCase)).Select(name => new Orderable(name, name, 0)).ToList(),
    };

    private static IEnumerable<Orderable> FolderOrder(IEnumerable<NoteFolder> folders, int depth) =>
        folders.SelectMany(folder => FolderOrder(folder.Children, depth + 1).Prepend(new Orderable(folder.Id.ToString(CultureInfo.InvariantCulture), folder.Name, depth)));

    private CommandReply OrderCommand(SlashCommand command)
    {
        var what = command.Options.GetValueOrDefault("what") is "filters" or "folders" ? command.Options["what"] : "lists";
        return new CommandReply(OrderCard(what, null, null), true);
    }

    /// <summary>The order now, a menu to pick one, and Move up and Move down for the one picked.</summary>
    private DiscordCard OrderCard(string what, string? selected, string? said)
    {
        var all = Orderables(what);
        var shown = all.Take(DiscordPages.MaxItems).ToList();
        var lines = shown.Select(each =>
        {
            var indent = string.Concat(Enumerable.Repeat("› ", each.Depth));
            var name = DiscordText.Escape(each.Name);
            return Shorten(each.Key == selected ? $"{indent}**{name}** (picked)" : $"{indent}{name}", 200);
        }).ToList();
        if (all.Count > shown.Count)
        {
            lines.Add($"and {all.Count - shown.Count} more, which can be moved on the PC");
        }

        var description = all.Count == 0 ? "There's nothing to put in order yet." : string.Join("\n", lines);
        if (said is not null)
        {
            description = $"{DiscordText.Escape(said)}\n\n{description}";
        }

        var actions = selected is null || !all.Any(each => each.Key == selected)
            ? []
            : new List<CardAction>
            {
                new("Move up", $"{OrderAction}{what}:{Carry(selected)}:up", ButtonRole.Other),
                new("Move down", $"{OrderAction}{what}:{Carry(selected)}:down", ButtonRole.Other),
            };
        return new DiscordCard($"Order of {OrderLabel(what).ToLowerInvariant()}", description, [], CardTone.Reminder)
        {
            Menu = new DiscordMenu($"{OrderMenu}:{what}", "Pick one to move", shown.Select(each => new MenuChoice(each.Name, Carry(each.Key))).ToList()),
            MoreRows = DiscordViews.Rows(actions),
        };
    }

    private DiscordCard? OnOrderPicked(string menu, IReadOnlyList<string> values)
    {
        var what = menu[(OrderMenu.Length + 1)..];
        return values.Count > 0 && Uncarry(values[0]) is { } key ? OrderCard(what, key, null) : OrderCard(what, null, "That one is gone now.");
    }

    /// <summary>Move up or Move down, through the same calls the PC's drag and drop makes.</summary>
    private DiscordCard? OnOrderAction(string action)
    {
        if (!action.StartsWith(OrderAction, StringComparison.Ordinal))
        {
            return null;
        }

        // What it's about comes first and the direction last. What's between is the name, which may hold colons too.
        var rest = action[OrderAction.Length..];
        var first = rest.IndexOf(':');
        var last = rest.LastIndexOf(':');
        if (first < 0 || last <= first || Uncarry(rest[(first + 1)..last]) is not { } key)
        {
            return OrderCard(first < 0 ? rest : rest[..first], null, "That button has run out. Pick it again.");
        }

        var what = rest[..first];
        var up = rest[(last + 1)..] == "up";
        var moved = what switch
        {
            "filters" => MoveFilter(key, up),
            "folders" => MoveFolder(key, up),
            _ => MoveList(key, up),
        };
        if (moved)
        {
            _host.Changed();
            ViewsChanged();
        }

        var name = Orderables(what).FirstOrDefault(each => each.Key == key)?.Name ?? "That one";
        return OrderCard(what, key, moved ? $"Moved {name} {(up ? "up" : "down")}." : $"{name} can't go any further {(up ? "up" : "down")}.");
    }

    /// <summary>Filters only move among their own kind: classes, then Personal, then the user's own.</summary>
    private bool MoveFilter(string key, bool up)
    {
        var filters = _database.GetToDoFilters().ToList();
        var index = filters.FindIndex(filter => filter.Id.ToString(CultureInfo.InvariantCulture) == key);
        var other = index + (up ? -1 : 1);
        if (index < 0 || other < 0 || other >= filters.Count || filters[other].Source != filters[index].Source)
        {
            return false;
        }

        (filters[index], filters[other]) = (filters[other], filters[index]);
        _database.SetToDoFilterOrder(filters.Select(filter => filter.Id).ToList());
        return true;
    }

    private bool MoveList(string name, bool up)
    {
        var names = Orderables("lists").Select(each => each.Key).ToList();
        var index = names.IndexOf(name);
        var other = index + (up ? -1 : 1);
        if (index < 0 || other < 0 || other >= names.Count)
        {
            return false;
        }

        (names[index], names[other]) = (names[other], names[index]);
        _database.OrderLists(names);
        return true;
    }

    /// <summary>A folder moves among the folders beside it, never into or out of another.</summary>
    private bool MoveFolder(string key, bool up)
    {
        if (!long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || _database.GetNoteFolder(id) is not { } folder)
        {
            return false;
        }

        var siblings = folder.ParentId is { } parent ? _database.GetNoteFolder(parent)?.Children ?? [] : _database.GetNoteFolders();
        var index = siblings.ToList().FindIndex(each => each.Id == id);
        var other = index + (up ? -1 : 1);
        return index >= 0 && other >= 0 && other < siblings.Count && _database.MoveNoteFolder(id, folder.ParentId, other);
    }
}
