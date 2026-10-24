using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RoomScheduler;

public record ChatMessage(bool User, string Text);

/// <summary>Conversation state grounded in the current plan. Never applies a move itself.</summary>
public sealed class SchedulingConversation
{
    public List<ChatMessage> Messages { get; } = new();
    public List<string> Suggestions { get; } = new();
    public Planner.OverrideProposal? Proposal { get; private set; }
    private Planner.Plan? _plan;
    private Placement? _target;
    private Placement? _lastTarget;
    private List<Placement> _candidates = new();
    private Room? _room;
    private bool _roomExplicit, _timeExplicit;
    private MeetingTime? _time;
    private bool _reserved, _gdcHours, _findSlot, _oversized;
    private string _awaiting = "";
    private string _pendingDetails = "";
    private bool _swapRequested, _swapTimes, _swapTimesExplicit;
    private string _swapPartnerQuery = "";
    private List<Placement> _partners = new();
    private readonly List<(string Label, Planner.OverrideProposal Proposal)> _swapOptions = new();
    private static readonly RegexOptions Ignore = RegexOptions.IgnoreCase;
    private static readonly string[] Stop = ("i need want please can could would you me my the a an to be moved move put switch reschedule change swap swaps exchange trade rooms times between with " +
        "it that for from into in on at time class course section reserved cs graduate slot hours gdc after before keep same current " +
        "monday tuesday wednesday thursday friday mon tue wed thu fri mwf tth mw am pm ok okay do yes thanks computer").Split(' ');

    public void Sync(Planner.Plan? plan)
    {
        if (ReferenceEquals(_plan, plan)) return;
        bool hadRequest = _target != null || Proposal != null;
        _plan = plan; ClearRequest(); _lastTarget = null;
        if (hadRequest) Reply("The schedule was rebuilt. I've cleared the previous proposal so the next request uses the new assignments.");
    }

    public void Cancel()
    {
        ClearRequest(); Reply("Canceled. Your schedule has not changed.");
    }

    public void Applied()
    {
        var last = _target;
        ClearRequest(); _lastTarget = last;
        Reply("Applied the reviewed changes. The schedule and workbook download now include them; room and instructor conflicts were checked.");
    }

    private void ClearRequest()
    {
        _target = null; _candidates.Clear(); _room = null; _time = null;
        _reserved = _gdcHours = _findSlot = _oversized = false;
        _roomExplicit = _timeExplicit = false;
        _awaiting = ""; _pendingDetails = ""; Proposal = null; Suggestions.Clear();
        _swapRequested = _swapTimes = _swapTimesExplicit = false;
        _swapPartnerQuery = ""; _partners.Clear(); _swapOptions.Clear();
    }

    private void Reply(string text, params string[] choices)
    {
        Messages.Add(new(false, text)); Suggestions.Clear(); Suggestions.AddRange(choices);
    }

    private string GraduateChoice => $"Graduate slot: {_plan!.Rules.GraduateSlot}";
    private string GdcChoice => $"CS-controlled GDC hours: from {MeetingTime.ClockTime(_plan!.Rules.CsOwnsGdcFrom)}";
    private static string Description(Placement p) => $"{p.Section.Title} — {p.Section.Instructor} (row {p.Section.SourceRow + 1})";

