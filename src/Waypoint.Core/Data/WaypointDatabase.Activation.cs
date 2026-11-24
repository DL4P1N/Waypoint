using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;

namespace Waypoint.Core.Data;

public sealed partial class WaypointDatabase
{
    private const string SessionColumns = "id, task_ref, task_title, action, status, started_at, begun_at, stopping_note, resource, method, step_id, queue, prompt_at, smaller_count";
    private const string HandoffColumns = "id, project, stopped, next, links, thoughts, created_at";
    private const string TrialPrompt = "trial";
    private const string ResumePrompt = "resume";
    private const int TrialMinutes = 2;
    private const string UnnamedProject = "My work";

    private static readonly TimeSpan StaleTrial = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan HandoffQuestionWindow = TimeSpan.FromHours(1);

    private void CreateActivationTables()
    {
        Execute("""
        CREATE TABLE IF NOT EXISTS activation_sessions (
            id INTEGER PRIMARY KEY,
            task_ref TEXT,
            task_title TEXT NOT NULL,
            action TEXT NOT NULL,
            status TEXT NOT NULL,
            started_at TEXT NOT NULL,
            begun_at TEXT,
            ended_at TEXT,
            stopping_note TEXT,
            resource TEXT,
            method TEXT NOT NULL,
            step_id INTEGER,
            queue TEXT NOT NULL DEFAULT '[]',
            prompt_at TEXT,
            prompt_kind TEXT,
            smaller_count INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS handoffs (
            id INTEGER PRIMARY KEY,
            capture_id INTEGER,
            project TEXT NOT NULL,
            stopped TEXT,
            next TEXT,
            links TEXT NOT NULL DEFAULT '[]',
            thoughts TEXT NOT NULL DEFAULT '[]',
            created_at TEXT NOT NULL
        );
        """);

        // When anything was last done on a session, and whether it was set aside for going stale.
        AddColumnIfMissing("activation_sessions", "touched_at", "TEXT");
        AddColumnIfMissing("activation_sessions", "went_stale", "INTEGER NOT NULL DEFAULT 0");
    }

    /// <summary>
    /// The session going right now. Catches it up first, since items can be closed from anywhere. One left alone for
    /// half a day, or since before today, counts as paused, so an old Start doesn't hide the suggestion for days.
    /// </summary>
    public ActivationSession? GetActiveSession(DateTime now)
    {
        if (ReadSession($"status = '{SessionStatus.Active}'") is not { } session)
        {
            return null;
        }

        if (Activation.IsStale(LastActivity(session), now))
        {
            Execute(session.Id, "status = $text, went_stale = 1", ("$text", SessionStatus.Paused.ToString()));
            return null;
        }

        var task = FindTask(session);
        if (session.TaskRef is not null && task is null or { Done: true })
        {
            EndSession(session.Id, SessionStatus.Finished, now);
            return null;
        }

        if (session.StepId is { } stepId && session.Queue.Count == 0 && FindStep(stepId) is null or { Done: true })
        {
            return MoveToNextStep(session, task, now);
        }

        return session;
    }

    /// <summary>Starts on a task, or picks one. A task with steps uses them, so the sorter's actions only fill in for a task without.</summary>
    public OpReply StartActivation(string? taskRef, string? firstAction, IReadOnlyList<string>? then, string method, DateTime now, string? note = null)
    {
        if (PlanStart(taskRef, firstAction, then, now, note) is not { } plan)
        {
            return new OpReply(Activation.NothingToPick, true);
        }

        using var lease = Connect(out var connection);
        using var transaction = Begin(connection);
        var reply = ApplyStart(connection, plan, method, now);
        transaction.Commit();
        return reply;
    }

