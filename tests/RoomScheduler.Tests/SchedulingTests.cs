using RoomScheduler;
using Xunit;

namespace RoomScheduler.Tests;

public class SchedulingTests
{
    private static Xlsx.Sheet Sheet(string name, params string[][] rows)
    {
        var sheet = new Xlsx.Sheet { Name = name };
        sheet.Rows.AddRange(rows.Select(r => r.ToList()));
        return sheet;
    }
    private static DepartmentWorkbook Requests(string size = "30", string track = "", bool preferences = true) =>
        DepartmentWorkbook.FromSheets(new List<Xlsx.Sheet>
        {
            Sheet("Classes App", new[] {"Title", "Professor", "Minimum Room Size Needed", "Tenure or PTF", "Semester", "Year"},
                new[] {"314 DATA STRUCTURES", "SMITH, A", size, track, "Fall", "2027"}),
            Sheet("Scheduling Preferences", new[] {"Prof", "course", "Time 1"},
                preferences ? new[] {"SMITH A", "314 DATA STRUCTURES", "MWF 10:00-11:00"} : new[] {"", "", ""})
        });
    private static List<Room> Rooms() => new()
    {
        new Room {Building = "GDC", RoomNumber = "2.216", Capacity = 200},
        new Room {Building = "GDC", RoomNumber = "1.100", Capacity = 40},
        new Room {Building = "GDC", RoomNumber = "1.200", Capacity = 100}
    };
    private static Planner Planner(List<Room>? rooms = null, PastSchedule? past = null, FacultyStore? faculty = null) =>
        new(rooms ?? Rooms(), new Campus(), past ?? PastSchedule.Empty, faculty ?? FacultyStore.InMemory());
    private static PastSection History(int term, int size) => new()
    {
        SemesterCode = term, Semester = term.ToString(), Course = "314", Title = "DATA STRUCTURES",
        Enrollment = size, Instructors = new() {"SMITH"}
    };

    [Fact]
    public void Unknown_track_has_one_general_information_flag_not_row_review()
    {
        var plan = Planner().Build(Requests());
        Assert.Single(plan.GeneralFlags, f => f.Message.Contains("unknown faculty track") && f.Level == FlagLevel.Info);
        Assert.DoesNotContain(plan.AllFlags, x => x.Flag.Message.Contains("Tenure or PTF is blank"));
        Assert.Contains(plan.AllFlags, x => x.Flag.Category == ReviewFlag.History && x.Flag.Level == FlagLevel.Review);
    }

    [Fact]
    public void No_instructor_history_or_form_still_gets_a_feasible_room_and_time()
    {
        var plan = Planner().Build(Requests(preferences: false, track: "t"));
        var p = Assert.Single(plan.Placements);
        Assert.NotNull(p.Room); Assert.NotNull(p.Time);
        Assert.False(p.Time!.Overlaps(plan.Rules.TenureLunch));
        Assert.Contains(p.Section.Flags, f => f.Message.Contains("provisional feasible"));
    }

    [Fact]
    public void Missing_enrollment_never_schedules_for_zero()
    {
        var p = Assert.Single(Planner().Build(Requests("0")).Placements);
        Assert.Equal(25, p.Section.SizeForRoom);
        Assert.NotNull(p.Room);
        Assert.Contains(p.Section.Flags, f => f.Category == ReviewFlag.Size && f.Level == FlagLevel.Review);
    }

    [Fact]
    public void Enrollment_uses_median_of_term_means_and_keeps_baseline_distinct()
    {
        var past = PastSchedule.Empty;
        past.Sections.AddRange(new[] {History(20239, 95), History(20249, 100), History(20259, 1)});
        var wb = Requests("0");
        wb.Sections[0].MinimumRoomCapacity = 150;
        var p = Assert.Single(Planner(past: past).Build(wb).Placements);
        Assert.Equal(95, p.Section.PastEnrollment);
        Assert.Equal(95, p.Section.SizeForRoom);
        Assert.True(p.Room!.Capacity >= 150);
    }

    [Fact]
    public void Low_or_missing_history_cannot_undercut_physical_minimum()
    {
        var past = PastSchedule.Empty; past.Sections.Add(History(20259, 1));
        var wb = Requests("100"); wb.Sections[0].MinimumRoomCapacity = 150;
        var p = Assert.Single(Planner(past: past).Build(wb).Placements);
        Assert.Equal(85, p.Section.SizeForRoom);
        Assert.Equal(200, p.Room!.Capacity);
        Assert.Equal(180, p.RegistrarCap);
    }

    [Fact]
    public void Preferred_baseline_guides_room_choice_without_becoming_enrollment()
    {
        var wb = Requests(); wb.Sections[0].PreferredRoomCapacity = 100;
        var p = Assert.Single(Planner().Build(wb).Placements);
        Assert.Equal(100, p.Room!.Capacity); Assert.Equal(30, p.Section.SizeForRoom);
    }

