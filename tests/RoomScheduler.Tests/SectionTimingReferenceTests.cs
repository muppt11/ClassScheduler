using RoomScheduler;
using Xunit;

namespace RoomScheduler.Tests;

public class SectionTimingReferenceTests
{
    private static (Planner Planner, Planner.Plan Plan, List<Room> Rooms, SchedulingConversation Chat) Fixture(bool duplicateTime = false)
    {
        var sheet = new Xlsx.Sheet { Name = "Classes App" };
        sheet.Rows.Add(new() { "Title", "Professor", "Minimum Room Size Needed" });
        sheet.Rows.Add(new() { "312 INTRODUCTION TO PROGRAMMING", "BEASLEY A", "30" });
        sheet.Rows.Add(new() { "312 INTRODUCTION TO PROGRAMMING", "BEASLEY A", "30" });
        sheet.Rows.Add(new() { "313E ELEMENTS OF SOFTWARE DESIGN", "FONG M", "30" });
        sheet.Rows.Add(new() { "313E ELEMENTS OF SOFTWARE DESIGN", "FONG M", "30" });
        sheet.Rows.Add(new() { "313E ELEMENTS OF SOFTWARE DESIGN", "RAVISHANKAR V", "30" });
        if (duplicateTime) sheet.Rows.Add(new() { "312 INTRODUCTION TO PROGRAMMING", "SMITH B", "30" });
        var rooms = Enumerable.Range(0, sheet.Rows.Count - 1)
            .Select(i => new Room { Building = "GDC", RoomNumber = $"1.{100 + i}", Capacity = 40 }).ToList();
        var planner = new Planner(rooms, new Campus(), PastSchedule.Empty, FacultyStore.InMemory());
        var plan = planner.Build(DepartmentWorkbook.FromSheets(new() { sheet }));
        for (int i = 0; i < plan.Placements.Count; i++)
        {
            plan.Placements[i].Room = rooms[i];
            plan.Placements[i].Time = i is 0 or 2 ? MeetingTime.Of("TTH", 9, 30, 11, 0) :
                i == 4 ? MeetingTime.Of("MW", 14, 0, 15, 30) : MeetingTime.Of("TTH", 14, 0, 15, 30);
        }
        return (planner, plan, rooms, new());
    }

    private static void Send((Planner Planner, Planner.Plan Plan, List<Room> Rooms, SchedulingConversation Chat) f, string text) =>
        f.Chat.Send(f.Plan, f.Rooms, (s, b) => f.Planner.ProposeOverride(f.Plan, s, b), text, false,
            (p, r, t, b) => f.Planner.ProposeOverride(f.Plan, p, r, t, b),
            (a, b, t, o) => f.Planner.ProposeSwap(f.Plan, a, b, t, o));

    [Theory]
    [InlineData("can you swap rooms for CS 312 (TTH 2 - 3:30pm) and CS 313E (TTH 2 - 3:30P)?")]
    [InlineData("swap rooms for CS 312 (TTH 2-3:30) with CS 313E (TTH 2-3:30)")]
    [InlineData("swap rooms for CS 312 (Tuesday and Thursday 2pm-3:30pm) and CS 313E (Tuesday and Thursday 2pm-3:30pm)")]
    [InlineData("swap rooms for CS 312 TTH 2pm-3:30pm with CS 313E TTH 2pm-3:30pm")]
    [InlineData("swap rooms for CS 312 Tuesday and Thursday 2pm-3:30pm with CS 313E Tuesday and Thursday 2pm-3:30pm")]
    [InlineData("swap rooms for CS 312 (TTH 14:00-15:30) with CS 313E (TTH 14:00-15:30)")]
    [InlineData("swap rooms for CS 312 (2pm) and CS 313E (TTH 2pm)")]
    public void Meeting_times_identify_both_sections_without_questions_or_time_changes(string request)
    {
        var f = Fixture();
        var before = f.Plan.Placements.Select(p => (p.Room, p.Time)).ToList();
        Send(f, request);
        Assert.True(f.Chat.Proposal != null, f.Chat.Messages.Last().Text);
        Assert.Equal(new[] { f.Plan.Placements[1], f.Plan.Placements[3] }, f.Chat.Proposal!.ProposedChanges.Select(c => c.Placement));
        Assert.All(f.Chat.Proposal.ProposedChanges, c => Assert.Equal(c.BeforeTime, c.Time));
        Assert.Equal(before, f.Plan.Placements.Select(p => (p.Room, p.Time)).ToList());
        Assert.DoesNotContain("Requested time:", f.Chat.RequestContext!);
        Assert.DoesNotContain("Search conditions: on", f.Chat.RequestContext!);
        f.Planner.ApplyOverride(f.Plan, f.Chat.Proposal);
        Assert.Equal(f.Rooms[3], f.Plan.Placements[1].Room);
        Assert.Equal(f.Rooms[1], f.Plan.Placements[3].Room);
    }

