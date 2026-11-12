using RoomScheduler;
using Xunit;

namespace RoomScheduler.Tests;

public partial class SwapTests
{
    [Theory]
    [InlineData("a room that can fit eighty students", 80, 80)]
    [InlineData("a room that holds seventy people", 70, 80)]
    [InlineData("a room that can seat eighty", 80, 80)]
    [InlineData("a room that accommodates eighty", 80, 80)]
    [InlineData("a room with capacity of 80", 80, 80)]
    [InlineData("an 80-seat room", 80, 80)]
    [InlineData("space for eighty", 80, 80)]
    [InlineData("seating for 80", 80, 80)]
    [InlineData("70+ seats", 70, 80)]
    [InlineData("70 seats or more", 70, 80)]
    [InlineData("70 seats minimum", 70, 80)]
    [InlineData("no fewer than 70 seats", 70, 80)]
    [InlineData("up to 75 seats", 70, 75)]
    [InlineData("maximum 75 students", 70, 75)]
    [InlineData("65-75 seats", 65, 75)]
    [InlineData("between 65 and 75 students", 65, 75)]
    [InlineData("between sixty-five and seventy-five students", 65, 75)]
    [InlineData("exactly eighty seats", 80, 80)]
    public void Everyday_capacity_wording_filters_every_swap_option(string phrase, int min, int max)
    {
        var f = Fixture(true);
        f.Rooms[0].Capacity = 60; f.Rooms[1].Capacity = 80; f.Rooms[2].Capacity = 70;
        foreach (var p in f.Plan.Placements) p.Section.SizeForRoom = 50;
        // The singular ‘swap ... with’ must distinguish capacity from a partner course.
        Send(f, "can you swap rooms for comp arch with " + phrase + "?");
        Assert.True(f.Chat.Proposal != null, Last(f.Chat));
        var options = f.Chat.Suggestions.Where(IsRoomOption).ToList();
        Assert.NotEmpty(options);
        foreach (string option in options)
        {
            Send(f, option);
            var destination = f.Chat.Proposal!.ProposedChanges.Single(c => c.Placement == f.Plan.Placements[0]);
            Assert.InRange(destination.Room.Capacity, min, max);
            Assert.Equal(2, f.Chat.Proposal.ProposedChanges.Count);
        }
        Assert.Equal(f.Rooms[0], f.Plan.Placements[0].Room);
    }

