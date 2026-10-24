using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RoomScheduler;

public partial class Planner
{
    public record OverrideChange(Placement Placement, Room? BeforeRoom, MeetingTime? BeforeTime, Room Room, MeetingTime Time);
    public sealed class OverrideProposal
    {
        internal Plan Source { get; init; } = null!;
        internal List<(Placement Placement, Room? Room, MeetingTime? Time)> Snapshot { get; init; } = new();
        internal List<OverrideChange> Changes { get; init; } = new();
        public IReadOnlyList<OverrideChange> ProposedChanges => Changes.AsReadOnly();
        public string Explanation { get; internal init; } = "";
        internal bool Applied { get; set; }
    }

    /// <summary>Deterministic grammar; ambiguous selectors and incomplete times require clarification.</summary>
    public OverrideProposal ProposeOverride(Plan plan, string request, bool allowOversizedRoom = false)
    {
        var match = Regex.Match(request.Trim().TrimEnd('.'),
            @"^Put\s+(.+?)\s+in\s+([A-Z]+\s+[\w.]+)\s+(.+?)\s+at\s+(.+)$", RegexOptions.IgnoreCase);
        if (!match.Success) throw new ArgumentException("Use: Put [course title or number] in GDC 2.216 Monday through Thursday at 10:00am-11:00am. Include an end time or use an already scheduled section.");
        string selector = match.Groups[1].Value;
        var targets = plan.Placements.Where(p => p.Section.Title.Equals(selector, StringComparison.OrdinalIgnoreCase) ||
            p.Section.Course.Equals(selector, StringComparison.OrdinalIgnoreCase) ||
            $"{p.Section.Title} / {p.Section.Instructor} / row {p.Section.SourceRow + 1}".Equals(selector, StringComparison.OrdinalIgnoreCase)).ToList();
        if (targets.Count != 1)
            throw new ArgumentException("Course must identify one section. Use the full title, or: title / instructor / row N (source spreadsheet row).");
        var target = targets[0];
        if (target.Section.Online) throw new ArgumentException("This section is online; change its modality before assigning a physical room.");
        string roomName = Regex.Replace(match.Groups[2].Value.Trim(), @"\s+", " ");
        var room = _rooms.SingleOrDefault(r => r.DisplayName.Equals(roomName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Room not found in the current room inventory.");
        string days = match.Groups[3].Value;
        var range = Regex.Match(days, @"^(Monday|Tuesday|Wednesday|Thursday|Friday)\s+(?:through|to)\s+(Monday|Tuesday|Wednesday|Thursday|Friday)$", RegexOptions.IgnoreCase);
        if (range.Success)
        {
            var first = Enum.Parse<DayOfWeek>(range.Groups[1].Value, true);
            var last = Enum.Parse<DayOfWeek>(range.Groups[2].Value, true);
            if (first > last) throw new ArgumentException("Day range must run forward within the week.");
            days = string.Join(",", Enumerable.Range((int)first, (int)last - (int)first + 1).Select(d => ((DayOfWeek)d).ToString()));
        }
        else if (!Regex.IsMatch(days, @"^(?:(?:Monday|Tuesday|Wednesday|Thursday|Friday|Mon|Tue|Wed|Thu|Fri|MWF|TTH|MW|M|T|W|TH|F)(?:[, /]+|$))+$", RegexOptions.IgnoreCase))
            throw new ArgumentException("Use named weekdays, a Monday through Thursday range, or a catalog pattern such as MWF or TTH.");
        string clock = match.Groups[4].Value;
        if (!clock.Contains('-') && target.Time == null)
            throw new ArgumentException("An end time is required for a section without an existing duration.");
        if (!Regex.IsMatch(clock, @"^\d{1,2}(?::[0-5]\d)?\s*(?:am|pm)(?:\s*-\s*\d{1,2}(?::[0-5]\d)?\s*(?:am|pm))?$", RegexOptions.IgnoreCase))
            throw new ArgumentException("Use explicit am/pm, for example 10:00am-11:00am.");
        var dayList = MeetingTime.ParseDays(days);
        string pattern = new MeetingTime(dayList, TimeSpan.Zero, TimeSpan.Zero).Pattern;
        var times = PreferenceParser.FindTimes($"{pattern} {(clock.Contains('-') ? "" : "at ")}{clock}", clock.Contains('-') ? null : target.Time?.Duration).ToList();
        if (times.Count != 1) throw new ArgumentException("Provide one unambiguous time, preferably with am/pm and an end time.");
        var time = times[0].Time;
        if (!TimeAllowed(target.Section, time))
        {
            string reason = target.Section.Track == FacultyTrack.Tenure && time.Overlaps(Rules.TenureLunch)
                ? $"tenure-track faculty lunch ({Rules.TenureLunch})"
                : target.Section.Profile?.UnavailableTimes().Any(t => t.Overlaps(time)) == true
                    ? "the instructor's unavailable times on their profile"
                    : target.Section.Wishes.Blocked.Any(b => b.Time.Overlaps(time))
                        ? "an unavailable time in the instructor's preference notes"
                        : $"the protected graduate slot ({Rules.GraduateSlot}) for an instructor who also teaches a graduate course";
            throw new ArgumentException($"{time} conflicts with {reason}. Choose another time; the schedule is unchanged.");
        }
        if (!RoomAllowed(target.Section, room))
        {
            string reason = Rules.Offline.IsOffline(room) ? "the room or building is offline"
                : room.Capacity < Math.Max(target.Section.SizeForRoom, target.Section.MinimumRoomCapacity)
                    ? $"it has {room.Capacity} seats, but at least {Math.Max(target.Section.SizeForRoom, target.Section.MinimumRoomCapacity)} are needed"
                    : "it does not meet the section's room eligibility or faculty accommodation requirements";
            throw new ArgumentException($"Cannot use {room.DisplayName}: {reason}.");
        }
        var instructorConflict = plan.Placements.FirstOrDefault(p => p != target && !target.Section.InstructorMissing &&
            p.Section.Surname == target.Section.Surname && p.Time != null && p.Time.Overlaps(time));
        if (instructorConflict != null)
            throw new ArgumentException($"{target.Section.Instructor} already teaches {instructorConflict.Section.Title} at {instructorConflict.Time}. Choose a different time.");
        if (Rules.FitIndicator(room.Capacity, target.Section.SizeForRoom) == "Max high" && !allowOversizedRoom)
            throw new ArgumentException("This room is much larger than needed. Select the oversized-room exception and preview again to record an intentional exception.");

        var all = plan.Placements.Concat(plan.Discussions).ToList();
        var copies = all.ToDictionary(p => p, p => new Placement { Section = p.Section, Time = p.Time, Room = p.Room, LectureOf = p.LectureOf });
        var moved = copies[target];
        moved.Time = time;
        moved.Room = room;
        var displaced = all.Where(p => p != target && p.Room == room && p.Time != null && p.Time.Overlaps(time)).ToList();
        foreach (var p in displaced) copies[p].Room = null;
        int attempts = 0;
        bool Reassign(int index)
        {
            if (index == displaced.Count)
            {
                try { ValidateOverride(copies.Values.ToList()); return true; }
                catch (ArgumentException) { return false; }
            }
            var original = displaced[index];
            var p = copies[original];
            foreach (var candidate in _rooms.OrderBy(r => Math.Abs(r.Capacity - original.Room!.Capacity)))
            {
                if (++attempts > 20000) return false;
                if (!RoomAllowed(p.Section, candidate) || RoomBusy(p, candidate, p.Time!, copies.Values.ToList()) ||
                    ScoreRoom(p, p.Time!, candidate, copies.Values.ToList(), new()) == null) continue;
                p.Room = candidate;
                if (Reassign(index + 1)) return true;
                p.Room = null;
            }
            return false;
        }
        if (!Reassign(0)) throw new ArgumentException("Cannot rehouse displaced classes at their existing times. Nothing changed; choose another room/time.");
        ValidateOverride(copies.Values.ToList());
        var changes = all.Where(p => p.Room != copies[p].Room || !Equals(p.Time, copies[p].Time))
            .Select(p => new OverrideChange(p, p.Room, p.Time, copies[p].Room!, copies[p].Time!)).ToList();
        return new OverrideProposal
        {
            Source = plan, Snapshot = all.Select(p => (p, p.Room, p.Time)).ToList(), Changes = changes,
            Explanation = $"Manual request: {request}. " + (allowOversizedRoom ? "Oversized-room exception explicitly allowed. " : "") +
                "Displaced sections retain their times. Review weekly hours and registrar approval before publishing. Rebuild replaces manual overrides."
        };
    }

    public void ApplyOverride(Plan plan, OverrideProposal proposal)
    {
        if (proposal.Applied || proposal.Source != plan || proposal.Snapshot.Count != plan.Placements.Count + plan.Discussions.Count ||
            proposal.Snapshot.Any(s => s.Placement.Room != s.Room || !Equals(s.Placement.Time, s.Time)))
            throw new InvalidOperationException("Plan changed since preview; generate a fresh proposal.");
        var copies = proposal.Snapshot.Select(s =>
        {
            var change = proposal.Changes.FirstOrDefault(c => c.Placement == s.Placement);
            return new Placement { Section = s.Placement.Section, Room = change?.Room ?? s.Room, Time = change?.Time ?? s.Time };
        }).ToList();
        ValidateOverride(copies);
        foreach (var change in proposal.Changes)
        {
            var p = change.Placement;
            p.Room = change.Room; p.Time = change.Time; p.ManualOverride = true;
            p.Option = new TimeOption(change.Time, 1, "manual override");
            p.Fit = Rules.FitIndicator(change.Room.Capacity, p.Section.SizeForRoom);
            p.RegistrarCap = Rules.RegistrarCap(change.Room.Capacity);
            p.Why.Clear(); p.Why.Add(proposal.Explanation);
            if (!p.IsDiscussion) Finish(p);
            p.Section.Flag(FlagLevel.Review, ReviewFlag.SpecialRequest, proposal.Explanation);
        }
        proposal.Applied = true;
    }

    private bool TimeAllowed(SectionRequest s, MeetingTime t) =>
        t.Days.Count > 0 && t.Start >= TimeSpan.Zero && t.End <= TimeSpan.FromHours(24) && t.End > t.Start &&
        !(s.Track == FacultyTrack.Tenure && t.Overlaps(Rules.TenureLunch)) &&
        !(s.Track == FacultyTrack.Tenure && !SchedulingRules.IsGraduate(s.Course) && _graduateTeachers.Contains(s.Surname) && t.Overlaps(Rules.GraduateSlot)) &&
        !(s.Profile?.UnavailableTimes().Any(b => b.Overlaps(t)) ?? false) &&
        !s.Wishes.Blocked.Any(b => b.Time.Overlaps(t));

    private void ValidateOverride(List<Placement> all)
    {
        foreach (var p in all.Where(p => p.Room != null && p.Time != null))
        {
            if (!TimeAllowed(p.Section, p.Time!) || !RoomAllowed(p.Section, p.Room!) ||
                RoomBusy(p, p.Room!, p.Time!, all) || InstructorBusy(p, p.Time!, all) ||
                ScoreRoom(p, p.Time!, p.Room!, all, new()) == null)
                throw new ArgumentException("Proposed schedule violates a room, instructor, availability, or accommodation constraint. Nothing changed.");
        }
    }
}
