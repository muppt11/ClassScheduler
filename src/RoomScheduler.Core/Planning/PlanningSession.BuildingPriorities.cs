using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RoomScheduler;

public partial class PlanningSession
{
    private record PendingBuildingPriority(BuildingPrioritySettings Settings, string Fingerprint, string Source, string? Preferences);
    private PendingBuildingPriority? _pendingBuildingPriority;

    private void LoadBuildingPriorities()
    {
        var saved = _store.Get("building-priorities");
        Rules.BuildingPriority = saved == null ? new() : JsonSerializer.Deserialize<BuildingPrioritySettings>(saved) ?? new();
        Rules.BuildingPriority.Validate();
    }

    public void SetBuildingPriorities(BuildingPrioritySettings settings)
    {
        settings = settings with { Buildings = settings.Buildings.Select(b => b.Trim().ToUpperInvariant()).ToList() };
        settings.Validate();
        var known = Rooms.Select(r => r.Building).Concat(Campus.Buildings).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = settings.Buildings.Where(b => !known.Contains(b)).ToList();
        if (settings.Enabled && unknown.Count > 0) throw new ArgumentException("Unknown building code(s): " + string.Join(", ", unknown) + ". Add their rooms first or choose codes from the room inventory.");
        _store.Set("building-priorities", JsonSerializer.Serialize(settings));
        Rules.BuildingPriority = settings;
        MarkInputsChanged();
    }

    public string BuildingUseReport(string building, TimeSpan? start = null, TimeSpan? end = null)
    {
        building = building.ToUpperInvariant();
        var settings = Rules.BuildingPriority;
        TimeSpan from = start ?? settings.Start, until = end ?? settings.End;
        if (from < TimeSpan.Zero || until > TimeSpan.FromHours(24) || from >= until)
            throw new ArgumentException("Use a start earlier than the finish within the same day.");
        var active = Rooms.Where(r => r.Building.Equals(building, StringComparison.OrdinalIgnoreCase) && !Rules.Offline.IsOffline(r) &&
            !(r.DepartmentOnly && (r.Notes.Contains("pod", StringComparison.OrdinalIgnoreCase) || r.Notes.Contains("project lab", StringComparison.OrdinalIgnoreCase)))).ToList();
        var placed = LatestPlan?.Placements.Concat(LatestPlan.Discussions)
            .Where(p => p.Room != null && active.Any(r => r.DisplayName == p.Room.DisplayName) && p.Time != null).ToList() ?? new();
        double hours = placed.Sum(p => Math.Max(0, (Math.Min(p.Time!.End.Ticks, until.Ticks) - Math.Max(p.Time.Start.Ticks, from.Ticks)) / (double)TimeSpan.TicksPerHour) *
            p.Time.Days.Count(d => d >= DayOfWeek.Monday && d <= DayOfWeek.Friday));
        double windowHours = active.Count * 5 * (until - from).TotalHours;
        int inside = placed.Count(p => p.Time!.Start >= from && p.Time.End <= until);
        var controlled = active.Where(r => r.DepartmentOnly || r.CsOwnsFrom != null).ToList();
        return $"{building}, {MeetingTime.ClockTime(from)}–{MeetingTime.ClockTime(until)} on weekdays: {active.Count} active room(s) in the inventory; {placed.Count} scheduled section(s), {inside} entirely within the window. " +
            $"{hours:0.#} booked room-hours out of {windowHours:0.#} inventory window-hours per week. " +
            $"{controlled.Count} room(s) have CS-only or recorded CS-control metadata. " +
            (LatestPlan == null ? "No schedule has been built yet. " : RequiresRebuild ? "These assignments are from the previous schedule; a rebuild is pending. " : "") +
            "This measures the uploaded plan, not campus-wide bookings, and does not guarantee every inventory hour can be assigned. Capacity, instructor requests, and room eligibility still apply.";
    }

    private List<string> BuildingsMentioned(string text) => Rooms.Select(r => r.Building).Concat(Campus.Buildings)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(b => (Code: b, Match: Regex.Match(text, $@"\b{Regex.Escape(b)}\b", RegexOptions.IgnoreCase)))
        .Where(b => b.Match.Success).OrderBy(b => b.Match.Index).Select(b => b.Code.ToUpperInvariant()).ToList();

