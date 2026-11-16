using System.Globalization;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.Core.Schedule;

public sealed record RoutineStep(string Label, bool Done)
{
    /// <summary>The step's name, which is what a screen reader says for it in a list.</summary>
    public override string ToString() => Label;
}

/// <summary>Tonight's checklist, shown only during the wind-down.</summary>
public sealed record BedtimeStatus(BedtimePlan Plan, IReadOnlyList<RoutineStep> Steps);

/// <summary>
/// The per-night checklist. A step only counts when the user says so, so a reminder
/// going out never marks anything, least of all a wind-down step.
/// </summary>
public static class BedtimeRoutine
{
    public const string Teeth = "Brush teeth";
    public const string SaveProject = "Save project";
    public const string SnoozeWord = "snooze bedtime";

    private static readonly string[] TeethWords = ["brushed teeth", "brushed my teeth", "i brushed my teeth"];
    private static readonly string[] DonePrefixes = ["done ", "done: ", "did ", "did my ", "i did ", "i did my ", "finished ", "finished my ", "i finished ", "i finished my ", "took ", "took my ", "i took ", "i took my "];

    /// <summary>The two named wind-down steps, in the order they come due, each only while its reminder is on.</summary>
    public static IReadOnlyList<string> WindDownSteps(BedtimeSettings settings) =>
        new[]
        {
            settings.EveningStepMinutes is not null ? settings.EveningStepLabel() : null,
            settings.WindDownStepMinutes is not null ? settings.WindDownStepLabel() : null,
        }
        .OfType<string>()
        .ToList();

    /// <summary>What a wind-down step's button sends, the same words as typing it, so both take the same path.</summary>
    public static string DoneText(string step) => $"done {step}";

    public static IReadOnlyList<string> Steps(BedtimeSettings settings) =>
        WindDownSteps(settings)
        .Concat(new[]
        {
            Teeth,
            SaveProject,
        })
        .Concat(settings.RoutineSteps ?? [])
        .ToList();

    /// <summary>The checklist during the wind-down, or null the rest of the time. Saving a handoff counts as saving the project.</summary>
    public static BedtimeStatus? GetStatus(WaypointDatabase database, BedtimeSchedule schedule, DateTime now)
    {
        if (schedule.GetWindDown(now) is not { } windDown)
        {
            return null;
        }

        var marked = database.GetRoutineMarks(windDown.Plan.Morning);
        var saved = database.HasHandoffSince(windDown.Start);
        var steps = Steps(schedule.Settings)
            .Select(step => new RoutineStep(step, marked.Contains(step) || (step == SaveProject && saved)))
            .ToList();
        return new BedtimeStatus(windDown.Plan, steps);
    }

    /// <summary>
    /// The step a note names, or null. Exact phrases only, like "done stretch" or "stretch done" for a wind-down
    /// step named Stretch, since "didn't do the stretch" must not match.
    /// </summary>
    public static string? ParseDoneWord(string text, BedtimeSettings settings)
    {
        var phrase = text.Trim().TrimEnd('.', '!').ToLowerInvariant();
        if (TeethWords.Contains(phrase))
        {
            return Teeth;
        }

        return new[] { settings.EveningStepLabel(), settings.WindDownStepLabel() }.FirstOrDefault(step =>
        {
            var name = step.ToLowerInvariant();
            return phrase == $"{name} done" || DonePrefixes.Any(prefix => phrase == prefix + name);
        });
    }

    public static bool IsSnoozeWord(string text) =>
        text.Trim().TrimEnd('.', '!').Equals(SnoozeWord, StringComparison.OrdinalIgnoreCase);

    public static OpReply Mark(WaypointDatabase database, BedtimeSchedule? schedule, string? step, bool done, DateTime now)
    {
        if (schedule?.GetPlan(now) is not { IsSchoolNight: true } plan)
        {
            return new OpReply(NothingTonight, true);
        }

        var steps = Steps(schedule.Settings);
        if (Match(steps, step) is not { } match)
        {
            return new OpReply($"Which one? Tonight's list is {string.Join(", ", steps).ToLowerInvariant()}.", true);
        }

        database.SetRoutineMark(plan.Morning, match, done);
        var left = GetStatus(database, schedule, now)?.Steps.Where(item => !item.Done).Select(item => item.Label.ToLowerInvariant()).ToList() ?? [];
        var said = done ? $"Checked off {match.ToLowerInvariant()}." : $"Unchecked {match.ToLowerInvariant()}.";
        return new OpReply(left.Count > 0 ? $"{said} Left tonight: {string.Join(", ", left)}." : said);
    }

    public static OpReply Snooze(WaypointDatabase database, BedtimeSchedule? schedule, DateTime now)
    {
        if (schedule?.GetPlan(now) is not { IsSchoolNight: true })
        {
            return new OpReply(NothingTonight, true);
        }

        var minutes = schedule.Settings.SnoozeMinutes;
        database.BedtimeSnoozedUntil = now.AddMinutes(minutes);
        return new OpReply($"Bedtime reminder back in {minutes} minutes.");
    }

