using RoomScheduler;
using Xunit;

namespace RoomScheduler.Tests;

public partial class SchedulingTests
{
    private static List<Room> PriorityRooms() => new() {
        new() { Building = "GDC", RoomNumber = "1.100", Capacity = 40 },
        new() { Building = "RLP", RoomNumber = "1.100", Capacity = 40 }
    };

    [Fact]
    public void Building_order_prefers_GDC_and_can_be_changed_to_RLP_first()
    {
        var planner = Planner(PriorityRooms());
        var first = Assert.Single(planner.Build(Requests()).Placements);
        Assert.Equal("GDC", first.Room!.Building); Assert.Contains(first.Why, why => why.Contains("building priority: GDC"));
        planner.Rules.BuildingPriority = new() { Buildings = new() { "RLP", "GDC" } };
        Assert.Equal("RLP", Assert.Single(planner.Build(Requests()).Placements).Room!.Building);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Building_preference_never_overrides_offline_or_capacity_requirements(bool offline)
    {
        var rooms = PriorityRooms(); var planner = Planner(rooms);
        if (offline) planner.Rules.Offline.SetBuilding("GDC", true, rooms); else rooms[0].Capacity = 20;
        var placement = Assert.Single(planner.Build(Requests()).Placements);
        Assert.Equal("RLP", placement.Room!.Building);
    }

    [Fact]
    public void Full_GDC_room_falls_back_to_another_building_without_stranding_a_class()
    {
        var planner = Planner(PriorityRooms()); planner.Rules.AllowAlternateTimes = false;
        var wb = Requests();
        wb.Sections.Add(new() { SourceRow = 2, Title = "315 OTHER", Instructor = "JONES B", ProjectedSize = 30 });
        wb.Forms.Add(new() { Professor = "JONES B", Course = "315 OTHER", Time1 = "MWF 10am-11am" });
        var plan = planner.Build(wb);
        Assert.All(plan.Placements, p => Assert.NotNull(p.Room));
        Assert.Single(plan.Placements, p => p.Room!.Building == "GDC");
        Assert.Single(plan.Placements, p => p.Room!.Building == "RLP"); AssertNoConflicts(plan);
    }

    [Fact]
    public void Flexible_courses_prefer_the_configured_window()
    {
        var planner = Planner(PriorityRooms());
        var placement = Assert.Single(planner.Build(Requests(preferences: false)).Placements);
        Assert.True(placement.Time!.Start >= new TimeSpan(9, 30, 0));
        Assert.True(placement.Time.End <= TimeSpan.FromHours(17));
    }

    [Theory]
    [InlineData("MWF 8am-9am", 8)]
    [InlineData("MWF 6pm-7pm", 18)]
    public void Explicit_time_requests_remain_valid_outside_the_preferred_window(string requested, int hour)
    {
        var wb = Requests(); wb.Forms[0] = new() { Professor = "SMITH A", Course = "314 DATA STRUCTURES", Time1 = requested };
        var placement = Assert.Single(Planner(PriorityRooms()).Build(wb).Placements);
        Assert.Equal(TimeSpan.FromHours(hour), placement.Time!.Start); Assert.NotNull(placement.Room);
    }

    [Theory]
    [InlineData("MWF 2pm-3pm", "1.101")]
    [InlineData("MWF 10am-11am", "1.100")]
    public void CS_control_is_preferred_only_during_recorded_control_hours(string requested, string roomNumber)
    {
        var rooms = PriorityRooms(); rooms.RemoveAt(1);
        rooms.Add(new() { Building = "GDC", RoomNumber = "1.101", Capacity = 40, CsOwnsFrom = new TimeSpan(12, 30, 0) });
        var wb = Requests(); wb.Forms[0] = new() { Professor = "SMITH A", Course = "314 DATA STRUCTURES", Time1 = requested };
        var placed = Assert.Single(Planner(rooms).Build(wb).Placements);
        Assert.Equal(roomNumber, placed.Room!.RoomNumber);
    }

    [Fact]
    public void Faculty_building_history_can_come_from_a_different_course()
    {
        var history = RoomScheduler.PastSchedule.Empty;
        history.Sections.Add(new() { SemesterCode = 20259, Semester = "Fall 2025", Course = "429", Title = "ARCHITECTURE",
            Instructors = new() { "SMITH" }, Building = "GDC", Room = "1.100", Enrollment = 30 });
        var planner = Planner(PriorityRooms(), history);
        var placement = Assert.Single(planner.Build(Requests()).Placements);
        Assert.Contains(placement.Why, why => why.Contains("SMITH taught in GDC in Fall 2025"));
        planner.Rules.BuildingPriority = planner.Rules.BuildingPriority with { UseFacultyHistory = false };
        Assert.DoesNotContain(Assert.Single(planner.Build(Requests()).Placements).Why, why => why.Contains("SMITH taught in GDC"));
    }

    [Fact]
    public void Building_priority_does_not_make_pod_rooms_eligible_for_ordinary_classes()
    {
        var rooms = PriorityRooms(); rooms[0].DepartmentOnly = true; rooms[0].Notes = "pod room";
        Assert.Equal("RLP", Assert.Single(Planner(rooms).Build(Requests()).Placements).Room!.Building);
    }
}

public partial class FileUploadTests
{
    [Fact]
    public void Building_priorities_persist_and_require_rebuilding()
    {
        var store = new MemoryStore(); var session = NewSession(store);
        session.Save("classes.xlsx", RevisedClasses("PTF"), FileKind.Other); session.Build("classes.xlsx");
        session.SetBuildingPriorities(new() { Buildings = new() { "rlp", "gdc" }, Start = TimeSpan.FromHours(10), End = TimeSpan.FromHours(16) });
        Assert.True(session.RequiresRebuild);
        var reopened = NewSession(store);
        Assert.Equal(new[] { "RLP", "GDC" }, reopened.Rules.BuildingPriority.Buildings);
        Assert.Equal(TimeSpan.FromHours(10), reopened.Rules.BuildingPriority.Start);
    }

