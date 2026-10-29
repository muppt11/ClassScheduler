using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RoomScheduler;

public partial class PlanningSession
{
    private string? _pendingFileConfirmation;
    public string ActiveRoomsFile => _roomsTableSource;

    public (string Source, string? Preferences) RecommendedFiles()
    {
        var latest = Of(FileKind.Requests).FirstOrDefault();
        if (latest == null) return ("", null);
        var workbook = DepartmentWorkbook.FromSheets(Sheets(latest.Name));
        var preferences = Of(FileKind.Preferences).FirstOrDefault(f =>
            (workbook.Forms.Count == 0 || f.Saved > latest.Saved) &&
            DepartmentWorkbook.FromSheets(Sheets(f.Name)).Forms.Any(form =>
                workbook.Sections.Any(s => s.Surname == form.Surname && s.Course == form.CourseNumber)));
        return (latest.Name, preferences?.Name);
    }

    public string FileUpdateSummary()
    {
        var files = Files();
        if (files.Count == 0) return "No files have been uploaded yet. Add updated spreadsheets under Additional files on Semester plan; I can then confirm which data they provide.";
        var next = RecommendedFiles();
        var nextWorkbook = next.Source.Length == 0 ? null : DepartmentWorkbook.FromSheets(Sheets(next.Source));
        var lines = new List<string> {
            $"Latest upload: {files[0].Name} — {FileType(files[0].Kind)}. {files[0].Detail}.",
            next.Source.Length == 0 ? "No Classes App workbook is available to build a schedule." :
                $"Next build with newest files: {next.Source} ({nextWorkbook!.Sections.Count} sections; {(nextWorkbook.Semester.Length == 0 ? "semester not specified" : nextWorkbook.Semester)}); preferences: {next.Preferences ?? "tabs in that Classes App workbook"}.",
            $"Rooms currently loaded: {RoomsSource} ({Rooms.Count} rooms).",
            "Building priorities: " + Rules.BuildingPriority.Describe(),
            $"Lecture capture currently loaded: {LectureCaptureSource} ({LectureCaptureRooms} rooms).",
            "History: " + (ActiveHistory.Count == 0 ? "no active uploaded schedules." : string.Join(", ", ActiveHistory.OrderBy(n => n)) + ". The newest uploaded copy of each semester supplies its history.")
        };
        var references = files.Where(f => f.Kind == FileKind.Reference).Select(f => f.Name).ToList();
        lines.Add("Reference updates: " + (references.Count == 0 ? "any reference tabs in the chosen workbooks." :
            string.Join(", ", references) + "; matching values are applied in upload order, newest last."));
        if (LatestPlan == null) lines.Add("Schedule: not built yet. Uploaded data is saved, but no schedule has been generated from it.");
        else lines.Add($"Current schedule was built from {LatestSource}" +
            (LatestPreferences != null ? $" using {LatestPreferences}" : "") + ". " +
            (RequiresRebuild ? "Rebuild required: the current schedule has not incorporated the pending data/settings changes." : "No pending data/settings changes for this built schedule."));
        lines.Add("Saved manual faculty choices (including cleared D/T/P), semester room baselines, added rooms, and offline settings remain in force.");
        var other = files.Where(f => f.Kind == FileKind.Other).Select(f => f.Name).ToList();
        if (other.Count > 0) lines.Add("Kept for reference only: " + string.Join(", ", other) + ". Their contents do not automatically change scheduling data.");
        return string.Join("\n", lines);
    }

    public string DescribeUpload(LibraryFile file) => file.Kind switch
    {
        FileKind.Rooms => $"Saved {file.Name} as the current rooms table. Rebuild to use the updated inventory in the schedule.",
        FileKind.LectureCapture => $"Saved {file.Name} as the current lecture-capture list. Rebuild to apply it to the schedule.",
        FileKind.PastSchedule => $"Saved {file.Name} as active history. Newest uploaded copies replace older data for the same semester; rebuild to apply.",
        FileKind.Reference => $"Saved {file.Name} as a reference update. Newer matching entries take precedence over older imports; saved manual choices remain. Rebuild to apply.",
        FileKind.Requests => $"Saved {file.Name} as the newest Classes App workbook. Review file updates with the chatbot or build the selected workbook to use it.",
        FileKind.Preferences => $"Saved {file.Name} as scheduling preferences. Select it for a matching Classes App workbook, or review file updates with the chatbot.",
        FileKind.Other => $"Saved {file.Name} for reference only. No supported scheduling table was detected, so its contents will not update the schedule.",
        _ => $"{file.Name}: {file.Detail}. Not saved."
    };

    private static string FileType(FileKind kind) => kind switch
    {
        FileKind.Requests => "Classes App workbook", FileKind.Preferences => "scheduling preferences",
        FileKind.Rooms => "rooms table", FileKind.LectureCapture => "lecture-capture list",
        FileKind.Reference => "course/faculty reference", FileKind.PastSchedule => "past schedule",
        _ => "reference-only file"
    };

