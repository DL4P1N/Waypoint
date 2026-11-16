using System.Windows;
using System.Windows.Controls;
using Waypoint.App;

namespace Waypoint.App.Tests;

public sealed class WindowLayoutTests
{
    [Fact]
    public void A_long_status_message_stays_on_one_line_with_the_whole_text_in_its_tip() => Ui.Run(ui =>
    {
        var window = new ItemsWindow(ui.Database, _ => { }, () => { }, _ => { });
        try
        {
            var status = (TextBlock)window.FindName("Status");
            Assert.Equal(TextWrapping.NoWrap, status.TextWrapping);
            Assert.Equal(TextTrimming.CharacterEllipsis, status.TextTrimming);

            status.Text = "1 message is saved and will be sorted when Claude is reachable, with more words to make it long.";
            Assert.Equal(status.Text, status.ToolTip);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void A_short_window_keeps_more_rows_with_a_slimmer_top_bar_and_a_taller_minimum() => Ui.Run(ui =>
    {
        Assert.Equal(48, ItemsWindow.TopBarHeightFor(480));
        Assert.Equal(64, ItemsWindow.TopBarHeightFor(780));

        var window = new ItemsWindow(ui.Database, _ => { }, () => { }, _ => { });
        try
        {
            Assert.Equal(480, window.MinHeight);
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [InlineData("Add your work hours in Settings", "work")]
    [InlineData("Connect Discord in Settings, for To do on your phone", "discord")]
    public void A_setup_line_for_settings_opens_its_own_section(string line, string section) => Ui.Run(ui =>
    {
        var window = new ItemsWindow(ui.Database, _ => { }, () => { }, _ => { });
        try
        {
            var away = new Waypoint.Core.Remote.ServerClient(new System.Net.Http.HttpClient(new AwayServer()) { BaseAddress = new Uri("http://server/") }, "token");
            window.ConnectSettings(new Waypoint.Core.Remote.ServerSettingsData(away), null, new Waypoint.Core.Schedule.BedtimeSettings(), () => []);
            window.ShowActivated = false;
            window.Left = -20000;
            window.Top = -20000;
            window.Show();
            ui.Pump();
            var today = UiTest.Named<TodayView>(window, "TodayPage");
            var button = UiTest.Descendants<Button>(UiTest.Named<ItemsControl>(today, "SetupSteps")).Single(found => System.Windows.Automation.AutomationProperties.GetName(found) == line);

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            ui.Pump();

            var settings = UiTest.Named<SettingsView>(window, "SettingsPage");
            Assert.Equal(Visibility.Visible, settings.Visibility);
            Assert.Equal(section, settings.SectionShown);
        }
        finally
        {
            window.Close();
        }
    });

    private sealed class AwayServer : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new System.Net.Http.HttpRequestException("No route to host");
    }
}
