using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Waypoint.App;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Remote;
using Waypoint.Core.Schedule;

namespace Waypoint.App.Tests;

/// <summary>Lists, History, and Settings in server mode, once the server stops answering.</summary>
public sealed class ServerModeViewTests
{
    /// <summary>Answers the page reads until it's switched off, then acts like a server that's gone.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        public bool Up { get; set; } = true;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!Up)
            {
                throw new HttpRequestException("No route to host");
            }

            var entry = new HistoryEntry(1, DateTime.Now, HistoryAction.Done, ItemSource.Capture, 1, ItemKind.Task, "return books", null, null, null, []);
            object body = request.RequestUri!.AbsolutePath switch
            {
                "/api/lists" => new[] { new ListOverview(WaypointDatabase.ShoppingList, [new ListEntry(1, WaypointDatabase.ShoppingList, "bread", false, DateTime.Now)]) },
                "/api/history" => new[] { new HistoryRow(entry, true) },
                "/api/settings/rate" => new NumberValue(20.00m),
                "/api/settings/tonight" => new TextValue("Bed by 11:30 PM"),
                "/api/settings/email" => new AlertEmailForm(AppId, null),
                "/api/settings/weekly" or "/api/settings/dates" => Array.Empty<object>(),
                _ => new { },
            };
            var json = JsonSerializer.Serialize(body, body.GetType(), RemoteJson.Options);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private const string AppId = "12345678-abcd-1234-abcd-1234567890ab";

    private static ServerClient Client(FakeServer fake) => new(new HttpClient(fake) { BaseAddress = new Uri("http://server/") }, "token");

    private static List<string> Shown(ItemsControl entries) =>
        entries.Items.Cast<object>().Select(row => UiTest.Prop<string>(row, "Text")!).ToList();

    [Fact]
    public void Adding_to_a_list_while_the_server_is_away_keeps_the_text_and_says_why() => Ui.Run(ui =>
    {
        var fake = new FakeServer();
        var view = new ListsView();
        view.Connect(new ServerListsData(Client(fake)));
        ui.Host(view);
        view.Refresh(force: true);
        ui.Pump();
        var entries = UiTest.Named<ItemsControl>(view, "Entries");
        var addItem = UiTest.Named<TextBox>(view, "AddItem");
        Assert.Equal(["bread"], Shown(entries));

        fake.Up = false;
        ui.Focus(addItem);
        addItem.Text = "eggs";
        Assert.True(ui.Press(addItem, Key.Enter));

        Assert.Equal("eggs", addItem.Text);
        Assert.Equal(PageMessages.CantChangeYet, UiTest.Named<TextBlock>(UiTest.Named<UndoBar>(view, "Bar"), "Message").Text);
        view.Refresh(force: true);
        ui.Pump();
        Assert.Equal(["bread"], Shown(entries));
    });

    [Fact]
    public void With_nothing_read_and_no_server_the_lists_page_still_opens() => Ui.Run(ui =>
    {
        var view = new ListsView();
        view.Connect(new ServerListsData(Client(new FakeServer { Up = false })));
        ui.Host(view);
        view.Refresh(force: true);
        ui.Pump();

        Assert.Equal(WaypointDatabase.ShoppingList, UiTest.Named<TextBlock>(view, "ListTitle").Text);
        Assert.Empty(Shown(UiTest.Named<ItemsControl>(view, "Entries")));
    });

    [Fact]
    public void Reopening_while_the_server_is_away_leaves_the_row_closed_and_says_why() => Ui.Run(ui =>
    {
        var fake = new FakeServer();
        var view = new HistoryView();
        view.Connect(new ServerHistoryData(Client(fake)), () => { });
        ui.Host(view);
        view.Refresh();
        ui.Pump();

        fake.Up = false;
        var entries = UiTest.Named<ItemsControl>(view, "Entries");
        entries.UpdateLayout();
        var box = UiTest.Descendants<CheckBox>(entries).Single();
        box.IsChecked = false;
        box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        ui.Pump();
        entries.UpdateLayout();

        Assert.Equal(PageMessages.CantChangeYet, UiTest.Named<TextBlock>(UiTest.Named<UndoBar>(view, "Bar"), "Message").Text);
        Assert.True(UiTest.Descendants<CheckBox>(entries).Single().IsChecked);
    });

