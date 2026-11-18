using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Waypoint.App;

namespace Waypoint.App.Tests;

public sealed class RenameWindowTests
{
    [Fact]
    public void Rename_has_a_heading_like_the_other_forms() => Ui.Run(ui =>
    {
        var owner = Window.GetWindow(ui.Host(new Grid()));

        var rename = new RenameWindow(owner, "Email advisor");
        var heading = (TextBlock)rename.FindName("Heading");
        Assert.Equal("Rename", heading.Text);
        Assert.Equal("Rename", rename.Title);
        Assert.Equal(16, heading.FontSize);
        Assert.Equal(FontWeights.Light, heading.FontWeight);

        var add = new RenameWindow(owner, "", "Add", "Filter name, such as Work", "New filter");
        Assert.Equal("New filter", ((TextBlock)add.FindName("Heading")).Text);
        Assert.Equal("New filter", add.Title);
    });

    /// <summary>Asked through real UI Automation from another thread, the way a screen reader or test tool sees it.</summary>
    [Fact]
    public void The_pairing_form_is_in_the_automation_tree_with_its_own_names() => Ui.Run(ui =>
    {
        var owner = Window.GetWindow(ui.Host(new Grid()));
        string? windowName = null, boxName = null, buttonName = null;
        var inTree = false;
        ui.RenameOpened = form =>
        {
            var handle = new WindowInteropHelper(form).Handle;
            var asked = Task.Run(() =>
            {
                var window = AutomationElement.FromHandle(handle);
                windowName = window.Current.Name;
                inTree = TreeWalker.ControlViewWalker.GetParent(window) is not null;
                var box = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "PairToken"));
                boxName = box?.Current.Name;
                var button = window.FindFirst(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.NameProperty, "Pair")));
                buttonName = button?.Current.Name;
            });
            // UI Automation calls back into this thread, so it keeps pumping until the question is answered and never
            // blocks waiting on it. A question that never finishes fails the test instead of hanging it.
            PumpUntil(asked, TimeSpan.FromSeconds(20));
            Assert.True(asked.IsCompleted, "UI Automation didn't finish reading the pairing form");
            asked.GetAwaiter().GetResult();
        };

        Assert.NotEqual(true, RenameWindow.ForPairing(owner).ShowDialog());

        Assert.Equal("Pair with server", windowName);
        Assert.True(inTree);
        Assert.Equal("Server token", boxName);
        Assert.Equal("Pair", buttonName);
    });

    /// <summary>Runs the dispatcher until the task ends or the time is up, whichever is first.</summary>
    private static void PumpUntil(Task task, TimeSpan limit)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(limit, DispatcherPriority.Send, (_, _) => frame.Continue = false, dispatcher);
        task.ContinueWith(_ => dispatcher.BeginInvoke(DispatcherPriority.Send, () => frame.Continue = false), TaskScheduler.Default);
        if (!task.IsCompleted)
        {
            Dispatcher.PushFrame(frame);
        }

        timer.Stop();
    }
}
