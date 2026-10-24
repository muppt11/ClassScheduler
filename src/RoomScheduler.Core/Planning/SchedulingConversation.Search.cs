using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RoomScheduler;

public sealed partial class SchedulingConversation
{
    private readonly SearchFilters _filters = new();
    private readonly List<(string Label, Planner.OverrideProposal Proposal)> _moveOptions = new();
    private bool _findRoom;
    private int _selectedOption;
    private Room? _availabilityRoom;
    private bool _availabilitySearch;
    public string? RequestContext => _target == null ? null :
        $"Working with {Description(_target)}. Current assignment: {_target.Room?.DisplayName ?? "no room"}, {_target.Time?.ToString() ?? "no time"}." +
        (_roomExplicit && _room != null ? " Requested room: " + _room.DisplayName + "." : "") +
        (_timeExplicit && _time != null ? " Requested time: " + _time + "." : "") + _filters.Describe();

    private sealed class SearchFilters
    {
        public string? Building;
        public HashSet<string> ExcludedBuildings = new(StringComparer.OrdinalIgnoreCase);
        public int? MinSeats, MaxSeats;
        public bool Recording;
        public TimeSpan? After, Before;
        public List<DayOfWeek>? Days;
        public HashSet<DayOfWeek> ExcludedDays = new();
        public bool SameTime, SameRoom, NoDisplacement;
        public int Direction, RoomSize;
        public void Clear()
        {
            Building = null; After = Before = null; Days = null; ExcludedDays.Clear(); ExcludedBuildings.Clear();
            MinSeats = MaxSeats = null; Recording = false;
            SameTime = SameRoom = NoDisplacement = false; Direction = RoomSize = 0;
        }
        public bool Accepts(MeetingTime t, Placement target) =>
            (After == null || t.Start >= After) && (Before == null || t.End <= Before) &&
            !t.Days.Any(ExcludedDays.Contains) && (Days == null || t.Days.OrderBy(d => d).SequenceEqual(Days.OrderBy(d => d))) &&
            (!SameTime || Equals(t, target.Time)) &&
            (Direction == 0 || target.Time == null || (Direction > 0 ? t.Start > target.Time.Start : t.Start < target.Time.Start));
        public bool Accepts(Room r, Placement target) =>
            (Building == null || r.Building.Equals(Building, StringComparison.OrdinalIgnoreCase)) &&
            !ExcludedBuildings.Contains(r.Building) && (MinSeats == null || r.Capacity >= MinSeats) &&
            (MaxSeats == null || r.Capacity <= MaxSeats) && (!Recording || r.LectureCapture) &&
            (!SameRoom || r == target.Room) && (RoomSize == 0 || target.Room == null ||
                (RoomSize > 0 ? r.Capacity > target.Room.Capacity : r.Capacity < target.Room.Capacity));
        public string Describe()
        {
            var parts = new List<string>();
            if (Building != null) parts.Add("in " + Building);
            if (ExcludedBuildings.Count > 0) parts.Add("outside " + string.Join(", ", ExcludedBuildings));
            if (MinSeats != null) parts.Add($"at least {MinSeats} seats");
            if (MaxSeats != null) parts.Add($"at most {MaxSeats} seats");
            if (Recording) parts.Add("with lecture capture");
            if (Days != null) parts.Add("on " + string.Join(", ", Days));
            if (ExcludedDays.Count > 0) parts.Add("excluding " + string.Join(", ", ExcludedDays.OrderBy(d => d)));
            if (After != null) parts.Add("starting at or after " + MeetingTime.ClockTime(After.Value));
            if (Before != null) parts.Add("ending by " + MeetingTime.ClockTime(Before.Value));
            if (Direction != 0) parts.Add(Direction > 0 ? "later than the current start" : "earlier than the current start");
            if (SameTime) parts.Add("keeping the current meeting time");
            if (SameRoom) parts.Add("keeping the current room");
            if (RoomSize != 0) parts.Add(RoomSize > 0 ? "a larger room" : "a smaller room");
            if (NoDisplacement) parts.Add("without moving other sections");
            return parts.Count == 0 ? "" : " Search conditions: " + string.Join("; ", parts) + ".";
        }
    }

