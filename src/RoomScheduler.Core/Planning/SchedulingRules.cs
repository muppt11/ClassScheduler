using System;
using System.Collections.Generic;

namespace RoomScheduler
{
    /// <summary>
    /// Department policies and thresholds in one place, so they can be changed
    /// without hunting through the scheduler.
    /// </summary>
    public class SchedulingRules
    {
        /// <summary>
        /// Tenure-track faculty have a faculty lunch, so they cannot teach anything
        /// that overlaps Monday or Wednesday 12:00-1:00, even if they ask for it.
        /// </summary>
        public MeetingTime TenureLunch { get; set; } = MeetingTime.Of("MW", 12, 0, 13, 0);

        /// <summary>
        /// Graduate-course time. TTH 11:00-12:30 is held for graduate courses, so a
        /// tenure-track professor who teaches a graduate course this semester can't
        /// have their other classes then (confirmed with Doug, Oct 2026).
        /// </summary>
        public MeetingTime GraduateSlot { get; set; } = MeetingTime.Of("TTH", 11, 0, 12, 30);

        /// <summary>
        /// Graduate courses have 8 or 9 as the middle digit of the course
        /// number (381B, 395T). Only tenure-track faculty teach them.
        /// </summary>
        public static bool IsGraduate(string course) =>
            course.Length >= 3 && char.IsDigit(course[0]) && (course[1] == '8' || course[1] == '9');

        /// <summary>The enrollment cap given to the registrar is room capacity minus this share.</summary>
        public int UnknownEnrollmentSize { get; set; } = 25;

        public double RegistrarHeadroom { get; set; } = 0.10;

        /// <summary>
        /// The size we report to the registrar (room capacity minus 10%) may run
        /// above the students we expect, to get the rooms we want. Up to this much
        /// over is "Normal".
        /// </summary>
        public double MinHighOver { get; set; } = 0.50;

        /// <summary>
        /// Reporting more than this much over is "Max high": past the 50-75% the
        /// department allows, so the class should be in a smaller room.
        /// Between the two is "Min high", still allowed.
        /// </summary>
        public double MaxHighOver { get; set; } = 0.75;

        /// <summary>
        /// Last year's real enrollment below this share of the projected size gets
        /// flagged and downsized.
        /// </summary>
        public double ActualFarBelowProjected { get; set; } = 0.75;

        /// <summary>How much such a class is downsized from its projected size (Doug: 15%).</summary>
        public double DownsizeShare { get; set; } = 0.15;

        /// <summary>
        /// Discussion sections are phase two, after the lecture schedule is
        /// approved (chairs' proof in January). Off until then.
        /// </summary>
        public bool ScheduleDiscussions { get; set; } = false;

        /// <summary>A walk this short between classes is ideal.</summary>
        public double PreferredWalkMinutes { get; set; } = 5;

        /// <summary>A walk up to this long is still close. Longer is flagged.</summary>
        public double CloseWalkMinutes { get; set; } = 10;

        /// <summary>Students planned for each discussion section until the department says otherwise.</summary>
        public int DiscussionSize { get; set; } = 25;

        /// <summary>Gap between two classes that counts as back-to-back.</summary>
        public TimeSpan BackToBackGap { get; set; } = TimeSpan.FromMinutes(30);

        /// <summary>CS owns its four big GDC rooms from this time on, Monday to Friday.</summary>
        public TimeSpan CsOwnsGdcFrom { get; set; } = new(12, 30, 0);

        /// <summary>
        /// The department's home building. Even without a request, CS classes run
        /// close to GDC (Fall 2025 used GDC, RLP, JGB and nearby buildings), so
        /// every section gets a mild pull towards it.
        /// </summary>
        public string HomeBuilding { get; set; } = "GDC";

        /// <summary>Department of course numbers written without a prefix ("314H" is CS 314H).</summary>
        public string Department { get; set; } = "CS";

        /// <summary>Buildings and rooms the scheduler never uses. CPE starts offline.</summary>
        public OfflineRooms Offline { get; set; } = OfflineRooms.Default();

        public int RegistrarCap(int roomCapacity) =>
            (int)Math.Floor(roomCapacity * (1 - RegistrarHeadroom));

        /// <summary>How far the size reported to the registrar runs above the students expected.</summary>
        public double OverReported(int roomCapacity, int expectedEnrollment) =>
            expectedEnrollment <= 0 ? 0 : (double)RegistrarCap(roomCapacity) / expectedEnrollment - 1;

        /// <summary>"Normal", "Min high" or "Max high", from how far the reported size runs over.</summary>
        public string FitIndicator(int roomCapacity, int expectedEnrollment)
        {
            if (expectedEnrollment <= 0) return "Normal";
            double over = OverReported(roomCapacity, expectedEnrollment);
            if (over >= MaxHighOver) return "Max high";
            if (over >= MinHighOver) return "Min high";
            return "Normal";
        }
    }
}
