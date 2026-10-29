using RoomScheduler;
using Xunit;

namespace RoomScheduler.Tests;

public partial class SwapTests
{
    private static (Planner Planner, Planner.Plan Plan, List<Room> Rooms, SchedulingConversation Chat) Fixture(bool third = false)
    {
        var sheet = new Xlsx.Sheet {Name = "Classes App"};
        sheet.Rows.AddRange(new[] {
            new List<string> {"Title", "Professor", "Minimum Room Size Needed"},
            new List<string> {"429 COMP ORGANIZATN AND ARCH", "SMITH A", "30"},
            new List<string> {"314 DATA STRUCTURES", "JONES B", "30"}
        });
        if (third) sheet.Rows.Add(new() {"375 SYSTEMS", "DOE C", "30"});
        var rooms = new List<Room> {
            new() {Building = "GDC", RoomNumber = "1.100", Capacity = 40},
            new() {Building = "GDC", RoomNumber = "2.216", Capacity = 50, CsOwnsFrom = TimeSpan.FromHours(12.5)},
            new() {Building = "RLP", RoomNumber = "1.104", Capacity = 40}
        };
        var planner = new Planner(rooms, new Campus(), PastSchedule.Empty, FacultyStore.InMemory());
        var plan = planner.Build(DepartmentWorkbook.FromSheets(new() {sheet}));
        for (int i = 0; i < plan.Placements.Count; i++)
        {
            plan.Placements[i].Room = rooms[i];
            plan.Placements[i].Time = MeetingTime.Of("MTWTH", 9, 0, 10, 0);
        }
        return (planner, plan, rooms, new());
    }
    private static void Send((Planner Planner, Planner.Plan Plan, List<Room> Rooms, SchedulingConversation Chat) f, string text) =>
        f.Chat.Send(f.Plan, f.Rooms, (s,b) => throw new InvalidOperationException("Generated moves must not use the text parser."), text, false,
            (p,r,t,b) => f.Planner.ProposeOverride(f.Plan,p,r,t,b),
            (a,b,t,o) => f.Planner.ProposeSwap(f.Plan,a,b,t,o));
    private static string Last(SchedulingConversation chat) => chat.Messages.Last().Text;
    private static void NoConflicts(Planner.Plan plan)
    {
        foreach (var p in plan.Placements.Where(p => p.Room != null && p.Time != null))
            foreach (var other in plan.Placements.Where(o => o != p && o.Time != null && o.Time.Overlaps(p.Time!)))
            {
                Assert.NotEqual(p.Room, other.Room);
                Assert.NotEqual(p.Section.Surname, other.Section.Surname);
            }
    }

    [Fact]
    public void Every_generated_weekday_pattern_can_round_trip_through_explicit_command_parser()
    {
        var f = Fixture();
        for (int mask = 1; mask < 32; mask++)
        {
            var days = Enumerable.Range(1,5).Where(d => (mask & (1 << (d-1))) != 0).Select(d => (DayOfWeek)d).ToList();
            var time = new MeetingTime(days, TimeSpan.FromHours(14), TimeSpan.FromHours(15));
            var proposal = f.Planner.ProposeOverride(f.Plan, $"Put 429 in GDC 1.100 {time.Pattern} at 2:00pm-3:00pm");
            Assert.Equal(time, proposal.ProposedChanges.Single(c => c.Placement.Section.Course == "429").Time);
        }
    }

    [Fact]
    public void Free_slot_search_uses_typed_meeting_times_including_four_day_patterns()
    {
        var f = Fixture();
        Send(f,"move comp arch to reserved CS time");
        Send(f,f.Chat.Suggestions.Last());
        Send(f,"find a free slot");
        Assert.True(f.Chat.Proposal != null, Last(f.Chat));
        Assert.Contains(f.Chat.Proposal!.ProposedChanges, c => c.Placement.Section.Course == "429" && c.Time.Pattern == "MTWTH");
        Assert.DoesNotContain("Use named weekdays", Last(f.Chat));
    }

    [Fact]
    public void Room_swap_preview_is_atomic_and_apply_preserves_both_times_without_conflicts()
    {
        var f = Fixture(); var a = f.Plan.Placements[0]; var b = f.Plan.Placements[1];
        var roomA = a.Room; var roomB = b.Room; var timeA = a.Time; var timeB = b.Time;
        var proposal = f.Planner.ProposeSwap(f.Plan,a,b);
        Assert.Equal(2,proposal.ProposedChanges.Count);
        Assert.Equal(roomA,a.Room); Assert.Equal(roomB,b.Room);
        f.Planner.ApplyOverride(f.Plan,proposal);
        Assert.Equal(roomB,a.Room); Assert.Equal(roomA,b.Room);
        Assert.Equal(timeA,a.Time); Assert.Equal(timeB,b.Time); NoConflicts(f.Plan);
    }

