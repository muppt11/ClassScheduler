using RoomScheduler;
using Xunit;

namespace RoomScheduler.Tests;

public partial class FileUploadTests
{
    private static byte[] BaselineClasses(string year = "2026", bool multiple = false) => Workbook(("Classes App", new[] {
        new[] { "Semester", "Year", "Title", "Professor", "Tenure or PTF", "Minimum Room Size Needed" },
        new[] { "Fall", year, "103F EXPLORING COMPUTERS", "SMITH A", "PTF", "30" },
        multiple ? new[] { "Fall", year, "103F EXPLORING COMPUTERS", "JONES B", "T", "30" } : new[] { "", "", "", "", "", "" }
    }));

    private static PlanningSession BaselineSession(IKeyValueStore? store = null, bool multiple = false)
    {
        var session = NewSession(store); session.Save("classes.xlsx", BaselineClasses(multiple: multiple), FileKind.Requests);
        session.Build("classes.xlsx"); return session;
    }

    [Fact]
    public void Chat_reviews_semester_baseline_then_saves_rebuilds_and_persists_every_section()
    {
        var store = new MemoryStore(); var session = BaselineSession(store, multiple: true); var old = session.LatestPlan;
        session.SendChat("Set 103F minimum seats to 40 and preferred seats to 60");
        Assert.Contains("Fall 2026, all 2 section(s)", LastChat(session));
        Assert.Contains("minimum 0 → 40", LastChat(session)); Assert.Contains("preferred 0 → 60", LastChat(session));
        Assert.Same(old, session.LatestPlan); Assert.Null(store.Get("baseline:Fall 2026:103F"));
        session.SendChat("Confirm course baseline");
        Assert.Contains("Saved baseline for 103F, Fall 2026", LastChat(session)); Assert.NotSame(old, session.LatestPlan);
        var reopened = NewSession(store).Build("classes.xlsx");
        Assert.All(reopened.Placements, p => {
            Assert.Equal(40, p.Section.MinimumRoomCapacity); Assert.Equal(60, p.Section.PreferredRoomCapacity);
            Assert.True(p.Room!.Capacity >= 40); Assert.Equal(reopened.Rules.RegistrarCap(p.Room.Capacity), p.RegistrarCap);
        });
    }

    [Fact]
    public void Updating_one_value_preserves_the_other_value()
    {
        var session = BaselineSession(); session.SetSemesterBaseline("Fall 2026", "103F", 40, 60); session.Build("classes.xlsx");
        session.SendChat("Set the minimum seats for 103F to 50.");
        Assert.Contains("minimum 40 → 50", LastChat(session)); Assert.Contains("preferred 60 → 60", LastChat(session));
        session.SendChat("yes");
        var section = Assert.Single(session.LatestPlan!.Placements).Section;
        Assert.Equal(50, section.MinimumRoomCapacity); Assert.Equal(60, section.PreferredRoomCapacity);
    }

    [Theory]
    [InlineData("Clear preferred for 103F", 40, 0)]
    [InlineData("Set 103F minimum seats to 0", 0, 60)]
    [InlineData("Clear the room baseline for 103F", 0, 0)]
    public void Chat_can_clear_individual_or_both_baseline_values(string request, int minimum, int preferred)
    {
        var session = BaselineSession(); session.SetSemesterBaseline("Fall 2026", "103F", 40, 60); session.Build("classes.xlsx");
        session.SendChat(request); Assert.Contains("Confirm course baseline", session.Conversation.Suggestions);
        session.SendChat("Confirm course baseline");
        var p = Assert.Single(session.LatestPlan!.Placements);
        Assert.Equal(minimum, p.Section.MinimumRoomCapacity); Assert.Equal(preferred, p.Section.PreferredRoomCapacity);
        Assert.True(p.Room!.Capacity >= p.Section.SizeForRoom);
    }

    [Fact]
    public void Missing_course_is_clarified_without_losing_the_requested_values()
    {
        var session = BaselineSession(); session.SendChat("Set minimum seats to 40 and preferred seats to 60");
        Assert.Contains("Which course", LastChat(session)); session.SendChat("103F");
        Assert.Contains("minimum 0 → 40", LastChat(session)); Assert.Contains("preferred 0 → 60", LastChat(session));
        session.SendChat("yes"); Assert.Equal(40, session.LatestPlan!.Placements[0].Section.MinimumRoomCapacity);
    }

    [Fact]
    public void Course_lookup_supports_a_pronoun_in_the_following_baseline_request()
    {
        var session = BaselineSession(); session.SendChat("where is 103F?");
        session.SendChat("Set its minimum seats to 40"); Assert.Contains("Proposed baseline for 103F", LastChat(session));
        session.SendChat("Confirm course baseline"); Assert.Equal(40, session.LatestPlan!.Placements[0].Section.MinimumRoomCapacity);
    }

    [Theory]
    [InlineData("Set 103F minimum seats to -1")]
    [InlineData("Set 103F preferred seats to 60.5")]
    [InlineData("Set 103F minimum seats to 99999999999999999999")]
    [InlineData("Set 103F minimum seats to 40 or 60")]
    [InlineData("Set 103F minimum seats to 40 and minimum seats to 60")]
    public void Invalid_or_ambiguous_counts_do_not_save_a_baseline(string request)
    {
        var store = new MemoryStore(); var session = BaselineSession(store); var old = session.LatestPlan;
        session.SendChat(request); Assert.DoesNotContain("Confirm course baseline", session.Conversation.Suggestions);
        Assert.Null(store.Get("baseline:Fall 2026:103F")); Assert.Same(old, session.LatestPlan);
    }

