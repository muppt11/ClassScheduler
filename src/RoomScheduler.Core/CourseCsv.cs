using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace RoomScheduler
{
    /// <summary>
    /// Reads a course list out of a CSV. Registrar exports vary between offices,
    /// so columns are matched against a few common spellings rather than one
    /// fixed set of headers, and unreadable rows are reported instead of thrown.
    /// </summary>
    public static class CourseCsv
    {
        private static readonly string[] CourseNames    = { "course", "course name", "class", "course number" };
        private static readonly string[] ProfessorNames = { "professor", "instructor", "professorname", "teacher", "faculty" };
        private static readonly string[] EnrollmentName = { "enrollment", "enrollmentcount", "enrolled", "enrl", "students", "seats" };
        private static readonly string[] DayNames       = { "days", "day", "dayofweek", "meetingdays", "pattern" };
        private static readonly string[] StartNames     = { "start", "starttime", "begin", "from" };
        private static readonly string[] EndNames       = { "end", "endtime", "finish", "to" };
        private static readonly string[] DurationNames  = { "duration", "minutes", "length" };
        private static readonly string[] UniqueNames    = { "unique", "uniquenumber", "unique number", "section" };

        public class LoadResult
        {
            public List<Course> Courses { get; } = new();
            public List<string> Problems { get; } = new();
        }

        public static LoadResult Load(TextReader reader)
        {
            var result = new LoadResult();

            var lines = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) != null)
                lines.Add(line);

            if (lines.Count < 2)
            {
                result.Problems.Add("The file needs a header row and at least one course.");
                return result;
            }

            var headers = Csv.ParseLine(lines[0])
                .Select(h => h.Trim().Trim('"').ToLowerInvariant())
                .ToList();

            int courseIdx = IndexOf(headers, CourseNames);
            int profIdx = IndexOf(headers, ProfessorNames);
            int enrolIdx = IndexOf(headers, EnrollmentName);
            int daysIdx = IndexOf(headers, DayNames);
            int startIdx = IndexOf(headers, StartNames);
            int endIdx = IndexOf(headers, EndNames);
            int durIdx = IndexOf(headers, DurationNames);
            int uniqueIdx = IndexOf(headers, UniqueNames);

            var missing = new List<string>();
            if (courseIdx == -1) missing.Add("course");
            if (enrolIdx == -1) missing.Add("enrollment");
            if (daysIdx == -1) missing.Add("days");
            if (startIdx == -1) missing.Add("start time");
            if (endIdx == -1 && durIdx == -1) missing.Add("end time or duration");

            if (missing.Count > 0)
            {
                result.Problems.Add("Missing required column(s): " + string.Join(", ", missing) +
                                    ". Found: " + string.Join(", ", headers.Where(h => h.Length > 0)));
                return result;
            }

            for (int i = 1; i < lines.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                    continue;

                var f = Csv.ParseLine(lines[i]);
                string Field(int idx) => idx >= 0 && idx < f.Count ? f[idx].Trim().Trim('"') : "";

                string courseName = Field(courseIdx);
                if (courseName.Length == 0)
                    continue;

                if (!int.TryParse(Field(enrolIdx), NumberStyles.Any, CultureInfo.InvariantCulture, out int enrollment))
                {
                    result.Problems.Add($"Row {i + 1} ({courseName}): enrollment '{Field(enrolIdx)}' is not a number.");
                    continue;
                }

                var days = ParseDays(Field(daysIdx));
                if (days.Count == 0)
                {
                    result.Problems.Add($"Row {i + 1} ({courseName}): could not read meeting days '{Field(daysIdx)}'.");
                    continue;
                }

                var start = ParseTime(Field(startIdx));
                if (start == null)
                {
                    result.Problems.Add($"Row {i + 1} ({courseName}): could not read start time '{Field(startIdx)}'.");
                    continue;
                }

                TimeSpan? duration = null;
                if (endIdx >= 0)
                {
                    var end = ParseTime(Field(endIdx));
                    if (end != null && end > start)
                        duration = end - start;
                }

                if (duration == null && durIdx >= 0 &&
                    int.TryParse(Field(durIdx), out int minutes) && minutes > 0)
                {
                    duration = TimeSpan.FromMinutes(minutes);
                }

                if (duration == null)
                {
                    result.Problems.Add($"Row {i + 1} ({courseName}): could not work out how long the class runs.");
                    continue;
                }

                result.Courses.Add(new Course
                {
                    UniqueNumber = Field(uniqueIdx),
                    CourseName = courseName,
                    ProfessorName = Field(profIdx),
                    EnrollmentCount = enrollment,
                    MeetingDays = days,
                    StartTime = start.Value,
                    Duration = duration.Value
                });
            }

            if (result.Courses.Count == 0 && result.Problems.Count == 0)
                result.Problems.Add("No usable course rows were found.");

            return result;
        }

        private static int IndexOf(List<string> headers, string[] candidates) =>
            headers.FindIndex(h => candidates.Contains(h.Replace("_", "").Replace(" ", "")) ||
                                   candidates.Contains(h));

        /// <summary>
        /// Reads meeting days written either as a catalog pattern such as MWF or
        /// TTH, or as a list such as "Mon,Wed,Fri".
        /// </summary>
        public static List<DayOfWeek> ParseDays(string raw)
        {
            var days = new List<DayOfWeek>();
            if (string.IsNullOrWhiteSpace(raw))
                return days;

            string text = raw.Trim().ToUpperInvariant();

            if (text.Contains(',') || text.Contains(' ') || text.Contains('/'))
            {
                foreach (var part in text.Split(new[] { ',', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var day = NamedDay(part);
                    if (day != null && !days.Contains(day.Value))
                        days.Add(day.Value);
                }

                if (days.Count > 0)
                    return days;
            }

            // Catalog style: walk the string, taking TH before T.
            int i = 0;
            while (i < text.Length)
            {
                if (text[i] == 'T' && i + 1 < text.Length && text[i + 1] == 'H')
                {
                    Add(DayOfWeek.Thursday); i += 2; continue;
                }

                switch (text[i])
                {
                    case 'M': Add(DayOfWeek.Monday); break;
                    case 'T': Add(DayOfWeek.Tuesday); break;
                    case 'W': Add(DayOfWeek.Wednesday); break;
                    case 'F': Add(DayOfWeek.Friday); break;
                    case 'S': Add(DayOfWeek.Saturday); break;
                }
                i++;
            }

            return days;

            void Add(DayOfWeek d)
            {
                if (!days.Contains(d)) days.Add(d);
            }
        }

        private static DayOfWeek? NamedDay(string token) => token switch
        {
            "MON" or "MONDAY" or "M" => DayOfWeek.Monday,
            "TUE" or "TUES" or "TUESDAY" or "T" => DayOfWeek.Tuesday,
            "WED" or "WEDNESDAY" or "W" => DayOfWeek.Wednesday,
            "THU" or "THUR" or "THURS" or "THURSDAY" or "TH" => DayOfWeek.Thursday,
            "FRI" or "FRIDAY" or "F" => DayOfWeek.Friday,
            "SAT" or "SATURDAY" => DayOfWeek.Saturday,
            _ => null
        };

        /// <summary>
        /// Reads a time written as "9:30 AM", "14:00", "930" or the catalog form
        /// "130P". Bare catalog times below 8 are read as afternoon, matching how
        /// the printed schedule is read.
        /// </summary>
        public static TimeSpan? ParseTime(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            string text = raw.Trim().ToUpperInvariant().Replace(".", "");

            if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var parsed))
                return parsed;

            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                return dt.TimeOfDay;

            bool pm = text.EndsWith("PM") || (text.EndsWith("P") && !text.EndsWith("AMP"));
            bool am = text.EndsWith("AM") || text.EndsWith("A");
            string digits = new string(text.Where(char.IsDigit).ToArray());

            if (digits.Length < 3 || digits.Length > 4)
                return null;

            int minute = int.Parse(digits.Substring(digits.Length - 2));
            int hour = int.Parse(digits.Substring(0, digits.Length - 2));

            if (minute > 59)
                return null;

            if (pm && hour < 12) hour += 12;
            else if (am && hour == 12) hour = 0;
            else if (!pm && !am && hour < 8) hour += 12;

            if (hour > 23)
                return null;

            return new TimeSpan(hour, minute, 0);
        }
    }
}