    /// <summary>Ends tonight's stages. The next night starts as usual.</summary>
    public static OpReply Dismiss(WaypointDatabase database, BedtimeSchedule? schedule, DateTime now)
    {
        if (schedule?.GetPlan(now) is not { IsSchoolNight: true } plan)
        {
            return new OpReply(NothingTonight, true);
        }

        foreach (var task in Enum.GetValues<BedtimeTask>())
        {
            database.MarkSent(plan.Morning, task);
        }

        database.BedtimeSnoozedUntil = null;
        return new OpReply("No more bedtime reminders tonight.");
    }

    public static OpReply Describe(WaypointDatabase database, BedtimeSchedule? schedule, DateTime now)
    {
        if (schedule?.GetPlan(now) is not { IsSchoolNight: true } plan)
        {
            return new OpReply(NothingTonight, true);
        }

        var bedtime = plan.Bedtime.ToString("h:mm tt", CultureInfo.InvariantCulture);
        if (GetStatus(database, schedule, now) is not { } status)
        {
            return new OpReply($"Bed by {bedtime} for {plan.Commitment}.", true);
        }

        var left = status.Steps.Where(step => !step.Done).Select(step => step.Label.ToLowerInvariant()).ToList();
        return new OpReply($"Bed by {bedtime} for {plan.Commitment}. {(left.Count > 0 ? $"Left tonight: {string.Join(", ", left)}." : "Everything's checked off.")}", true);
    }

    /// <summary>
    /// Whether a stage can stay quiet. Once the spot is saved tonight, the save-your-spot stage has nothing left
    /// to ask, so it's skipped. A missed wind-down step riding along still goes out.
    /// </summary>
    public static bool Skips(BedtimeReminder reminder, bool spotSaved) =>
        spotSaved && reminder.Task == BedtimeTask.SaveSpot && reminder.Also is not { Count: > 0 };

    /// <summary>The stages that ask for a spot get a Save my spot button, until one is saved tonight.</summary>
    public static bool OffersSaveSpot(BedtimeReminder reminder, bool spotSaved) =>
        !spotSaved && reminder.Task is BedtimeTask.SaveSpot or BedtimeTask.WrapUp;

    /// <summary>
    /// What to send for a stage. Nothing here gives advice about the steps, since those are the user's own settings.
    /// To help with the switch, it names the task in progress, and stops asking for a spot once one is saved tonight.
    /// </summary>
    public static (string Headline, string Text) Describe(BedtimeReminder reminder, bool spotSaved = false, string? working = null, BedtimeSettings? settings = null)
    {
        settings ??= new BedtimeSettings();
        var on = string.IsNullOrWhiteSpace(working) ? "" : $" on {working.Trim()}";
        var bed = $"Bed by {reminder.Plan.Bedtime.ToString("h:mm tt", CultureInfo.InvariantCulture)} for {reminder.Plan.Commitment}.";
        var also = reminder.Also is { Count: > 0 } missed
            ? $"\nAlso: {string.Join(", ", missed.Select(task => StepName(task, settings)?.ToLowerInvariant()).OfType<string>())}"
            : "";

        return reminder.Task switch
        {
            BedtimeTask.HeadsUp => ("Choose one last thing to finish", bed + also),
            BedtimeTask.SaveSpot when spotSaved => ("Your spot is saved", bed + also),
            BedtimeTask.SaveSpot => ("Let's write down where you stopped", $"Use Save my spot to write where you are{on} and what's next.\n{bed}{also}"),
            BedtimeTask.Routine => ("Time to start winding down", bed + also),
            BedtimeTask.EveningStep or BedtimeTask.WindDownStep => ($"Time for {StepName(reminder.Task, settings)?.ToLowerInvariant()}", bed + also),
            BedtimeTask.WrapUp when spotSaved => ("Last call before bed", $"Your spot is saved, so you can stop here. {bed}{also}"),
            BedtimeTask.WrapUp => ("Save your stopping point?", $"Last call before bed. {bed}{also}"),
            _ => ("Bedtime", $"Time to head to bed for {reminder.Plan.Commitment}. Snooze if you need a little longer.{also}"),
        };
    }

    /// <summary>The name of the wind-down step a stage reminds about, or null for any other stage.</summary>
    public static string? StepName(BedtimeTask task, BedtimeSettings settings) => task switch
    {
        BedtimeTask.EveningStep => settings.EveningStepLabel(),
        BedtimeTask.WindDownStep => settings.WindDownStepLabel(),
        _ => null,
    };

    private const string NothingTonight = "There's no early morning coming up, so nothing's planned for tonight.";

    private static string? Match(IReadOnlyList<string> steps, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var known = text.Contains("teeth", StringComparison.OrdinalIgnoreCase) ? Teeth
            : text.Contains("project", StringComparison.OrdinalIgnoreCase) || text.Contains("spot", StringComparison.OrdinalIgnoreCase) ? SaveProject
            : null;

        return steps.FirstOrDefault(step => step.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? steps.FirstOrDefault(step => step == known)
            ?? steps.FirstOrDefault(step => step.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase) || text.Contains(step, StringComparison.OrdinalIgnoreCase));
    }
}
