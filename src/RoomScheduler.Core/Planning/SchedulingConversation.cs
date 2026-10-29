using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RoomScheduler;

public record ChatMessage(bool User, string Text);

/// <summary>Conversation state grounded in the current plan. Never applies a move itself.</summary>
public sealed partial class SchedulingConversation
{
    public List<ChatMessage> Messages { get; } = new();
    public List<string> Suggestions { get; } = new();
    public Planner.OverrideProposal? Proposal { get; private set; }
    private Planner.Plan? _plan;
    private Placement? _target;
    private Placement? _lastTarget;
    private List<Placement> _referencedCourses = new();
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
    private static readonly string[] Stop = ("day days weekly week once twice single frequency morning afternoon evening timing timings meeting duration hours long per make set best recommended compare capacity expected spare seats teaching teaches teach instructor professor faculty smallest disruptions fit recording lecture capture max maximum minimum actually instead okay fine allowed classsize and or of i need want please can could would you me my the a an to be moved move put switch reschedule change swap swaps exchange trade rooms times between with " +
        "adjust holds seats unused used been has not than more less under over capacity too large smaller it that for from into in on at time class course section reserved cs graduate slot hours gdc after before keep same current " +
        "monday tuesday wednesday thursday friday mon tue wed thu fri mwf tth mw am pm ok okay do yes thanks computer find free available feasible suggest recommend show give options option alternatives alternative another smaller bigger larger room keep only without other classes same current existing later earlier after before any second first third").Split(' ');

    public void Sync(Planner.Plan? plan)
    {
        if (ReferenceEquals(_plan, plan)) return;
        bool hadRequest = _target != null || Proposal != null;
        _plan = plan; ClearRequest(); _lastTarget = null; _referencedCourses.Clear();
        if (hadRequest) Reply(plan == null ? "Planning inputs changed. I've cleared the previous proposal; rebuild before requesting changes." : "The schedule was rebuilt. I've cleared the previous proposal so the next request uses the new assignments.");
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
        _availabilityRoom = null; _availabilitySearch = false; _availabilityDetails = "";
        _target = null; _candidates.Clear(); _room = null; _time = null;
        _reserved = _gdcHours = _findSlot = _oversized = false;
        _roomExplicit = _timeExplicit = false;
        _awaiting = ""; _pendingDetails = ""; Proposal = null; Suggestions.Clear();
        _swapRequested = _swapTimes = _swapTimesExplicit = false;
        _swapPartnerQuery = ""; _partners.Clear(); _swapOptions.Clear();
        _moveOptions.Clear(); _filters.Clear(); _findRoom = false; _selectedOption = 0;
    }

    private void Reply(string text, params string[] choices)
    {
        Messages.Add(new(false, text)); Suggestions.Clear(); Suggestions.AddRange(choices);
    }

    internal static bool IsCourseStopWord(string word) => Stop.Contains(word);
    internal IReadOnlyList<string> ReferencedCourseNumbers => _referencedCourses.Count > 0
        ? _referencedCourses.Select(p => p.Section.Course).Distinct().ToList()
        : _lastTarget != null ? new[] { _lastTarget.Section.Course } : Array.Empty<string>();

