using System;
using System.Collections.Generic;
using System.Linq;

namespace RoomScheduler
{
    /// <summary>
    /// Assigns courses to rooms using a most-constrained-first heuristic:
    /// the largest classes get first pick of rooms, and each is placed in
    /// the smallest room that fits it without a time conflict.
    /// </summary>
    public class Scheduler
    {
        private readonly List<Room> _rooms;
        private readonly List<Course> _courses;
        private readonly CourseHistory _history;

        // Tracks which courses have already been placed in each room, so
        // later courses can be checked for day/time overlaps.
        private readonly Dictionary<Room, List<Course>> _roomBookings = new();

        public List<(Course Course, Room Room)> Assignments { get; } = new();
        public List<Course> UnresolvedConflicts { get; } = new();

        public Scheduler(List<Room> rooms, List<Course> courses, CourseHistory? history = null)
        {
            _rooms = rooms;
            _courses = courses;
            _history = history ?? CourseHistory.Empty;

            foreach (var room in rooms)
                _roomBookings[room] = new List<Course>();
        }

        /// <summary>
        /// Runs the assignment algorithm. Largest classes (by enrollment) are
        /// scheduled first so they are not left fighting over scraps.
        /// </summary>
        public void Run()
        {
            Assignments.Clear();
            UnresolvedConflicts.Clear();
            foreach (var bookings in _roomBookings.Values)
                bookings.Clear();

            var sortedCourses = _courses
                .OrderByDescending(c => c.EnrollmentCount)
                .ToList();

            foreach (var course in sortedCourses)
            {
                var room = FindBestAvailableRoom(course);

                if (room == null)
                {
                    UnresolvedConflicts.Add(course);
                    continue;
                }

                _roomBookings[room].Add(course);
                Assignments.Add((course, room));
            }
        }

        private Room? FindBestAvailableRoom(Course course)
        {
            return _rooms
                .Where(room => room.Capacity >= course.EnrollmentCount)
                .Where(room => !HasConflict(room, course))
                .OrderByDescending(room => ScoreRoom(course, room))
                .ThenBy(room => room.Capacity)
                .FirstOrDefault();
        }

        // How much a room in a familiar building is worth, per past section seen
        // there, and how many past sections still count towards the bonus.
        private const int AffinityPointsPerSection = 20;
        private const int AffinityMaxSections = 10;

        /// <summary>
        /// Higher is better. Every wasted seat costs a point, so without any
        /// history this is just the old "smallest room that fits" rule. A
        /// building the department actually teaches in earns a bonus, capped so
        /// that a small class is never pushed into a lecture hall just to stay
        /// in a familiar building.
        /// </summary>
        private int ScoreRoom(Course course, Room room)
        {
            int wastedSeats = room.Capacity - course.EnrollmentCount;
            int pastSections = _history.BuildingUsage(course.Department, room.Building);
            int affinityBonus = Math.Min(pastSections, AffinityMaxSections) * AffinityPointsPerSection;

            return affinityBonus - wastedSeats;
        }

        /// <summary>Why a room was picked, for display in the schedule output.</summary>
        public string ExplainChoice(Course course, Room room)
        {
            int pastSections = _history.BuildingUsage(course.Department, room.Building);
            return pastSections > 0
                ? $"{course.Department} has used {room.Building} {pastSections}x"
                : "best capacity fit";
        }

