using RoomScheduler;
using Xunit;

namespace RoomScheduler.Tests;

public partial class ConversationTests
{
    [Fact]
    public void Instructor_only_move_selects_their_course()
    {
        var f = Fixture(); Send(f, "move Smith to Tuesday at 2pm");
        Assert.NotNull(f.Chat.Proposal);
        Assert.Contains(f.Chat.Proposal!.ProposedChanges, c => c.Placement.Section.Course == "429");
    }

    [Fact]
    public void Source_row_can_identify_a_course_without_an_earlier_clarification()
    {
        var f = Fixture(); Send(f, "move row 3 to Tuesday at 2pm");
        Assert.NotNull(f.Chat.Proposal);
        Assert.Contains(f.Chat.Proposal!.ProposedChanges, c => c.Placement.Section.Course == "314");
    }

    [Fact]
    public void Seat_count_is_not_mistaken_for_a_course_number()
    {
        var f = Fixture(); Send(f, "find data structures a room no more than 429 seats after 2pm");
        Assert.NotNull(f.Chat.Proposal);
        Assert.Contains(f.Chat.Proposal!.ProposedChanges, c => c.Placement.Section.Course == "314");
    }

    [Fact]
    public void Implicit_room_followup_after_lookup_keeps_the_course()
    {
        var f = Fixture(); f.Plan.Placements[0].Room = f.Rooms[0];
        Send(f, "where is comp arch?"); Send(f, "find a smaller room");
        Assert.NotNull(f.Chat.Proposal);
        Assert.Contains(f.Chat.Proposal!.ProposedChanges, c => c.Placement.Section.Course == "429" && c.Room.Capacity < 200);
    }

    [Fact]
    public void Instructor_schedule_lists_sections_and_does_not_replace_a_preview()
    {
        var f = Fixture(); Send(f, "move comp arch to Tuesday at 2pm"); var preview = f.Chat.Proposal;
        Send(f, "what does Jones teach?");
        Assert.Contains("314 DATA STRUCTURES", Last(f.Chat)); Assert.Contains("40 seats", Last(f.Chat));
        Assert.Same(preview, f.Chat.Proposal);
    }

    [Fact]
    public void Smallest_room_preference_ranks_feasible_rooms_by_capacity()
    {
        var f = Fixture(); var target = f.Plan.Placements[0]; target.Room = f.Rooms[0];
        var small = new Room { Building = "RLP", RoomNumber = "1.105", Capacity = 32 }; f.Rooms.Add(small);
        Send(f, "find comp arch the smallest feasible room");
        var c = f.Chat.Proposal!.ProposedChanges.Single(c => c.Placement == target);
        Assert.Same(small, c.Room); Assert.Equal(target.Time, c.Time);
        Assert.Contains("32 seats", Last(f.Chat)); Assert.Contains("2 spare seats", Last(f.Chat));
        Assert.Contains("32 seats", f.Chat.Suggestions.First());
    }

    [Fact]
    public void Comparing_rooms_reports_size_occupancy_and_fit_without_changing_a_preview()
    {
        var f = Fixture(); Send(f, "move comp arch to Tuesday at 2pm"); var preview = f.Chat.Proposal;
        Send(f, "compare GDC 2.216 and RLP 1.104");
        Assert.Contains("200 seats", Last(f.Chat)); Assert.Contains("40 seats", Last(f.Chat));
        Assert.Contains("expected class size 30", Last(f.Chat)); Assert.Contains("Occupied", Last(f.Chat));
        Assert.Same(preview, f.Chat.Proposal);
    }

    [Fact]
    public void Multiple_named_rooms_ask_which_one_instead_of_choosing_arbitrarily()
    {
        var f = Fixture(); Send(f, "move comp arch to GDC 1.100 or RLP 1.104 Tuesday at 2pm");
        Assert.Null(f.Chat.Proposal); Assert.Contains("more than one room", Last(f.Chat));
    }

    [Theory]
    [InlineData("between 35 and 45 seats")]
    [InlineData("45 seats max")]
    [InlineData("45 students or fewer")]
    public void Conversational_seat_ranges_are_respected(string request)
    {
        var f = Fixture(); f.Plan.Placements[0].Room = f.Rooms[0];
        f.Rooms.Add(new Room { Building = "RLP", RoomNumber = "1.105", Capacity = 50 });
        Send(f, "find comp arch a room " + request);
        Assert.NotNull(f.Chat.Proposal);
        foreach (var option in f.Chat.Suggestions.Where(s => s.StartsWith("Option ")).ToList())
        {
            Send(f, option);
            var c = f.Chat.Proposal!.ProposedChanges.Single(c => c.Placement.Section.Course == "429");
            Assert.InRange(c.Room.Capacity, 35, 45);
        }
    }

