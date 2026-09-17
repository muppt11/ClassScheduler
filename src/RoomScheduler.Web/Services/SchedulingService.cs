using System.Collections.Generic;
using System.Linq;

namespace RoomScheduler.Web.Services
{
    /// <summary>
    /// Holds the room list and the past-usage history for the whole app, and
    /// runs the scheduler on demand. Rooms and history are read once at startup
    /// because neither changes while the app is running.
    /// </summary>
    public class SchedulingService
    {
        private const string RoomsCsvPath = "Sept2026_Classroom-Types_Rooms_Report.csv";
        private const string HistoryCsvPath = "data/course_history.csv";

        public List<Room> Rooms { get; }
        public CourseHistory History { get; }

        public SchedulingService()
        {
            Rooms = Room.LoadFromCsv(RoomsCsvPath);
            History = CourseHistory.LoadFromCsv(HistoryCsvPath);
        }

        public int RoomCount => Rooms.Count;
        public int BuildingCount => Rooms.Select(r => r.Building).Distinct().Count();

        public Scheduler Run(List<Course> courses)
        {
            var scheduler = new Scheduler(Rooms, courses, History);
            scheduler.Run();
            return scheduler;
        }

        public Scheduler RunGenerated(int count)
        {
            return Run(Scheduler.GenerateFakeCourses(count));
        }
    }
}
