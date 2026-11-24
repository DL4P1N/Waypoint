using System.Globalization;
using Waypoint.Core.Background;
using Waypoint.Core.Calendar;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Schedule;

namespace Waypoint.Core.Remote;

/// <summary>
/// Alert email's app ID and the address Tailspin Pay emails are forwarded from, as the Settings page shows them. Neither is
/// a secret. The sign-in itself is never shown or sent.
/// </summary>
public sealed record AlertEmailForm(string? ClientId, string? OwnAddress);

/// <summary>The code to enter on Microsoft's page to sign alert email in, or why it can't start.</summary>
public sealed record EmailSignIn(string? UserCode, string? VerificationUri, string? Problem = null);

/// <summary>
/// The Discord IDs shown on the Settings page. The token itself is never shown or sent back, only whether one is saved.
/// The two forums are where each note and each list gets its own post.
/// </summary>
public sealed record DiscordForm(string? OwnerUserId, string? CaptureChannelId, string? AlertsChannelId, string? BudgetChannelId, bool HasToken, string? NotesForumChannelId = null, string? ListsForumChannelId = null);

/// <summary>
/// The settings that belong to whatever runs reminders: work hours, pay rate, bedtime, classes, Discord, and the calendar feed. In server mode
/// they live on the server. Theme, the notes folder, recording, and starting with Windows are this PC's own, so
/// the page keeps those local either way.
/// </summary>
public interface ISettingsData
{
    /// <summary>True when these are kept on the server, so the page can say where things are saved.</summary>
    bool OnServer { get; }

    IReadOnlyList<WeeklyHours> GetWeeklyHours();

    IReadOnlyList<WorkDate> GetWorkDates(DateOnly from);

    int GetCommuteMinutes();

    decimal GetHourlyRate();

    /// <summary>The bedtime in use: settings.json with the page's choices laid over it.</summary>
    BedtimeSettings GetBedtime();

    /// <summary>Tonight's plan in a sentence, worked out where reminders run.</summary>
    string DescribeTonight(DateTime now);

    /// <summary>Returns why they weren't saved, or null once they are and reminders use them.</summary>
    string? SetBedtimeChoices(BedtimeChoices choices);

    /// <summary>The classes in use and the term dates: the setup file's, with what the page saved laid over them.</summary>
    ClassesForm GetClasses();

    /// <summary>Saves the whole list of classes and the term dates, uses them straight away, and says how it went.</summary>
    string SaveClasses(ClassesForm form);

    OpReply SetWeeklyHours(IReadOnlyList<string> days, string? hours);

    OpReply RemoveWeeklyHours(IReadOnlyList<string> days);

    OpReply AddWorkShift(DateTime? start, DateTime? end);

    OpReply SetDayOff(DateTime? date);

    OpReply RemoveWorkDate(DateOnly date);

    OpReply SetCommute(string? minutes);

    OpReply SetHourlyRate(decimal? dollars);

    DiscordForm GetDiscord();

    /// <summary>Saves the IDs and the token if one was typed, reconnects, and says how it went. A blank token keeps the one there is.</summary>
    string SaveDiscord(DiscordForm form, string? token);

    Task<string> TestDiscordAsync();

    /// <summary>Whether a feed is set up and whether it was saved here. The link is never sent back.</summary>
    CalendarFeedForm GetCalendarFeed();

    /// <summary>Saves a feed link and starts reading it, and says how it went.</summary>
    string SaveCalendarFeed(string? link);

    /// <summary>Forgets the link saved here, and says what's used now.</summary>
    string RemoveCalendarFeed();

    /// <summary>Undo for Remove: the link removed last is saved again where it was kept, without being sent back.</summary>
    string PutBackCalendarFeed();

    AlertEmailForm GetAlertEmail();

    /// <summary>Saves alert email's app ID and forwarding address and uses them straight away, or says why not.</summary>
    string SaveAlertEmail(AlertEmailForm form);
}

/// <summary>
/// The settings where the worker runs: on this PC in local mode, and on the server, which answers with this same
/// class, so a change on either end goes through one set of rules.
/// </summary>
public sealed class LocalSettingsData(WaypointDatabase database, WaypointWorker worker) : ISettingsData
{
    public bool OnServer => false;

    public IReadOnlyList<WeeklyHours> GetWeeklyHours() => database.GetWeeklyHours();

    public IReadOnlyList<WorkDate> GetWorkDates(DateOnly from) => database.GetWorkDates(from);

    public int GetCommuteMinutes() => database.GetCommuteMinutes();

