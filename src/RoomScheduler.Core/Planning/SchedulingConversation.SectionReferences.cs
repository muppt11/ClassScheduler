using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RoomScheduler;

public sealed partial class SchedulingConversation
{
    private const string ReferenceDay = @"(?:Monday|Tuesday|Wednesday|Thursday|Friday|Mon|Tue|Wed|Thu|Fri|(?:TH|M|T|W|F)+)\b";
    private const string ReferenceClock = @"\d{1,2}(?::[0-5]\d)?\s*(?:[ap](?:m)?)?";
    private const string ReferenceTiming = @"(?:(?<days>" + ReferenceDay + @"(?:\s*(?:,|/|and)?\s*" + ReferenceDay + @")*)\s*)?(?:at\s+)?(?<start>" + ReferenceClock + @")?(?:\s*[-–]\s*(?<end>" + ReferenceClock + @"))?";

    private sealed record SectionReference(string Source, Match Timing)
    {
        public bool Matches(Placement placement)
        {
            if (placement.Time is not MeetingTime time) return false;
            if (Timing.Groups["days"].Success)
            {
                var days = Regex.Matches(Timing.Groups["days"].Value, ReferenceDay, Ignore)
                    .SelectMany(m => MeetingTime.ParseDays(m.Value)).Distinct().OrderBy(d => d);
                if (!days.SequenceEqual(time.Days.OrderBy(d => d))) return false;
            }
            return (!Timing.Groups["start"].Success || MatchesClock(Timing.Groups["start"].Value, time.Start)) &&
                (!Timing.Groups["end"].Success || MatchesClock(Timing.Groups["end"].Value, time.End));
        }
    }

    private static bool MatchesClock(string text, TimeSpan actual)
    {
        var clock = Regex.Match(text.Trim(), @"^(?<hour>\d{1,2})(?::(?<minute>[0-5]\d))?\s*(?<period>[ap](?:m)?)?$", Ignore);
        if (!clock.Success) return false;
        int hour = int.Parse(clock.Groups["hour"].Value);
        int minute = clock.Groups["minute"].Success ? int.Parse(clock.Groups["minute"].Value) : 0;
        string period = clock.Groups["period"].Value.ToLowerInvariant();
        if (period.Length > 0)
            return hour is >= 1 and <= 12 && actual.Hours == hour % 12 + (period[0] == 'p' ? 12 : 0) && actual.Minutes == minute;
        // Unmarked clocks match the actual schedule; if AM and PM both match, keep both candidates.
        return hour <= 23 && actual.Minutes == minute && (hour is >= 1 and <= 12 ? actual.Hours % 12 == hour % 12 : actual.Hours == hour);
    }

    private static SectionReference? ReadSectionReference(string text, bool allowBareTiming, out string identity)
    {
        identity = text;
        foreach (Match parentheses in Regex.Matches(text, @"\(([^()]*)\)"))
        {
            var timing = Regex.Match(parentheses.Groups[1].Value.Trim(), "^" + ReferenceTiming + "$", Ignore);
            if (!timing.Success || !timing.Groups["days"].Success && !timing.Groups["start"].Success) continue;
            identity = text.Remove(parentheses.Index, parentheses.Length);
            return new(parentheses.Value, timing);
        }
        if (!allowBareTiming) return null;
        // Bare timing is identifying information only in a swap operand or a section-selection reply.
        var suffix = Regex.Match(text.TrimEnd('?', '!', '.', ' '), @"(?:^|\s)(?:(?:on|at|currently at|currently on)\s+)?(?<timing>" + ReferenceTiming + ")$", Ignore);
        if (!suffix.Success || !suffix.Groups["days"].Success && !suffix.Groups["start"].Success) return null;
        if (!suffix.Groups["days"].Success && !Regex.IsMatch(suffix.Groups["timing"].Value, @"[:ap]", Ignore)) return null;
        identity = text.Remove(suffix.Index, suffix.Length);
        return new(suffix.Value, suffix);
    }

    private List<Placement> FindCourses(string text, bool allowBareTiming = false)
    {
        var reference = ReadSectionReference(text, allowBareTiming, out string identity);
        var courses = FindCoursesByName(identity);
        return reference == null ? courses : courses.Where(reference.Matches).ToList();
    }

    private static string RemoveSectionReferences(string text, string course, string? partner, bool swap)
    {
        foreach (string operand in partner == null ? new[] { course } : new[] { course, partner })
        {
            var reference = ReadSectionReference(operand, swap, out _);
            if (reference != null) text = text.Replace(reference.Source, " ", StringComparison.Ordinal);
        }
        return text;
    }
}
