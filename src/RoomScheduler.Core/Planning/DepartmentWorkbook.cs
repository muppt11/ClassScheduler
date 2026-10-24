using System;
using System.Collections.Generic;
using System.Linq;

namespace RoomScheduler
{
    /// <summary>
    /// Reads the department's semester workbook: a "Classes App" sheet listing
    /// sections (Title, Professor, Tenure or PTF, Minimum Room Size Needed, ...)
    /// and a "Scheduling Preferences" sheet with each professor's form.
    /// Column headers are matched by name, since they shift between semesters.
    /// </summary>
    public class DepartmentWorkbook
    {
        public string ClassesSheetName { get; private set; } = "";
        public List<string> ClassesHeaders { get; private set; } = new();
        public List<List<string>> ClassesRows { get; } = new();
        public int ClassesHeaderRow { get; private set; }

        public List<SectionRequest> Sections { get; } = new();
        public List<PreferenceForm> Forms { get; } = new();

        /// <summary>Forms that did not match any section on the Classes App sheet.</summary>
        public List<PreferenceForm> UnmatchedForms { get; } = new();

        public string Semester { get; private set; } = "";

        public static DepartmentWorkbook FromSheets(List<Xlsx.Sheet> sheets)
        {
            var wb = new DepartmentWorkbook();

            var classes = sheets.FirstOrDefault(s => s.Name.Contains("Classes", StringComparison.OrdinalIgnoreCase))
                          ?? sheets.FirstOrDefault(s => HeaderRow(s, "Title") >= 0);
            var prefs = sheets.FirstOrDefault(s => HeaderRow(s, "Prof") >= 0 && HeaderRow(s, "course") >= 0);

            if (classes != null && HeaderRow(classes, "Title") >= 0) wb.ReadClasses(classes);
            if (prefs != null) wb.ReadPreferences(prefs);
            return wb;
        }

        private static int HeaderRow(Xlsx.Sheet s, string mustContain) =>
            s.Rows.FindIndex(r => r.Any(c => c.Trim().Equals(mustContain, StringComparison.OrdinalIgnoreCase)));

        private void ReadClasses(Xlsx.Sheet sheet)
        {
            ClassesSheetName = sheet.Name;
            ClassesHeaderRow = Math.Max(0, HeaderRow(sheet, "Title"));
            ClassesHeaders = sheet.Rows[ClassesHeaderRow].Select(c => c.Trim()).ToList();
            ClassesRows.AddRange(sheet.Rows.Select(r => r.ToList()));

            int Col(params string[] names) =>
                ClassesHeaders.FindIndex(h => names.Any(n => h.Equals(n, StringComparison.OrdinalIgnoreCase)));

            int title = Col("Title"), prof = Col("Professor", "Instructor"), track = Col("Tenure or PTF"),
                prio = Col("Prioritized Professor", "Prioritized Instructor"),
                size = Col("Minimum Room Size Needed", "Mimimum Room Size Needed"),
                type = Col("Type"), tags = Col("Tags"), proctors = Col("Number of proctors needed"),
                tas = Col("Number of TAs needed"), sem = Col("Semester"), year = Col("Year");

            for (int r = ClassesHeaderRow + 1; r < sheet.Rows.Count; r++)
            {
                string Get(int c) => c >= 0 ? sheet.Cell(r, c).Trim() : "";
                if (Get(title).Length == 0) continue;

                if (Semester.Length == 0 && Get(sem).Length > 0)
                    Semester = $"{Get(sem)} {Get(year)}".Trim();

                int.TryParse(Get(size), out int projected);
                int.TryParse(Get(proctors), out int p);
                int.TryParse(Get(tas), out int t);

                Sections.Add(new SectionRequest
                {
                    SourceRow = r,
                    Title = Get(title),
                    Instructor = Get(prof),
                    Track = Get(track).ToLowerInvariant() switch
                    {
                        "t" => FacultyTrack.Tenure,
                        "ptf" or "p" => FacultyTrack.Professional,
                        _ => FacultyTrack.Unknown
                    },
                    Difficult = Get(prio).Equals("D", StringComparison.OrdinalIgnoreCase),
                    ProjectedSize = projected,
                    SizeForRoom = projected,
                    Type = Get(type),
                    Tags = Get(tags),
                    ProctorsNeeded = p,
                    TAsNeeded = t
                });
            }
        }

        private void ReadPreferences(Xlsx.Sheet sheet)
        {
            int hr = Math.Max(0, HeaderRow(sheet, "Prof"));
            var h = sheet.Rows[hr].Select(c => c.Trim()).ToList();
            int Col(string name) => h.FindIndex(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));

            for (int r = hr + 1; r < sheet.Rows.Count; r++)
            {
                string Get(string name)
                {
                    int c = Col(name);
                    return c >= 0 ? sheet.Cell(r, c).Trim() : "";
                }

                if (Get("Prof").Length == 0 || Get("course").Length == 0) continue;

                Forms.Add(new PreferenceForm
                {
                    Submitted = Get("submission time"),
                    Professor = Get("Prof"),
                    Course = Get("course"),
                    Time1 = Get("Time 1"),
                    Time2 = Get("Time 2"),
                    Time3 = Get("Time 3"),
                    TimeOther = Get("Time other"),
                    RoomRequest = Get("Room Request"),
                    Area = Get("Area of Campus"),
                    Requirements = Get("Room Requirements"),
                    Discussion = Get("Discussion section"),
                    DiscussionRequirements = Get("Discusssion requirements").Length > 0
                        ? Get("Discusssion requirements")
                        : Get("Discussion requirements"),
                    CrossListed = Get("cross-listed"),
                    CrossListedWith = Get("crosslisted classes"),
                    Comments = Get("comments")
                });
            }
        }

        /// <summary>
        /// Pairs each form with a section taught by the same surname for the same
        /// course number. A professor teaching three sections usually sends three
        /// forms, so they are paired off in order, closest title first.
        /// </summary>
        public void MatchForms()
        {
            UnmatchedForms.Clear();
            var taken = new HashSet<SectionRequest>();

            foreach (var form in Forms)
            {
                var candidates = Sections
                    .Where(s => !taken.Contains(s) && s.Surname == form.Surname && s.Course == form.CourseNumber)
                    .OrderByDescending(s => TitleOverlap(s.Title, form.Course))
                    .ToList();

                // Some forms carry a course with a stray suffix ("378H:" vs "378H").
                if (candidates.Count == 0)
                    candidates = Sections
                        .Where(s => !taken.Contains(s) && s.Surname == form.Surname &&
                                    TitleOverlap(s.Title, form.Course) >= 2)
                        .ToList();

                var match = candidates.FirstOrDefault();
                if (match == null)
                {
                    UnmatchedForms.Add(form);
                    continue;
                }

                match.Form = form;
                taken.Add(match);
            }
        }

        private static int TitleOverlap(string a, string b)
        {
            var wa = a.ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToHashSet();
            var wb = b.ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1);
            return wb.Count(wa.Contains);
        }
    }
}
