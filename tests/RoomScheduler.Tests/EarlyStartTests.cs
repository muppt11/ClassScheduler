using RoomScheduler;
using Xunit;

namespace RoomScheduler.Tests;

public partial class SchedulingTests
{
    [Fact]
    public void Automatic_planning_prefers_nine_or_later_without_time_requests()
    {
        var plan = Planner().Build(Requests(preferences: false));
        var placed = Assert.Single(plan.Placements);
        Assert.NotNull(placed.Room); Assert.True(placed.Time!.Start >= TimeSpan.FromHours(9));
    }

    [Fact]
    public void Explicit_eight_am_preference_is_honored()
    {
        var wb = Requests();
        wb.Forms[0] = new PreferenceForm { Professor = "SMITH A", Course = "314 DATA STRUCTURES", Time1 = "MWF 8am-9am" };
        var placed = Assert.Single(Planner().Build(wb).Placements);
        Assert.NotNull(placed.Room); Assert.Equal(TimeSpan.FromHours(8), placed.Time!.Start);
    }

    [Fact]
    public void Eight_am_remains_a_fallback_when_later_times_are_unavailable()
    {
        var faculty = FacultyStore.InMemory();
        var profile = faculty.GetOrAdd("SMITH", "SMITH A"); profile.Unavailable = "MWF 9am-5pm; MWF 5pm-9pm; TTH 9am-5pm; TTH 5pm-9pm";
        Assert.NotEmpty(profile.UnavailableTimes());
        var placed = Assert.Single(Planner(faculty: faculty).Build(Requests(preferences: false)).Placements);
        Assert.NotNull(placed.Room); Assert.Equal(TimeSpan.FromHours(8), placed.Time!.Start);
    }

    private static PastSchedule EarlyHistory(bool discussion = false)
    {
        var history = PastSchedule.Empty;
        history.Sections.Add(new PastSection { SemesterCode = 20259, Semester = "Fall 2025", Course = "314",
            Title = "DATA STRUCTURES", Instructors = new() { "SMITH" }, Enrollment = 30,
            Time = MeetingTime.Of("MWF", discussion ? 10 : 8, 0, discussion ? 11 : 9, 0),
            DiscussionTime = discussion ? MeetingTime.Of("F", 8, 0, 9, 0) : null });
        return history;
    }

    [Fact]
    public void Historical_eight_am_time_is_a_fallback_rather_than_a_current_request()
    {
        var placed = Assert.Single(Planner(past: EarlyHistory()).Build(Requests(preferences: false)).Placements);
        Assert.NotNull(placed.Room); Assert.True(placed.Time!.Start >= TimeSpan.FromHours(9));
        Assert.Contains(placed.Section.Flags, f => f.Message.Contains("History only offers an early start"));
    }

    [Fact]
    public void Early_history_is_still_used_when_alternate_times_are_disabled()
    {
        var planner = Planner(past: EarlyHistory()); planner.Rules.AllowAlternateTimes = false;
        var placed = Assert.Single(planner.Build(Requests(preferences: false)).Placements);
        Assert.NotNull(placed.Room); Assert.Equal(TimeSpan.FromHours(8), placed.Time!.Start);
    }

    [Fact]
    public void Discussion_history_prefers_a_later_feasible_time()
    {
        var planner = Planner(past: EarlyHistory(discussion: true)); planner.Rules.ScheduleDiscussions = true;
        var discussion = Assert.Single(planner.Build(Requests()).Discussions);
        Assert.NotNull(discussion.Room); Assert.True(discussion.Time!.Start >= TimeSpan.FromHours(9));
    }

    [Fact]
    public void Discussion_notes_can_explicitly_request_eight_am()
    {
        var wb = Requests(); wb.Forms[0] = new PreferenceForm { Professor = "SMITH A", Course = "314 DATA STRUCTURES",
            Time1 = "MWF 10am-11am", Discussion = "Y", DiscussionRequirements = "Friday 8am-9am" };
        var planner = Planner(); planner.Rules.ScheduleDiscussions = true;
        var discussion = Assert.Single(planner.Build(wb).Discussions);
        Assert.NotNull(discussion.Room); Assert.Equal(TimeSpan.FromHours(8), discussion.Time!.Start);
    }
}

public partial class ConversationTests
{
    [Fact]
    public void Chat_time_search_prefers_later_times_even_when_eight_am_is_currently_assigned()
    {
        var f = Fixture(); f.Plan.Placements[0].Time = MeetingTime.Of("MWF", 8, 0, 9, 0);
        Send(f, "find comp arch a free slot");
        Assert.NotNull(f.Chat.Proposal);
        foreach (var option in f.Chat.Suggestions.Where(s => s.StartsWith("Option ")).ToList())
        {
            Send(f, option);
            var change = f.Chat.Proposal!.ProposedChanges.First(c => c.Placement.Section.Course == "429");
            Assert.True(change.Time.Start >= TimeSpan.FromHours(9));
        }
    }

    [Fact]
    public void Chat_accepts_explicit_eight_am_move()
    {
        var f = Fixture(); Send(f, "move comp arch to Tuesday at 8am");
        Assert.NotNull(f.Chat.Proposal);
        var change = f.Chat.Proposal!.ProposedChanges.First(c => c.Placement.Section.Course == "429");
        Assert.Equal(TimeSpan.FromHours(8), change.Time.Start);
    }

    [Fact]
    public void Chat_uses_eight_am_when_no_later_option_is_feasible()
    {
        var f = Fixture(); f.Plan.Placements[0].Section.Profile!.Unavailable = "MWF 9am-5pm; MWF 5pm-9pm; TTH 9am-5pm; TTH 5pm-9pm";
        Send(f, "find comp arch a free slot");
        Assert.NotNull(f.Chat.Proposal);
        var change = f.Chat.Proposal!.ProposedChanges.First(c => c.Placement.Section.Course == "429");
        Assert.Equal(TimeSpan.FromHours(8), change.Time.Start); Assert.Contains("fallback", Last(f.Chat));
    }

    [Fact]
    public void Room_only_changes_keep_an_existing_eight_am_meeting()
    {
        var f = Fixture(); var target = f.Plan.Placements[0];
        target.Time = MeetingTime.Of("MWF", 8, 0, 9, 0); target.Room = f.Rooms[0];
        Send(f, "find comp arch a smaller room at the same time");
        Assert.NotNull(f.Chat.Proposal);
        Assert.Equal(target.Time, f.Chat.Proposal!.ProposedChanges.First(c => c.Placement == target).Time);
    }
}
