using RoomScheduler;
using Xunit;

namespace RoomScheduler.Tests;

/// <summary>
/// The website's file steps, run against an in-memory store instead of the
/// browser's local storage. Spreadsheets are built here so the tests need
/// none of the department's private files.
/// </summary>
public partial class FileUploadTests
{
    private static PlanningSession NewSession(IKeyValueStore? store = null)
    {
        var session = new PlanningSession();
        session.Initialize(store ?? new MemoryStore(), DataFiles.FromDataFolder());
        return session;
    }

    private static byte[] Workbook(params (string Name, string[][] Rows)[] sheets)
    {
        var outSheets = sheets.Select(s =>
        {
            var sheet = new Xlsx.OutSheet { Name = s.Name };
            foreach (var row in s.Rows) sheet.AddRow(row);
            return sheet;
        });
        return Xlsx.Write(outSheets);
    }

    // A registrar export: one CS 314 section in GDC 2.216, MWF 11-12 (Excel day fractions).
    private static byte[] PastSchedule(string semester = "20259") => Workbook(("All CS Sections", new[]
    {
        new[] { "CCYYS", "UNQ", "CNBR", "TITLE", "INST NAME", "TOT SEATS TKN", "DAYS1", "FROM T1", "TO T1", "BLDG1", "RM1" },
        new[] { semester, "54635", "314", "DATA STRUCTURES", "SMITH, A;", "120", "MWF", "0.4583333333", "0.5", "GDC", "2.216" }
    }));

    private static byte[] Requests() => Workbook(
        ("Fall 26 Classes App", new[]
        {
            new[] { "Semester", "Year", "Title", "Professor", "Tenure or PTF", "Minimum Room Size Needed", "Days", "Time" },
            new[] { "Fall", "2026", "314 DATA STRUCTURES", "SMITH, A", "ptf", "120", "", "" },
            new[] { "Fall", "2026", "388G ALGORITHMS", "JONES, B", "t", "30", "", "" }
        }),
        ("Fall 26 Scheduling Preferences", new[]
        {
            new[] { "Prof", "course", "Time 1", "Time 2", "Time 3", "Room Request" },
            new[] { "SMITH A", "314 DATA STRUCTURES", "MWF 11:00-12:00", "MWF 1:00-2:00", "MWF 2:00-3:00", "GDC" },
            new[] { "JONES B", "388G ALGORITHMS", "MW 12:30-2:00", "TTH 2:00-3:30", "TTH 3:30-5:00", "GDC" }
        }));

    private static byte[] RoomsTable() => Workbook(("GPC Rooms", new[]
    {
        new[] { "Building", "Room", "Size", "Features", "Notes" },
        new[] { "GDC", "2.216", "200", "CON/Wb", "" },
        new[] { "RLP", "1.104", "76", "Bb/CON/CWS", "" }
    }));

    private static byte[] LectureList() => Workbook(("Lecture Online", new[]
    {
        new[] { "Room", "Normal Capacity", "Scheduling Department" },
        new[] { "RLP 1.104", "76", "Office of the Registrar" }
    }));

    [Fact]
    public void Fresh_session_has_built_in_rooms_and_no_files()
    {
        var s = NewSession();
        Assert.True(s.Rooms.Count > 200);
        Assert.Empty(s.Files());
        Assert.True(s.LectureCaptureRooms > 100);
        Assert.True(s.Rules.Offline.BuildingOffline("CPE"));
    }

    [Fact]
    public void Preference_sheet_is_saved_and_can_be_scheduled()
    {
        var s = NewSession();
        var info = s.Save("fall26.xlsx", Requests(), FileKind.Requests);

        Assert.Equal(FileKind.Requests, info.Kind);
        Assert.Single(s.Of(FileKind.Requests));

        var plan = s.Build("fall26.xlsx");
        Assert.Equal(2, plan.Placements.Count(p => p.Room != null));
        var grad = plan.Placements.Single(p => p.Section.Course == "388G");
        Assert.False(grad.Time!.Overlaps(s.Rules.TenureLunch)); // asked for MW 12:30, tenure lunch blocks it
    }

    [Fact]
    public void Past_schedule_becomes_history_even_if_added_in_the_wrong_step()
    {
        var s = NewSession();
        var info = s.Save("fall25.xlsx", PastSchedule(), FileKind.Requests);

        Assert.Equal(FileKind.PastSchedule, info.Kind);
        Assert.Empty(s.Of(FileKind.Requests));
        Assert.Contains("fall25.xlsx", s.ActiveHistory);
        Assert.Single(s.Past.Sections);
    }

