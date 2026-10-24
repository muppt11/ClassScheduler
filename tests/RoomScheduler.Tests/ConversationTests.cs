using RoomScheduler;
using Xunit;

namespace RoomScheduler.Tests;

public class ConversationTests
{
    private static (SchedulingConversation Chat, Planner Planner, Planner.Plan Plan, List<Room> Rooms) Fixture(bool duplicate = false)
    {
        var sheet = new Xlsx.Sheet {Name = "Classes App"};
        sheet.Rows.AddRange(new[] {
            new List<string> {"Title", "Professor", "Minimum Room Size Needed"},
            new List<string> {"429 COMPUTER ARCHITECTURE", "SMITH A", "30"},
            new List<string> {"314 DATA STRUCTURES", "JONES B", "30"}
        });
        if (duplicate) sheet.Rows.Add(new() {"429 COMPUTER ARCHITECTURE", "DOE M", "30"});
        var rooms = new List<Room> {
            new() {Building = "GDC", RoomNumber = "2.216", Capacity = 200, CsOwnsFrom = TimeSpan.FromHours(12.5)},
            new() {Building = "GDC", RoomNumber = "1.100", Capacity = 40},
            new() {Building = "RLP", RoomNumber = "1.104", Capacity = 40}
        };
        var planner = new Planner(rooms, new Campus(), PastSchedule.Empty, FacultyStore.InMemory());
        var plan = planner.Build(DepartmentWorkbook.FromSheets(new() {sheet}));
        // Stable initial times for conversation-focused tests.
        for (int i = 0; i < plan.Placements.Count; i++)
        {
            plan.Placements[i].Time = MeetingTime.Of("MWF", 9, 0, 10, 0);
            plan.Placements[i].Room = rooms[(i + 1) % rooms.Count];
        }
        return (new(), planner, plan, rooms);
    }
    private static void Send((SchedulingConversation Chat, Planner Planner, Planner.Plan Plan, List<Room> Rooms) f, string text, bool allow = false) =>
        f.Chat.Send(f.Plan, f.Rooms, (s, b) => f.Planner.ProposeOverride(f.Plan, s, b), text, allow);
    private static string Last(SchedulingConversation chat) => chat.Messages.Last().Text;

    [Fact]
    public void Screenshot_request_resolves_nickname_and_asks_which_reserved_time_without_losing_context()
    {
        var f = Fixture();
        Send(f, "ok I need comp arch to be moved to the reserved cs time");
        Assert.Contains("429 COMPUTER ARCHITECTURE", Last(f.Chat));
        Assert.Contains("Which do you mean", Last(f.Chat));
        Assert.Equal(2, f.Chat.Suggestions.Count);
        Assert.Null(f.Chat.Proposal);
        Send(f, "can you do that for me");
        Assert.Contains("Which do you mean", Last(f.Chat));
        Assert.DoesNotContain("Use: Put", Last(f.Chat));
        Send(f, f.Chat.Suggestions.First());
        Assert.True(f.Chat.Proposal != null, Last(f.Chat));
        Assert.Contains(f.Chat.Proposal!.ProposedChanges, c => c.Placement.Section.Course == "429" && c.Time == f.Plan.Rules.GraduateSlot);
        Assert.Equal(MeetingTime.Of("MWF", 9, 0, 10, 0), f.Plan.Placements[0].Time);
    }

    [Fact]
    public void Plain_language_time_change_keeps_room_and_duration_and_waits_for_explicit_apply()
    {
        var f = Fixture(); var p = f.Plan.Placements[0]; var room = p.Room;
        Send(f, "could you move comp arch to Tuesday and Thursday at 2pm please");
        var change = Assert.Single(f.Chat.Proposal!.ProposedChanges);
        Assert.Equal(room, change.Room);
        Assert.Equal(MeetingTime.Of("TTH", 14, 0, 15, 0), change.Time);
        var proposal = f.Chat.Proposal;
        Send(f, "yes please");
        Assert.Same(proposal, f.Chat.Proposal);
        Assert.Contains("Apply reviewed changes", Last(f.Chat));
        Assert.Equal(MeetingTime.Of("MWF", 9, 0, 10, 0), p.Time);
    }

