using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RoomScheduler;

public partial class PlanningSession
{
    private const string AddedRoomsKey = "added-rooms";
    private string _roomsTableSource = "the built-in rooms table";
    public int AddedRoomCount { get; private set; }
    private HashSet<string> _lectureCaptureNames = new(StringComparer.OrdinalIgnoreCase);

    private record AddedRoom(string Building, string RoomNumber, int Capacity, List<string> Features,
        string Notes, bool LectureCapture, bool DepartmentOnly)
    {
        public Room ToRoom() => new()
        {
            Building = Building, RoomNumber = RoomNumber, Capacity = Capacity,
            Features = new(Features, StringComparer.OrdinalIgnoreCase), Notes = Notes,
            LectureCapture = LectureCapture, DepartmentOnly = DepartmentOnly
        };
    }

    private List<AddedRoom> ReadAddedRooms() =>
        JsonSerializer.Deserialize<List<AddedRoom>>(_store.Get(AddedRoomsKey) ?? "[]") ?? new();

    private static string NormalizeRoomNumber(string number) => Regex.Replace(number.Trim().ToUpperInvariant(),
        @"\.(\d{1,3})(?=[A-Z]?$)", m => "." + m.Groups[1].Value.PadRight(3, '0'));

    private static bool SameRoom(Room first, Room second) =>
        first.Building.Equals(second.Building, StringComparison.OrdinalIgnoreCase) &&
        NormalizeRoomNumber(first.RoomNumber) == NormalizeRoomNumber(second.RoomNumber);

    /// <summary>Save a new room before updating the inventory or invalidating the current plan.</summary>
    public Room AddRoom(string building, string roomNumber, int capacity, string features = "",
        bool lectureCapture = false, bool departmentOnly = false, string notes = "")
    {
        building = building.Trim().ToUpperInvariant();
        roomNumber = roomNumber.Trim().ToUpperInvariant();
        if (!Regex.IsMatch(building, @"^[A-Z]{2,5}$"))
            throw new ArgumentException("Enter a building code of 2–5 letters, such as GDC or RLP.");
        if (!Regex.IsMatch(roomNumber, @"^\d+(?:\.\d{1,3})?[A-Z]?$"))
            throw new ArgumentException("Enter a room number such as 2.216, 116, or 2.216A, without the building code.");
        if (capacity <= 0) throw new ArgumentException("Room capacity must be at least 1 seat.");
        roomNumber = NormalizeRoomNumber(roomNumber);
        var added = new AddedRoom(building, roomNumber, capacity,
            Regex.Split(features.Trim(), @"[\s,;/]+").Where(f => f.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            notes.Trim(), lectureCapture, departmentOnly);
        var room = added.ToRoom();
        var saved = ReadAddedRooms();
        if (Rooms.Any(r => SameRoom(r, room)) || saved.Any(r => SameRoom(r.ToRoom(), room)))
            throw new ArgumentException($"{room.DisplayName} already exists. Enter a different room number.");

        saved.Add(added);
        _store.Set(AddedRoomsKey, JsonSerializer.Serialize(saved));
        room.LectureCapture |= _lectureCaptureNames.Contains(room.DisplayName);
        Rooms.Add(room);
        AddedRoomCount++;
        LectureCaptureRooms = Rooms.Count(r => r.LectureCapture);
        MarkInputsChanged();
        return room;
    }

    private void LoadAddedRooms(HashSet<string> lectureCaptureNames)
    {
        _lectureCaptureNames = lectureCaptureNames;
        AddedRoomCount = 0;
        foreach (var saved in ReadAddedRooms())
        {
            var room = saved.ToRoom();
            // A newer registrar inventory wins if it now includes a manually added room.
            if (Rooms.Any(r => SameRoom(r, room))) continue;
            room.LectureCapture |= lectureCaptureNames.Contains(room.DisplayName);
            Rooms.Add(room);
            AddedRoomCount++;
        }
    }
}