    [Fact]
    public void History_can_be_switched_off_and_the_choice_is_remembered()
    {
        var store = new MemoryStore();
        var s = NewSession(store);
        s.Save("fall25.xlsx", PastSchedule(), FileKind.PastSchedule);
        s.SetHistoryActive("fall25.xlsx", false);
        Assert.Empty(s.Past.Sections);

        var reopened = NewSession(store);
        Assert.Single(reopened.Of(FileKind.PastSchedule));
        Assert.Empty(reopened.Past.Sections);

        reopened.SetHistoryActive("fall25.xlsx", true);
        Assert.Single(reopened.Past.Sections);
    }

    [Fact]
    public void Files_survive_reopening_the_page()
    {
        var store = new MemoryStore();
        var s = NewSession(store);
        s.Save("fall26.xlsx", Requests(), FileKind.Requests);
        s.Save("fall25.xlsx", PastSchedule(), FileKind.PastSchedule);

        var reopened = NewSession(store);
        Assert.Equal(2, reopened.Files().Count);
        Assert.Single(reopened.Past.Sections);
        Assert.NotNull(reopened.Build("fall26.xlsx"));
    }

    [Fact]
    public void Saving_the_same_name_replaces_the_file()
    {
        var s = NewSession();
        s.Save("history.xlsx", PastSchedule("20259"), FileKind.PastSchedule);
        s.Save("history.xlsx", PastSchedule("20262"), FileKind.PastSchedule);

        Assert.Single(s.Files());
        Assert.Equal("Spring 2026", s.Past.Sections.Single().Semester);
    }

    [Fact]
    public void Unrecognized_spreadsheet_is_turned_away_in_steps_1_to_3()
    {
        var s = NewSession();
        var junk = Workbook(("Notes", new[] { new[] { "anything", "else" } }));

        Assert.Equal(FileKind.Unknown, s.Save("notes.xlsx", junk, FileKind.Requests).Kind);
        Assert.Equal(FileKind.Unknown, s.Save("notes.xlsx", junk, FileKind.PastSchedule).Kind);
        Assert.Empty(s.Files());
    }

    [Fact]
    public void Additional_files_keeps_anything_and_gives_it_back_unchanged()
    {
        var store = new MemoryStore();
        var s = NewSession(store);
        var pdf = "%PDF-1.4 campus map"u8.ToArray();
        var junk = Workbook(("Notes", new[] { new[] { "anything", "else" } }));

        Assert.Equal(FileKind.Other, s.Save("map.pdf", pdf, FileKind.Other).Kind);
        Assert.Equal(FileKind.Other, s.Save("notes.xlsx", junk, FileKind.Other).Kind);
        Assert.Equal(pdf, s.FileBytes("map.pdf"));

        var reopened = NewSession(store);
        Assert.Equal(2, reopened.Of(FileKind.Other).Count);
    }

    [Fact]
    public void Additional_files_still_routes_department_files_to_their_step()
    {
        var s = NewSession();
        Assert.Equal(FileKind.PastSchedule, s.Save("fall25.xlsx", PastSchedule(), FileKind.Other).Kind);
        Assert.Equal(FileKind.Requests, s.Save("fall26.xlsx", Requests(), FileKind.Other).Kind);
        Assert.Empty(s.Of(FileKind.Other));
    }

    [Fact]
    public void New_rooms_table_replaces_the_built_in_one_until_removed()
    {
        var s = NewSession();
        int builtIn = s.Rooms.Count;

        Assert.Equal(FileKind.Rooms, s.Save("rooms.xlsx", RoomsTable(), FileKind.Rooms).Kind);
        Assert.Equal("rooms.xlsx", s.RoomsSource);
        Assert.Contains(s.Rooms, r => r.DisplayName == "RLP 1.104");
        Assert.True(s.Rooms.Count < builtIn);

        s.Delete("rooms.xlsx");
        Assert.Equal(builtIn, s.Rooms.Count);
    }

