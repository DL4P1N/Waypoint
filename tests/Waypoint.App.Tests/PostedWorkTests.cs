using System.Windows.Threading;
using Waypoint.App;

namespace Waypoint.App.Tests;

/// <summary>Work handed to the window thread, such as a Discord capture to save, including while the app is closing.</summary>
public sealed class PostedWorkTests
{
    [Fact]
    public void Work_handed_over_while_closing_holds_the_window_thread_still_runs_once_each_in_order() => Ui.Run(ui =>
    {
        var ran = new List<string>();
        var problems = new List<Exception>();
        var work = new PostedWork(Dispatcher.CurrentDispatcher, problems.Add);

        // As at exit: the window thread waits on Discord while its events hand work over from another thread.
        Task.Run(() =>
        {
            work.Post(() => ran.Add("capture one"));
            work.Post(() => throw new InvalidOperationException("made up"));
            work.Post(() => ran.Add("capture two"));
        }).Wait();
        Assert.Empty(ran);

        work.RunLeft();
        Assert.Equal(["capture one", "capture two"], ran);
        Assert.Equal("made up", Assert.Single(problems).Message);

        // The dispatcher's own turns come later and find nothing left to run.
        ui.Pump();
        Assert.Equal(["capture one", "capture two"], ran);
    });

    [Fact]
    public void Work_handed_over_normally_runs_on_the_window_thread_in_order() => Ui.Run(ui =>
    {
        var ran = new List<(string What, bool OnWindowThread)>();
        var dispatcher = Dispatcher.CurrentDispatcher;
        var work = new PostedWork(dispatcher, _ => { });

        Task.Run(() =>
        {
            work.Post(() => ran.Add(("first", dispatcher.CheckAccess())));
            work.Post(() => ran.Add(("second", dispatcher.CheckAccess())));
        }).Wait();
        ui.Pump();

        Assert.Equal([("first", true), ("second", true)], ran);
        work.RunLeft();
        Assert.Equal(2, ran.Count);
    });
}
