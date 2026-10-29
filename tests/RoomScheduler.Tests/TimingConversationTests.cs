using RoomScheduler;
using Xunit;

namespace RoomScheduler.Tests;

public partial class ConversationTests
{
    [Fact]
    public void One_day_request_asks_weekday_and_preserves_actual_weekly_hours()
    {
        var f = Fixture(); Send(f, "where is comp arch?");
        Send(f, "make it one day a week");
        Assert.Null(f.Chat.Proposal); Assert.Contains("Which 1 weekday", Last(f.Chat));
        Send(f, "Friday at 2pm");
        Assert.NotNull(f.Chat.Proposal);
        var c = f.Chat.Proposal!.ProposedChanges.Single(c => c.Placement == f.Plan.Placements[0]);
        Assert.Equal(MeetingTime.Of("F", 14, 0, 17, 0), c.Time);
        Assert.Equal(MeetingTime.Of("MWF", 9, 0, 10, 0), c.Placement.Time);
    }

    [Fact]
    public void One_day_options_are_checked_and_offer_distinct_weekdays()
    {
        var f = Fixture(); Send(f, "find one-day options for comp arch, no Fridays");
        Assert.NotNull(f.Chat.Proposal);
        var patterns = new HashSet<string>();
        foreach (var option in f.Chat.Suggestions.Where(s => s.StartsWith("Option ")).ToList())
        {
            Send(f, option);
            var c = f.Chat.Proposal!.ProposedChanges.Single(c => c.Placement == f.Plan.Placements[0]);
            Assert.Single(c.Time.Days); Assert.DoesNotContain(DayOfWeek.Friday, c.Time.Days);
            Assert.Equal(3, c.Time.Duration.TotalHours); Assert.True(c.Time.Start.TotalHours >= 9);
            Assert.True(patterns.Add(c.Time.Pattern));
        }
        Assert.True(patterns.Count >= 2);
        f.Planner.ApplyOverride(f.Plan, f.Chat.Proposal!);
        Assert.All(f.Plan.Placements, p => Assert.NotNull(p.Room));
    }

    [Theory]
    [InlineData("change comp arch days to Tuesday and Thursday", "TTH", 90)]
    [InlineData("change comp arch days to Monday through Thursday", "MTWTH", 45)]
    [InlineData("macnhge comp arch days to Friday", "F", 180)]
    [InlineData("change comp arch to one day Friday at the same start time", "F", 180)]
    public void Day_only_changes_preserve_weekly_hours(string request, string pattern, int minutes)
    {
        var f = Fixture(); Send(f, request);
        Assert.True(f.Chat.Proposal != null, Last(f.Chat));
        var c = f.Chat.Proposal!.ProposedChanges.Single(c => c.Placement == f.Plan.Placements[0]);
        Assert.Equal(pattern, c.Time.Pattern); Assert.Equal(minutes, c.Time.Duration.TotalMinutes);
        if (request.Contains("same start")) Assert.Equal(TimeSpan.FromHours(9), c.Time.Start);
    }

    [Theory]
    [InlineData("move comp arch to Friday afternoon", "F", 12, 17)]
    [InlineData("move comp arch to Tuesday morning", "T", 9, 12)]
    [InlineData("move comp arch to Thursday evening", "TH", 17, 21)]
    public void Named_periods_bound_the_entire_meeting(string request, string days, int start, int end)
    {
        var f = Fixture(); Send(f, request);
        Assert.True(f.Chat.Proposal != null, Last(f.Chat));
        var c = f.Chat.Proposal!.ProposedChanges.Single(c => c.Placement == f.Plan.Placements[0]);
        Assert.Equal(days, c.Time.Pattern); Assert.True(c.Time.Start.TotalHours >= start); Assert.True(c.Time.End.TotalHours <= end);
    }

