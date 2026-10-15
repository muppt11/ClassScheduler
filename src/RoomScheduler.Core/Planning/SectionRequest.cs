using System;
using System.Collections.Generic;
using System.Linq;

namespace RoomScheduler
{
    /// <summary>
    /// One row of the department's Classes App sheet: a section that needs a time
    /// and a room, joined with the professor's preference form and profile.
    /// </summary>
    public class SectionRequest
    {
        /// <summary>Row in the source sheet, 0-based, used to write results back in place.</summary>
        public int SourceRow { get; init; }

        public string Title { get; init; } = "";
        public string Course => Names.CourseNumber(Title);
        public string Instructor { get; init; } = "";
        public string Surname => Names.Surname(Instructor);
        public bool InstructorMissing => Names.IsPlaceholder(Instructor);

        public FacultyTrack Track { get; set; }
        public bool Difficult { get; set; }

        /// <summary>"Minimum Room Size Needed" on the sheet: the projected class size.</summary>
        public int ProjectedSize { get; init; }

        /// <summary>What Fall 2025 (or whatever history is loaded) actually enrolled.</summary>
        public int? PastEnrollment { get; set; }

        /// <summary>The size the room is chosen for, after comparing projected and past numbers.</summary>
        public int SizeForRoom { get; set; }

        public string Type { get; init; } = "";
        public string Tags { get; init; } = "";
        public int ProctorsNeeded { get; init; }
        public int TAsNeeded { get; init; }

        public PreferenceForm? Form { get; set; }
        public Wishes Wishes { get; set; } = new();
        public FacultyProfile? Profile { get; set; }
        public List<PastSection> PastSections { get; set; } = new();

        public bool Online => Wishes.Online || Title.Contains("-WB", StringComparison.OrdinalIgnoreCase);

        public List<ReviewFlag> Flags { get; } = new();

        public void Flag(FlagLevel level, string category, string message) =>
            Flags.Add(new ReviewFlag(level, category, message));

        /// <summary>Lower sorts first. Difficult faculty, then tenure track, then professional track.</summary>
        public int PriorityTier =>
            Difficult ? 0 : Track switch { FacultyTrack.Tenure => 1, FacultyTrack.Professional => 2, _ => 3 };

        public string PriorityLabel =>
            (Difficult ? "D " : "") + Track switch { FacultyTrack.Tenure => "T", FacultyTrack.Professional => "P", _ => "?" };
    }

    /// <summary>The result for one section.</summary>
    public class Placement
    {
        public SectionRequest Section { get; init; } = null!;
        public MeetingTime? Time { get; set; }
        public Room? Room { get; set; }
        public TimeOption? Option { get; set; }
        public List<string> Why { get; } = new();

        /// <summary>For a discussion section, the lecture it belongs to.</summary>
        public Placement? LectureOf { get; init; }
        public bool IsDiscussion => LectureOf != null;

        /// <summary>For a lecture, its planned discussion section, if any.</summary>
        public Placement? Discussion { get; set; }

        public string Label => IsDiscussion ? $"{Section.Title} (discussion)" : Section.Title;

        public string Status =>
            IsDiscussion ? (Room == null ? "Discussion unplaced" : "Discussion") :
            Section.Online ? "Online" :
            Time == null ? "Not scheduled" :
            Room == null ? "No room" : "Scheduled";

        public string Fit { get; set; } = "";
        public int? RegistrarCap { get; set; }
    }
}
