using Waypoint.App;

namespace Waypoint.App.Tests;

public sealed class CornerCardsTests
{
    private const double Highest = 16;
    private const double Lowest = 1000;

    [Fact]
    public void The_first_card_sits_in_the_corner() =>
        Assert.Equal(850, CornerCards.TopFor(150, Highest, Lowest, []));

    [Fact]
    public void A_new_card_stacks_on_the_real_top_of_the_one_below_whatever_its_height()
    {
        // A tall card below, then a short one: the next goes over the short one's own top, not a fixed step.
        (double, double)[] taken = [(700, 1000), (542, 692)];

        Assert.Equal(534 - 120, CornerCards.TopFor(120, Highest, Lowest, taken));
    }

    [Fact]
    public void A_space_left_by_a_closed_card_is_taken_again_when_the_new_card_fits()
    {
        // The card in the corner closed, leaving the one above it up.
        (double, double)[] taken = [(500, 700)];

        Assert.Equal(850, CornerCards.TopFor(150, Highest, Lowest, taken));
    }

    [Fact]
    public void A_card_too_tall_for_the_gap_goes_above_the_others()
    {
        (double, double)[] taken = [(850, 1000), (500, 700)];

        Assert.Equal(492 - 200, CornerCards.TopFor(200, Highest, Lowest, taken));
    }

    [Fact]
    public void No_two_cards_ever_overlap_however_many_go_up_and_close()
    {
        var heights = new[] { 150.0, 210, 120, 180, 150, 90 };
        var up = new List<(double Top, double Bottom)>();
        foreach (var height in heights)
        {
            var top = CornerCards.TopFor(height, Highest, Lowest, up);
            Assert.DoesNotContain(up, card => top < card.Bottom && card.Top < top + height);
            up.Add((top, top + height));
            if (up.Count == 3)
            {
                up.RemoveAt(0);
            }
        }
    }

    [Fact]
    public void With_no_room_left_a_card_goes_back_to_the_corner()
    {
        (double, double)[] taken = [(500, 1000), (16, 492)];

        Assert.Equal(850, CornerCards.TopFor(150, Highest, Lowest, taken));
    }
}
