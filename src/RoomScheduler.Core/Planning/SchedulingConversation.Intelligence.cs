using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RoomScheduler;

public sealed partial class SchedulingConversation
{
    private string _availabilityDetails = "";

    private static List<Room> MentionedRooms(string text, IReadOnlyList<Room> rooms) => rooms
        .Where(r => Regex.IsMatch(text, $@"\b{Regex.Escape(r.DisplayName)}\b", Ignore)).ToList();

    private static bool IsRefinement(string text) => TimingIntent(text) || WantsRoomSearch(text) || Regex.IsMatch(text,
        @"\b(after|before|between|keep|same|only|instead|actually|anywhere|any time|any day|any room|any size|allow|avoid|outside|no |not |without|smallest|fewest|recording|lecture capture)\b|\b(?:Monday|Tuesday|Wednesday|Thursday|Friday) (?:is |are )?(?:okay|ok|fine)\b", Ignore);

    private static bool ReadSeatBounds(string text, SearchFilters filters)
    {
        var matches = Regex.Matches(text,
            @"\b(at least|at most|no more than|no less than|minimum of|maximum of|under|over|less than|fewer than|more than)\s+(\d+)\s*(?:seats?|people|students?)\b", Ignore);
        if (matches.Count > 0) filters.Error = null;
        foreach (Match match in matches)
        {
            if (!int.TryParse(match.Groups[2].Value, out int seats))
            { filters.Error = "That seat count is too large. Give me a seat count within the room inventory's range."; continue; }
            string bound = match.Groups[1].Value.ToLowerInvariant();
            if (bound is "at least" or "no less than" or "minimum of") filters.MinSeats = seats;
            else if (bound is "over" or "more than") filters.MinSeats = (long)seats + 1;
            else filters.MaxSeats = bound is "under" or "less than" or "fewer than" ? (long)seats - 1 : seats;
        }
        return matches.Count > 0;
    }