    /// <summary>
    /// Start on a reminder card: the card is taken, the answer is kept for the suggestion's habits, and the session
    /// starts, all in one write, so a failure partway leaves the card to be pressed again. Null when the card was
    /// already answered or its item is done.
    /// </summary>
    public (StoredItem Item, OpReply Reply)? StartFromReminder(string messageId, DateTime now)
    {
        StoredItem? found;
        using (Connect(out var read))
        using (var find = read.CreateCommand())
        {
            find.CommandText = "SELECT item_id, source FROM sent_reminders WHERE handled = 0 AND message_id = $message";
            find.Parameters.AddWithValue("$message", messageId);
            using var reader = find.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            found = FindItem(read, Enum.Parse<ItemSource>(reader.GetString(1)), reader.GetInt64(0));
        }

        if (found is not { Done: false } item)
        {
            return null;
        }

        var plan = PlanStart(ItemRef.Format(item), null, null, now, null);
        using var lease = Connect(out var connection);
        using var transaction = Begin(connection);

        // Two taps of Start at once only begin once, and an item finished meanwhile isn't started.
        if (!ClaimSentReminder(connection, messageId) || FindItem(connection, item.Source, item.Id) is not { Done: false })
        {
            return null;
        }

        RecordReminderAnswer(connection, item, ReminderAnswer.Started, now);
        var reply = plan is null ? new OpReply(Activation.NothingToPick, true) : ApplyStart(connection, plan, Activation.ReminderMethod, now);
        transaction.Commit();
        return (item, reply);
    }

    /// <summary>
    /// What starting comes to, read before anything is written: the task, a paused session of its own to pick up,
    /// or the action, steps, and link a new one opens on. A session going now is caught up first, as it always is.
    /// </summary>
    private sealed record StartPlan(StoredItem Task, ActivationSession? Paused, string Action, List<string> Queue, long? StepId, string? Resource, Handoff? Spot);

    private StartPlan? PlanStart(string? taskRef, string? firstAction, IReadOnlyList<string>? then, DateTime now, string? note)
    {
        var open = GetOpenItems();
        var named = ItemRef.TryParse(taskRef, out var source, out var id) ? open.FirstOrDefault(item => item.Source == source && item.Id == id) : null;
        if ((named ?? Activation.PickTask(WithoutQuietWaiting(open, DateOnly.FromDateTime(now)), note)) is not { } task)
        {
            return null;
        }

        // Starting the task going now picks it up where it is, the same as one that was paused.
        var reference = ItemRef.Format(task);
        var active = GetActiveSession(now);
        var paused = active?.TaskRef == reference ? active : ReadSession($"status = '{SessionStatus.Paused}' AND task_ref = '{reference}'");

        // A saved spot for this task is picked up, so starting again opens on its next action and its link.
        var spot = GetSpotFor(task, now);
        var spotNext = string.IsNullOrWhiteSpace(spot?.Next) ? null : spot.Next.Trim();
        var step = task.ParentRef is not null ? task : ItemGroups.StepsOf(task, open).FirstOrDefault();
        // The saved spot's next step is the user's own, so it comes before a first action written for any task.
        var action = step?.Title ?? spotNext ?? (!string.IsNullOrWhiteSpace(firstAction) ? firstAction.Trim() : Activation.Fallback(0));
        var queue = step is null ? (then ?? []).Where(entry => !string.IsNullOrWhiteSpace(entry)).Select(entry => entry.Trim()).ToList() : [];
        var resource = spot?.Links.Select(link => SafeResource.Check(link)).FirstOrDefault(link => link is not null);
        return new StartPlan(task, paused, action, queue, step?.Id, resource, spot);
    }