    [Fact]
    public void Ambiguous_section_selection_preserves_the_original_time_request()
    {
        var f = Fixture(true);
        Send(f, "move comp arch to Tuesday at 2pm");
        Assert.Null(f.Chat.Proposal); Assert.Equal(2, f.Chat.Suggestions.Count);
        Send(f, f.Chat.Suggestions.Last());
        Assert.True(f.Chat.Proposal != null, Last(f.Chat));
        Assert.Contains(f.Chat.Proposal!.ProposedChanges, c => c.Placement.Section.Surname == "DOE" && c.Time == MeetingTime.Of("T", 14, 0, 15, 0));
    }

    [Fact]
    public void Gdc_hours_are_distinct_from_graduate_slot_and_find_slot_is_a_followup()
    {
        var f = Fixture();
        Send(f, "move comp arch to reserved CS time");
        Send(f, f.Chat.Suggestions.Last());
        Assert.Null(f.Chat.Proposal);
        Assert.Contains("Which days", Last(f.Chat));
        Send(f, "find a free slot", true);
        Assert.True(f.Chat.Proposal != null, Last(f.Chat));
        var change = f.Chat.Proposal!.ProposedChanges.First(c => c.Placement.Section.Course == "429");
        Assert.True(change.Time.Start >= f.Plan.Rules.CsOwnsGdcFrom);
        Assert.NotNull(change.Room.CsOwnsFrom);
    }

    [Fact]
    public void Explicit_oversized_room_exception_can_be_confirmed_in_conversation()
    {
        var f = Fixture();
        Send(f, "put comp arch in GDC 2.216 Tuesday at 2pm");
        Assert.Null(f.Chat.Proposal);
        Assert.Contains("larger", Last(f.Chat));
        Send(f, "yes, allow the larger room");
        Assert.True(f.Chat.Proposal != null, Last(f.Chat));
        Assert.Contains("exception explicitly allowed", f.Chat.Proposal!.Explanation);
    }

    [Fact]
    public void Queries_use_actual_schedule_and_do_not_discard_pending_proposals()
    {
        var f = Fixture();
        Send(f, "move comp arch to Tuesday at 2pm");
        var proposal = f.Chat.Proposal;
        Send(f, "why did comp arch get that room?");
        Assert.Contains("Expected size", Last(f.Chat));
        Assert.Same(proposal, f.Chat.Proposal);
        Send(f, "show review summary");
        Assert.Contains("sections scheduled", Last(f.Chat));
        Assert.DoesNotContain("I understand:", Last(f.Chat));
        Send(f, "where is data structures?");
        Assert.Contains("314 DATA STRUCTURES", Last(f.Chat));
        Assert.Contains("MWF", Last(f.Chat));
    }

    [Fact]
    public void Invalid_room_and_hard_constraints_produce_specific_explanations_without_changes()
    {
        var f = Fixture();
        Send(f, "move comp arch to XYZ 9.999 Tuesday at 2pm");
        Assert.Null(f.Chat.Proposal); Assert.Contains("couldn't find XYZ 9.999", Last(f.Chat));
        f.Plan.Placements[0].Section.Track = FacultyTrack.Tenure;
        Send(f, "move comp arch to GDC 1.100 Monday at 12pm");
        Assert.Null(f.Chat.Proposal); Assert.Contains("faculty lunch", Last(f.Chat));
        Assert.Equal(MeetingTime.Of("MWF", 9, 0, 10, 0), f.Plan.Placements[0].Time);
    }

    [Fact]
    public void Rebuild_clears_stale_context_and_preview()
    {
        var f = Fixture(); Send(f, "move comp arch to Tuesday at 2pm");
        Assert.True(f.Chat.Proposal != null, Last(f.Chat));
        var replacement = Fixture();
        f.Chat.Sync(replacement.Plan);
        Assert.Null(f.Chat.Proposal);
        Assert.Contains("rebuilt", Last(f.Chat));
    }

