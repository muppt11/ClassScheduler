using System;
using System.Collections.Generic;
using System.Linq;

namespace RoomScheduler
{
    /// <summary>
    /// When a section meets: a set of weekdays plus a start and end time,
    /// for example MWF 11:00-12:00 or TTH 12:30-2:00.
    /// </summary>
    public record MeetingTime(IReadOnlyList<DayOfWeek> Days, TimeSpan Start, TimeSpan End)
    {
        public TimeSpan Duration => End - Start;

        public bool SharesDayWith(MeetingTime other) => Days.Intersect(other.Days).Any();

        public bool Overlaps(MeetingTime other) =>
            SharesDayWith(other) && Start < other.End && other.Start < End;

        /// <summary>Catalog day pattern such as "MWF" or "TTH".</summary>
        public string Pattern => string.Concat(Days.OrderBy(DayOrder).Select(DayCode));

        public string Clock => $"{ClockTime(Start)}-{ClockTime(End)}";

        public override string ToString() => $"{Pattern} {Clock}";

        public static MeetingTime Of(string days, int startHour, int startMinute, int endHour, int endMinute) =>
            new(ParseDays(days), new TimeSpan(startHour, startMinute, 0), new TimeSpan(endHour, endMinute, 0));

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
            var singleDay = NamedDay(text);
            if (singleDay != null) return new List<DayOfWeek> { singleDay.Value };

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

        public static string ClockTime(TimeSpan t)
        {
            int h = t.Hours % 12;
            if (h == 0) h = 12;
            return $"{h}:{t.Minutes:00}{(t.Hours >= 12 ? "pm" : "am")}";
        }

        public static int DayOrder(DayOfWeek d) => d == DayOfWeek.Sunday ? 7 : (int)d;

        public static string DayCode(DayOfWeek day) => day switch
        {
            DayOfWeek.Monday => "M",
            DayOfWeek.Tuesday => "T",
            DayOfWeek.Wednesday => "W",
            DayOfWeek.Thursday => "TH",
            DayOfWeek.Friday => "F",
            DayOfWeek.Saturday => "S",
            _ => "SU"
        };

        public virtual bool Equals(MeetingTime? other) =>
            other is not null && Start == other.Start && End == other.End && Pattern == other.Pattern;

        public override int GetHashCode() => HashCode.Combine(Start, End, Pattern);
    }
}