    [Fact]
    public void Invalid_or_failed_priority_saves_preserve_the_current_settings()
    {
        var store = new FailingStore(); var session = NewSession(store); var before = session.Rules.BuildingPriority;
        Assert.Throws<ArgumentException>(() => session.SetBuildingPriorities(new() { Buildings = new() { "XYZ" } }));
        Assert.Throws<ArgumentException>(() => session.SetBuildingPriorities(new() { Start = TimeSpan.FromHours(17), End = TimeSpan.FromHours(9) }));
        store.FailNextKey = "building-priorities";
        Assert.Throws<InvalidOperationException>(() => session.SetBuildingPriorities(new() { Buildings = new() { "RLP" } }));
        Assert.Same(before, session.Rules.BuildingPriority); Assert.Null(store.Inner.Get("building-priorities"));
    }

    [Fact]
    public void Doug_style_request_is_reviewed_then_saved_and_rebuilt_on_confirmation()
    {
        var session = NewSession(); session.Save("classes.xlsx", RevisedClasses("PTF"), FileKind.Other);
        var old = session.Build("classes.xlsx");
        session.SendChat("Fill up rooms in GDC as much as possible between 9:30 and 5 PM");
        Assert.Contains("Proposed scheduling priorities", LastChat(session)); Assert.Contains("9:30am–5:00pm", LastChat(session));
        Assert.Same(old, session.LatestPlan);
        session.SendChat("Confirm building priorities");
        Assert.Contains("Saved building priorities", LastChat(session)); Assert.Contains("Rebuilt from classes.xlsx", LastChat(session));
        Assert.NotSame(old, session.LatestPlan); Assert.False(session.RequiresRebuild);
    }

    [Fact]
    public void Building_order_and_followup_hours_are_remembered_before_confirmation()
    {
        var session = NewSession(); session.SendChat("Prioritize GDC"); session.SendChat("then RLP");
        session.SendChat("between 10am and 4pm");
        Assert.Contains("GDC → RLP", LastChat(session)); Assert.Contains("10:00am–4:00pm", LastChat(session));
        session.SendChat("yes");
        Assert.Equal(new[] { "GDC", "RLP" }, session.Rules.BuildingPriority.Buildings);
        Assert.Equal(TimeSpan.FromHours(10), session.Rules.BuildingPriority.Start); Assert.Null(session.LatestPlan);
    }

    [Theory]
    [InlineData("Prioritize XYZ between 9am and 5pm")]
    [InlineData("prioritize gdc, then xyz between 9am and 5pm")]
    [InlineData("Prioritize GDC between 9 and 5")]
    [InlineData("Prioritize GDC between 5pm and 9am")]
    public void Unclear_priority_requests_do_not_change_saved_settings(string request)
    {
        var session = NewSession(); var before = session.Rules.BuildingPriority;
        session.SendChat(request); Assert.Same(before, session.Rules.BuildingPriority);
        Assert.DoesNotContain("Confirm building priorities", session.Conversation.Suggestions);
    }