    [Theory]
    [InlineData("F")]
    [InlineData("")]
    public void Hybrid_physical_room_is_in_room_history_even_with_missing_days(string days)
    {
        var past = PastSchedule.FromWorkbook(new[] {Sheet("Actual",
            new[] {"CCYYS", "CNBR", "TITLE", "DAYS1", "BLDG1", "RM1", "NON-INV NOTELINES"},
            new[] {"20259", "303E", "ELEMENTS", days, "ART", "1.1020000000000001", "Partially taught as a Web-based course."})});
        var section = Assert.Single(past.InRoom("ART 1.102"));
        Assert.True(section.Hybrid); Assert.False(section.Online);
    }

    private static (Planner Planner, Planner.Plan Plan, List<Room> Rooms) OverrideFixture(int roomCount = 3)
    {
        var rooms = Rooms().Take(roomCount).ToList();
        var planner = Planner(rooms);
        var wb = Requests();
        wb.Sections.Add(new SectionRequest { SourceRow = 2, Title = "315 OTHER COURSE", Instructor = "JONES B", ProjectedSize = 30 });
        wb.Forms.Add(new PreferenceForm { Professor = "JONES B", Course = "315 OTHER COURSE", Time1 = "MWF 10:00-11:00" });
        return (planner, planner.Build(wb), rooms);
    }
    private static void AssertNoConflicts(Planner.Plan plan)
    {
        var placed = plan.Placements.Concat(plan.Discussions).Where(p => p.Time != null).ToList();
        for (int i = 0; i < placed.Count; i++)
            foreach (var b in placed.Skip(i + 1).Where(b => b.Time!.Overlaps(placed[i].Time!)))
            {
                Assert.False(placed[i].Room != null && placed[i].Room == b.Room);
                Assert.False(!placed[i].Section.InstructorMissing && placed[i].Section.Surname == b.Section.Surname);
            }
    }

    [Fact]
    public void Override_previews_displacement_then_applies_without_conflicts()
    {
        var (planner, plan, rooms) = OverrideFixture();
        var first = plan.Placements[0]; var second = plan.Placements[1];
        var before = first.Room;
        var proposal = planner.ProposeOverride(plan, $"Put 314 in {second.Room!.DisplayName} Monday through Thursday at 10:00am-11:00am", true);
        Assert.Equal(before, first.Room); Assert.Equal(2, proposal.ProposedChanges.Count);
        planner.ApplyOverride(plan, proposal);
        Assert.Equal(4, first.Time!.Days.Count);
        Assert.NotEqual(first.Room, second.Room);
        AssertNoConflicts(plan);
        Assert.Throws<InvalidOperationException>(() => planner.ApplyOverride(plan, proposal));
    }

    [Fact]
    public void Impossible_displacement_leaves_original_plan_unchanged()
    {
        var rooms = new List<Room> { Rooms()[1] };
        var planner = Planner(rooms); var wb = Requests();
        wb.Sections.Add(new SectionRequest {SourceRow = 2, Title = "315 OTHER", Instructor = "JONES B", ProjectedSize = 30});
        wb.Forms.Add(new PreferenceForm {Professor = "JONES B", Course = "315 OTHER", Time1 = "MWF 1:00-2:00"});
        var plan = planner.Build(wb); var original = plan.Placements[0].Time;
        Assert.Throws<ArgumentException>(() => planner.ProposeOverride(plan, "Put 314 in GDC 1.100 MWF at 1:00pm-2:00pm"));
        Assert.Equal(original, plan.Placements[0].Time); AssertNoConflicts(plan);
    }

    [Fact]
    public void Override_checks_policy_offline_room_and_stale_preview()
    {
        var (planner, plan, rooms) = OverrideFixture();
        plan.Placements[0].Section.Track = FacultyTrack.Tenure;
        Assert.Throws<ArgumentException>(() => planner.ProposeOverride(plan, "Put 314 in GDC 2.216 MW at 12:00pm-1:00pm", true));
        var proposal = planner.ProposeOverride(plan, "Put 314 in GDC 2.216 TTH at 2:00pm-3:30pm", true);
        planner.Rules.Offline.SetBuilding("GDC", true, rooms);
        Assert.Throws<ArgumentException>(() => planner.ApplyOverride(plan, proposal));
        planner.Rules.Offline.SetBuilding("GDC", false, rooms);
        plan.Placements[1].Time = MeetingTime.Of("F", 16, 0, 17, 0);
        Assert.Throws<InvalidOperationException>(() => planner.ApplyOverride(plan, proposal));
    }