    [Fact]
    public void Lectures_Online_list_under_additional_files_is_used()
    {
        var s = NewSession();
        Assert.Equal(FileKind.LectureCapture, s.Save("lectures.xlsx", LectureList(), FileKind.Other).Kind);

        Assert.Equal("lectures.xlsx", s.LectureCaptureSource);
        Assert.Equal(1, s.LectureCaptureRooms);
        Assert.True(s.Rooms.Single(r => r.DisplayName == "RLP 1.104").LectureCapture);

        s.Delete("lectures.xlsx");
        Assert.True(s.LectureCaptureRooms > 100);
    }

    [Fact]
    public void Too_large_files_are_turned_away()
    {
        var s = NewSession();
        var big = new byte[PlanningSession.MaxFileBytes + 1];
        Assert.Equal(FileKind.Unknown, s.Save("big.pdf", big, FileKind.Other).Kind);
        Assert.Empty(s.Files());
    }

    [Fact]
    public void Removing_a_file_removes_it_everywhere()
    {
        var store = new MemoryStore();
        var s = NewSession(store);
        s.Save("fall25.xlsx", PastSchedule(), FileKind.PastSchedule);
        s.Delete("fall25.xlsx");

        Assert.Empty(s.Files());
        Assert.Empty(s.Past.Sections);
        Assert.Null(s.FileBytes("fall25.xlsx"));
        Assert.Empty(NewSession(store).Files());
    }
    [Fact]
    public void Separate_classes_and_preferences_are_kept_and_joined_explicitly()
    {
        var store = new MemoryStore();
        var s = NewSession(store);
        var sheets = Xlsx.Read(new MemoryStream(Requests()));
        byte[] Part(int index)
        {
            var part = new Xlsx.OutSheet { Name = sheets[index].Name };
            foreach (var row in sheets[index].Rows) part.AddRow(row);
            return Xlsx.Write(new[] { part });
        }
        var classesBytes = Part(0);
        Assert.Equal(FileKind.Requests, s.Save("classes.xlsx", classesBytes, FileKind.Requests).Kind);
        Assert.Equal(FileKind.Preferences, s.Save("preferences.xlsx", Part(1), FileKind.Requests).Kind);
        var reopened = NewSession(store);
        Assert.Equal(2, reopened.Files().Count);
        var plan = reopened.Build("classes.xlsx", "preferences.xlsx");
        Assert.All(plan.Placements, p => Assert.NotNull(p.Section.Form));
        Assert.All(plan.Placements, p => Assert.NotNull(p.Room));
        Assert.Empty(plan.Workbook.UnmatchedForms);
        Assert.Equal(FileKind.Unknown, s.Save("classes.xlsx", Part(1), FileKind.Requests).Kind);
        Assert.Equal(classesBytes, s.FileBytes("classes.xlsx"));
    }

    [Fact]
    public void Reference_baselines_and_faculty_edits_survive_reopening_and_semester_changes()
    {
        var store = new MemoryStore();
        var s = NewSession(store);
        s.Save("classes.xlsx", Requests(), FileKind.Requests);
        var reference = Workbook(
            ("Course Reference", new[] {
                new[] { "Course", "Minimum Room Capacity", "Preferred Room Capacity", "Semester" },
                new[] { "314", "150", "200", "" } }),
            ("Faculty Reference", new[] {
                new[] { "Professor", "Tenure or PTF" }, new[] { "SMITH A", "T" } }));
        Assert.Equal(FileKind.Reference, s.Save("reference.xlsx", reference, FileKind.Other).Kind);
        var initial = s.Build("classes.xlsx");
        Assert.Equal(150, initial.Placements[0].Section.MinimumRoomCapacity);
        Assert.Equal(FacultyTrack.Tenure, initial.Placements[0].Section.Track);
        s.Faculty.Find("SMITH")!.Track = FacultyTrack.Professional;
        s.Faculty.Save();
        s.SetSemesterBaseline("Fall 2026", "314", 175, 200);
        var reopened = NewSession(store);
        var section = reopened.Build("classes.xlsx").Placements[0].Section;
        Assert.Equal(175, section.MinimumRoomCapacity);
        Assert.Equal(200, section.PreferredRoomCapacity);
        Assert.Equal(FacultyTrack.Professional, section.Track);
        Assert.Throws<ArgumentException>(() => reopened.SetSemesterBaseline("Fall 2026", "314", 200, 100));
    }

