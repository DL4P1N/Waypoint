using Waypoint.App;

namespace Waypoint.App.Tests;

public sealed class WindowResizeTests
{
    // The resize math is internal to the app, so it's reached the same way the other tests reach private parts.
    private static (int Left, int Top, int Right, int Bottom) Resized(int edge, int dx, int dy) =>
        ((int, int, int, int))typeof(WindowStyling).GetMethod("Resized", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [edge, (100, 100, 500, 400), dx, dy, 200, 150])!;

    [Fact]
    public void Each_edge_moves_only_its_own_sides()
    {
        Assert.Equal((80, 100, 500, 400), Resized(10, -20, 30));
        Assert.Equal((100, 100, 530, 400), Resized(11, 30, 30));
        Assert.Equal((100, 90, 500, 400), Resized(12, 30, -10));
        Assert.Equal((100, 100, 500, 440), Resized(15, 30, 40));
        Assert.Equal((90, 80, 500, 400), Resized(13, -10, -20));
        Assert.Equal((100, 100, 520, 430), Resized(17, 20, 30));
    }

    [Fact]
    public void A_drag_stops_at_the_minimum_size_without_moving_the_far_side()
    {
        Assert.Equal((300, 250, 500, 400), Resized(13, 900, 900));
        Assert.Equal((100, 100, 300, 250), Resized(17, -900, -900));
    }
}
