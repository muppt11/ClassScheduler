using System;
using System.Collections.Generic;
using System.Linq;

namespace RoomScheduler;

/// <summary>Optional, explicitly named templates; never infer baselines from CAP1/CAP2.</summary>
public static class ReferenceSheets
{
    public static bool HasReferences(IEnumerable<Xlsx.Sheet> sheets) =>
        sheets.Any(s => s.Name.Equals("Course Reference", StringComparison.OrdinalIgnoreCase) ||
                        s.Name.Equals("Faculty Reference", StringComparison.OrdinalIgnoreCase));

    public static void Apply(IEnumerable<Xlsx.Sheet> sheets, DepartmentWorkbook workbook, FacultyStore faculty)
    {
        foreach (var sheet in sheets.Where(s => HasReferences(new[] { s })))
        {
            if (sheet.Rows.Count == 0) throw new ArgumentException($"{sheet.Name} is empty.");
            var headers = sheet.Rows[0];
            int Col(string name) => headers.FindIndex(h => h.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
            bool course = sheet.Name.Equals("Course Reference", StringComparison.OrdinalIgnoreCase);
            if (Col(course ? "Course" : "Professor") < 0 ||
                (course ? Col("Minimum Room Capacity") < 0 && Col("Preferred Room Capacity") < 0 : Col("Tenure or PTF") < 0))
                throw new ArgumentException($"Invalid {sheet.Name} headers; see the reference template in README.");
            foreach (var row in sheet.Rows.Skip(1))
            {
                string Get(string name) { int c = Col(name); return c >= 0 && c < row.Count ? row[c].Trim() : ""; }
                if (course)
                {
                    string key = Get("Course"), semester = Get("Semester");
                    if (key.Length == 0 || (semester.Length > 0 && !semester.Equals(workbook.Semester, StringComparison.OrdinalIgnoreCase))) continue;
                    int Capacity(string name)
                    {
                        string raw = Get(name);
                        if (raw.Length == 0) return 0;
                        if (!int.TryParse(raw, out int v) || v < 0) throw new ArgumentException($"Invalid {name} for {key}.");
                        return v;
                    }
                    int minimum = Capacity("Minimum Room Capacity"), preferred = Capacity("Preferred Room Capacity");
                    if (preferred > 0 && preferred < minimum) throw new ArgumentException($"Preferred capacity is below minimum for {key}.");
                    foreach (var section in workbook.Sections.Where(s => s.Course.Equals(key, StringComparison.OrdinalIgnoreCase)))
                    {
                        section.MinimumRoomCapacity = minimum;
                        section.PreferredRoomCapacity = preferred;
                    }
                }
                else
                {
                    string name = Get("Professor");
                    if (Names.IsPlaceholder(name)) continue;
                    var track = Get("Tenure or PTF").ToLowerInvariant() switch
                    {
                        "t" => FacultyTrack.Tenure,
                        "p" or "ptf" => FacultyTrack.Professional,
                        "" => FacultyTrack.Unknown,
                        _ => throw new ArgumentException($"Invalid faculty track for {name}; use T or PTF.")
                    };
                    var profile = faculty.GetOrAdd(Names.Surname(name), name);
                    // Saved edits and existing profiles take precedence over reusable imports.
                    if (profile.Track == FacultyTrack.Unknown) profile.Track = track;
                }
            }
        }
    }
}
