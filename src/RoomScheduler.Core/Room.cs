using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RoomScheduler
{
    /// <summary>
    /// A physical classroom that courses can be assigned to.
    /// </summary>
    public class Room
    {
        public string Building { get; set; } = "";
        public string RoomNumber { get; set; } = "";
        public int Capacity { get; set; }

        public string DisplayName => $"{Building} {RoomNumber}";

        public override string ToString() => $"{DisplayName} (cap {Capacity})";

        /// <summary>
        /// Loads rooms from a CSV export of the UT Austin registrar's
        /// "Classroom Types & Rooms" report (GPC sheet). Expects a header
        /// row containing the columns "BLDG ACRONYM", "ROOM #", and "CAPACITY".
        /// </summary>
        public static List<Room> LoadFromCsv(string path)
        {
            path = DataFile.Resolve(path);
            var rooms = new List<Room>();

            // The registrar export can list the same physical room twice (SZB 1.510
            // appears on two rows). Without this guard each copy becomes a separate
            // Room and the scheduler happily double-books the real room.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!File.Exists(path))
            {
                Console.WriteLine($"Warning: room CSV not found at '{path}'. Returning empty room list.");
                return rooms;
            }

            var lines = File.ReadAllLines(path);
            if (lines.Length == 0)
                return rooms;

            var headers = Csv.ParseLine(lines[0]).Select(h => h.Trim().Trim('"')).ToList();

            int buildingIdx = headers.FindIndex(h => h.Equals("BLDG ACRONYM", StringComparison.OrdinalIgnoreCase));
            int roomIdx = headers.FindIndex(h => h.Equals("ROOM #", StringComparison.OrdinalIgnoreCase));
            int capacityIdx = headers.FindIndex(h => h.Equals("CAPACITY", StringComparison.OrdinalIgnoreCase));

            if (buildingIdx == -1 || roomIdx == -1 || capacityIdx == -1)
            {
                Console.WriteLine("Warning: CSV is missing one of the expected columns " +
                    "(\"BLDG ACRONYM\", \"ROOM #\", \"CAPACITY\"). Returning empty room list.");
                return rooms;
            }

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                    continue;

                var fields = Csv.ParseLine(lines[i]);
                if (fields.Count <= Math.Max(buildingIdx, Math.Max(roomIdx, capacityIdx)))
                    continue;

                string building = fields[buildingIdx].Trim().Trim('"');
                string roomNumber = fields[roomIdx].Trim().Trim('"');
                string capacityRaw = fields[capacityIdx].Trim().Trim('"');

                if (string.IsNullOrWhiteSpace(building) || string.IsNullOrWhiteSpace(roomNumber))
                    continue;

                if (!int.TryParse(capacityRaw, out int capacity))
                    continue;

                if (!seen.Add($"{building}|{roomNumber}"))
                    continue;

                rooms.Add(new Room
                {
                    Building = building,
                    RoomNumber = roomNumber,
                    Capacity = capacity
                });
            }

            return rooms;
        }

    }
}