    /// <summary>Pauses whatever is going, then picks up or starts the planned session, in the caller's write.</summary>
    private static OpReply ApplyStart(SqliteConnection connection, StartPlan plan, string method, DateTime now)
    {
        using (var pause = connection.CreateCommand())
        {
            pause.CommandText = "UPDATE activation_sessions SET status = $paused WHERE status = $active";
            pause.Parameters.AddWithValue("$paused", SessionStatus.Paused.ToString());
            pause.Parameters.AddWithValue("$active", SessionStatus.Active.ToString());
            pause.ExecuteNonQuery();
        }

        // Starting it here means it wasn't left for after this, so no card about it holds it later.
        ForgetAfterThis(connection, plan.Task.Source, plan.Task.Id);

        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$status", SessionStatus.Active.ToString());
        command.Parameters.AddWithValue("$now", FormatReminder(now));
        if (plan.Paused is { } paused)
        {
            command.CommandText = "UPDATE activation_sessions SET status = $status, prompt_at = NULL, prompt_kind = NULL, touched_at = $now WHERE id = $id";
            command.Parameters.AddWithValue("$id", paused.Id);
            command.ExecuteNonQuery();
            return new OpReply(Activation.JobReply(paused.Action), true);
        }

        command.CommandText = """
            INSERT INTO activation_sessions (task_ref, task_title, action, status, started_at, touched_at, method, step_id, queue, resource)
            VALUES ($ref, $title, $action, $status, $now, $now, $method, $step, $queue, $resource)
            """;
        command.Parameters.AddWithValue("$resource", (object?)plan.Resource ?? DBNull.Value);
        command.Parameters.AddWithValue("$ref", ItemRef.Format(plan.Task));
        command.Parameters.AddWithValue("$title", plan.Task.Title);
        command.Parameters.AddWithValue("$action", plan.Action);
        command.Parameters.AddWithValue("$method", method);
        command.Parameters.AddWithValue("$step", (object?)plan.StepId ?? DBNull.Value);
        command.Parameters.AddWithValue("$queue", JsonSerializer.Serialize(plan.Queue));
        command.ExecuteNonQuery();

        return new OpReply(TransitionAssist.Describe(plan.Spot) is { } stopped ? $"{stopped} {Activation.JobReply(plan.Action)}" : Activation.JobReply(plan.Action), true);
    }

    /// <summary>Handles a short reply locally. Returns null if there's nothing for it to act on.</summary>
    public OpReply? ApplySessionWord(SessionWord word, DateTime now)
    {
        if (word == SessionWord.Resume)
        {
            return GetActiveSession(now) is { } running ? new OpReply(Activation.JobReply(running.Action), true) : ResumeSession(now);
        }

        if (GetActiveSession(now) is not { } session)
        {
            return word == SessionWord.Cancel && ReadSession($"status = '{SessionStatus.Paused}'") is { } paused ? Cancel(paused, now) : null;
        }

        return word switch
        {
            SessionWord.Did => DidIt(session, now),
            SessionWord.Smaller => MakeSmaller(null, now),
            SessionWord.Pause => DeclineSession(session, now),
            SessionWord.Cancel => Cancel(session, now),
            _ => StartTrial(session, now),
        };
    }

    /// <summary>Not now on the session card: the place is kept, and the picks pass this one over for a snooze window.</summary>
    private OpReply DeclineSession(ActivationSession session, DateTime now)
    {
        var reply = PauseSession(null, null, now);
        if (session.TaskRef is { } declined)
        {
            RecordNotNow(declined, now);
        }

        return reply;
    }

    private void RecordNotNow(string itemRef, DateTime now)
    {
        using var lease = Connect(out var connection);
        using var transaction = Begin(connection);
        RecordNotNow(connection, itemRef, now);
        transaction.Commit();
    }

    /// <summary>Read and written in the caller's write, so two declines at once can't drop one.</summary>
    private static void RecordNotNow(SqliteConnection connection, string itemRef, DateTime now) =>
        WritePreference(connection, NotNowDeclines.Preference, NotNowDeclines.Add(ReadPreference(connection, NotNowDeclines.Preference), itemRef, now));

    /// <summary>Puts a smaller action in front of the current one, which comes back once it's done.</summary>
    public OpReply MakeSmaller(string? action, DateTime now)
    {
        if (GetActiveSession(now) is not { } session)
        {
            return new OpReply("Nothing's in progress. Say \"I can't start\" when you want a hand.", true);
        }

        var smaller = string.IsNullOrWhiteSpace(action) ? Activation.Fallback(session.SmallerCount) : action.Trim();
        var queue = smaller == session.Action ? session.Queue : [session.Action, .. session.Queue];
        Execute(session.Id, "action = $text, queue = $queue, smaller_count = smaller_count + 1, touched_at = $at", ("$text", smaller), ("$queue", JsonSerializer.Serialize(queue)), ("$at", FormatReminder(now)));
        return new OpReply($"Smaller, then: {smaller}.", true);
    }

