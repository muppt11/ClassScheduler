using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RoomScheduler
{
    /// <summary>One section as it actually ran in a past semester.</summary>
    public class PastSection
    {
        public string Semester { get; init; } = "";

        /// <summary>The registrar's CCYYS code, e.g. 20259 for Fall 2025. Sorts in time order.</summary>
        public int SemesterCode { get; init; }

        public string Unique { get; init; } = "";
        public string Course { get; init; } = "";
        public string Title { get; init; } = "";
        public List<string> Instructors { get; init; } = new();
        public int Enrollment { get; init; }
        public MeetingTime? Time { get; init; }
        public string Building { get; init; } = "";
        public string Room { get; init; } = "";
        public MeetingTime? DiscussionTime { get; init; }
        public string DiscussionBuilding { get; init; } = "";
        public string DiscussionRoom { get; init; } = "";
        public bool Online { get; init; }

        /// <summary>"Partially taught as a Web-based course": meets in person for only part of its hours.</summary>
        public bool Hybrid { get; init; }

        public string RoomName => Building.Length == 0 ? "" : $"{Building} {Room}";
    }

    /// <summary>
    /// Past semesters loaded from the registrar export the department keeps
    /// (columns CCYYS, UNQ, CNBR, INST NAME, DAYS1, FROM T1, BLDG1, ...).
    /// Answers "what did this professor teach, when and where".
    /// </summary>
    public class PastSchedule
    {
        public List<PastSection> Sections { get; } = new();

        public static PastSchedule Empty => new();

        public string Label =>
            Sections.Count == 0 ? "none" : string.Join(", ", Semesters);

        /// <summary>Semesters loaded, newest first.</summary>
        public IEnumerable<string> Semesters =>
            Sections.OrderByDescending(s => s.SemesterCode).Select(s => s.Semester).Distinct();

        /// <summary>
        /// Several semesters' exports as one history. A semester that appears in
        /// more than one file is taken from the first file only, so the same
        /// export uploaded twice does not count twice.
        /// </summary>
        public static PastSchedule Combine(IEnumerable<PastSchedule> parts)
        {
            var all = new PastSchedule();
            var seen = new HashSet<int>();
            foreach (var part in parts)
            {
                var codes = part.Sections.Select(s => s.SemesterCode).Distinct().ToList();
                foreach (var code in codes.Where(seen.Add))
                    all.Sections.AddRange(part.Sections.Where(s => s.SemesterCode == code));
            }
            return all;
        }

        public static PastSchedule FromWorkbook(IEnumerable<Xlsx.Sheet> sheets)
        {
            var past = new PastSchedule();

            foreach (var sheet in sheets)
            {
                int headerRow = sheet.Rows.FindIndex(r => r.Any(c => c.Trim() == "CNBR"));
                if (headerRow < 0) continue;

                var h = sheet.Rows[headerRow].Select(c => c.Trim()).ToList();
                int Col(string name) => h.IndexOf(name);

                for (int r = headerRow + 1; r < sheet.Rows.Count; r++)
                {
                    string Get(string name)
                    {
                        int c = Col(name);
                        return c >= 0 ? sheet.Cell(r, c).Trim() : "";
                    }

                    string course = Get("CNBR");
                    if (course.Length == 0) continue;

                    int.TryParse(Get("TOT SEATS TKN"), out int enrolled);
                    string notes = Get("NON-INV NOTELINES");

                    past.Sections.Add(new PastSection
                    {
                        Semester = SemesterName(Get("CCYYS")),
                        SemesterCode = int.TryParse(Get("CCYYS"), out int code) ? code : 0,
                        Unique = Get("UNQ"),
                        Course = course,
                        Title = Get("TITLE"),
                        Instructors = Get("INST NAME")
                            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(Names.Surname)
                            .Where(s => s.Length > 0)
                            .ToList(),
                        Enrollment = enrolled,
                        Time = TimeOf(Get("DAYS1"), Get("FROM T1"), Get("TO T1")),
                        Building = Get("BLDG1"),
                        Room = RoomNumber(Get("RM1")),
                        DiscussionTime = TimeOf(Get("DAYS2"), Get("FROM T2"), Get("TO T2")),
                        DiscussionBuilding = Get("BLDG2"),
                        DiscussionRoom = RoomNumber(Get("RM2")),
                        Online = notes.StartsWith("Taught as a Web-based", StringComparison.OrdinalIgnoreCase) ||
                                 Get("DAYS1").Length == 0,
                        Hybrid = notes.StartsWith("Partially taught as a Web-based", StringComparison.OrdinalIgnoreCase)
                    });
                }
            }

            return past;
        }

        /// <summary>
        /// Sections this professor taught for this course number, from the most
        /// recent semester they taught it. That is what "last time" means.
        /// </summary>
        public List<PastSection> For(string surname, string course) =>
            Latest(Sections.Where(s => s.Instructors.Contains(surname, StringComparer.OrdinalIgnoreCase) &&
                                       string.Equals(s.Course, course, StringComparison.OrdinalIgnoreCase)));

        public List<PastSection> ForInstructor(string surname) =>
            Latest(Sections.Where(s => s.Instructors.Contains(surname, StringComparer.OrdinalIgnoreCase)));

        public List<PastSection> ForCourse(string course) =>
            Latest(Sections.Where(s => string.Equals(s.Course, course, StringComparison.OrdinalIgnoreCase)));

        private static List<PastSection> Latest(IEnumerable<PastSection> sections)
        {
            var list = sections.ToList();
            if (list.Count == 0) return list;
            int newest = list.Max(s => s.SemesterCode);
            return list.Where(s => s.SemesterCode == newest).ToList();
        }

        /// <summary>"20259" is Fall 2025: the last digit is 2 spring, 6 summer, 9 fall.</summary>
        private static string SemesterName(string ccyys)
        {
            if (ccyys.Length != 5) return ccyys;
            string term = ccyys[4] switch { '2' => "Spring", '6' => "Summer", '9' => "Fall", _ => "Term" };
            return $"{term} {ccyys.Substring(0, 4)}";
        }

        /// <summary>Excel turns "1.316" into 1.3160000000000001 and "308" into 308.000.</summary>
        private static string RoomNumber(string raw)
        {
            if (raw.Length == 0 || raw == "0") return "";
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                return raw;
            return v == Math.Floor(v) && v >= 100
                ? ((int)v).ToString(CultureInfo.InvariantCulture)
                : v.ToString("0.000", CultureInfo.InvariantCulture);
        }

        private static MeetingTime? TimeOf(string days, string from, string to)
        {
            var d = MeetingTime.ParseDays(days);
            var s = Xlsx.DayFraction(from);
            var e = Xlsx.DayFraction(to);
            if (d.Count == 0 || s == null || e == null || e <= s) return null;
            return new MeetingTime(d, s.Value, e.Value);
        }
    }

    /// <summary>Name normalising shared by every sheet, which all spell names a little differently.</summary>
    public static class Names
    {
        /// <summary>"SCOTT, M" / "ABRAHAM S" / "ELNOZAHY,, M" all become "SCOTT", "ABRAHAM", "ELNOZAHY".</summary>
        public static string Surname(string name)
        {
            name = name.Trim().ToUpperInvariant();
            int cut = name.IndexOfAny(new[] { ',', ' ' });
            return cut > 0 ? name.Substring(0, cut) : name;
        }

        /// <summary>A name is a placeholder if it is blank, "TBD" or a run of x's.</summary>
        public static bool IsPlaceholder(string name)
        {
            var t = name.Trim().Trim(',').ToLowerInvariant();
            return t.Length == 0 || t == "tbd" || t == "staff" || t.All(ch => ch == 'x');
        }

        /// <summary>"314H DATA STRUCTURES" is 314H, "388:  NLP" is 388, "RBT 350 GATEWAY" is RBT 350.</summary>
        public static string CourseNumber(string title)
        {
            var parts = title.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "";
            string first = parts[0].TrimEnd(':').ToUpperInvariant();

            // A department prefix like "RBT" or "C S" comes first for non-CS courses.
            if (first.All(char.IsLetter) && parts.Length > 1)
                return first + " " + parts[1].TrimEnd(':').ToUpperInvariant();

            return first;
        }
    }
}
