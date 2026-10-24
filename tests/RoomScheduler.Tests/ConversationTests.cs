using RoomScheduler;
using Xunit;

namespace RoomScheduler.Tests;

public class ConversationTests
{
    private static (SchedulingConversation Chat, Planner Planner, Planner.Plan Plan, List<Room> Rooms) Fixture(bool duplicate = false, bool abbreviated = false)
    {
        var sheet = new Xlsx.Sheet {Name = "Classes App"};
        sheet.Rows.AddRange(new[] {
            new List<string> {"Title", "Professor", "Minimum Room Size Needed"},
            new List<string> {abbreviated ? "429 COMP ORGANIZATN AND ARCH" : "429 COMPUTER ARCHITECTURE", "SMITH A", "30"},
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
        f.Chat.Send(f.Plan, f.Rooms, (s, b) => f.Planner.ProposeOverride(f.Plan, s, b), text, allow,
            (target, room, time, b) => f.Planner.ProposeOverride(f.Plan, target, room, time, b),
            (first, second, times, b) => f.Planner.ProposeSwap(f.Plan, first, second, times, b));
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
        Assert.NotNull(f.Chat.Proposal);
        Assert.Contains(f.Chat.Proposal!.ProposedChanges, c => c.Placement == f.Plan.Placements[0] && c.Time.Start > TimeSpan.FromHours(9));
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

    [Fact]
    public void Searches_respect_building_days_and_start_end_bounds_instead_of_guessing_exact_time()
    {
        var f = Fixture();
        Send(f, "find a slot for comp arch in GDC Tuesdays and Thursdays after 2pm before 5pm");
        Assert.NotNull(f.Chat.Proposal);
        var c = f.Chat.Proposal!.ProposedChanges.Single(c => c.Placement == f.Plan.Placements[0]);
        Assert.Equal("GDC", c.Room.Building); Assert.Equal("TTH", c.Time.Pattern);
        Assert.True(c.Time.Start >= TimeSpan.FromHours(14)); Assert.True(c.Time.End <= TimeSpan.FromHours(17));
        Assert.Contains("Search conditions", Last(f.Chat));
    }

    [Fact]
    public void Followup_refines_window_and_excludes_friday_without_losing_course_or_building()
    {
        var f = Fixture(); Send(f, "find a slot for comp arch in RLP after 2pm");
        Send(f, "before 5pm, no Fridays");
        Assert.NotNull(f.Chat.Proposal);
        foreach (var c in f.Chat.Proposal!.ProposedChanges.Where(c => c.Placement.Section.Course == "429"))
        {
            Assert.Equal("RLP", c.Room.Building); Assert.DoesNotContain(DayOfWeek.Friday, c.Time.Days);
            Assert.True(c.Time.Start >= TimeSpan.FromHours(14)); Assert.True(c.Time.End <= TimeSpan.FromHours(17));
        }
    }

    [Fact]
    public void Smaller_room_request_can_keep_time_and_never_changes_the_plan_during_search()
    {
        var f = Fixture(); var p = f.Plan.Placements[0]; var before = p.Room; var time = p.Time;
        f.Rooms.Add(new Room {Building="GDC", RoomNumber="1.101", Capacity=35});
        // Planner owns the supplied room list, so this newly configured room is eligible.
        Send(f, "find comp arch a smaller room and keep the same time");
        Assert.NotNull(f.Chat.Proposal);
        var c = f.Chat.Proposal!.ProposedChanges.Single(c => c.Placement == p);
        Assert.True(c.Room.Capacity < before!.Capacity); Assert.Equal(time, c.Time);
        Assert.Same(before, p.Room); Assert.Equal(time, p.Time);
    }

    [Fact]
    public void Natural_ordinal_and_other_options_choose_distinct_checked_previews()
    {
        var f = Fixture(); Send(f, "find a free slot for comp arch after 2pm");
        var first = f.Chat.Proposal; Assert.NotNull(first);
        Send(f, "the second one"); var second = f.Chat.Proposal;
        Assert.NotSame(first, second); Assert.NotNull(second);
        Send(f, "something else"); Assert.NotSame(second, f.Chat.Proposal);
        Send(f, "what would change?"); Assert.Contains("→", Last(f.Chat));
        Assert.Contains("Nothing has changed", Last(f.Chat));
    }

    [Fact]
    public void Contradictory_window_is_explained_and_can_be_reset_without_relaxing_it_silently()
    {
        var f = Fixture(); Send(f, "find a free slot for comp arch after 4pm before 2pm");
        Assert.Null(f.Chat.Proposal); Assert.Contains("window is empty", Last(f.Chat));
        Send(f, "any time"); Assert.NotNull(f.Chat.Proposal);
    }

    [Fact]
    public void Room_availability_question_remembers_room_across_followup_and_includes_discussions()
    {
        var f = Fixture();
        f.Plan.Discussions.Add(new Placement {Section=f.Plan.Placements[1].Section, Room=f.Rooms[1], Time=MeetingTime.Of("TTH",14,0,15,0)});
        Send(f, "is GDC 1.100 available?"); Assert.Contains("What days", Last(f.Chat));
        Send(f, "TTH 2pm-3pm"); Assert.Contains("occupied", Last(f.Chat)); Assert.Contains("314", Last(f.Chat));
        Assert.Null(f.Chat.Proposal);
    }

    [Fact]
    public void Without_moving_other_sections_is_honored_when_finding_a_different_room()
    {
        var f = Fixture();
        f.Rooms.Add(new Room {Building="RLP", RoomNumber="1.105", Capacity=40});
        Send(f, "find another room for comp arch at the same time without moving other classes");
        var c = Assert.Single(f.Chat.Proposal!.ProposedChanges);
        Assert.Equal("1.105", c.Room.RoomNumber); Assert.Equal(f.Plan.Placements[0].Time, c.Time);
    }

    [Fact]
    public void Hard_time_constraints_remain_in_force_for_every_alternative_and_apply_is_conflict_free()
    {
        var f = Fixture(); var target = f.Plan.Placements[0]; target.Section.Track=FacultyTrack.Tenure;
        Send(f, "find a slot for comp arch Monday after 11am before 6pm");
        Assert.NotNull(f.Chat.Proposal);
        var choices = f.Chat.Suggestions.Where(s=>s.StartsWith("Option ")).ToList();
        foreach (var choice in choices)
        {
            Send(f, choice);
            Assert.All(f.Chat.Proposal!.ProposedChanges, c => Assert.False(c.Time.Overlaps(f.Plan.Rules.TenureLunch)));
        }
        f.Planner.ApplyOverride(f.Plan, f.Chat.Proposal!);
        foreach(var a in f.Plan.Placements.Where(p=>p.Room!=null && p.Time!=null))
            Assert.DoesNotContain(f.Plan.Placements, b=>a!=b && a.Room==b.Room && b.Time?.Overlaps(a.Time!)==true);
    }

    [Fact]
    public void New_invalid_request_clears_old_option_choices()
    {
        var f = Fixture(); Send(f, "find a free slot for comp arch"); Assert.NotNull(f.Chat.Proposal);
        Send(f, "actually use XYZ 9.999"); Assert.Null(f.Chat.Proposal);
        Send(f, "option 1"); Assert.Null(f.Chat.Proposal); Assert.Contains("no current options", Last(f.Chat));
    }

    [Fact]
    public void Full_course_word_matches_an_abbreviated_workbook_title_and_compact_room_name()
    {
        var f = Fixture(abbreviated: true);
        Send(f, "move computer architecture to GDC1.100 Tuesday at 2pm");
        Assert.NotNull(f.Chat.Proposal); Assert.Contains(f.Chat.Proposal!.ProposedChanges, c=>c.Placement.Section.Course=="429");
    }

    [Fact]
    public void Course_lookup_changes_the_reference_for_a_later_pronoun_without_applying_old_preview()
    {
        var f = Fixture(); Send(f, "move comp arch to Tuesday at 2pm");
        Send(f, "where is data structures?"); Send(f, "move it to Thursday at 3pm");
        Assert.NotNull(f.Chat.Proposal); Assert.Contains(f.Chat.Proposal!.ProposedChanges, c=>c.Placement.Section.Course=="314");
        Assert.Equal(MeetingTime.Of("MWF",9,0,10,0), f.Plan.Placements[0].Time);
    }

    [Fact]
    public void Ambiguous_time_bounds_ask_for_meridiem_instead_of_ignoring_the_requested_window()
    {
        var f=Fixture(); Send(f,"find a slot for comp arch after 2");
        Assert.Null(f.Chat.Proposal); Assert.Contains("am or pm",Last(f.Chat));
    }

    [Fact]
    public void Bare_course_number_is_not_mistaken_for_an_option_number()
    {
        var f=Fixture(); Send(f,"429"); Assert.Contains("429 COMPUTER ARCHITECTURE",Last(f.Chat));
        Send(f,"move it later"); Assert.NotNull(f.Chat.Proposal);
    }

    [Fact]
    public void Free_room_queries_can_clarify_the_interval_without_creating_a_move()
    {
        var f=Fixture(); Send(f,"which rooms are free?"); Assert.Contains("start/end",Last(f.Chat));
        Send(f,"Tuesday and Thursday 2pm-3pm"); Assert.Contains("3 active room(s)",Last(f.Chat));
        Assert.Contains("not campus-wide",Last(f.Chat)); Assert.Null(f.Chat.Proposal);
    }

    [Fact]
    public void Excluded_building_and_minimum_seat_count_are_respected()
    {
        var f=Fixture(); f.Rooms.Add(new Room {Building="RLP",RoomNumber="1.105",Capacity=45});
        Send(f,"find a room for comp arch outside GDC with at least 45 seats at the same time");
        var c=Assert.Single(f.Chat.Proposal!.ProposedChanges);
        Assert.Equal("RLP",c.Room.Building); Assert.True(c.Room.Capacity>=45);
        Assert.Equal(f.Plan.Placements[0].Time,c.Time);
    }

    [Fact]
    public void Review_summary_gives_actual_priorities_not_only_counts()
    {
        var f=Fixture(); Send(f,"what needs attention?");
        Assert.Contains("Start here",Last(f.Chat)); Assert.Contains("row 2",Last(f.Chat));
        Assert.Null(f.Chat.Proposal);
    }

    [Fact]
    public void Same_room_constraint_applies_to_each_suggested_time()
    {
        var f=Fixture(); Send(f,"find comp arch a free slot after 2pm in the same room");
        var choices=f.Chat.Suggestions.Where(s=>s.StartsWith("Option ")).ToList(); Assert.NotEmpty(choices);
        foreach(var choice in choices)
        {
            Send(f,choice);
            var c=f.Chat.Proposal!.ProposedChanges.Single(c=>c.Placement.Section.Course=="429");
            Assert.Equal(f.Plan.Placements[0].Room,c.Room); Assert.True(c.Time.Start>=TimeSpan.FromHours(14));
        }
    }

    [Fact]
    public void Comparing_options_preserves_the_selected_preview_and_lists_affected_sections()
    {
        var f=Fixture(); Send(f,"find a slot for comp arch after 2pm"); var selected=f.Chat.Proposal;
        Send(f,"compare the options"); Assert.Same(selected,f.Chat.Proposal);
        Assert.Contains("Option 1:",Last(f.Chat)); Assert.Contains("Option 2:",Last(f.Chat));
        Assert.Contains("→",Last(f.Chat)); Assert.Contains("Nothing has changed",Last(f.Chat));
    }

    [Fact]
    public void Room_question_with_incomplete_time_keeps_asking_instead_of_creating_a_move()
    {
        var f=Fixture(abbreviated: true); Send(f,"move comp arch to Tuesday at 2pm"); var selected=f.Chat.Proposal;
        Send(f,"is GDC 1.100 free?"); Send(f,"Thursday at 2pm");
        Assert.Contains("full start/end time",Last(f.Chat)); Assert.Same(selected,f.Chat.Proposal);
        Send(f,"Tuesday and Thursday 2pm-3pm"); Assert.Contains("No classes",Last(f.Chat)); Assert.Same(selected,f.Chat.Proposal);
    }

    [Fact]
    public void An_exact_request_matching_the_current_assignment_does_not_move_it_elsewhere()
    {
        var f=Fixture(); Send(f,"put comp arch in GDC 1.100 MWF 9am-10am");
        Assert.Null(f.Chat.Proposal); Assert.Contains("already",Last(f.Chat));
    }

    [Fact]
    public void Between_window_and_list_of_excluded_weekdays_are_understood_together()
    {
        var f=Fixture(); Send(f,"find a slot for comp arch between 2pm and 5pm, no Mondays or Fridays");
        Assert.NotNull(f.Chat.Proposal);
        var c=f.Chat.Proposal!.ProposedChanges.Single(c=>c.Placement.Section.Course=="429");
        Assert.DoesNotContain(DayOfWeek.Monday,c.Time.Days); Assert.DoesNotContain(DayOfWeek.Friday,c.Time.Days);
        Assert.True(c.Time.Start>=TimeSpan.FromHours(14)); Assert.True(c.Time.End<=TimeSpan.FromHours(17));
    }

    [Fact]
    public void Keeping_current_time_and_requesting_a_conflicting_window_is_not_silently_relaxed()
    {
        var f=Fixture(); Send(f,"find a room for comp arch at the same time after 2pm");
        Assert.Null(f.Chat.Proposal); Assert.Contains("keeping the current meeting time",Last(f.Chat));
        Assert.Contains("schedule is unchanged",Last(f.Chat));
    }

    [Fact]
    public void Repeating_an_ambiguous_course_name_does_not_reuse_a_previous_section()
    {
        var f=Fixture(true); Send(f,"move comp arch to Tuesday at 2pm"); Send(f,"row 2"); Assert.NotNull(f.Chat.Proposal);
        Send(f,"move comp arch to Thursday at 3pm");
        Assert.Null(f.Chat.Proposal); Assert.Contains("Which one",Last(f.Chat));
    }

    [Fact]
    public void Pronoun_after_an_ambiguous_lookup_asks_for_a_section_instead_of_using_an_older_course()
    {
        var f=Fixture(true); Send(f,"where is data structures?"); Send(f,"where is comp arch?");
        Send(f,"move it to Tuesday at 2pm");
        Assert.Null(f.Chat.Proposal); Assert.Contains("Which one",Last(f.Chat));
    }

    [Fact]
    public void Unknown_named_course_never_moves_the_previous_course()
    {
        var f=Fixture(); Send(f,"move comp arch to Tuesday at 2pm");
        Send(f,"move basket weaving to Thursday at 3pm");
        Assert.Null(f.Chat.Proposal); Assert.Contains("couldn't match",Last(f.Chat));
    }

    [Theory]
    [InlineData("MTWTH", "MTWTH")]
    [InlineData("MWTH", "MWTH")]
    [InlineData("MF", "MF")]
    public void Explicit_catalog_day_patterns_are_preserved(string days,string expected)
    {
        var f=Fixture(); Send(f,$"move comp arch to {days} at 2pm");
        Assert.NotNull(f.Chat.Proposal);
        Assert.Equal(expected,f.Chat.Proposal!.ProposedChanges.Single(c=>c.Placement.Section.Course=="429").Time.Pattern);
    }

    [Fact]
    public void Multiple_time_ranges_are_not_silently_reduced_to_the_first_one()
    {
        var f=Fixture(); Send(f,"move comp arch to Tuesday 2pm-3pm or 4pm-5pm");
        Assert.Null(f.Chat.Proposal); Assert.Contains("clear meeting time",Last(f.Chat));
    }

    [Fact]
    public void No_later_than_sets_an_end_bound_without_requiring_a_later_start()
    {
        var f=Fixture(); Send(f,"find comp arch a slot no later than 9am");
        Assert.NotNull(f.Chat.Proposal);
        var c=f.Chat.Proposal!.ProposedChanges.Single(c=>c.Placement.Section.Course=="429");
        Assert.True(c.Time.End<=TimeSpan.FromHours(9));
    }

    [Fact]
    public void Reversed_weekday_range_is_rejected_even_with_a_search_window()
    {
        var f=Fixture(); Send(f,"find comp arch a slot Friday through Monday after 2pm");
        Assert.Null(f.Chat.Proposal); Assert.Contains("forward",Last(f.Chat));
    }

    [Fact]
    public void Oversized_source_row_number_is_a_clarification_not_an_overflow()
    {
        var f=Fixture(true); Send(f,"move comp arch to Tuesday at 2pm");
        Send(f,"row 9999999999999999999999999");
        Assert.Null(f.Chat.Proposal); Assert.Contains("Which one",Last(f.Chat));
    }

}