    [Fact]
    public void Oversized_exceptions_are_explicit_and_invalid_text_is_rejected()
    {
        var planner = Planner(); var plan = planner.Build(Requests());
        Assert.Throws<ArgumentException>(() => planner.ProposeOverride(plan, "Put 314 in GDC 2.216 TTH at 2:00pm-3:30pm"));
        var proposal = planner.ProposeOverride(plan, "Put 314 in GDC 2.216 TTH at 2:00pm-3:30pm", true);
        Assert.Contains("exception explicitly allowed", proposal.Explanation);
        Assert.Throws<ArgumentException>(() => planner.ProposeOverride(plan, "Put 314 in GDC 2.216 tomorrow at 2pm", true));
    }
    [Fact]
    public void Override_rejects_instructor_overlap_profile_unavailability_and_undersized_rooms()
    {
        var (planner, plan, rooms) = OverrideFixture();
        var first = plan.Placements[0];
        first.Section.Profile!.Unavailable = "TTH 2:00-3:30";
        Assert.Throws<ArgumentException>(() => planner.ProposeOverride(plan,
            "Put 314 in GDC 2.216 TTH at 2:00pm-3:30pm", true));
        first.Section.Profile.Unavailable = "";
        first.Section.Wishes.Blocked.Add((MeetingTime.Of("F", 9, 0, 12, 0), "not Friday morning"));
        Assert.Throws<ArgumentException>(() => planner.ProposeOverride(plan,
            "Put 314 in GDC 2.216 F at 10:00am-11:00am", true));
        plan.Placements.Add(new Placement {
            Section = new SectionRequest {Title = "316 SECOND", Instructor = "SMITH A", ProjectedSize = 30},
            Room = rooms[2], Time = MeetingTime.Of("TTH", 15, 0, 16, 0) });
        Assert.Throws<ArgumentException>(() => planner.ProposeOverride(plan,
            "Put 314 in GDC 2.216 TTH at 3:00pm-4:00pm", true));
        first.Section.MinimumRoomCapacity = 150;
        Assert.Throws<ArgumentException>(() => planner.ProposeOverride(plan,
            "Put 314 in GDC 1.100 TTH at 10:00am-11:00am", true));
    }

    [Fact]
    public void Override_rejects_ambiguous_sections_and_preserves_duration_for_start_only()
    {
        var planner = Planner(); var plan = planner.Build(Requests());
        var proposal = planner.ProposeOverride(plan, "Put 314 in GDC 2.216 Tuesday at 2:00pm", true);
        var change = Assert.Single(proposal.ProposedChanges);
        Assert.Equal(TimeSpan.FromHours(1), change.Time.Duration);
        Assert.Equal(new[] {DayOfWeek.Tuesday}, change.Time.Days);
        plan.Placements.Add(new Placement { Section = new SectionRequest {Title = "314 DATA STRUCTURES", Instructor = "OTHER A"} });
        Assert.Throws<ArgumentException>(() => planner.ProposeOverride(plan,
            "Put 314 in GDC 2.216 TTH at 2:00pm-3:30pm", true));
    }

    [Fact]
    public void Semester_specific_reference_does_not_apply_to_another_semester_and_invalid_values_fail()
    {
        var wb = Requests(); var faculty = FacultyStore.InMemory();
        var sheet = Sheet("Course Reference", new[] {"Course", "Minimum Room Capacity", "Semester"},
            new[] {"314", "150", "Fall 2026"});
        ReferenceSheets.Apply(new[] {sheet}, wb, faculty);
        Assert.Equal(0, wb.Sections[0].MinimumRoomCapacity);
        sheet.Rows[1][2] = "Fall 2027";
        ReferenceSheets.Apply(new[] {sheet}, wb, faculty);
        Assert.Equal(150, wb.Sections[0].MinimumRoomCapacity);
        sheet.Rows[1][1] = "-1";
        Assert.Throws<ArgumentException>(() => ReferenceSheets.Apply(new[] {sheet}, wb, faculty));
    }

    [Fact]
    public void Approved_override_exports_the_new_time_even_if_source_cells_were_filled()
    {
        var wb = DepartmentWorkbook.FromSheets(new List<Xlsx.Sheet> {
            Sheet("Classes App", new[] {"Title", "Professor", "Minimum Room Size Needed", "Days", "Time", "Bldg", "Room"},
                new[] {"398T TALKS", "SMITH A", "30", "MWF", "old time", "ART", "1.102"}) });
        var planner = Planner(); var plan = planner.Build(wb);
        planner.ApplyOverride(plan, planner.ProposeOverride(plan,
            "Put 398T in GDC 2.216 Monday through Thursday at 10:00am-11:00am", true));
        var exported = Xlsx.Read(new MemoryStream(PlanExport.Workbook(plan, Rooms())))[0];
        Assert.Equal("MTWTH", exported.Cell(1, 3));
        Assert.Equal("10:00am-11:00am", exported.Cell(1, 4));
        Assert.Equal("GDC", exported.Cell(1, 5));
        Assert.Equal("2.216", exported.Cell(1, 6));
    }

}