    [Fact]
    public void Explicit_room_swap_and_pronoun_followup_generate_proposals_instead_of_free_slot_retry()
    {
        var f = Fixture();
        Send(f,"swap rooms for comp arch with data structures");
        Assert.True(f.Chat.Proposal != null,Last(f.Chat));
        Assert.Contains("room-only",Last(f.Chat));
        Assert.Equal(2,f.Chat.Proposal!.ProposedChanges.Count);
        Send(f,"swap it then");
        Assert.True(f.Chat.Proposal != null,Last(f.Chat));
        Assert.DoesNotContain("Use named weekdays",Last(f.Chat));
    }

    [Fact]
    public void Swap_with_another_course_preserves_a_pronoun_course_reference()
    {
        var f = Fixture(); Send(f,"where is comp arch?"); Send(f,"swap it with data structures");
        Assert.True(f.Chat.Proposal != null,Last(f.Chat));
        Assert.Equal(2,f.Chat.Proposal!.ProposedChanges.Count);
    }

    [Fact]
    public void Successful_free_slot_proposal_does_not_pin_later_swap_search_to_its_generated_room_and_time()
    {
        var f = Fixture(); Send(f,"move comp arch to another time"); Send(f,"find a free slot");
        Assert.NotNull(f.Chat.Proposal);
        Send(f,"swap it then");
        Assert.True(f.Chat.Proposal != null,Last(f.Chat));
        Assert.Contains("swap option",Last(f.Chat));
    }

    [Fact]
    public void Swap_options_can_be_selected_and_no_changes_happen_until_apply()
    {
        var f = Fixture(true); Send(f,"swap rooms for comp arch");
        Assert.True(f.Chat.Proposal != null,Last(f.Chat));
        var choices = f.Chat.Suggestions.Where(s => s.StartsWith("Swap with")).ToList();
        Assert.Equal(2,choices.Count);
        var first = f.Chat.Proposal;
        Send(f,choices[1]);
        Assert.NotSame(first,f.Chat.Proposal);
        Assert.Equal(f.Rooms[0],f.Plan.Placements[0].Room);
        f.Planner.ApplyOverride(f.Plan,f.Chat.Proposal!); NoConflicts(f.Plan);
    }

    [Fact]
    public void Swapping_rooms_and_times_is_explicit_and_validated()
    {
        var f = Fixture(); f.Plan.Placements[1].Time = MeetingTime.Of("TTH",14,0,15,0);
        var a = f.Plan.Placements[0]; var b = f.Plan.Placements[1]; var before = a.Time;
        var proposal = f.Planner.ProposeSwap(f.Plan,a,b,true);
        Assert.Equal(b.Time,proposal.ProposedChanges.Single(c => c.Placement == a).Time);
        f.Planner.ApplyOverride(f.Plan,proposal); Assert.Equal(before,b.Time); NoConflicts(f.Plan);
    }

    [Fact]
    public void Swap_rejects_undersized_rooms_offline_rooms_and_stale_previews()
    {
        var f = Fixture(); var a = f.Plan.Placements[0]; var b = f.Plan.Placements[1];
        b.Section.MinimumRoomCapacity = 45;
        Assert.Throws<ArgumentException>(() => f.Planner.ProposeSwap(f.Plan,a,b));
        b.Section.MinimumRoomCapacity = 0;
        var proposal = f.Planner.ProposeSwap(f.Plan,a,b);
        f.Planner.Rules.Offline.SetRoom(f.Rooms[1],true,f.Rooms);
        Assert.Throws<ArgumentException>(() => f.Planner.ApplyOverride(f.Plan,proposal));
        Assert.Equal(f.Rooms[0],a.Room);
        f.Planner.Rules.Offline.SetRoom(f.Rooms[1],false,f.Rooms);
        b.Time = MeetingTime.Of("F",15,0,16,0);
        Assert.Throws<InvalidOperationException>(() => f.Planner.ApplyOverride(f.Plan,proposal));
    }

    [Fact]
    public void Swap_cannot_create_a_third_party_room_conflict_or_bypass_faculty_lunch()
    {
        var f = Fixture(true); var a = f.Plan.Placements[0]; var b = f.Plan.Placements[1]; var c = f.Plan.Placements[2];
        b.Time = MeetingTime.Of("TTH",14,0,15,0);
        c.Room = b.Room; c.Time = a.Time;
        Assert.Throws<ArgumentException>(() => f.Planner.ProposeSwap(f.Plan,a,b));
        c.Room = f.Rooms[2]; b.Time = MeetingTime.Of("MW",12,0,13,0); a.Section.Track = FacultyTrack.Tenure;
        Assert.Throws<ArgumentException>(() => f.Planner.ProposeSwap(f.Plan,a,b,true));
        Assert.Equal(f.Rooms[0],a.Room);
    }
    [Fact]
    public void Explicit_room_and_time_swap_phrase_and_short_partner_followup_are_understood()
    {
        var f = Fixture(); f.Plan.Placements[1].Time = MeetingTime.Of("TTH",14,0,15,0);
        Send(f,"swap rooms and times for comp arch with data structures");
        Assert.True(f.Chat.Proposal != null,Last(f.Chat)); Assert.Contains("room-and-time",Last(f.Chat));
        Send(f,"swap with data structures");
        Assert.True(f.Chat.Proposal != null,Last(f.Chat));
    }

