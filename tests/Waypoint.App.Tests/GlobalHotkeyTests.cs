namespace Waypoint.App.Tests;

/// <summary>Keys registered system-wide, such as Ctrl+Alt+Space for Ask Waypoint and Ctrl+Alt+M while recording.</summary>
public sealed class GlobalHotkeyTests
{
    // F24 is on almost no keyboard, so nothing else on the machine is likely to hold Ctrl+Alt+F24.
    private const uint VkF24 = 0x87;

    [Fact]
    public void Each_key_gets_its_own_id() => Ui.Run(_ =>
    {
        using var ask = new GlobalHotkey(() => { }, GlobalHotkey.VkSpace);
        using var mark = new GlobalHotkey(() => { }, GlobalHotkey.VkM);

        Assert.NotEqual(ask.Id, mark.Id);
    });

    [Fact]
    public void Keys_another_holder_already_has_are_refused_without_a_crash_and_free_again_once_let_go() => Ui.Run(_ =>
    {
        var first = new GlobalHotkey(() => { }, VkF24);
        using var second = new GlobalHotkey(() => { }, VkF24);
        Assert.True(first.Register());

        Assert.False(second.Register());

        first.Dispose();
        Assert.True(second.Register());
    });
}
