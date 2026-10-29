using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace RoomScheduler;

public sealed partial class SchedulingConversation
{
    private static readonly Regex Frequency = new(
        @"\b(?<count>one|single|two|three|four|five|\d+)\s+(?:days?|times?)(?:\s+(?:a|per|each)\s+week)?\b|" +
        @"\b(?<word>once|twice)\s+(?:a|per|each)\s+week\b|\bdays?\s+(?:to|=)\s*(?<count>one|two|three|four|five|\d+)\b", Ignore);
    private const string TimingDays = @"\b(?:Monday|Tuesday|Wednesday|Thursday|Friday|Mon|Tue|Wed|Thu|Fri|(?:TH|M|T|W|F)+)\b";

    private static bool TimingIntent(string text) => Frequency.IsMatch(text) || Regex.IsMatch(text,
        @"\b(?:morning|afternoon|evening|timings?|meeting days?|day pattern|weekly hours)\b|\b(?:change|set|adjust|switch)\b.*\b(?:days?|times?)\b|\b(?:end|finish|ending|start|starting)(?: it| the class)?\s+at\b|\b(?:for|duration(?: of)?)\s+\d+\s*(?:minutes?|hours?)\b|\b\d+(?:\.\d+)?\s*(?:minutes?|mins?|hours?|hrs?)\s+(?:later|earlier)\b", Ignore);

    private static TimeSpan WeeklyDuration(Placement target) => target.Time is MeetingTime time && time.Days.Count > 0
        ? TimeSpan.FromTicks(time.Duration.Ticks * time.Days.Count)
        : TimeSpan.FromHours(target.Section.Course.Length > 0 && char.IsDigit(target.Section.Course[0]) ? Math.Max(1, target.Section.Course[0] - '0') : 3);