    [Fact]
    public void Multiple_requested_times_get_a_clarification_instead_of_a_guessed_time()
    {
        var f = Fixture(); Send(f, "move comp arch to Tuesday at 2pm or 4pm");
        Assert.Null(f.Chat.Proposal);
        Assert.Contains("clear meeting time", Last(f.Chat));
    }

    [Fact]
    public void Cancel_discards_request_without_changing_assignments()
    {
        var f = Fixture(); Send(f, "move comp arch to Tuesday at 2pm"); Send(f, "cancel");
        Assert.Null(f.Chat.Proposal);
        Assert.Equal(MeetingTime.Of("MWF", 9, 0, 10, 0), f.Plan.Placements[0].Time);
    }
    [Fact]
    public void Policy_and_room_availability_queries_are_grounded_in_the_plan()
    {
        var f = Fixture();
        Send(f, "what is the reserved CS time?");
        Assert.Contains(f.Plan.Rules.GraduateSlot.ToString(), Last(f.Chat));
        Assert.Null(f.Chat.Proposal);
        Send(f, "is GDC 1.100 free Tuesday 2pm-3pm?");
        Assert.Contains("No classes in the current plan", Last(f.Chat));
        Send(f, "is GDC 1.100 free Monday 9am-10am?");
        Assert.Contains("occupied", Last(f.Chat));
        Assert.Contains("429", Last(f.Chat));
    }

    [Fact]
    public void Incomplete_new_change_invalidates_the_previous_preview_and_asks_for_details()
    {
        var f = Fixture(); Send(f, "move comp arch to Tuesday at 2pm");
        Assert.NotNull(f.Chat.Proposal);
        Send(f, "move comp arch to XYZ 9.999");
        Assert.Null(f.Chat.Proposal);
        Assert.Contains("couldn't find", Last(f.Chat));
        Send(f, "cancel");
        Send(f, "move comp arch later");
        Assert.Null(f.Chat.Proposal);
        Assert.Contains("What days/time or room", Last(f.Chat));
    }

    [Fact]
    public void Pronouns_work_after_a_course_lookup_and_queries_do_not_change_assignments()
    {
        var f = Fixture();
        Send(f, "where is comp arch?");
        Assert.Contains("429", Last(f.Chat));
        Send(f, "can you move it to Thursday at 3pm?");
        Assert.NotNull(f.Chat.Proposal);
        Assert.Contains(f.Chat.Proposal!.ProposedChanges, c => c.Placement.Section.Course == "429" && c.Time.Pattern == "TH");
        Assert.Equal(MeetingTime.Of("MWF", 9, 0, 10, 0), f.Plan.Placements[0].Time);
    }

    [Fact]
    public void Applied_move_retains_the_course_reference_for_a_later_pronoun_request()
    {
        var f = Fixture(); Send(f, "move comp arch to Tuesday at 2pm");
        f.Planner.ApplyOverride(f.Plan, f.Chat.Proposal!); f.Chat.Applied();
        Send(f, "move it to Thursday at 3pm");
        Assert.NotNull(f.Chat.Proposal);
        Assert.Contains(f.Chat.Proposal!.ProposedChanges, c => c.Placement.Section.Course == "429" && c.Time == MeetingTime.Of("TH", 15, 0, 16, 0));
    }

    [Fact]
    public void Oversized_checkbox_consent_survives_clarification_but_not_a_completed_request()
    {
        var f = Fixture();
        Send(f, "move comp arch to reserved CS time in GDC 2.216", true);
        Assert.Null(f.Chat.Proposal);
        Send(f, f.Chat.Suggestions.First());
        Assert.NotNull(f.Chat.Proposal);
        f.Planner.ApplyOverride(f.Plan, f.Chat.Proposal!); f.Chat.Applied();
        Send(f, "move data structures to GDC 2.216 Friday at 2pm");
        Assert.Null(f.Chat.Proposal);
        Assert.Contains("oversized-room exception", Last(f.Chat));
    }

}