    private static (TimeSpan Start, TimeSpan End)? RequestedPriorityWindow(string text)
    {
        text = Regex.Replace(text, @"\b([ap])\.m\.", "$1m", RegexOptions.IgnoreCase);
        const string clock = @"\d{1,2}(?::[0-5]\d)?\s*(?:am|pm)?";
        var range = Regex.Match(text, @"\b(?:between|from)\s+(" + clock + @")\s*(?:and|to|[-–])\s*(" + clock + @")\b", RegexOptions.IgnoreCase);
        if (!range.Success)
        {
            if (Regex.IsMatch(text, @"\d", RegexOptions.IgnoreCase)) throw new ArgumentException("Give a full preferred window, for example ‘between 9:30am and 5pm’.");
            return null;
        }
        if (!Regex.IsMatch(range.Value, @"am|pm|\b(?:1[3-9]|2[0-3]):", RegexOptions.IgnoreCase))
            throw new ArgumentException("Include am or pm in the window, for example ‘between 9:30am and 5pm’.");
        TimeSpan Clock(string raw)
        {
            var value = Regex.Match(raw.Trim(), @"^(\d{1,2})(?::([0-5]\d))?\s*(am|pm)?$", RegexOptions.IgnoreCase);
            if (!value.Success) throw new ArgumentException("Use a clear start and finish, such as 9:30am and 5pm.");
            int hour = int.Parse(value.Groups[1].Value, CultureInfo.InvariantCulture);
            int minute = value.Groups[2].Success ? int.Parse(value.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            if (value.Groups[3].Success)
            {
                if (hour < 1 || hour > 12) throw new ArgumentException("Use hours 1–12 with am or pm.");
                hour = hour % 12 + (value.Groups[3].Value.Equals("pm", StringComparison.OrdinalIgnoreCase) ? 12 : 0);
            }
            else if (hour is >= 1 and <= 7) hour += 12;
            if (hour > 23) throw new ArgumentException("Use times within the same day.");
            return new TimeSpan(hour, minute, 0);
        }
        var start = Clock(range.Groups[1].Value); var end = Clock(range.Groups[2].Value);
        if (start >= end) throw new ArgumentException("That window is reversed. Try ‘between 9:30am and 5pm’ within one day.");
        return (start, end);
    }

    private bool TryBuildingPriorityChat(string text)
    {
        const RegexOptions ignore = RegexOptions.IgnoreCase;
        bool confirm = Regex.IsMatch(text, @"^confirm building priorities[.!?]?$", ignore) ||
            _pendingBuildingPriority != null && Regex.IsMatch(text, @"^(yes|yes please|confirm)[.!?]?$", ignore);
        if (confirm)
        {
            var pending = _pendingBuildingPriority;
            _pendingBuildingPriority = null;
            if (pending == null) { Conversation.SessionReply(text, "Describe the building order and preferred hours first so I can show the settings before you confirm."); return true; }
            if (pending.Fingerprint != FileConfirmationFingerprint())
            { Conversation.SessionReply(text, "The files, settings, or schedule changed since the priority review. Describe the priority again before confirming."); return true; }
            bool saved = false;
            try
            {
                SetBuildingPriorities(pending.Settings); saved = true;
                if (pending.Source.Length > 0) Build(pending.Source, pending.Preferences);
                Conversation.SessionReply(text, "Saved building priorities. " + Rules.BuildingPriority.Describe() +
                    (pending.Source.Length > 0 ? $"\nRebuilt from {pending.Source}; previous manual room/time moves were replaced. " +
                        $"{LatestPlan!.Placements.Count(p => !p.Section.Online && (p.Room == null || p.Time == null))} section(s) still need placing.\n" +
                        BuildingUseReport(pending.Settings.Buildings.FirstOrDefault() ?? Rules.HomeBuilding) : "\nThey will apply when you upload files and build a schedule."),
                    "Show building priorities", "What needs attention?");
            }
            catch (Exception ex)
            { Conversation.SessionReply(text, (saved ? "The priorities were saved, but the schedule could not be rebuilt. " : "The priorities could not be saved. ") + ex.Message + " The previous schedule has not been replaced."); }
            return true;
        }
        if (_pendingBuildingPriority != null && Regex.IsMatch(text, @"^(cancel|cancel priorities|no|never mind)[.!?]?$", ignore))
        { _pendingBuildingPriority = null; Conversation.SessionReply(text, "Canceled the priority change. Saved settings and the schedule are unchanged."); return true; }
        var buildings = BuildingsMentioned(text);
        string building = buildings.FirstOrDefault() ?? Rules.BuildingPriority.Buildings.FirstOrDefault() ?? Rules.HomeBuilding;
        if (Regex.IsMatch(text, @"\b(how full|utilization|utilisation|room usage|room-hours|using .*space|how much.*(?:use|using))\b", ignore))
        {
            try { var window = RequestedPriorityWindow(text); Conversation.SessionReply(text, BuildingUseReport(building, window?.Start, window?.End)); }
            catch (ArgumentException ex) { Conversation.SessionReply(text, ex.Message); }
            return true;
        }
        if (Regex.IsMatch(text, @"\b(what|which|show)\b.*\b(rooms?|space)\b.*\b(control|own|CS-only)\b", ignore))
        {
            var controlled = Rooms.Where(r => r.Building.Equals(building, StringComparison.OrdinalIgnoreCase) && (r.DepartmentOnly || r.CsOwnsFrom != null)).ToList();
            Conversation.SessionReply(text, controlled.Count == 0 ? $"No CS-control metadata is recorded for {building} rooms in this inventory." :
                string.Join("\n", controlled.Select(r => $"{r.DisplayName}: {r.Capacity} seats; " + (r.DepartmentOnly ? "CS-only departmental room" : $"CS control from {MeetingTime.ClockTime(r.CsOwnsFrom!.Value)}") +
                    (Rules.Offline.IsOffline(r) ? "; offline" : "") + ". " + r.Notes)) + "\nPod/project labs still require a specific room request; control metadata does not override room eligibility.");
            return true;
        }
        if (buildings.Count > 0 && Regex.IsMatch(text, @"\b(history|past|previous|prior|last semester)\b", ignore) && !Regex.IsMatch(text, @"\b(prioriti[sz](?:e|ing)|fill)\b", ignore))
        {
            var named = Past.Sections.SelectMany(p => p.Instructors).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(n => Regex.IsMatch(text, $@"\b{Regex.Escape(n)}\b", ignore)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var person = Regex.Match(text, @"^(?:did|has|was)\s+(?:professor\s+)?([a-z][a-z'-]*)\b", ignore);
            if (named.Count == 0 && person.Success && !new[] { "faculty", "anyone", "we", "they", "professors" }.Contains(person.Groups[1].Value.ToLowerInvariant()))
            { Conversation.SessionReply(text, $"No matching history for {person.Groups[1].Value} in {building} was found in the active uploaded schedules."); return true; }
            var history = Past.Sections.Where(p => p.Building.Equals(building, StringComparison.OrdinalIgnoreCase) && (named.Count == 0 || p.Instructors.Any(named.Contains)))
                .OrderByDescending(p => p.SemesterCode).ToList();
            Conversation.SessionReply(text, history.Count == 0 ? $"No matching faculty use of {building} appears in the active uploaded past schedules." :
                string.Join("\n", history.Take(8).Select(p => $"{string.Join(", ", p.Instructors)}: {p.Course} {p.Title}, {p.RoomName}, {p.Semester}.")) + "\nOnly active uploaded history is used; this does not infer campus-wide room availability.");
            return true;
        }
        if (Regex.IsMatch(text, @"^(?:show|what|which|explain|why)\b.*\b(?:building priorit(?:y|ies)|prioriti[sz]e|priority list)\b", ignore))
        { Conversation.SessionReply(text, Rules.BuildingPriority.Describe() + "\n" + BuildingUseReport(building)); return true; }

        bool disabling = Regex.IsMatch(text, @"\b(?:turn off|disable|clear|remove) (?:the )?building priorit(?:y|ies)\b", ignore);
        bool request = disabling || Regex.IsMatch(text, @"\b(?:prioriti[sz](?:e|ing)|fill(?: up)?|priority list|building priorities)\b", ignore) ||
            buildings.Count > 0 && Regex.IsMatch(text, @"\buse .+ first\b", ignore);
        bool refine = _pendingBuildingPriority != null && Regex.IsMatch(text, @"\b(?:between|from|then|also|history|controlled)\b", ignore);
        if (!request && !refine) return false;
        try
        {
            var current = _pendingBuildingPriority?.Settings ?? Rules.BuildingPriority;
            var order = buildings;
            if (refine && Regex.IsMatch(text, @"^\s*(?:then|also|and)\b", ignore)) order = current.Buildings.Concat(order).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (order.Count == 0 && !refine && !disabling) throw new ArgumentException("Name a building from the room inventory, for example ‘Prioritize GDC, then RLP, between 9:30am and 5pm’.");
            var unknownCodes = Regex.Matches(text, @"\b[A-Z]{2,5}\b").Select(m => m.Value)
                .Where(c => c is not ("CS" or "AM" or "PM") && !Rooms.Any(r => r.Building.Equals(c, StringComparison.OrdinalIgnoreCase)) && !Campus.Knows(c)).ToList();
            var fillers = new[] { "THEN", "ALSO", "AND", "CS", "AM", "PM", "NO", "NOT", "USE", "ROOM", "ROOMS", "SPACE", "FIRST", "FROM", "WITH", "AFTER", "PRIOR", "USING", "LIKE", "KEEP", "SAVE", "SHOW", "READ", "HAVE", "MORE", "ONLY" };
            unknownCodes.AddRange(Regex.Matches(text, @"(?=(?:\b(?:then|also|and)\s+|,\s*)([a-z]{2,5})\b)", ignore)
                .Select(m => m.Groups[1].Value.ToUpperInvariant())
                .Where(c => !fillers.Contains(c) && !Rooms.Any(r => r.Building.Equals(c, StringComparison.OrdinalIgnoreCase)) && !Campus.Knows(c)));
            if (unknownCodes.Count > 0) throw new ArgumentException("Unknown building code(s): " + string.Join(", ", unknownCodes));
            var window = RequestedPriorityWindow(text);
            var proposed = current with { Enabled = !disabling, Buildings = order.Count == 0 ? current.Buildings.ToList() : order,
                Start = window?.Start ?? current.Start, End = window?.End ?? current.End,
                UseFacultyHistory = Regex.IsMatch(text, @"\b(?:without|ignore|don't use) (?:faculty )?history\b", ignore) ? false : Regex.IsMatch(text, @"\b(?:use|using|include|consider) (?:faculty |past |prior )*history\b", ignore) ? true : current.UseFacultyHistory,
                PreferControlledRooms = Regex.IsMatch(text, @"\b(?:without|ignore) (?:CS[- ]?)?controlled\b", ignore) ? false :
                    Regex.IsMatch(text, @"\b(?:prefer|use) (?:CS[- ]?)?controlled\b", ignore) ? true : current.PreferControlledRooms };
            proposed.Validate();
            var files = LatestPlan != null && !RequiresRebuild ? (Source: LatestSource, Preferences: LatestPreferences) : RecommendedFiles();
            _pendingFileConfirmation = null;
            _pendingBuildingPriority = new(proposed, FileConfirmationFingerprint(), files.Source, files.Preferences);
            Conversation.SessionReply(text, "Proposed scheduling priorities: " + proposed.Describe() +
                (files.Source.Length == 0 ? "\nConfirm to save these settings for the next schedule." : $"\nConfirm to save and rebuild {files.Source}" +
                    (files.Preferences != null ? $" using {files.Preferences}" : "") + ". This replaces current manual room/time moves.") +
                " Nothing has changed yet. This is a bounded scheduling search, not a guarantee of maximum building occupancy.", "Confirm building priorities", "Cancel priorities");
        }
        catch (ArgumentException ex) { _pendingBuildingPriority = null; Conversation.SessionReply(text, ex.Message + " Saved priorities and the schedule are unchanged."); }
        return true;
    }
}