    [Fact]
    public void Availability_queries_apply_building_capacity_and_recording_filters()
    {
        var f = Fixture(); f.Rooms[1].LectureCapture = true;
        Send(f, "which rooms are free in GDC under 60 seats with lecture capture Tuesday 2pm-3pm?");
        Assert.Contains("1 active room(s)", Last(f.Chat)); Assert.Contains("GDC 1.100: 40 seats", Last(f.Chat));
        Assert.DoesNotContain("RLP 1.104:", Last(f.Chat)); Assert.Null(f.Chat.Proposal);
    }

    [Fact]
    public void Availability_clarification_remembers_query_filters_without_changing_move_filters()
    {
        var f = Fixture(); Send(f, "move comp arch to Tuesday at 2pm"); var preview = f.Chat.Proposal;
        Send(f, "which rooms are free in RLP under 60 seats?");
        Send(f, "Tuesday 2pm-3pm");
        Assert.Contains("1 active room(s)", Last(f.Chat)); Assert.Contains("RLP 1.104: 40 seats", Last(f.Chat));
        Assert.Same(preview, f.Chat.Proposal);
        Assert.DoesNotContain("in RLP", f.Chat.RequestContext!);
    }

    [Fact]
    public void Capacity_question_describes_the_selected_option_and_preserves_it()
    {
        var f = Fixture(); Send(f, "find comp arch a free slot after 2pm"); var preview = f.Chat.Proposal!;
        var room = preview.ProposedChanges.First(c => c.Placement.Section.Course == "429").Room;
        Send(f, "how many seats in that room?");
        Assert.Contains(room.DisplayName, Last(f.Chat)); Assert.Contains($"{room.Capacity} seats", Last(f.Chat));
        Assert.Contains("not been applied", Last(f.Chat)); Assert.Same(preview, f.Chat.Proposal);
    }

    [Fact]
    public void Oversized_room_audit_is_grounded_in_room_fit_rules()
    {
        var f = Fixture(); f.Plan.Placements[0].Room = f.Rooms[0];
        Send(f, "which classes have oversized rooms?");
        Assert.Contains("429 COMPUTER ARCHITECTURE", Last(f.Chat)); Assert.Contains("170 spare seats", Last(f.Chat));
        Assert.DoesNotContain("314 DATA STRUCTURES", Last(f.Chat)); Assert.Null(f.Chat.Proposal);
    }

    [Fact]
    public void Explicit_day_relaxation_removes_only_that_exclusion()
    {
        var f = Fixture(); Send(f, "find comp arch a slot after 2pm, no Fridays");
        Send(f, "Fridays are okay");
        Assert.NotNull(f.Chat.Proposal);
        Assert.DoesNotContain("excluding Friday", f.Chat.RequestContext!);
        var c = f.Chat.Proposal!.ProposedChanges.First(c => c.Placement.Section.Course == "429");
        Assert.True(c.Time.Start >= TimeSpan.FromHours(14));
    }

    [Fact]
    public void Explicit_building_choice_removes_its_previous_exclusion()
    {
        var f = Fixture(); Send(f, "find comp arch a slot outside GDC after 2pm");
        Send(f, "actually in GDC");
        Assert.NotNull(f.Chat.Proposal);
        var c = f.Chat.Proposal!.ProposedChanges.First(c => c.Placement.Section.Course == "429");
        Assert.Equal("GDC", c.Room.Building); Assert.True(c.Time.Start >= TimeSpan.FromHours(14));
        Assert.DoesNotContain("outside GDC", f.Chat.RequestContext!);
    }

    [Theory]
    [InlineData("at least 99999999999999999999999 seats")]
    [InlineData("over 2147483647 seats")]
    public void Impossible_or_oversized_numeric_constraints_never_fall_back_to_unconstrained_results(string constraint)
    {
        var f = Fixture(); Send(f, "find comp arch a room " + constraint);
        Assert.Null(f.Chat.Proposal); Assert.Contains("unchanged", Last(f.Chat));
    }

    [Fact]
    public void Removing_seat_limits_clears_only_the_requested_constraint()
    {
        var f = Fixture(); Send(f, "find comp arch a room in RLP under 20 seats");
        Assert.Null(f.Chat.Proposal); Send(f, "any size");
        Assert.NotNull(f.Chat.Proposal);
        Assert.Equal("RLP", f.Chat.Proposal!.ProposedChanges.First(c => c.Placement.Section.Course == "429").Room.Building);
    }