        /// <summary>
        /// A room is unusable if something already booked in it shares a meeting
        /// day with this course AND overlaps it in time. An MWF section holds the
        /// room on all three of those days.
        /// </summary>
        private bool HasConflict(Room room, Course course)
        {
            foreach (var booked in _roomBookings[room])
            {
                bool sharesADay = booked.MeetingDays.Intersect(course.MeetingDays).Any();
                if (!sharesADay)
                    continue;

                bool overlaps = course.StartTime < booked.EndTime && booked.StartTime < course.EndTime;
                if (overlaps)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// One realistic way a class can meet, taken from the UT Austin printed
        /// course offerings catalog: the days it meets, how long each meeting
        /// runs, and the start times the catalog uses for that shape of class.
        /// </summary>
        private record SlotTemplate(DayOfWeek[] Days, int DurationMinutes, int[] StartTimes);

        private static int At(int hour, int minute) => hour * 60 + minute;

        private static readonly DayOfWeek[] Mwf =
            { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday };
        private static readonly DayOfWeek[] Mw =
            { DayOfWeek.Monday, DayOfWeek.Wednesday };
        private static readonly DayOfWeek[] Tth =
            { DayOfWeek.Tuesday, DayOfWeek.Thursday };

        /// <summary>
        /// Meeting patterns observed in the Spring 2026 catalog. Common patterns
        /// are listed more than once, so picking one at random roughly matches
        /// the real mix of class shapes.
        /// </summary>
        private static readonly SlotTemplate[] SlotTemplates =
        {
            // Three days a week, 50 minutes: the classic lecture.
            new(Mwf, 50, new[] { At(9,0), At(10,0), At(11,0), At(12,0), At(13,0), At(14,0), At(15,0) }),
            new(Mwf, 50, new[] { At(9,0), At(10,0), At(11,0), At(12,0), At(13,0), At(14,0), At(15,0) }),

            // Tuesday and Thursday, 75 minutes.
            new(Tth, 75, new[] { At(9,30), At(11,0), At(12,30), At(14,0), At(15,30), At(17,0) }),
            new(Tth, 75, new[] { At(9,30), At(11,0), At(12,30), At(14,0), At(15,30), At(17,0) }),

            // Monday and Wednesday, 90 minutes.
            new(Mw, 90, new[] { At(9,0), At(10,30), At(13,0), At(14,30), At(16,0) }),

            // Once a week for 3 hours: seminars.
            new(new[] { DayOfWeek.Monday }, 180, new[] { At(9,0), At(12,30), At(14,0) }),
            new(new[] { DayOfWeek.Tuesday }, 180, new[] { At(9,0), At(12,30), At(14,0) }),
            new(new[] { DayOfWeek.Wednesday }, 180, new[] { At(9,0), At(12,30), At(14,0) }),
            new(new[] { DayOfWeek.Thursday }, 180, new[] { At(9,0), At(12,30), At(14,0) }),

            // Once a week for 4 hours: studios. The catalog shows 130-530P.
            new(new[] { DayOfWeek.Monday }, 240, new[] { At(9,0), At(13,30) }),
            new(new[] { DayOfWeek.Friday }, 240, new[] { At(9,0), At(13,30) }),

            // Evening sections. The catalog has a 700-800P class.
            new(new[] { DayOfWeek.Tuesday }, 60, new[] { At(18,0), At(19,0) }),
            new(new[] { DayOfWeek.Wednesday }, 120, new[] { At(18,0), At(18,30) }),
        };

        /// <summary>
        /// Generates realistic-looking fake courses for testing the scheduler
        /// before real course data is available. Meeting patterns, durations and
        /// start times are modeled on the UT Austin Spring 2026 course offerings.
        /// Enrollment ranges 15-300, and unique numbers step by 5 as the catalog does.
        /// </summary>
        public static List<Course> GenerateFakeCourses(int count, int? seed = null)
        {
            var random = seed.HasValue ? new Random(seed.Value) : new Random();

            string[] lastNames =
            {
                "JOHNSON", "CHEN", "GARCIA", "PATEL", "KIM", "NGUYEN", "SMITH", "ROSSI",
                "MARTINEZ", "BROWN", "OKAFOR", "LOPEZ", "ANDERSON", "KHAN", "SILVA", "NOVAK",
                "BRAVO", "DUDLEY", "GLASSELL", "JENNINGS", "PYREK", "SIDDIQUI", "BIEG", "BAKO"
            };

            // The catalog prints instructors as surname plus a single initial.
            char[] initials = "ABCDEFGHIJKLMNPRSTW".ToCharArray();

            // Departments that appear in data/course_history.csv, so generated
            // courses can actually exercise the building preference.
            string[] departments = { "ARI", "ARC", "CS", "MIS", "MAN", "PR", "CMS" };

            // Suffix letters the catalog uses, as in ARI 310L and ARC 327P.
            string[] suffixes = { "", "", "", "L", "M", "P", "R", "T", "K" };

            var courses = new List<Course>();
            int uniqueNumber = 355;

            for (int i = 0; i < count; i++)
            {
                string professor =
                    $"{lastNames[random.Next(lastNames.Length)]}, {initials[random.Next(initials.Length)]}";

                string department = departments[random.Next(departments.Length)];
                int courseNumber = random.Next(1, 7) * 100 + random.Next(0, 10) * 10;
                string courseName = $"{department} {courseNumber}{suffixes[random.Next(suffixes.Length)]}";

                var template = SlotTemplates[random.Next(SlotTemplates.Length)];
                int startMinutes = template.StartTimes[random.Next(template.StartTimes.Length)];

                courses.Add(new Course
                {
                    UniqueNumber = uniqueNumber.ToString("00000"),
                    ProfessorName = professor,
                    CourseName = courseName,
                    EnrollmentCount = random.Next(15, 301),
                    MeetingDays = template.Days.ToList(),
                    StartTime = TimeSpan.FromMinutes(startMinutes),
                    Duration = TimeSpan.FromMinutes(template.DurationMinutes)
                });

                uniqueNumber += 5;
            }

            return courses;
        }
    }
}
