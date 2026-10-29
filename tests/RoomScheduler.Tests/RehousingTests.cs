using RoomScheduler;
using Xunit;

namespace RoomScheduler.Tests;

public partial class SwapTests
{
    [Fact]
    public void Chat_move_into_occupied_room_previews_and_applies_a_room_for_the_displaced_class()
    {
        var f = Fixture(); var first = f.Plan.Placements[0]; var displaced = f.Plan.Placements[1];
        var before = f.Plan.Placements.Select(p => (p.Room, p.Time)).ToList();
        Send(f, "move comp arch to GDC 2.216");
        Assert.NotNull(f.Chat.Proposal);
        Assert.Contains("Every displaced class has a checked replacement room", Last(f.Chat));
        var replacement = f.Chat.Proposal!.ProposedChanges.Single(c => c.Placement == displaced);
        Assert.NotNull(replacement.Room); Assert.Equal(displaced.Time, replacement.Time);
        Assert.Equal(before, f.Plan.Placements.Select(p => (p.Room, p.Time)).ToList());
        f.Planner.ApplyOverride(f.Plan, f.Chat.Proposal!);
        Assert.Equal(f.Rooms[1], first.Room); Assert.Equal(replacement.Room, displaced.Room);
        Assert.All(f.Plan.Placements, p => Assert.NotNull(p.Room)); NoConflicts(f.Plan);
    }

    [Fact]
    public void A_longer_meeting_that_displaces_two_classes_rehouses_both_at_their_original_times()
    {
        var f = Fixture(true); var first = f.Plan.Placements[0];
        var second = f.Plan.Placements[1]; var third = f.Plan.Placements[2];
        second.Time = MeetingTime.Of("MW", 9, 0, 10, 0);
        third.Room = second.Room; third.Time = MeetingTime.Of("MW", 10, 0, 11, 0);
        var secondTime = second.Time; var thirdTime = third.Time;
        var before = f.Plan.Placements.Select(p => (p.Room, p.Time)).ToList();
        var proposal = f.Planner.ProposeOverride(f.Plan, first, f.Rooms[1], MeetingTime.Of("MW", 9, 0, 11, 0));
        Assert.Equal(3, proposal.ProposedChanges.Count);
        Assert.Equal(before, f.Plan.Placements.Select(p => (p.Room, p.Time)).ToList());
        f.Planner.ApplyOverride(f.Plan, proposal);
        Assert.All(f.Plan.Placements, p => Assert.NotNull(p.Room));
        Assert.Equal(secondTime, second.Time); Assert.Equal(thirdTime, third.Time); NoConflicts(f.Plan);
    }

    [Fact]
    public void Displaced_discussion_is_included_in_the_preview_and_keeps_a_checked_room()
    {
        var f = Fixture(); var first = f.Plan.Placements[0]; var second = f.Plan.Placements[1];
        second.Time = MeetingTime.Of("MW", 9, 0, 10, 0);
        var discussion = new Placement { LectureOf = second, Room = f.Rooms[1],
            Time = MeetingTime.Of("MW", 10, 0, 11, 0),
            Section = new SectionRequest { Title = "101 DISCUSSION", Instructor = "LEE D",
                SizeForRoom = 30, MinimumRoomCapacity = 30 } };
        f.Plan.Discussions.Add(discussion);
        var discussionTime = discussion.Time;
        var proposal = f.Planner.ProposeOverride(f.Plan, first, f.Rooms[1], MeetingTime.Of("MW", 9, 0, 11, 0));
        var change = proposal.ProposedChanges.Single(c => c.Placement == discussion);
        Assert.NotNull(change.Room); Assert.Equal(discussionTime, change.Time);
        f.Planner.ApplyOverride(f.Plan, proposal);
        Assert.All(f.Plan.Placements.Concat(f.Plan.Discussions), p => Assert.NotNull(p.Room));
        Assert.Equal(change.Room, discussion.Room); Assert.Equal(discussionTime, discussion.Time);
        foreach (var p in f.Plan.Placements.Concat(f.Plan.Discussions))
            Assert.DoesNotContain(f.Plan.Placements.Concat(f.Plan.Discussions),
                other => other != p && other.Room == p.Room && other.Time!.Overlaps(p.Time!));
    }

    [Fact]
    public void Existing_unassigned_section_does_not_block_rehousing_assigned_classes()
    {
        var f = Fixture(true); var unassigned = f.Plan.Placements[2]; unassigned.Room = null;
        var assigned = f.Plan.Placements.Where(p => p.Room != null).ToList();
        var proposal = f.Planner.ProposeOverride(f.Plan, assigned[0], f.Rooms[1], assigned[0].Time!);
        f.Planner.ApplyOverride(f.Plan, proposal);
        Assert.All(assigned, p => Assert.NotNull(p.Room)); Assert.Null(unassigned.Room);
    }

    [Fact]
    public void Invalid_replacement_room_at_apply_time_leaves_every_assignment_unchanged()
    {
        var f = Fixture(); var first = f.Plan.Placements[0]; var second = f.Plan.Placements[1];
        var before = f.Plan.Placements.Select(p => (p.Room, p.Time)).ToList();
        var proposal = f.Planner.ProposeOverride(f.Plan, first, f.Rooms[1], first.Time!);
        var replacement = proposal.ProposedChanges.Single(c => c.Placement == second).Room;
        replacement.Capacity = 10;
        Assert.Throws<ArgumentException>(() => f.Planner.ApplyOverride(f.Plan, proposal));
        Assert.Equal(before, f.Plan.Placements.Select(p => (p.Room, p.Time)).ToList());
        Assert.All(f.Plan.Placements, p => Assert.NotNull(p.Room));
    }
}
