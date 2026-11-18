using System.Net;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Background;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Discord;
using Waypoint.Core.Email;

namespace Waypoint.Core.Tests;

/// <summary>Dose reminders: Took it kept once for its day from any card, local "did I take it?" answers, and pruning.</summary>
public sealed class DoseTests : IDisposable
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Now);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"waypoint-doses-{Guid.NewGuid()}");
    private readonly WaypointDatabase _database;

    public DoseTests()
    {
        Directory.CreateDirectory(_folder);
        _database = new WaypointDatabase(Path.Combine(_folder, "waypoint.db"));
    }

    public void Dispose()
    {
        for (var attempt = 0; ; attempt++)
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_folder, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 50)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static DateTime At(DateOnly day, int hour, int minute) => day.ToDateTime(new TimeOnly(hour, minute));

    private StoredItem StoreDose(string title, DateOnly? day = null, TimeOnly? time = null, bool dose = true)
    {
        _database.CompleteCapture(_database.AddCapture(title), [new SortedItem(ItemKind.Reminder, title, day ?? Today, null, time ?? new TimeOnly(8, 0), Recurs: new Recurrence(RecurrenceUnit.Day), Dose: dose)]);
        return _database.GetOpenItems().Single(item => item.Title == title);
    }

    private StoredItem Reload(StoredItem item) => _database.GetOpenItems().Single(open => open.Id == item.Id);

    private sealed class CountingSorter : ICaptureSorter
    {
        public int Calls { get; private set; }

        public Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<CaptureResult?>(new CaptureResult([], Answer: "from the sorter"));
        }
    }

    private async Task<(string? Answer, int SorterCalls)> Ask(string text, DateTime now)
    {
        var sorter = new CountingSorter();
        CaptureOutcome? outcome = null;
        var service = new CaptureService(_database, sorter, clock: () => now);
        service.Sorted += result => outcome = result;
        service.Capture(text);
        await service.ProcessPendingAsync();
        return (outcome?.Answer, sorter.Calls);
    }

    [Fact]
    public void A_repeating_medicine_reminder_is_sorted_into_a_dose()
    {
        var items = SortedItemParser.Parse("""
            {"items": [
                {"kind": "reminder", "title": "Take vitamin D", "due_date": "2026-11-18", "due_time": "09:00", "amount_cents": null,
                 "recurrence": {"unit": "day", "interval": 1}, "dose": true},
                {"kind": "reminder", "title": "Water the plants", "due_date": "2026-11-18", "amount_cents": null,
                 "recurrence": {"unit": "day", "interval": 1}, "dose": false}
            ]}
            """);

        Assert.True(items[0].Dose);
        Assert.False(items[1].Dose);
        Assert.Contains("Set dose true", ClaudeCliSorter.SystemPrompt);
    }

    [Fact]
    public void Only_a_repeating_reminder_is_stored_as_a_dose_and_it_never_nags()
    {
        _database.CompleteCapture(_database.AddCapture("x"),
        [
            new SortedItem(ItemKind.Reminder, "Allergy pill", Today, null, new TimeOnly(22, 0), RepeatMinutes: 30, Recurs: new Recurrence(RecurrenceUnit.Day), Dose: true),
            new SortedItem(ItemKind.Reminder, "Pick up the prescription", Today, null, new TimeOnly(15, 0), Dose: true),
        ]);

        var open = _database.GetOpenItems();
        Assert.True(open.Single(item => item.Title == "Allergy pill").Dose);
        Assert.False(open.Single(item => item.Title == "Pick up the prescription").Dose);
        Assert.Equal("Allergy pill", Assert.Single(_database.GetDoses()).Item.Title);
        Assert.All(_database.GetDueReminders(At(Today, 23, 0)).Where(due => due.Item.Title == "Allergy pill"), due => Assert.Null(due.RepeatMinutes));
    }

    [Fact]
    public void Took_it_keeps_the_time_and_moves_on_to_the_next_day_like_Done()
    {
        var dose = StoreDose("Vitamin D");

        var reply = _database.AnswerDose(dose.Id, Today, DoseAnswer.Took, At(Today, 8, 12));

        Assert.Equal("Vitamin D: marked taken at 8:12 AM.", reply);
        Assert.Equal(Today.AddDays(1), Reload(dose).DueDate);
        var taken = Assert.Single(Assert.Single(_database.GetDoses()).Taken);
        Assert.Equal(new DoseTaken(Today, At(Today, 8, 12)), taken);
        Assert.Contains(_database.GetHistory(), entry => entry.ItemId == dose.Id && entry.Action == HistoryAction.Done && entry.DueDate == Today);
    }

    [Fact]
    public void A_second_Took_it_for_the_same_day_says_when_and_keeps_nothing_more()
    {
        var dose = StoreDose("Vitamin D");
        _database.AnswerDose(dose.Id, Today, DoseAnswer.Took, At(Today, 8, 12));

        // The PC card for the same day is clicked afterwards, then the row and Not now on the card.
        Assert.Equal("Already marked taken at 8:12 AM.", _database.AnswerDose(dose.Id, Today, DoseAnswer.Took, At(Today, 8, 30)));
        Assert.Equal("Already marked taken at 8:12 AM.", _database.AnswerDose(dose.Id, Today, DoseAnswer.NotNow, At(Today, 8, 31)));
        var row = _database.TakeDose(Reload(dose), At(Today, 9, 0));

        Assert.False(row.Recorded);
        Assert.Equal("Already marked taken at 8:12 AM.", row.Text);
        Assert.Single(Assert.Single(_database.GetDoses()).Taken);
        Assert.Equal(Today.AddDays(1), Reload(dose).DueDate);
    }

    [Fact]
    public void Typing_that_it_was_taken_counts_once_too()
    {
        var dose = StoreDose("Vitamin D");
        var first = _database.ChangeItem(new ItemChange(ItemSource.Capture, dose.Id, ChangeAction.Done), At(Today, 8, 5));
        var second = _database.ChangeItem(new ItemChange(ItemSource.Capture, dose.Id, ChangeAction.Done), At(Today, 8, 40));

        Assert.Equal("Vitamin D: marked taken at 8:05 AM.", ItemGroups.ChangeSummary(first));
        Assert.Equal("Already marked taken at 8:05 AM.", ItemGroups.ChangeSummary(second));
        Assert.Single(Assert.Single(_database.GetDoses()).Taken);
    }

    [Fact]
    public void Undoing_Took_it_takes_the_time_back()
    {
        var dose = StoreDose("Vitamin D");
        _database.TakeDose(dose, At(Today, 8, 5));

        _database.SetDone(Reload(dose), false);

        Assert.Empty(Assert.Single(_database.GetDoses()).Taken);
        Assert.Equal(Today, Reload(dose).DueDate);
    }

    [Fact]
    public void Skip_lets_the_day_go_and_Not_now_comes_back_once()
    {
        var skipped = StoreDose("Allergy pill", time: new TimeOnly(22, 0));
        var later = StoreDose("Vitamin D");

        Assert.Equal("Skipped: Allergy pill", _database.AnswerDose(skipped.Id, Today, DoseAnswer.Skip, At(Today, 22, 1)));
        var card = Assert.Single(_database.GetDueReminders(At(Today, 8, 0)), due => due.Item.Id == later.Id);
        Assert.Equal("Back once in 10 minutes: Vitamin D", _database.AnswerDose(later.Id, Today, DoseAnswer.NotNow, At(Today, 8, 1), card));

        Assert.Equal(Today.AddDays(1), Reload(skipped).DueDate);
        Assert.All(_database.GetDoses(), dose => Assert.Empty(dose.Taken));
        var back = Assert.Single(_database.GetDueReminders(At(Today, 8, 11)), due => due.Item.Id == later.Id);
        Assert.True(back.IsFollowUp);

        // Took it on the skipped day's card afterwards still counts for that day, and History says so.
        Assert.Equal("Allergy pill: marked taken at 10:30 PM.", _database.AnswerDose(skipped.Id, Today, DoseAnswer.Took, At(Today, 22, 30)));
        Assert.Contains(_database.GetHistory(), entry => entry.ItemId == skipped.Id && entry.Action == HistoryAction.Done && entry.DueDate == Today);
        Assert.Equal(Today.AddDays(1), Reload(skipped).DueDate);
    }

    [Fact]
    public void A_day_left_unanswered_is_simply_gone_and_today_still_comes()
    {
        var dose = StoreDose("Vitamin D", Today.AddDays(-2));

        _database.MoveOnPastDoses(At(Today, 9, 30));

        // Today's 8:00 has passed, but it still comes, and nothing was kept for the days that went by.
        Assert.Equal(Today, Reload(dose).DueDate);
        Assert.Contains(_database.GetDueReminders(At(Today, 9, 30)), due => due.Item.Id == dose.Id);
        Assert.Empty(Assert.Single(_database.GetDoses()).Taken);
    }

    [Fact]
    public void Taken_times_are_pruned_after_about_a_month()
    {
        var dose = StoreDose("Vitamin D", Today.AddDays(-31));
        _database.AnswerDose(dose.Id, Today.AddDays(-31), DoseAnswer.Took, At(Today.AddDays(-31), 8, 0));
        _database.AnswerDose(dose.Id, Today.AddDays(-30), DoseAnswer.Took, At(Today.AddDays(-30), 8, 30));

        _database.MoveOnPastDoses(At(Today, 7, 0));

        var kept = Assert.Single(Assert.Single(_database.GetDoses()).Taken);
        Assert.Equal(Today.AddDays(-30), kept.DueOn);
    }

    [Fact]
    public void A_repeating_reminder_can_be_marked_as_a_dose_from_its_menu()
    {
        var fishOil = StoreDose("Fish oil", dose: false);
        var task = _database.AddByHand(new HandAddedItem(ItemKind.Task, "Laundry", Today), DateTime.Now).Item!;

        Assert.Contains(RowAction.Dose, RowActions.For(fishOil, Today, movedFromFeed: false));
        Assert.DoesNotContain(RowAction.Dose, RowActions.For(task, Today, movedFromFeed: false));
        Assert.True(_database.SetDose(fishOil, true));
        Assert.False(_database.SetDose(task, true));
        Assert.True(Reload(fishOil).Dose);

        Assert.True(_database.SetDose(Reload(fishOil), false));
        Assert.False(Reload(fishOil).Dose);
    }

    [Fact]
    public void Answers_say_only_what_was_marked()
    {
        var now = At(Today, 14, 0);
        var item = new StoredItem(1, ItemKind.Reminder, "Vitamin D", Today.AddDays(1), null, false, Recurs: new Recurrence(RecurrenceUnit.Day), Dose: true);

        Assert.Equal("Yes, at 8:12 AM today.", Doses.Answer(new DoseRecord(item, [new DoseTaken(Today, At(Today, 8, 12))]), false, now));
        Assert.Equal(
            "Not marked today. Last marked yesterday at 8:05 AM.",
            Doses.Answer(new DoseRecord(item, [new DoseTaken(Today.AddDays(-1), At(Today.AddDays(-1), 8, 5))]), false, now));
        Assert.Equal("Not marked today. Nothing marked in the last 30 days.", Doses.Answer(new DoseRecord(item, []), false, now));
        Assert.Equal("Last marked yesterday at 8:05 AM.", Doses.Answer(new DoseRecord(item, [new DoseTaken(Today.AddDays(-1), At(Today.AddDays(-1), 8, 5))]), true, now));
    }

    [Theory]
    [InlineData("did I take my meds?", "meds", false)]
    [InlineData("Did I take vitamin D today?", "vitamin D", false)]
    [InlineData("have I taken my allergy pill yet", "allergy pill", false)]
    [InlineData("when did I last take my allergy pill?", "allergy pill", true)]
    public void Did_I_take_it_questions_are_read_locally(string text, string subject, bool asksWhen)
    {
        Assert.Equal(new DoseQuestion(subject, asksWhen), Doses.ParseQuestion(text));
    }

    [Fact]
    public async Task Did_I_take_it_is_answered_from_the_kept_times_without_the_sorter()
    {
        var vitamin = StoreDose("Vitamin D");
        StoreDose("Allergy pill", time: new TimeOnly(22, 0));
        _database.AnswerDose(vitamin.Id, Today, DoseAnswer.Took, At(Today, 8, 12));
        var now = At(Today, 15, 0);

        var one = await Ask("did I take vitamin D today?", now);
        var all = await Ask("did I take my meds?", now);
        var last = await Ask("when did I last take my allergy pill?", now);
        var other = await Ask("did I take out the trash?", now);

        Assert.Equal(("Yes, at 8:12 AM today.", 0), one);
        Assert.Equal(("Vitamin D: Yes, at 8:12 AM today.\nAllergy pill: Not marked today. Nothing marked in the last 30 days.", 0), all);
        Assert.Equal(("Nothing marked in the last 30 days.", 0), last);
        Assert.Equal(1, other.SorterCalls);
    }

    [Fact]
    public async Task With_no_doses_the_answer_says_so_instead_of_guessing()
    {
        Assert.Equal((Doses.NoneYet, 0), await Ask("did I take my meds?", At(Today, 9, 0)));
    }

    // A Monday morning, outside any quiet time.
    private static readonly DateTime Morning = new(2026, 11, 9, 10, 0, 0);

    private sealed class FakeDiscord : HttpMessageHandler
    {
        private int _posts;

        public List<(string Id, string Body)> Posts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
            }

            var id = request.Method == HttpMethod.Post ? $"m{Interlocked.Increment(ref _posts)}" : "x";
            if (request.Method == HttpMethod.Post)
            {
                lock (Posts)
                {
                    Posts.Add((id, body));
                }
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"id":"{{id}}"}""") };
        }
    }

    private sealed class Host : IWorkerHost
    {
        public List<DueReminder> Shown { get; } = [];

        public void ShowNotice(string title, string text)
        {
        }

        public void ShowReminder(DueReminder reminder, Action closed) => Shown.Add(reminder);

        public void ShowWeeklyReset(string text)
        {
        }

        public void Changed()
        {
        }

        public void Post(Action action) => action();
    }

    private sealed class NoTokens : ITokenStore
    {
        public string? Load() => null;

        public void Save(string token)
        {
        }
    }

    [Fact]
    public async Task On_Discord_the_card_reads_Took_it_Not_now_Skip_and_the_PC_card_after_says_it_was_already_taken()
    {
        var now = Morning;
        var discord = new FakeDiscord();
        var day = DateOnly.FromDateTime(Morning);
        var dose = StoreDose("Allergy pill", day, new TimeOnly(9, 0));
        var settings = new WaypointSettings(Discord: new DiscordSettings("token", "owner", "capture", "alerts", "budget"));
        var worker = new WaypointWorker(_database, settings, _folder, new Host(), exception => throw exception, new WorkerParts(
            _ => new NoTokens(),
            Clock: () => now,
            Http: () => new HttpClient(discord),
            CaptureSorter: new CountingSorter(),
            NudgePlanner: null,
            ConnectGateway: false))
        {
            RetryWait = _ => Task.CompletedTask,
        };
        worker.StartDiscord();

        worker.CheckAlerts();
        (string Id, string Body) card = default;
        for (var waited = 0; waited < 100 && card.Id is null; waited++)
        {
            await Task.Delay(50);
            lock (discord.Posts)
            {
                card = discord.Posts.FirstOrDefault(post => post.Body.Contains("Took it"));
            }
        }

        Assert.NotNull(card.Id);
        var took = card.Body.IndexOf("\"Took it\"", StringComparison.Ordinal);
        var notNow = card.Body.IndexOf("\"Not now\"", StringComparison.Ordinal);
        var skip = card.Body.IndexOf("\"Skip\"", StringComparison.Ordinal);
        Assert.True(took < notNow && notNow < skip, card.Body);

        // The dose stays on its day until it's answered.
        Assert.Equal(day, Reload(dose).DueDate);

        now = Morning.AddMinutes(5);
        Assert.Equal("Allergy pill: marked taken at 10:05 AM.", worker.OnButtonPressed(card.Id, AlertActionKind.Done));
        Assert.Equal(day.AddDays(1), Reload(dose).DueDate);

        now = Morning.AddMinutes(20);
        Assert.Equal("Already marked taken at 10:05 AM.", _database.AnswerDose(dose.Id, day, DoseAnswer.Took, now));
        Assert.Contains("Already marked taken at 10:05 AM.", worker.OnDiscordCommand(new SlashCommand("took", new Dictionary<string, string>())).Card.Title);
        Assert.Single(Assert.Single(_database.GetDoses()).Taken);
        Assert.Equal(day.AddDays(1), Reload(dose).DueDate);
    }
}