    private static string Normalize(string text)
    {
        text = Regex.Replace(text, @"\b([ap])\.m\.(?=\s|$|[?!,])", "$1m", Ignore);
        text = Regex.Replace(text, @"\b(Mondays|Tuesdays|Wednesdays|Thursdays|Fridays)\b", m => m.Value[..^1], Ignore);
        text = Regex.Replace(text, @"\bTues\b", "Tuesday", Ignore);
        text = Regex.Replace(text, @"\bThurs?\b", "Thursday", Ignore);
        text = Regex.Replace(text, @"\b([A-Za-z]{2,5})(\d+\.\d+)\b", "$1 $2");
        return text;
    }

    private static bool WantsOptions(string text) => Regex.IsMatch(text,
        @"\b(other|another|different|more|better)\s+(?:room\s+|time\s+|swap\s+)?(?:options?|choices?|alternatives?|suggestions?)\b|\b(something else|try (?:a different|another) one|next option)\b", Ignore);

    private static int OptionNumber(string text)
    {
        var number = Regex.Match(text.Trim(), @"^(?:(?:choose|show|try|use|select|pick|option|number)\s+)*(?:(\d+)|(?:the\s+)?(first|second|third))(?:\s+(?:one|option))?[.!]?$", Ignore);
        if (!number.Success) return -1;
        if (number.Groups[1].Success)
        {
            if (!int.TryParse(number.Groups[1].Value, out int value) || value == 0 || value >= 100 && !Regex.IsMatch(text, @"\b(option|number)\b", Ignore)) return -1;
            return value - 1;
        }
        return number.Groups[2].Value.ToLowerInvariant() switch { "first" => 0, "second" => 1, _ => 2 };
    }

