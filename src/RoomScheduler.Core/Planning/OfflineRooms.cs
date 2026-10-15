using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RoomScheduler
{
    /// <summary>
    /// Buildings and rooms that are off limits for now (renovation, closed, or
    /// just not ours this semester). The scheduler never puts a class in one.
    /// A whole building can be offline, or single rooms in it. Saved as JSON so
    /// it stays set between runs; CPE starts offline.
    /// </summary>
    public class OfflineRooms
    {
        public HashSet<string> Buildings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Single rooms, as "GDC 2.216".</summary>
        public HashSet<string> Rooms { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        private Action<string>? _save;
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        /// <summary>The starting list when nothing has been saved yet.</summary>
        public static OfflineRooms Default() => new() { Buildings = new(StringComparer.OrdinalIgnoreCase) { "CPE" } };

        /// <summary>From saved JSON (null for the default list). Changes are handed to <paramref name="save"/>.</summary>
        public static OfflineRooms FromJson(string? json, Action<string>? save)
        {
            OfflineRooms result;
            if (!string.IsNullOrWhiteSpace(json))
            {
                var saved = JsonSerializer.Deserialize<OfflineRooms>(json) ?? Default();
                result = new OfflineRooms
                {
                    Buildings = new(saved.Buildings, StringComparer.OrdinalIgnoreCase),
                    Rooms = new(saved.Rooms, StringComparer.OrdinalIgnoreCase)
                };
            }
            else
            {
                result = Default();
            }

            result._save = save;
            return result;
        }

        public bool IsOffline(Room room) =>
            Buildings.Contains(room.Building) || Rooms.Contains(room.DisplayName);

        public bool BuildingOffline(string building) => Buildings.Contains(building);

        /// <summary>Checking a building takes every room in it offline; unchecking brings them all back.</summary>
        public void SetBuilding(string building, bool offline, IEnumerable<Room> allRooms)
        {
            var inBuilding = allRooms.Where(r => r.Building.Equals(building, StringComparison.OrdinalIgnoreCase)).ToList();
            Rooms.RemoveWhere(name => inBuilding.Any(r => r.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase)));
            if (offline) Buildings.Add(building);
            else Buildings.Remove(building);
            Save();
        }

        /// <summary>
        /// One room. Bringing a room back in an offline building turns the building
        /// check off and keeps its other rooms offline one by one.
        /// </summary>
        public void SetRoom(Room room, bool offline, IEnumerable<Room> allRooms)
        {
            if (!offline && Buildings.Contains(room.Building))
            {
                Buildings.Remove(room.Building);
                foreach (var other in allRooms.Where(r => r.Building.Equals(room.Building, StringComparison.OrdinalIgnoreCase) && r != room))
                    Rooms.Add(other.DisplayName);
            }

            if (offline) Rooms.Add(room.DisplayName);
            else Rooms.Remove(room.DisplayName);

            // Every room in the building checked one by one = the building is offline.
            var inBuilding = allRooms.Where(r => r.Building.Equals(room.Building, StringComparison.OrdinalIgnoreCase)).ToList();
            if (inBuilding.Count > 0 && inBuilding.All(r => Rooms.Contains(r.DisplayName)))
            {
                foreach (var r in inBuilding) Rooms.Remove(r.DisplayName);
                Buildings.Add(room.Building);
            }

            Save();
        }

        public void Save()
        {
            _save?.Invoke(JsonSerializer.Serialize(new { Buildings = Buildings.OrderBy(b => b), Rooms = Rooms.OrderBy(r => r) }, Json));
        }
    }
}
