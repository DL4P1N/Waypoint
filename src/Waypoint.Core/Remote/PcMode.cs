using System.Globalization;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;

namespace Waypoint.Core.Remote;

/// <summary>How the PC app runs, from the server part of settings.json.</summary>
public enum PcRun
{
    /// <summary>On its own database, with alerts, Discord, email, and the calendar.</summary>
    Local,

    /// <summary>Through the server, which sends everything.</summary>
    Server,

    /// <summary>Server mode with no address yet. Nothing that sends is started, so nothing goes out twice.</summary>
    NeedsAddress,
}

public static class PcMode
{
    /// <summary>The mode the PC last ran in, kept in its own database, so going back to Local can be noticed.</summary>
    public const string LastModeKey = "pc_last_mode";

    public const string NeedsAddressNotice =
        "Server mode is on, but settings.json has no server address, so this PC sends nothing: no reminders, Discord, email, or calendar. "
        + "What you type waits on this PC and goes to the server once the address is set. "
        + "Add the address under \"server\" in settings.json, then restart Waypoint.";

    /// <summary>Where captures typed in server mode wait for the server. Server mode with no address uses it too.</summary>
    public const string QueueFileName = "server-queue.json";

    /// <summary>Set when Discord was saved on Settings while there was no address, so it's sent to the server once there is.</summary>
    public const string HeldDiscordKey = "discord_held_for_server";

    public const string HeldDiscordReply =
        "Saved on this PC. Nothing connects here while server mode has no address. These go to the server once the server address is set and it's reached.";

    /// <summary>The tray's tip, which has less room.</summary>
    public const string NeedsAddressTray = "Waypoint: add the server address to settings.json";

    private const string ServerValue = "server";

    // Discord's IDs count milliseconds from the start of 2015.
    private const long DiscordEpochMilliseconds = 1420070400000;

    public static PcRun Choose(ServerSettings? server) => server switch
    {
        { Mode: DataMode.Server, Address: { } address } when !string.IsNullOrWhiteSpace(address) => PcRun.Server,
        { Mode: DataMode.Server } => PcRun.NeedsAddress,
        _ => PcRun.Local,
    };

    /// <summary>Whether reminders, alerts, Discord, email, and the calendar are started on this PC.</summary>
    public static bool Sends(PcRun run) => run == PcRun.Local;

    /// <summary>
    /// Remembers the mode at each start. On the first Local start after server mode, Discord's reading place is moved
    /// to now, so captures and replies the server already handled aren't taken again from where this PC left off.
    /// Returns true when it moved.
    /// </summary>
    public static bool Started(WaypointDatabase database, PcRun run, DateTimeOffset now)
    {
        if (run != PcRun.Local)
        {
            database.SetPreference(LastModeKey, ServerValue);
            return false;
        }

        if (database.GetPreference(LastModeKey) != ServerValue)
        {
            return false;
        }

        var place = Snowflake(now).ToString(CultureInfo.InvariantCulture);
        database.SetPreference(DiscordBridge.LastMessageKey, place);
        database.SetPreference(DiscordBridge.LastAlertKey, place);
        database.SetPreference(LastModeKey, "local");
        return true;
    }

    /// <summary>The queue typing goes to while server mode has no address, or null when typing is handled as usual.</summary>
    public static CaptureQueue? HeldCaptures(PcRun run, string dataDirectory) =>
        run == PcRun.NeedsAddress ? new CaptureQueue(Path.Combine(dataDirectory, QueueFileName)) : null;

    /// <summary>
    /// Sends Discord settings saved while there was no address to the server, once, and returns its answer. Null when
    /// nothing was waiting. If the server can't be reached it throws, and they're sent on a later try.
    /// </summary>
    public static string? SendHeldDiscord(WaypointDatabase database, string? token, Func<DiscordForm, string?, string> save)
    {
        if (database.GetPreference(HeldDiscordKey) != "1")
        {
            return null;
        }

        string? Saved(string key) => database.GetPreference(key) is { Length: > 0 } value ? value : null;
        var form = new DiscordForm(Saved(DiscordSetup.OwnerKey), Saved(DiscordSetup.CaptureKey), Saved(DiscordSetup.AlertsKey), Saved(DiscordSetup.BudgetKey), token is not null, Saved(DiscordSetup.NotesForumKey), Saved(DiscordSetup.ListsForumKey));
        var reply = save(form, token);
        database.SetPreference(HeldDiscordKey, "");
        return reply;
    }

    /// <summary>The smallest Discord ID made at <paramref name="at"/>. Anything sent after it has a larger one.</summary>
    public static ulong Snowflake(DateTimeOffset at) =>
        (ulong)Math.Max(0, at.ToUnixTimeMilliseconds() - DiscordEpochMilliseconds) << 22;
}
