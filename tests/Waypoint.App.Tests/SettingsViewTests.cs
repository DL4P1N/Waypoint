using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Waypoint.App;
using Waypoint.Core.Background;
using Waypoint.Core.Calendar;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Remote;
using Waypoint.Core.Schedule;

namespace Waypoint.App.Tests;

public sealed class SettingsViewTests
{
    [Fact]
    public void Connections_always_list_Discord_first_with_a_short_line()
    {
        var rows = SettingsView.ConnectionRows(
        [
            new ConnectionStatus("Discord", "Not set up. Fill it in below."),
            new ConnectionStatus("Calendar feed", "Not set up. Paste its link below."),
        ]);

        Assert.Equal(["Discord", "Calendar feed"], rows.Select(row => row.Name));
        Assert.Equal("Not set up. Details are under Discord.", rows[0].State);
    }

    [Fact]
    public void Discord_shows_even_when_nothing_reports_on_it()
    {
        var rows = SettingsView.ConnectionRows([new ConnectionStatus("Alert email", "Not set up.")]);

        Assert.Equal("Discord", rows[0].Name);
        Assert.StartsWith("Not set up.", rows[0].State);
    }

    /// <summary>A server that's away, so only what this PC keeps can change.</summary>
    private sealed class AwayServer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("No route to host");
    }

    private static SettingsView Open(UiTest ui, Action? changed = null)
    {
        var view = new SettingsView();
        var client = new ServerClient(new HttpClient(new AwayServer()) { BaseAddress = new Uri("http://server/") }, "token");
        view.Connect(new ServerSettingsData(client), ui.Database, null, new BedtimeSettings(), () => [], changed ?? (() => { }));
        ui.Host(view);
        view.Load();
        ui.Pump();
        return view;
    }

    [Fact]
    public void Less_see_through_is_saved_on_this_pc_and_applied_at_once() => Ui.Run(ui =>
    {
        var changes = 0;
        var view = Open(ui, () => changes++);
        var box = UiTest.Named<CheckBox>(view, "LessSeeThroughOn");
        Assert.False(box.IsChecked);

        box.IsChecked = true;

        Assert.True(ui.Database.GetTheme().LessSeeThrough);
        Assert.Equal(1, changes);

        view.Load();
        Assert.True(UiTest.Named<CheckBox>(view, "LessSeeThroughOn").IsChecked);
        Assert.Equal(1, changes);
    });

    [Fact]
    public void The_card_sound_is_on_until_turned_off_and_kept_on_this_pc() => Ui.Run(ui =>
    {
        var view = Open(ui);
        var box = UiTest.Named<CheckBox>(view, "CardSoundOn");
        Assert.True(box.IsChecked);

        box.IsChecked = false;

        Assert.False(CornerCards.SoundOn(ui.Database.GetPreference(CornerCards.SoundPreference)));
        view.Load();
        Assert.False(UiTest.Named<CheckBox>(view, "CardSoundOn").IsChecked);
    });

    [Fact]
    public void Work_hour_hints_fit_their_boxes_in_the_smallest_window() => Ui.Run(ui =>
    {
        var view = Open(ui);
        // At the window's smallest width, 1040, Settings beside the side menu and Ask is 650 wide.
        view.Width = 650;
        foreach (var (section, name) in new[]
        {
            ("work", "WeeklyStart"), ("work", "WeeklyEnd"), ("work", "ShiftStart"), ("work", "ShiftEnd"),
            ("classes", "ClassName"), ("classes", "ClassStart"), ("classes", "ClassEnd"), ("classes", "ClassPlace"), ("classes", "ClassTravel"),
        })
        {
            view.ShowSection(section);
            ui.Pump();
            var box = UiTest.Named<TextBox>(view, name);
            var hint = new System.Windows.Media.FormattedText(
                (string)box.Tag,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new System.Windows.Media.Typeface(box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch),
                box.FontSize,
                System.Windows.Media.Brushes.Black,
                1);
            var room = box.ActualWidth - box.Padding.Left - box.Padding.Right - box.BorderThickness.Left - box.BorderThickness.Right;
            Assert.True(hint.Width <= room, $"{name}'s hint \"{box.Tag}\" needs {hint.Width:0} but has {room:0}");
        }
    });

    [Fact]
    public void Settings_open_on_Look_and_show_one_section_at_a_time() => Ui.Run(ui =>
    {
        var view = Open(ui);

        Assert.Equal("look", view.SectionShown);
        Assert.Equal(Visibility.Visible, UiTest.Named<StackPanel>(view, "LookSection").Visibility);
        Assert.Equal(Visibility.Collapsed, UiTest.Named<StackPanel>(view, "WorkSection").Visibility);
        Assert.Equal(["Look", "Work", "Classes", "Bedtime", "Notes and recording", "Discord", "Connections", "Feedback"], SettingsView.Sections.Select(section => section.Label));

        view.ShowSection("bedtime");

        Assert.Equal(Visibility.Collapsed, UiTest.Named<StackPanel>(view, "LookSection").Visibility);
        Assert.Equal(Visibility.Visible, UiTest.Named<StackPanel>(view, "BedtimeSection").Visibility);
        Assert.Equal("bedtime", ui.Database.GetPreference("settings_section"));
    });

    [Fact]
    public void The_open_section_is_remembered() => Ui.Run(ui =>
    {
        var first = Open(ui);
        first.ShowSection("discord");
        Window.GetWindow(first).Close();

        var again = Open(ui);

        Assert.Equal("discord", again.SectionShown);
        Assert.Equal(Visibility.Visible, UiTest.Named<StackPanel>(again, "DiscordSection").Visibility);
    });

    [Theory]
    [InlineData("theme", "look")]
    [InlineData("see-through", "look")]
    [InlineData("commute", "work")]
    [InlineData("class schedule", "classes")]
    [InlineData("term", "classes")]
    [InlineData("wind-down step", "bedtime")]
    [InlineData("notes folder", "notes")]
    [InlineData("token", "discord")]
    [InlineData("calendar", "connections")]
    public void Search_finds_the_section_a_setting_is_in(string search, string section) =>
        Assert.Equal(section, SettingsView.Find(search)?.Key);

    [Fact]
    public void Searching_opens_the_section_and_says_when_nothing_fits() => Ui.Run(ui =>
    {
        var view = Open(ui);
        var column = UiTest.Named<NestedColumn>(view, "Column");
        var search = UiTest.Named<TextBox>(column, "SearchBox");

        search.Text = "commute";
        Assert.Equal("work", view.SectionShown);
        Assert.Equal(Visibility.Visible, UiTest.Named<StackPanel>(view, "WorkSection").Visibility);

        search.Text = "zebra";
        Assert.Equal("No setting matches that.", UiTest.Named<TextBlock>(column, "Problem").Text);
        Assert.Equal("work", view.SectionShown);
    });

    [Fact]
    public void Every_save_button_says_Save_and_names_what_it_saves_for_a_screen_reader() => Ui.Run(ui =>
    {
        var view = Open(ui);
        var saves = UiTest.Descendants<Button>(view)
            .Where(button => (button.Content as string)?.StartsWith("Save", StringComparison.Ordinal) == true && button.Content as string != "Save my spot")
            .ToList();

        Assert.Equal(8, saves.Count);
        Assert.All(saves, button => Assert.Equal("Save", button.Content));
        Assert.Equal(
            ["Save regular hours", "Save commute", "Save hourly pay", "Save classes", "Save bedtime", "Save Discord", "Save calendar feed", "Save alert email"],
            saves.Select(System.Windows.Automation.AutomationProperties.GetName));
    });

    [Fact]
    public void A_work_change_is_answered_under_the_part_it_was_made_in() => Ui.Run(ui =>
    {
        var view = Open(ui);
        view.ShowSection("work");

        UiTest.Call(view, "OnWeeklySaved", view, new RoutedEventArgs());
        Assert.Equal("Pick at least one day.", UiTest.Named<TextBlock>(view, "WeeklyStatus").Text);
        Assert.Equal(Visibility.Visible, UiTest.Named<TextBlock>(view, "WeeklyStatus").Visibility);

        UiTest.Call(view, "OnShiftAdded", view, new RoutedEventArgs());
        Assert.Equal("Try times like 9:00 AM and 5:00 PM, or 9-5 in the first box.", UiTest.Named<TextBlock>(view, "ShiftStatus").Text);
        Assert.Equal(Visibility.Collapsed, UiTest.Named<TextBlock>(view, "WeeklyStatus").Visibility);

        UiTest.Call(view, "OnCommuteSaved", view, new RoutedEventArgs());
        Assert.Equal(PageMessages.CantChangeYet, UiTest.Named<TextBlock>(view, "WorkStatus").Text);
        Assert.Equal(Visibility.Collapsed, UiTest.Named<TextBlock>(view, "ShiftStatus").Visibility);
    });

    /// <summary>Settings kept in memory, with the calendar feed's rules, so the page can be tried without a worker.</summary>
    private sealed class MemorySettings : ISettingsData
    {
        public string? Link { get; private set; }

        public bool OnServer => Remote;

        public IReadOnlyList<WeeklyHours> GetWeeklyHours() => [];

        public IReadOnlyList<WorkDate> GetWorkDates(DateOnly from) => [];

        public int GetCommuteMinutes() => 15;

        public decimal GetHourlyRate() => 20.00m;

        public BedtimeSettings GetBedtime() => new();

        public string DescribeTonight(DateTime now) => "";

        public BedtimeChoices? SavedBedtime { get; private set; }

        public string? SetBedtimeChoices(BedtimeChoices choices)
        {
            SavedBedtime = choices;
            return null;
        }

        public ClassesForm Classes { get; set; } = new(null, null, []);

        public bool Remote { get; init; }

        public ClassesForm GetClasses() => Classes;

        public string SaveClasses(ClassesForm form)
        {
            if (ClassSetup.Problem(form) is { } problem)
            {
                return problem;
            }

            Classes = form with { SavedHere = true };
            return Waypoint.Core.Background.WaypointWorker.ClassesSaved(form.Classes.Count);
        }

        public OpReply SetWeeklyHours(IReadOnlyList<string> days, string? hours) => new($"Work saved: {string.Join(", ", days)}, {hours}.");

        public OpReply RemoveWeeklyHours(IReadOnlyList<string> days) => new("");

        public OpReply AddWorkShift(DateTime? start, DateTime? end) => new("");

        public OpReply SetDayOff(DateTime? date) => new("");

        public OpReply RemoveWorkDate(DateOnly date) => new("");

        public OpReply SetCommute(string? minutes) => new("");

        public OpReply SetHourlyRate(decimal? dollars) => new("");

        public DiscordForm GetDiscord() => new(null, null, null, null, false);

        public string SaveDiscord(DiscordForm form, string? token) => "";

        public Task<string> TestDiscordAsync() => Task.FromResult("");

        public AlertEmailForm GetAlertEmail() => new(null, null);

        public string SaveAlertEmail(AlertEmailForm form) => "";

        public CalendarFeedForm GetCalendarFeed() => new(Link is not null, Link is not null);

        public string SaveCalendarFeed(string? link)
        {
            if (CalendarFeedSetup.Clean(link) is not { } clean)
            {
                return CalendarFeedSetup.NotALink;
            }

            Link = clean;
            return "Saved. Reading the feed now, then every hour.";
        }

        public string RemoveCalendarFeed()
        {
            Link = null;
            return "Removed. Nothing more is read from the feed, and what it already added stays.";
        }
    }

    [Fact]
    public void Feedback_sits_last_and_lists_and_clears_what_was_kept() => Ui.Run(ui =>
    {
        ui.Database.AddFeedback(Feedback.Reminder, null, "too early", new DateTime(2026, 11, 11, 15, 5, 0));
        var view = new SettingsView { FeedbackData = new LocalFeedbackData(ui.Database) };
        view.Connect(new MemorySettings(), ui.Database, null, new BedtimeSettings(), () => [], () => { });
        ui.Host(view);
        view.ShowSection("feedback");
        view.Refresh();
        ui.Pump();

        Assert.Equal("feedback", SettingsView.Sections[^1].Key);
        Assert.Equal("feedback", SettingsView.Find("didn't help")?.Key);
        Assert.Equal(Visibility.Visible, UiTest.Named<StackPanel>(view, "FeedbackSection").Visibility);
        var rows = ((System.Collections.IEnumerable)UiTest.Named<ItemsControl>(view, "FeedbackRows").ItemsSource).Cast<string>().ToList();
        Assert.Equal(["Nov 11, 3:05 PM  ·  Reminder card: too early"], rows);

        var clear = UiTest.Named<Button>(view, "FeedbackClear");
        var before = ui.Database.GetFeedback();
        clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, clear));
        Assert.Empty(ui.Database.GetFeedback());
        Assert.Equal(Visibility.Visible, UiTest.Named<TextBlock>(view, "NoFeedback").Visibility);

        // The usual undo bar puts them all back as they were.
        var bar = UiTest.Named<UndoBar>(view, "FeedbackBar");
        Assert.Equal(Visibility.Visible, bar.Visibility);
        Assert.Equal("Cleared 1 entry.", UiTest.Named<TextBlock>(bar, "Message").Text);
        var undo = UiTest.Named<Button>(bar, "UndoButton");
        undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, undo));
        ui.Pump();
        Assert.Equal(before, ui.Database.GetFeedback());
        Assert.Equal(["Nov 11, 3:05 PM  ·  Reminder card: too early"], ((System.Collections.IEnumerable)UiTest.Named<ItemsControl>(view, "FeedbackRows").ItemsSource).Cast<string>().ToList());
    });

    private static SettingsView OpenClasses(UiTest ui, MemorySettings settings, Action? changed = null)
    {
        var view = new SettingsView();
        view.Connect(settings, ui.Database, null, new BedtimeSettings(), () => [], changed ?? (() => { }));
        ui.Host(view);
        view.Load();
        view.ShowSection("classes");
        ui.Pump();
        return view;
    }

    private static List<string> ClassLines(SettingsView view) =>
        ((System.Collections.IEnumerable)UiTest.Named<ItemsControl>(view, "ClassRows").ItemsSource).Cast<object>().Select(row => row.ToString()!).ToList();

    [Fact]
    public void Classes_are_added_changed_and_removed_on_Settings() => Ui.Run(ui =>
    {
        var changes = 0;
        var settings = new MemorySettings();
        var view = OpenClasses(ui, settings, () => changes++);
        Assert.Equal(Visibility.Visible, UiTest.Named<StackPanel>(view, "ClassesSection").Visibility);
        Assert.Equal(Visibility.Visible, UiTest.Named<TextBlock>(view, "NoClasses").Visibility);
        Assert.Equal("First day: not set", UiTest.Named<Button>(view, "TermStartButton").Content);

        // Something left out is said under the form, and nothing is saved.
        UiTest.Named<TextBox>(view, "ClassName").Text = "CS 110";
        UiTest.Call(view, "OnClassesSaved", view, new RoutedEventArgs());
        Assert.Equal("Pick the days it meets.", UiTest.Named<TextBlock>(view, "ClassesStatus").Text);
        Assert.Empty(settings.Classes.Classes);

        var days = UiTest.Named<ListBox>(view, "ClassDays");
        days.SelectedItems.Add("Monday");
        days.SelectedItems.Add("Wednesday");
        UiTest.Named<TextBox>(view, "ClassStart").Text = "9-10:15";
        UiTest.Named<TextBox>(view, "ClassPlace").Text = "Room 1";
        UiTest.Named<TextBox>(view, "ClassTravel").Text = "15";
        UiTest.Call(view, "OnClassesSaved", view, new RoutedEventArgs());

        var added = Assert.Single(settings.Classes.Classes);
        Assert.Equal(new TimeOnly(9, 0), added.Start);
        Assert.Equal(new TimeOnly(10, 15), added.End);
        Assert.Equal(15, added.TravelMinutes);
        Assert.Equal(["CS 110  Mon, Wed  9:00 AM to 10:15 AM  Room 1"], ClassLines(view));
        Assert.Equal("Saved. Waypoint uses this class straight away.", UiTest.Named<TextBlock>(view, "ClassesStatus").Text);
        Assert.Equal("", UiTest.Named<TextBox>(view, "ClassName").Text);
        Assert.Empty(days.SelectedItems);
        Assert.Equal(Visibility.Collapsed, UiTest.Named<TextBlock>(view, "NoClasses").Visibility);
        Assert.Equal(1, changes);

        // Edit fills the form with the class, and Save changes it rather than adding another.
        ui.Pump();
        var row = UiTest.Descendants<Button>(view).Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Change CS 110");
        UiTest.Call(view, "OnClassEdited", row, new RoutedEventArgs());
        Assert.Equal("Change CS 110", UiTest.Named<TextBlock>(view, "ClassFormTitle").Text);
        Assert.Equal("9:00 AM", UiTest.Named<TextBox>(view, "ClassStart").Text);
        Assert.Equal(["Monday", "Wednesday"], days.SelectedItems.Cast<string>().OrderBy(day => day));
        UiTest.Named<TextBox>(view, "ClassEnd").Text = "10:30 AM";
        UiTest.Call(view, "OnClassesSaved", view, new RoutedEventArgs());
        Assert.Equal(new TimeOnly(10, 30), Assert.Single(settings.Classes.Classes).End);
        Assert.Equal("Add a class", UiTest.Named<TextBlock>(view, "ClassFormTitle").Text);

        ui.Pump();
        var remove = UiTest.Descendants<Button>(view).Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Remove CS 110");
        UiTest.Call(view, "OnClassRemoved", remove, new RoutedEventArgs());
        Assert.Empty(settings.Classes.Classes);
        Assert.Empty(ClassLines(view));
        Assert.Equal("Saved. There are no classes now.", UiTest.Named<TextBlock>(view, "ClassesStatus").Text);
    });

    [Fact]
    public void A_removed_class_comes_back_as_it_was_and_in_its_place_with_Undo() => Ui.Run(ui =>
    {
        var art = new ClassMeeting("Art 101", "Room 4", [DayOfWeek.Monday, DayOfWeek.Wednesday], new TimeOnly(9, 0), new TimeOnly(10, 15), 10);
        var bio = new ClassMeeting("Bio 102", "", [DayOfWeek.Tuesday], new TimeOnly(11, 0), new TimeOnly(12, 0));
        var settings = new MemorySettings { Classes = new(new DateOnly(2030, 1, 7), new DateOnly(2030, 5, 3), [art, bio]) };
        var view = OpenClasses(ui, settings);

        var remove = UiTest.Descendants<Button>(view).Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Remove Art 101");
        UiTest.Call(view, "OnClassRemoved", remove, new RoutedEventArgs());
        Assert.Equal([bio], settings.Classes.Classes);
        var bar = UiTest.Named<UndoBar>(view, "ClassesBar");
        Assert.Equal(Visibility.Visible, bar.Visibility);
        Assert.Equal("Removed Art 101", UiTest.Named<TextBlock>(bar, "Message").Text);

        var undo = UiTest.Named<Button>(bar, "UndoButton");
        undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, undo));
        ui.Pump();
        Assert.Equal([art, bio], settings.Classes.Classes);
        Assert.Equal(new DateOnly(2030, 1, 7), settings.Classes.TermStart);
        Assert.Equal(new DateOnly(2030, 5, 3), settings.Classes.TermEnd);
        Assert.Equal([SettingsView.ClassLabel(art), SettingsView.ClassLabel(bio)], ClassLines(view));
        Assert.Equal(Visibility.Collapsed, bar.Visibility);
    });

    [Fact]
    public void Classes_saved_on_the_server_are_used_on_this_pc_too() => Ui.Run(ui =>
    {
        var here = new ClassSchedule(DateOnly.MinValue, DateOnly.MinValue, [], [new DateOnly(2026, 10, 16)]);
        var settings = new MemorySettings { Remote = true };
        var view = new SettingsView { Classes = here };
        view.Connect(settings, ui.Database, null, new BedtimeSettings(), () => [], () => { });
        ui.Host(view);
        view.Load();
        view.ShowSection("classes");
        ui.Pump();

        UiTest.Named<TextBox>(view, "ClassName").Text = "MATH 140";
        UiTest.Named<ListBox>(view, "ClassDays").SelectedItems.Add("Tuesday");
        UiTest.Named<TextBox>(view, "ClassStart").Text = "1:00 PM";
        UiTest.Named<TextBox>(view, "ClassEnd").Text = "2:20 PM";
        UiTest.Call(view, "OnClassesSaved", view, new RoutedEventArgs());

        Assert.Equal("MATH 140", Assert.Single(here.Meetings).Course);
        Assert.Equal([new DateOnly(2026, 10, 16)], here.NoClassDates);
        Assert.Equal(new DateTime(2026, 11, 10, 13, 0, 0), here.GetNextClass(new DateTime(2026, 11, 10, 9, 0, 0))!.Start);
        Assert.Contains("MATH 140", ui.Database.ScheduleCourses);
    });

    [Fact]
    public void Nothing_on_Settings_points_at_settings_json() => Ui.Run(ui =>
    {
        var settings = new MemorySettings();
        settings.SaveCalendarFeed("https://example.test/feed.ics");
        var view = OpenClasses(ui, settings);

        var words = UiTest.Descendants<TextBlock>(view).Select(block => block.Text)
            .Concat(UiTest.Descendants<Button>(view).Select(button => button.Content as string ?? ""))
            .Append(SettingsView.ServerSetUpElsewhere);
        Assert.All(words, text => Assert.DoesNotContain("settings.json", text));
        Assert.Contains("setup file", UiTest.Named<TextBlock>(view, "SetUpElsewhere").Text);
        Assert.DoesNotContain("class schedule", UiTest.Named<TextBlock>(view, "SetUpElsewhere").Text);
    });

    [Fact]
    public void Bedtime_has_a_late_night_check_ins_switch_that_starts_off_and_no_dim_switch() => Ui.Run(ui =>
    {
        var settings = new MemorySettings();
        var view = new SettingsView();
        view.Connect(settings, ui.Database, null, new BedtimeSettings(), () => [], () => { });
        ui.Host(view);
        view.Load();
        view.ShowSection("bedtime");
        ui.Pump();

        // Off for a new setup, until turned on here.
        var lateCheckIns = UiTest.Named<CheckBox>(view, "LateCheckInsOn");
        Assert.False(lateCheckIns.IsChecked);
        Assert.Null(view.FindName("DimOn"));

        lateCheckIns.IsChecked = true;
        UiTest.Call(view, "OnBedtimeSaved", view, new RoutedEventArgs());
        Assert.True(settings.SavedBedtime!.LateCheckIns);
    });

    [Fact]
    public void Bedtime_names_the_wind_down_steps_and_Save_keeps_the_names() => Ui.Run(ui =>
    {
        var settings = new MemorySettings();
        var view = new SettingsView();
        view.Connect(settings, ui.Database, null, new BedtimeSettings(), () => [], () => { });
        ui.Host(view);
        view.Load();
        view.ShowSection("bedtime");
        ui.Pump();

        Assert.Equal(BedtimeSettings.DefaultEveningStepName, UiTest.Named<TextBox>(view, "EveningStepName").Text);
        Assert.Equal(BedtimeSettings.DefaultWindDownStepName, UiTest.Named<TextBox>(view, "WindDownStepName").Text);

        UiTest.Named<TextBox>(view, "EveningStepName").Text = "  Stretch ";
        UiTest.Named<TextBox>(view, "WindDownStepName").Text = "";
        UiTest.Call(view, "OnBedtimeSaved", view, new RoutedEventArgs());

        Assert.Equal("Stretch", settings.SavedBedtime!.EveningStepName);
        Assert.Null(settings.SavedBedtime.WindDownStepName);
    });

    [Fact]
    public void Bedtime_ticks_save_as_soon_as_they_are_clicked_and_leave_unsaved_boxes_alone() => Ui.Run(ui =>
    {
        var settings = new MemorySettings();
        var view = new SettingsView();
        view.Connect(settings, ui.Database, null, new BedtimeSettings(), () => [], () => { });
        ui.Host(view);
        view.Load();
        view.ShowSection("bedtime");
        ui.Pump();
        Assert.Null(settings.SavedBedtime);

        // Hours typed but not saved stay for Save; the tick is kept at once, with the hours as they were saved.
        UiTest.Named<TextBox>(view, "SleepHours").Text = "9";
        foreach (var name in new[] { "LateCheckInsOn", "EveningStepOn", "WindDownStepOn" })
        {
            UiTest.Named<CheckBox>(view, name).IsChecked = false;
        }

        UiTest.Named<CheckBox>(view, "QuietOn").IsChecked = true;
        var saved = settings.SavedBedtime!;
        Assert.False(saved.LateCheckIns);
        Assert.False(saved.EveningStep);
        Assert.False(saved.WindDownStep);
        Assert.True(saved.QuietAfterWrapUp);
        Assert.Equal(8, saved.SleepHours);
        Assert.Equal("9", UiTest.Named<TextBox>(view, "SleepHours").Text);

        view.Load();
        Assert.Same(saved, settings.SavedBedtime);
    });

    [Fact]
    public void The_calendar_feed_is_pasted_in_Connections_and_never_shown_again() => Ui.Run(ui =>
    {
        var settings = new MemorySettings();
        var view = new SettingsView();
        view.Connect(settings, ui.Database, null, new BedtimeSettings(), () => [], () => { });
        ui.Host(view);
        view.Load();
        view.ShowSection("connections");
        ui.Pump();
        var box = UiTest.Named<TextBox>(view, "CalendarFeed");
        var remove = UiTest.Named<Button>(view, "CalendarRemove");
        Assert.Equal(Visibility.Collapsed, remove.Visibility);

        box.Text = "my calendar";
        UiTest.Call(view, "OnCalendarFeedSaved", view, new RoutedEventArgs());
        Assert.Equal(CalendarFeedSetup.NotALink, UiTest.Named<TextBlock>(view, "CalendarStatus").Text);
        Assert.Equal("my calendar", box.Text);
        Assert.Null(settings.Link);

        box.Text = "webcal://example.test/feed.ics";
        UiTest.Call(view, "OnCalendarFeedSaved", view, new RoutedEventArgs());
        Assert.Equal("https://example.test/feed.ics", settings.Link);
        Assert.Equal("", box.Text);
        Assert.StartsWith("Saved.", UiTest.Named<TextBlock>(view, "CalendarStatus").Text);
        Assert.Equal("A feed link is saved. Paste a new one to replace it.", UiTest.Named<TextBlock>(view, "CalendarFeedNote").Text);
        Assert.Equal(Visibility.Visible, remove.Visibility);

        view.Load();
        Assert.Equal("", UiTest.Named<TextBox>(view, "CalendarFeed").Text);

        UiTest.Call(view, "OnCalendarFeedRemoved", view, new RoutedEventArgs());
        Assert.Null(settings.Link);
        Assert.Equal(Visibility.Collapsed, remove.Visibility);
        Assert.StartsWith("Removed.", UiTest.Named<TextBlock>(view, "CalendarStatus").Text);
    });

    [Theory]
    [InlineData("9-5", "", "09:00", "17:00")]
    [InlineData("9 to 5", "", "09:00", "17:00")]
    [InlineData("9am-5pm", "", "09:00", "17:00")]
    [InlineData("9-5pm", "", "09:00", "17:00")]
    [InlineData("1-5pm", "", "13:00", "17:00")]
    [InlineData("1-5", "", "13:00", "17:00")]
    [InlineData("10:30 – 2", "", "10:30", "14:00")]
    [InlineData("8 am - 12", "", "08:00", "12:00")]
    [InlineData("13:00-17:30", "", "13:00", "17:30")]
    [InlineData("5pm-11pm", "", "17:00", "23:00")]
    [InlineData("9:00 AM", "5:00 PM", "09:00", "17:00")]
    public void Hours_can_be_typed_as_one_range_or_in_two_boxes(string start, string end, string from, string to) =>
        Assert.Equal((TimeOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture), TimeOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture)), SettingsView.ReadHours(start, end));

    [Theory]
    [InlineData("17-17", "")]
    [InlineData("whenever", "")]
    [InlineData("9-5", "6 PM")]
    [InlineData("25-5", "")]
    [InlineData("13pm-5", "")]
    public void Hours_that_cant_be_read_ask_again(string start, string end) =>
        Assert.Null(SettingsView.ReadHours(start, end));

    [Fact]
    public void Saving_regular_hours_keeps_the_days_picked() => Ui.Run(ui =>
    {
        var view = new SettingsView();
        view.Connect(new MemorySettings(), ui.Database, null, new BedtimeSettings(), () => [], () => { });
        ui.Host(view);
        view.Load();
        view.ShowSection("work");
        ui.Pump();
        var days = UiTest.Named<ListBox>(view, "Days");
        days.SelectedItems.Add("Monday");
        days.SelectedItems.Add("Wednesday");
        UiTest.Named<TextBox>(view, "WeeklyStart").Text = "9-5";

        UiTest.Call(view, "OnWeeklySaved", view, new RoutedEventArgs());

        Assert.Equal(["Monday", "Wednesday"], days.SelectedItems.Cast<string>());
        Assert.Equal("", UiTest.Named<TextBox>(view, "WeeklyStart").Text);
    });

    [Fact]
    public void Discord_on_this_pc_says_where_the_token_is_kept_without_pointing_at_settings_json() => Ui.Run(ui =>
    {
        var view = new SettingsView();
        view.Connect(new MemorySettings(), ui.Database, null, new BedtimeSettings(), () => [], () => { });
        ui.Host(view);
        view.Load();
        view.ShowSection("discord");
        ui.Pump();

        var where = UiTest.Named<TextBlock>(view, "DiscordWhere").Text;
        Assert.DoesNotContain("settings.json", where);
        Assert.Contains("encrypted", where);
    });
}

