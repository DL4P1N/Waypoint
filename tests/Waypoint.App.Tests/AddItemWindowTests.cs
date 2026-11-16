using System.Windows;
using System.Windows.Controls;
using Waypoint.App;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;

namespace Waypoint.App.Tests;

public sealed class AddItemWindowTests
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private static string? Picked(ListBox list) => list.SelectedItem?.ToString();

    private static List<string> Labels(ListBox list) => list.Items.Cast<object>().Select(item => item.ToString()!).ToList();

    [Fact]
    public void A_task_starts_with_no_date_and_its_reminders_say_when_its_due() => Ui.Run(ui =>
    {
        var owner = Window.GetWindow(ui.Host(new Grid()));
        HandAddedItem? added = null;
        var form = new AddItemWindow(owner, ItemKind.Task, Today, [new FormPlace("Personal")], null, item =>
        {
            added = item;
            return null;
        })
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
        };
        form.Show();
        ui.Pump();
        try
        {
            var dates = UiTest.Named<ListBox>(form, "Dates");
            var kinds = UiTest.Named<ListBox>(form, "Kinds");
            var reminders = UiTest.Named<ListBox>(form, "Reminders");

            Assert.Equal(["No date", "Today", "Tomorrow"], Labels(dates));
            Assert.Equal("No date", Picked(dates));
            Assert.DoesNotContain("When it starts", Labels(reminders));
            Assert.Contains("When it's due", Labels(reminders));

            // Anything but a task needs a date, so it starts on today, and an event can start.
            kinds.SelectedIndex = 3;
            Assert.Equal(["Today", "Tomorrow"], Labels(dates));
            Assert.Equal("Today", Picked(dates));
            Assert.Contains("When it starts", Labels(reminders));

            // Back to a task, it goes back to no date, since no date was chosen by hand.
            kinds.SelectedIndex = 0;
            Assert.Equal("No date", Picked(dates));

            dates.SelectedIndex = 2;
            UiTest.Named<TextBox>(form, "TitleBox").Text = "call the bank";

            // Not shown as a dialog here, so closing it with a result throws once the item has been handed over.
            Assert.ThrowsAny<Exception>(() => UiTest.Call(form, "OnAddClicked", form, new RoutedEventArgs()));
            Assert.Equal(Today.AddDays(1), added?.Date);
        }
        finally
        {
            form.Close();
        }
    });
}