    [Theory]
    [InlineData(35)]
    [InlineData(45)]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(200)]
    [InlineData(301)]
    [InlineData(1234)]
    public void Capacity_search_works_for_arbitrary_numbers(int seats)
    {
        var f = Fixture(true);
        f.Rooms[0].Capacity = seats; f.Rooms[1].Capacity = seats - 1; f.Rooms[2].Capacity = seats + 1;
        foreach (var p in f.Plan.Placements) p.Section.SizeForRoom = seats - 5;
        Send(f, $"find room swaps for comp arch with {seats:N0} seats");
        Assert.True(f.Chat.Proposal != null, Last(f.Chat));
        Assert.Equal(seats + 1, f.Chat.Proposal!.ProposedChanges.Single(c => c.Placement == f.Plan.Placements[0]).Room.Capacity);
        Assert.Single(f.Chat.Suggestions, IsRoomOption);
    }

    [Theory]
    [InlineData("one hundred and twenty", 120)]
    [InlineData("two hundred thirty-five", 235)]
    [InlineData("one thousand two hundred and thirty-four", 1234)]
    public void Written_numbers_are_understood_as_capacity(string written, int seats)
    {
        var f = Fixture();
        f.Rooms[0].Capacity = seats; f.Rooms[1].Capacity = seats + 1;
        foreach (var p in f.Plan.Placements) p.Section.SizeForRoom = seats - 5;
        Send(f, $"find room swaps for comp arch with {written} seats");
        Assert.True(f.Chat.Proposal != null, Last(f.Chat));
        Assert.Contains($"at least {seats} seats", f.Chat.RequestContext!);
    }

    [Theory]
    [InlineData("actually make it 70")]
    [InlineData("I meant 70")]
    [InlineData("actually 70 seats instead")]
    [InlineData("can you find options for comp arch with exactly 70 seats instead?")]
    public void Corrections_replace_old_seat_bounds_and_remember_swap_intent(string correction)
    {
        var f = Fixture(true);
        f.Rooms[0].Capacity = 60; f.Rooms[1].Capacity = 80; f.Rooms[2].Capacity = 70;
        foreach (var p in f.Plan.Placements) p.Section.SizeForRoom = 50;
        Send(f, "find room and time swaps for comp arch with exactly 80 seats");
        Assert.NotNull(f.Chat.Proposal);
        Send(f, correction);
        Assert.True(f.Chat.Proposal != null, Last(f.Chat));
        Assert.Contains("room-and-time swap", Last(f.Chat));
        Assert.DoesNotContain("at most 80 seats", f.Chat.RequestContext!);
        Assert.Equal(70, f.Chat.Proposal!.ProposedChanges.Single(c => c.Placement == f.Plan.Placements[0]).Room.Capacity);
    }

    [Theory]
    [InlineData("around 80 seats")]
    [InlineData("about eighty seats")]
    [InlineData("-80 seats")]
    [InlineData("80.5 seats")]
    public void Ambiguous_or_invalid_capacity_requests_clear_old_options_and_ask_for_clarification(string request)
    {
        var f = Fixture(true);
        Send(f, "find room swaps for comp arch");
        Assert.NotNull(f.Chat.Proposal);
        Send(f, request);
        Assert.Null(f.Chat.Proposal);
        Assert.DoesNotContain(f.Chat.Suggestions, IsRoomOption);
        Assert.DoesNotContain("I found", Last(f.Chat));
        Send(f, "actually exactly 40 seats instead");
        Assert.True(f.Chat.Proposal != null, Last(f.Chat));
    }

    [Fact]
    public void Named_swap_partner_can_be_combined_with_a_capacity_constraint()
    {
        var f = Fixture(true);
        Send(f, "swap rooms for comp arch with data structures with 45 seats");
        Assert.True(f.Chat.Proposal != null, Last(f.Chat));
        Assert.Contains(f.Chat.Proposal!.ProposedChanges, c => c.Placement == f.Plan.Placements[1]);
        Send(f, "swap rooms for comp arch with data structures with 60 seats");
        Assert.Null(f.Chat.Proposal);
        Assert.Contains("at least 60 seats", Last(f.Chat));
    }

    [Fact]
    public void Natural_time_followups_switch_swap_modes_and_keep_seat_requirements()
    {
        var f = Fixture(true);
        f.Plan.Placements[1].Time = MeetingTime.Of("TTH", 14, 0, 15, 0);
        Send(f, "find room and time swaps for comp arch with 40 seats");
        Assert.NotNull(f.Chat.Proposal);
        Send(f, "don't change the time please");
        Assert.True(f.Chat.Proposal != null, Last(f.Chat));
        Assert.Contains("For this capacity request", Last(f.Chat));
        Assert.All(f.Chat.Proposal!.ProposedChanges, c => Assert.Equal(c.BeforeTime, c.Time));
        Send(f, "the time is flexible");
        Assert.True(f.Chat.Proposal != null, Last(f.Chat));
        Assert.Contains("room-and-time swap", Last(f.Chat));
        Assert.Contains("at least 40 seats", f.Chat.RequestContext!);
    }

    [Theory]
    [InlineData("can you show me the second option?")]
    [InlineData("please use the second one")]
    [InlineData("let's go with the second one")]
    public void Natural_option_selection_keeps_the_preview_unapplied(string request)
    {
        var f = Fixture(true);
        Send(f, "find room swaps for comp arch");
        var first = f.Chat.Proposal;
        Send(f, request);
        Assert.NotNull(f.Chat.Proposal);
        Assert.NotSame(first, f.Chat.Proposal);
        Assert.Contains("Selected option 2", Last(f.Chat));
        Assert.Equal(f.Rooms[0], f.Plan.Placements[0].Room);
    }
}