    private bool ChooseOption(string text)
    {
        var options = _swapOptions.Count > 0 ? _swapOptions : _moveOptions;
        if (options.Count == 0 || _awaiting is "course" or "swap-partner") return false;
        int index = options.FindIndex(o => o.Label.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (index < 0) index = OptionNumber(text);
        if (index < 0 && WantsOptions(text)) index = _selectedOption + 1;
        if (index < 0) return false;
        if (index >= options.Count)
        {
            Reply("Those are all the checked options from this search. Tell me a different room, building, day, or time window to search again; your current preview is still below.", options.Select(o => o.Label).ToArray());
            return true;
        }
        _selectedOption = index; Proposal = options[index].Proposal;
        Reply($"Selected option {index + 1}. {ExplainChanges(Proposal)} Nothing has changed yet; review the preview and click Apply reviewed changes.", options.Select(o => o.Label).ToArray());
        return true;
    }

    private static string ExplainChanges(Planner.OverrideProposal proposal) =>
        string.Join("\n", proposal.ProposedChanges.Select(c =>
            $"{Description(c.Placement)}: {c.BeforeRoom?.DisplayName ?? "no room"}, {c.BeforeTime?.ToString() ?? "no time"} → {c.Room.DisplayName}, {c.Time}."));

    private void AnswerAvailability(Room room, MeetingTime time)
    {
        var conflicts = _plan!.Placements.Concat(_plan.Discussions).Where(p => p.Room == room && p.Time?.Overlaps(time) == true).ToList();
        Reply(_plan.Rules.Offline.IsOffline(room) ? $"{room.DisplayName} is offline." : conflicts.Count == 0
            ? $"No classes in the current plan use {room.DisplayName} at {time}. This only checks this uploaded plan, not the registrar's campus-wide bookings. Capacity and instructor constraints still need checking for a specific course."
            : $"{room.DisplayName} is occupied at {time}: " + string.Join("; ", conflicts.Select(Description)));
    }

    private void AnswerFreeRooms(string text, IReadOnlyList<Room> rooms)
    {
        var time = ParseTime(text, null);
        if (time == null)
        { _availabilitySearch = true; Reply("What days and start/end time should I check? For example, ‘Tuesday and Thursday 2pm–3pm’. I need the full interval to check availability."); return; }
        var all = _plan!.Placements.Concat(_plan.Discussions).ToList();
        var available = rooms.Where(r => !_plan.Rules.Offline.IsOffline(r) &&
            !all.Any(p => p.Room == r && p.Time?.Overlaps(time) == true)).OrderBy(r => r.Capacity).ThenBy(r => r.DisplayName).ToList();
        Reply(available.Count == 0 ? $"No active rooms are free at {time} in the current plan." :
            $"{available.Count} active room(s) have no overlapping class at {time}. Here are the first {Math.Min(8, available.Count)} by capacity:\n" +
            string.Join("\n", available.Take(8).Select(r => $"{r.DisplayName}: {r.Capacity} seats")) +
            "\nThis checks the current plan, not campus-wide bookings. Name a course for capacity, accommodation, and instructor checks.");
    }

    // Returns the remaining exact-time text. Bounds are constraints, never interpreted as exact times.
    private string ReadFilters(string text, IReadOnlyList<Room> rooms)
    {
        text = Regex.Replace(text, @"\bbetween\s+(\d{1,2}(?::[0-5]\d)?\s*(?:am|pm))\s+and\s+(\d{1,2}(?::[0-5]\d)?\s*(?:am|pm))\b", "after $1 before $2", Ignore);
        if (Regex.IsMatch(text, @"\b(any building|anywhere|any room)\b", Ignore))
        { _filters.Building = null; _filters.ExcludedBuildings.Clear(); _filters.SameRoom = false; _room = null; _roomExplicit = false; }
        foreach (string building in rooms.Select(r => r.Building).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Regex.IsMatch(text, $@"\b(?:not in|avoid|outside|except|excluding)\s+{Regex.Escape(building)}\b", Ignore))
            {
                _filters.ExcludedBuildings.Add(building);
                if (_filters.Building == building) _filters.Building = null;
                _findRoom = true; continue;
            }
            if (Regex.IsMatch(text, $@"\b(?:in|within|only|prefer|use)\s+{Regex.Escape(building)}\b", Ignore))
            {
                _filters.Building = building;
                if (_room?.Building != building) { _room = null; _roomExplicit = false; }
            }
        }
        foreach (var match in Regex.Matches(text, @"\b(at least|at most|no more than|minimum of|maximum of)\s+(\d+)\s+seats\b", Ignore).Cast<Match>())
            if (int.TryParse(match.Groups[2].Value, out int seats))
            {
                if (match.Groups[1].Value.Equals("at least", StringComparison.OrdinalIgnoreCase) || match.Groups[1].Value.StartsWith("minimum", StringComparison.OrdinalIgnoreCase)) _filters.MinSeats = seats;
                else _filters.MaxSeats = seats;
                _findRoom = true;
            }
        if (Regex.IsMatch(text, @"\b(with (?:recording|lecture capture)|needs? (?:recording|lecture capture))\b", Ignore))
        { _filters.Recording = true; _findRoom = true; }
        if (Regex.IsMatch(text, @"\b(?:same|current|existing) (?:meeting )?time\b|\bkeep (?:its |the )?time\b", Ignore))
        { _filters.SameTime = true; _filters.Direction = 0; _time = _target?.Time; _timeExplicit = false; _findSlot = false; _findRoom = true; }
        if (Regex.IsMatch(text, @"\b(?:same|current|existing) room\b", Ignore))
        { _filters.SameRoom = true; _room = _target?.Room; _roomExplicit = false; }
        if (Regex.IsMatch(text, @"\b(?:without (?:moving|displacing)|don'?t (?:move|displace)) (?:any |the )?other (?:classes|sections|courses)\b", Ignore)) _filters.NoDisplacement = true;
        if (Regex.IsMatch(text, @"\b(?:allow|okay to|ok to) (?:moving|displacing|move) other (?:classes|sections|courses)\b", Ignore)) _filters.NoDisplacement = false;
        if (Regex.IsMatch(text, @"\b(smaller|bigger|larger) (?:class)?room\b", Ignore))
        { _filters.RoomSize = Regex.IsMatch(text, @"\bsmaller\b", Ignore) ? -1 : 1; _findRoom = true; if (!_roomExplicit) _room = null; }
        string relativeText = Regex.Replace(text, @"\bno (?:earlier|later) than\b", "", Ignore);
        if (Regex.IsMatch(relativeText, @"\b(later|earlier)\b", Ignore))
        { _filters.Direction = Regex.IsMatch(relativeText, @"\bearlier\b", Ignore) ? -1 : 1; _filters.SameTime = false; _findSlot = true; _timeExplicit = false; }
        if (Regex.IsMatch(text, @"\b(any time|any day)\b", Ignore))
        { _filters.After = _filters.Before = null; _filters.Days = null; _filters.ExcludedDays.Clear(); _filters.Direction = 0; _filters.SameTime = false; }

        string remaining = Regex.Replace(text, @"\b(after|before|by|not before|no earlier than|no later than)\s+(\d{1,2}(?::[0-5]\d)?\s*(?:am|pm))\b", m =>
        {
            var time = PreferenceParser.FindTimes("at " + m.Groups[2].Value, TimeSpan.FromMinutes(30), new[] { DayOfWeek.Monday }).FirstOrDefault().Time;
            if (time == null) return m.Value;
            if (Regex.IsMatch(m.Groups[1].Value, @"^(after|not before|no earlier than)$", Ignore)) _filters.After = time.Start;
            else _filters.Before = time.Start;
            _findSlot = true; _timeExplicit = false;
            return " ";
        }, Ignore);
        const string weekday = @"(?:Monday|Tuesday|Wednesday|Thursday|Friday|Mon|Tue|Wed|Thu|Fri)\b";
        remaining = Regex.Replace(remaining, @"\b(?:no|not|except|excluding|avoid|without)\s+(" + weekday + @"(?:\s*(?:,|and|or)\s*" + weekday + @")*)", m =>
        {
            foreach (Match day in Regex.Matches(m.Groups[1].Value, weekday, Ignore))
                foreach (var parsed in MeetingTime.ParseDays(day.Value)) _filters.ExcludedDays.Add(parsed);
            _findSlot = true; _timeExplicit = false;
            return " ";
        }, Ignore);
        var named = Regex.Matches(remaining, @"\b(Monday|Tuesday|Wednesday|Thursday|Friday|Mon|Tue|Wed|Thu|Fri|(?:TH|M|T|W|F)+)\b", Ignore);
        if (_findSlot && named.Count > 0)
        {
            var days = named.SelectMany(m => MeetingTime.ParseDays(m.Value)).Distinct().OrderBy(d => d).ToList();
            var range = Regex.Match(remaining, @"\b(Monday|Tuesday|Wednesday|Thursday|Friday)\s+(?:through|to)\s+(Monday|Tuesday|Wednesday|Thursday|Friday)\b", Ignore);
            if (range.Success)
            {
                int start = (int)Enum.Parse<DayOfWeek>(range.Groups[1].Value, true), end = (int)Enum.Parse<DayOfWeek>(range.Groups[2].Value, true);
                if (end >= start) days = Enumerable.Range(start, end - start + 1).Select(d => (DayOfWeek)d).ToList();
            }
            _filters.Days = days;
        }
        return remaining;
    }