    // Returns true after asking a question or reporting invalid input; otherwise
    // skipParser indicates that this request already supplied a complete time/search.
    private bool ConfigureTiming(string details, Planner.OverrideChange? previousPreview, out bool skipParser)
    {
        skipParser = false;
        if (Regex.IsMatch(details, @"\b(?:Saturday|Sunday|weekends?)\b", Ignore) &&
            !Regex.IsMatch(details, @"\b(?:no|not|without|avoid) weekends?\b", Ignore))
        { Reply("This scheduler supports Monday through Friday. Choose a weekday; your schedule is unchanged."); return true; }

        var frequency = Frequency.Match(details);
        var named = Regex.Matches(details, TimingDays, Ignore).SelectMany(m => MeetingTime.ParseDays(m.Value)).Distinct().OrderBy(d => d).ToList();
        var dayRange = Regex.Match(details, @"\b(Monday|Tuesday|Wednesday|Thursday|Friday)\s+(?:through|to)\s+(Monday|Tuesday|Wednesday|Thursday|Friday)\b", Ignore);
        if (dayRange.Success)
        {
            int first = (int)Enum.Parse<DayOfWeek>(dayRange.Groups[1].Value, true), last = (int)Enum.Parse<DayOfWeek>(dayRange.Groups[2].Value, true);
            if (last >= first) named = Enumerable.Range(first, last - first + 1).Select(d => (DayOfWeek)d).ToList();
        }
        bool hasClock = Regex.IsMatch(details, @"\b\d{1,2}(?::[0-5]\d)?\s*(?:am|pm)\b|\d{1,2}:\d{2}|\bat\s+\d{1,2}\b", Ignore);
        bool dayChange = frequency.Success || Regex.IsMatch(details, @"\b(?:change|set|adjust|switch)\b.*\b(?:days?|day pattern)\b", Ignore) ||
            named.Count > 0 && !hasClock && Regex.IsMatch(details, @"\b(?:move|change|make|put|switch|only)\b", Ignore);
        if (frequency.Success)
        {
            string count = frequency.Groups["count"].Value.ToLowerInvariant();
            int value = count switch { "one" or "single" => 1, "two" => 2, "three" => 3, "four" => 4, "five" => 5,
                _ => frequency.Groups["word"].Success ? frequency.Groups["word"].Value.Equals("once", StringComparison.OrdinalIgnoreCase) ? 1 : 2 :
                    int.TryParse(count, out int numeric) ? numeric : 0 };
            if (value is < 1 or > 5) { Reply("Choose between 1 and 5 meeting days per week, Monday through Friday. Nothing has changed."); return true; }
            _filters.DayCount = value;
            if (_filters.Days?.Count != value) _filters.Days = null;
        }
        else if (dayChange && named.Count > 0) _filters.DayCount = named.Count;
        if (dayChange)
        {
            _filters.PreserveWeeklyHours = true;
            _filters.SameTime = false;
            _time = null; _timeExplicit = false; _findSlot = true;
            if (Regex.IsMatch(details, @"\b(?:same|current) (?:start )?time\b|\bkeep (?:the )?start\b", Ignore)) _filters.SameStart = true;
        }

        var duration = Regex.Match(details, @"\b(?:for|lasts?|duration(?: of)?|make(?: it| the class)?)\s+(\d+(?:\.\d+)?)\s*(hours?|hrs?|minutes?|mins?)\b", Ignore);
        if (duration.Success)
        {
            if (!double.TryParse(duration.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double amount) || amount <= 0 || amount > 1440)
            { Reply("Give a positive meeting duration within one day, such as ‘for 90 minutes’. Nothing has changed."); return true; }
            double minutes = amount * (duration.Groups[2].Value.StartsWith("h", StringComparison.OrdinalIgnoreCase) ? 60 : 1);
            if (minutes > 1440) { Reply("A meeting must fit within one day. Nothing has changed."); return true; }
            _filters.Duration = TimeSpan.FromMinutes(minutes);
        }

        var period = Regex.Match(details, @"\b(morning|afternoon|evening)\b", Ignore);
        if (period.Success)
        {
            if (named.Count == 0 && _filters.DayCount == null && _target!.Time != null) _filters.Days = _target.Time.Days.ToList();
            _filters.Period = period.Value.ToLowerInvariant();
            _filters.PeriodStart = TimeSpan.FromHours(_filters.Period switch { "morning" => 9, "afternoon" => 12, _ => 17 });
            _filters.PeriodEnd = TimeSpan.FromHours(_filters.Period switch { "morning" => 12, "afternoon" => 17, _ => 21 });
            _filters.SameTime = false; _findSlot = true; _time = null; _timeExplicit = false;
        }

        if (_filters.DayCount is int requestedCount && named.Count > 0 && named.Count != requestedCount)
        { Reply($"You asked for {requestedCount} meeting day(s) but named {named.Count}. Choose {requestedCount} weekday(s), or change the requested number. Nothing has changed."); return true; }
        var finish = Regex.Match(details, @"\b(?:end|finish|ending)(?: it| the class)?\s+at\s+(\d{1,2}(?::[0-5]\d)?\s*(?:am|pm)|\d{1,2}:[0-5]\d)\b", Ignore);
        if (finish.Success)
        {
            var clock = PreferenceParser.FindTimes("Monday at " + finish.Groups[1].Value, TimeSpan.FromMinutes(30)).FirstOrDefault().Time;
            TimeSpan end;
            if (clock != null) end = clock.Start;
            else if (!TimeSpan.TryParse(finish.Groups[1].Value, CultureInfo.InvariantCulture, out end) || end.TotalHours < 13 || end > TimeSpan.FromHours(24))
            { Reply("Give the ending time with am or pm, for example ‘finish at 4pm’. Nothing has changed."); return true; }
            _filters.EndAt = end; _filters.StartAt = null; _filters.SameStart = _filters.SameTime = false;
            _time = null; _timeExplicit = false; _findSlot = true;
            if (_filters.DayCount == null && _filters.Days == null) _filters.Days = named.Count > 0 ? named : _target!.Time?.Days.ToList();
        }
        var shift = Regex.Match(details, @"\b(\d+(?:\.\d+)?)\s*(minutes?|mins?|hours?|hrs?)\s+(later|earlier)\b", Ignore);
        if (shift.Success)
        {
            var anchor = previousPreview != null && previousPreview.Placement == _target ? previousPreview.Time : _time ?? _target!.Time;
            if (anchor == null) { Reply("That section has no meeting time to shift. Give me days and a full start/end time."); return true; }
            if (!double.TryParse(shift.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double amount) || amount > 1440)
            { Reply("Give a smaller shift within the same day. Nothing has changed."); return true; }
            double minutes = amount * (shift.Groups[2].Value.StartsWith("h", StringComparison.OrdinalIgnoreCase) ? 60 : 1);
            if (minutes > 1440) { Reply("The shift must stay within the same day. Nothing has changed."); return true; }
            var offset = TimeSpan.FromMinutes(minutes * (shift.Groups[3].Value.Equals("earlier", StringComparison.OrdinalIgnoreCase) ? -1 : 1));
            var shiftedDays = named.Count > 0 ? named : _filters.Days ?? anchor.Days.ToList();
            var shiftedLength = _filters.Duration ?? (_filters.PreserveWeeklyHours ? TimeSpan.FromTicks(WeeklyDuration(_target!).Ticks / shiftedDays.Count) : anchor.Duration);
            if (anchor.Start + offset < TimeSpan.Zero || anchor.Start + offset + shiftedLength > TimeSpan.FromHours(24))
            { Reply("That shift would cross midnight. Choose times within the same day; nothing has changed."); return true; }
            _filters.Direction = 0; _filters.StartAt = _filters.EndAt = null; _filters.SameStart = false;
            _filters.Days = shiftedDays;
            _time = new(shiftedDays, anchor.Start + offset, anchor.Start + offset + shiftedLength); _timeExplicit = true; _findSlot = false;
            skipParser = true; return false;
        }

        if (named.Count > 0 && (dayChange || period.Success || duration.Success || hasClock || _awaiting == "meeting-days" || _filters.DayCount != null))
        {
            if (_filters.DayCount is int count && named.Count != count)
            { Reply($"You asked for {count} meeting day(s) but named {named.Count}. Choose {count} weekday(s), or change the requested number. Nothing has changed."); return true; }
            _filters.Days = named;
            _awaiting = "";
            _findSlot = true;
        }
        bool anyDays = Regex.IsMatch(details, @"\b(?:any(?: weekday| day)?|find|options?)\b", Ignore);
        if (_filters.DayCount != null && _filters.Days == null && !anyDays)
        {
            _awaiting = "meeting-days";
            double hours = WeeklyDuration(_target!).TotalHours;
            Reply($"Which {_filters.DayCount} weekday(s) should {_target!.Section.Title} meet? I'll preserve its current {hours:0.##} weekly hours unless you give an explicit duration/end time. " +
                "You can also ask me to find day options.", new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday" }
                    .Where(d => !_filters.ExcludedDays.Contains(Enum.Parse<DayOfWeek>(d))).Append("Find day options").ToArray());
            return true;
        }

        string clocksOnly = duration.Success ? details.Remove(duration.Index, duration.Length) : details;
        clocksOnly = Regex.Replace(clocksOnly, @"\b(?:end|finish|ending)(?: it| the class)?\s+at\s+(\d{1,2}(?::[0-5]\d)?\s*(?:am|pm)|\d{1,2}:[0-5]\d)\b", " ", Ignore);
        var unmarked = Regex.Match(clocksOnly, @"\b(?:at|starting(?: at)?|start(?: at)?)\s+(\d{1,2}(?::[0-5]\d)?)(?![\d.:])\b(?!\s*(?:am|pm))", Ignore);
        if (unmarked.Success && !Regex.IsMatch(clocksOnly, @"\b(?:1[3-9]|2[0-3]):[0-5]\d\b") &&
            !Regex.IsMatch(clocksOnly, @"\b\d{1,2}(?::[0-5]\d)?\s*[-–]\s*\d{1,2}(?::[0-5]\d)?\s*(?:am|pm)\b", Ignore) && _filters.Period == null)
        { _awaiting = "clock"; _time = null; _timeExplicit = false; Reply("Is that am or pm? For example, ‘at 2pm’. I've kept the course and day conditions; nothing has changed."); return true; }

        // Interpret unambiguous 24-hour times and a start within a named part of day.
        var clock24 = Regex.Match(clocksOnly, @"\b(\d{1,2}:[0-5]\d)(?:\s*[-–]\s*(\d{1,2}:[0-5]\d))?\b(?!\s*(?:am|pm))", Ignore);
        bool use24 = clock24.Success && (int.Parse(clock24.Groups[1].Value.Split(':')[0]) >= 13 ||
            clock24.Groups[2].Success && int.Parse(clock24.Groups[2].Value.Split(':')[0]) >= 13);
        string parseText = clocksOnly;
        if (unmarked.Success && _filters.Period != null)
        {
            string clock = unmarked.Groups[1].Value;
            parseText = parseText[..unmarked.Index] + "at " + clock + (_filters.Period == "morning" ? "am" : "pm") + parseText[(unmarked.Index + unmarked.Length)..];
        }
        if (_awaiting == "clock" || _filters.PreserveWeeklyHours || _filters.Duration != null || _filters.EndAt != null || period.Success || use24 || unmarked.Success && _filters.Period != null)
        {
            var days = _filters.Days ?? (_filters.DayCount is int count ? Enumerable.Range(1, count).Select(d => (DayOfWeek)d).ToList() : (_time ?? _target!.Time)?.Days.ToList());
            if (days == null || days.Count == 0) { Reply("Which weekdays should this section meet? Include its start and end time."); return true; }
            var baseTime = _time ?? _target!.Time;
            var length = _filters.Duration ?? (_filters.PreserveWeeklyHours ? TimeSpan.FromTicks(WeeklyDuration(_target!).Ticks / days.Count) : baseTime?.Duration ?? TimeSpan.FromHours(1));
            var current = new MeetingTime(days, baseTime?.Start ?? _plan!.Rules.PreferredStartTime, (baseTime?.Start ?? _plan!.Rules.PreferredStartTime) + length);
            MeetingTime? parsed;
            if (use24)
            {
                if (!TimeSpan.TryParse(clock24.Groups[1].Value, CultureInfo.InvariantCulture, out var start) || start >= TimeSpan.FromHours(24))
                { Reply("Give a valid time within the same day. Nothing has changed."); return true; }
                var end = start + length;
                if (clock24.Groups[2].Success && !TimeSpan.TryParse(clock24.Groups[2].Value, CultureInfo.InvariantCulture, out end) || end <= start || end > TimeSpan.FromHours(24))
                { Reply("The end must be later than the start within the same day. Nothing has changed."); return true; }
                parsed = new(days, start, end);
            }
            else parsed = ParseTime(parseText, current);
            bool explicitClock = Regex.IsMatch(parseText, @"\b\d{1,2}(?::[0-5]\d)?\s*(?:am|pm)\b", Ignore) || use24;
            if (explicitClock && parsed == null)
            { Reply("Give one clear start/end time, for example ‘Friday 2pm–5pm’. Nothing has changed."); return true; }
            if (parsed != null && explicitClock)
            {
                _awaiting = ""; _filters.SameStart = false; _filters.EndAt = null;
                if (_filters.DayCount != null && _filters.Days == null)
                {
                    _filters.StartAt = parsed.Start;
                    if (Regex.IsMatch(parseText, @"\d\s*(?:am|pm)?\s*[-–]\s*\d", Ignore)) _filters.Duration = parsed.Duration;
                    _time = null; _timeExplicit = false; _findSlot = true;
                }
                else { _filters.StartAt = null; _time = parsed; _timeExplicit = true; _findSlot = false; }
            }
            else { _time = null; _timeExplicit = false; _findSlot = true; }
            skipParser = true;
        }
        return false;
    }

    private static string WeeklyHoursNote(Planner.OverrideProposal proposal)
    {
        var changed = proposal.ProposedChanges.Where(c => c.BeforeTime != null &&
            Math.Abs((c.BeforeTime.Duration.TotalHours * c.BeforeTime.Days.Count) - c.Time.Duration.TotalHours * c.Time.Days.Count) > 0.01);
        return string.Join(" ", changed.Select(c => $"Weekly hours for {c.Placement.Section.Course} would change from {c.BeforeTime!.Duration.TotalHours * c.BeforeTime.Days.Count:0.##} to {c.Time.Duration.TotalHours * c.Time.Days.Count:0.##}; review the course's contact-hour requirement before applying."));
    }
}
