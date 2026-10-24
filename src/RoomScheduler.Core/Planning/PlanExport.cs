using System;
using System.Collections.Generic;
using System.Linq;

namespace RoomScheduler
{
    /// <summary>Turns a plan into the spreadsheets the department works from.</summary>
    public static class PlanExport
    {
        // Half-hour rows like the department's Gates template, which starts at 9:00am.
        // This starts at 8:00 because some classes (371N) meet at 8.
        private static readonly TimeSpan GridStart = new(8, 0, 0);
        private static readonly TimeSpan GridEnd = new(21, 0, 0);

        private static readonly string[] Palette =
        {
            "FCE4D6", "DDEBF7", "E2EFDA", "FFF2CC", "EDE1F5", "D9F2F2", "F8D7DA", "E7E6E6",
            "FFE699", "C6E0B4", "BDD7EE", "F4B183"
        };

        private static readonly DayOfWeek[] Weekdays =
            { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };

        public static byte[] Workbook(Planner.Plan plan, IEnumerable<Room> rooms)
        {
            var sheets = new List<Xlsx.OutSheet>
            {
                ClassesSheet(plan),
                ReviewSheet(plan),
                RegistrarSheet(plan),
                FinalsSheet(plan)
            };
            sheets.AddRange(RoomGrids(plan, rooms));
            return Xlsx.Write(sheets);
        }

        public static byte[] GridsOnly(Planner.Plan plan, IEnumerable<Room> rooms) =>
            Xlsx.Write(RoomGrids(plan, rooms));

        /// <summary>
        /// The department's own Classes App sheet with blank fields filled in:
        /// days, time, building and room from the schedule, tenure/PTF from the
        /// profile, D for prioritized instructors, and whether a discussion was asked for.
        /// Extra columns on the right carry the registrar cap, fit and flags.
        /// </summary>
        public static Xlsx.OutSheet ClassesSheet(Planner.Plan plan)
        {
            var wb = plan.Workbook;
            var sheet = new Xlsx.OutSheet { Name = Fallback(wb.ClassesSheetName, "Classes") + " (filled)" };
            var headers = wb.ClassesHeaders.ToList();
            var extra = new[] { "Enrollment cap for registrar", "Expected size", "Reported over expected", "Room fit",
                                "Time choice used", "Flags to review" };
            if (plan.Rules.ScheduleDiscussions)
                extra = extra.Take(4).Append("Discussion planned").Concat(extra.Skip(4)).ToArray();

            int Col(params string[] names) =>
                headers.FindIndex(h => names.Any(n => h.Equals(n, StringComparison.OrdinalIgnoreCase)));

            int days = Col("Days"), time = Col("Time"), bldg = Col("Bldg"), room = Col("Room"),
                track = Col("Tenure or PTF"), prio = Col("Prioritized Professor", "Prioritized Instructor"),
                disc = Col("Discussion");

            sheet.AddRow(headers.Concat(extra), bold: true);
            var byRow = plan.Placements.ToDictionary(p => p.Section.SourceRow);

            for (int r = wb.ClassesHeaderRow + 1; r < wb.ClassesRows.Count; r++)
            {
                if (!byRow.TryGetValue(r, out var p)) continue;
                var s = p.Section;
                var cells = wb.ClassesRows[r].Concat(Enumerable.Repeat("", Math.Max(0, headers.Count - wb.ClassesRows[r].Count)))
                    .Take(headers.Count)
                    .Select(v => (Xlsx.OutCell?)new Xlsx.OutCell(v))
                    .ToList();

                void Fill(int c, string value, bool replace = false)
                {
                    if (c < 0 || value.Length == 0 || (!replace && cells[c]!.Text.Trim().Length > 0)) return;
                    cells[c] = new Xlsx.OutCell(value, "FFF2CC");
                }

                if (p.Time != null)
                {
                    Fill(days, p.Time.Pattern, p.ManualOverride);
                    Fill(time, p.Time.Clock, p.ManualOverride);
                }
                if (s.Online) Fill(days, "WEB");
                if (p.Room != null)
                {
                    if (bldg >= 0)
                    {
                        Fill(bldg, p.Room.Building, p.ManualOverride);
                        Fill(room, p.Room.RoomNumber, p.ManualOverride);
                    }
                    else Fill(room, p.Room.DisplayName, p.ManualOverride);
                }

                Fill(track, s.Track switch { FacultyTrack.Tenure => "t", FacultyTrack.Professional => "ptf", _ => "" });
                if (s.Difficult) Fill(prio, "D");
                if (s.Form != null) Fill(disc, s.Wishes.WantsDiscussion ? "Yes" : "No");

                var flags = s.Flags.Where(f => f.Level != FlagLevel.Info).Select(f => f.Message).ToList();
                cells.Add(new Xlsx.OutCell(p.RegistrarCap?.ToString() ?? ""));
                cells.Add(new Xlsx.OutCell(s.SizeForRoom.ToString()));
                cells.Add(new Xlsx.OutCell(p.Room == null ? "" : $"{plan.Rules.OverReported(p.Room.Capacity, s.SizeForRoom):+0%;-0%;0%}"));
                cells.Add(new Xlsx.OutCell(p.Fit, p.Fit == "Max high" ? "F8CBAD" : p.Fit == "Min high" ? "FFE699" : null));
                if (plan.Rules.ScheduleDiscussions)
                    cells.Add(new Xlsx.OutCell(p.Discussion == null ? "" : $"{p.Discussion.Time} {p.Discussion.Room?.DisplayName}".Trim()));
                cells.Add(new Xlsx.OutCell(p.Option == null ? "" : $"{p.Option.Rank} ({p.Option.Source})"));
                cells.Add(new Xlsx.OutCell(string.Join("\n", flags), flags.Count > 0 ? "FCE4D6" : null));

                sheet.Rows.Add(cells);
            }

            sheet.ColumnWidths.AddRange(headers.Select(h => h == "Title" ? 42.0 : 14.0).Concat(new[] { 14.0, 10, 12, 10, 28, 16, 70 }));
            return sheet;
        }