    /// <summary>Stops without losing the place. A time brings it back then.</summary>
    public OpReply PauseSession(string? note, DateTime? until, DateTime now)
    {
        if (GetActiveSession(now) is not { } session)
        {
            return new OpReply("Nothing's in progress.", true);
        }

        Execute(
            session.Id,
            "status = $status, stopping_note = COALESCE($note, stopping_note), prompt_at = $at, prompt_kind = $kind",
            ("$status", SessionStatus.Paused.ToString()),
            ("$note", note),
            ("$at", until is { } at ? FormatReminder(at) : null),
            ("$kind", until is null ? null : ResumePrompt));

        var when = until is { } time ? $" I'll bring it back {WhenBack(time, now)}." : " Say resume whenever you're ready.";
        return new OpReply($"Okay, your place in {session.TaskTitle} is saved.{when}", true);
    }

    /// <summary>The day is named once it isn't today, so a time picked for later in the week reads right.</summary>
    private static string WhenBack(DateTime time, DateTime now)
    {
        var clock = time.ToString("h:mm tt", CultureInfo.CurrentCulture);
        return (time.Date - now.Date).Days switch
        {
            0 => $"at {clock}",
            1 => $"tomorrow at {clock}",
            _ => $"on {time.ToString("dddd, MMM d", CultureInfo.CurrentCulture)} at {clock}",
        };
    }

    /// <summary>The session put off with Pause, which Resume brings back.</summary>
    public ActivationSession? GetPausedSession() => ReadSession($"status = '{SessionStatus.Paused}'");

    public OpReply ResumeSession(DateTime? now = null) =>
        GetPausedSession() is { } paused ? Resume(paused, now ?? DateTime.Now) : new OpReply("Nothing's paused right now.", true);

    public OpReply CancelSession(DateTime now) =>
        (GetActiveSession(now) ?? GetPausedSession()) is { } session ? Cancel(session, now) : new OpReply("Nothing's in progress.", true);

    /// <summary>
    /// End on a session card: stops that session if it's still going or paused, with nothing marked done. A card from
    /// a session that's already over changes nothing, whatever is going now.
    /// </summary>
    public string EndSessionFromCard(long sessionId, DateTime now) =>
        (GetActiveSession(now) ?? GetPausedSession()) is { } session && session.Id == sessionId
            ? Cancel(session, now).Text
            : SessionAlreadyEnded;

    public const string SessionAlreadyEnded = "That session already ended, so nothing changed.";

    /// <summary>Keeps a link or document to open, once it passes the check. Anything else is turned down.</summary>
    public OpReply SetSessionResource(string? resource, DateTime now)
    {
        if (GetActiveSession(now) is not { } session)
        {
            return new OpReply("Nothing's in progress to attach that to.");
        }

        if (SafeResource.Check(resource) is not { } safe)
        {
            return new OpReply("I can only open web links and document files, so I left that out.");
        }

        Execute(session.Id, "resource = $text, touched_at = $at", ("$text", safe), ("$at", FormatReminder(now)));
        return new OpReply("Added Open the work");
    }

    /// <summary>The trial's end or a planned comeback, once each. A trial that's long past has nothing left to say.</summary>
    public SessionPrompt? TakeDuePrompt(DateTime now)
    {
        if (ReadSession($"prompt_at IS NOT NULL AND prompt_at <= '{FormatReminder(now)}' AND status IN ('{SessionStatus.Active}', '{SessionStatus.Paused}')") is not { } session)
        {
            return null;
        }

        var isTrial = ReadPromptKind(session.Id) == TrialPrompt;
        Execute(session.Id, "prompt_at = NULL, prompt_kind = NULL, action = $text", ("$text", isTrial ? Activation.KeepGoing : session.Action));

        if (!isTrial)
        {
            return new SessionPrompt("Ready when you are", $"{session.TaskTitle}: {session.Action}. Say resume to pick it back up.");
        }

        return now - session.PromptAt > StaleTrial ? null : new SessionPrompt("Two minutes are up", "Nice. Keep going, or stop here. Both are fine.");
    }