    [Theory]
    [InlineData("move comp arch to Friday from 2 to 5pm", 14, 17)]
    [InlineData("move comp arch to Friday 14:00-16:00", 14, 16)]
    [InlineData("move comp arch to Friday afternoon at 2", 14, 15)]
    [InlineData("move comp arch to one day Friday at 8:15am", 8.25, 11.25)]
    public void Exact_ranges_and_explicit_early_starts_are_respected(string request, double start, double end)
    {
        var f = Fixture(); Send(f, request);
        Assert.True(f.Chat.Proposal != null, Last(f.Chat));
        var c = f.Chat.Proposal!.ProposedChanges.Single(c => c.Placement == f.Plan.Placements[0]);
        Assert.Equal("F", c.Time.Pattern); Assert.Equal(start, c.Time.Start.TotalHours); Assert.Equal(end, c.Time.End.TotalHours);
        if (end - start != 3) Assert.Contains("Weekly hours", Last(f.Chat));
    }

    [Fact]
    public void Ambiguous_clock_clears_old_preview_and_can_be_clarified()
    {
        var f = Fixture(); Send(f, "move comp arch to Friday at 3pm");
        Send(f, "change it to Tuesday at 2");
        Assert.Null(f.Chat.Proposal); Assert.Contains("am or pm", Last(f.Chat));
        Send(f, "at 2pm");
        Assert.True(f.Chat.Proposal != null, Last(f.Chat));
        Assert.Equal(MeetingTime.Of("T", 14, 0, 15, 0), f.Chat.Proposal!.ProposedChanges.First().Time);
    }

    [Theory]
    [InlineData("30 minutes later", 14.5)]
    [InlineData("half an hour later", 14.5)]
    [InlineData("one hour earlier", 13)]
    public void Relative_shifts_use_the_checked_preview(string refinement, double start)
    {
        var f = Fixture(); Send(f, "move comp arch to Friday at 2pm"); Send(f, refinement);
        Assert.True(f.Chat.Proposal != null, Last(f.Chat));
        var c = f.Chat.Proposal!.ProposedChanges.First();
        Assert.Equal("F", c.Time.Pattern); Assert.Equal(start, c.Time.Start.TotalHours); Assert.Equal(1, c.Time.Duration.TotalHours);
    }

    [Theory]
    [InlineData("change comp arch times to finish at 4pm", "MWF", 15, 16)]
    [InlineData("change comp arch to one day Friday finish at 4pm", "F", 13, 16)]
    [InlineData("move comp arch to Friday at 2pm for 90 minutes", "F", 14, 15.5)]
    public void End_times_and_meeting_durations_are_honored(string request, string days, double start, double end)
    {
        var f = Fixture(); Send(f, request);
        Assert.True(f.Chat.Proposal != null, Last(f.Chat));
        var c = f.Chat.Proposal!.ProposedChanges.First();
        Assert.Equal(days, c.Time.Pattern); Assert.Equal(start, c.Time.Start.TotalHours); Assert.Equal(end, c.Time.End.TotalHours);
    }

    [Theory]
    [InlineData("change comp arch to two days Friday", "named 1")]
    [InlineData("change comp arch to 6 days", "between 1 and 5")]
    [InlineData("move comp arch to Saturday at 2pm", "Monday through Friday")]
    [InlineData("move comp arch to Friday at 2pm for 0 minutes", "positive")]
    [InlineData("move comp arch to Friday 16:00-14:00", "end must be later")]
    public void Invalid_timing_requests_never_change_assignments(string request, string explanation)
    {
        var f = Fixture(); Send(f, request);
        Assert.Null(f.Chat.Proposal); Assert.Contains(explanation, Last(f.Chat));
        Assert.Equal(MeetingTime.Of("MWF", 9, 0, 10, 0), f.Plan.Placements[0].Time);
    }

    [Fact]
    public void New_period_replaces_previous_period_and_cancel_clears_day_count()
    {
        var f = Fixture(); Send(f, "move comp arch to Friday afternoon"); Send(f, "morning instead");
        Assert.True(f.Chat.Proposal != null, Last(f.Chat)); Assert.True(f.Chat.Proposal!.ProposedChanges.First().Time.End.TotalHours <= 12);
        Send(f, "make it one day a week"); Send(f, "cancel"); Send(f, "move data structures to Tuesday and Thursday at 2pm");
        Assert.True(f.Chat.Proposal != null, Last(f.Chat)); Assert.Equal("TTH", f.Chat.Proposal!.ProposedChanges.First().Time.Pattern);
    }
}
