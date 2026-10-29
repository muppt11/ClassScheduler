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
        public long? MinSeats, MaxSeats;
        public string? Error;
        public bool PreferSmallest;
        public bool Recording, UnusedRoom;
        public TimeSpan? After, Before;
        public List<DayOfWeek>? Days;
        public HashSet<DayOfWeek> ExcludedDays = new();
        public bool SameTime, SameRoom, NoDisplacement;
        public int? DayCount;
        public bool PreserveWeeklyHours, SameStart;
        public TimeSpan? Duration, StartAt, EndAt, PeriodStart, PeriodEnd;
        public string? Period;
        public int Direction, RoomSize;
        public void Clear()
        {
            Building = null; After = Before = null; Days = null; ExcludedDays.Clear(); ExcludedBuildings.Clear();
            MinSeats = MaxSeats = null; Error = null; PreferSmallest = false; Recording = UnusedRoom = false;
            SameTime = SameRoom = NoDisplacement = PreserveWeeklyHours = SameStart = false; Direction = RoomSize = 0;
            DayCount = null; Duration = StartAt = EndAt = PeriodStart = PeriodEnd = null; Period = null;
        }
        public bool Accepts(MeetingTime t, Placement target) =>
            (After == null || t.Start >= After) && (Before == null || t.End <= Before) &&
            (PeriodStart == null || t.Start >= PeriodStart) && (PeriodEnd == null || t.End <= PeriodEnd) &&
            (DayCount == null || t.Days.Count == DayCount) && (StartAt == null || t.Start == StartAt) && (EndAt == null || t.End == EndAt) &&
            (!SameStart || target.Time == null || t.Start == target.Time.Start) &&
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
            if (UnusedRoom) parts.Add("a room with no assignments in the current plan (including discussions)");
            if (DayCount != null) parts.Add($"{DayCount} meeting day(s) per week");
            if (PreserveWeeklyHours) parts.Add("preserving the current weekly hours unless an explicit duration/end time overrides them");
            if (Duration != null) parts.Add($"{Duration.Value.TotalMinutes:0.##} minutes per meeting");
            if (StartAt != null) parts.Add("starting exactly at " + MeetingTime.ClockTime(StartAt.Value));
            if (EndAt != null) parts.Add("ending exactly at " + MeetingTime.ClockTime(EndAt.Value));
            if (SameStart) parts.Add("keeping the current start time");
            if (Period != null) parts.Add($"{Period} ({MeetingTime.ClockTime(PeriodStart!.Value)}–{MeetingTime.ClockTime(PeriodEnd!.Value)})");
            if (Days != null) parts.Add("on " + string.Join(", ", Days));
            if (ExcludedDays.Count > 0) parts.Add("excluding " + string.Join(", ", ExcludedDays.OrderBy(d => d)));
            if (After != null) parts.Add("starting at or after " + MeetingTime.ClockTime(After.Value));
            if (Before != null) parts.Add("ending by " + MeetingTime.ClockTime(Before.Value));
            if (Direction != 0) parts.Add(Direction > 0 ? "later than the current start" : "earlier than the current start");
            if (SameTime) parts.Add("keeping the current meeting time");
            if (SameRoom) parts.Add("keeping the current room");
            if (RoomSize != 0) parts.Add(RoomSize > 0 ? "a larger room" : "a smaller room");
            if (PreferSmallest) parts.Add("preferring the smallest feasible room");
            if (NoDisplacement) parts.Add("without moving other sections");
            return parts.Count == 0 ? "" : " Search conditions: " + string.Join("; ", parts) + ".";
        }
    }

    private static string Normalize(string text)
    {
        text = text.Replace('’', '\'');
        text = Regex.Replace(text, @"\b(?:macnhge|chnage|chagne|cahnge)\b", "change", Ignore);
        text = Regex.Replace(text, @"\btimings\b", "times", Ignore);
        text = Regex.Replace(text, @"\b(one|single|two|three|four|five|[0-9]+)[- ]?days?\b", "$1 day", Ignore);
        text = Regex.Replace(text, @"\bnoon\b", "12pm", Ignore);
        text = Regex.Replace(text, @"\bhalf (?:an? )?hour\b", "30 minutes", Ignore);
        text = Regex.Replace(text, @"\ban hour\b", "1 hour", Ignore);
        text = Regex.Replace(text, @"\b(one|two|three|four|five|six)\s+(hours?|minutes?)\b", m =>
            ((Array.IndexOf(new[] { "one", "two", "three", "four", "five", "six" }, m.Groups[1].Value.ToLowerInvariant()) + 1).ToString()) + " " + m.Groups[2].Value, Ignore);
        text = Regex.Replace(text, @"\b(\d{1,2}(?::[0-5]\d)?\s*(?:am|pm)?)\s+to\s+(\d{1,2}(?::[0-5]\d)?\s*(?:am|pm))\b", "$1-$2", Ignore);
        text = Regex.Replace(text, @"\b([ap])\.m\.(?=\s|$|[?!,])", "$1m", Ignore);
        text = Regex.Replace(text, @"\b(Mondays|Tuesdays|Wednesdays|Thursdays|Fridays)\b", m => m.Value[..^1], Ignore);
        text = Regex.Replace(text, @"\bTues\b", "Tuesday", Ignore);
        text = Regex.Replace(text, @"\bThurs?\b", "Thursday", Ignore);
        text = Regex.Replace(text, @"\b([A-Za-z]{2,5})(\d+\.\d+)\b", "$1 $2");
        text = Regex.Replace(text, @"\b(?:classroom|classrooms)\b", m => m.Value.EndsWith("s", StringComparison.OrdinalIgnoreCase) ? "rooms" : "room", Ignore);
        text = Regex.Replace(text, @"\bbetween\s+(\d+)\s+and\s+(\d+)\s+(seats?|people|students?)\b", "at least $1 $3 and at most $2 $3", Ignore);
        text = Regex.Replace(text, @"\b(\d+)\s*(seats?|people|students?)\s*(?:max|maximum|or fewer|or less)\b", "at most $1 $2", Ignore);
        text = Regex.Replace(text, @"\bfrom\s+(\d{1,2}(?::[0-5]\d)?\s*(?:am|pm))\s+to\s+(\d{1,2}(?::[0-5]\d)?\s*(?:am|pm))\b", "$1-$2", Ignore);
        return text;
    }

    private static bool WantsRoomSearch(string text) => Regex.IsMatch(text,
        @"\b(?:smallest|smaller|bigger|larger|unused|free|available|unoccupied)\s+(?:class)?room\b|\broom\s+(?:(?:that|which)\s+)?(?:is|that's)\s+(?:smaller|bigger|larger|unused|too (?:large|big|small))\b|\b(?:under|over|at most|at least|no more than|no less than|less than|fewer than|more than|maximum of|minimum of)\s+\d+\s*(?:seats?|people|students?)\b|\b(?:has not|hasn't|not) been used\b|\b(?:smallest|fewest disruptions|fewest changes)\b", Ignore);

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
        if (index < 0 && Regex.IsMatch(text, @"^(?:show |choose |pick |use )?(?:the )?(?:best|recommended)(?: one| option)?[.!?]?$", Ignore)) index = 0;
        if (index < 0) return false;
        if (index >= options.Count)
        {
            Reply("Those are all the checked options from this search. Tell me a different room, building, day, or time window to search again; your current preview is still below.", options.Select(o => o.Label).ToArray());
            return true;
        }
        _selectedOption = index; Proposal = options[index].Proposal;
        Reply($"Selected option {index + 1}. {OptionFit(Proposal)} Nothing has changed yet; review the preview and click Apply reviewed changes.", options.Select(o => o.Label).ToArray());
        return true;
    }

    private static string ExplainChanges(Planner.OverrideProposal proposal) =>
        string.Join("\n", proposal.ProposedChanges.Select(c =>
            $"{Description(c.Placement)}: {RoomLabel(c.BeforeRoom)}, {c.BeforeTime?.ToString() ?? "no time"} → {RoomLabel(c.Room)}, {c.Time}.")) + " " + WeeklyHoursNote(proposal);

    private void AnswerAvailability(Room room, MeetingTime time)
    {
        var conflicts = _plan!.Placements.Concat(_plan.Discussions).Where(p => p.Room == room && p.Time?.Overlaps(time) == true).ToList();
        Reply(_plan.Rules.Offline.IsOffline(room) ? $"{room.DisplayName} is offline." : conflicts.Count == 0
            ? $"No classes in the current plan use {room.DisplayName} at {time}. This only checks this uploaded plan, not the registrar's campus-wide bookings. Capacity and instructor constraints still need checking for a specific course."
            : $"{room.DisplayName} is occupied at {time}: " + string.Join("; ", conflicts.Select(Description)));
    }

    private void AnswerFreeRooms(string text, IReadOnlyList<Room> rooms)
    {
        string details = _availabilityDetails.Length == 0 ? text : _availabilityDetails + " " + text;
        var time = ParseTime(details, null);
        if (time == null)
        { _availabilityDetails = details; _availabilitySearch = true; Reply("What days and start/end time should I check? For example, ‘Tuesday and Thursday 2pm–3pm’. I need the full interval to check availability."); return; }
        var filters = RoomQueryFilters(details, rooms);
        _availabilityDetails = "";
        if (filters.Error != null) { Reply(filters.Error); return; }
        if (filters.MinSeats > filters.MaxSeats) { Reply("The minimum seat count exceeds the maximum. Please give a different seat range."); return; }
        var all = _plan!.Placements.Concat(_plan.Discussions).ToList();
        var context = _target ?? _lastTarget ?? new Placement { Section = new SectionRequest() };
        var available = rooms.Where(r => filters.Accepts(r, context) && !_plan.Rules.Offline.IsOffline(r) &&
            (!filters.UnusedRoom || !all.Any(p => p.Room == r)) &&
            !all.Any(p => p.Room == r && p.Time?.Overlaps(time) == true)).OrderBy(r => r.Capacity).ThenBy(r => r.DisplayName).ToList();
        Reply(available.Count == 0 ? $"No active rooms match those conditions at {time} in the current plan." + filters.Describe() :
            $"{available.Count} active room(s) have no overlapping class at {time}." + filters.Describe() + $" Here are the first {Math.Min(8, available.Count)} by capacity:\n" +
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
                _filters.Building = building; _filters.ExcludedBuildings.Remove(building);
                if (_room?.Building != building) { _room = null; _roomExplicit = false; }
            }
        }
        if (Regex.IsMatch(text, @"\b(any size|remove (?:the )?seat limits?|no seat limit)\b", Ignore))
        { _filters.MinSeats = _filters.MaxSeats = null; _filters.Error = null; _filters.RoomSize = 0; _findRoom = true; }
        if (ReadSeatBounds(text, _filters)) _findRoom = true;
        if (Regex.IsMatch(text, @"\bsmallest\b", Ignore)) { _filters.PreferSmallest = true; _findRoom = true; }
        if (Regex.IsMatch(text, @"\b(?:fewest disruptions|fewest changes|least disruption)\b", Ignore)) _filters.PreferSmallest = false;
        if (Regex.IsMatch(text, @"\b(?:free|available|unoccupied) room\b", Ignore))
        { _filters.NoDisplacement = true; _findRoom = true; }
        if (Regex.IsMatch(text, @"\b(?:unused|never used)\b|\b(?:has not|hasn't|not) been used\b", Ignore))
        { _filters.UnusedRoom = true; _findRoom = true; }
        if (Regex.IsMatch(text, @"\b(?:already used|allow used rooms)\b", Ignore)) _filters.UnusedRoom = false;
        if (Regex.IsMatch(text, @"\b(with (?:recording|lecture capture)|needs? (?:recording|lecture capture))\b", Ignore))
        { _filters.Recording = true; _findRoom = true; }
        if (Regex.IsMatch(text, @"\b(?:same|current|existing) (?:meeting )?time\b|\bkeep (?:its |the )?time\b", Ignore))
        { _filters.SameTime = true; _filters.Direction = 0; _time = _target?.Time; _timeExplicit = false; _findSlot = false; _findRoom = true; }
        if (Regex.IsMatch(text, @"\b(?:same|current|existing) room\b", Ignore))
        { _filters.SameRoom = true; _room = _target?.Room; _roomExplicit = false; }
        if (Regex.IsMatch(text, @"\b(?:without (?:moving|displacing)|don'?t (?:move|displace)) (?:any |the )?other (?:classes|sections|courses)\b", Ignore)) _filters.NoDisplacement = true;
        if (Regex.IsMatch(text, @"\b(?:allow|okay to|ok to) (?:moving|displacing|move) other (?:classes|sections|courses)\b", Ignore)) _filters.NoDisplacement = false;
        if (Regex.IsMatch(text, @"\b(smaller|bigger|larger) (?:class)?room\b|\broom\s+(?:(?:that|which)\s+)?(?:is|that's)\s+(?:smaller|bigger|larger|too (?:large|big|small))\b", Ignore))
        { _filters.RoomSize = Regex.IsMatch(text, @"\bsmaller\b|\btoo (?:large|big)\b", Ignore) ? -1 : 1; _findRoom = true; if (!_roomExplicit) _room = null; }
        string relativeText = Regex.Replace(text, @"\bno (?:earlier|later) than\b", "", Ignore);
        if (Regex.IsMatch(relativeText, @"\b(later|earlier)\b", Ignore))
        { _filters.Direction = Regex.IsMatch(relativeText, @"\bearlier\b", Ignore) ? -1 : 1; _filters.SameTime = false; _findSlot = true; _timeExplicit = false; }
        if (Regex.IsMatch(text, @"\bany time\b", Ignore))
        { _filters.StartAt = _filters.EndAt = null; _filters.SameStart = false; _filters.Period = null; _filters.PeriodStart = _filters.PeriodEnd = null; }
        if (Regex.IsMatch(text, @"\b(any time|any day)\b", Ignore))
        { _filters.After = _filters.Before = null; _filters.Days = null; _filters.ExcludedDays.Clear(); _filters.Direction = 0; _filters.SameTime = false; }

        const string allowedDay = @"(?:Monday|Tuesday|Wednesday|Thursday|Friday|Mon|Tue|Wed|Thu|Fri)";
        text = Regex.Replace(text, @"\b(" + allowedDay + @") (?:is |are )?(?:okay|ok|fine|allowed)\b|\ballow (" + allowedDay + @")\b", m =>
        {
            foreach (var day in MeetingTime.ParseDays(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)) _filters.ExcludedDays.Remove(day);
            _findSlot = true; _timeExplicit = false;
            return " ";
        }, Ignore);
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
        var all = _plan!.Placements.Concat(_plan.Discussions).ToList();
        if (_filters.Error != null) { Reply(_filters.Error + " Your schedule is unchanged."); return; }
        if (_filters.MinSeats > _filters.MaxSeats)
        { Reply("The minimum seat count exceeds the maximum. Tell me a different seat limit; your schedule is unchanged." + _filters.Describe()); return; }
        var candidates = rooms.Where(r => _filters.Accepts(r, target) &&
            (!_filters.UnusedRoom || !all.Any(p => p.Room == r)) &&
            r.Capacity >= Math.Max(target.Section.SizeForRoom, target.Section.MinimumRoomCapacity) && !_plan!.Rules.Offline.IsOffline(r) &&
            (!_gdcHours || r.Building == _plan.Rules.HomeBuilding && r.CsOwnsFrom != null) && (_room == null || !_roomExplicit || r == _room)).ToList();
        if (_roomExplicit && _room != null && _filters.Accepts(_room, target) &&
            (!_filters.UnusedRoom || !all.Any(p => p.Room == _room))) candidates = new() { _room };
        if (_filters.SameRoom && target.Room == null || _filters.SameTime && target.Time == null)
        { Reply("That section has no current room/time to keep. Give me a room or meeting time, or say ‘any room, any time’."); return; }
        if (_gdcHours && _roomExplicit && _room != null && (_room.Building != _plan!.Rules.HomeBuilding || _room.CsOwnsFrom == null))
        { Reply($"{_room.DisplayName} isn't marked as a CS-controlled GDC room in the inventory. Choose one of those rooms, or change the request."); return; }
        if (_filters.After != null && _filters.Before != null && _filters.After >= _filters.Before)
        { Reply("That time window is empty: the start must be earlier than the finish. Tell me a different ‘after’ or ‘before’ time, or say ‘any time’." + _filters.Describe()); return; }

        var times = _findSlot && !_filters.SameTime
            ? SearchTimes(target, _gdcHours ? _plan!.Rules.CsOwnsGdcFrom : TimeSpan.FromHours(8), _filters.Days,
                _filters.DayCount, _filters.PreserveWeeklyHours, _filters.Duration, _filters.StartAt, _filters.EndAt)
            : new List<MeetingTime> { _time ?? target.Time! };
        var combinations = (from time in times.Where(t => t != null && _filters.Accepts(t, target) && (!_gdcHours || t.Start >= _plan.Rules.CsOwnsGdcFrom))
            from room in candidates
            let occupied = all.Count(p => p != target && p.Room == room && p.Time?.Overlaps(time) == true)
            where !(_filters.NoDisplacement && occupied > 0)
            where room != target.Room || !Equals(time, target.Time)
            orderby _findSlot && !_timeExplicit && _filters.StartAt == null && _plan.Rules.IsEarlyStart(time),
                _filters.PreferSmallest ? room.Capacity : occupied, occupied, -_plan.Rules.BuildingPriority.Score(room, time), Equals(time, target.Time) ? 0 : 1, room == target.Room ? 0 : 1,
                target.Time == null ? 0 : Math.Abs((time.Start - target.Time.Start).TotalMinutes),
                Math.Abs(room.Capacity - Math.Max(target.Section.SizeForRoom, target.Section.PreferredRoomCapacity)), room.DisplayName
            select (time, room)).Take(600).ToList();
        var failures = new List<string>();
        foreach (var (time, room) in combinations)
        {
            try
            {
                string selector = $"{target.Section.Title} / {target.Section.Instructor} / row {target.Section.SourceRow + 1}";
                if (_filters.DayCount != null && _filters.Days == null && _moveOptions.Any(o =>
                    o.Proposal.ProposedChanges.First(c => c.Placement == target).Time.Pattern == time.Pattern)) continue;
                var proposal = proposeMove != null ? proposeMove(target, room, time, _oversized) :
                    propose($"Put {selector} in {room.DisplayName} {time.Pattern} at {time.Clock}", _oversized);
                if (proposal.ProposedChanges.Count == 0 || _filters.NoDisplacement && proposal.ProposedChanges.Any(c => c.Placement != target)) continue;
                string label = $"Option {_moveOptions.Count + 1}: {room.DisplayName} ({room.Capacity} seats) · {time} · {proposal.ProposedChanges.Count} section(s) change";
                _moveOptions.Add((label, proposal));
                if (_moveOptions.Count == 9) break;
            }
            catch (ArgumentException ex) { if (!failures.Contains(ex.Message)) failures.Add(ex.Message); }
        }
        if (_moveOptions.Count > 0)
        {
            var ranked = _moveOptions.OrderBy(o => _findSlot && !_timeExplicit && _filters.StartAt == null && _plan.Rules.IsEarlyStart(o.Proposal.ProposedChanges.First(c => c.Placement == target).Time)).ThenBy(o => _filters.PreferSmallest ? o.Proposal.ProposedChanges.First(c => c.Placement == target).Room.Capacity : o.Proposal.ProposedChanges.Count).ThenBy(o => o.Proposal.ProposedChanges.Count).Take(3).Select(o => o.Proposal).ToList();
            _moveOptions.Clear();
            foreach (var option in ranked)
            {
                var destination = option.ProposedChanges.First(c => c.Placement == target);
                _moveOptions.Add(($"Option {_moveOptions.Count + 1}: {destination.Room.DisplayName} ({destination.Room.Capacity} seats) · {destination.Time} · {option.ProposedChanges.Count} section(s) change", option));
            }
            Proposal = _moveOptions[0].Proposal; _awaiting = "";
            // Keep explicit user constraints separate from the automatically selected option.
            var first = Proposal.ProposedChanges.First(c => c.Placement == target);
            Reply($"I read this as moving {target.Section.Title} ({target.Section.Instructor}) to {first.Room.DisplayName} ({first.Room.Capacity} seats), {first.Time}. " +
                $"I found {_moveOptions.Count} checked option(s), {(_filters.PreferSmallest ? "preferring smaller rooms" : "preferring fewer affected sections")}. {_filters.Describe()} " +
                (_findSlot && !_timeExplicit && _filters.StartAt == null && _plan.Rules.IsEarlyStart(first.Time) ? "An early start is a fallback: no later start passed this bounded search with your conditions. " : "") +
                WeeklyHoursNote(Proposal) + " " +
                $"Expected class size: {target.Section.SizeForRoom}; {first.Room.Capacity - target.Section.SizeForRoom} spare seats. {Proposal.ProposedChanges.Count} section(s) would change. " +
                (Proposal.ProposedChanges.Count > 1 ? "Every displaced class has a checked replacement room in the preview; all changes apply together. " : "") + (_moveOptions.Count > 1 ? "Choose an option below or say ‘the second one’. " : "The checked option is shown below. ") + "Review before applying; nothing has changed yet.",
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