    private string FileConfirmationFingerprint()
    {
        var next = RecommendedFiles();
        return JsonSerializer.Serialize(new {
        InputsRevision = _inputsRevision, Files = Files().Select(f => new { f.Name, f.Kind, f.Saved }), Next = new { next.Source, next.Preferences },
        Faculty = Faculty.All.ToList(), Rules, Rooms = Rooms.Select(r => new { r.DisplayName, r.Capacity, r.Features, r.LectureCapture }),
        History = ActiveHistory.OrderBy(n => n).ToList(), Source = LatestSource, Preferences = LatestPreferences,
        Manual = LatestPlan?.Placements.Concat(LatestPlan.Discussions).Select(p => new { p.Section.SourceRow, Room = p.Room?.DisplayName, p.Time })
        });
    }

    /// <summary>File questions remain available when stale scheduling proposals are blocked.</summary>
    public void SendChat(string message, bool allowOversized = false)
    {
        string text = message.Trim();
        if (text.Length == 0) return;
        if (TryCourseBaselineChat(text)) return;
        if (TryBuildingPriorityChat(text)) return;
        const RegexOptions ignore = RegexOptions.IgnoreCase;
        bool confirm = Regex.IsMatch(text, @"^(?:confirm rebuild with newest files|confirm updates|yes,? rebuild(?: with newest files)?|apply updated files)[.!?]?$", ignore) ||
            _pendingFileConfirmation != null && Regex.IsMatch(text, @"^(?:yes|yes please|confirm)[.!?]?$", ignore);
        if (confirm)
        {
            if (_pendingFileConfirmation == null)
            { Conversation.SessionReply(text, "Review the file updates first so you can confirm the exact source files before rebuilding.", "Review file updates"); return; }
            if (_pendingFileConfirmation != FileConfirmationFingerprint())
            {
                _pendingFileConfirmation = null;
                Conversation.SessionReply(text, "The files, settings, or schedule changed since that review. Review the latest file updates before confirming a rebuild.", "Review file updates"); return;
            }
            _pendingFileConfirmation = null;
            var next = RecommendedFiles();
            try
            {
                Build(next.Source, next.Preferences);
                Conversation.SessionReply(text, $"Confirmed: rebuilt the schedule from {next.Source}" +
                    (next.Preferences != null ? $" using {next.Preferences}" : " using its embedded preferences") +
                    ". The updated room, history, and reference data have been checked by the scheduler. " +
                    $"{LatestPlan!.Placements.Count(p => !p.Section.Online && (p.Room == null || p.Time == null))} in-person section(s) still need placing. " +
                    "Previous manual room/time moves were replaced by this rebuild.\n" +
                    FileUpdateSummary(), "What needs attention?", "Which files are active?");
            }
            catch (Exception ex)
            { Conversation.SessionReply(text, "The rebuild did not complete: " + ex.Message + " The previous schedule has not been replaced. Review or correct the files before trying again.", "Review file updates"); }
            return;
        }
        if (_pendingFileConfirmation != null && Regex.IsMatch(text, @"^(?:cancel|cancel updates|never mind|no)[.!?]?$", ignore))
        { _pendingFileConfirmation = null; Conversation.SessionReply(text, "Canceled the rebuild. Uploaded files remain saved; the schedule has not changed."); return; }
        bool fileQuestion = Regex.IsMatch(text, @"\b(?:files?|uploads?|uploaded|imports?)\b|\b(?:updated|new|latest) data\b", ignore);
        bool review = Regex.IsMatch(text, @"\b(?:review|use|apply|rebuild|update)\b", ignore) && fileQuestion;
        if (fileQuestion)
        {
            _pendingBuildingPriority = null;
            var next = RecommendedFiles();
            if (review && next.Source.Length > 0)
            {
                _pendingFileConfirmation = FileConfirmationFingerprint();
                int manual = LatestPlan?.Placements.Concat(LatestPlan.Discussions).Count(p => p.ManualOverride) ?? 0;
                Conversation.SessionReply(text, FileUpdateSummary() +
                    $"\nConfirm rebuilding with {next.Source}" + (next.Preferences != null ? $" and {next.Preferences}" : "") +
                    $"? This replaces the current schedule and its {manual} manual room/time assignment(s). Nothing has been rebuilt yet.",
                    "Confirm rebuild with newest files", "Cancel updates");
            }
            else Conversation.SessionReply(text, FileUpdateSummary(), next.Source.Length > 0 ? new[] { "Review file updates" } : Array.Empty<string>());
            return;
        }
        _pendingFileConfirmation = null;
        _pendingBuildingPriority = null;
        if (RequiresRebuild)
        { Conversation.SessionReply(text, "Updated files or settings are waiting to be applied. I can confirm which files are loaded; review file updates and rebuild before requesting schedule changes.", "Which files are active?", "Review file updates"); return; }
        Conversation.Send(LatestPlan, Rooms, ProposeOverride, text, allowOversized, ProposeMove, ProposeSwap);
    }
}