        public static Xlsx.OutSheet ReviewSheet(Planner.Plan plan)
        {
            var sheet = new Xlsx.OutSheet { Name = "Review queue" };
            sheet.AddRow(new[] { "Level", "Category", "Course", "Instructor", "Message" }, bold: true);

            foreach (var f in plan.GeneralFlags)
                sheet.AddRow(new[] { f.Level.ToString(), f.Category, "", "", f.Message });

            foreach (var (p, f) in plan.AllFlags.OrderByDescending(x => x.Flag.Level).ThenBy(x => x.Flag.Category))
                sheet.Rows.Add(new List<Xlsx.OutCell?>
                {
                    new(f.Level.ToString(), f.Level == FlagLevel.Problem ? "F8CBAD" : f.Level == FlagLevel.Review ? "FFE699" : null),
                    new(f.Category), new(p.Section.Title), new(p.Section.Instructor), new(f.Message)
                });

            sheet.ColumnWidths.AddRange(new[] { 9.0, 18, 40, 18, 100 });
            return sheet;
        }

        /// <summary>
        /// What goes to the registrar: room, capacity, and the size to report
        /// (capacity minus 10%), which may run 50-75% over the students expected.
        /// Discussion sections follow their lecture.
        /// </summary>
        public static Xlsx.OutSheet RegistrarSheet(Planner.Plan plan)
        {
            var sheet = new Xlsx.OutSheet { Name = "Registrar" };
            sheet.AddRow(new[] { "Course", "Instructor", "Days", "Time", "Building", "Room", "Room capacity",
                                 "Size to report", "Expected", "Last time", "Reported over expected", "Fit" }, bold: true);

            foreach (var p in Ordered(plan))
            {
                var s = p.Section;
                sheet.Rows.Add(new List<Xlsx.OutCell?>
                {
                    new(s.Title), new(s.Instructor), new(p.Time?.Pattern ?? (s.Online ? "WEB" : "")), new(p.Time?.Clock ?? ""),
                    new(p.Room?.Building ?? ""), new(p.Room?.RoomNumber ?? ""), new(p.Room?.Capacity.ToString() ?? ""),
                    new(p.RegistrarCap?.ToString() ?? ""), new(s.SizeForRoom.ToString()),
                    new(s.PastEnrollment?.ToString() ?? ""),
                    new(p.Room == null ? "" : $"{plan.Rules.OverReported(p.Room.Capacity, s.SizeForRoom):+0%;-0%;0%}"),
                    new(p.Fit, p.Fit == "Max high" ? "F8CBAD" : p.Fit == "Min high" ? "FFE699" : null)
                });

                if (p.Discussion is { } d)
                    sheet.AddRow(new[]
                    {
                        $"{s.Title} (discussion)", "TA", d.Time?.Pattern ?? "", d.Time?.Clock ?? "",
                        d.Room?.Building ?? "", d.Room?.RoomNumber ?? "", d.Room?.Capacity.ToString() ?? "",
                        "", plan.Rules.DiscussionSize.ToString(), "", "", ""
                    });
            }

            sheet.ColumnWidths.AddRange(new[] { 42.0, 18, 8, 16, 9, 8, 10, 10, 10, 10, 12, 10 });
            return sheet;
        }

        /// <summary>
        /// Final exam planning: proctors and TAs needed per section, with the
        /// students expected. Exam times and rooms are TBD until the department
        /// says how they are set.
        /// </summary>
        public static Xlsx.OutSheet FinalsSheet(Planner.Plan plan)
        {
            var sheet = new Xlsx.OutSheet { Name = "Finals and proctors" };
            sheet.AddRow(new[] { "Course", "Instructor", "Meets", "Room", "Expected students", "Proctors needed",
                                 "TAs needed", "Exam time", "Exam room" }, bold: true);

            foreach (var p in Ordered(plan).Where(p => !p.Section.Online))
            {
                var s = p.Section;
                sheet.AddRow(new[]
                {
                    s.Title, s.Instructor, p.Time?.ToString() ?? "", p.Room?.DisplayName ?? "",
                    s.SizeForRoom.ToString(), s.ProctorsNeeded.ToString(), s.TAsNeeded.ToString(),
                    "TBD", "TBD"
                });
            }

            int proctors = plan.Placements.Where(p => !p.Section.Online).Sum(p => p.Section.ProctorsNeeded);
            sheet.AddRow(new[] { "Total", "", "", "", "", proctors.ToString(),
                                 plan.Placements.Sum(p => p.Section.TAsNeeded).ToString() }, bold: true);
            sheet.ColumnWidths.AddRange(new[] { 42.0, 18, 22, 12, 10, 10, 10, 28, 12 });
            return sheet;
        }

