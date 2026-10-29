using RoomScheduler;
using Xunit;

namespace RoomScheduler.Tests;

public partial class FileUploadTests
{
    [Fact]
    public void Manually_added_room_keeps_details_after_reopening_and_inventory_uploads()
    {
        var store = new MemoryStore(); var session = NewSession(store);
        var added = session.AddRoom(" abc ", " 2.21a ", 45, "CON, Wb / CWS Wb", true, true, " New teaching room ");
        Assert.Equal("ABC 2.210A", added.DisplayName); Assert.Equal(3, added.Features.Count);
        Assert.Equal("New teaching room", added.Notes); Assert.Equal(1, session.AddedRoomCount);
        var reopened = NewSession(store);
        reopened.Save("rooms.xlsx", RoomsTable(), FileKind.Rooms);
        reopened.Save("capture.xlsx", LectureList(), FileKind.LectureCapture);
        var room = Assert.Single(reopened.Rooms, r => r.DisplayName == "ABC 2.210A");
        Assert.Equal(45, room.Capacity); Assert.True(room.LectureCapture); Assert.True(room.DepartmentOnly);
        Assert.Contains("wb", room.Features, room.Features.Comparer); Assert.Equal("New teaching room", room.Notes);
        Assert.Contains("1 added room", reopened.RoomsSource);
        Assert.Equal(reopened.Rooms.Count(r => r.LectureCapture), reopened.LectureCaptureRooms);
        reopened.Delete("rooms.xlsx"); reopened.Delete("capture.xlsx");
        Assert.Single(reopened.Rooms, r => r.DisplayName == room.DisplayName);
    }

    [Fact]
    public void Duplicate_rooms_are_rejected_including_case_and_decimal_formatting()
    {
        var session = NewSession(); var existing = session.Rooms.First(); int before = session.Rooms.Count;
        Assert.Throws<ArgumentException>(() => session.AddRoom(existing.Building.ToLowerInvariant(), existing.RoomNumber, 40));
        Assert.Equal(before, session.Rooms.Count);
        session.AddRoom("ABC", "1.1", 35);
        Assert.Throws<ArgumentException>(() => session.AddRoom("abc", "1.100", 50));
        Assert.Equal(before + 1, session.Rooms.Count); Assert.Equal(1, session.AddedRoomCount);
    }

    [Theory]
    [InlineData("", "1.100", 30)]
    [InlineData("GDC", "", 30)]
    [InlineData("G DC", "1.100", 30)]
    [InlineData("GDC", "GDC 1.100", 30)]
    [InlineData("GDC", "1.12345", 30)]
    [InlineData("ABC", "1.100", 0)]
    [InlineData("ABC", "1.100", -1)]
    public void Invalid_new_rooms_do_not_change_storage_or_inventory(string building, string number, int capacity)
    {
        var store = new MemoryStore(); var session = NewSession(store); int before = session.Rooms.Count;
        Assert.Throws<ArgumentException>(() => session.AddRoom(building, number, capacity));
        Assert.Equal(before, session.Rooms.Count); Assert.Null(store.Get("added-rooms"));
    }

    [Fact]
    public void Adding_a_room_requires_rebuilding_before_previewing_changes()
    {
        var session = NewSession(); session.Save("classes.xlsx", Requests(), FileKind.Requests);
        var plan = session.Build("classes.xlsx"); Assert.False(session.RequiresRebuild);
        var room = session.AddRoom("ABC", "1.100", 150);
        Assert.True(session.RequiresRebuild);
        Assert.Throws<InvalidOperationException>(() => session.ProposeMove(plan.Placements[0], room, plan.Placements[0].Time!, false));
        session.Build("classes.xlsx"); Assert.False(session.RequiresRebuild);
    }

    [Fact]
    public void A_manually_added_room_is_used_by_the_scheduler_when_it_is_the_only_room_large_enough()
    {
        var session = NewSession(); int size = session.Rooms.Max(r => r.Capacity) + 1;
        var room = session.AddRoom("GDC", "99.999", size + 20);
        session.Save("large.xlsx", Workbook(("Classes App", new[] {
            new[] { "Title", "Professor", "Minimum Room Size Needed" },
            new[] { "314 DATA STRUCTURES", "SMITH A", size.ToString() }
        })), FileKind.Requests);
        var placement = Assert.Single(session.Build("large.xlsx").Placements);
        Assert.Same(room, placement.Room); Assert.NotNull(placement.Time);
    }

    [Fact]
    public void A_failed_room_save_does_not_change_the_inventory_or_invalidate_the_plan()
    {
        var store = new FailingStore(); var session = NewSession(store);
        session.Save("classes.xlsx", Requests(), FileKind.Requests); session.Build("classes.xlsx");
        int count = session.Rooms.Count; string source = session.RoomsSource;
        store.FailNextKey = "added-rooms";
        Assert.Throws<InvalidOperationException>(() => session.AddRoom("ABC", "1.100", 40));
        Assert.Equal(count, session.Rooms.Count); Assert.Equal(source, session.RoomsSource);
        Assert.Equal(0, session.AddedRoomCount); Assert.False(session.RequiresRebuild);
        Assert.Null(store.Inner.Get("added-rooms"));
    }

    [Fact]
    public void Uploaded_inventory_can_supply_details_for_an_added_room_without_creating_a_duplicate()
    {
        var session = NewSession(); session.AddRoom("ABC", "1.100", 35, "Wb");
        session.Save("rooms.xlsx", Workbook(("GPC Rooms", new[] {
            new[] { "Building", "Room", "Capacity", "Features" },
            new[] { "ABC", "1.100", "80", "CON" }
        })), FileKind.Rooms);
        Assert.Equal(80, Assert.Single(session.Rooms, r => r.DisplayName == "ABC 1.100").Capacity);
        session.Delete("rooms.xlsx");
        Assert.Equal(35, Assert.Single(session.Rooms, r => r.DisplayName == "ABC 1.100").Capacity);
    }

    [Fact]
    public void Added_room_in_an_offline_building_stays_offline()
    {
        var session = NewSession(); var room = session.AddRoom("CPE", "99.999", 40);
        Assert.True(session.Rules.Offline.IsOffline(room));
    }

    [Fact]
    public void Added_rooms_also_inherit_the_active_lecture_capture_list()
    {
        var store = new MemoryStore(); var data = new DataFiles("", "", "building,room\nABC,1.100", "", "");
        var session = new PlanningSession(); session.Initialize(store, data);
        var room = session.AddRoom("ABC", "1.1", 40);
        Assert.True(room.LectureCapture); Assert.Equal(1, session.LectureCaptureRooms);
        var reopened = new PlanningSession(); reopened.Initialize(store, data);
        Assert.True(Assert.Single(reopened.Rooms).LectureCapture);
    }
}