    [Fact]
    public void Free_slot_followup_can_exit_swap_mode_and_search_actual_free_times()
    {
        var f = Fixture(); Send(f,"swap rooms for comp arch");
        Assert.NotNull(f.Chat.Proposal);
        Send(f,"find a free slot");
        Assert.True(f.Chat.Proposal != null,Last(f.Chat));
        Assert.Contains("I read this as moving",Last(f.Chat));
    }

    [Fact]
    public void Move_can_preview_a_three_room_chain_while_every_displaced_class_keeps_its_time()
    {
        var f = Fixture(true);
        var a=f.Plan.Placements[0]; var b=f.Plan.Placements[1]; var c=f.Plan.Placements[2];
        // A's small room fits only C. B needs C's medium room when A takes B's large room.
        f.Rooms[0].Capacity=35; f.Rooms[1].Capacity=80; f.Rooms[2].Capacity=60;
        a.Section.SizeForRoom=30; b.Section.SizeForRoom=55; c.Section.SizeForRoom=30;
        var time=a.Time!;
        var proposal=f.Planner.ProposeOverride(f.Plan,a,f.Rooms[1],time,true);
        Assert.Equal(3,proposal.ProposedChanges.Count);
        Assert.Equal(f.Rooms[0],a.Room); Assert.Equal(f.Rooms[1],b.Room); Assert.Equal(f.Rooms[2],c.Room);
        Assert.All(proposal.ProposedChanges, change=>Assert.Equal(time,change.Time));
        f.Planner.ApplyOverride(f.Plan,proposal);
        Assert.Equal(f.Rooms[1],a.Room); Assert.Equal(f.Rooms[2],b.Room); Assert.Equal(f.Rooms[0],c.Room);
        NoConflicts(f.Plan);
    }

    [Fact]
    public void An_impossible_room_chain_leaves_every_original_assignment_untouched()
    {
        var f=Fixture(true); var a=f.Plan.Placements[0]; var b=f.Plan.Placements[1];
        f.Rooms[0].Capacity=35; f.Rooms[1].Capacity=80; f.Rooms[2].Capacity=40;
        b.Section.SizeForRoom=60;
        var before=f.Plan.Placements.Select(p=>(p.Room,p.Time)).ToList();
        Assert.Throws<ArgumentException>(()=>f.Planner.ProposeOverride(f.Plan,a,f.Rooms[1],a.Time!,true));
        Assert.Equal(before,f.Plan.Placements.Select(p=>(p.Room,p.Time)).ToList());
        NoConflicts(f.Plan);
    }

    [Fact]
    public void Swap_options_accept_natural_ordinal_selection()
    {
        var f=Fixture(true); Send(f,"find room swaps for comp arch"); var first=f.Chat.Proposal;
        Assert.NotNull(first); Send(f,"the second one"); Assert.NotNull(f.Chat.Proposal);
        Assert.NotSame(first,f.Chat.Proposal); Assert.Contains("Selected option 2",Last(f.Chat));
    }

    [Fact]
    public void Seat_limit_followup_refines_swap_partners_instead_of_switching_to_a_move()
    {
        var f = Fixture(true); Send(f, "swap rooms for comp arch");
        Send(f, "under 45 seats");
        Assert.NotNull(f.Chat.Proposal); Assert.Contains("room-only swap", Last(f.Chat));
        Assert.Equal(2, f.Chat.Proposal!.ProposedChanges.Count);
        var change = f.Chat.Proposal!.ProposedChanges.Single(c => c.Placement.Section.Course == "429");
        Assert.Equal(f.Rooms[2], change.Room);
        Assert.Contains("40 seats", f.Chat.Suggestions.First());
        Assert.DoesNotContain("50 seats", f.Chat.Suggestions.First());
    }

    [Fact]
    public void Selected_swap_and_comparisons_keep_capacity_and_spare_seats_visible()
    {
        var f = Fixture(true); Send(f, "swap rooms for comp arch");
        Send(f, "the second one");
        Assert.Contains("seats", Last(f.Chat)); Assert.Contains("spare", Last(f.Chat));
        var preview = f.Chat.Proposal;
        Send(f, "compare the options");
        Assert.Contains("40 seats", Last(f.Chat)); Assert.Contains("50 seats", Last(f.Chat));
        Assert.Same(preview, f.Chat.Proposal);
    }

}