        public const string UsedMark = "\u25CF ";
        private const string UsedTab = "70AD47";

        /// <summary>
        /// One weekly calendar per room in the department's Gates template layout:
        /// half-hour rows, weekday columns, each class written into the cells it
        /// occupies. Every room the schedule uses gets one, plus every GDC room
        /// from the template. Used rooms come first, marked with a dot and a
        /// green tab so they stand out from the empty ones. An index sheet lists them all.
        /// </summary>
        public static List<Xlsx.OutSheet> RoomGrids(Planner.Plan plan, IEnumerable<Room> rooms)
        {
            var colors = new Dictionary<string, string>();
            var roomList = rooms.ToList();
            var used = roomList.Where(r => plan.InRoom(r).Count > 0).ToHashSet();

            var ordered = roomList
                .Where(r => used.Contains(r) || r.Building == "GDC")
                .OrderBy(r => !used.Contains(r))
                .ThenBy(r => r.Building != "GDC")
                .ThenBy(r => r.Building)
                .ThenBy(r => r.RoomNumber)
                .ToList();

            var index = new Xlsx.OutSheet { Name = "Rooms index" };
            index.AddRow(new[] { "Room", "Capacity", "In use", "Classes", "Hours per week" }, bold: true);
            foreach (var r in ordered)
            {
                var here = plan.InRoom(r);
                double hours = here.Sum(p => p.Time!.Duration.TotalHours * p.Time.Days.Count);
                bool isUsed = used.Contains(r);
                index.Rows.Add(new List<Xlsx.OutCell?>
                {
                    new((isUsed ? UsedMark : "") + r.DisplayName, isUsed ? "E2EFDA" : null, isUsed),
                    new(r.Capacity.ToString()), new(isUsed ? "yes" : "empty"),
                    new(here.Count.ToString()), new(hours.ToString("0.#"))
                });
            }
            index.ColumnWidths.AddRange(new[] { 16.0, 10, 8, 8, 14 });

            var result = new List<Xlsx.OutSheet> { index };
            foreach (var room in ordered)
                result.Add(RoomGrid(plan, room, colors, used.Contains(room)));
            return result;
        }

        public static Xlsx.OutSheet RoomGrid(Planner.Plan plan, Room room, Dictionary<string, string> colors, bool used = true)
        {
            var sheet = new Xlsx.OutSheet
            {
                Name = (used ? UsedMark : "") + room.DisplayName,
                TabColor = used ? UsedTab : null
            };
            sheet.Set(0, 0, new Xlsx.OutCell((used ? UsedMark : "") + room.DisplayName, used ? "E2EFDA" : null, true));
            sheet.Set(0, 2, new Xlsx.OutCell($"capacity {room.Capacity}  {room.Notes}{(used ? "" : "  (empty this semester)")}".Trim()));
            sheet.Set(1, 0, new Xlsx.OutCell("", null, true));
            for (int d = 0; d < Weekdays.Length; d++)
                sheet.Set(1, d + 1, new Xlsx.OutCell(Weekdays[d].ToString(), null, true));

            int row = 2;
            for (var t = GridStart; t < GridEnd; t += TimeSpan.FromMinutes(30), row++)
            {
                sheet.Set(row, 0, new Xlsx.OutCell(MeetingTime.ClockTime(t)));
                var slot = t;
                for (int d = 0; d < Weekdays.Length; d++)
                {
                    var here = plan.InRoom(room).FirstOrDefault(p =>
                        p.Time!.Days.Contains(Weekdays[d]) && p.Time.Start <= slot && slot < p.Time.End);
                    if (here == null) continue;

                    string key = here.Label;
                    if (!colors.TryGetValue(key, out var color))
                        colors[key] = color = Palette[colors.Count % Palette.Length];

                    bool first = here.Time!.Start > slot - TimeSpan.FromMinutes(30);
                    string who = here.IsDiscussion ? "TA" : here.Section.Instructor;
                    string text = first ? $"CS {here.Label}\n{who}\n{here.Time.Clock}" : "";
                    sheet.Set(row, d + 1, new Xlsx.OutCell(text, color));
                }
            }

            sheet.ColumnWidths.AddRange(new[] { 10.0, 24, 24, 24, 24, 24 });
            return sheet;
        }

        private static IEnumerable<Placement> Ordered(Planner.Plan plan) =>
            plan.Placements.OrderBy(p => p.Section.Course).ThenBy(p => p.Section.Instructor);

        private static string Fallback(string s, string f) => string.IsNullOrWhiteSpace(s) ? f : s;
    }
}