    [Fact]
    public void Best_option_selects_a_checked_preview_without_applying_it()
    {
        var f = Fixture(); Send(f, "find comp arch a slot after 2pm"); var preview = f.Chat.Proposal;
        Send(f, "the recommended option");
        Assert.Same(preview, f.Chat.Proposal); Assert.Contains("Selected option 1", Last(f.Chat));
        Assert.Equal(MeetingTime.Of("MWF", 9, 0, 10, 0), f.Plan.Placements[0].Time);
    }
    [Fact]
    public void Misspelled_course_name_asks_for_confirmation_and_preserves_requested_time()
    {
        var f = Fixture(); Send(f, "move archtecture to Tuesday at 2pm");
        Assert.Null(f.Chat.Proposal); Assert.Contains("Did you mean", Last(f.Chat));
        Send(f, "yes");
        Assert.NotNull(f.Chat.Proposal);
        var c = f.Chat.Proposal!.ProposedChanges.First(c => c.Placement.Section.Course == "429");
        Assert.Equal(MeetingTime.Of("T", 14, 0, 15, 0), c.Time);
    }

    [Fact]
    public void A_new_request_can_replace_an_unconfirmed_spelling_suggestion()
    {
        var f = Fixture(); Send(f, "move archtecture to Tuesday at 2pm");
        Send(f, "move data structures to Thursday at 3pm");
        Assert.NotNull(f.Chat.Proposal);
        var c = f.Chat.Proposal!.ProposedChanges.First(c => c.Placement.Section.Course == "314");
        Assert.Equal(MeetingTime.Of("TH", 15, 0, 16, 0), c.Time);
    }

    [Fact]
    public void Choosing_a_room_after_clarification_keeps_requested_time_and_seat_limit()
    {
        var f = Fixture(); Send(f, "move comp arch to GDC 1.100 or RLP 1.104 Tuesday at 2pm, under 50 seats");
        Send(f, "Use RLP 1.104");
        Assert.NotNull(f.Chat.Proposal);
        var c = f.Chat.Proposal!.ProposedChanges.First(c => c.Placement.Section.Course == "429");
        Assert.Equal("RLP", c.Room.Building); Assert.Equal(MeetingTime.Of("T", 14, 0, 15, 0), c.Time);
        Assert.Contains("at most 49 seats", f.Chat.RequestContext!);
    }

    [Theory]
    [InlineData("move data structures with Smith to Tuesday at 2pm")]
    [InlineData("move 429 with Jones to Tuesday at 2pm")]
    [InlineData("move 999 architecture to Tuesday at 2pm")]
    [InlineData("move 999 archtecture to Tuesday at 2pm")]
    public void Conflicting_or_unknown_course_selectors_never_choose_a_different_course(string request)
    {
        var f = Fixture(); Send(f, request);
        Assert.Null(f.Chat.Proposal);
        Assert.All(f.Plan.Placements, p => Assert.Equal(MeetingTime.Of("MWF", 9, 0, 10, 0), p.Time));
    }

    [Fact]
    public void Capacity_followup_uses_the_most_recent_course_lookup()
    {
        var f = Fixture(); Send(f, "move comp arch to Tuesday at 2pm");
        Send(f, "where is data structures?"); Send(f, "how many seats in that room?");
        Assert.Contains("314 DATA STRUCTURES", Last(f.Chat)); Assert.DoesNotContain("429 COMPUTER ARCHITECTURE", Last(f.Chat));
    }

    [Fact]
    public void Spelling_confirmation_preserves_an_open_ended_slot_search()
    {
        var f = Fixture(); Send(f, "find archtecture a free slot");
        Assert.Null(f.Chat.Proposal); Assert.Contains("Did you mean", Last(f.Chat));
        Send(f, "yes"); Assert.NotNull(f.Chat.Proposal);
    }

    [Fact]
    public void An_invalid_seat_count_is_not_silently_ignored_when_another_bound_is_valid()
    {
        var f = Fixture(); Send(f, "find comp arch a room at least 999999999999999999999 seats and under 60 seats");
        Assert.Null(f.Chat.Proposal); Assert.Contains("seat count is too large", Last(f.Chat));
        Send(f, "at least 30 seats"); Assert.NotNull(f.Chat.Proposal);
    }

}
