using RoomScheduler;
using Xunit;

namespace RoomScheduler.Tests;

public class CapacityRoomSearchTests
{
    private static (Planner Planner, Planner.Plan Plan, List<Room> Rooms, SchedulingConversation Chat) Fixture(string occupancy = "none", bool replacement = true)
    {
        var sheet = new Xlsx.Sheet { Name = "Classes App" };
        sheet.Rows.Add(new() { "Title", "Professor", "Minimum Room Size Needed" });
        sheet.Rows.Add(new() { "104C COMPETITIVE PROGRAMMING", "VOUGA P", "30" });
        if (occupancy != "none") sheet.Rows.Add(new() { "314 DATA STRUCTURES", "JONES B", "60" });
        var rooms = new List<Room> {
            new() { Building = "GDC", RoomNumber = "1.100", Capacity = 30 },
            new() { Building = "GDC", RoomNumber = "2.200", Capacity = 80 }
        };
        if (replacement) rooms.AddRange(new[] {
            new Room { Building = "RLP", RoomNumber = "1.104", Capacity = 60 },
            new Room { Building = "GDC", RoomNumber = "2.201", Capacity = 120 },
            new Room { Building = "GDC", RoomNumber = "2.202", Capacity = 79 },
            new Room { Building = "GDC", RoomNumber = "2.203", Capacity = 121 },
            new Room { Building = "GDC", RoomNumber = "2.204", Capacity = 100 }
        });
        var planner = new Planner(rooms, new Campus(), PastSchedule.Empty, FacultyStore.InMemory());
        if (replacement) planner.Rules.Offline.SetRoom(rooms.Last(), true, rooms);
        var plan = planner.Build(DepartmentWorkbook.FromSheets(new() { sheet }));
        for (int i = 0; i < plan.Placements.Count; i++)
        {
            plan.Placements[i].Room = rooms[i];
            plan.Placements[i].Time = i == 1 && occupancy == "other-time" ?
                MeetingTime.Of("TTH", 9, 0, 10, 0) : MeetingTime.Of("M", 16, 0, 17, 0);
            plan.Placements[i].Section.SizeForRoom = i == 0 ? 30 : 60;
        }
        return (planner, plan, rooms, new());
    }

    private static void Send((Planner Planner, Planner.Plan Plan, List<Room> Rooms, SchedulingConversation Chat) f, string message) =>
        f.Chat.Send(f.Plan, f.Rooms, (text, allow, seats) => f.Planner.ProposeOverride(f.Plan, text, allow, seats), message, false,
            (p, room, time, allow, seats) => f.Planner.ProposeOverride(f.Plan, p, room, time, allow, seats),
            (a, b, times, allow, seats) => f.Planner.ProposeSwap(f.Plan, a, b, times, allow, seats));

    private static void AssertNoConflicts(Planner.Plan plan)
    {
        var placed = plan.Placements.Concat(plan.Discussions).Where(p => p.Room != null && p.Time != null).ToList();
        foreach (var p in placed)
        {
            Assert.True(p.Room!.Capacity >= Math.Max(p.Section.SizeForRoom, p.Section.MinimumRoomCapacity));
            foreach (var other in placed.Where(o => o != p && o.Time!.Overlaps(p.Time!)))
            {
                Assert.NotEqual(p.Room, other.Room);
                Assert.NotEqual(p.Section.Surname, other.Section.Surname);
            }
        }
    }

    [Theory]
    [InlineData("can you give room swap options that would have 80 seats for 104C COMPETITIVE PROGRAMMING?", 80, int.MaxValue)]
    [InlineData("find room swaps for 104C with 80–120 seats", 80, 120)]
    [InlineData("swap rooms for 104C with exactly 80 seats", 80, 80)]
    [InlineData("find 104C an unused room between 80 and 120 seats", 80, 120)]
    public void Requested_capacity_search_includes_unused_rooms_without_changing_enrollment(string request, int minimum, int maximum)
    {
        var f = Fixture();
        var target = f.Plan.Placements[0];
        var before = (target.Room, target.Time);
        Send(f, request);
        Assert.True(f.Chat.Proposal != null, f.Chat.Messages.Last().Text);
        var choices = f.Chat.Suggestions.Where(s => s.StartsWith("Option ")).ToList();
        Assert.NotEmpty(choices);
        foreach (string choice in choices)
        {
            Send(f, choice);
            var change = Assert.Single(f.Chat.Proposal!.ProposedChanges);
            Assert.Equal(target, change.Placement);
            Assert.InRange(change.Room.Capacity, minimum, maximum);
            Assert.False(f.Planner.Rules.Offline.IsOffline(change.Room));
            Assert.Equal(before.Time, change.Time);
            Assert.Equal(before, (target.Room, target.Time));
            Assert.Equal(30, target.Section.SizeForRoom);
        }
        f.Planner.ApplyOverride(f.Plan, f.Chat.Proposal!);
        Assert.InRange(target.Room!.Capacity, minimum, maximum);
        Assert.Equal(30, target.Section.SizeForRoom);
        AssertNoConflicts(f.Plan);
    }

