using RoomScheduler;
using Xunit;

namespace RoomScheduler.Tests;

/// <summary>
/// The website's file steps, run against an in-memory store instead of the
/// browser's local storage. Spreadsheets are built here so the tests need
/// none of the department's private files.
/// </summary>
public class FileUploadTests
{
    private static PlanningSession NewSession(MemoryStore? store = null)
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
}
