using System;
using System.Collections.Generic;
using System.Linq;

namespace RoomScheduler
{
    /// <summary>
    /// A single class section that needs to be assigned a room.
    /// Modeled on the UT Austin printed course offerings catalog, where a
    /// section has a 5-digit unique number and meets on a recurring day
    /// pattern such as MWF or TTH.
    /// </summary>
    public class Course
    {
        public string UniqueNumber { get; set; } = "";
        public string ProfessorName { get; set; } = "";
        public string CourseName { get; set; } = "";
        public int EnrollmentCount { get; set; }

        /// <summary>Days this section meets, e.g. Monday/Wednesday/Friday.</summary>
        public List<DayOfWeek> MeetingDays { get; set; } = new();

        public TimeSpan StartTime { get; set; }
        public TimeSpan Duration { get; set; }

        public TimeSpan EndTime => StartTime + Duration;

        /// <summary>Department prefix of the course name, so "ARI" from "ARI 310L".</summary>
        public string Department
        {
            get
            {
                int space = CourseName.IndexOf(' ');
                return space > 0 ? CourseName.Substring(0, space) : CourseName;
            }
        }

        /// <summary>Catalog-style day pattern, e.g. "MWF" or "TTH".</summary>
        public string MeetingPattern =>
            string.Concat(MeetingDays.OrderBy(DayOrder).Select(DayCode));

        /// <summary>
        /// Catalog-style time range, e.g. "930 - 1100" or "130 - 530P". The printed
        /// catalog marks PM on the end time only, so "130 - 530P" reads 1:30pm to 5:30pm.
        /// </summary>
        public string TimeRange =>
            $"{CatalogTime(StartTime, markPm: false)} - {CatalogTime(EndTime, markPm: true)}";

        /// <summary>
        /// Readable clock form used on screen, matching the registrar's online
        /// course schedule: "9:30 a.m.-11:00 a.m.". The printed catalog drops the
        /// colon, which looks like a rendering fault on a web page.
        /// </summary>
        public string ClockRange => $"{ClockTime(StartTime)}-{ClockTime(EndTime)}";

        private static string ClockTime(TimeSpan t)
        {
            int hour = t.Hours;
            int displayHour = hour % 12;
            if (displayHour == 0) displayHour = 12;

            string suffix = hour >= 12 ? "p.m." : "a.m.";
            return $"{displayHour}:{t.Minutes:00} {suffix}";
        }

        public override string ToString() =>
            $"{CourseName} ({ProfessorName}) - {MeetingPattern} {TimeRange}, {EnrollmentCount} students";

        private static int DayOrder(DayOfWeek d) => d == DayOfWeek.Sunday ? 7 : (int)d;

        private static string DayCode(DayOfWeek day) => day switch
        {
            DayOfWeek.Monday => "M",
            DayOfWeek.Tuesday => "T",
            DayOfWeek.Wednesday => "W",
            DayOfWeek.Thursday => "TH",
            DayOfWeek.Friday => "F",
            DayOfWeek.Saturday => "S",
            _ => "SU"
        };

        /// <summary>
        /// Formats a time the way the printed catalog does: no colon, and a
        /// trailing "P" only where the catalog prints one. 9:30am becomes "930"
        /// and 5:30pm becomes "530P". Noon is left unmarked, matching entries
        /// like "900 - 1200".
        /// </summary>
        private static string CatalogTime(TimeSpan t, bool markPm)
        {
            int hour = t.Hours;
            int minute = t.Minutes;

            int displayHour = hour % 12;
            if (displayHour == 0) displayHour = 12;

            bool pm = hour >= 13;

            return $"{displayHour}{minute:00}" + (markPm && pm ? "P" : "");
        }
    }
}
