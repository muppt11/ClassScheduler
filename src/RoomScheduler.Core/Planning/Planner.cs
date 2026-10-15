using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RoomScheduler
{
    /// <summary>
    /// Builds a semester schedule from the department's Classes App sheet, the
    /// professors' preference forms, past semesters and faculty profiles.
    ///
    /// Sections are placed in priority order: difficult faculty first, then tenure
    /// track, then professional track. Each section tries its requested times in
    /// order and takes the best free room at the best time it can get. A clean-up
    /// pass then moves sections into better rooms that turned out to be free.
    /// Anything a person should check is attached to the section as a flag.
    /// </summary>
    public class Planner
    {
        private readonly List<Room> _rooms;
        private readonly Campus _campus;
        private readonly PastSchedule _past;
        private readonly FacultyStore _faculty;
        private readonly CourseHistory _departmentHistory;

        public SchedulingRules Rules { get; }

        // Weights. A time choice outranks any room preference: professors care
        // more about when than where.
        private const int PointsPerRank = 500;
        private const int AskedRoom = 300;
        private const int AskedFloor = 200;
        private const int AskedBuilding = 150;
        private const int Avoided = -1000;
        private const int PastRoom = 100;
        private const int PastBuilding = 40;

        // Buildings the department taught in, from the 2022-2026 printed catalogs
        // (data/course_history.csv): a little per past section, capped.
        private const int DepartmentPointsPerSection = 8;
        private const int DepartmentMaxSections = 10;
        private const int PerMinuteFromHome = 5;
        private const int MissingFeature = 60;
        private const int PerWalkMinute = 20;
        private const int CannotMakeIt = -400;
        private const int BackToBackBonus = 200;
        private const int SameRoomBonus = 200;
        private const int BeforeCsHours = -30;
        private const int MaxHighPenalty = 150;
        private const int MinHighPenalty = 40;
        private const int NonStandardRoom = -800;
        // Worth more than a requested building, less than one step down their time choices.
        private const int LectureCaptureBonus = 350;

        // Minutes between two "back-to-back" classes that a walk can use, since a
        // 10:00-11:00 class really ends at 10:50.
        private const double PassingMinutes = 10;

        public Planner(List<Room> rooms, Campus campus, PastSchedule past, FacultyStore faculty,
                       SchedulingRules? rules = null, CourseHistory? departmentHistory = null)
        {
            _departmentHistory = departmentHistory ?? CourseHistory.Empty;
            _rooms = rooms;
            _campus = campus;
            _past = past;
            _faculty = faculty;
            Rules = rules ?? new SchedulingRules();
        }

        public class Plan
        {
            public string Semester { get; init; } = "";
            public List<Placement> Placements { get; } = new();
            public List<Placement> Discussions { get; } = new();
            public List<ReviewFlag> GeneralFlags { get; } = new();
            public DepartmentWorkbook Workbook { get; init; } = null!;
            public SchedulingRules Rules { get; init; } = null!;

            public IEnumerable<(Placement Placement, ReviewFlag Flag)> AllFlags =>
                Placements.SelectMany(p => p.Section.Flags.Select(f => (p, f)));

            public List<Placement> InRoom(Room room) =>
                Placements.Concat(Discussions).Where(p => p.Room == room).OrderBy(p => p.Time!.Start).ToList();
        }

        public Plan Build(DepartmentWorkbook workbook)
        {
            workbook.MatchForms();
            var plan = new Plan { Semester = workbook.Semester, Workbook = workbook, Rules = Rules };

            foreach (var form in workbook.UnmatchedForms)
                plan.GeneralFlags.Add(new ReviewFlag(FlagLevel.Review, ReviewFlag.NeedsInfo,
                    $"Preference form from {form.Professor} for \"{form.Course}\" matches no section on the Classes App sheet."));

            // Tenure-track faculty teaching a graduate course this semester keep the
            // graduate slot clear for their other classes.
            _graduateTeachers = workbook.Sections
                .Where(x => !x.InstructorMissing && SchedulingRules.IsGraduate(x.Course))
                .Select(x => x.Surname)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var section in workbook.Sections)
            {
                Prepare(section);
                plan.Placements.Add(new Placement { Section = section });
            }

            var order = plan.Placements
                .Where(p => !p.Section.Online && Options(p.Section).Count > 0)
                .GroupBy(p => p.Section.Surname)
                .OrderBy(g => g.Min(p => p.Section.PriorityTier))
                .ThenByDescending(g => g.Max(p => p.Section.SizeForRoom))
                .SelectMany(g => g.OrderBy(p => Options(p.Section).Count).ThenBy(p => p.Section.SourceRow))
                .ToList();

            foreach (var p in order)
                Place(p, plan.Placements);

            Improve(order, plan.Placements);
            if (Rules.ScheduleDiscussions)
                PlaceDiscussions(plan);

            foreach (var p in plan.Placements)
                Finish(p);

            return plan;
        }

        // ------------------------------------------------------------ preparation

        private readonly Dictionary<SectionRequest, List<TimeOption>> _options = new();
        private HashSet<string> _graduateTeachers = new();

        private List<TimeOption> Options(SectionRequest s) =>
            _options.TryGetValue(s, out var o) ? o : new List<TimeOption>();

        private void Prepare(SectionRequest s)
        {
            if (s.InstructorMissing)
            {
                s.Flag(FlagLevel.Problem, ReviewFlag.NeedsInfo, "No instructor assigned yet, so there are no preferences to work from.");
            }
            else
            {
                s.Profile = _faculty.GetOrAdd(s.Surname, s.Instructor);
                if (s.Profile.Track != FacultyTrack.Unknown) s.Track = s.Profile.Track;
                else if (s.Track != FacultyTrack.Unknown) s.Profile.Track = s.Track;
                s.Difficult |= s.Profile.Difficult;

                if (s.Track == FacultyTrack.Unknown)
                    s.Flag(FlagLevel.Review, ReviewFlag.NeedsInfo, "Tenure or PTF is blank, so this section has the lowest priority.");

                if (SchedulingRules.IsGraduate(s.Course) && s.Track == FacultyTrack.Professional)
                    s.Flag(FlagLevel.Review, ReviewFlag.Policy,
                        $"{s.Course} is a graduate course, but {s.Instructor} is professional track. Only tenure-track faculty teach graduate courses.");
            }

            if (!Rules.ScheduleDiscussions && s.Form != null && s.Wishes.WantsDiscussion)
                s.Flag(FlagLevel.Info, ReviewFlag.SpecialRequest, "Discussion section requested; discussions are scheduled in phase two.");

            if (s.Form != null)
                s.Wishes = PreferenceParser.Interpret(s.Form, _campus, _rooms);
            s.Flags.AddRange(s.Wishes.Flags);

            s.PastSections = s.InstructorMissing ? new() : _past.For(s.Surname, s.Course);

            SizeFromHistory(s);
            if (s.Online)
            {
                s.Flag(FlagLevel.Info, ReviewFlag.Room, "Web-based section, so no classroom.");
                _options[s] = new();
                return;
            }

            _options[s] = BuildOptions(s);
            CheckClaims(s);

            var offList = _rooms
                .Where(r => Rules.Offline.IsOffline(r) &&
                            (s.PastSections.Any(p => p.RoomName.Equals(r.DisplayName, StringComparison.OrdinalIgnoreCase)) ||
                             s.Wishes.Rooms.Contains(r.DisplayName, StringComparer.OrdinalIgnoreCase) ||
                             s.Wishes.Buildings.Contains(r.Building, StringComparer.OrdinalIgnoreCase)))
                .Select(r => Rules.Offline.BuildingOffline(r.Building) ? r.Building : r.DisplayName)
                .Distinct().ToList();
            if (offList.Count > 0)
                s.Flag(FlagLevel.Info, ReviewFlag.Room,
                    $"Taught in or asked for {string.Join(", ", offList)}, which is offline for now, so it was placed elsewhere.");

            if (s.Profile?.HasAccommodations == true)
                s.Flag(FlagLevel.Info, ReviewFlag.Accommodation,
                    "Has accommodations on file; only rooms that meet them are used." +
                    (s.Profile.AccommodationNotes.Length > 0 ? $" ({s.Profile.AccommodationNotes})" : ""));

            if (s.Profile?.Complaints.Count > 0)
                s.Flag(FlagLevel.Info, ReviewFlag.SpecialRequest,
                    $"{s.Profile.Complaints.Count} complaint(s) on file. Latest: \"{s.Profile.Complaints.Last().Text}\"");
        }

        /// <summary>
        /// Compares the projected size with what really enrolled last time. A class
        /// that drew far fewer students than projected is downsized by 15% and
        /// flagged for review. One that drew more is sized up.
        /// </summary>
        private void SizeFromHistory(SectionRequest s)
        {
            var inPerson = s.PastSections.Where(p => !p.Online && p.Enrollment > 0).ToList();
            string source = "their sections";
            if (inPerson.Count == 0)
            {
                inPerson = _past.ForCourse(s.Course).Where(p => !p.Online && p.Enrollment > 0 &&
                                                                 TitlesMatch(p.Title, s.Title)).ToList();
                source = "all sections";
            }
            if (inPerson.Count == 0) return;

            int actual = (int)Math.Round(inPerson.Average(p => p.Enrollment));
            s.PastEnrollment = actual;
            string when = inPerson[0].Semester;

            if (s.ProjectedSize <= 0)
            {
                s.SizeForRoom = actual;
                s.Flag(FlagLevel.Info, ReviewFlag.Size, $"No projected size given; using {when} enrollment ({actual}).");
            }
            else if (actual < s.ProjectedSize * Rules.ActualFarBelowProjected)
            {
                s.SizeForRoom = (int)Math.Ceiling(s.ProjectedSize * (1 - Rules.DownsizeShare));
                s.Flag(FlagLevel.Review, ReviewFlag.Size,
                    $"Projected {s.ProjectedSize}, but {when} enrolled only {actual} ({source}). " +
                    $"Downsized {Rules.DownsizeShare:0%} to {s.SizeForRoom}; check it.");
            }
            else if (actual > s.ProjectedSize * 1.1)
            {
                s.SizeForRoom = actual;
                s.Flag(FlagLevel.Review, ReviewFlag.Size,
                    $"{when} enrolled {actual}, more than the projected {s.ProjectedSize}. Sized up to {actual}.");
            }
        }

        private static bool TitlesMatch(string pastTitle, string title)
        {
            // Topics courses (378, 395T) share a number, so compare the first title word too.
            var a = pastTitle.ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var b = title.ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
            return a.Length == 0 || b.Count == 0 || a[0].Length < 3 || b.Any(w => w.StartsWith(a[0].Substring(0, 3)));
        }

        private List<TimeOption> BuildOptions(SectionRequest s)
        {
            var options = s.Wishes.Times.ToList();

            if (options.Count == 0 && !s.InstructorMissing)
            {
                var same = s.PastSections.Where(p => p.Time != null).Select(p => p.Time!).Distinct().ToList();
                if (same.Count > 0)
                {
                    options.AddRange(same.Select((t, i) => new TimeOption(t, i + 1, $"same as {s.PastSections[0].Semester}")));
                    s.Flag(FlagLevel.Info, ReviewFlag.History,
                        $"No time preference given; using what they taught last time: {string.Join(", ", same)}.");
                }
                else
                {
                    var any = _past.ForInstructor(s.Surname).Where(p => p.Time != null).ToList();
                    if (any.Count > 0)
                    {
                        options.AddRange(any.Select(p => p.Time!).Distinct()
                            .Select((t, i) => new TimeOption(t, i + 1, $"their {any[0].Semester} time")));
                        s.Flag(FlagLevel.Review, ReviewFlag.History,
                            $"No time preference and no past record for {s.Course}; using the times of their other {any[0].Semester} classes.");
                    }
                    else if (s.Form != null)
                    {
                        s.Flag(FlagLevel.Problem, ReviewFlag.NeedsInfo,
                            "Preference form has no time the app can read, and there is no past schedule to fall back on. Reach out to them.");
                    }
                    else
                    {
                        s.Flag(FlagLevel.Problem, ReviewFlag.NeedsInfo,
                            "No preference form and no past schedule, likely a new instructor. Reach out to them for times.");
                    }
                }
            }

            CheckCreditHours(s, options);

            // Hard rules on time.
            var blocked = new List<(MeetingTime Time, string Why)>();
            if (s.Track == FacultyTrack.Tenure)
                blocked.Add((Rules.TenureLunch, $"tenure-track faculty lunch ({Rules.TenureLunch})"));
            if (s.Track == FacultyTrack.Tenure && !SchedulingRules.IsGraduate(s.Course) && _graduateTeachers.Contains(s.Surname))
                blocked.Add((Rules.GraduateSlot, $"graduate-course time ({Rules.GraduateSlot}), since they teach a graduate course"));
            if (s.Profile != null)
                blocked.AddRange(s.Profile.UnavailableTimes().Select(t => (t, $"unavailable on their profile ({t})")));

            foreach (var opt in options.ToList())
            {
                var hit = blocked.FirstOrDefault(b => b.Time.Overlaps(opt.Time));
                if (hit.Time == null) continue;
                options.Remove(opt);
                s.Flag(FlagLevel.Info, ReviewFlag.Policy, $"Asked for {opt.Time} ({opt.Source}), not given: overlaps the {hit.Why}.");
            }

            if (options.Count == 0 && s.Wishes.Times.Count > 0)
                s.Flag(FlagLevel.Problem, ReviewFlag.Time, "Every requested time is ruled out by policy or the profile.");

            return options;
        }

        /// <summary>
        /// At UT the first digit of a course number is its credit hours, and a
        /// course meets one clock hour a week per credit (MWF 50-minute classes
        /// sit in 60-minute slots): 303E is 3 hours, 429 and 439 are 4. A request
        /// that does not add up is probably a mistake on the form. If some
        /// requested times add up, the ones that do not are dropped.
        /// </summary>
        private void CheckCreditHours(SectionRequest s, List<TimeOption> options)
        {
            if (s.Course.Length == 0 || !char.IsDigit(s.Course[0]) || options.Count == 0) return;

            // Hybrid courses meet in person for only part of their hours.
            if (s.Flags.Any(f => f.Message == "Hybrid course.") ||
                s.PastSections.Any(p => p.Hybrid)) return;
            int credits = s.Course[0] - '0';
            if (credits == 0) return;

            double Hours(MeetingTime t) => t.Duration.TotalHours * t.Days.Count;
            bool Fits(MeetingTime t) => Math.Abs(Hours(t) - credits) < 0.01;

            var wrong = options.Where(o => !Fits(o.Time)).ToList();
            if (wrong.Count == 0) return;

            string list = string.Join(", ", wrong.Select(o => $"{o.Time} ({Hours(o.Time):0.#} h/week)"));
            if (wrong.Count < options.Count)
            {
                options.RemoveAll(o => !Fits(o.Time));
                s.Flag(FlagLevel.Review, ReviewFlag.Contradiction,
                    $"{s.Course} is {credits} credit hours, so it meets {credits} hours a week. Dropped requested times that do not: {list}.");
            }
            else
            {
                var usual = s.PastSections.Select(p => p.Time).Where(t => t != null && Fits(t)).Select(t => t!.ToString()).Distinct().ToList();
                s.Flag(FlagLevel.Review, ReviewFlag.Contradiction,
                    $"{s.Course} is {credits} credit hours, but every requested time gives a different number of hours: {list}." +
                    (usual.Count > 0 ? $" Last time it met {string.Join(", ", usual)}." : " Scheduled as asked; check with them."));
            }
        }

        /// <summary>
        /// Professors often write "same as Fall 2025" or "where I am teaching now".
        /// Those claims are checked against the record, and mismatches flagged.
        /// </summary>
        private void CheckClaims(SectionRequest s)
        {
            foreach (var claim in s.Wishes.HistoryClaims.Distinct())
            {
                if (s.PastSections.Count == 0)
                {
                    s.Flag(FlagLevel.Review, ReviewFlag.Contradiction,
                        $"Says \"{claim}\", but there is no record of them teaching {s.Course} in {_past.Label}.");
                    continue;
                }

                var pastTimes = s.PastSections.Where(p => p.Time != null).Select(p => p.Time!).ToList();
                var pastRooms = s.PastSections.Select(p => p.RoomName).Where(r => r.Length > 0).ToList();
                var asked = s.Wishes.Times.Select(t => t.Time).ToList();

                bool timeOk = pastTimes.Count == 0 || asked.Count == 0 || asked.Any(pastTimes.Contains);
                bool roomOk = s.Wishes.Rooms.Count == 0 || s.Wishes.Rooms.Any(r => pastRooms.Contains(r, StringComparer.OrdinalIgnoreCase));
                string record = string.Join("; ", s.PastSections.Select(p => $"{p.Time} {p.RoomName}".Trim()));

                if (timeOk && roomOk)
                    s.Flag(FlagLevel.Info, ReviewFlag.History, $"Checked \"{claim}\": matches {s.PastSections[0].Semester} ({record}).");
                else
                    s.Flag(FlagLevel.Review, ReviewFlag.Contradiction,
                        $"Says \"{claim}\", but {s.PastSections[0].Semester} was {record}. " +
                        $"Request: {string.Join(", ", asked.Take(2))} {string.Join(", ", s.Wishes.Rooms)}".TrimEnd());
            }

            // Discussion rooms they say were used before.
            string disc = s.Form?.DiscussionRequirements ?? "";
            foreach (var room in _rooms.Where(r => disc.Contains(r.DisplayName, StringComparison.OrdinalIgnoreCase)))
            {
                bool used = s.PastSections.Any(p => $"{p.DiscussionBuilding} {p.DiscussionRoom}".Equals(room.DisplayName, StringComparison.OrdinalIgnoreCase));
                if (!used && s.PastSections.Count > 0)
                    s.Flag(FlagLevel.Review, ReviewFlag.Contradiction,
                        $"Mentions discussion room {room.DisplayName} from before, but the record shows " +
                        $"{string.Join(", ", s.PastSections.Select(p => $"{p.DiscussionBuilding} {p.DiscussionRoom}".Trim()).Where(x => x.Length > 0).DefaultIfEmpty("none"))}.");
            }
        }

        // ------------------------------------------------------------ placing

        private void Place(Placement p, List<Placement> all)
        {
            var best = BestChoice(p, all, out bool anyTimeFree);
            if (best != null)
            {
                p.Option = best.Value.Option;
                p.Time = best.Value.Option.Time;
                p.Room = best.Value.Room;
                p.Why.Clear();
                p.Why.Add(TimeReason(best.Value.Option));
                p.Why.AddRange(best.Value.Reasons);
                return;
            }

            var opts = Options(p.Section);
            var free = opts.FirstOrDefault(o => !InstructorBusy(p, o.Time, all));
            if (free != null)
            {
                p.Option = free;
                p.Time = free.Time;
                p.Why.Add(TimeReason(free));
                p.Section.Flag(FlagLevel.Problem, ReviewFlag.Room,
                    $"No free room for {p.Section.SizeForRoom} at any requested time. Time is held as {free.Time}; find a room by hand.");
            }
            else if (!anyTimeFree)
            {
                p.Section.Flag(FlagLevel.Problem, ReviewFlag.Time,
                    "Every requested time overlaps another class this instructor is already teaching.");
            }
        }

        private static string TimeReason(TimeOption o) =>
            o.Source.StartsWith("choice") || o.Source == "notes"
                ? $"time: {Ordinal(o.Rank)} preference ({o.Source})"
                : $"time: {o.Source}";

        private (TimeOption Option, Room Room, int Score, List<string> Reasons)? BestChoice(
            Placement p, List<Placement> all, out bool anyTimeFree)
        {
            anyTimeFree = false;
            (TimeOption, Room, int, List<string>)? best = null;

            foreach (var opt in Options(p.Section))
            {
                if (InstructorBusy(p, opt.Time, all)) continue;
                anyTimeFree = true;

                foreach (var room in _rooms)
                {
                    if (!RoomAllowed(p.Section, room)) continue;
                    if (RoomBusy(p, room, opt.Time, all)) continue;

                    var reasons = new List<string>();
                    int? score = ScoreRoom(p, opt.Time, room, all, reasons);
                    if (score == null) continue;

                    int total = score.Value - PointsPerRank * (opt.Rank - 1);
                    if (best == null || total > best.Value.Item3 ||
                        (total == best.Value.Item3 && room.Capacity < best.Value.Item2.Capacity))
                        best = (opt, room, total, reasons);
                }
            }

            return best;
        }

        private static bool InstructorBusy(Placement p, MeetingTime t, List<Placement> all) =>
            !p.Section.InstructorMissing &&
            all.Any(o => o != p && o.Time != null && o.Section.Surname == p.Section.Surname && o.Time.Overlaps(t));

        private static bool RoomBusy(Placement p, Room room, MeetingTime t, List<Placement> all) =>
            all.Any(o => o != p && o.Room == room && o.Time != null && o.Time.Overlaps(t));

        /// <summary>Hard rules on rooms: size, department-only labs, and accommodations.</summary>
        private bool RoomAllowed(SectionRequest s, Room room)
        {
            if (room.Capacity < s.SizeForRoom) return false;
            if (Rules.Offline.IsOffline(room)) return false;

            // Pod rooms and project labs in GDC are not lecture rooms unless asked for by name.
            if (room.DepartmentOnly &&
                (room.Notes.Contains("pod", StringComparison.OrdinalIgnoreCase) ||
                 room.Notes.Contains("project lab", StringComparison.OrdinalIgnoreCase)) &&
                !s.Wishes.Rooms.Contains(room.DisplayName, StringComparer.OrdinalIgnoreCase))
                return false;

            var prof = s.Profile;
            if (prof != null)
            {
                if (prof.GroundFloorOnly && room.Floor > 1) return false;
                var need = prof.RequiredFeatureSet();
                if (need.Count > 0 && !need.All(room.Features.Contains)) return false;
            }

            return true;
        }

        /// <summary>Higher is better. Null means the room breaks an accommodation for this time.</summary>
        private int? ScoreRoom(Placement p, MeetingTime time, Room room, List<Placement> all, List<string> reasons)
        {
            var s = p.Section;
            var w = s.Wishes;
            int score = 0;

            if (w.Rooms.Contains(room.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                score += AskedRoom;
                reasons.Add("asked for this room");
            }
            else if (w.FloorPrefixes.Any(f => room.DisplayName.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
            {
                score += AskedFloor;
                reasons.Add("on the floor they asked for");
            }

            if (w.Buildings.Contains(room.Building, StringComparer.OrdinalIgnoreCase))
            {
                score += AskedBuilding;
                if (!reasons.Contains("asked for this room")) reasons.Add($"asked for {room.Building}");
            }

            if (w.AvoidBuildings.Contains(room.Building, StringComparer.OrdinalIgnoreCase) ||
                w.AvoidRoomPrefixes.Any(a => room.DisplayName.StartsWith(a, StringComparison.OrdinalIgnoreCase)))
            {
                score += Avoided;
                reasons.Add("WARNING: they asked to avoid this");
            }

            if (s.PastSections.Any(ps => ps.RoomName.Equals(room.DisplayName, StringComparison.OrdinalIgnoreCase)))
            {
                score += PastRoom;
                reasons.Add($"taught here in {s.PastSections[0].Semester}");
            }
            else if (s.PastSections.Any(ps => ps.Building.Equals(room.Building, StringComparison.OrdinalIgnoreCase)))
            {
                score += PastBuilding;
            }

            string dept = s.Course.Contains(' ') ? s.Course.Split(' ')[0] : Rules.Department;
            int deptSections = _departmentHistory.BuildingUsage(dept, room.Building);
            if (deptSections > 0)
            {
                score += Math.Min(deptSections, DepartmentMaxSections) * DepartmentPointsPerSection;
                reasons.Add($"{dept} has used {room.Building} {deptSections}x");
            }

            {
                double walk = _campus.WalkMinutes(Rules.HomeBuilding, room.Building) ?? 10;
                score -= w.NearGdc ? WalkCost(walk) : (int)(walk * PerMinuteFromHome);
                if (w.NearGdc && walk > 0 && walk <= 4 && room.Building != Rules.HomeBuilding)
                    reasons.Add($"{walk:0} min from {Rules.HomeBuilding}");
            }

            // Doug: nearly every room has a media console (CON), so it is not counted.
            // A glass board (Gb) does the job of a whiteboard (Wb).
            int missing = w.Features.Count(f => f != "CON" && !room.Features.Contains(f) &&
                                                !(f == "Wb" && room.Features.Contains("Gb")));
            if (missing > 0 && room.Features.Count > 0)
            {
                score -= missing * MissingFeature;
                reasons.Add($"missing {missing} requested feature(s)");
            }

            // Keep class size and room capacity close (Doug). A registrar cap
            // (capacity minus 10%) 50-75% over the class is "Min high", past 75%
            // "Max high"; both cost, and both are flagged once placed.
            double target = room.Capacity * (1 - Rules.RegistrarHeadroom);
            double over = target - s.SizeForRoom;
            score -= (int)(over >= 0 ? over : -over * 3);
            string fit = Rules.FitIndicator(room.Capacity, s.SizeForRoom);
            if (fit == "Max high") score -= MaxHighPenalty;
            else if (fit == "Min high") score -= MinHighPenalty;

            if (room.NonStandard)
            {
                score += NonStandardRoom;
                reasons.Add("WARNING: non-standard classroom (NSC)");
            }

            if (w.Recording && room.LectureCapture)
            {
                score += LectureCaptureBonus;
                reasons.Add("supports Lectures Online (recording)");
            }

            if (room.CsOwnsFrom != null && time.Start < room.CsOwnsFrom.Value)
            {
                score += BeforeCsHours;
                reasons.Add("before CS's 12:30 hours, registrar must agree");
            }

            // Walking between a professor's back-to-back classes.
            foreach (var o in all)
            {
                if (o == p || o.Room == null || o.Time == null || s.InstructorMissing) continue;
                if (o.Section.Surname != s.Surname || !o.Time.SharesDayWith(time)) continue;

                double gap = o.Time.End <= time.Start ? (time.Start - o.Time.End).TotalMinutes
                           : time.End <= o.Time.Start ? (o.Time.Start - time.End).TotalMinutes
                           : -1;
                if (gap < 0 || gap > Rules.BackToBackGap.TotalMinutes) continue;

                double walk = _campus.WalkMinutes(o.Room.Building, room.Building) ?? 10;
                if (s.Profile?.MaxWalkMinutes is int max && walk > max) return null;

                score -= WalkCost(walk);
                if (walk > Rules.CloseWalkMinutes && walk <= gap + PassingMinutes)
                    reasons.Add($"WARNING: {walk:0} min walk from {o.Room.DisplayName}, over {Rules.CloseWalkMinutes:0} min");
                if (walk > gap + PassingMinutes)
                {
                    score += CannotMakeIt;
                    reasons.Add($"WARNING: {walk:0} min walk from {o.Room.DisplayName} with {gap:0} min gap");
                }

                bool sameCourse = o.Section.Course == s.Course;
                if ((w.BackToBack || w.SameRoom) && (sameCourse || w.BackToBack))
                {
                    score += BackToBackBonus;
                    if (o.Room == room)
                    {
                        score += SameRoomBonus;
                        reasons.Add($"back-to-back with {o.Section.Course} in the same room");
                    }
                    else reasons.Add($"back-to-back with {o.Section.Course}");
                }
                else if (o.Room == room)
                {
                    reasons.Add($"same room as their {o.Section.Course}, no walk");
                }
            }

            return score;
        }

        /// <summary>
        /// Cost of a walk: almost nothing up to 5 minutes (preferred), climbing to
        /// 10 (still close), and steep beyond that.
        /// </summary>
        private int WalkCost(double minutes)
        {
            double preferred = Rules.PreferredWalkMinutes, close = Rules.CloseWalkMinutes;
            if (minutes <= preferred) return (int)(minutes * 2);
            if (minutes <= close) return (int)(preferred * 2 + (minutes - preferred) * PerWalkMinute);
            return (int)(preferred * 2 + (close - preferred) * PerWalkMinute + (minutes - close) * PerWalkMinute * 3);
        }

        // ------------------------------------------------------------ discussions

        private const int PastDiscussionRoom = 200;
        private const int PerDiscussionRank = 300;

        /// <summary>
        /// One discussion per lecture section, as in the registrar export. A
        /// section gets one when its form asks for it, or when it had one last
        /// time and the form does not say no. Times come from the form's
        /// discussion notes, then last time's discussion. Rooms favor last time's
        /// room and a short walk from the lecture and from the course's other
        /// discussions, which one TA often runs back to back.
        /// </summary>
        private void PlaceDiscussions(Plan plan)
        {
            var occupied = plan.Placements.Where(p => p.Room != null).ToList();

            foreach (var group in plan.Placements.Where(p => p.Time != null && !p.Section.Online)
                         .GroupBy(p => (p.Section.Surname, p.Section.Course)))
            {
                int index = 0;
                foreach (var lecture in group.OrderBy(p => p.Time!.Start))
                {
                    var s = lecture.Section;
                    var pastWithDisc = s.PastSections.Where(p => p.DiscussionTime != null).ToList();
                    var past = pastWithDisc.Count > 0 ? pastWithDisc[index % pastWithDisc.Count] : null;
                    index++;

                    bool formSaysYes = s.Form != null && s.Wishes.WantsDiscussion;
                    bool formSaysNo = s.Form != null && s.Form.Discussion.StartsWith("N", StringComparison.OrdinalIgnoreCase) &&
                                      s.Form.DiscussionRequirements.Trim().Length == 0;

                    if (past != null && formSaysNo)
                    {
                        s.Flag(FlagLevel.Info, ReviewFlag.History,
                            $"Had a discussion in {past.Semester} ({past.DiscussionTime}) but the form says no, so none is planned.");
                        continue;
                    }
                    if (!formSaysYes && past == null) continue;

                    var options = DiscussionOptions(s, past, lecture);
                    if (options.Count == 0)
                    {
                        s.Flag(FlagLevel.Review, ReviewFlag.Time,
                            "Discussion requested but no time to go on (no notes, no past discussion). Place it by hand.");
                        continue;
                    }

                    var disc = new Placement { Section = s, LectureOf = lecture };
                    var siblings = plan.Discussions.Where(d => d.Section.Surname == s.Surname && d.Section.Course == s.Course).ToList();
                    (TimeOption Opt, Room Room, int Score, List<string> Why)? best = null;

                    // Only when one person runs them all ("the same person handles both")
                    // can a course's discussions not overlap each other.
                    bool oneTa = Regex.IsMatch(s.Form?.DiscussionRequirements ?? "", @"same (person|ta)\b", RegexOptions.IgnoreCase);

                    foreach (var opt in options)
                    {
                        if (oneTa && siblings.Any(d => d.Time != null && d.Time.Overlaps(opt.Time))) continue;

                        foreach (var room in _rooms)
                        {
                            if (room.Capacity < Rules.DiscussionSize || Rules.Offline.IsOffline(room)) continue;
                            if (occupied.Concat(plan.Discussions).Any(o => o.Room == room && o.Time!.Overlaps(opt.Time))) continue;

                            var why = new List<string>();
                            int score = -PerDiscussionRank * (opt.Rank - 1) - (room.Capacity - Rules.DiscussionSize);

                            if (past != null && $"{past.DiscussionBuilding} {past.DiscussionRoom}" == room.DisplayName)
                            {
                                score += PastDiscussionRoom;
                                why.Add($"discussion room in {past.Semester}");
                            }

                            double fromLecture = _campus.WalkMinutes(lecture.Room?.Building ?? Rules.HomeBuilding, room.Building) ?? 10;
                            score -= WalkCost(fromLecture);
                            if (fromLecture <= Rules.PreferredWalkMinutes) why.Add($"{fromLecture:0} min from the lecture");

                            foreach (var sib in siblings.Where(d => d.Room != null))
                                score -= WalkCost(_campus.WalkMinutes(sib.Room!.Building, room.Building) ?? 10);

                            if (Regex.IsMatch(s.Form?.DiscussionRequirements ?? "", "gdc", RegexOptions.IgnoreCase))
                                score -= WalkCost(_campus.WalkMinutes("GDC", room.Building) ?? 10);

                            if (best == null || score > best.Value.Score)
                                best = (opt, room, score, why);
                        }
                    }

                    if (best == null)
                    {
                        s.Flag(FlagLevel.Problem, ReviewFlag.Room, "No free room for the discussion at any possible time. Place it by hand.");
                        continue;
                    }

                    disc.Option = best.Value.Opt;
                    disc.Time = best.Value.Opt.Time;
                    disc.Room = best.Value.Room;
                    disc.Why.Add($"time: {best.Value.Opt.Source}");
                    disc.Why.AddRange(best.Value.Why);
                    lecture.Discussion = disc;
                    plan.Discussions.Add(disc);
                }
            }
        }

        private List<TimeOption> DiscussionOptions(SectionRequest s, PastSection? past, Placement lecture)
        {
            var options = new List<TimeOption>();
            var duration = past?.DiscussionTime?.Duration ?? TimeSpan.FromHours(1);
            string notes = s.Form?.DiscussionRequirements ?? "";

            foreach (var (t, _) in PreferenceParser.FindTimes(notes, duration))
                options.Add(new TimeOption(t, 1, "discussion notes"));

            if (past?.DiscussionTime != null)
            {
                var t0 = past.DiscussionTime;
                options.Add(new TimeOption(t0, options.Count == 0 ? 1 : 2, $"same as {past.Semester}"));

                // Fallback: the same day, an hour or two either side.
                foreach (int shift in new[] { 1, -1, 2, -2 })
                {
                    var start = t0.Start + TimeSpan.FromHours(shift);
                    if (start < new TimeSpan(8, 0, 0) || start + t0.Duration > new TimeSpan(21, 0, 0)) continue;
                    options.Add(new TimeOption(new MeetingTime(t0.Days, start, start + t0.Duration), 3,
                        $"{(shift > 0 ? "+" : "")}{shift}h from {past.Semester}'s time"));
                }
            }

            // "Monday discussion sections" / "On Fridays": every hour that day, nearest last time's start first.
            if (options.Count == 0 || !options.Any(o => o.Source == "discussion notes"))
            {
                var days = Regex.Matches(notes, @"\b(mondays?|tuesdays?|wednesdays?|thursdays?|fridays?)\b", RegexOptions.IgnoreCase)
                    .Select(m => MeetingTime.ParseDays(m.Value.Substring(0, m.Value.StartsWith("th", StringComparison.OrdinalIgnoreCase) ? 2 : 1)))
                    .SelectMany(d => d).Distinct().ToList();
                var anchor = past?.DiscussionTime?.Start ?? new TimeSpan(10, 0, 0);
                foreach (var day in days)
                    foreach (int h in Enumerable.Range(9, 8).OrderBy(h => Math.Abs((new TimeSpan(h, 0, 0) - anchor).TotalHours)))
                    {
                        var t = new MeetingTime(new[] { day }, new TimeSpan(h, 0, 0), new TimeSpan(h, 0, 0) + duration);
                        if (!options.Any(o => o.Time.Equals(t)))
                            options.Add(new TimeOption(t, options.Count == 0 ? 1 : 2, $"{day}s, from the notes"));
                    }
            }

            options.RemoveAll(o => o.Time.Overlaps(lecture.Time!));
            return options;
        }

        // ------------------------------------------------------------ clean-up

        /// <summary>
        /// Once everyone has a spot, rooms that were taken early may have been
        /// freed or bettered. Each section is lifted out and placed again; if the
        /// new spot is clearly better it moves. Repeats until nothing moves.
        /// </summary>
        private void Improve(List<Placement> order, List<Placement> all)
        {
            for (int pass = 0; pass < 3; pass++)
            {
                int moved = 0;
                foreach (var p in order)
                {
                    if (p.Room == null || p.Option == null) continue;

                    var currentReasons = new List<string>();
                    int current = (ScoreRoom(p, p.Time!, p.Room, all, currentReasons) ?? int.MinValue / 2)
                                  - PointsPerRank * (p.Option.Rank - 1);

                    var best = BestChoice(p, all, out _);
                    if (best == null || best.Value.Score <= current + 20) continue;
                    if (best.Value.Room == p.Room && best.Value.Option.Time.Equals(p.Time)) continue;

                    string from = $"{p.Time} {p.Room.DisplayName}";
                    p.Option = best.Value.Option;
                    p.Time = best.Value.Option.Time;
                    p.Room = best.Value.Room;
                    p.Why.Clear();
                    p.Why.Add(TimeReason(best.Value.Option));
                    p.Why.AddRange(best.Value.Reasons);
                    p.Why.Add($"moved from {from} in clean-up");
                    moved++;
                }
                if (moved == 0) break;
            }
        }

        private void Finish(Placement p)
        {
            var s = p.Section;
            if (p.Room != null)
            {
                p.Fit = Rules.FitIndicator(p.Room.Capacity, s.SizeForRoom);
                p.RegistrarCap = Rules.RegistrarCap(p.Room.Capacity);

                string over = $"{Rules.OverReported(p.Room.Capacity, s.SizeForRoom):+0%;-0%}";
                if (p.Fit == "Max high")
                    s.Flag(FlagLevel.Review, ReviewFlag.Size,
                        $"Room too big: {p.Room.DisplayName} reports {p.RegistrarCap} for an expected {s.SizeForRoom} ({over}). No closer room was free.");
                else if (p.Fit == "Min high")
                    s.Flag(FlagLevel.Info, ReviewFlag.Size,
                        $"Room on the big side: {p.Room.DisplayName} reports {p.RegistrarCap} for an expected {s.SizeForRoom} ({over}).");
                // A few seats under is normal; more than 10% under is too far off.
                if (p.RegistrarCap < s.SizeForRoom)
                    s.Flag(p.RegistrarCap < s.SizeForRoom * (1 - Rules.RegistrarHeadroom) ? FlagLevel.Review : FlagLevel.Info,
                        ReviewFlag.Size,
                        $"Room tight: registrar cap {p.RegistrarCap} (capacity minus 10%) is below the expected {s.SizeForRoom}.");
                if (s.Wishes.Recording && !p.Room.LectureCapture)
                    s.Flag(FlagLevel.Review, ReviewFlag.SpecialRequest,
                        $"Wants lectures recorded, but {p.Room.DisplayName} does not support Lectures Online" +
                        (_rooms.Any(r => r.LectureCapture) ? ". No room on the list was free at their times." : " (the list is empty)."));
                if (p.Why.Any(w => w.StartsWith("WARNING")))
                    s.Flag(FlagLevel.Review, ReviewFlag.Room, string.Join("; ", p.Why.Where(w => w.StartsWith("WARNING"))).Replace("WARNING: ", ""));
                if (p.Option != null && p.Option.Rank > 1 && s.Wishes.Times.Count > 0)
                    s.Flag(FlagLevel.Info, ReviewFlag.Time, $"Got their {Ordinal(p.Option.Rank)} preference, not their first.");
            }
        }

        private static string Ordinal(int n) => n switch { 1 => "1st", 2 => "2nd", 3 => "3rd", _ => $"{n}th" };
    }
}