    [Fact]
    public void Multiple_history_files_are_used_and_duplicate_terms_do_not_count_twice()
    {
        var s = NewSession();
        s.Save("2025.xlsx", PastSchedule("20259"), FileKind.PastSchedule);
        s.Save("2024.xlsx", PastSchedule("20249"), FileKind.PastSchedule);
        s.Save("2025 copy.xlsx", PastSchedule("20259"), FileKind.PastSchedule);
        Assert.Equal(2, s.Past.Sections.Count);
        Assert.Equal("Fall 2025", s.Past.Semesters.First());
    }

    [Fact]
    public void Empty_room_inventory_is_reported_before_building_an_empty_schedule()
    {
        var session = new PlanningSession();
        session.Initialize(new MemoryStore(), new DataFiles("", "", "", "", ""));
        session.Save("requests.xlsx", Requests(), FileKind.Requests);
        var error = Assert.Throws<InvalidDataException>(() => session.Build("requests.xlsx"));
        Assert.Contains("room inventory is empty", error.Message);
    }

    [Fact]
    public void Missing_selected_preferences_file_is_rejected_without_replacing_the_current_plan()
    {
        var s=NewSession(); s.Save("classes.xlsx",Requests(),FileKind.Requests); var plan=s.Build("classes.xlsx");
        Assert.Throws<InvalidDataException>(()=>s.Build("classes.xlsx","missing.xlsx"));
        Assert.Same(plan,s.LatestPlan); Assert.Null(s.LatestPreferences);
    }

    [Fact]
    public void Case_variant_replacement_returns_the_canonical_stored_filename()
    {
        var s=NewSession(); s.Save("Classes.xlsx",Requests(),FileKind.Requests);
        var result=s.Save("classes.XLSX",Requests(),FileKind.Requests);
        Assert.Equal("Classes.xlsx",result.Name); Assert.Equal("Classes.xlsx",s.Files().Single().Name);
    }

    [Fact]
    public void Semester_reference_override_wins_even_if_the_generic_row_comes_last()
    {
        var s=NewSession(); s.Save("classes.xlsx",Requests(),FileKind.Requests);
        s.Save("reference.xlsx",Workbook(("Course Reference",new[] {
            new[]{"Course","Minimum Room Capacity","Preferred Room Capacity","Semester"},
            new[]{"314","175","200","Fall 2026"}, new[]{"314","150","160",""}
        })),FileKind.Other);
        var section=s.Build("classes.xlsx").Placements[0].Section;
        Assert.Equal(175,section.MinimumRoomCapacity); Assert.Equal(200,section.PreferredRoomCapacity);
    }

    [Fact]
    public void Room_upload_invalidates_manual_previews_until_the_schedule_is_rebuilt()
    {
        var s=NewSession(); s.Save("classes.xlsx",Requests(),FileKind.Requests); var plan=s.Build("classes.xlsx");
        var p=plan.Placements[0]; var time=MeetingTime.Of("F",15,0,16,0);
        var preview=s.ProposeMove(p,p.Room!,time,true);
        s.Save("rooms.xlsx",RoomsTable(),FileKind.Rooms);
        Assert.True(s.RequiresRebuild);
        Assert.Throws<InvalidOperationException>(()=>s.ApplyOverride(preview));
        Assert.Throws<InvalidOperationException>(()=>s.ProposeMove(p,s.Rooms.First(),time,true));
        Assert.Same(plan,s.LatestPlan);
        s.Build("classes.xlsx"); Assert.False(s.RequiresRebuild);
    }

    [Fact]
    public void Faculty_offline_baseline_and_rule_edits_require_rebuilding_but_notes_do_not()
    {
        var s=NewSession(); s.Save("classes.xlsx",Requests(),FileKind.Requests); s.Build("classes.xlsx");
        s.Save("map.pdf","map"u8.ToArray(),FileKind.Other); Assert.False(s.RequiresRebuild);
        s.Faculty.All.First().Track=FacultyTrack.Unknown; s.Faculty.Save(); Assert.True(s.RequiresRebuild);
        s.Build("classes.xlsx"); Assert.False(s.RequiresRebuild);
        s.Rules.Offline.SetRoom(s.Rooms.First(),true,s.Rooms); Assert.True(s.RequiresRebuild);
        s.Build("classes.xlsx"); s.SetSemesterBaseline("Fall 2026","314",140,160); Assert.True(s.RequiresRebuild);
        s.Build("classes.xlsx"); s.Rules.AllowAlternateTimes=!s.Rules.AllowAlternateTimes; Assert.True(s.RequiresRebuild);
    }

