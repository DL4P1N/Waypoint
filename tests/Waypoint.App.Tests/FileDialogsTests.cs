using System.Diagnostics;
using System.Windows.Threading;
using Waypoint.App;

namespace Waypoint.App.Tests;

/// <summary>
/// Closing while a file or folder dialog is open. The dialog here is a stand-in that runs its own loop on the window
/// thread until it's asked to close, as Windows' dialogs do, so nothing appears on screen.
/// </summary>
public sealed class FileDialogsTests
{
    [Fact]
    public void Closing_asked_for_while_a_dialog_is_open_closes_it_and_goes_ahead_once_it_has_returned() => Ui.Run(ui =>
    {
        DispatcherFrame? dialog = null;
        var closeAsked = 0;
        var dialogs = new FileDialogs(() =>
        {
            closeAsked++;
            if (dialog is not null)
            {
                dialog.Continue = false;
            }
        }, TimeSpan.FromSeconds(30));
        var steps = new List<string>();

        string? picked = dialogs.Show(() =>
        {
            dialog = new DispatcherFrame();

            // Closing is asked for from inside the dialog's loop, as the tray's Exit or the exit event would be.
            Dispatcher.CurrentDispatcher.BeginInvoke(() =>
            {
                dialogs.CloseThen(() => steps.Add("closed"));
                steps.Add("asked");
            });
            Dispatcher.PushFrame(dialog);
            steps.Add("dialog returned");
            return @"C:\made-up\statement.csv";
        }, (string?)null);
        steps.Add("caller carried on");

        // Answered as cancelled, so the file isn't imported after closing, and closing waited for the dialog.
        Assert.Null(picked);
        Assert.Equal(1, closeAsked);
        Assert.Equal(["asked", "dialog returned", "caller carried on"], steps);

        ui.Pump();
        Assert.Equal(["asked", "dialog returned", "caller carried on", "closed"], steps);
        Assert.Equal(0, dialogs.Open);

        // Nothing opens once closing has started.
        var shown = false;
        Assert.Equal("cancelled", dialogs.Show(() =>
        {
            shown = true;
            return "picked";
        }, "cancelled"));
        Assert.False(shown);
    });

    [Fact]
    public void Closing_with_no_dialog_open_goes_ahead_straight_away() => Ui.Run(ui =>
    {
        var dialogs = new FileDialogs(() => throw new InvalidOperationException("Nothing is open to close"), TimeSpan.FromSeconds(30));
        Assert.Equal(DialogAnswer.Picked, dialogs.Show(() => DialogAnswer.Picked, DialogAnswer.Cancelled));

        var closed = 0;
        dialogs.CloseThen(() => closed++);
        Assert.Equal(1, closed);
        Assert.True(dialogs.Closing);
    });

    [Fact]
    public void A_dialog_that_wont_close_holds_closing_only_until_the_limit() => Ui.Run(ui =>
    {
        var closeAsked = 0;
        var dialogs = new FileDialogs(() => closeAsked++, TimeSpan.FromSeconds(3));
        var closedInside = false;
        var answer = dialogs.Show(() =>
        {
            var dialog = new DispatcherFrame();
            var closed = false;
            Dispatcher.CurrentDispatcher.BeginInvoke(() => dialogs.CloseThen(() => closed = true));

            // Stays open, asked again meanwhile, until closing went ahead without it.
            var until = Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) =>
            {
                if (closed || until.Elapsed > TimeSpan.FromSeconds(20))
                {
                    timer.Stop();
                    dialog.Continue = false;
                }
            };
            timer.Start();
            Dispatcher.PushFrame(dialog);
            closedInside = closed;
            return DialogAnswer.Picked;
        }, DialogAnswer.Cancelled);

        Assert.True(closedInside);
        Assert.True(closeAsked > 1);
        Assert.Equal(DialogAnswer.Cancelled, answer);
        ui.Pump();
    });

    private enum DialogAnswer
    {
        Picked,
        Cancelled,
    }
}
