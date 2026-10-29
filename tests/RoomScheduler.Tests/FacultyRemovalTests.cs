using RoomScheduler;
using Xunit;

namespace RoomScheduler.Tests;

public partial class FileUploadTests
{
    [Fact]
    public void Adding_back_a_professor_persists_and_restores_their_classes_on_rebuild()
    {
        var store = new MemoryStore(); var session = NewSession(store);
        session.Save("classes.xlsx", Requests(), FileKind.Requests); session.Build("classes.xlsx");
        var original = session.Faculty.Find("SMITH")!;
        original.ExternalNotes = "deleted note";
        original.GroundFloorOnly = true;
        original.Complaints.Add(new Complaint { Text = "deleted complaint" });
        session.Faculty.Save();
        session.Faculty.RemovePermanently("SMITH"); session.Build("classes.xlsx");
        var removed = Assert.Single(session.Faculty.RemovedProfiles);
        Assert.Equal("SMITH, A", removed.DisplayName);
        Assert.DoesNotContain(session.LatestPlan!.Placements, p => p.Section.Surname == "SMITH");

        var reopened = NewSession(store); reopened.Build("classes.xlsx");
        var restored = reopened.Faculty.AddBack(" smith ");
        Assert.Equal("SMITH, A", restored.DisplayName);
        Assert.True(reopened.RequiresRebuild); Assert.False(reopened.Faculty.IsRemoved("SMITH"));
        Assert.Empty(reopened.Faculty.RemovedProfiles); Assert.Same(restored, reopened.Faculty.Find("SMITH"));
        Assert.Empty(restored.Complaints); Assert.Equal("", restored.ExternalNotes); Assert.False(restored.HasAccommodations);

        var again = NewSession(store); Assert.NotNull(again.Faculty.Find("SMITH"));
        var plan = again.Build("classes.xlsx");
        Assert.Contains(plan.Placements, p => p.Section.Surname == "SMITH");
        Assert.Equal(FacultyTrack.Professional, again.Faculty.Find("SMITH")!.Track);
        Assert.Empty(again.Faculty.Find("SMITH")!.Complaints);
        Assert.DoesNotContain("deleted", store.Get("faculty")!);
        again.Faculty.RemovePermanently("SMITH");
        Assert.DoesNotContain(again.Build("classes.xlsx").Placements, p => p.Section.Surname == "SMITH");
    }

    [Fact]
    public void Failed_or_invalid_add_back_does_not_change_the_removed_status_or_plan()
    {
        var store = new FailingStore(); var session = NewSession(store);
        session.Save("classes.xlsx", Requests(), FileKind.Requests); session.Build("classes.xlsx");
        session.Faculty.RemovePermanently("SMITH"); var plan = session.Build("classes.xlsx");
        string? before = store.Inner.Get("faculty");
        Assert.Throws<ArgumentException>(() => session.Faculty.AddBack("UNKNOWN"));
        Assert.Throws<ArgumentException>(() => session.Faculty.AddBack("JONES"));
        store.FailNextKey = "faculty";
        Assert.Throws<InvalidOperationException>(() => session.Faculty.AddBack("SMITH"));
        Assert.True(session.Faculty.IsRemoved("SMITH")); Assert.Null(session.Faculty.Find("SMITH"));
        Assert.Equal(before, store.Inner.Get("faculty")); Assert.False(session.RequiresRebuild);
        Assert.Same(plan, session.LatestPlan);
    }

    [Fact]
    public void Older_removal_markers_can_be_added_back_without_a_saved_display_name()
    {
        string? saved = null;
        var faculty = new FacultyStore("[{\"Surname\":\"SMITH\",\"Removed\":true}]", value => saved = value);
        Assert.Equal("SMITH", Assert.Single(faculty.RemovedProfiles).Surname);
        var restored = faculty.AddBack("smith");
        Assert.Equal("SMITH", restored.DisplayName); Assert.Empty(faculty.RemovedProfiles);
        Assert.NotNull(new FacultyStore(saved, null).Find("SMITH"));
        Assert.Throws<ArgumentException>(() => faculty.AddBack("SMITH"));
    }