    [Fact]
    public void Reference_tabs_in_the_selected_preferences_workbook_are_applied()
    {
        var s=NewSession(); s.Save("classes.xlsx",Requests(),FileKind.Requests);
        var prefs=Xlsx.Read(new MemoryStream(Requests()))[1];
        s.Save("prefs.xlsx",Workbook((prefs.Name,prefs.Rows.Select(r=>r.ToArray()).ToArray()),
            ("Course Reference",new[]{new[]{"Course","Minimum Room Capacity"},new[]{"314","175"}})),FileKind.Requests);
        Assert.Equal(175,s.Build("classes.xlsx","prefs.xlsx").Placements[0].Section.MinimumRoomCapacity);
    }

    private sealed class FailingStore : IKeyValueStore
    {
        public MemoryStore Inner { get; }=new();
        public string? FailNextKey { get; set; }
        public string? Get(string key)=>Inner.Get(key);
        public void Set(string key,string value)
        {
            if(key==FailNextKey) { FailNextKey=null; throw new InvalidOperationException("Storage quota exceeded"); }
            Inner.Set(key,value);
        }
        public void Remove(string key)=>Inner.Remove(key);
    }

    [Fact]
    public void Failed_file_replacement_restores_original_bytes_index_and_history_choice()
    {
        var store=new FailingStore(); var s=NewSession(store); var bytes=PastSchedule("20259");
        s.Save("history.xlsx",bytes,FileKind.PastSchedule); s.SetHistoryActive("history.xlsx",false);
        var before=store.Inner.Values.ToDictionary(x=>x.Key,x=>x.Value);
        store.FailNextKey="files";
        Assert.Throws<InvalidOperationException>(()=>s.Save("history.xlsx",PastSchedule("20262"),FileKind.PastSchedule));
        Assert.Equal(bytes,s.FileBytes("history.xlsx")); Assert.Empty(s.Past.Sections);
        Assert.Equal(before.OrderBy(x=>x.Key),store.Inner.Values.OrderBy(x=>x.Key));
    }

    [Fact]
    public void Failed_new_upload_leaves_no_unindexed_file_and_failed_delete_restores_it()
    {
        var store=new FailingStore(); var s=NewSession(store); store.FailNextKey="files";
        Assert.Throws<InvalidOperationException>(()=>s.Save("requests.xlsx",Requests(),FileKind.Requests));
        Assert.Empty(s.Files()); Assert.Null(s.FileBytes("requests.xlsx"));
        var bytes=Requests(); s.Save("requests.xlsx",bytes,FileKind.Requests); store.FailNextKey="files";
        Assert.Throws<InvalidOperationException>(()=>s.Delete("requests.xlsx"));
        Assert.Equal(bytes,s.FileBytes("requests.xlsx")); Assert.Single(s.Files());
    }

    [Fact]
    public void Failed_history_choice_save_does_not_change_the_history_in_memory()
    {
        var store=new FailingStore(); var s=NewSession(store); s.Save("history.xlsx",PastSchedule(),FileKind.PastSchedule);
        store.FailNextKey="history-off";
        Assert.Throws<InvalidOperationException>(()=>s.SetHistoryActive("history.xlsx",false));
        Assert.Single(s.Past.Sections); Assert.Contains("history.xlsx",s.ActiveHistory);
    }

    [Fact]
    public void Room_usage_is_resolved_by_room_name_after_inventory_objects_are_reloaded()
    {
        var s=NewSession(); s.Save("classes.xlsx",Requests(),FileKind.Requests); var plan=s.Build("classes.xlsx");
        var assigned=plan.Placements[0]; var replacement=new Room {Building=assigned.Room!.Building,RoomNumber=assigned.Room.RoomNumber,Capacity=assigned.Room.Capacity};
        Assert.Contains(assigned,plan.InRoom(replacement));
    }

