using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using Waypoint.App;
using Waypoint.Core.Budget;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Remote;
using Waypoint.Core.Schedule;

namespace Waypoint.App.Tests;

/// <summary>
/// What a screen reader hears on each page and form. A row read out as its type, such as
/// "Waypoint.App.NotesView+NoteRow" or "FormChoice { Label = ... }", means a list shows something with no readable name.
/// </summary>
public sealed class AutomationNamesTests
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>Settings in server mode, so the work-hour rows can be filled without a worker.</summary>
    private sealed class SettingsServer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            object body = request.RequestUri!.AbsolutePath switch
            {
                "/api/settings/weekly" => new[] { new WeeklyHours(DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(17, 0)) },
                "/api/settings/dates" => new[] { new WorkDate(Today.AddDays(2), new TimeOnly(10, 0), new TimeOnly(14, 0)), new WorkDate(Today.AddDays(3), null, null) },
                "/api/settings/rate" => new NumberValue(20.00m),
                "/api/settings/tonight" => new TextValue("Bed by 11:30 PM"),
                _ => new { },
            };
            var json = JsonSerializer.Serialize(body, body.GetType(), RemoteJson.Options);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    /// <summary>Every name in the automation tree under <paramref name="root"/> that reads as code rather than words.</summary>
    private static List<string> CodeNames(UIElement root)
    {
        var found = new List<string>();
        Walk(UIElementAutomationPeer.CreatePeerForElement(root), found, 0);
        return found;
    }

    private static void NoCodeNames(UIElement root)
    {
        var found = CodeNames(root);
        Assert.True(found.Count == 0, $"Read out as code: {string.Join(" | ", found)}");
    }

    private static void Walk(AutomationPeer? peer, List<string> found, int depth)
    {
        if (peer is null || depth > 80)
        {
            return;
        }

        var name = peer.GetName() ?? "";
        if (name.Contains('{') || name.Contains("Waypoint.", StringComparison.Ordinal))
        {
            found.Add($"{peer.GetClassName()}: {name}");
        }

        foreach (var child in peer.GetChildren() ?? [])
        {
            Walk(child, found, depth + 1);
        }
    }

    /// <summary>Shows a form off screen without taking focus, the way the pages are hosted.</summary>
    private static void ShowOffscreen(UiTest ui, Window form)
    {
        form.WindowStartupLocation = WindowStartupLocation.Manual;
        form.Left = -20000;
        form.Top = -20000;
        form.ShowActivated = false;
        form.ShowInTaskbar = false;
        form.Show();
        ui.Pump();
    }

    [Fact]
    public void Every_page_names_its_rows_in_words() => Ui.Run(ui =>
    {
        var database = ui.Database;
        database.SaveCategory(new BudgetCategory("Groceries", 22500));
        database.AddExpense("Trader Joes", 4200, "Groceries", Today, DateTime.Now);
        database.ApplyBudgetOp(new BudgetOp(BudgetOpKind.AddWait, "headphones", 8000, null, 48), Today);
        var capture = database.AddCapture("call mom");
        database.CompleteCapture(capture, [new SortedItem(ItemKind.Task, "call mom", Today, null)]);
        database.AddToList("packing", ["charger"], 0, DateTime.Now);
        database.CreateNote("Lab", "# Results\nThe sample turned blue", DateTime.Now);

        var today = new TodayView();
        today.Connect(database, () => { }, _ => { }, _ => { }, () => { });
        ui.Host(today);
        today.Refresh(null);
        ui.Pump();
        NoCodeNames((today));
        Window.GetWindow(today).Close();

        var budget = new BudgetView();
        ui.Host(budget);
        budget.Connect(new LocalBudgetData(database), () => { });
        ui.Pump();
        NoCodeNames((budget));
        Window.GetWindow(budget).Close();

        var lists = new ListsView();
        lists.Connect(database);
        ui.Host(lists);
        lists.Refresh(force: true);
        ui.Pump();
        NoCodeNames((lists));
        Window.GetWindow(lists).Close();

        var history = new HistoryView();
        history.Connect(database, () => { });
        ui.Host(history);
        history.Refresh();
        ui.Pump();
        NoCodeNames((history));
        Window.GetWindow(history).Close();

        var notes = new NotesView();
        notes.Connect(database);
        ui.Host(notes);
        notes.Refresh(force: true);
        ui.Pump();
        NoCodeNames((notes));
        Window.GetWindow(notes).Close();

        var settings = new SettingsView();
        settings.Connect(new ServerSettingsData(new ServerClient(new HttpClient(new SettingsServer()) { BaseAddress = new Uri("http://server/") }, "token")), database, null, new BedtimeSettings(), () => [], () => { });
        ui.Host(settings);
        settings.Load();
        ui.Pump();
        Assert.NotEmpty(UiTest.Named<ItemsControl>(settings, "WeeklyRows").Items);
        NoCodeNames((settings));
    });

    [Fact]
    public void Every_form_names_its_choices_in_words() => Ui.Run(ui =>
    {
        var host = ui.Host(new Grid());
        var owner = Window.GetWindow(host);

        var budgetForm = new BudgetFormWindow(owner, "New expense", "Add", Today);
        budgetForm.AddChoice("category", "Category", [new FormChoice("Groceries", "Groceries"), new FormChoice("Fun", "Fun")], "Groceries");
        ShowOffscreen(ui, budgetForm);
        try
        {
            NoCodeNames((budgetForm));
        }
        finally
        {
            budgetForm.Close();
        }

        var addForm = new AddItemWindow(owner, ItemKind.Task, Today, [new FormPlace("Personal"), new FormPlace("CS 101", Course: "CS 101")], null, _ => null);
        ShowOffscreen(ui, addForm);
        try
        {
            NoCodeNames((addForm));
        }
        finally
        {
            addForm.Close();
        }

        var waitingForm = new WaitingWindow(owner, Today, "call mom", _ => null);
        ShowOffscreen(ui, waitingForm);
        try
        {
            NoCodeNames((waitingForm));
        }
        finally
        {
            waitingForm.Close();
        }
    });

    /// <summary>The name set on a row's own container, not only one worked out from its item.</summary>
    private static List<string> RowNames(UiTest ui, ListBox list)
    {
        list.UpdateLayout();
        ui.Pump();
        return list.Items.Cast<object>()
            .Select(item => list.ItemContainerGenerator.ContainerFromItem(item))
            .OfType<ListBoxItem>()
            .Select(System.Windows.Automation.AutomationProperties.GetName)
            .ToList();
    }

    [Fact]
    public void The_capture_box_swatches_form_choices_and_note_rows_have_names_of_their_own() => Ui.Run(ui =>
    {
        var capture = new CaptureWindow(_ => { }, _ => { });
        Assert.Equal("What's on your mind?", System.Windows.Automation.AutomationProperties.GetName(UiTest.Named<TextBox>(capture, "Input")));

        var settings = new SettingsView();
        settings.Connect(new ServerSettingsData(new ServerClient(new HttpClient(new SettingsServer()) { BaseAddress = new Uri("http://server/") }, "token")), ui.Database, null, new BedtimeSettings(), () => [], () => { });
        ui.Host(settings);
        settings.Load();
        ui.Pump();
        var swatches = RowNames(ui, UiTest.Named<ListBox>(settings, "Swatches"));
        Assert.NotEmpty(swatches);
        Assert.All(swatches, name => Assert.False(string.IsNullOrEmpty(name) || name.Contains("Waypoint.", StringComparison.Ordinal), name));
        Window.GetWindow(settings).Close();

        ui.Database.CreateNote("Lab", "# Results\nThe sample turned blue", DateTime.Now);
        var notes = new NotesView();
        notes.Connect(ui.Database);
        ui.Host(notes);
        notes.Refresh(force: true);
        ui.Pump();
        Assert.Contains(RowNames(ui, UiTest.Named<ListBox>(notes, "NotesList")), name => name.StartsWith("Lab, ", StringComparison.Ordinal));
        Window.GetWindow(notes).Close();

        var owner = Window.GetWindow(ui.Host(new Grid()));
        var form = new BudgetFormWindow(owner, "New expense", "Add", Today);
        form.AddChoice("category", "Category", [new FormChoice("Groceries", "Groceries"), new FormChoice("Fun", "Fun")], "Groceries");
        ShowOffscreen(ui, form);
        try
        {
            var choices = UiTest.Descendants<ListBox>(form).Single(list => System.Windows.Automation.AutomationProperties.GetName(list) == "Category");
            Assert.Equal(["Groceries", "Fun"], RowNames(ui, choices));
        }
        finally
        {
            form.Close();
        }
    });

    [Fact]
    public void Budget_boxes_are_named_for_what_goes_in_them_and_note_rows_dont_repeat_the_title() => Ui.Run(ui =>
    {
        var owner = Window.GetWindow(ui.Host(new Grid()));
        var form = new BudgetFormWindow(owner, "Can I afford this?", "Check", Today);
        form.AddMoney("amount", "Price, such as 45");
        form.AddText("reason", "Why you want it, optional");
        ShowOffscreen(ui, form);
        try
        {
            var boxes = UiTest.Descendants<TextBox>(form).ToList();
            Assert.Equal(["Price", "Why you want it, optional"], boxes.Select(System.Windows.Automation.AutomationProperties.GetName));
            Assert.Equal("Price, such as 45", System.Windows.Automation.AutomationProperties.GetHelpText(boxes[0]));
        }
        finally
        {
            form.Close();
        }

        ui.Database.CreateNote("Lecture 14: malloc and free", "# Lecture 14\nmalloc hands out memory", DateTime.Now);
        var notes = new NotesView();
        notes.Connect(ui.Database);
        ui.Host(notes);
        notes.Refresh(force: true);
        ui.Pump();
        var name = RowNames(ui, UiTest.Named<ListBox>(notes, "NotesList")).Single(found => found.StartsWith("Lecture 14:", StringComparison.Ordinal));
        Assert.EndsWith("malloc hands out memory", name);
        Window.GetWindow(notes).Close();
    });
}