    internal void SessionReply(string message, string response, params string[] choices)
    {
        Messages.Add(new(true, message.Trim()));
        Reply(response, choices);
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
        var previousPreview = Proposal?.ProposedChanges.FirstOrDefault(c => c.Placement == _target);
        string text = Normalize(message.Trim());
        if (text.Length == 0) return;
        Messages.Add(new(true, message.Trim()));
        if (Regex.IsMatch(text, @"^(cancel|never mind|nevermind|forget (it|that)|start over|reset request)[.!]?$", Ignore)) { Cancel(); return; }
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
                "I keep the current room or time when you only change one. Try ‘make comp arch one day a week’, ‘Friday afternoon’, ‘30 minutes later’, or ‘finish at 4pm’. Day-only changes preserve current weekly hours; explicit durations/ranges show any weekly-hour change for review. I’ll ask which section, weekdays, or am/pm you mean when needed. " +
                "For swaps, say ‘swap rooms for comp arch with data structures’ or ‘swap it then’ to see feasible partners. " +
                "Try ‘find comp arch a slot in GDC after 2pm before 5pm, no Fridays’, ‘find a smaller room and keep the same time’, " +
                "or ‘without moving other classes’. Ask for ‘the smallest feasible room’ to rank by capacity, or ‘which classes have oversized rooms?’ to identify room-fit improvements. I compare up to three checked options; say ‘the second one’ or ‘something else’. " +
                "Ask ‘what would change?’ to explain a preview. You can also ask where a course meets, why it got that room, what's in a room, or which classes are unassigned. Try ‘compare GDC 1.100 and RLP 1.104’, ‘how many seats in that room?’, or an instructor's name. " +
                "Say ‘start over’ to clear a request. I work with the loaded schedule in this browser and cannot check campus-wide bookings.",
                "Show review summary", "Which classes have no room?"); return;
        }

        if (Regex.IsMatch(text, @"^(?:what|which|show|list)\b.*\brooms?\b.*\b(free|available|open)\b", Ignore) && !MatchRoom(text, rooms).Mentioned)
        { AnswerFreeRooms(text, rooms); return; }
        if (AnswerSmartQuestion(text, rooms)) return;
        if (ChooseOption(text)) return;
        if (Regex.IsMatch(text, @"\b(compare|differences?)\b.*\b(options?|choices?|alternatives?)\b", Ignore) && (_moveOptions.Count > 0 || _swapOptions.Count > 0))
        {
            var options = _swapOptions.Count > 0 ? _swapOptions : _moveOptions;
            Reply(string.Join("\n\n", options.Select(o => o.Label + "\n" + OptionFit(o.Proposal))) +
                "\nAll of these previews passed constraint checks. Nothing has changed; choose one to review before applying.", options.Select(o => o.Label).ToArray()); return;
        }
        if (OptionNumber(text) >= 0 && _candidates.Count < 2 && _awaiting is not ("swap-partner" or "spelling"))
        { Reply("There are no current options to select. Tell me the course and change you want, and I'll check fresh options."); return; }
        if (Proposal != null && Regex.IsMatch(text, @"\b(what would change|who (?:would|gets?) (?:move|moved|displaced)|explain (?:this|the) (?:option|proposal)|why (?:this|that) (?:option|proposal))\b", Ignore))
        {
            Reply("This preview would make these changes:\n" + ExplainChanges(Proposal) + "\n" + Proposal.Explanation +
                " Nothing has changed yet. Apply reviewed changes only if the full list is correct."); return;
        }
        if (_availabilityRoom != null || _availabilitySearch)
        {
            bool newIntent = Regex.IsMatch(text, @"\b(move|put|swap|switch|reschedule|change|find|where|why|what|which|show|summary|review)\b", Ignore) || FindCourses(text).Count > 0;
            if (!newIntent)
            {
                var queryTime = ParseTime(text, null);
                if (queryTime == null) { Reply("Please give the days and full start/end time, for example ‘Tuesday and Thursday 2pm–3pm’, so I can check that entire interval."); return; }
                var queryRoom = _availabilityRoom; _availabilityRoom = null; _availabilitySearch = false;
                if (queryRoom != null) AnswerAvailability(queryRoom, queryTime); else AnswerFreeRooms(text, rooms);
                return;
            }
            _availabilityRoom = null; _availabilitySearch = false; _availabilityDetails = "";
        }
        bool swap = Regex.IsMatch(text, @"\b(swap|swaps|exchange|trade)\b", Ignore);
        bool search = Regex.IsMatch(text, @"\b(morning|afternoon|evening)\b", Ignore) || WantsRoomSearch(text) || Regex.IsMatch(text, @"\b(find|suggest|recommend|look for|show|give)\b.*\b(rooms?|slots?|times?|options?|alternatives?|swaps?)\b|\b(smaller|bigger|larger|better|different|another) room\b|\b(later|earlier)\b", Ignore);
        bool move = swap || search || TimingIntent(text) || Regex.IsMatch(text, @"\b(move|moved|put|switch|reschedule|relocate|shift|change|adjust)\b", Ignore);
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
        if ((!move || Regex.IsMatch(text, @"\b(why|where|when|summary|review|unassigned|unscheduled|no room|what.*in|is.*free|is.*available)\b", Ignore)) && !swap && !followup && (_target == null && _candidates.Count == 0 ||
            Regex.IsMatch(text, @"\b(where|when|why|what|which|summary|review|explain|is|are|free|available)\b", Ignore)))
        {
            if (AnswerQuestion(text, rooms)) return;
        }
        if (!followup && (move || IsRefinement(text) || Regex.IsMatch(text, @"\b\d{1,2}(?::\d{2})?\s*(?:am|pm)\b|\b(after|before|later|earlier|same|keep|anywhere|only|no Friday)\b", Ignore) || MatchRoom(text, rooms).Mentioned))
        { Proposal = null; _moveOptions.Clear(); _swapOptions.Clear(); }
        // A new move starts fresh; replies such as ‘actually use GDC 2.216’ refine the pending move.
        var matches = FindCourses(courseText);
        if (_awaiting == "spelling")
        {
            var selected = SelectCandidate(text, _candidates);
            if (selected == null && followup && _candidates.Count == 1) selected = _candidates[0];
            if (selected != null)
            { matches = new() { selected }; _candidates.Clear(); _awaiting = ""; }
            else if (!move)
            { Reply("Choose a course below to confirm the spelling correction, or say Cancel.", _candidates.Select(Description).ToArray()); return; }
            else { _candidates.Clear(); _awaiting = ""; _pendingDetails = ""; }
        }
        bool refersBack = Regex.IsMatch(courseText, @"\b(it|same course|that class|that course|this class|this course)\b", Ignore);
        if (matches.Count == 0 && (refersBack || IsRefinement(text)) && _referencedCourses.Count > 1)
            matches = _referencedCourses.ToList();
        var namedMove = Regex.Match(courseText, @"\b(?:move|put|reschedule|relocate|shift)\s+(.+?)\s+(?:to|into|in|on|at)\b", Ignore);
        if (matches.Count == 0 && move && !refersBack)
        {
            var similar = SimilarCourses(namedMove.Success ? namedMove.Groups[1].Value : courseText);
            if (similar.Count > 0)
            {
                ClearRequest(); _candidates = similar; _awaiting = "spelling"; _pendingDetails = text;
                Reply("I couldn't find that exact course name. Did you mean one of these? Confirm the course before I check your requested change.", similar.Select(Description).ToArray()); return;
            }
        }
        if (matches.Count == 0 && namedMove.Success && !refersBack &&
            !Regex.IsMatch(namedMove.Groups[1].Value, @"^(?:the )?(?:class|course|section|room|time)$", Ignore))
        {
            ClearRequest(); _lastTarget = null; _referencedCourses.Clear();
            Reply($"I couldn't match ‘{namedMove.Groups[1].Value}’ to a course in this schedule. Give me its course number or title. Your schedule is unchanged."); return;
        }
        if (matches.Count == 0 && _lastTarget != null && (refersBack || IsRefinement(text)))
        { if (_target != null && _target != _lastTarget) ClearRequest(); _target = _lastTarget; }
        if (partnerQuery != null || move && matches.Count > 0 && (matches.Count > 1 || _target == null || !matches.Contains(_target)))
            ClearRequest();
        bool refiningSwap = _swapRequested && !swap && matches.Count == 0 && IsRefinement(text) &&
            !Regex.IsMatch(text, @"\b(move|put|find|reschedule|relocate|shift|change|adjust)\b", Ignore);
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
        else if (move && !refiningSwap) { _swapRequested = false; _partners.Clear(); _swapOptions.Clear(); }
        if (partnerQuery != null && matches.Count == 0 && _lastTarget != null && Regex.IsMatch(courseText, @"\b(it|that class|that course)\b", Ignore))
            _target = _lastTarget;
        if (search && !swap && !refiningSwap)
        {
            if (WantsRoomSearch(text) || Regex.IsMatch(text, @"\b(?:room|rooms)\b", Ignore) && !Regex.IsMatch(text, @"\b(?:time|slot)\b", Ignore)) _findRoom = true;
            else if (!Regex.IsMatch(text, @"\b(summary|review|unassigned|unscheduled)\b", Ignore)) _findSlot = true;
        }
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
            if (selection != null) { _target = selection; _candidates.Clear(); _awaiting = ""; }
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
        _referencedCourses = new() { _target };
        if (_target.Section.Online) { Reply($"{_target.Section.Title} is marked online, so it has no physical classroom. Change its modality before assigning a room."); return; }

        string details = _pendingDetails.Length > 0 ? _pendingDetails + " " + text : text;
        _pendingDetails = "";
        if (Regex.IsMatch(details, @"\b(?:find|suggest|recommend|look for)\b.*\b(?:slot|time)\b", Ignore))
            _findSlot = true;
        var namedRooms = MentionedRooms(details, rooms);
        if (namedRooms.Count > 1)
        {
            _pendingDetails = details;
            foreach (var namedRoom in namedRooms)
                _pendingDetails = Regex.Replace(_pendingDetails, Regex.Escape(namedRoom.DisplayName), " ", Ignore);
            Reply("You named more than one room. Which one should I check first? You can also say ‘compare " + string.Join(" and ", namedRooms.Select(r => r.DisplayName)) + "’. Nothing has changed.", namedRooms.Select(r => "Use " + r.DisplayName).ToArray()); return; }
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
        var requestedDays = Regex.Match(details, @"\b(Monday|Tuesday|Wednesday|Thursday|Friday)\s+(?:through|to)\s+(Monday|Tuesday|Wednesday|Thursday|Friday)\b", Ignore);
        if (requestedDays.Success && Enum.Parse<DayOfWeek>(requestedDays.Groups[1].Value, true) > Enum.Parse<DayOfWeek>(requestedDays.Groups[2].Value, true))
        { Reply("The weekday range must run forward within the week, for example Monday through Thursday. Which days should I use?"); return; }
        // Pasted catalog labels can include instructor initials such as M or W.
        // They identify the course, not requested weekdays.
        string constraintDetails = details;
        foreach (string label in new[] { _target.Section.Title, _target.Section.Instructor })
            if (!string.IsNullOrWhiteSpace(label))
                constraintDetails = Regex.Replace(constraintDetails, Regex.Escape(label), " ", Ignore);
        string exactDetails = ReadFilters(constraintDetails, rooms);
        if (Regex.IsMatch(exactDetails, @"\b(after|before|by)\s+\d", Ignore))
        { Reply("Please include am or pm for that time window, for example ‘after 2pm’ or ‘before 11am’. Nothing has changed."); return; }
        if ((_findRoom || _findSlot) && !_roomExplicit) _room = null;
        if (_findSlot && !_timeExplicit) _time = null;
        if (ConfigureTiming(exactDetails, previousPreview, out bool timingConfigured)) return;
        var parsed = timingConfigured || text.StartsWith("CS-controlled GDC hours", StringComparison.OrdinalIgnoreCase) ? null : ParseTime(exactDetails, _time ?? _target.Time);
        if (parsed != null && (!_findSlot || Regex.IsMatch(exactDetails, @"\b\d{1,2}(?::\d{2})?\s*(?:am|pm)\b", Ignore)))
        { _time = parsed; _timeExplicit = true; _findSlot = false; Proposal = null; }
        bool explicitClock = Regex.IsMatch(exactDetails, @"\b\d{1,2}(?::\d{2})?\s*(?:am|pm)\b|\d{1,2}:\d{2}", Ignore);
        if (explicitClock && parsed == null && !timingConfigured && !text.StartsWith("CS-controlled GDC hours", StringComparison.OrdinalIgnoreCase))
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
        if (_time == null && _room == null && !_findSlot && !_findRoom)
        {
            Reply($"I have {_target.Section.Title}. What days/time or room would you like to change? I can keep the other details the same, or look for a free slot.", "Find a free slot"); return;
        }
        if (_time == null && !_findSlot) _time = _target.Time;
        if (_time == null && !_findSlot)
        {
            _awaiting = "time"; Reply($"{_target.Section.Title} has no existing meeting time. What days and start/end time should I use?", "Find a free slot"); return;
        }
        // Preserve a previous proposal on vague follow-ups, rather than throwing away the user's work.
        if (Proposal != null && roomMatch.Room == null && parsed == null && !search && !Regex.IsMatch(text, @"\b(after|before|by|no|not|later|earlier|same|keep|in)\b", Ignore))
        {
            Reply("Your proposal is still below. Review it and click Apply reviewed changes, or tell me what to adjust."); return;
        }
        PreviewMoves(rooms, propose, proposeMove);
    }

    private void PreviewSwaps(IReadOnlyList<Room> rooms, Func<Placement, Placement, bool, bool, Planner.OverrideProposal>? proposeSwap)
    {
        Proposal = null; _swapOptions.Clear(); _moveOptions.Clear(); _selectedOption = 0;
        if (proposeSwap == null) { Reply("Room-swap support isn't available in this client. Reload the updated app."); return; }
        if (_filters.Error != null) { Reply(_filters.Error + " Your schedule is unchanged."); return; }
        if (_filters.MinSeats > _filters.MaxSeats)
        { Reply("The minimum seat count exceeds the maximum. Tell me a different seat limit; your schedule is unchanged."); return; }
        if (_filters.UnusedRoom)
        { Reply("A swap exchanges rooms with another section, so its room is already used. Your unused-room condition is still active. Ask for another unused room, or say ‘allow used rooms’ before searching for swaps.", "Find an unused room", "Allow used rooms", "Cancel"); return; }
        if (_filters.NoDisplacement)
        { Reply("A swap moves another section, but your request says not to move other classes. Say ‘allow moving other classes’ to permit a swap, or ask for a free room.", "Find a free slot", "Cancel"); return; }
        if (_target?.Room == null || _target.Time == null)
        { Reply("I need an assigned room and meeting time for the first section before I can exchange it with another class."); return; }
        if (!_swapTimesExplicit)
            _swapTimes = (_gdcHours && _target.Time.Start < _plan!.Rules.CsOwnsGdcFrom) || (_time != null && !Equals(_time, _target.Time)) || _findSlot;
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
            .Where(p => _room == null || !_roomExplicit || p.Room == _room)
            .Where(p => _filters.Accepts(p.Room!, _target) && _filters.Accepts(_swapTimes ? p.Time! : _target.Time, _target))
            .Where(p => _time == null || Equals(_swapTimes ? p.Time : _target.Time, _time))
            .Where(p => !_gdcHours || p.Room!.Building == _plan!.Rules.HomeBuilding && p.Room.CsOwnsFrom != null &&
                (_swapTimes ? p.Time! : _target.Time).Start >= _plan.Rules.CsOwnsGdcFrom)
            .Where(p => _partners.Count > 0 || p.Room!.Capacity >= Math.Max(_target.Section.SizeForRoom, _target.Section.MinimumRoomCapacity) &&
                _target.Room.Capacity >= Math.Max(p.Section.SizeForRoom, p.Section.MinimumRoomCapacity))
            .OrderBy(p => _swapTimes && !_timeExplicit && _plan!.Rules.IsEarlyStart(p.Time!))
            .ThenBy(p => _filters.PreferSmallest ? p.Room!.Capacity : Math.Abs(p.Room!.Capacity - Math.Max(_target.Section.SizeForRoom, _target.Section.PreferredRoomCapacity)))
            .ThenBy(p => p.Section.SourceRow).Take(80).ToList();
        string? error = null;
        foreach (var partner in candidates)
        {
            try
            {
                var proposal = proposeSwap(_target, partner, _swapTimes, _oversized);
                if (proposal.ProposedChanges.Count == 0) continue;
                string label = $"Swap with {Description(partner)} — {partner.Room!.DisplayName} ({partner.Room.Capacity} seats), {partner.Time}";
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
            Reply($"I found {_swapOptions.Count} feasible {(_swapTimes ? "room-and-time" : "room-only")} swap option(s) for {_target.Section.Title}.{_filters.Describe()} " +
                (_swapTimes ? "These exchange both rooms and meeting times; review contact hours for both courses. " : "Both classes keep their current meeting times. ") +
                "Both classes receive checked rooms, and both assignments apply together. The first option is shown below. Choose another option if preferred, then review both sections and Apply. Nothing has changed yet.",
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
        if (Regex.IsMatch(text, @"\b(summary|review|problems|needs attention|fix first)\b", Ignore))
        {
            var flags = _plan!.AllFlags.Select(x => x.Flag).Concat(_plan.GeneralFlags).ToList();
            var priorities = _plan.AllFlags.Where(x => x.Flag.Level >= FlagLevel.Review)
                .OrderByDescending(x => x.Flag.Level).Take(4).Select(x => $"{Description(x.Placement)}: {x.Flag.Message}").ToList();
            Reply($"{_plan.Placements.Count(p => p.Status == "Scheduled")} sections scheduled; {_plan.Placements.Count(p => p.Status is "No room" or "Not scheduled")} need placing. " +
                $"{flags.Count(f => f.Level == FlagLevel.Problem)} problems and {flags.Count(f => f.Level == FlagLevel.Review)} items to review. " +
                (priorities.Count > 0 ? "\nStart here (problems first):\n" + string.Join("\n", priorities) + "\n" : "") +
                "Ask why a particular course got its room, or open the Review queue for all explanations."); return true;
        }
        var courses = FindCourses(text);
        if (courses.Count == 0 && Regex.IsMatch(text, @"\b(it|this course|that course|this class|that class)\b", Ignore))
        {
            if (_referencedCourses.Count > 1) courses = _referencedCourses.ToList();
            else if ((_lastTarget ?? _target) is Placement last) courses.Add(last);
        }
        if (courses.Count > 0) { _referencedCourses = courses.ToList(); _lastTarget = courses.Count == 1 ? courses[0] : null; }
        if (courses.Count > 0)
        {
            bool why = Regex.IsMatch(text, @"\b(why|explain|size|enrollment|capacity)\b", Ignore);
            Reply(string.Join("\n\n", courses.Select(p => $"{Description(p)}: {p.Time?.ToString() ?? "no time assigned"}, {(p.Section.Online ? "online" : RoomLabel(p.Room))}." +
                (why ? $" Expected size: {p.Section.SizeForRoom}; minimum room capacity: {p.Section.MinimumRoomCapacity}. " + string.Join("; ", p.Why.Concat(p.Section.Flags.Select(f => f.Message))) : "")))); return true;
        }
        var room = MatchRoom(text, rooms);
        if (room.Room != null)
        {
            var occupants = _plan!.InRoom(room.Room);
            if (Regex.IsMatch(text, @"\b(free|available)\b", Ignore))
            {
                var time = ParseTime(text, null);
                if (time == null) { _availabilityRoom = room.Room; Reply($"What days and start/end time should I check for {room.Room.DisplayName}?"); return true; }
                AnswerAvailability(room.Room, time); return true;
            }
            Reply($"{room.Room.DisplayName}: {room.Room.Capacity} seats. " + (_plan.Rules.Offline.IsOffline(room.Room) ? "This room is offline. " : "") +
                (occupants.Count == 0 ? "No classes are assigned here in the current plan." : string.Join("\n", occupants.Select(p => $"{Description(p)} — {p.Time}")))); return true;
        }
        return false;
    }

    private List<Placement> FindCourses(string text)
    {
        string clean = Regex.Replace(text, @"\b[A-Za-z]{2,5}\s+\d+\.\d+\b", " ");
        clean = Regex.Replace(clean, @"\b(?:at least|at most|no more than|no less than|under|over|less than|fewer than|more than|minimum of|maximum of)\s+\d+\s*(?:seats?|people|students?)\b", " ", Ignore);
        var rowMatch = Regex.Match(clean, @"\brow\s+(\d+)\b", Ignore);
        if (rowMatch.Success)
            return int.TryParse(rowMatch.Groups[1].Value, out int row) ? _plan!.Placements.Where(p => p.Section.SourceRow + 1 == row).ToList() : new();
        var words = Regex.Matches(clean.ToLowerInvariant(), @"[a-z0-9]+").Select(m => m.Value).ToHashSet();
        var pool = _plan!.Placements.ToList();
        var numbered = pool.Where(p => p.Section.Course.Length > 0 && Regex.IsMatch(clean,
            $@"(?<![\w])(?:c\s*s\s*)?{Regex.Escape(p.Section.Course)}(?![\w])", Ignore)).ToList();
        if (numbered.Count > 0) pool = numbered;
        var named = _plan.Placements.Where(p => !p.Section.InstructorMissing && p.Section.Surname.Length > 1 &&
            Regex.IsMatch(clean, $@"\b{Regex.Escape(p.Section.Surname)}\b", Ignore)).ToList();
        if (numbered.Count > 0) return named.Count == 0 ? numbered : numbered.Where(named.Contains).ToList();
        if (Regex.IsMatch(clean, @"\b(?:CS\s*)?\d{3}[a-z]?\b", Ignore)) return new();
        var scored = pool.Select(p =>
        {
            var title = Regex.Matches(p.Section.Title.ToLowerInvariant(), @"[a-z]+").Select(m => m.Value).ToList();
            int matched = words.Where(w => w.Length >= 3 && !Stop.Contains(w)).Count(w => title.Any(t => t.StartsWith(w) || t.Length >= 4 && w.StartsWith(t)));
            return (p, score: matched);
        }).Where(x => x.score > 0).ToList();
        if (scored.Count == 0) return named;
        var best = scored.Where(x => x.score == scored.Max(v => v.score)).Select(x => x.p).ToList();
        return named.Count == 0 ? best : best.Where(named.Contains).ToList();
    }

    private static Placement? SelectCandidate(string text, List<Placement> choices)
    {
        int ordinal = OptionNumber(text);
        if (ordinal >= 0 && ordinal < choices.Count) return choices[ordinal];
        var index = Regex.Match(text.Trim(), @"^(?:option |number )?(\d+)[.)]?$", Ignore);
        if (index.Success && int.TryParse(index.Groups[1].Value, out int n) && n > 0 && n <= choices.Count) return choices[n - 1];
        var rows = Regex.Match(text, @"\brow\s+(\d+)\b", Ignore);
        if (rows.Success) return int.TryParse(rows.Groups[1].Value, out int row) ? choices.FirstOrDefault(p => p.Section.SourceRow + 1 == row) : null;
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
        var named = Regex.Matches(text, @"\b(monday|tuesday|wednesday|thursday|friday|mon|tue|wed|thu|fri|(?:TH|M|T|W|F)+)\b", Ignore);
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
        if (clocks.Count > (range.Success ? 2 : 1)) return null;
        var clock = range.Success ? range : Regex.Match(text, @"\b\d{1,2}(?::[0-5]\d)?\s*(?:am|pm)(?:\s*[-–]\s*\d{1,2}(?::[0-5]\d)?\s*(?:am|pm))?\b", Ignore);
        if (clock.Success)
        {
            string value = clock.Value.Replace('–', '-');
            var times = PreferenceParser.FindTimes($"{pattern} {(value.Contains('-') ? "" : "at ")}{value}",
                value.Contains('-') ? null : current?.Duration, MeetingTime.ParseDays(pattern));
            return times.Count == 1 ? times[0].Time : null;
        }
        return days.Count > 0 && current != null ? new MeetingTime(days, current.Start, current.End) : null;
    }

    private static List<MeetingTime> SearchTimes(Placement target, TimeSpan from, List<DayOfWeek>? requestedDays = null,
        int? dayCount = null, bool preserveWeeklyHours = false, TimeSpan? meetingDuration = null, TimeSpan? fixedStart = null, TimeSpan? fixedEnd = null)
    {
        IEnumerable<string> patterns;
        if (requestedDays != null) patterns = new[] { new MeetingTime(requestedDays, TimeSpan.Zero, TimeSpan.Zero).Pattern };
        else if (dayCount != null)
            patterns = Enumerable.Range(1, 31).Select(mask => Enumerable.Range(1, 5).Where(day => (mask & (1 << (day - 1))) != 0).Select(day => (DayOfWeek)day).ToList())
                .Where(days => days.Count == dayCount).Select(days => new MeetingTime(days, TimeSpan.Zero, TimeSpan.Zero).Pattern);
        else patterns = target.Time != null ? new[] { target.Time.Pattern, "MWF", "TTH" }.Distinct() : new[] { "MWF", "TTH" };
        var result = new List<MeetingTime>();
        foreach (string pattern in patterns)
        {
            var days = MeetingTime.ParseDays(pattern);
            if (days.Count == 0) continue;
            int credits = target.Section.Course.Length > 0 && char.IsDigit(target.Section.Course[0]) ? Math.Max(1, target.Section.Course[0] - '0') : 3;
            var duration = meetingDuration ?? (preserveWeeklyHours ? TimeSpan.FromTicks(WeeklyDuration(target).Ticks / days.Count) :
                target.Time != null && pattern == target.Time.Pattern ? target.Time.Duration : TimeSpan.FromHours((double)credits / days.Count));
            var starts = fixedEnd.HasValue ? new[] { fixedEnd.Value - duration } : fixedStart.HasValue ? new[] { fixedStart.Value } : Enumerable.Range(0, 27).Select(n => from + TimeSpan.FromMinutes(30 * n));
            foreach (var start in starts)
                if (duration > TimeSpan.Zero && start >= TimeSpan.Zero && start + duration <= TimeSpan.FromHours(fixedStart.HasValue || fixedEnd.HasValue ? 24 : 21))
                    result.Add(new MeetingTime(days, start, start + duration));
        }
        return result;
    }
}