    public decimal GetHourlyRate() => database.GetHourlyRate();

    public BedtimeSettings GetBedtime() => worker.BedtimeSchedule.Settings;

    public string DescribeTonight(DateTime now) => BedtimeRoutine.Describe(database, worker.BedtimeSchedule, now).Text;

    public string? SetBedtimeChoices(BedtimeChoices choices)
    {
        if (database.SetBedtimeChoices(choices) is { } problem)
        {
            return problem;
        }

        // Reminders use the new choices straight away, without a restart.
        worker.BedtimeSchedule.Settings = choices.ApplyTo(worker.FileBedtime);
        return null;
    }

    public ClassesForm GetClasses() => worker.GetClasses();

    public string SaveClasses(ClassesForm form) => worker.SaveClasses(form);

    public OpReply SetWeeklyHours(IReadOnlyList<string> days, string? hours) => database.SetWeeklyHours(days, hours);

    public OpReply RemoveWeeklyHours(IReadOnlyList<string> days) => database.RemoveWeeklyHours(days);

    public OpReply AddWorkShift(DateTime? start, DateTime? end) => database.AddWorkShift(start, end);

    public OpReply SetDayOff(DateTime? date) => database.SetDayOff(date);

    public OpReply RemoveWorkDate(DateOnly date) => database.RemoveWorkDate(date);

    public OpReply SetCommute(string? minutes) => database.SetCommute(minutes);

    public OpReply SetHourlyRate(decimal? dollars) => database.SetHourlyRate(dollars);

    public DiscordForm GetDiscord() => new(
        worker.DiscordSettings?.OwnerUserId,
        worker.DiscordSettings?.CaptureChannelId,
        worker.DiscordSettings?.AlertsChannelId,
        worker.DiscordSettings?.BudgetChannelId,
        HasToken: worker.DiscordSettings is not null,
        worker.DiscordSettings?.NotesForumChannelId,
        worker.DiscordSettings?.ListsForumChannelId);

    public string SaveDiscord(DiscordForm form, string? token) =>
        worker.SaveDiscord(form.OwnerUserId, form.CaptureChannelId, form.AlertsChannelId, form.BudgetChannelId, token, form.NotesForumChannelId, form.ListsForumChannelId);

    public Task<string> TestDiscordAsync() => worker.TestDiscordAsync();

    public CalendarFeedForm GetCalendarFeed() => worker.GetCalendarFeed();

    public string SaveCalendarFeed(string? link) => worker.SaveCalendarFeed(link);

    public string RemoveCalendarFeed() => worker.RemoveCalendarFeed();

    public string PutBackCalendarFeed() => worker.PutBackCalendarFeed();

    public AlertEmailForm GetAlertEmail() => new(worker.OutlookSettings?.ClientId, worker.OutlookSettings?.OwnAddress);

    public string SaveAlertEmail(AlertEmailForm form) => worker.SaveAlertEmail(form.ClientId, form.OwnAddress);
}

/// <summary>
/// The Settings page's server half. Reads show the last answer while the server is away, and changes say they
/// can't be made yet. A Discord token typed here goes to the server over the paired connection and is kept there
/// with the server's other sign-ins, so settings.json on the server never needs editing for it.
/// </summary>
public sealed class ServerSettingsData(ServerClient server) : ISettingsData
{
    private static readonly HttpMethod Post = HttpMethod.Post;
    private static readonly HttpMethod Put = HttpMethod.Put;

    public const string ClassesPath = "api/settings/classes";

    public const string BedtimePath = "api/settings/bedtime";

    public bool OnServer => true;

    public IReadOnlyList<WeeklyHours> GetWeeklyHours() => server.GetOr<List<WeeklyHours>>("api/settings/weekly", []);

    public IReadOnlyList<WorkDate> GetWorkDates(DateOnly from) =>
        server.GetOr<List<WorkDate>>($"api/settings/dates?from={from.ToString("O", CultureInfo.InvariantCulture)}", []);

    public int GetCommuteMinutes() => (int)server.GetOr("api/settings/commute", new NumberValue(0)).Value;

    public decimal GetHourlyRate() => server.GetOr("api/settings/rate", new NumberValue(0)).Value;

    public BedtimeSettings GetBedtime() => server.GetOr(BedtimePath, new BedtimeSettings());

    public string DescribeTonight(DateTime now) => server.GetOr("api/settings/tonight", new TextValue(null)).Value ?? "";