    [Fact]
    public void Permanent_removal_deletes_profile_details_and_survives_reopening_and_reimports()
    {
        var store = new MemoryStore(); var session = NewSession(store);
        session.Save("classes.xlsx", Requests(), FileKind.Requests);
        session.Build("classes.xlsx");
        var profile = session.Faculty.Find("SMITH")!;
        profile.ExternalNotes = "private notes";
        profile.AccommodationNotes = "private accommodations";
        profile.Complaints.Add(new Complaint { Text = "private complaint" });
        session.Faculty.Save(); session.Build("classes.xlsx");

        session.Faculty.RemovePermanently(" smith ");
        Assert.Null(session.Faculty.Find("SMITH"));
        Assert.DoesNotContain(session.Faculty.All, p => p.Surname == "SMITH");
        Assert.True(session.RequiresRebuild);
        Assert.DoesNotContain("private", store.Get("faculty")!);
        Assert.Null(session.Conversation.Proposal);
        Assert.Throws<InvalidOperationException>(() => session.Faculty.GetOrAdd("SMITH", "SMITH A"));

        var reopened = NewSession(store);
        reopened.Save("updated.xlsx", Requests(), FileKind.Other);
        reopened.Save("reference.xlsx", Workbook(("Faculty Reference", new[] {
            new[] { "Professor", "Tenure or PTF" }, new[] { "SMITH A", "t" }
        })), FileKind.Other);
        var plan = reopened.Build("updated.xlsx");
        Assert.Null(reopened.Faculty.Find("smith"));
        Assert.True(reopened.Faculty.IsRemoved("smith"));
        Assert.DoesNotContain(plan.Placements.Concat(plan.Discussions), p => p.Section.Surname == "SMITH");
        Assert.Contains(plan.Placements, p => p.Section.Surname == "JONES");
        Assert.DoesNotContain(plan.GeneralFlags, f => f.Message.Contains("SMITH", StringComparison.OrdinalIgnoreCase));
        reopened.Build("updated.xlsx");
        Assert.True(NewSession(store).Faculty.IsRemoved("SMITH"));
        Assert.NotNull(reopened.FileBytes("classes.xlsx"));
    }

    [Fact]
    public void Removed_professors_are_absent_from_export_and_a_schedule_can_have_all_professors_removed()
    {
        var session = NewSession(); session.Save("classes.xlsx", Requests(), FileKind.Requests);
        session.Build("classes.xlsx");
        session.Faculty.RemovePermanently("SMITH");
        var plan = session.Build("classes.xlsx");
        var bytes = PlanExport.Workbook(plan, session.Rooms);
        using var stream = new MemoryStream(bytes);
        var sheets = Xlsx.Read(stream);
        Assert.DoesNotContain(sheets.SelectMany(s => s.Rows).SelectMany(r => r), cell => cell.Contains("SMITH", StringComparison.OrdinalIgnoreCase));
        session.Faculty.RemovePermanently("JONES");
        var empty = session.Build("classes.xlsx");
        Assert.Empty(empty.Placements); Assert.Empty(empty.Discussions); Assert.Empty(session.Faculty.All);
    }

    [Fact]
    public void Failed_removal_save_keeps_the_profile_and_current_plan_usable()
    {
        var store = new FailingStore(); var session = NewSession(store);
        session.Save("classes.xlsx", Requests(), FileKind.Requests);
        var plan = session.Build("classes.xlsx");
        var profile = session.Faculty.Find("SMITH"); var saved = store.Inner.Get("faculty");
        store.FailNextKey = "faculty";
        Assert.Throws<InvalidOperationException>(() => session.Faculty.RemovePermanently("SMITH"));
        Assert.Same(profile, session.Faculty.Find("SMITH")); Assert.False(session.Faculty.IsRemoved("SMITH"));
        Assert.Equal(saved, store.Inner.Get("faculty")); Assert.False(session.RequiresRebuild);
        Assert.Same(plan, session.LatestPlan);
    }

    [Fact]
    public void Invalid_or_repeated_removal_does_not_change_saved_profiles()
    {
        var store = new MemoryStore(); var session = NewSession(store);
        session.Save("classes.xlsx", Requests(), FileKind.Requests); session.Build("classes.xlsx");
        string? before = store.Get("faculty");
        Assert.Throws<ArgumentException>(() => session.Faculty.RemovePermanently("UNKNOWN"));
        Assert.Equal(before, store.Get("faculty")); Assert.False(session.RequiresRebuild);
        session.Faculty.RemovePermanently("SMITH"); before = store.Get("faculty");
        Assert.Throws<ArgumentException>(() => session.Faculty.RemovePermanently("SMITH"));
        Assert.Equal(before, store.Get("faculty"));
        Assert.NotNull(session.Faculty.Find("JONES"));
    }
}
