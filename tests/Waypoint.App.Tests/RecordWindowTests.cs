using System.Windows;
using System.Windows.Controls;
using Waypoint.App;
using Waypoint.Core.Recording;

namespace Waypoint.App.Tests;

public sealed class RecordWindowTests
{
    [Fact]
    public void With_no_class_on_it_asks_plainly_and_names_the_class_when_one_is_on() => Ui.Run(ui =>
    {
        var owner = Window.GetWindow(ui.Host(new Grid()));

        var plain = new RecordWindow(owner, null, AudioInput.Microphone, keepAudio: false);
        Assert.Equal("Record a lecture now?", ((TextBlock)plain.FindName("Question")).Text);

        var inClass = new RecordWindow(owner, "CS 101, Tue Nov 3", AudioInput.Microphone, keepAudio: false);
        Assert.Equal("Record CS 101, Tue Nov 3?", ((TextBlock)inClass.FindName("Question")).Text);
    });
}