    public string? SetBedtimeChoices(BedtimeChoices choices) => server.Send<TextValue>(Put, "api/settings/bedtime", choices).Value;

    public ClassesForm GetClasses() => server.GetOr(ClassesPath, new ClassesForm(null, null, []));

    public string SaveClasses(ClassesForm form) => server.Send<TextValue>(Put, ClassesPath, form).Value ?? "";

    public OpReply SetWeeklyHours(IReadOnlyList<string> days, string? hours) => server.Send<OpReply>(Post, "api/settings/weekly", new WeeklyHoursRequest(days, hours));

    public OpReply RemoveWeeklyHours(IReadOnlyList<string> days) => server.Send<OpReply>(Post, "api/settings/weekly/remove", new WeeklyHoursRequest(days, null));

    public OpReply AddWorkShift(DateTime? start, DateTime? end) => server.Send<OpReply>(Post, "api/settings/shifts", new ShiftRequest(start, end));

    public OpReply SetDayOff(DateTime? date) => server.Send<OpReply>(Post, "api/settings/dayoff", new ShiftRequest(date, null));

    public OpReply RemoveWorkDate(DateOnly date) =>
        server.Send<OpReply>(HttpMethod.Delete, $"api/settings/dates/{date.ToString("O", CultureInfo.InvariantCulture)}");

    public OpReply SetCommute(string? minutes) => server.Send<OpReply>(Put, "api/settings/commute", new TextValue(minutes));

    public OpReply SetHourlyRate(decimal? dollars) => server.Send<OpReply>(Put, "api/settings/rate", new RateRequest(dollars));

    public DiscordForm GetDiscord() => server.GetOr("api/settings/discord", new DiscordForm(null, null, null, null, false));

    /// <summary>Saving starts Discord again on the server, which can take longer than a page read waits.</summary>
    public string SaveDiscord(DiscordForm form, string? token) =>
        server.SendLong<TextValue>(Put, "api/settings/discord", new DiscordSaveRequest(form.OwnerUserId, form.CaptureChannelId, form.AlertsChannelId, form.BudgetChannelId, token, form.NotesForumChannelId, form.ListsForumChannelId), "the Discord status here").Value ?? "";

    /// <summary>Off the window's thread, since the server waits on Discord, rate limits included, before it answers.</summary>
    public Task<string> TestDiscordAsync() =>
        Task.Run(() => server.SendLong<TextValue>(Post, "api/settings/discord/test", null, "Discord for the test message").Value ?? "");

    public CalendarFeedForm GetCalendarFeed() => server.GetOr("api/settings/calendar", new CalendarFeedForm(false, false));

    public string SaveCalendarFeed(string? link) => server.Send<TextValue>(Put, "api/settings/calendar", new TextValue(link)).Value ?? "";

    public string RemoveCalendarFeed() => server.Send<TextValue>(HttpMethod.Delete, "api/settings/calendar").Value ?? "";

    /// <summary>A server from before Undo was added doesn't know the call, so it says the server needs updating.</summary>
    public string PutBackCalendarFeed()
    {
        try
        {
            return server.Send<TextValue>(Post, "api/settings/calendar/put-back").Value ?? "";
        }
        catch (ServerRefusedException refused) when (refused.Status is 404 or 405)
        {
            return PutBackNeedsNewerServer;
        }
    }

    public const string PutBackNeedsNewerServer = "The server needs updating before Undo can put the feed link back. Paste the link again to use it.";

    public AlertEmailForm GetAlertEmail() => server.GetOr("api/settings/email", new AlertEmailForm(null, null));

    public string SaveAlertEmail(AlertEmailForm form) => server.Send<TextValue>(Put, "api/settings/email", form).Value ?? "";

    /// <summary>Starts alert email's sign-in on the server, which finishes it there once the code is entered.</summary>
    public EmailSignIn ConnectAlertEmail() => server.SendLong<EmailSignIn>(Post, "api/email/connect", null, "the Alert email line on the Settings page");
}

public sealed record NumberValue(decimal Value);

public sealed record RateRequest(decimal? Dollars);

public sealed record WeeklyHoursRequest(IReadOnlyList<string> Days, string? Hours);

public sealed record ShiftRequest(DateTime? Start, DateTime? End);

/// <summary>The Settings page's Discord form. The token is only ever sent this way, never read back.</summary>
public sealed record DiscordSaveRequest(string? OwnerUserId, string? CaptureChannelId, string? AlertsChannelId, string? BudgetChannelId, string? Token, string? NotesForumChannelId = null, string? ListsForumChannelId = null);