    public void Send(Planner.Plan? plan, IReadOnlyList<Room> rooms, Func<string, bool, Planner.OverrideProposal> propose,
                     string message, bool allowOversized = false,
                     Func<Placement, Room, MeetingTime, bool, Planner.OverrideProposal>? proposeMove = null,
                     Func<Placement, Placement, bool, bool, Planner.OverrideProposal>? proposeSwap = null)
    {
        Sync(plan);
        string text = message.Trim();
        if (text.Length == 0) return;
        Messages.Add(new(true, text));
        if (Regex.IsMatch(text, @"^(cancel|never mind|nevermind|forget (it|that))[.!]?$", Ignore)) { Cancel(); return; }
        if (_plan == null)
        {
            Reply("Build a schedule on Semester plan first. Then I can look up your courses, explain assignments and preview changes."); return;
        }
        bool followup = Regex.IsMatch(text, @"^(yes|yes please|okay|ok|sure|do it|can you do that(?: for me)?|please do(?: that)?|apply(?: it)?)[.!?]?$", Ignore);
        if (Proposal != null && followup)
        {
            Reply("The proposed changes are ready below. Review them, then click Apply reviewed changes. Nothing changes until you click it."); return;
        }
        if (Regex.IsMatch(text, @"^(help|what can you do)[!?]?$", Ignore))
        {
            Reply("You can use course numbers or short names, for example ‘move comp arch to Tuesday at 2pm’ or ‘put data structures in GDC 2.216’. " +
                "I keep the current room or time when you only change one. I’ll ask which section or reserved time you mean when needed. " +
                "For swaps, say ‘swap rooms for comp arch with data structures’ or ‘swap it then’ to see feasible partners. " +
                "You can also ask where a course meets, why it got that room, what's in a room, or which classes are unassigned.",
                "Show review summary", "Which classes have no room?"); return;
        }

        var selectedSwap = _swapOptions.FirstOrDefault(o => o.Label.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (selectedSwap.Proposal != null)
        {
            Proposal = selectedSwap.Proposal;
            Reply("Selected this swap. Review both sections below, then Apply reviewed changes. Nothing has changed yet.", _swapOptions.Select(o => o.Label).ToArray()); return;
        }
        bool swap = Regex.IsMatch(text, @"\b(swap|swaps|exchange|trade)\b", Ignore);
        bool move = swap || Regex.IsMatch(text, @"\b(move|moved|put|switch|reschedule|relocate|shift|change)\b", Ignore);
        if (_awaiting == "swap-partner" && (!move || _partners.Any(p => Description(p).Equals(text, StringComparison.OrdinalIgnoreCase))))
        {
            var selected = SelectCandidate(text, _partners);
            if (selected != null)
            {
                _partners = new() { selected }; _swapPartnerQuery = ""; _oversized |= allowOversized;
                PreviewSwaps(rooms, proposeSwap); return;
            }
            Reply("Which section should swap with the pending course? Choose one below, or say Cancel.", _partners.Select(Description).ToArray()); return;
        }
        string courseText = text;
        string? partnerQuery = null;
        if (swap)
        {
            var contextPair = Regex.Match(text, @"^(?:swap|exchange|trade)\s+with\s+(.+?)[.!?]?$", Ignore);
            var pair = Regex.Match(text, @"\b(?:swap|exchange|trade)\s+(?:the\s+)?(?:(?:rooms?|slots?|times?)(?:\s+and\s+(?:rooms?|times?))?\s+)?(?:(?:for|between|of)\s+)?(.+?)\s+(?:with|and)\s+(.+?)[.!?]?$", Ignore);
            if (pair.Success && !Regex.IsMatch(text, @"^find room and time swaps", Ignore))
            {
                courseText = pair.Groups[1].Value; partnerQuery = pair.Groups[2].Value;
            }
            else if (contextPair.Success) { courseText = "it"; partnerQuery = contextPair.Groups[1].Value; }
        }
        if (!move && !followup && (_target == null && _candidates.Count == 0 ||
            Regex.IsMatch(text, @"\b(where|when|why|what|which|summary|review|explain)\b", Ignore)))
        {
            if (AnswerQuestion(text, rooms)) return;
        }
        if (!followup && (move || Regex.IsMatch(text, @"\b\d{1,2}(?::\d{2})?\s*(?:am|pm)\b", Ignore) || MatchRoom(text, rooms).Mentioned))
            Proposal = null;
        // A new move starts fresh; replies such as ‘actually use GDC 2.216’ refine the pending move.
        var matches = FindCourses(courseText);
        if (_target == null && matches.Count == 0 && _lastTarget != null && Regex.IsMatch(text, @"\b(it|same course|that class|that course)\b", Ignore))
            _target = _lastTarget;
        if (partnerQuery != null || move && matches.Count > 0 && (_target == null || !matches.Contains(_target))) ClearRequest();
        if (swap)
        {
            _swapRequested = true;
            if (!_timeExplicit) _time = null;
            if (!_roomExplicit) _room = null;
            _findSlot = false;
            if (partnerQuery != null) _swapPartnerQuery = partnerQuery;
            if (Regex.IsMatch(text, @"\b(times?|slots?)\b", Ignore)) { _swapTimes = true; _swapTimesExplicit = true; }
            else if (Regex.IsMatch(text, @"\brooms?\b", Ignore)) { _swapTimes = false; _swapTimesExplicit = true; }
            if (Regex.IsMatch(text, @"^find room(?: and time)? swaps", Ignore))
            { _swapPartnerQuery = ""; _partners.Clear(); _time = null; _room = null; _timeExplicit = _roomExplicit = false; }
        }
        else if (move) { _swapRequested = false; _partners.Clear(); _swapOptions.Clear(); }
        if (partnerQuery != null && matches.Count == 0 && _lastTarget != null && Regex.IsMatch(courseText, @"\b(it|that class|that course)\b", Ignore))
            _target = _lastTarget;
        _oversized |= allowOversized;
        if (Regex.IsMatch(text, @"reserved\s+(?:cs\s+)?time|cs\s+(?:reserved\s+)?time", Ignore)) _reserved = true;
        if (text.Contains("graduate slot", StringComparison.OrdinalIgnoreCase) || text.Contains("graduate time", StringComparison.OrdinalIgnoreCase))
        {
            _time = _plan.Rules.GraduateSlot; _timeExplicit = true; _reserved = false; _gdcHours = false;
        }
        if (text.Contains("CS-controlled GDC", StringComparison.OrdinalIgnoreCase) || text.Contains("GDC hours", StringComparison.OrdinalIgnoreCase))
        {
            _gdcHours = true; _reserved = false; _time = null; _timeExplicit = false;
        }
        if (Regex.IsMatch(text, @"\b(any time|find (?:a |an )?(?:free|available|feasible) (?:time|slot)|find a slot)\b", Ignore))
        {
            Proposal = null;
            if (!_roomExplicit) _room = null;
            _findSlot = true; _timeExplicit = false; _swapRequested = false; _partners.Clear(); _swapPartnerQuery = ""; _swapOptions.Clear();
        }
        if ((_awaiting == "oversized" || _awaiting == "swap-oversized") && (followup || Regex.IsMatch(text, @"\b(allow|use|yes).*(larger?|bigger|big|oversized)\b", Ignore))) _oversized = true;

        if (_candidates.Count > 1)
        {
            var selection = SelectCandidate(text, _candidates);
            if (selection != null) { _target = selection; _candidates.Clear(); }
            else if (matches.Count > 0) _candidates = matches;
        }
        else if (matches.Count == 1) _target = matches[0];
        else if (matches.Count > 1) _candidates = matches;

        if (_target == null)
        {
            if (_candidates.Count == 1) { _target = _candidates[0]; _candidates.Clear(); }
            else if (_candidates.Count > 1)
            {
                _awaiting = "course";
                if (_pendingDetails.Length == 0) _pendingDetails = text;
                Reply("I found more than one matching section. Which one do you mean? Reply with the instructor, row number, or one of these choices.",
                    _candidates.Select(Description).ToArray()); return;
            }
            else
            {
                Reply("Which course should I work with? A course number, title, or short name such as ‘comp arch’ is enough. I’ll look it up in your schedule."); return;
            }
        }
        _lastTarget = _target;
        if (_target.Section.Online) { Reply($"{_target.Section.Title} is marked online, so it has no physical classroom. Change its modality before assigning a room."); return; }

        string details = _pendingDetails.Length > 0 ? _pendingDetails : text;
        _pendingDetails = "";
        var roomMatch = MatchRoom(details, rooms);
        if (roomMatch.Mentioned && roomMatch.Room == null) { Reply($"I couldn't find {roomMatch.Name} in the current room inventory. Which room should I use?"); return; }
        if (roomMatch.Room != null) { _room = roomMatch.Room; _roomExplicit = true; Proposal = null; }
        if (_reserved && !Regex.IsMatch(details, @"\b\d{1,2}(?::\d{2})?\s*(?:am|pm)\b", Ignore))
        {
            _awaiting = "reserved";
            Reply($"I found {Description(_target)}. ‘Reserved CS time’ could mean the graduate-course slot ({_plan.Rules.GraduateSlot}) " +
                $"or the CS-controlled GDC room hours (from {MeetingTime.ClockTime(_plan.Rules.CsOwnsGdcFrom)}). Which do you mean?", GraduateChoice, GdcChoice); return;
        }
        _reserved = false;
        var parsed = text.StartsWith("CS-controlled GDC hours", StringComparison.OrdinalIgnoreCase) ? null : ParseTime(details, _time ?? _target.Time);
        if (parsed != null) { _time = parsed; _timeExplicit = true; Proposal = null; }
        bool explicitClock = Regex.IsMatch(details, @"\b\d{1,2}(?::\d{2})?\s*(?:am|pm)\b|\d{1,2}:\d{2}", Ignore);
        if (explicitClock && parsed == null && !text.StartsWith("CS-controlled GDC hours", StringComparison.OrdinalIgnoreCase))
        {
            Reply($"I have {_target.Section.Title}, but need a clear meeting time. Which days and start/end time should I use? For example, Tuesday and Thursday 2pm–3:30pm."); return;
        }
        if (_swapRequested)
        {
            PreviewSwaps(rooms, proposeSwap); return;
        }
        if (_gdcHours && _time != null && _time.Start < _plan.Rules.CsOwnsGdcFrom)
        {
            _time = null; Reply($"CS controls those GDC rooms from {MeetingTime.ClockTime(_plan.Rules.CsOwnsGdcFrom)}. What time after that should I try?", "Find a free slot"); return;
        }
        if (_gdcHours && _time == null && !_findSlot)
        {
            _awaiting = "time";
            Reply($"I'll keep working on {_target.Section.Title}. Which days and start time within the CS-controlled GDC hours should I try? " +
                "You can also ask me to find a free slot.", "Find a free slot"); return;
        }
        if (_time == null && _room == null && !_findSlot)
        {
            Reply($"I have {_target.Section.Title}. What days/time or room would you like to change? I can keep the other details the same, or look for a free slot.", "Find a free slot"); return;
        }
        if (_time == null && !_findSlot) _time = _target.Time;
        if (_time == null && !_findSlot)
        {
            _awaiting = "time"; Reply($"{_target.Section.Title} has no existing meeting time. What days and start/end time should I use?", "Find a free slot"); return;
        }
        // Preserve a previous proposal on vague follow-ups, rather than throwing away the user's work.
        if (Proposal != null && roomMatch.Room == null && parsed == null)
        {
            Reply("Your proposal is still below. Review it and click Apply reviewed changes, or tell me what to adjust."); return;
        }
        var candidates = rooms.Where(r => r.Capacity >= Math.Max(_target.Section.SizeForRoom, _target.Section.MinimumRoomCapacity) &&
                !_plan.Rules.Offline.IsOffline(r) && (!_gdcHours || (r.Building == _plan.Rules.HomeBuilding && r.CsOwnsFrom != null)))
            .OrderByDescending(r => r == _room || (_room == null && r == _target.Room))
            .ThenBy(r => Math.Abs(r.Capacity - Math.Max(_target.Section.SizeForRoom, _target.Section.PreferredRoomCapacity))).ToList();
        if (_room != null)
        {
            if (_gdcHours && (_room.Building != _plan.Rules.HomeBuilding || _room.CsOwnsFrom == null))
            {
                Reply($"{_room.DisplayName} isn't marked as a CS-controlled GDC room in the inventory. Choose one of those rooms, or change the request."); return;
            }
            candidates = new() { _room };
        }
        string? error = null;
        var times = _findSlot ? SearchTimes(_target, _gdcHours ? _plan.Rules.CsOwnsGdcFrom : TimeSpan.FromHours(8)).Where(t => !Equals(t, _target.Time)).ToList() : new List<MeetingTime> { _time! };
        int attempts = 0;
        foreach (var time in times)
            foreach (var room in candidates)
            {
                if (++attempts > 400) break;
                string selector = $"{_target.Section.Title} / {_target.Section.Instructor} / row {_target.Section.SourceRow + 1}";
                try
                {
                    var proposal = proposeMove != null
                        ? proposeMove(_target, room, time, allowOversized || _oversized)
                        : propose($"Put {selector} in {room.DisplayName} {time.Pattern} at {time.Clock}", allowOversized || _oversized);
                    if (proposal.ProposedChanges.Count == 0) { Reply($"{_target.Section.Title} is already in {room.DisplayName} at {time}."); return; }
                    Proposal = proposal; _time = time; _room = room; _findSlot = false; _awaiting = "";
                    Reply($"I read this as moving {_target.Section.Title} ({_target.Section.Instructor}) to {room.DisplayName}, {time}. " +
                        $"{proposal.ProposedChanges.Count} section(s) would change. Review the before/after list below, then Apply or Cancel. Nothing has changed yet."); return;
                }
                catch (ArgumentException ex)
                {
                    error ??= ex.Message;
                    if (ex.Message.Contains("larger than needed", StringComparison.OrdinalIgnoreCase) && _room != null)
                    {
                        _awaiting = "oversized";
                        Reply($"{room.DisplayName} is much larger than {_target.Section.Title} needs. Should I allow an intentional oversized-room exception for this request?", "Yes, allow the larger room", "Cancel"); return;
                    }
                }
            }
        Reply($"I couldn't find a valid move for {_target.Section.Title}. {error ?? "No matching rooms are available in the current inventory."} " +
            "Your schedule is unchanged. Try a room swap, exchange rooms and times, or change the request.", "Find room swaps", "Find room and time swaps", "Find a free slot", "Cancel");
    }

    private void PreviewSwaps(IReadOnlyList<Room> rooms, Func<Placement, Placement, bool, bool, Planner.OverrideProposal>? proposeSwap)
    {
        Proposal = null; _swapOptions.Clear();
        if (proposeSwap == null) { Reply("Room-swap support isn't available in this client. Reload the updated app."); return; }
        if (_target?.Room == null || _target.Time == null)
        { Reply("I need an assigned room and meeting time for the first section before I can exchange it with another class."); return; }
        if (!_swapTimesExplicit)
            _swapTimes = (_gdcHours && _target.Time.Start < _plan!.Rules.CsOwnsGdcFrom) || (_time != null && !Equals(_time, _target.Time));
        if (_swapPartnerQuery.Length > 0)
        {
            _partners = FindCourses(_swapPartnerQuery).Where(p => p != _target).ToList();
            if (_partners.Count == 0)
            { Reply("Which other course should swap with " + _target.Section.Title + "? I couldn't match the second course in this schedule.", "Find room swaps", "Find room and time swaps", "Cancel"); return; }
            if (_partners.Count == 1) _swapPartnerQuery = "";
            if (_partners.Count > 1)
            {
                _awaiting = "swap-partner";
                Reply("Which section of the other course should swap with " + Description(_target) + "?", _partners.Select(Description).ToArray()); return;
            }
        }
        var candidates = (_partners.Count > 0 ? _partners : _plan!.Placements)
            .Where(p => p != _target && !p.Section.Online && p.Room != null && p.Time != null)
            .Where(p => _room == null || p.Room == _room)
            .Where(p => _time == null || Equals(_swapTimes ? p.Time : _target.Time, _time))
            .Where(p => !_gdcHours || p.Room!.Building == _plan!.Rules.HomeBuilding && p.Room.CsOwnsFrom != null &&
                (_swapTimes ? p.Time! : _target.Time).Start >= _plan.Rules.CsOwnsGdcFrom)
            .Where(p => _partners.Count > 0 || p.Room!.Capacity >= Math.Max(_target.Section.SizeForRoom, _target.Section.MinimumRoomCapacity) &&
                _target.Room.Capacity >= Math.Max(p.Section.SizeForRoom, p.Section.MinimumRoomCapacity))
            .OrderBy(p => Math.Abs(p.Room!.Capacity - Math.Max(_target.Section.SizeForRoom, _target.Section.PreferredRoomCapacity)))
            .ThenBy(p => p.Section.SourceRow).Take(80).ToList();
        string? error = null;
        foreach (var partner in candidates)
        {
            try
            {
                var proposal = proposeSwap(_target, partner, _swapTimes, _oversized);
                if (proposal.ProposedChanges.Count == 0) continue;
                string label = $"Swap with {Description(partner)} — {partner.Room!.DisplayName}, {partner.Time}";
                _swapOptions.Add((label, proposal));
                if (_swapOptions.Count == 3) break;
            }
            catch (ArgumentException ex)
            {
                error ??= ex.Message;
                if (_partners.Count == 1 && ex.Message.Contains("larger than needed", StringComparison.OrdinalIgnoreCase))
                {
                    _awaiting = "swap-oversized";
                    Reply(ex.Message + " Should I allow that exception for this swap?", "Yes, allow the larger room", "Cancel"); return;
                }
            }
        }
        if (_swapOptions.Count > 0)
        {
            Proposal = _swapOptions[0].Proposal; _awaiting = "";
            Reply($"I found {_swapOptions.Count} feasible {(_swapTimes ? "room-and-time" : "room-only")} swap option(s) for {_target.Section.Title}. " +
                (_swapTimes ? "These exchange both rooms and meeting times; review contact hours for both courses. " : "Both classes keep their current meeting times. ") +
                "The first option is shown below. Choose another option if preferred, then review both sections and Apply. Nothing has changed yet.",
                _swapOptions.Select(o => o.Label).Append("Find room and time swaps").Append("Cancel").ToArray()); return;
        }
        string scope = _swapTimes ? "room-and-time" : "room-only";
        Reply($"I couldn't find a feasible {scope} swap matching the current request for {_target.Section.Title}. " +
            (error ?? "No eligible partner meets the requested room/time and CS-hours conditions.") +
            " No changes were made. You can broaden the search or specify the other course.",
            _swapTimes ? "Find room swaps" : "Find room and time swaps", "Find a free slot", "Cancel");
    }

    private bool AnswerQuestion(string text, IReadOnlyList<Room> rooms)
    {
        if (Regex.IsMatch(text, @"reserved\s+(?:cs\s+)?time|cs\s+(?:reserved\s+)?time|graduate\s+(?:slot|time)|GDC hours", Ignore))
        {
            Reply($"The configured graduate-course slot is {_plan!.Rules.GraduateSlot}. CS controls the designated GDC rooms from {MeetingTime.ClockTime(_plan.Rules.CsOwnsGdcFrom)}. " +
                "Those are different policies; ‘reserved CS time’ needs clarification before moving a course.",
                _target != null ? new[] { GraduateChoice, GdcChoice } : Array.Empty<string>()); return true;
        }
        if (Regex.IsMatch(text, @"\b(unassigned|unscheduled|no room|not scheduled|missing room)\b", Ignore))
        {
            var list = _plan!.Placements.Where(p => !p.Section.Online && p.Room == null).ToList();
            Reply(list.Count == 0 ? "Every in-person section has a room. Online sections intentionally have no classroom." :
                string.Join("\n", list.Select(p => Description(p) + ": " + string.Join("; ", p.Section.Flags.Where(f => f.Level == FlagLevel.Problem).Select(f => f.Message))))); return true;
        }
        if (Regex.IsMatch(text, @"\b(summary|review|problems)\b", Ignore))
        {
            var flags = _plan!.AllFlags.Select(x => x.Flag).Concat(_plan.GeneralFlags).ToList();
            Reply($"{_plan.Placements.Count(p => p.Status == "Scheduled")} sections scheduled; {_plan.Placements.Count(p => p.Status is "No room" or "Not scheduled")} need placing. " +
                $"{flags.Count(f => f.Level == FlagLevel.Problem)} problems and {flags.Count(f => f.Level == FlagLevel.Review)} items to review. " +
                "Ask why a particular course got its room, or open the Review queue for all explanations."); return true;
        }
        var courses = FindCourses(text);
        if (courses.Count == 0 && (_target ?? _lastTarget) is Placement last &&
            Regex.IsMatch(text, @"\b(it|this course|that course|this class|that class)\b", Ignore)) courses.Add(last);
        if (courses.Count == 1) _lastTarget = courses[0];
        if (courses.Count > 0)
        {
            bool why = Regex.IsMatch(text, @"\b(why|explain|size|enrollment|capacity)\b", Ignore);
            Reply(string.Join("\n\n", courses.Select(p => $"{Description(p)}: {p.Time?.ToString() ?? "no time assigned"}, {p.Room?.DisplayName ?? (p.Section.Online ? "online" : "no room assigned")}." +
                (why ? $" Expected size: {p.Section.SizeForRoom}; minimum room capacity: {p.Section.MinimumRoomCapacity}. " + string.Join("; ", p.Why.Concat(p.Section.Flags.Select(f => f.Message))) : "")))); return true;
        }
        var room = MatchRoom(text, rooms);
        if (room.Room != null)
        {
            var occupants = _plan!.InRoom(room.Room);
            if (Regex.IsMatch(text, @"\b(free|available)\b", Ignore))
            {
                var time = ParseTime(text, null);
                if (time == null) { Reply($"What days and start/end time should I check for {room.Room.DisplayName}?"); return true; }
                var conflicts = occupants.Where(p => p.Time!.Overlaps(time)).ToList();
                Reply(_plan.Rules.Offline.IsOffline(room.Room) ? $"{room.Room.DisplayName} is offline." : conflicts.Count == 0
                    ? $"No classes in the current plan use {room.Room.DisplayName} at {time}. Capacity and instructor constraints still need checking for a specific course."
                    : $"{room.Room.DisplayName} is occupied at {time}: " + string.Join("; ", conflicts.Select(Description))); return true;
            }
            Reply($"{room.Room.DisplayName}: {room.Room.Capacity} seats. " + (_plan.Rules.Offline.IsOffline(room.Room) ? "This room is offline. " : "") +
                (occupants.Count == 0 ? "No classes are assigned here in the current plan." : string.Join("\n", occupants.Select(p => $"{Description(p)} — {p.Time}")))); return true;
        }
        return false;
    }

    private List<Placement> FindCourses(string text)
    {
        string clean = Regex.Replace(text, @"\b[A-Za-z]{2,5}\s+\d+\.\d+\b", " ");
        var words = Regex.Matches(clean.ToLowerInvariant(), @"[a-z0-9]+").Select(m => m.Value).ToHashSet();
        var scored = _plan!.Placements.Select(p =>
        {
            int score = Regex.IsMatch(clean, $@"(?<![\w])(?:c\s*s\s*)?{Regex.Escape(p.Section.Course)}(?![\w])", Ignore) ? 100 : 0;
            var title = Regex.Matches(p.Section.Title.ToLowerInvariant(), @"[a-z]+").Select(m => m.Value).ToList();
            int matched = words.Where(w => w.Length >= 3 && !Stop.Contains(w)).Count(w => title.Any(t => t.StartsWith(w)));
            if (matched > 0) score += matched * 10;
            if (score > 0 && words.Contains(p.Section.Surname.ToLowerInvariant())) score += 5;
            return (p, score);
        }).Where(x => x.score > 0).ToList();
        return scored.Count == 0 ? new() : scored.Where(x => x.score == scored.Max(v => v.score)).Select(x => x.p).ToList();
    }

    private static Placement? SelectCandidate(string text, List<Placement> choices)
    {
        var index = Regex.Match(text.Trim(), @"^(?:option |number )?(\d+)[.)]?$", Ignore);
        if (index.Success && int.TryParse(index.Groups[1].Value, out int n) && n > 0 && n <= choices.Count) return choices[n - 1];
        var rows = Regex.Match(text, @"\brow\s+(\d+)\b", Ignore);
        if (rows.Success) return choices.FirstOrDefault(p => p.Section.SourceRow + 1 == int.Parse(rows.Groups[1].Value));
        var matches = choices.Where(p => text.Contains(p.Section.Instructor, StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(text, $@"\b{Regex.Escape(p.Section.Surname)}\b", Ignore)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static (bool Mentioned, Room? Room, string Name) MatchRoom(string text, IReadOnlyList<Room> rooms)
    {
        foreach (var building in rooms.Select(r => r.Building).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var match = Regex.Match(text, $@"\b{Regex.Escape(building)}\s*(\d+(?:\.\d{{1,3}})?[a-z]?)\b", Ignore);
            if (!match.Success) continue;
            string name = $"{building} {match.Groups[1].Value}";
            return (true, rooms.FirstOrDefault(r => r.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase)), name);
        }
        var unknown = Regex.Match(text, @"\b([a-z]{2,5})\s+(\d+\.\d{1,3})\b", Ignore);
        return unknown.Success ? (true, null, $"{unknown.Groups[1].Value.ToUpperInvariant()} {unknown.Groups[2].Value}") : (false, null, "");
    }

    private static MeetingTime? ParseTime(string text, MeetingTime? current)
    {
        var named = Regex.Matches(text, @"\b(monday|tuesday|wednesday|thursday|friday|mon|tue|wed|thu|fri|MWF|TTH|MW|TH|M|T|W|F)\b", Ignore);
        var days = named.SelectMany(m => MeetingTime.ParseDays(m.Value)).Distinct().ToList();
        var dayRange = Regex.Match(text, @"\b(Monday|Tuesday|Wednesday|Thursday|Friday)\s+(?:through|to)\s+(Monday|Tuesday|Wednesday|Thursday|Friday)\b", Ignore);
        if (dayRange.Success)
        {
            int start = (int)Enum.Parse<DayOfWeek>(dayRange.Groups[1].Value, true), end = (int)Enum.Parse<DayOfWeek>(dayRange.Groups[2].Value, true);
            if (end < start) return null;
            days = Enumerable.Range(start, end - start + 1).Select(n => (DayOfWeek)n).ToList();
        }
        string pattern = new MeetingTime(days.Count > 0 ? days : current?.Days ?? new List<DayOfWeek>(), TimeSpan.Zero, TimeSpan.Zero).Pattern;
        if (pattern.Length == 0) return null;
        var range = Regex.Match(text, @"\b\d{1,2}(?::[0-5]\d)?\s*(?:am|pm)?\s*[-–]\s*\d{1,2}(?::[0-5]\d)?\s*(?:am|pm)\b", Ignore);
        var clocks = Regex.Matches(text, @"\b\d{1,2}(?::[0-5]\d)?\s*(?:am|pm)\b", Ignore);
        if (clocks.Count > 1 && !range.Success) return null;
        var clock = range.Success ? range : Regex.Match(text, @"\b\d{1,2}(?::[0-5]\d)?\s*(?:am|pm)(?:\s*[-–]\s*\d{1,2}(?::[0-5]\d)?\s*(?:am|pm))?\b", Ignore);
        if (clock.Success)
        {
            string value = clock.Value.Replace('–', '-');
            return PreferenceParser.FindTimes($"{pattern} {(value.Contains('-') ? "" : "at ")}{value}",
                value.Contains('-') ? null : current?.Duration).Select(t => t.Time).SingleOrDefault();
        }
        return days.Count > 0 && current != null ? new MeetingTime(days, current.Start, current.End) : null;
    }

    private static List<MeetingTime> SearchTimes(Placement target, TimeSpan from)
    {
        var patterns = target.Time != null ? new[] { target.Time.Pattern, "MWF", "TTH" }.Distinct() : new[] { "MWF", "TTH" };
        var result = new List<MeetingTime>();
        foreach (string pattern in patterns)
        {
            var days = MeetingTime.ParseDays(pattern);
            int credits = target.Section.Course.Length > 0 && char.IsDigit(target.Section.Course[0]) ? Math.Max(1, target.Section.Course[0] - '0') : 3;
            var duration = target.Time != null && pattern == target.Time.Pattern ? target.Time.Duration : TimeSpan.FromHours((double)credits / days.Count);
            for (var start = from; start + duration <= TimeSpan.FromHours(21); start += TimeSpan.FromMinutes(30))
                result.Add(new MeetingTime(days, start, start + duration));
        }
        return result;
    }
}