    [Fact]
    public void Priority_confirmation_is_rejected_when_the_reviewed_inputs_change()
    {
        var session = NewSession(); session.SendChat("Prioritize RLP, then GDC between 10am and 4pm");
        var before = session.Rules.BuildingPriority;
        session.Save("notes.pdf", "%PDF notes"u8.ToArray(), FileKind.Other);
        session.SendChat("Confirm building priorities");
        Assert.Contains("changed since the priority review", LastChat(session)); Assert.Same(before, session.Rules.BuildingPriority);
    }

    [Fact]
    public void Priority_queries_report_status_without_proposing_a_change()
    {
        var session = NewSession(); session.SendChat("Show building priorities");
        Assert.Contains("Building order: GDC", LastChat(session));
        Assert.DoesNotContain("Confirm building priorities", session.Conversation.Suggestions);
        session.SendChat("Which GDC rooms do we control?"); Assert.Contains("12:30pm", LastChat(session));
    }

    [Fact]
    public void Building_use_report_measures_only_bookings_in_the_uploaded_plan()
    {
        var session = new PlanningSession(); session.Initialize(new MemoryStore(),
            new DataFiles("building,room,capacity,features,notes\nGDC,1.100,40,CON,", "", "", "", ""));
        session.Save("classes.xlsx", RevisedClasses("PTF"), FileKind.Other); session.Build("classes.xlsx");
        session.SendChat("How full is GDC?");
        Assert.Contains("3 booked room-hours out of 37.5", LastChat(session));
        Assert.Contains("not campus-wide bookings", LastChat(session));
    }

    [Fact]
    public void Chatbot_can_explain_a_professors_prior_building_use()
    {
        var session = NewSession(); session.Save("past.xlsx", PastSchedule(), FileKind.Other);
        session.SendChat("Did Smith teach in GDC in previous semesters?");
        Assert.Contains("SMITH", LastChat(session)); Assert.Contains("GDC 2.216", LastChat(session)); Assert.Contains("Fall 2025", LastChat(session));
    }

    [Fact]
    public void Building_priority_can_be_disabled_after_review()
    {
        var session = NewSession(); session.SendChat("Disable building priorities"); session.SendChat("Confirm building priorities");
        Assert.False(session.Rules.BuildingPriority.Enabled);
    }

    [Fact]
    public void Using_the_first_course_option_is_not_mistaken_for_a_global_priority_request()
    {
        var session = NewSession(); session.Save("classes.xlsx", RevisedClasses("PTF"), FileKind.Other); session.Build("classes.xlsx");
        session.SendChat("find data structures a free slot after 2pm"); var preview = session.Conversation.Proposal;
        Assert.NotNull(preview); session.SendChat("use the first one");
        Assert.Same(preview, session.Conversation.Proposal); Assert.Contains("Selected option 1", LastChat(session));
    }
    [Theory]
    [InlineData("Prioritize GDC between 8am and 9pm", 8, 21)]
    [InlineData("Prioritize GDC between 9:30 a.m. and 5 p.m.", 9.5, 17)]
    public void Chat_priority_windows_support_a_full_day_and_punctuated_meridiems(string request, double start, double end)
    {
        var session = NewSession(); session.SendChat(request);
        Assert.Contains("Confirm building priorities", session.Conversation.Suggestions);
        session.SendChat("Confirm building priorities");
        Assert.Equal(TimeSpan.FromHours(start), session.Rules.BuildingPriority.Start);
        Assert.Equal(TimeSpan.FromHours(end), session.Rules.BuildingPriority.End);
    }

    [Fact]
    public void An_unknown_professor_does_not_get_other_faculty_history_as_their_own()
    {
        var session = NewSession(); session.Save("past.xlsx", PastSchedule(), FileKind.Other);
        session.SendChat("Did Howard teach in GDC in previous semesters?");
        Assert.Contains("No matching history for Howard", LastChat(session));
        Assert.DoesNotContain("SMITH:", LastChat(session));
    }

    [Theory]
    [InlineData("Prioritize GDC, then RLP, between 9:30am and 5pm")]
    [InlineData("prioritize gdc and rlp between 9:30am and 5pm")]
    public void Natural_building_lists_keep_the_full_order(string request)
    {
        var session = NewSession(); session.SendChat(request);
        Assert.Contains("GDC → RLP", LastChat(session));
        Assert.Contains("Confirm building priorities", session.Conversation.Suggestions);
        session.SendChat("Confirm building priorities");
        Assert.Equal(new[] { "GDC", "RLP" }, session.Rules.BuildingPriority.Buildings);
    }

}