    [Theory]
    [InlineData("T")]
    [InlineData("PTF")]
    public void Clearing_a_track_stays_cleared_after_save_reopen_and_reference_import(string importedTrack)
    {
        var store = new MemoryStore(); var session = NewSession(store);
        var classes = Workbook(("Classes App", new[] {
            new[] { "Title", "Professor", "Tenure or PTF", "Minimum Room Size Needed" },
            new[] { "314 DATA STRUCTURES", "SMITH A", importedTrack, "30" }
        }));
        session.Save("classes.xlsx", classes, FileKind.Requests); session.Build("classes.xlsx");
        var profile = session.Faculty.Find("SMITH")!;
        Assert.NotEqual(FacultyTrack.Unknown, profile.Track);
        profile.SetTrack(FacultyTrack.Unknown); session.Faculty.Save();
        var reopened = NewSession(store);
        Assert.Equal(FacultyTrack.Unknown, reopened.Faculty.Find("SMITH")!.Track);
        reopened.Save("reference.xlsx", Workbook(("Faculty Reference", new[] {
            new[] { "Professor", "Tenure or PTF" }, new[] { "SMITH A", importedTrack }
        })), FileKind.Other);
        var rebuilt = reopened.Build("classes.xlsx");
        var section = Assert.Single(rebuilt.Placements).Section;
        Assert.Equal(FacultyTrack.Unknown, section.Track);
        Assert.True(reopened.Faculty.Find("SMITH")!.TrackExplicitlySet);
        Assert.Contains(rebuilt.GeneralFlags, f => f.Message.Contains("unknown faculty track"));
        Assert.Single(rebuilt.Placements); // Clearing a designation never deletes the professor's course.
    }

    [Fact]
    public void Removing_D_overrides_the_workbook_priority_after_reopening()
    {
        var store = new MemoryStore(); var session = NewSession(store);
        session.Save("classes.xlsx", Workbook(("Classes App", new[] {
            new[] { "Title", "Professor", "Tenure or PTF", "Prioritized Professor", "Minimum Room Size Needed" },
            new[] { "314 DATA STRUCTURES", "SMITH A", "PTF", "D", "30" }
        })), FileKind.Requests);
        var initial = Assert.Single(session.Build("classes.xlsx").Placements);
        Assert.True(initial.Section.Difficult);
        var profile = session.Faculty.Find("SMITH")!;
        Assert.True(profile.Difficult); // The tracker reflects the imported designation.
        profile.SetDifficult(false); session.Faculty.Save();
        var reopened = NewSession(store);
        var rebuilt = Assert.Single(reopened.Build("classes.xlsx").Placements);
        Assert.False(rebuilt.Section.Difficult);
        Assert.Equal(FacultyTrack.Professional, rebuilt.Section.Track); // D removal does not remove P.
        Assert.True(reopened.Faculty.Find("SMITH")!.DifficultExplicitlySet);
    }

    [Fact]
    public void Clearing_P_keeps_D_and_can_be_changed_to_T_later()
    {
        var store = new MemoryStore(); var session = NewSession(store);
        session.Save("classes.xlsx", Requests(), FileKind.Requests); session.Build("classes.xlsx");
        var profile = session.Faculty.Find("SMITH")!;
        profile.SetDifficult(true); profile.SetTrack(FacultyTrack.Unknown); session.Faculty.Save();
        var reopened = NewSession(store);
        var section = reopened.Build("classes.xlsx").Placements.Single(p => p.Section.Surname == "SMITH").Section;
        Assert.True(section.Difficult); Assert.Equal(FacultyTrack.Unknown, section.Track);
        reopened.Faculty.Find("SMITH")!.SetTrack(FacultyTrack.Tenure); reopened.Faculty.Save();
        var again = NewSession(store).Build("classes.xlsx").Placements.Single(p => p.Section.Surname == "SMITH").Section;
        Assert.Equal(FacultyTrack.Tenure, again.Track); Assert.True(again.Difficult);
    }

    [Fact]
    public void Older_profiles_without_explicit_edit_flags_still_accept_imported_tracks()
    {
        string? saved = null;
        var faculty = new FacultyStore("[{\"Surname\":\"SMITH\",\"DisplayName\":\"SMITH A\",\"Track\":0,\"Difficult\":false}]", json => saved = json);
        var workbook = DepartmentWorkbook.FromSheets(new() {
            new Xlsx.Sheet { Name = "Classes App", Rows = { new() { "Title", "Professor", "Tenure or PTF", "Minimum Room Size Needed" },
                new() { "314 DATA STRUCTURES", "SMITH A", "PTF", "30" } } }
        });
        var planner = new Planner(new() { new Room { Building = "GDC", RoomNumber = "1.100", Capacity = 40 } },
            new Campus(), RoomScheduler.PastSchedule.Empty, faculty);
        Assert.Equal(FacultyTrack.Professional, Assert.Single(planner.Build(workbook).Placements).Section.Track);
        faculty.Save(); Assert.NotNull(saved);
    }

}
