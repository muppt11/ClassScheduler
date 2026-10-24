using RoomScheduler;
using Xunit;

namespace RoomScheduler.Tests;

/// <summary>
/// The department's real spreadsheets, when they are on this computer in
/// data/private. They are never committed, so these pass without checking
/// anything elsewhere (for example on GitHub).
/// </summary>
public class RealFilesTests
{
    private static string Private(string name) => Path.Combine(DataFile.Root(), "data", "private", name);

    private static byte[]? Read(string name) =>
        File.Exists(Private(name)) ? File.ReadAllBytes(Private(name)) : null;

    [Fact]
    public void Real_files_are_recognized_and_build_a_schedule()
    {
        var requests = Read("fall26_requests.xlsx");
        var past = Read("fall25_actual.xlsx");
        var rooms = Read("gpc_rooms_features.xlsx");
        if (requests == null || past == null || rooms == null) return;

        var s = new PlanningSession();
        s.Initialize(new MemoryStore(), DataFiles.FromDataFolder());

        Assert.Equal(FileKind.Requests, s.Save("fall26_requests.xlsx", requests, FileKind.Requests).Kind);
        Assert.Equal(FileKind.PastSchedule, s.Save("fall25_actual.xlsx", past, FileKind.PastSchedule).Kind);
        Assert.Equal(FileKind.Rooms, s.Save("gpc_rooms_features.xlsx", rooms, FileKind.Rooms).Kind);

        var gates = Read("gates_template.xlsx");
        if (gates != null)
            Assert.Equal(FileKind.Unknown, s.Save("gates_template.xlsx", gates, FileKind.Requests).Kind);

        var plan = s.Build("fall26_requests.xlsx");
        Assert.All(plan.Placements.Where(p => !p.Section.Online), p => Assert.NotNull(p.Room));
        Assert.DoesNotContain(plan.Placements, p => p.Room?.Building == "CPE");

        // No room or instructor is double booked.
        var placed = plan.Placements.Where(p => p.Time != null).ToList();
        foreach (var a in placed)
            foreach (var b in placed.Where(b => b != a && b.Time!.Overlaps(a.Time!)))
            {
                Assert.False(a.Room != null && a.Room == b.Room, $"{a.Room?.DisplayName} double booked");
                Assert.False(!a.Section.InstructorMissing && a.Section.Surname == b.Section.Surname, $"{a.Section.Surname} double booked");
            }
    }

    [Fact]
    public void Real_Lectures_Online_list_is_recognized()
    {
        var list = Read("lectures_online.xlsx");
        if (list == null) return;

        var s = new PlanningSession();
        s.Initialize(new MemoryStore(), DataFiles.FromDataFolder());
        var info = s.Save("lectures_online.xlsx", list, FileKind.Other);

        Assert.Equal(FileKind.LectureCapture, info.Kind);
        Assert.True(s.LectureCaptureRooms > 100);
    }
    [Fact]
    public void Older_sample_with_repeated_instructor_times_places_every_in_person_section()
    {
        var requests = Read("fall25_requests.xlsx"); var past = Read("fall25_actual.xlsx");
        if (requests == null || past == null) return;
        var session = new PlanningSession(); session.Initialize(new MemoryStore(), DataFiles.FromDataFolder());
        session.Save("requests.xlsx", requests, FileKind.Requests);
        session.Save("history.xlsx", past, FileKind.PastSchedule);
        var plan = session.Build("requests.xlsx");
        Assert.All(plan.Placements.Where(p => !p.Section.Online), p => Assert.NotNull(p.Room));
        var placed = plan.Placements.Where(p => p.Time != null).ToList();
        foreach (var p in placed)
            foreach (var other in placed.Where(o => o != p && o.Time!.Overlaps(p.Time!)))
            {
                Assert.False(p.Room != null && p.Room == other.Room);
                Assert.False(!p.Section.InstructorMissing && p.Section.Surname == other.Section.Surname);
            }
    }

}
