using System;
using System.Linq;

namespace RoomScheduler
{
    public class Program
    {
        // CSV export of the UT Austin registrar's Sept 2026 classroom report (GPC sheet).
        private const string RoomsCsvPath = "Sept2026_Classroom-Types_Rooms_Report.csv";

        // Past sections extracted from the printed course offerings.
        private const string HistoryCsvPath = "data/course_history.csv";

        public static void Main(string[] args)
        {
            int fakeCourseCount = 10;
            if (args.Length > 0 && int.TryParse(args[0], out int parsedCount))
                fakeCourseCount = parsedCount;

            Console.WriteLine($"Loading rooms from '{RoomsCsvPath}'...");
            var rooms = Room.LoadFromCsv(RoomsCsvPath);
            Console.WriteLine($"Loaded {rooms.Count} room(s).");

            var history = CourseHistory.LoadFromCsv(HistoryCsvPath);
            Console.WriteLine($"Loaded {history.RecordCount} past section(s) " +
                              $"across {history.Departments.Count()} department(s).\n");

            Console.WriteLine($"Generating {fakeCourseCount} fake course(s) for testing...\n");
            var courses = Scheduler.GenerateFakeCourses(fakeCourseCount);

            var scheduler = new Scheduler(rooms, courses, history);
            scheduler.Run();

            PrintAssignedSchedule(scheduler);
            PrintConflicts(scheduler);
        }

        private static void PrintAssignedSchedule(Scheduler scheduler)
        {
            Console.WriteLine(new string('=', 104));
            Console.WriteLine(" ASSIGNED SCHEDULE");
            Console.WriteLine(new string('=', 104));
            PrintHeader();

            if (scheduler.Assignments.Count == 0)
            {
                Console.WriteLine("(no courses were assigned)");
            }
            else
            {
                var ordered = scheduler.Assignments
                    .OrderBy(a => FirstDay(a.Course))
                    .ThenBy(a => a.Course.StartTime);

                foreach (var (course, room) in ordered)
                {
                    Console.WriteLine(
                        $"{course.CourseName,-9} {course.UniqueNumber,-7} {course.MeetingPattern,-4} " +
                        $"{course.TimeRange,-13} {room.Building + " " + room.RoomNumber,-12} " +
                        $"{room.Capacity,4}  {course.EnrollmentCount,4}  " +
                        $"{course.ProfessorName,-14}  {scheduler.ExplainChoice(course, room)}");
                }
            }

            Console.WriteLine();
        }

        private static void PrintConflicts(Scheduler scheduler)
        {
            Console.WriteLine(new string('=', 104));
            Console.WriteLine(" CONFLICTS - COULD NOT ASSIGN");
            Console.WriteLine(new string('=', 104));

            if (scheduler.UnresolvedConflicts.Count == 0)
            {
                Console.WriteLine("(none - every course was successfully assigned)");
            }
            else
            {
                Console.WriteLine($"{"course",-9} {"unique",-7} {"days",-4} {"time",-13} {"enroll",6}");
                Console.WriteLine(new string('-', 104));

                foreach (var course in scheduler.UnresolvedConflicts)
                {
                    Console.WriteLine(
                        $"{course.CourseName,-9} {course.UniqueNumber,-7} {course.MeetingPattern,-4} " +
                        $"{course.TimeRange,-13} {course.EnrollmentCount,6}  {course.ProfessorName}");
                }
            }

            Console.WriteLine();
        }

        private static void PrintHeader()
        {
            Console.WriteLine(
                $"{"course",-9} {"unique",-7} {"days",-4} {"time",-13} {"room",-12} {"cap",4}  {"enrl",4}  {"instructor",-14}  why");
            Console.WriteLine(new string('-', 104));
        }

        /// <summary>Earliest weekday a section meets, used only for display ordering.</summary>
        private static int FirstDay(Course course) =>
            course.MeetingDays.Count == 0 ? 99 : course.MeetingDays.Min(d => (int)d);
    }
}