    private void PreviewMoves(IReadOnlyList<Room> rooms, Func<string, bool, Planner.OverrideProposal> propose,
        Func<Placement, Room, MeetingTime, bool, Planner.OverrideProposal>? proposeMove)
    {
        var target = _target!;
        _moveOptions.Clear(); _swapOptions.Clear(); Proposal = null; _selectedOption = 0;
        if (!_findRoom && !_findSlot && (_room == null || _room == target.Room) && _time != null && Equals(_time, target.Time))
        { Reply($"{target.Section.Title} is already in {target.Room?.DisplayName} at {_time}. No changes are needed."); return; }
        var candidates = rooms.Where(r => _filters.Accepts(r, target) &&
            r.Capacity >= Math.Max(target.Section.SizeForRoom, target.Section.MinimumRoomCapacity) && !_plan!.Rules.Offline.IsOffline(r) &&
            (!_gdcHours || r.Building == _plan.Rules.HomeBuilding && r.CsOwnsFrom != null) && (_room == null || !_roomExplicit || r == _room)).ToList();
        if (_roomExplicit && _room != null && _filters.Accepts(_room, target)) candidates = new() { _room };
        if (_filters.SameRoom && target.Room == null || _filters.SameTime && target.Time == null)
        { Reply("That section has no current room/time to keep. Give me a room or meeting time, or say ‘any room, any time’."); return; }
        if (_gdcHours && _roomExplicit && _room != null && (_room.Building != _plan!.Rules.HomeBuilding || _room.CsOwnsFrom == null))
        { Reply($"{_room.DisplayName} isn't marked as a CS-controlled GDC room in the inventory. Choose one of those rooms, or change the request."); return; }
        if (_filters.After != null && _filters.Before != null && _filters.After >= _filters.Before)
        { Reply("That time window is empty: the start must be earlier than the finish. Tell me a different ‘after’ or ‘before’ time, or say ‘any time’." + _filters.Describe()); return; }

        var times = _findSlot && !_filters.SameTime
            ? SearchTimes(target, _gdcHours ? _plan!.Rules.CsOwnsGdcFrom : TimeSpan.FromHours(8), _filters.Days)
            : new List<MeetingTime> { _time ?? target.Time! };
        var all = _plan!.Placements.Concat(_plan.Discussions).ToList();
        var combinations = (from time in times.Where(t => t != null && _filters.Accepts(t, target) && (!_gdcHours || t.Start >= _plan.Rules.CsOwnsGdcFrom))
            from room in candidates
            let occupied = all.Count(p => p != target && p.Room == room && p.Time?.Overlaps(time) == true)
            where !(_filters.NoDisplacement && occupied > 0)
            where room != target.Room || !Equals(time, target.Time)
            orderby occupied, Equals(time, target.Time) ? 0 : 1, room == target.Room ? 0 : 1,
                target.Time == null ? 0 : Math.Abs((time.Start - target.Time.Start).TotalMinutes),
                Math.Abs(room.Capacity - Math.Max(target.Section.SizeForRoom, target.Section.PreferredRoomCapacity)), room.DisplayName
            select (time, room)).Take(600).ToList();
        var failures = new List<string>();
        foreach (var (time, room) in combinations)
        {
            try
            {
                string selector = $"{target.Section.Title} / {target.Section.Instructor} / row {target.Section.SourceRow + 1}";
                var proposal = proposeMove != null ? proposeMove(target, room, time, _oversized) :
                    propose($"Put {selector} in {room.DisplayName} {time.Pattern} at {time.Clock}", _oversized);
                if (proposal.ProposedChanges.Count == 0 || _filters.NoDisplacement && proposal.ProposedChanges.Any(c => c.Placement != target)) continue;
                string label = $"Option {_moveOptions.Count + 1}: {room.DisplayName} · {time} · {proposal.ProposedChanges.Count} section(s) change";
                _moveOptions.Add((label, proposal));
                if (_moveOptions.Count == 9) break;
            }
            catch (ArgumentException ex) { if (!failures.Contains(ex.Message)) failures.Add(ex.Message); }
        }
        if (_moveOptions.Count > 0)
        {
            var ranked = _moveOptions.OrderBy(o => o.Proposal.ProposedChanges.Count).Take(3).Select(o => o.Proposal).ToList();
            _moveOptions.Clear();
            foreach (var option in ranked)
            {
                var destination = option.ProposedChanges.First(c => c.Placement == target);
                _moveOptions.Add(($"Option {_moveOptions.Count + 1}: {destination.Room.DisplayName} · {destination.Time} · {option.ProposedChanges.Count} section(s) change", option));
            }
            Proposal = _moveOptions[0].Proposal; _awaiting = "";
            // Keep explicit user constraints separate from the automatically selected option.
            var first = Proposal.ProposedChanges.First(c => c.Placement == target);
            Reply($"I read this as moving {target.Section.Title} ({target.Section.Instructor}) to {first.Room.DisplayName}, {first.Time}. " +
                $"I found {_moveOptions.Count} checked option(s), preferring fewer affected sections. {_filters.Describe()} " +
                $"{Proposal.ProposedChanges.Count} section(s) would change. " + (_moveOptions.Count > 1 ? "Choose an option below or say ‘the second one’. " : "The checked option is shown below. ") + "Review before applying; nothing has changed yet.",
                _moveOptions.Select(o => o.Label).Append("Find room swaps").Append("Cancel").ToArray()); return;
        }
        if (_roomExplicit && failures.Any(f => f.Contains("larger than needed", StringComparison.OrdinalIgnoreCase)))
        {
            _awaiting = "oversized";
            Reply($"{_room!.DisplayName} is much larger than {target.Section.Title} needs. Should I allow an intentional oversized-room exception for this request?", "Yes, allow the larger room", "Cancel"); return;
        }
        string reason = failures.Count > 0 ? string.Join("\n", failures.Take(3)) :
            "No rooms and meeting times match those conditions and the required capacity. Try a different building, room size, or time window.";
        Reply($"I couldn't find a valid move for {target.Section.Title}.{_filters.Describe()}\n{reason}\nYour schedule is unchanged. " +
            "I won't relax those conditions without your request. Try a swap, or tell me what to change.", "Find room swaps", "Find room and time swaps", "Find a free slot", "Cancel");
    }
}