    [Fact]
    public void A_room_used_at_another_time_is_available_at_the_requested_sections_time()
    {
        var f = Fixture("other-time");
        Send(f, "find room swaps for 104C with exactly 80 seats");
        Assert.True(f.Chat.Proposal != null, f.Chat.Messages.Last().Text);
        var change = Assert.Single(f.Chat.Proposal!.ProposedChanges);
        Assert.Equal(f.Rooms[1], change.Room);
        f.Planner.ApplyOverride(f.Plan, f.Chat.Proposal);
        AssertNoConflicts(f.Plan);
    }

    [Fact]
    public void Occupied_room_move_rehouses_the_displaced_class_in_a_free_room_before_atomic_apply()
    {
        var f = Fixture("same-time");
        var before = f.Plan.Placements.Select(p => (p.Room, p.Time)).ToList();
        Send(f, "swap rooms for 104C with exactly 80 seats");
        Assert.True(f.Chat.Proposal != null, f.Chat.Messages.Last().Text);
        Assert.Equal(2, f.Chat.Proposal!.ProposedChanges.Count);
        Assert.Contains(f.Chat.Proposal.ProposedChanges, c => c.Placement == f.Plan.Placements[0] && c.Room == f.Rooms[1]);
        var replacement = f.Chat.Proposal.ProposedChanges.Single(c => c.Placement == f.Plan.Placements[1]).Room;
        Assert.True(replacement.Capacity >= 60);
        Assert.DoesNotContain(f.Plan.Placements, p => p.Room == replacement && p.Time!.Overlaps(f.Plan.Placements[1].Time!));
        Assert.All(f.Chat.Proposal.ProposedChanges, c => Assert.Equal(c.BeforeTime, c.Time));
        Assert.Equal(before, f.Plan.Placements.Select(p => (p.Room, p.Time)).ToList());
        Send(f, "yes");
        Assert.Equal(before, f.Plan.Placements.Select(p => (p.Room, p.Time)).ToList());
        f.Planner.ApplyOverride(f.Plan, f.Chat.Proposal);
        Assert.Equal(f.Rooms[1], f.Plan.Placements[0].Room);
        Assert.Equal(replacement, f.Plan.Placements[1].Room);
        AssertNoConflicts(f.Plan);
    }

    [Fact]
    public void No_room_change_is_offered_if_the_displaced_class_cannot_be_rehoused()
    {
        var f = Fixture("same-time", false);
        var before = f.Plan.Placements.Select(p => (p.Room, p.Time)).ToList();
        Send(f, "find room swaps for 104C with exactly 80 seats");
        Assert.Null(f.Chat.Proposal);
        Assert.Equal(before, f.Plan.Placements.Select(p => (p.Room, p.Time)).ToList());
    }

    [Fact]
    public void A_requested_minimum_is_rechecked_before_applying_a_preview()
    {
        var f = Fixture();
        Send(f, "find room swaps for 104C with exactly 80 seats");
        Assert.NotNull(f.Chat.Proposal);
        f.Rooms[1].Capacity = 60;
        Assert.Throws<ArgumentException>(() => f.Planner.ApplyOverride(f.Plan, f.Chat.Proposal!));
        Assert.Equal(f.Rooms[0], f.Plan.Placements[0].Room);
    }

    [Fact]
    public void Text_proposal_fallback_uses_the_same_requested_capacity_and_keeps_actual_enrollment()
    {
        var f = Fixture();
        f.Chat.Send(f.Plan, f.Rooms, (text, oversized, seats) => f.Planner.ProposeOverride(f.Plan, text, oversized, seats),
            "find 104C a room with 80–120 seats");
        Assert.True(f.Chat.Proposal != null, f.Chat.Messages.Last().Text);
        var change = Assert.Single(f.Chat.Proposal!.ProposedChanges);
        Assert.InRange(change.Room.Capacity, 80, 120);
        Assert.Equal(30, change.Placement.Section.SizeForRoom);
        f.Planner.ApplyOverride(f.Plan, f.Chat.Proposal);
        AssertNoConflicts(f.Plan);
    }

    [Fact]
    public void Requested_capacity_for_one_course_does_not_relax_the_partners_oversized_room_check()
    {
        var f = Fixture("same-time");
        f.Rooms[0].Capacity = 150;
        Assert.Throws<ArgumentException>(() => f.Planner.ProposeSwap(f.Plan, f.Plan.Placements[0], f.Plan.Placements[1], requestedMinimumSeats: 80));
        f.Rooms[0].Capacity = 70;
        var proposal = f.Planner.ProposeSwap(f.Plan, f.Plan.Placements[0], f.Plan.Placements[1], requestedMinimumSeats: 80);
        f.Planner.ApplyOverride(f.Plan, proposal);
        Assert.Equal(30, f.Plan.Placements[0].Section.SizeForRoom);
        AssertNoConflicts(f.Plan);
    }
}