    [Fact]
    public void Preferred_below_minimum_asks_for_correction_then_accepts_a_followup()
    {
        var session = BaselineSession(); session.SendChat("Set 103F minimum seats to 80 and preferred seats to 60");
        Assert.Contains("below minimum 80", LastChat(session)); Assert.DoesNotContain("Confirm course baseline", session.Conversation.Suggestions);
        session.SendChat("preferred seats to 100"); Assert.Contains("minimum 0 → 80", LastChat(session));
        session.SendChat("Confirm course baseline"); Assert.Equal(100, session.LatestPlan!.Placements[0].Section.PreferredRoomCapacity);
    }

    [Fact]
    public void Baseline_questions_explain_values_without_rebuilding()
    {
        var session = BaselineSession(); var old = session.LatestPlan;
        session.SendChat("What is the difference between minimum and preferred?"); Assert.Contains("hard room-capacity floor", LastChat(session));
        session.SendChat("Show baseline for 103F"); Assert.Contains("minimum 0 seats; preferred 0 seats", LastChat(session));
        Assert.Same(old, session.LatestPlan);
    }

    [Fact]
    public void A_one_time_room_search_does_not_create_a_saved_baseline()
    {
        var store = new MemoryStore(); var session = BaselineSession(store);
        session.SendChat("Find 103F a room with at least 40 seats");
        Assert.NotNull(session.Conversation.Proposal); Assert.Null(store.Get("baseline:Fall 2026:103F"));
    }

    [Fact]
    public void Baseline_is_scoped_to_the_selected_semester_and_rejects_a_different_semester()
    {
        var store = new MemoryStore(); var session = BaselineSession(store);
        session.SendChat("Set 103F minimum seats to 40 for Fall 2027"); Assert.Contains("workbook is for Fall 2026", LastChat(session));
        Assert.Null(store.Get("baseline:Fall 2027:103F"));
        session.SendChat("Set 103F minimum seats to 40"); session.SendChat("Confirm course baseline");
        session.Save("next-year.xlsx", BaselineClasses("2027"), FileKind.Other);
        Assert.Equal(0, Assert.Single(session.Build("next-year.xlsx").Placements).Section.MinimumRoomCapacity);
    }

    [Fact]
    public void Unknown_course_is_not_replaced_by_the_previous_course()
    {
        var session = BaselineSession(); session.SendChat("Set 103F minimum seats to 40");
        session.SendChat("Set 999X minimum seats to 60"); Assert.Contains("couldn't match course 999X", LastChat(session));
        session.SendChat("Confirm course baseline"); Assert.Equal(0, session.LatestPlan!.Placements[0].Section.MinimumRoomCapacity);
    }

    [Fact]
    public void Changes_to_another_baseline_invalidate_the_reviewed_confirmation()
    {
        var session = BaselineSession(); session.SendChat("Set 103F minimum seats to 40"); var old = session.LatestPlan;
        session.SetSemesterBaseline("Fall 2026", "314", 50, 60); session.SendChat("Confirm course baseline");
        Assert.Contains("changed since the baseline review", LastChat(session)); Assert.Same(old, session.LatestPlan);
    }

    [Fact]
    public void Canceling_keeps_the_saved_values_and_schedule()
    {
        var store = new MemoryStore(); var session = BaselineSession(store); var old = session.LatestPlan;
        session.SendChat("Set 103F minimum seats to 40"); session.SendChat("Cancel baseline");
        Assert.Null(store.Get("baseline:Fall 2026:103F")); Assert.Same(old, session.LatestPlan);
    }

    [Fact]
    public void Failed_baseline_save_does_not_change_the_schedule_or_storage()
    {
        var store = new FailingStore(); var session = BaselineSession(store); var old = session.LatestPlan;
        session.SendChat("Set 103F minimum seats to 40"); store.FailNextKey = "baseline:Fall 2026:103F";
        session.SendChat("Confirm course baseline"); Assert.Contains("baseline was not saved", LastChat(session));
        Assert.Null(store.Inner.Get("baseline:Fall 2026:103F")); Assert.Same(old, session.LatestPlan);
    }

    [Fact]
    public void A_failed_rebuild_reports_that_the_baseline_was_saved_but_the_old_schedule_remains()
    {
        var store = new FailingStore(); var session = BaselineSession(store); var old = session.LatestPlan;
        session.SendChat("Set 103F minimum seats to 40"); store.FailNextKey = "faculty";
        session.SendChat("Confirm course baseline");
        Assert.Contains("baseline was saved, but rebuilding failed", LastChat(session));
        Assert.NotNull(store.Inner.Get("baseline:Fall 2026:103F")); Assert.Same(old, session.LatestPlan); Assert.True(session.RequiresRebuild);
    }
    [Fact]
    public void Baseline_query_retains_its_read_only_intent_through_course_clarification()
    {
        var session = BaselineSession(); var old = session.LatestPlan;
        session.SendChat("Show course baseline"); Assert.Contains("Which course", LastChat(session));
        session.SendChat("103F"); Assert.Contains("minimum 0 seats; preferred 0 seats", LastChat(session));
        Assert.DoesNotContain("Confirm course baseline", session.Conversation.Suggestions); Assert.Same(old, session.LatestPlan);
    }

    [Theory]
    [InlineData("Save for semester and rebuild")]
    [InlineData("Save it and rebuild")]
    public void The_baseline_editors_save_wording_confirms_a_reviewed_chat_request(string confirmation)
    {
        var session = BaselineSession(); session.SendChat("Set 103F minimum seats to 40");
        session.SendChat(confirmation); Assert.Contains("Saved baseline for 103F", LastChat(session));
        Assert.Equal(40, session.LatestPlan!.Placements[0].Section.MinimumRoomCapacity);
    }

}