    [Fact]
    public void Settings_show_the_servers_values_and_a_change_while_it_is_away_says_why() => Ui.Run(ui =>
    {
        var fake = new FakeServer();
        var view = new SettingsView();
        view.Connect(new ServerSettingsData(Client(fake)), ui.Database, null, new BedtimeSettings(), () => [], () => { });
        ui.Host(view);
        view.Load();
        ui.Pump();

        Assert.Equal("20.00", UiTest.Named<TextBox>(view, "HourlyRate").Text);
        Assert.Equal("Bed by 11:30 PM", UiTest.Named<TextBlock>(view, "Tonight").Text);
        Assert.Contains("server", UiTest.Named<TextBlock>(view, "DiscordWhere").Text);
        Assert.DoesNotContain("settings.json", UiTest.Named<TextBlock>(view, "DiscordWhere").Text);
        Assert.Equal(SettingsView.ServerSetUpElsewhere, UiTest.Named<TextBlock>(view, "SetUpElsewhere").Text);
        Assert.Equal(AppId, UiTest.Named<TextBox>(view, "EmailClientId").Text);
        Assert.DoesNotContain("settings.json", SettingsView.ServerSetUpElsewhere);

        fake.Up = false;
        UiTest.Named<TextBox>(view, "HourlyRate").Text = "22.00";
        UiTest.Call(view, "OnHourlyRateSaved", view, new RoutedEventArgs());

        Assert.Equal(PageMessages.CantChangeYet, UiTest.Named<TextBlock>(view, "WorkStatus").Text);
    });

    [Fact]
    public void Theme_stays_on_this_pc_in_server_mode() => Ui.Run(ui =>
    {
        var view = new SettingsView();
        view.Connect(new ServerSettingsData(Client(new FakeServer { Up = false })), ui.Database, null, new BedtimeSettings(), () => [], () => { });
        ui.Host(view);
        view.Load();
        ui.Pump();

        var swatches = UiTest.Named<ListBox>(view, "Swatches");
        swatches.SelectedItem = swatches.Items.Cast<object>().First(swatch => UiTest.Prop<string>(swatch, "Name") != Waypoint.Core.Theme.Themes.Default.Name);

        Assert.NotEqual(Waypoint.Core.Theme.Themes.Default.Name, ui.Database.GetTheme().Name);
    });

    [Fact]
    public void Moving_the_notes_folder_while_the_server_is_away_keeps_the_old_folder() => Ui.Run(ui =>
    {
        var type = typeof(NotesView).Assembly.GetType("Waypoint.App.NoteFileSync")!;
        var here = Path.Combine(Path.GetTempPath(), $"waypoint-notes-{Guid.NewGuid()}");
        var there = here + " moved";
        var online = false;
        Func<bool> canSync = () => online;
        var sync = (IDisposable)Activator.CreateInstance(type, ui.Database, here, (Action)(() => { }), (Action<Exception>)(_ => { }), TimeSpan.FromHours(1), canSync)!;
        var move = type.GetMethod("MoveTo", [typeof(string), typeof(Action<string>)])!;
        string? saved = null;
        try
        {
            // Saved only once the move is made, so the next start doesn't use a folder the notes never went to.
            var away = Assert.Throws<System.Reflection.TargetInvocationException>(() => move.Invoke(sync, [there, (Action<string>)(root => saved = root)]));
            Assert.IsType<ServerUnavailableException>(away.InnerException);
            Assert.Null(saved);
            Assert.Equal(here, UiTest.Prop<string>(sync, "Root"));

            online = true;
            move.Invoke(sync, [there, (Action<string>)(root => saved = root)]);
            Assert.Equal(there, saved);
            Assert.Equal(there, UiTest.Prop<string>(sync, "Root"));
        }
        finally
        {
            sync.Dispose();
            ui.Pump();
            foreach (var folder in new[] { here, there }.Where(Directory.Exists))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    });

    [Fact]
    public void This_pc_keeps_its_id_for_the_server_and_a_copied_data_folder_gets_its_own()
    {
        var pcId = typeof(NotesView).Assembly.GetType("Waypoint.App.ServerConnection")!
            .GetMethod("PcId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var data = Path.Combine(Path.GetTempPath(), $"waypoint-pc-id-{Guid.NewGuid()}");
        var copy = data + " trial";
        try
        {
            var first = (string)pcId.Invoke(null, [data])!;
            Assert.Equal(first, (string)pcId.Invoke(null, [data])!);

            Directory.CreateDirectory(copy);
            foreach (var file in Directory.GetFiles(data))
            {
                File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
            }

            Assert.NotEqual(first, (string)pcId.Invoke(null, [copy])!);
        }
        finally
        {
            Directory.Delete(data, recursive: true);
            if (Directory.Exists(copy))
            {
                Directory.Delete(copy, recursive: true);
            }
        }
    }
}
