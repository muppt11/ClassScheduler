using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RoomScheduler
{
    /// <summary>
    /// Where departments have actually held class in past semesters, read from
    /// course sections extracted out of the printed UT Austin course offerings.
    ///
    /// This is deliberately building level rather than room level. Departments
    /// mostly teach in their own buildings, and most of those rooms are not in
    /// the registrar's general purpose classroom list, so matching on an exact
    /// past room would miss almost every time. Which buildings a department
    /// uses is a far denser signal and survives the scanning errors.
    /// </summary>
    public class CourseHistory
    {
        // department -> building -> number of sections seen there
        private readonly Dictionary<string, Dictionary<string, int>> _usage =
            new(StringComparer.OrdinalIgnoreCase);

        public int RecordCount { get; private set; }

        public IEnumerable<string> Departments => _usage.Keys;

        /// <summary>An empty history. Scheduling still works, just without the preference.</summary>
        public static CourseHistory Empty => new();

        public static CourseHistory LoadFromCsv(string path)
        {
            path = DataFile.Resolve(path);
            var history = new CourseHistory();

            if (!File.Exists(path))
            {
                Console.WriteLine($"Note: no course history at '{path}'. Room preference is disabled.");
                return history;
            }

            var lines = File.ReadAllLines(path);
            if (lines.Length < 2)
                return history;

            var headers = Csv.ParseLine(lines[0]).Select(h => h.Trim()).ToList();
            int courseIdx = headers.FindIndex(h => h.Equals("course", StringComparison.OrdinalIgnoreCase));
            int buildingIdx = headers.FindIndex(h => h.Equals("building", StringComparison.OrdinalIgnoreCase));

            if (courseIdx == -1 || buildingIdx == -1)
            {
                Console.WriteLine("Note: course history is missing a 'course' or 'building' column.");
                return history;
            }

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                    continue;

                var fields = Csv.ParseLine(lines[i]);
                if (fields.Count <= Math.Max(courseIdx, buildingIdx))
                    continue;

                string department = DepartmentOf(fields[courseIdx]);
                string building = fields[buildingIdx].Trim();

                if (department.Length == 0 || building.Length == 0)
                    continue;

                history.Record(department, building);
            }

            return history;
        }

        private void Record(string department, string building)
        {
            if (!_usage.TryGetValue(department, out var buildings))
            {
                buildings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                _usage[department] = buildings;
            }

            buildings.TryGetValue(building, out int count);
            buildings[building] = count + 1;
            RecordCount++;
        }

        /// <summary>How many past sections this department held in this building.</summary>
        public int BuildingUsage(string department, string building)
        {
            if (_usage.TryGetValue(department, out var buildings) &&
                buildings.TryGetValue(building, out int count))
            {
                return count;
            }

            return 0;
        }

        /// <summary>The buildings a department uses most, busiest first.</summary>
        public IEnumerable<(string Building, int Count)> TopBuildings(string department, int take)
        {
            if (!_usage.TryGetValue(department, out var buildings))
                return Enumerable.Empty<(string, int)>();

            return buildings
                .OrderByDescending(entry => entry.Value)
                .Take(take)
                .Select(entry => (entry.Key, entry.Value));
        }

        /// <summary>Pulls "ARI" out of "ARI 310L".</summary>
        private static string DepartmentOf(string courseName)
        {
            courseName = courseName.Trim();
            int space = courseName.IndexOf(' ');
            return space > 0 ? courseName.Substring(0, space) : courseName;
        }
    }
}
