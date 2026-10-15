using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RoomScheduler
{
    /// <summary>
    /// plan &lt;requests.xlsx&gt; [--history past.xlsx] [--out schedule.xlsx] [--compare actual.xlsx]
    ///
    /// Builds a schedule from a department workbook. With --compare it scores the
    /// result against what really ran, which is how the planner is tested: give it
    /// the Fall 2025 requests and compare with the Fall 2025 actual schedule.
    /// </summary>
    public static class PlanCommand
    {
        private static bool Verbose;

        public static void Run(string[] args)
        {
            Verbose = args.Contains("--verbose");
            if (args.Length == 0)
            {
                Console.WriteLine("usage: plan <requests.xlsx> [--history past.xlsx] [--out schedule.xlsx] [--compare actual.xlsx] [--flags]");
                return;
            }

            string? Opt(string name)
            {
                int i = Array.IndexOf(args, name);
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            }

            var rules = new SchedulingRules();
            var rooms = Room.LoadWithFeatures("data/gpc_rooms_features.csv", "data/gdc_rooms.csv", rules);
            Room.MarkLectureCapture(rooms, "data/lecture_capture_rooms.csv");
            var campus = Campus.Load("data/buildings.csv");
            var past = Opt("--history") is string h ? PastSchedule.FromWorkbook(Xlsx.Read(h)) : PastSchedule.Empty;

            var workbook = DepartmentWorkbook.FromSheets(Xlsx.Read(args[0]));
            var deptHistory = CourseHistory.LoadFromCsv("data/course_history.csv");
            var plan = new Planner(rooms, campus, past, FacultyStore.InMemory(), rules, deptHistory).Build(workbook);

            Console.WriteLine($"{rooms.Count} rooms, {past.Sections.Count} past sections ({past.Label})");
            Console.WriteLine($"{workbook.Sections.Count} sections, {workbook.Forms.Count} forms, {workbook.UnmatchedForms.Count} unmatched forms");
            foreach (var g in plan.Placements.GroupBy(p => p.Status))
                Console.WriteLine($"  {g.Key,-14} {g.Count()}");

            var flags = plan.AllFlags.Select(x => x.Flag).Concat(plan.GeneralFlags).ToList();
            foreach (var g in flags.GroupBy(f => (f.Level, f.Category)).OrderByDescending(g => g.Key.Level))
                Console.WriteLine($"  {g.Key.Level,-8} {g.Key.Category,-20} {g.Count()}");

            if (args.Contains("--flags"))
            {
                foreach (var f in plan.GeneralFlags)
                    Console.WriteLine($"[{f.Level}] {f.Category}: {f.Message}");
                foreach (var (p, f) in plan.AllFlags.Where(x => x.Flag.Level != FlagLevel.Info))
                    Console.WriteLine($"[{f.Level}] {p.Section.Course} {p.Section.Surname}: {f.Message}");
            }

            if (args.Contains("--list"))
            {
                foreach (var p in plan.Placements.OrderBy(p => p.Section.Course))
                    Console.WriteLine($"{p.Section.Course,-6} {p.Section.Surname,-14} {p.Section.PriorityLabel,-4} " +
                                      $"{p.Time?.ToString() ?? "-",-24} {p.Room?.DisplayName ?? "-",-10} " +
                                      $"{p.Section.SizeForRoom,4} {p.Fit,-8} {string.Join("; ", p.Why)}" +
                                      (p.Discussion != null ? $"\n{"",30}discussion {p.Discussion.Time} {p.Discussion.Room?.DisplayName} ({string.Join("; ", p.Discussion.Why)})" : ""));
            }

            if (Opt("--compare") is string actualPath)
                Compare(plan, PastSchedule.FromWorkbook(Xlsx.Read(actualPath)));

            if (Opt("--out") is string outPath)
            {
                File.WriteAllBytes(outPath, PlanExport.Workbook(plan, rooms));
                Console.WriteLine($"wrote {outPath}");
            }

            CheckForClashes(plan);
        }

        /// <summary>How close the plan came to what the department actually did.</summary>
        private static void Compare(Planner.Plan plan, PastSchedule actual)
        {
            int compared = 0, sameTime = 0, sameBuilding = 0, sameRoom = 0;
            var used = new HashSet<PastSection>();

            foreach (var p in plan.Placements.Where(p => p.Time != null))
            {
                var real = actual.For(p.Section.Surname, p.Section.Course)
                    .Where(a => a.Time != null && !used.Contains(a))
                    .OrderByDescending(a => a.Time!.Equals(p.Time))
                    .ThenByDescending(a => a.RoomName == p.Room?.DisplayName)
                    .FirstOrDefault();
                if (real == null) continue;

                used.Add(real);
                compared++;
                if (real.Time!.Equals(p.Time)) sameTime++;
                if (p.Room != null && real.Building == p.Room.Building) sameBuilding++;
                if (p.Room != null && real.RoomName == p.Room.DisplayName) sameRoom++;
                if (Verbose && !(real.Time!.Equals(p.Time) && real.Building == p.Room?.Building))
                    Console.WriteLine($"  diff {p.Section.Course,-6} {p.Section.Surname,-12} plan {p.Time} {p.Room?.DisplayName,-10} actual {real.Time} {real.RoomName,-10} | {string.Join("; ", p.Why)}");
            }

            Console.WriteLine($"compared with actual: {compared} sections");
            Console.WriteLine($"  same time      {sameTime} ({Pct(sameTime, compared)})");
            Console.WriteLine($"  same building  {sameBuilding} ({Pct(sameBuilding, compared)})");
            Console.WriteLine($"  same room      {sameRoom} ({Pct(sameRoom, compared)})");
        }

        /// <summary>Reads the plan back and fails loudly on any double booking.</summary>
        private static void CheckForClashes(Planner.Plan plan)
        {
            var placed = plan.Placements.Concat(plan.Discussions).Where(p => p.Time != null).ToList();
            int clashes = 0;
            for (int i = 0; i < placed.Count; i++)
                for (int j = i + 1; j < placed.Count; j++)
                {
                    var a = placed[i];
                    var b = placed[j];
                    if (!a.Time!.Overlaps(b.Time!)) continue;
                    if (a.Room != null && a.Room == b.Room)
                    {
                        clashes++;
                        Console.WriteLine($"ROOM CLASH {a.Room.DisplayName}: {a.Section.Title} / {b.Section.Title}");
                    }
                    if (!a.Section.InstructorMissing && !a.IsDiscussion && !b.IsDiscussion && a.Section.Surname == b.Section.Surname)
                    {
                        clashes++;
                        Console.WriteLine($"INSTRUCTOR CLASH {a.Section.Surname}: {a.Section.Title} / {b.Section.Title}");
                    }
                }

            var lunch = plan.Rules.TenureLunch;
            foreach (var d in placed.Where(p => p.IsDiscussion && p.Time!.Overlaps(p.LectureOf!.Time!)))
            {
                clashes++;
                Console.WriteLine($"DISCUSSION OVERLAPS LECTURE: {d.Section.Title}");
            }

            foreach (var p in placed.Where(p => !p.IsDiscussion && p.Section.Track == FacultyTrack.Tenure && p.Time!.Overlaps(lunch)))
            {
                clashes++;
                Console.WriteLine($"LUNCH RULE BROKEN: {p.Section.Title} {p.Time}");
            }

            Console.WriteLine(clashes == 0 ? "no clashes" : $"{clashes} CLASHES");
        }

        private static string Pct(int a, int b) => b == 0 ? "-" : $"{100.0 * a / b:0}%";
    }
}
