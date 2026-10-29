using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RoomScheduler;

/// <summary>Soft building and time preferences; never bypass room or instructor constraints.</summary>
public sealed record BuildingPrioritySettings
{
    public bool Enabled { get; init; } = true;
    public List<string> Buildings { get; init; } = new() { "GDC" };
    public TimeSpan Start { get; init; } = new(9, 30, 0);
    public TimeSpan End { get; init; } = TimeSpan.FromHours(17);
    public bool UseFacultyHistory { get; init; } = true;
    public bool PreferControlledRooms { get; init; } = true;

    public void Validate()
    {
        if (Buildings.Count > 10 || Buildings.Any(b => !Regex.IsMatch(b, @"^[A-Z]{2,5}$")) ||
            Buildings.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Buildings.Count || Enabled && Buildings.Count == 0)
            throw new ArgumentException("Enter up to ten different building codes in priority order, such as GDC, RLP, ECJ.");
        if (Start < TimeSpan.Zero || End > TimeSpan.FromHours(24) || Start >= End)
            throw new ArgumentException("The preferred start must be earlier than the finish within the same day.");
    }

    public bool InWindow(MeetingTime time) => time.Start >= Start && time.End <= End;

    public static bool ControlledAt(Room room, MeetingTime time) =>
        room.DepartmentOnly || room.CsOwnsFrom is TimeSpan from && time.Start >= from;

    public int Score(Room room, MeetingTime time)
    {
        if (!Enabled) return 0;
        int index = Buildings.FindIndex(b => b.Equals(room.Building, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return 0;
        return 180 / (index + 1) + (InWindow(time) ? 80 : 0) +
            (PreferControlledRooms && ControlledAt(room, time) ? 60 : 0);
    }

    public string Describe() => !Enabled ? "Building priorities are off." :
        $"Building order: {string.Join(" → ", Buildings)}. Preferred window: {MeetingTime.ClockTime(Start)}–{MeetingTime.ClockTime(End)}. " +
        (PreferControlledRooms ? "Prefer CS-controlled rooms during their recorded control hours. " : "") +
        (UseFacultyHistory ? "Use faculty building history from active past schedules. " : "") +
        "These are preferences: other buildings and times remain fallbacks, and all capacity, availability, policy, and accommodation checks still apply.";
}
