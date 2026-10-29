using RoomScheduler;
using Xunit;

namespace RoomScheduler.Tests;

public partial class FileUploadTests
{
    private static byte[] FacultyReference(string track) => Workbook(("Faculty Reference", new[] {
        new[] { "Professor", "Tenure or PTF" }, new[] { "SMITH A", track }
    }));

    private static byte[] RevisedClasses(string track, string difficult = "") => Workbook(("Classes App", new[] {
        new[] { "Title", "Professor", "Tenure or PTF", "Prioritized Professor", "Minimum Room Size Needed" },
        new[] { "314 DATA STRUCTURES", "SMITH A", track, difficult, "30" }
    }));

    private static string LastChat(PlanningSession session) => session.Conversation.Messages.Last().Text;

    [Fact]
    public void Newer_faculty_reference_updates_old_imported_values_after_reopening()
    {
        var store = new MemoryStore(); var session = NewSession(store);
        session.Save("classes.xlsx", Requests(), FileKind.Requests);
        session.Save("old-reference.xlsx", FacultyReference("T"), FileKind.Other);
        Assert.Equal(FacultyTrack.Tenure, session.Build("classes.xlsx").Placements[0].Section.Track);
        var reopened = NewSession(store);
        reopened.Save("updated-reference.xlsx", FacultyReference("PTF"), FileKind.Other);
        Assert.Equal(FacultyTrack.Professional, reopened.Build("classes.xlsx").Placements[0].Section.Track);
        Assert.Equal(FacultyTrack.Professional, reopened.Faculty.Find("SMITH")!.Track);
    }

    [Fact]
    public void Updated_classes_refresh_imported_track_and_D_designations()
    {
        var session = NewSession(); session.Save("old.xlsx", RevisedClasses("PTF", "D"), FileKind.Other);
        var old = Assert.Single(session.Build("old.xlsx").Placements);
        Assert.True(old.Section.Difficult); Assert.Equal(FacultyTrack.Professional, old.Section.Track);
        session.Save("new.xlsx", RevisedClasses("T"), FileKind.Other);
        var updated = Assert.Single(session.Build("new.xlsx").Placements);
        Assert.False(updated.Section.Difficult); Assert.Equal(FacultyTrack.Tenure, updated.Section.Track);
    }

    [Fact]
    public void Newer_imports_preserve_manual_faculty_clearings_and_room_additions()
    {
        var session = NewSession(); session.Save("old.xlsx", RevisedClasses("PTF", "D"), FileKind.Other);
        session.Build("old.xlsx");
        session.Faculty.Find("SMITH")!.SetTrack(FacultyTrack.Unknown);
        session.Faculty.Find("SMITH")!.SetDifficult(false); session.Faculty.Save();
        session.AddRoom("ABC", "1.100", 40);
        session.Save("new.xlsx", RevisedClasses("T", "D"), FileKind.Other);
        session.Save("new-reference.xlsx", FacultyReference("T"), FileKind.Other);
        var updated = Assert.Single(session.Build("new.xlsx").Placements);
        Assert.False(updated.Section.Difficult); Assert.Equal(FacultyTrack.Unknown, updated.Section.Track);
        Assert.Contains(session.Rooms, r => r.DisplayName == "ABC 1.100");
    }

    [Fact]
    public void Newer_reference_rows_override_older_embedded_reference_tabs()
    {
        var session = NewSession();
        session.Save("classes.xlsx", Workbook(("Classes App", new[] {
            new[] { "Title", "Professor", "Minimum Room Size Needed" }, new[] { "314 DATA STRUCTURES", "SMITH A", "30" }
        }), ("Course Reference", new[] {
            new[] { "Course", "Minimum Room Capacity" }, new[] { "314", "100" }
        })), FileKind.Other);
        Assert.Equal(100, Assert.Single(session.Build("classes.xlsx").Placements).Section.MinimumRoomCapacity);
        session.Save("new-reference.xlsx", Workbook(("Course Reference", new[] {
            new[] { "Course", "Minimum Room Capacity" }, new[] { "314", "150" }
        })), FileKind.Other);
        Assert.Equal(150, Assert.Single(session.Build("classes.xlsx").Placements).Section.MinimumRoomCapacity);
    }

    [Fact]
    public void New_matching_preference_file_is_recommended_and_used_after_chat_confirmation()
    {
        var session = NewSession(); session.Save("classes.xlsx", Requests(), FileKind.Other);
        session.Build("classes.xlsx");
        session.Save("new-preferences.xlsx", Workbook(("Scheduling Preferences", new[] {
            new[] { "Prof", "course", "Time 1" }, new[] { "SMITH A", "314 DATA STRUCTURES", "MWF 8am-9am" }
        })), FileKind.Other);
        Assert.True(session.RequiresRebuild); Assert.Equal("new-preferences.xlsx", session.RecommendedFiles().Preferences);
        session.SendChat("Review file updates");
        Assert.Contains("new-preferences.xlsx", LastChat(session));
        session.SendChat("Confirm rebuild with newest files");
        Assert.Contains("Confirmed: rebuilt", LastChat(session));
        Assert.Equal("new-preferences.xlsx", session.LatestPreferences);
        Assert.Equal(TimeSpan.FromHours(8), session.LatestPlan!.Placements[0].Time!.Start);
        Assert.False(session.RequiresRebuild);
    }

    [Fact]
    public void Preferences_for_other_instructors_are_not_automatically_selected()
    {
        var session = NewSession(); session.Save("classes.xlsx", Requests(), FileKind.Other);
        session.Save("unrelated-preferences.xlsx", Workbook(("Scheduling Preferences", new[] {
            new[] { "Prof", "course", "Time 1" }, new[] { "DOE C", "429 ARCHITECTURE", "MWF 8am-9am" }
        })), FileKind.Other);
        Assert.Null(session.RecommendedFiles().Preferences);
    }

