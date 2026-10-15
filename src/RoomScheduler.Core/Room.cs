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

        /// <summary>
        /// Feature codes from the registrar table, e.g. CON (media console),
        /// CWS (continuous writing surface), MC (movable chairs), Wb (whiteboard).
        /// </summary>
        public HashSet<string> Features { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public string Notes { get; set; } = "";

        /// <summary>Supports Lectures Online (recording), from the registrar's list in data/lecture_capture_rooms.csv.</summary>
        public bool LectureCapture { get; set; }

        /// <summary>NSC: non-standard classroom. Doug: avoid.</summary>
        public bool NonStandard => Features.Contains("NSC");

        /// <summary>A departmental room (not general purpose) that only CS can use.</summary>
        public bool DepartmentOnly { get; set; }

        /// <summary>For general purpose rooms CS controls part of the day, when that starts.</summary>
        public TimeSpan? CsOwnsFrom { get; set; }

        /// <summary>Floor from the room number: "2.216" is 2, "116" is 1, "0.128" is the basement.</summary>
        public int Floor
        {
            get
            {
                int dot = RoomNumber.IndexOf('.');
                string lead = dot > 0 ? RoomNumber.Substring(0, dot) : RoomNumber.Substring(0, Math.Min(1, RoomNumber.Length));
                return int.TryParse(lead, out int f) ? f : 1;
            }
        }

        public override string ToString() => $"{DisplayName} (cap {Capacity})";

        /// <summary>
        /// Loads the department's room table (building, room, capacity, features,
        /// notes) plus the GDC rooms from the Gates schedule. GDC rooms that are not
        /// general purpose classrooms are added as department-only rooms.
        /// </summary>
        public static List<Room> LoadWithFeatures(string featuresCsv, string gdcCsv, SchedulingRules rules) =>
            FromCsvText(DataFile.ReadText(featuresCsv), DataFile.ReadText(gdcCsv), rules);

        /// <summary>The same, from the files' text (the website downloads them instead of reading disk).</summary>
        public static List<Room> FromCsvText(string featuresCsvText, string gdcCsvText, SchedulingRules rules) =>
            FromFeatureRows(Csv.Lines(featuresCsvText).Skip(1).Select(Csv.ParseLine).ToList(), gdcCsvText, rules);

        /// <summary>
        /// Marks rooms that can record lectures, from a building,room list the
        /// department keeps. Returns how many rooms were marked.
        /// </summary>
        public static int MarkLectureCapture(List<Room> rooms, string csvPath) =>
            MarkLectureCaptureText(rooms, DataFile.ReadText(csvPath));

        public static int MarkLectureCaptureText(List<Room> rooms, string csvText)
        {
            var names = Csv.Lines(csvText).Skip(1)
                .Select(Csv.ParseLine)
                .Where(f => f.Count >= 2 && f[0].Trim().Length > 0)
                .Select(f => $"{f[0].Trim()} {f[1].Trim()}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            int marked = 0;
            foreach (var room in rooms)
            {
                room.LectureCapture = names.Contains(room.DisplayName);
                if (room.LectureCapture) marked++;
            }
            return marked;
        }

        /// <summary>
        /// The same room table as a spreadsheet (a sheet with Building, Room, Size,
        /// Features and Notes columns), for when the registrar sends a new one.
        /// Returns null if no sheet looks like a room table.
        /// </summary>
        public static List<Room>? FromFeatureWorkbook(List<Xlsx.Sheet> sheets, string gdcCsvText, SchedulingRules rules)
        {
            foreach (var sheet in sheets)
            {
                int hr = sheet.Rows.FindIndex(r => r.Any(c => c.Trim().Equals("Features", StringComparison.OrdinalIgnoreCase)));
                if (hr < 0) continue;

                var h = sheet.Rows[hr].Select(c => c.Trim().ToLowerInvariant()).ToList();
                int b = h.IndexOf("building"), r = h.IndexOf("room"), cap = h.FindIndex(x => x is "size" or "capacity"),
                    feat = h.IndexOf("features"), notes = h.IndexOf("notes");
                if (b < 0 || r < 0 || cap < 0) continue;

                var rows = sheet.Rows.Skip(hr + 1)
                    .Select(row => new List<string>
                    {
                        Get(row, b), RoomNumberText(Get(row, r)), Get(row, cap), Get(row, feat), Get(row, notes)
                    })
                    .ToList();
                return FromFeatureRows(rows, gdcCsvText, rules);
            }
            return null;

            static string Get(List<string> row, int i) => i >= 0 && i < row.Count ? row[i].Trim() : "";
        }

        /// <summary>Excel stores room "2.210" as the number 2.21; put the trailing zero back.</summary>
        private static string RoomNumberText(string raw) =>
            raw.Contains('.') && double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v)
                ? v.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)
                : raw;

        private static List<Room> FromFeatureRows(List<List<string>> featureRows, string gdcCsvText, SchedulingRules rules)
        {
            var rooms = new List<Room>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            {
                foreach (var f in featureRows)
                {
                    if (f.Count < 3 || !int.TryParse(f[2], out int cap)) continue;
                    if (!seen.Add($"{f[0].Trim()}|{f[1].Trim()}")) continue;

                    string notes = f.Count > 4 ? f[4].Trim() : "";
                    rooms.Add(new Room
                    {
                        Building = f[0].Trim(),
                        RoomNumber = f[1].Trim(),
                        Capacity = cap,
                        Features = new HashSet<string>(
                            (f.Count > 3 ? f[3] : "").Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                            StringComparer.OrdinalIgnoreCase),
                        Notes = notes,
                        CsOwnsFrom = notes.Contains("Computer Science owns", StringComparison.OrdinalIgnoreCase)
                            ? rules.CsOwnsGdcFrom
                            : null
                    });
                }
            }

            {
                foreach (var line in Csv.Lines(gdcCsvText).Skip(1))
                {
                    var f = Csv.ParseLine(line);
                    if (f.Count < 4 || !int.TryParse(f[1], out int cap)) continue;
                    if (!seen.Add($"GDC|{f[0].Trim()}")) continue;

                    rooms.Add(new Room
                    {
                        Building = "GDC",
                        RoomNumber = f[0].Trim(),
                        Capacity = cap,
                        Notes = f[2].Trim(),
                        DepartmentOnly = true
                    });
                }
            }

            return rooms;
        }

    }
}