public sealed class SettingsFieldHintTests
{
    /// <summary>Settings that only know commute and hourly pay, on a test database. Nothing else is reached here.</summary>
    public class WorkOnlySettings : System.Reflection.DispatchProxy
    {
        public Waypoint.Core.Data.WaypointDatabase Database { get; set; } = null!;

        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_OnServer" => false,
            "SetCommute" => Database.SetCommute((string?)args![0]),
            "GetCommuteMinutes" => Database.GetCommuteMinutes(),
            "GetWeeklyHours" => Database.GetWeeklyHours(),
            "DescribeTonight" => "",
            "GetWorkDates" => Database.GetWorkDates((DateOnly)args![0]),
            "SetHourlyRate" => Database.SetHourlyRate((decimal?)args![0]),
            _ => throw new NotSupportedException(method.Name),
        };
    }

    [Fact]
    public void The_work_from_home_box_saves_a_commute_of_zero_and_clearing_it_goes_back_to_the_default() => Ui.Run(ui =>
    {
        var settings = System.Reflection.DispatchProxy.Create<Waypoint.Core.Remote.ISettingsData, WorkOnlySettings>();
        ((WorkOnlySettings)(object)settings).Database = ui.Database;
        var view = new SettingsView();
        view.Connect(settings, ui.Database, null, new Waypoint.Core.Schedule.BedtimeSettings(), () => [], () => { });
        var box = UiTest.Named<System.Windows.Controls.CheckBox>(view, "WorkRemote");

        box.IsChecked = true;
        UiTest.Call(view, "OnWorkRemoteClicked", null, null);
        Assert.Equal(0, ui.Database.GetCommuteMinutes());
        Assert.Equal("0", UiTest.Named<System.Windows.Controls.TextBox>(view, "Commute").Text);

        box.IsChecked = false;
        UiTest.Call(view, "OnWorkRemoteClicked", null, null);
        Assert.Equal(15, ui.Database.GetCommuteMinutes());
    });

    [Fact]
    public void Bad_hourly_pay_and_commute_get_an_example_under_the_field() => Ui.Run(ui =>
    {
        var settings = System.Reflection.DispatchProxy.Create<Waypoint.Core.Remote.ISettingsData, WorkOnlySettings>();
        ((WorkOnlySettings)(object)settings).Database = ui.Database;
        var view = new SettingsView();
        view.Connect(settings, ui.Database, null, new Waypoint.Core.Schedule.BedtimeSettings(), () => [], () => { });

        UiTest.Named<System.Windows.Controls.TextBox>(view, "HourlyRate").Text = "abc";
        UiTest.Call(view, "OnHourlyRateSaved", null, null);
        UiTest.Named<System.Windows.Controls.TextBox>(view, "Commute").Text = "abc";
        UiTest.Call(view, "OnCommuteSaved", null, null);

        var rate = UiTest.Named<System.Windows.Controls.TextBlock>(view, "HourlyRateHint");
        var commute = UiTest.Named<System.Windows.Controls.TextBlock>(view, "CommuteHint");
        Assert.Equal("Try an amount like 20.00.", rate.Text);
        Assert.Equal(System.Windows.Visibility.Visible, rate.Visibility);
        Assert.Equal("Try a number of minutes, like 15.", commute.Text);
        Assert.Equal(System.Windows.Visibility.Collapsed, UiTest.Named<System.Windows.Controls.TextBlock>(view, "WorkStatus").Visibility);
    });
}