    [Fact]
    public void Chat_can_confirm_loaded_files_while_the_current_schedule_needs_rebuilding()
    {
        var session = NewSession(); session.Save("old.xlsx", RevisedClasses("PTF"), FileKind.Other);
        var old = session.Build("old.xlsx");
        session.Save("new.xlsx", RevisedClasses("T"), FileKind.Other);
        Assert.True(session.RequiresRebuild);
        session.SendChat("did you get my updated files?");
        Assert.Contains("new.xlsx", LastChat(session)); Assert.Contains("Rebuild required", LastChat(session));
        Assert.Same(old, session.LatestPlan); Assert.Equal("old.xlsx", session.LatestSource);
        session.SendChat("Review file updates");
        Assert.Same(old, session.LatestPlan); Assert.Contains("Nothing has been rebuilt yet", LastChat(session));
        session.SendChat("yes");
        Assert.Equal("new.xlsx", session.LatestSource); Assert.NotSame(old, session.LatestPlan);
    }

    [Fact]
    public void New_upload_after_review_requires_a_fresh_confirmation()
    {
        var session = NewSession(); session.Save("classes.xlsx", RevisedClasses("PTF"), FileKind.Other);
        var old = session.Build("classes.xlsx"); session.SendChat("Review file updates");
        session.Save("classes.xlsx", RevisedClasses("T"), FileKind.Other);
        session.SendChat("Confirm rebuild with newest files");
        Assert.Contains("changed since that review", LastChat(session)); Assert.Same(old, session.LatestPlan);
    }

    [Fact]
    public void Changed_manual_settings_after_review_require_a_fresh_confirmation()
    {
        var session = NewSession(); session.Save("classes.xlsx", RevisedClasses("PTF"), FileKind.Other);
        var old = session.Build("classes.xlsx"); session.SendChat("Review file updates");
        session.Faculty.Find("SMITH")!.SetTrack(FacultyTrack.Tenure); session.Faculty.Save();
        session.SendChat("Confirm updates");
        Assert.Contains("changed since that review", LastChat(session)); Assert.Same(old, session.LatestPlan);
    }

    [Fact]
    public void Confirmation_without_review_does_not_rebuild()
    {
        var session = NewSession(); session.Save("classes.xlsx", RevisedClasses("PTF"), FileKind.Other);
        session.SendChat("Confirm updates");
        Assert.Null(session.LatestPlan); Assert.Contains("Review the file updates first", LastChat(session));
    }

    [Fact]
    public void Canceling_an_update_keeps_the_saved_files_and_previous_schedule()
    {
        var session = NewSession(); session.Save("old.xlsx", RevisedClasses("PTF"), FileKind.Other);
        var old = session.Build("old.xlsx"); session.Save("new.xlsx", RevisedClasses("T"), FileKind.Other);
        session.SendChat("Review file updates"); session.SendChat("Cancel updates");
        Assert.Same(old, session.LatestPlan); Assert.NotNull(session.FileBytes("new.xlsx")); Assert.True(session.RequiresRebuild);
    }

    [Fact]
    public void Unsupported_files_are_reported_as_reference_only_instead_of_claiming_import_success()
    {
        var session = NewSession(); session.Save("notes.pdf", "%PDF-1.4 notes"u8.ToArray(), FileKind.Other);
        session.SendChat("which files are active?");
        Assert.Contains("notes.pdf", LastChat(session)); Assert.Contains("do not automatically change", LastChat(session));
        Assert.Null(session.LatestPlan);
    }

    [Fact]
    public void Invalid_reference_update_does_not_partially_replace_profiles_or_schedule()
    {
        var session = NewSession(); session.Save("classes.xlsx", RevisedClasses("PTF"), FileKind.Other);
        var old = session.Build("classes.xlsx"); var profile = session.Faculty.Find("SMITH")!;
        session.Save("invalid-reference.xlsx", Workbook(("Faculty Reference", new[] {
            new[] { "Professor", "Tenure or PTF" }, new[] { "SMITH A", "T" }
        }), ("Course Reference", new[] {
            new[] { "Course", "Minimum Room Capacity" }, new[] { "314", "-10" }
        })), FileKind.Other);
        session.SendChat("Review file updates"); session.SendChat("Confirm updates");
        Assert.Contains("did not complete", LastChat(session)); Assert.Same(old, session.LatestPlan);
        Assert.Same(profile, session.Faculty.Find("SMITH")); Assert.Equal(FacultyTrack.Professional, profile.Track);
    }

    [Fact]
    public void Schedule_questions_clear_an_older_pending_rebuild_confirmation()
    {
        var session = NewSession(); session.Save("classes.xlsx", RevisedClasses("PTF"), FileKind.Other);
        var old = session.Build("classes.xlsx"); session.SendChat("Review file updates");
        session.SendChat("where is data structures?"); session.SendChat("Confirm updates");
        Assert.Contains("Review the file updates first", LastChat(session)); Assert.Same(old, session.LatestPlan);
    }

    [Fact]
    public void Upload_order_is_strict_even_for_quick_successive_updates()
    {
        var session = NewSession();
        session.Save("a.xlsx", RevisedClasses("PTF"), FileKind.Other);
        session.Save("b.xlsx", RevisedClasses("T"), FileKind.Other);
        Assert.Equal("b.xlsx", session.RecommendedFiles().Source);
        var files = session.Files(); Assert.True(files[0].Saved > files[1].Saved);
    }
}
