using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RoomScheduler;

public partial class PlanningSession
{
    private record PendingCourseBaseline(string Source, string? Preferences, string Semester, string? Course,
        int? Minimum, int? Preferred, string Fingerprint, bool QueryOnly = false);
    private PendingCourseBaseline? _pendingCourseBaseline;

    private (int Minimum, int Preferred) EffectiveBaseline(string source, string? preferences, string semester, string course)
    {
        var saved = _store.Get(BaselineKey(semester, course));
        if (saved != null)
        {
            var values = JsonSerializer.Deserialize<int[]>(saved)!;
            return (values[0], values[1]);
        }
        var workbook = DepartmentWorkbook.FromSheets(Sheets(source));
        var faculty = FacultyStore.InMemory();
        foreach (var file in Files().Where(f => f.Kind == FileKind.Reference || f.Name == source || f.Name == preferences).OrderBy(f => f.Saved))
            ReferenceSheets.Apply(Sheets(file.Name), workbook, faculty);
        var section = workbook.Sections.First(s => s.Course.Equals(course, StringComparison.OrdinalIgnoreCase));
        return (section.MinimumRoomCapacity, section.PreferredRoomCapacity);
    }

    private static readonly Regex BaselineAmounts = new(
        @"\b(?<field>minimum|min|preferred|pref)(?:\s+(?:room\s+)?(?:capacity|size|seats?))?\s*(?:to|of|at|is|=|:)?\s*(?<value>-?\d+(?:[.,]\d+)*)(?!\w)|" +
        @"(?<!\w)(?<value>-?\d+(?:[.,]\d+)*)\s+(?<field>minimum|min|preferred|pref)\b", RegexOptions.IgnoreCase);

    private static string NormalizeBaselineRequest(string text) => Regex.Replace(text,
        @"\b(minimum|min|preferred|pref)(?:\s+(?:room\s+)?(?:capacity|size|seats?))?\s+for\s+(.+?)\s+(?:to|at|=)\s*(-?\d+(?:[.,]\d+)*)(?!\w)",
        "$2 $1 seats to $3", RegexOptions.IgnoreCase);

    private List<string> BaselineCourses(string text, DepartmentWorkbook workbook)
    {
        string selector = BaselineAmounts.Replace(text, " ");
        selector = Regex.Replace(selector, @"\b(?:Fall|Spring|Summer)\s+20\d{2}\b", " ", RegexOptions.IgnoreCase);
        var courses = workbook.Sections.Select(s => s.Course).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var exact = courses.Where(c => Regex.IsMatch(selector, $@"(?<!\w)(?:CS\s*)?{Regex.Escape(c)}(?!\w)", RegexOptions.IgnoreCase)).ToList();
        if (exact.Count > 0) return exact;
        if (Regex.IsMatch(selector, @"\b\d{3}[a-z]?\b", RegexOptions.IgnoreCase)) return new();
        var words = Regex.Matches(selector.ToLowerInvariant(), @"[a-z]+")
            .Select(m => m.Value).Where(w => w.Length >= 3 && !SchedulingConversation.IsCourseStopWord(w) &&
                !new[] { "baseline", "baselines", "minimum", "preferred", "semester", "save", "set", "reset", "clear", "remove", "values", "value", "physical", "student", "students", "enrollment", "update" }.Contains(w)).ToList();
        var named = workbook.Sections.Where(s => !s.InstructorMissing && Regex.IsMatch(selector, $@"\b{Regex.Escape(s.Surname)}\b", RegexOptions.IgnoreCase))
            .Select(s => s.Course).Distinct().ToList();
        var scored = workbook.Sections.Select(s => (s.Course, Score: words.Count(w =>
            Regex.Matches(s.Title.ToLowerInvariant(), @"[a-z]+").Any(t => t.Value.StartsWith(w) || t.Value.Length >= 4 && w.StartsWith(t.Value)))))
            .Where(s => s.Score > 0).ToList();
        if (scored.Count > 0)
        {
            var best = scored.Where(s => s.Score == scored.Max(x => x.Score)).Select(s => s.Course).Distinct().ToList();
            return named.Count > 0 ? best.Where(named.Contains).ToList() : best;
        }
        if (named.Count > 0) return named;
        if (Regex.IsMatch(selector, @"\b(?:it|its|that course|same course|that class)\b", RegexOptions.IgnoreCase))
            return Conversation.ReferencedCourseNumbers.Where(courses.Contains).ToList();
        return new();
    }

