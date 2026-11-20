namespace ClaudeUsageWidget.Tests;

public class ScheduleTests
{
    // 2026-11-17 is a Tuesday.
    private static DateTime Tue(int h, int m = 0) => new(2026, 11, 17, h, m, 0, DateTimeKind.Local);

    /// <summary>The resetsAt of a 5-hour window that started at this local time.</summary>
    private static long WindowFrom(DateTime localStart) => new DateTimeOffset(localStart).ToUnixTimeSeconds() + 5 * 3600;

    private static AutoHelloConfig Schedule(string times = "07:00, 12:00, 17:00", bool weekdaysOnly = true) =>
        new() { Mode = "schedule", Schedule = times, WeekdaysOnly = weekdaysOnly };

    [Fact]
    public void ParseSchedule_keeps_valid_times_sorted_and_once()
    {
        var times = AutoHello.ParseSchedule("17:00; 07:00, bad 25:00 12:00 07:00");
        Assert.Equal([TimeSpan.FromHours(7), TimeSpan.FromHours(12), TimeSpan.FromHours(17)], times);
    }

    [Fact]
    public void Due_when_a_start_time_passed_and_no_window_serves_it()
    {
        Assert.True(AutoHello.ScheduleDue(Schedule(), WindowFrom(Tue(7)), Tue(12, 30)));
    }

    [Fact]
    public void A_window_opened_up_to_an_hour_early_serves_the_start_time()
    {
        Assert.False(AutoHello.ScheduleDue(Schedule(), WindowFrom(Tue(11, 30)), Tue(12, 30)));
    }

    [Fact]
    public void Not_due_before_the_first_start_time()
    {
        Assert.False(AutoHello.ScheduleDue(Schedule(), 0, Tue(6, 59)));
    }

    [Fact]
    public void Due_with_no_window_at_all_once_a_time_passed()
    {
        Assert.True(AutoHello.ScheduleDue(Schedule(), 0, Tue(7, 1)));
    }

    [Fact]
    public void A_start_time_missed_yesterday_is_not_served_today()
    {
        // Last window Monday morning; Tuesday 06:00 has no start time behind it yet.
        Assert.False(AutoHello.ScheduleDue(Schedule(), WindowFrom(Tue(9).AddDays(-1)), Tue(6)));
    }

    [Fact]
    public void Weekends_are_skipped_when_asked()
    {
        var saturday = Tue(12, 30).AddDays(4);
        Assert.False(AutoHello.ScheduleDue(Schedule(), 0, saturday));
        Assert.True(AutoHello.ScheduleDue(Schedule(weekdaysOnly: false), 0, saturday));
    }

    [Theory]
    [InlineData("08:00", "20:00", 7, false)]
    [InlineData("08:00", "20:00", 8, true)]
    [InlineData("08:00", "20:00", 20, false)]
    [InlineData("22:00", "06:00", 23, true)]   // across midnight
    [InlineData("22:00", "06:00", 3, true)]
    [InlineData("22:00", "06:00", 12, false)]
    [InlineData("nonsense", "20:00", 3, true)] // unreadable hours don't block
    public void InAllowedHours(string from, string to, int hour, bool expected)
    {
        var cfg = new AutoHelloConfig { From = from, To = to, WeekdaysOnly = false };
        Assert.Equal(expected, AutoHello.InAllowedHours(cfg, Tue(hour)));
    }

    [Fact]
    public void ShouldStart_follows_the_mode()
    {
        var reset = new AutoHelloConfig { Mode = "reset", From = "08:00", To = "20:00" };
        Assert.True(AutoHello.ShouldStart(reset, 0, Tue(9)));
        Assert.False(AutoHello.ShouldStart(reset, 0, Tue(21)));

        Assert.False(AutoHello.ShouldStart(Schedule("10:00"), 0, Tue(9)));
        Assert.True(AutoHello.ShouldStart(Schedule("10:00"), 0, Tue(10)));
    }
}