    private SearchFilters RoomQueryFilters(string text, IReadOnlyList<Room> rooms)
    {
        var filters = new SearchFilters();
        ReadSeatBounds(text, filters);
        foreach (string building in rooms.Select(r => r.Building).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Regex.IsMatch(text, $@"\b(?:not in|avoid|outside|except|excluding)\s+{Regex.Escape(building)}\b", Ignore))
                filters.ExcludedBuildings.Add(building);
            else if (Regex.IsMatch(text, $@"\b(?:in|within|only|use)\s+{Regex.Escape(building)}\b", Ignore))
                filters.Building = building;
        }
        filters.Recording = Regex.IsMatch(text, @"\b(?:with|needs?) (?:recording|lecture capture)\b", Ignore);
        filters.UnusedRoom = Regex.IsMatch(text, @"\bunused\b|\b(?:has not|hasn't|not) been used\b", Ignore);
        return filters;
    }

    private bool AnswerSmartQuestion(string text, IReadOnlyList<Room> rooms)
    {
        if (Regex.IsMatch(text, @"\b(?:which|show|list|find)\b.*\b(?:oversized|too (?:big|large)|wasted seats|room fit)\b", Ignore))
        {
            var oversized = _plan!.Placements.Where(p => p.Room != null && !p.Section.Online &&
                _plan.Rules.FitIndicator(p.Room.Capacity, p.Section.SizeForRoom) == "Max high")
                .OrderByDescending(p => p.Room!.Capacity - p.Section.SizeForRoom).ToList();
            Reply(oversized.Count == 0 ? "No assigned classrooms are marked oversized by the current room-fit rules." :
                $"{oversized.Count} section(s) have oversized rooms. Largest seat gaps first:\n" +
                string.Join("\n", oversized.Take(8).Select(p => $"{Description(p)}: {p.Room!.DisplayName}, {p.Room.Capacity} seats; expected class size {p.Section.SizeForRoom}; {p.Room.Capacity - p.Section.SizeForRoom} spare seats.")) +
                "\nName a section and ask for a smaller room to check alternatives. Nothing has changed.");
            return true;
        }
        var mentioned = MentionedRooms(text, rooms);
        if (mentioned.Count >= 2 && Regex.IsMatch(text, @"\b(compare|difference|versus|vs)\b", Ignore))
        {
            var target = _lastTarget ?? _target;
            Reply(string.Join("\n\n", mentioned.Select(r => DescribeRoom(r, target))) +
                "\nThese are inventory and occupancy comparisons. Request a move to validate all scheduling constraints; nothing has changed.");
            return true;
        }
        if (Regex.IsMatch(text, @"\b(how (?:big|large)|how many seats|room (?:size|capacity)|capacity of|class size|enrollment)\b", Ignore) &&
            !Regex.IsMatch(text, @"\b(move|find|swap|put|change|adjust)\b", Ignore))
        {
            if (mentioned.Count == 1) { Reply(DescribeRoom(mentioned[0], _lastTarget ?? _target)); return true; }
            var matches = FindCourses(text);
            var target = matches.Count == 1 ? matches[0] : matches.Count == 0 && _referencedCourses.Count <= 1 ? _lastTarget ?? _target : null;
            if (target == null) return false;
            var proposed = Proposal?.ProposedChanges.FirstOrDefault(c => c.Placement == target);
            var room = proposed?.Room ?? target.Room;
            Reply($"{Description(target)}: expected class size {target.Section.SizeForRoom}; minimum room capacity {Math.Max(target.Section.SizeForRoom, target.Section.MinimumRoomCapacity)}. " +
                (room == null ? "No room is currently assigned." :
                $"{(proposed != null ? "The selected preview uses" : "The current assignment uses")} {room.DisplayName}, {room.Capacity} seats; {room.Capacity - target.Section.SizeForRoom} spare seats.") +
                (proposed != null ? " The preview has not been applied." : ""));
            return true;
        }
        // Instructor-only questions should list their sections, rather than guess one.
        if (Regex.IsMatch(text, @"\b(?:teach|teaches|teaching|instructor|professor|faculty)\b", Ignore) &&
            !Regex.IsMatch(text, @"\b(move|swap|find|put|change|adjust)\b", Ignore))
        {
            var courses = FindCourses(text);
            if (courses.Count == 0) return false;
            _referencedCourses = courses.ToList(); _lastTarget = courses.Count == 1 ? courses[0] : null;
            Reply(string.Join("\n", courses.Select(p => $"{Description(p)}: {p.Time?.ToString() ?? "no time"}, {RoomLabel(p.Room)}.")));
            return true;
        }
        return false;
    }

    private string DescribeRoom(Room room, Placement? target)
    {
        var occupants = _plan!.Placements.Concat(_plan.Discussions).Where(p => p.Room == room).ToList();
        string description = $"{room.DisplayName}: {room.Capacity} seats; " +
            (room.LectureCapture ? "lecture capture available" : "no lecture capture listed") +
            (_plan.Rules.Offline.IsOffline(room) ? "; offline" : "; active") +
            $"; {occupants.Count} assignment(s) in the loaded plan.";
        if (target != null)
        {
            int minimum = Math.Max(target.Section.SizeForRoom, target.Section.MinimumRoomCapacity);
            description += $" For {target.Section.Course}: " + (room.Capacity < minimum ? $"too small (at least {minimum} seats required)." :
                $"{room.Capacity - target.Section.SizeForRoom} spare seats for expected class size {target.Section.SizeForRoom}.");
            if (target.Time != null)
            {
                var blockers = occupants.Where(p => p != target && p.Time?.Overlaps(target.Time) == true).ToList();
                description += blockers.Count == 0 ? " No other section in this plan overlaps its current time." :
                    " Occupied at its current time by " + string.Join("; ", blockers.Select(Description)) + ".";
            }
        }
        return description;
    }

    private static string RoomLabel(Room? room) => room == null ? "no room" : $"{room.DisplayName} ({room.Capacity} seats)";

    // Suggest spelling corrections, but require confirmation before using one.
    // Course numbers and short abbreviations are never corrected speculatively.
    private List<Placement> SimilarCourses(string text)
    {
        if (Regex.IsMatch(text, @"\b\d{3}[a-z]?\b", Ignore)) return new();
        var words = Regex.Matches(text.ToLowerInvariant(), @"[a-z]+")
            .Select(m => m.Value).Where(w => w.Length >= 5 && !Stop.Contains(w)).ToList();
        return _plan!.Placements.Where(p =>
        {
            var names = Regex.Matches((p.Section.Title + " " + p.Section.Surname).ToLowerInvariant(), @"[a-z]+")
                .Select(m => m.Value).Where(w => w.Length >= 5);
            return names.Any(name => words.Any(word => OneEditApart(word, name)));
        }).Take(4).ToList();
    }

    private static bool OneEditApart(string first, string second)
    {
        if (Math.Abs(first.Length - second.Length) > 1 || first == second) return false;
        if (first.Length > second.Length) (first, second) = (second, first);
        int a = 0, b = 0, edits = 0;
        while (a < first.Length && b < second.Length)
        {
            if (first[a] == second[b]) { a++; b++; continue; }
            if (++edits > 1) return false;
            if (first.Length == second.Length) a++;
            b++;
        }
        return edits + (b < second.Length ? 1 : 0) == 1;
    }

    private string OptionFit(Planner.OverrideProposal proposal)
    {
        var change = proposal.ProposedChanges.FirstOrDefault(c => c.Placement == _target);
        if (change == null) return ExplainChanges(proposal);
        int size = change.Placement.Section.SizeForRoom;
        return $"{change.Room.Capacity} seats for expected class size {size} ({change.Room.Capacity - size} spare); " +
            (Equals(change.Time, change.BeforeTime) ? "keeps the meeting time; " : "changes the meeting time; ") +
            $"{proposal.ProposedChanges.Count} section(s) affected.\n" + ExplainChanges(proposal);
    }
}