    [Fact]
    public void Identifying_times_do_not_pin_a_room_and_time_exchange_to_the_original_times()
    {
        var f = Fixture();
        Send(f, "swap rooms and times for CS 312 (TTH 9:30am-11am) with CS 313E (MW 2pm-3:30pm)");
        Assert.True(f.Chat.Proposal != null, f.Chat.Messages.Last().Text);
        Assert.Contains(f.Chat.Proposal!.ProposedChanges, c => c.Placement == f.Plan.Placements[0] && c.Time == f.Plan.Placements[4].Time);
        Assert.Contains(f.Chat.Proposal.ProposedChanges, c => c.Placement == f.Plan.Placements[4] && c.Time == f.Plan.Placements[0].Time);
    }

    [Theory]
    [InlineData("swap rooms for CS 312 (MWF 2pm-3:30pm) with CS 313E (TTH 2pm-3:30pm)")]
    [InlineData("swap rooms for CS 312 (TTH 2pm-3:30pm) with CS 313E (TTH 4pm-5:30pm)")]
    public void A_nonexistent_current_time_never_falls_back_to_another_section(string request)
    {
        var f = Fixture();
        Send(f, "swap rooms for CS 312 (TTH 9:30am-11am) with CS 313E (TTH 9:30am-11am)");
        Assert.NotNull(f.Chat.Proposal);
        Send(f, request);
        Assert.Null(f.Chat.Proposal);
        Assert.Contains("No changes were made", f.Chat.Messages.Last().Text);
    }

    [Fact]
    public void Truly_identical_course_times_still_need_an_instructor_or_row()
    {
        var f = Fixture(true);
        Send(f, "swap rooms for CS 312 (TTH 2pm-3:30pm) with CS 313E (TTH 2pm-3:30pm)");
        Assert.Null(f.Chat.Proposal);
        Assert.Equal(2, f.Chat.Suggestions.Count);
        Assert.DoesNotContain(f.Chat.Suggestions, s => s.Contains("row 2"));
    }

    [Fact]
    public void A_timing_only_reply_can_select_a_section_without_repeating_the_course()
    {
        var f = Fixture();
        Send(f, "swap rooms for CS 312 with CS 313E");
        Assert.Null(f.Chat.Proposal);
        Send(f, "TTH 2-3:30pm");
        Assert.Null(f.Chat.Proposal);
        Send(f, "TTH 2-3:30pm");
        Assert.True(f.Chat.Proposal != null, f.Chat.Messages.Last().Text);
        Assert.Equal(new[] { f.Plan.Placements[1], f.Plan.Placements[3] }, f.Chat.Proposal!.ProposedChanges.Select(c => c.Placement));
        Assert.All(f.Chat.Proposal.ProposedChanges, c => Assert.Equal(c.BeforeTime, c.Time));
    }

    [Fact]
    public void Timing_selection_replies_identify_sections_at_different_times_without_adding_destination_constraints()
    {
        var f = Fixture();
        Send(f, "swap rooms for CS 312 with CS 313E");
        Send(f, "TTH 9:30am-11am");
        Send(f, "MW 2-3:30pm");
        Assert.True(f.Chat.Proposal != null, f.Chat.Messages.Last().Text);
        Assert.Equal(new[] { f.Plan.Placements[0], f.Plan.Placements[4] }, f.Chat.Proposal!.ProposedChanges.Select(c => c.Placement));
        Assert.All(f.Chat.Proposal.ProposedChanges, c => Assert.Equal(c.BeforeTime, c.Time));
        Assert.DoesNotContain("Requested time:", f.Chat.RequestContext!);
        Assert.DoesNotContain("Search conditions:", f.Chat.RequestContext!);
    }

    [Fact]
    public void A_parenthetical_section_time_is_distinct_from_a_move_destination()
    {
        var f = Fixture();
        Send(f, "move CS 312 (TTH 2pm-3:30pm) to Friday at 10am");
        Assert.True(f.Chat.Proposal != null, f.Chat.Messages.Last().Text);
        Assert.Contains(f.Chat.Proposal!.ProposedChanges, c => c.Placement == f.Plan.Placements[1] && c.Time == MeetingTime.Of("F", 10, 0, 11, 30));
    }
}