    private bool TryCourseBaselineChat(string message)
    {
        const RegexOptions ignore = RegexOptions.IgnoreCase;
        bool confirm = Regex.IsMatch(message, @"^(?:confirm (?:course |room |semester )?baseline|save(?: (?:it|this baseline|the baseline))?(?: for (?:this |the )?semester)? and rebuild)[.!?]?$", ignore) ||
            _pendingCourseBaseline != null && Regex.IsMatch(message, @"^(?:yes|yes please|confirm)[.!?]?$", ignore);
        if (confirm)
        {
            var pending = _pendingCourseBaseline;
            if (pending?.Course == null || pending.Minimum == null && pending.Preferred == null)
            { Conversation.SessionReply(message, "Specify the course and minimum or preferred seats first; I will show the semester baseline before you confirm."); return true; }
            if (pending.Fingerprint != FileConfirmationFingerprint())
            { _pendingCourseBaseline = null; Conversation.SessionReply(message, "Files, settings, or the schedule changed since the baseline review. Describe the baseline again before confirming."); return true; }
            bool saved = false;
            try
            {
                var current = EffectiveBaseline(pending.Source, pending.Preferences, pending.Semester, pending.Course);
                int minimum = pending.Minimum ?? current.Minimum, preferred = pending.Preferred ?? current.Preferred;
                if (minimum < 0 || preferred < 0 || preferred > 0 && preferred < minimum)
                    throw new ArgumentException("Preferred seats must be at least minimum seats, or 0 to clear the preference.");
                SetSemesterBaseline(pending.Semester, pending.Course, minimum, preferred); saved = true;
                _pendingCourseBaseline = null;
                Build(pending.Source, pending.Preferences);
                Conversation.SessionReply(message, $"Saved baseline for {pending.Course}, {pending.Semester}: minimum {minimum}, preferred {preferred}. " +
                    $"Rebuilt {pending.Source}; previous manual room/time moves were replaced. " +
                    $"{LatestPlan!.Placements.Count(p => !p.Section.Online && (p.Room == null || p.Time == null))} in-person section(s) still need placing. " +
                    "Enrollment requirements and the registrar capacity-minus-10% rule still apply.", "What needs attention?", $"Show baseline for {pending.Course}");
            }
            catch (Exception ex)
            { Conversation.SessionReply(message, (saved ? "The baseline was saved, but rebuilding failed. " : "The baseline was not saved. ") + ex.Message + " The previous schedule has not been replaced."); }
            return true;
        }
        if (_pendingCourseBaseline != null && Regex.IsMatch(message, @"^(?:cancel|cancel baseline|no|never mind)[.!?]?$", ignore))
        { _pendingCourseBaseline = null; Conversation.SessionReply(message, "Canceled the baseline change. Saved values and the schedule are unchanged."); return true; }

        string text = NormalizeBaselineRequest(message);
        bool baselineWord = Regex.IsMatch(text, @"\bbaselines?\b", ignore);
        bool fieldWord = Regex.IsMatch(text, @"\b(?:minimum|min|preferred|pref)\b", ignore);
        bool change = Regex.IsMatch(text, @"\b(?:set|save|change|update|clear|reset|remove|make)\b", ignore);
        bool question = Regex.IsMatch(text, @"^\s*(?:what|show|explain|how|which|find)\b", ignore) && !change;
        bool courseChoice = _pendingCourseBaseline != null && Regex.IsMatch(text.Trim(), @"^(?:CS\s*)?\d{3}[a-z]?[.!?]?$", ignore);
        bool sizeIntent = BaselineAmounts.Matches(text).Count > 0 || Regex.IsMatch(text, @"\b(?:seats?|capacity|size|vs|versus|difference|mean|meaning)\b", ignore) ||
            Regex.IsMatch(text, @"\b(?:clear|reset|remove)\b", ignore);
        bool relevant = baselineWord || fieldWord && (sizeIntent || _pendingCourseBaseline != null) && (change || question || _pendingCourseBaseline != null) || courseChoice;
        if (Regex.IsMatch(text, @"\b(?:files?|uploads?|uploaded|imports?)\b", ignore) && BaselineAmounts.Matches(text).Count == 0)
        { _pendingCourseBaseline = null; return false; }
        if (!relevant || !baselineWord && Regex.IsMatch(text, @"\b(?:find|move|swap|suggest|recommend)\b", ignore))
        { _pendingCourseBaseline = null; return false; }
        _pendingFileConfirmation = null; _pendingBuildingPriority = null;
        if (question && !Regex.IsMatch(text, @"\b\d{3}[a-z]?\b|\bfor\b", ignore) && Regex.IsMatch(text, @"\b(?:vs|versus|difference|mean|meaning)\b", ignore))
        {
            Conversation.SessionReply(message, "Minimum seats is a hard room-capacity floor. Preferred seats steers room choice. Enrollment still determines required student seats; the registrar cap remains room capacity minus 10%. " +
                "Use 0 to clear a baseline value. For example, ‘Set 103F minimum seats to 40 and preferred seats to 60’. Saving is semester-specific and rebuilds the schedule after confirmation."); return true;
        }
        try
        {
            if (Regex.IsMatch(text, @"\bor\s+-?\d", ignore) || BaselineAmounts.Matches(text)
                .GroupBy(m => m.Groups["field"].Value.StartsWith("min", StringComparison.OrdinalIgnoreCase))
                .Any(g => g.Select(m => m.Groups["value"].Value).Distinct().Count() > 1))
                throw new ArgumentException("Give one minimum and one preferred seat count, rather than alternative values.");
            var draft = _pendingCourseBaseline;
            bool continuation = draft != null && (!baselineWord || !change);
            if (continuation && draft!.Fingerprint != FileConfirmationFingerprint())
                throw new ArgumentException("Files or settings changed while clarifying the baseline. Start the baseline request again using the current files.");
            var files = LatestPlan != null ? (Source: LatestSource, Preferences: LatestPreferences) : RecommendedFiles();
            if (continuation) files = (draft!.Source, draft.Preferences);
            if (continuation && draft!.QueryOnly && !change && BaselineAmounts.Matches(text).Count == 0) question = true;
            var workbook = DepartmentWorkbook.FromSheets(Sheets(files.Source));
            if (workbook.Sections.Count == 0) throw new ArgumentException("Upload a Classes App workbook or build a schedule first so I can identify the course and semester.");
            if (string.IsNullOrWhiteSpace(workbook.Semester)) throw new ArgumentException("The Classes App workbook needs Semester and Year values before I can save a semester-specific baseline.");
            if (Regex.Matches(text, @"\b(?:Fall|Spring|Summer)\s+20\d{2}\b", ignore).Select(m => m.Value.ToLowerInvariant()).Distinct().Count() > 1)
                throw new ArgumentException("Change one semester at a time. Name the semester of the selected workbook.");
            var namedSemester = Regex.Match(text, @"\b(?:Fall|Spring|Summer)\s+20\d{2}\b", ignore);
            if (namedSemester.Success && !namedSemester.Value.Equals(workbook.Semester, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"This workbook is for {workbook.Semester}, but you named {namedSemester.Value}. Select the workbook for that semester first.");
            string selector = BaselineAmounts.Replace(text, " ");
            var unknown = Regex.Matches(selector, @"\b(?:CS\s*)?(\d{3}[a-z]?)\b", ignore)
                .Select(m => m.Groups[1].Value).Where(c => !workbook.Sections.Any(s => s.Course.Equals(c, StringComparison.OrdinalIgnoreCase))).ToList();
            if (unknown.Count > 0) throw new ArgumentException("I couldn't match course " + string.Join(", ", unknown) + " in this workbook. Give a course from the current semester.");
            var courses = BaselineCourses(text, workbook);
            if (continuation && draft!.Course != null && (courses.Count > 1 || courses.Count == 1 && courses[0] != draft.Course)) continuation = false;
            string? course = courses.Count == 1 ? courses[0] : continuation ? draft!.Course : null;
            if (courses.Count > 1) course = null;
            int? minimum = continuation ? draft!.Minimum : null, preferred = continuation ? draft!.Preferred : null;
            foreach (Match amount in BaselineAmounts.Matches(text))
            {
                string raw = amount.Groups["value"].Value;
                if (Regex.IsMatch(raw, @"^-?\d{1,3}(?:,\d{3})+$")) raw = raw.Replace(",", "");
                if (!int.TryParse(raw, out int value) || value < 0)
                    throw new ArgumentException("Use whole, nonnegative seat counts; 0 clears a value.");
                if (amount.Groups["field"].Value.StartsWith("min", StringComparison.OrdinalIgnoreCase)) minimum = value; else preferred = value;
            }
            bool clear = Regex.IsMatch(text, @"\b(?:clear|reset|remove)\b", ignore);
            if (clear)
            {
                bool both = Regex.IsMatch(text, @"\b(?:both|all)\b", ignore) || baselineWord && !fieldWord;
                if (both || Regex.IsMatch(text, @"\b(?:minimum|min)\b", ignore) && !BaselineAmounts.Matches(text).Any(m => m.Groups["field"].Value.StartsWith("min", StringComparison.OrdinalIgnoreCase))) minimum = 0;
                if (both || Regex.IsMatch(text, @"\b(?:preferred|pref)\b", ignore) && !BaselineAmounts.Matches(text).Any(m => m.Groups["field"].Value.StartsWith("pref", StringComparison.OrdinalIgnoreCase))) preferred = 0;
            }
            if (question && course != null)
            {
                if (_pendingCourseBaseline?.Course != course) _pendingCourseBaseline = null;
                var current = EffectiveBaseline(files.Source, files.Preferences, workbook.Semester, course);
                Conversation.SessionReply(message, $"Baseline for {course}, {workbook.Semester}: minimum {current.Minimum} seats; preferred {current.Preferred} seats. " +
                    "0 means no baseline for that value. Minimum is a hard floor; preferred steers room choice. Enrollment and the registrar capacity-minus-10% rule still apply."); return true;
            }
            _pendingFileConfirmation = null; _pendingBuildingPriority = null;
            _pendingCourseBaseline = new(files.Source, files.Preferences, workbook.Semester, course, minimum, preferred, FileConfirmationFingerprint(), question);
            if (course == null)
            {
                var choices = (courses.Count > 0 ? courses : workbook.Sections.Select(s => s.Course).Distinct().ToList()).Take(12).ToArray();
                Conversation.SessionReply(message, "Which course should get this semester baseline? Give its course number, such as 103F. The baseline applies to every section of that course in the semester.", choices); return true;
            }
            var before = EffectiveBaseline(files.Source, files.Preferences, workbook.Semester, course);
            if (minimum == null && preferred == null)
            { Conversation.SessionReply(message, $"For {course}, {workbook.Semester}, the current minimum is {before.Minimum} and preferred is {before.Preferred}. What minimum and/or preferred seat count should I save? Use 0 to clear a value."); return true; }
            int nextMin = minimum ?? before.Minimum, nextPref = preferred ?? before.Preferred;
            if (nextPref > 0 && nextPref < nextMin)
            { Conversation.SessionReply(message, $"Preferred {nextPref} is below minimum {nextMin}. Give a preferred value at least {nextMin}, or ‘clear preferred’. Nothing has been saved."); return true; }
            Conversation.SessionReply(message, $"Proposed baseline for {course}, {workbook.Semester}, all {workbook.Sections.Count(s => s.Course == course)} section(s): " +
                $"minimum {before.Minimum} → {nextMin} seats; preferred {before.Preferred} → {nextPref} seats. " +
                "Minimum is a hard room-capacity floor; preferred steers room choice. 0 clears that baseline value. Enrollment still controls required student seats; the registrar cap remains capacity minus 10%. " +
                $"Confirm to save for this semester and rebuild {files.Source}, replacing current manual room/time moves. Nothing has changed yet.", "Confirm course baseline", "Cancel baseline");
        }
        catch (ArgumentException ex) { _pendingCourseBaseline = null; Conversation.SessionReply(message, ex.Message + " Saved baselines and the schedule are unchanged."); }
        catch (Exception ex) { _pendingCourseBaseline = null; Conversation.SessionReply(message, "I couldn't read the baseline inputs: " + ex.Message + " Nothing has been saved."); }
        return true;
    }
}
