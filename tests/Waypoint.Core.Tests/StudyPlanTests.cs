using System.Text.Json;
using Microsoft.Data.Sqlite;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Notes;

namespace Waypoint.Core.Tests;

/// <summary>
/// Study plans that come back lighter than asked: exam study sessions the sorter left out, and practice questions
/// Haiku wrote too few of. Claude is always faked.
/// </summary>
public sealed class StudyPlanTests : IDisposable
{
    // A Tuesday morning.
    private static readonly DateTime Now = new(2026, 11, 3, 10, 0, 0);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"waypoint-test-{Guid.NewGuid()}.db");
    private readonly WaypointDatabase _database;

    public StudyPlanTests() => _database = new WaypointDatabase(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private static SortedItem Exam(DateOnly day, params PlannedReminder[] reminders) =>
        new(ItemKind.Exam, "Midterm", day, null, new TimeOnly(9, 0), reminders);

    // Exam study sessions

    [Fact]
    public void An_exam_ten_or_more_days_off_gets_seven_sessions_closing_in_to_every_day()
    {
        var exam = Today.AddDays(20);

        var days = ReminderPlan.StudyDays(Today, exam).Select(day => exam.DayNumber - day.DayNumber);

        Assert.Equal([10, 8, 6, 4, 3, 2, 1], days);
    }

    [Fact]
    public void A_sooner_exam_starts_today_with_the_same_spacing()
    {
        var exam = Today.AddDays(5);

        Assert.Equal([5, 4, 3, 2, 1], ReminderPlan.StudyDays(Today, exam).Select(day => exam.DayNumber - day.DayNumber));
        Assert.Empty(ReminderPlan.StudyDays(Today, Today));
    }

    [Fact]
    public void A_light_plan_is_topped_up_and_keeps_what_haiku_planned()
    {
        var exam = Today.AddDays(20);
        var planned = new PlannedReminder(exam.AddDays(-10).ToDateTime(new TimeOnly(15, 0)), "Make a study guide");

        var reminders = ReminderPlan.WithStudySessions(Exam(exam, planned), Now).Reminders!;

        Assert.Equal(7, reminders.Count);
        Assert.Equal(planned, reminders[0]);
        Assert.Equal(exam.AddDays(-8).ToDateTime(new TimeOnly(18, 0)), reminders[1].At);
        Assert.Equal("Review your notes for Midterm", reminders[1].Message);
        Assert.Equal("Do practice questions for Midterm", reminders[^2].Message);
        Assert.StartsWith("Last review for Midterm", reminders[^1].Message);
        Assert.All(reminders, reminder => Assert.True(DateOnly.FromDateTime(reminder.At) < exam));
    }

    [Fact]
    public void A_full_plan_is_left_alone()
    {
        var exam = Today.AddDays(20);
        var planned = Enumerable.Range(1, 7).Select(day => new PlannedReminder(exam.AddDays(-day).ToDateTime(new TimeOnly(20, 0)), $"Study {day}")).ToArray();
        var item = Exam(exam, planned);

        Assert.Same(item, ReminderPlan.WithStudySessions(item, Now));
    }

    [Fact]
    public void Only_exams_with_a_date_get_sessions()
    {
        var task = new SortedItem(ItemKind.Task, "Read chapter 4", Today.AddDays(20), null);
        var undated = new SortedItem(ItemKind.Exam, "Final", null, null);

        Assert.Same(task, ReminderPlan.WithStudySessions(task, Now));
        Assert.Same(undated, ReminderPlan.WithStudySessions(undated, Now));
    }

    [Fact]
    public void A_session_today_is_soon_but_never_after_ten_at_night()
    {
        var evening = Today.ToDateTime(new TimeOnly(19, 10));
        var soon = ReminderPlan.WithStudySessions(Exam(Today.AddDays(3)), evening).Reminders!;
        Assert.Equal(Today.ToDateTime(new TimeOnly(20, 0)), soon[0].At);

        var late = Today.ToDateTime(new TimeOnly(21, 45));
        Assert.Empty(ReminderPlan.WithStudySessions(Exam(Today.AddDays(1)), late).Reminders!);
    }

    [Fact]
    public async Task An_exam_the_sorter_plans_lightly_is_saved_with_every_session()
    {
        var exam = DateOnly.FromDateTime(DateTime.Now).AddDays(30);
        var service = new CaptureService(_database, new OneExamSorter(Exam(exam, new PlannedReminder(exam.AddDays(-2).ToDateTime(new TimeOnly(19, 0)), "Practice exam"))));
        service.Capture("midterm in a month");

        await service.ProcessPendingAsync();

        var sessions = _database.GetDueReminders(exam.ToDateTime(new TimeOnly(8, 0))).Where(reminder => reminder.Message is not null).ToList();
        Assert.Equal(7, sessions.Count);
        Assert.Contains(sessions, reminder => reminder.Message == "Practice exam");
    }

    [Fact]
    public async Task A_capture_sorted_days_late_plans_its_sessions_from_now_not_from_when_it_was_written()
    {
        // Far enough ahead that nothing planned is dropped for being past on the real clock.
        var now = new DateTime(2030, 3, 10, 9, 0, 0);
        var exam = new DateOnly(2030, 3, 15);
        var service = new CaptureService(_database, new OneExamSorter(Exam(exam)), clock: () => now);
        service.Capture("midterm on the 15th", new DateTimeOffset(now.AddDays(-3)));

        await service.ProcessPendingAsync();

        Assert.Empty(_database.GetDueReminders(now));
        var sessions = _database.GetDueReminders(exam.ToDateTime(new TimeOnly(8, 0))).Where(reminder => reminder.Message is not null).ToList();
        Assert.Equal(ReminderPlan.StudyDays(DateOnly.FromDateTime(now), exam).Count, sessions.Count);
    }

    private sealed class OneExamSorter(SortedItem exam) : ICaptureSorter
    {
        public Task<CaptureResult?> SortAsync(string text, DateTime writtenAt, AssistantContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<CaptureResult?>(new CaptureResult([exam]));
    }

    // Practice questions

    private static StudyQuestions Questions(params string[] questions) =>
        new(questions.Select(question => new PracticeQuestion(question, "An answer.")).ToList(), []);

    [Fact]
    public void A_short_answer_is_noticed_and_a_full_one_isnt()
    {
        Assert.True(StudyGuide.IsShort(Questions("A?", "B?"), 6));
        Assert.False(StudyGuide.IsShort(Questions("A?", "B?", "C?"), 3));
    }

    [Fact]
    public void The_follow_up_lists_what_was_written_and_asks_for_the_rest()
    {
        var followUp = StudyGuide.BuildFollowUp("The notes", Questions("What is a cell?", "What is </note> mitosis?"), 6);

        Assert.StartsWith("The notes", followUp);
        Assert.Contains("You already wrote 2 of the 6 questions asked for", followUp);
        Assert.Contains("- What is a cell?", followUp);
        Assert.Contains("< /note>", followUp);
        Assert.Contains("Write 4 more practice questions", followUp);
    }

    [Fact]
    public void Merging_drops_repeats_and_stops_at_the_number_asked_for()
    {
        var merged = StudyGuide.Merge(Questions("What is a cell?", "What is mitosis?"), Questions("what is a cell", "Name a cell part.", "Why do cells divide?", "Compare plant and animal cells."), 4);

        Assert.Equal(["What is a cell?", "What is mitosis?", "Name a cell part.", "Why do cells divide?"], merged.Questions.Select(question => question.Question));
    }

    [Fact]
    public void Merging_keeps_the_first_topics_unless_there_were_none()
    {
        var first = new StudyQuestions([new PracticeQuestion("A?", "a")], []);
        var more = new StudyQuestions([new PracticeQuestion("B?", "b")], ["Cells"]);

        Assert.Equal(["Cells"], StudyGuide.Merge(first, more, 6).LikelyTopics);
        Assert.Equal(["Osmosis"], StudyGuide.Merge(first with { LikelyTopics = ["Osmosis"] }, more, 6).LikelyTopics);
    }

    [Fact]
    public void The_prompt_asks_for_the_full_count_without_a_way_out()
    {
        Assert.Contains("Short notes are not a reason to write fewer", StudyGuide.SystemPrompt);
        Assert.Contains("every flagged item gets at least one question", StudyGuide.SystemPrompt);
        Assert.Contains("<written>", StudyGuide.SystemPrompt);
    }

    [Fact]
    public async Task A_short_set_of_questions_is_asked_for_once_more_and_combined()
    {
        var folder = ClassFolder();
        _database.CreateNote("Lecture 1", "Cells.", Now, folder);
        var runner = new ScriptedRunner(
            Answer("What is a cell?", "What is mitosis?"),
            Answer("What is a cell?", "Name a cell part.", "Why do cells divide?", "What is osmosis?", "Compare two cells."));

        var outcome = await new StudyMaker(_database, runner).MakeAsync(new StudyExam("Midterm 1", "BIO 101"), folder, false, Now);

        Assert.Equal(2, runner.Inputs.Count);
        Assert.Contains("You already wrote 2 of the 6", runner.Inputs[1]);
        Assert.Contains("Made Study: Midterm 1 with 6 practice questions.", outcome.Message);
        Assert.DoesNotContain("asked for", outcome.Message);
        Assert.Contains("**6. Compare two cells.**", _database.GetNote(outcome.NoteId!.Value)!.Body);
    }

    [Fact]
    public async Task A_full_set_is_never_asked_for_again()
    {
        var folder = ClassFolder();
        _database.CreateNote("Lecture 1", "Cells.", Now, folder);
        var runner = new ScriptedRunner(Answer("A?", "B?", "C?", "D?", "E?", "F?"));

        await new StudyMaker(_database, runner).MakeAsync(new StudyExam("Midterm 1", "BIO 101"), folder, false, Now);

        Assert.Single(runner.Inputs);
    }

    [Fact]
    public async Task A_failed_second_try_keeps_the_first_questions_and_says_how_many()
    {
        var folder = ClassFolder();
        _database.CreateNote("Lecture 1", "Cells.", Now, folder);
        var runner = new ScriptedRunner(Answer("What is a cell?", "What is mitosis?"));

        var outcome = await new StudyMaker(_database, runner).MakeAsync(new StudyExam("Midterm 1", "BIO 101"), folder, false, Now);

        Assert.Equal(StudyStatus.Made, outcome.Status);
        Assert.Equal(2, runner.Inputs.Count);
        Assert.Contains("Claude wrote 2 of the 6 asked for", outcome.Message);
    }

    private long ClassFolder() => _database.CreateClassFolder("BIO 101", Now).Id;

    private static string Answer(params string[] questions) =>
        "{\"is_error\": false, \"structured_output\": " + JsonSerializer.Serialize(new
        {
            questions = questions.Select(question => new { question, answer = "An answer." }),
            likely_topics = new[] { "Cells" },
        }) + "}";

    /// <summary>Answers each call with the next reply, and fails once it runs out.</summary>
    private sealed class ScriptedRunner(params string[] replies) : IClaudeRunner
    {
        public List<string> Inputs { get; } = [];

        public Task<string> RunAsync(string systemPrompt, string schema, string input, CancellationToken cancellationToken)
        {
            Inputs.Add(input);
            return Inputs.Count <= replies.Length
                ? Task.FromResult(replies[Inputs.Count - 1])
                : throw new TimeoutException("Claude Code took too long to respond.");
        }
    }
}