    /// <summary>What's helped so far, only when asked. Plain counts, with nothing to live up to.</summary>
    public string DescribeSessionStats()
    {
        var sessions = new List<(string Method, bool Begun, double? Minutes)>();
        using (Connect(out var connection))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT method, started_at, begun_at FROM activation_sessions";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var begun = reader.IsDBNull(2) ? (DateTime?)null : DateTime.Parse(reader.GetString(2), CultureInfo.InvariantCulture);
                sessions.Add((reader.GetString(0), begun is not null, (begun - DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture))?.TotalMinutes));
            }
        }

        if (sessions.Count == 0)
        {
            return "Nothing to look at yet.";
        }

        var lines = sessions
            .GroupBy(session => session.Method)
            .Select(group =>
            {
                var delays = group.Where(session => session.Minutes is not null).Select(session => session.Minutes!.Value).Order().ToList();
                var delay = delays.Count > 0 ? $", about {Math.Round(delays[delays.Count / 2])} min to the first action" : "";
                return $"- {group.Key}: began {delays.Count} of {group.Count()}{delay}";
            });
        return string.Join("\n", lines);
    }

    /// <summary>Saves a stopping point. Without a next action it asks, since the next one is what makes coming back easy.</summary>
    public OpReply SaveHandoff(string? project, string? stopped, string? next, IReadOnlyList<string>? entries, long captureId, DateTime now)
    {
        var name = string.IsNullOrWhiteSpace(project) ? GetActiveSession(now)?.TaskTitle ?? UnnamedProject : project.Trim();
        var notes = entries ?? [];
        var links = notes.Where(entry => LinkLike().IsMatch(entry)).ToList();

        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO handoffs (capture_id, project, stopped, next, links, thoughts, created_at)
            VALUES ($capture, $project, $stopped, $next, $links, $thoughts, $now)
            """;
        command.Parameters.AddWithValue("$capture", captureId);
        command.Parameters.AddWithValue("$project", name);
        command.Parameters.AddWithValue("$stopped", (object?)stopped ?? DBNull.Value);
        command.Parameters.AddWithValue("$next", string.IsNullOrWhiteSpace(next) ? DBNull.Value : next.Trim());
        command.Parameters.AddWithValue("$links", JsonSerializer.Serialize(links));
        command.Parameters.AddWithValue("$thoughts", JsonSerializer.Serialize(notes.Except(links).ToList()));
        command.Parameters.AddWithValue("$now", FormatReminder(now));
        command.ExecuteNonQuery();

        return string.IsNullOrWhiteSpace(next)
            ? new OpReply($"Saved your spot on {name}. What's the next thing to do when you come back?", true)
            : new OpReply($"Saved your spot on {name}. Next time: {next.Trim()}");
    }

    /// <summary>The project still waiting for its next action, if it was asked about recently.</summary>
    public string? GetWaitingHandoff(DateTime now) => GetWaitingSpot(now)?.Project;

    /// <summary>The saved spot "what's the next thing to do?" was asked about, while the answer still goes to it.</summary>
    public Handoff? GetWaitingSpot(DateTime now) =>
        GetHandoffs("next IS NULL AND created_at >= $since ORDER BY id DESC LIMIT 1", ("$since", FormatReminder(now - HandoffQuestionWindow))).FirstOrDefault();

    public OpReply SetHandoffNext(string? next, DateTime now)
    {
        var waiting = GetWaitingSpot(now);
        if (waiting is null || string.IsNullOrWhiteSpace(next))
        {
            return new OpReply("");
        }

        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE handoffs SET next = $next WHERE id = $id";
        command.Parameters.AddWithValue("$next", next.Trim());
        command.Parameters.AddWithValue("$id", waiting.Id);
        command.ExecuteNonQuery();
        return new OpReply($"Saved. Next time on {waiting.Project}: {next.Trim()}");
    }

    /// <summary>The latest stopping point for a project, or the latest of all if none is named.</summary>
    public string DescribeHandoff(string? project)
    {
        var handoffs = GetHandoffs("1 = 1 ORDER BY id DESC");
        var match = project is null
            ? handoffs.FirstOrDefault()
            : handoffs.FirstOrDefault(handoff => handoff.Project.Contains(project, StringComparison.OrdinalIgnoreCase))
                ?? handoffs.FirstOrDefault(handoff => SearchIndex.Mentions(handoff.Project, project));

        if (match is null)
        {
            return project is null ? "No saved spots yet." : $"No saved spot for {project}.";
        }

        var lines = new[]
        {
            $"{match.Project}, saved {ShortDate(DateOnly.FromDateTime(match.CreatedAt))}",
            match.Stopped is { } stopped ? $"Stopped: {stopped}" : null,
            match.Next is { } next ? $"Next: {next}" : null,
            match.Links.Count > 0 ? $"Links: {string.Join(", ", match.Links)}" : null,
            match.Thoughts.Count > 0 ? $"Remember: {string.Join("; ", match.Thoughts)}" : null,
        };
        return string.Join("\n", lines.Where(line => line is not null));
    }

    /// <summary>Takes a sent reminder off the list and gives back its item, so Start can begin on it.</summary>
    public StoredItem? TakeSentReminderItem(string messageId)
    {
        using var lease = Connect(out var connection);
        using var find = connection.CreateCommand();
        find.CommandText = "SELECT item_id, source FROM sent_reminders WHERE handled = 0 AND message_id = $message";
        find.Parameters.AddWithValue("$message", messageId);

        long id;
        ItemSource source;
        using (var reader = find.ExecuteReader())
        {
            if (!reader.Read())
            {
                return null;
            }

            id = reader.GetInt64(0);
            source = Enum.Parse<ItemSource>(reader.GetString(1));
        }

        // Two taps of Start at once only begin once.
        if (!ClaimSentReminder(connection, messageId))
        {
            return null;
        }

        return FindItem(connection, source, id) is { Done: false } item ? item : null;
    }

    private IEnumerable<SearchDoc> HandoffDocs(DateTime now) =>
        GetHandoffs("1 = 1").Select(handoff => new SearchDoc(
            SearchIndex.Handoff,
            $"Saved spot {ShortDate(DateOnly.FromDateTime(handoff.CreatedAt))}",
            handoff.Next is { } next ? $"{handoff.Project}: next, {next}" : handoff.Project,
            handoff.CreatedAt,
            null,
            string.Join(' ', new[] { handoff.Stopped }.Concat(handoff.Links).Concat(handoff.Thoughts))));

    private OpReply DidIt(ActivationSession session, DateTime now)
    {
        Execute(session.Id, "begun_at = COALESCE(begun_at, $text), touched_at = $text", ("$text", FormatReminder(now)));

        if (session.Action is Activation.TrialOffer or Activation.KeepGoing)
        {
            EndSession(session.Id, SessionStatus.Finished, now);
            return new OpReply("Nice. That's a real start, and you can stop here or carry on.", true);
        }

        if (session.Queue.Count > 0)
        {
            Execute(session.Id, "action = $text, queue = $queue", ("$text", session.Queue[0]), ("$queue", JsonSerializer.Serialize(session.Queue.Skip(1))));
            return new OpReply($"Next: {session.Queue[0]}.", true);
        }

        if (session.StepId is { } stepId && FindStep(stepId) is { Done: false } step)
        {
            SetDone(step, true);
        }

        var moved = MoveToNextStep(session, FindTask(session), now);
        return new OpReply(moved.Action == Activation.TrialOffer ? "Nice, you've started. Want to try it for two minutes?" : $"Next: {moved.Action}.", true);
    }

    private ActivationSession MoveToNextStep(ActivationSession session, StoredItem? task, DateTime now)
    {
        var next = task is null ? null : ItemGroups.StepsOf(task, GetOpenItems()).FirstOrDefault();
        var action = next?.Title ?? Activation.TrialOffer;
        Execute(session.Id, "action = $text, step_id = $step", ("$text", action), ("$step", next?.Id.ToString(CultureInfo.InvariantCulture)));
        return session with { Action = action, StepId = next?.Id };
    }

    private OpReply StartTrial(ActivationSession session, DateTime now)
    {
        Execute(
            session.Id,
            "prompt_at = $at, prompt_kind = $kind, action = $text, touched_at = $touched",
            ("$at", FormatReminder(now.AddMinutes(TrialMinutes))),
            ("$touched", FormatReminder(now)),
            ("$kind", TrialPrompt),
            ("$text", session.Action == Activation.TrialOffer ? Activation.KeepGoing : session.Action));
        return new OpReply("Two minutes, starting now. I'll check in once, and you can stop then.", true);
    }

    private OpReply Resume(ActivationSession session, DateTime now)
    {
        Execute(session.Id, "status = $text, prompt_at = NULL, prompt_kind = NULL, touched_at = $at", ("$text", SessionStatus.Active.ToString()), ("$at", FormatReminder(now)));
        return new OpReply(Activation.JobReply(session.Action), true);
    }

    private OpReply Cancel(ActivationSession session, DateTime now)
    {
        EndSession(session.Id, SessionStatus.Canceled, now);
        return new OpReply($"Okay, stopped. {session.TaskTitle} is still on your list.", true);
    }

    private void EndSession(long id, SessionStatus status, DateTime now) =>
        Execute(id, "status = $text, ended_at = $at, prompt_at = NULL, prompt_kind = NULL", ("$text", status.ToString()), ("$at", FormatReminder(now)));

    /// <summary>The latest of when it started, when the first action was done, and when anything else was done on it.</summary>
    private DateTime LastActivity(ActivationSession session)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT touched_at FROM activation_sessions WHERE id = $id";
        command.Parameters.AddWithValue("$id", session.Id);
        var touched = command.ExecuteScalar() is string text ? DateTime.Parse(text, CultureInfo.InvariantCulture) : session.StartedAt;
        return new[] { session.StartedAt, session.BegunAt ?? session.StartedAt, touched }.Max();
    }

    private StoredItem? FindTask(ActivationSession session)
    {
        using var lease = Connect(out var connection);
        return ItemRef.TryParse(session.TaskRef, out var source, out var id) ? FindItem(connection, source, id) : null;
    }

    private StoredItem? FindStep(long id)
    {
        using var lease = Connect(out var connection);
        return FindItem(connection, ItemSource.Capture, id);
    }

    private string? ReadPromptKind(long id)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT prompt_kind FROM activation_sessions WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as string;
    }

    /// <summary>The newest session matching a fixed filter, which only ever holds this class's own values.</summary>
    private ActivationSession? ReadSession(string filter)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SessionColumns} FROM activation_sessions WHERE {filter} ORDER BY id DESC LIMIT 1";

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new ActivationSession(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            Enum.Parse<SessionStatus>(reader.GetString(4)),
            DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
            reader.IsDBNull(6) ? null : DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetInt64(10),
            JsonSerializer.Deserialize<List<string>>(reader.GetString(11)) ?? [],
            reader.IsDBNull(12) ? null : DateTime.Parse(reader.GetString(12), CultureInfo.InvariantCulture),
            reader.GetInt32(13));
    }

    private void Execute(long sessionId, string assignments, params (string Name, string? Value)[] values)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE activation_sessions SET {assignments} WHERE id = $id";
        command.Parameters.AddWithValue("$id", sessionId);
        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    private List<Handoff> GetHandoffs(string filter, params (string Name, string Value)[] values)
    {
        using var lease = Connect(out var connection);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {HandoffColumns} FROM handoffs WHERE {filter}";
        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var handoffs = new List<Handoff>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            handoffs.Add(new Handoff(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                JsonSerializer.Deserialize<List<string>>(reader.GetString(4)) ?? [],
                JsonSerializer.Deserialize<List<string>>(reader.GetString(5)) ?? [],
                DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture)));
        }

        return handoffs;
    }

    [GeneratedRegex(@"^(https?://|[A-Za-z]:[\\/])")]
    private static partial Regex LinkLike();
}
