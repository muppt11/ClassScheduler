namespace RoomScheduler
{
    public enum FlagLevel
    {
        /// <summary>Something the app did on its own, recorded so it can be checked.</summary>
        Info,

        /// <summary>Needs a person to look at it before the schedule goes out.</summary>
        Review,

        /// <summary>The app could not do what was asked.</summary>
        Problem
    }

    /// <summary>Something about a section that a person should know or check.</summary>
    public record ReviewFlag(FlagLevel Level, string Category, string Message)
    {
        public const string Contradiction = "Possible inaccuracy";
        public const string SpecialRequest = "Special request";
        public const string NeedsInfo = "Needs info";
        public const string Policy = "Policy";
        public const string Size = "Class size";
        public const string Room = "Room";
        public const string Time = "Time";
        public const string History = "Past schedule";
        public const string Accommodation = "Accommodation";
    }
}
