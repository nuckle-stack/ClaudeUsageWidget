namespace ClaudeUsageWidget.Tests;

/// <summary>
/// Much of the week still unused, two days before it renews. Each account's
/// week ends on its own day and hour (whatever its readings say), so the
/// cases use several.
/// </summary>
public class LeftoverTests
{
    private static DateTime Local(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0, DateTimeKind.Local);
    private static long Unix(DateTime local) => new DateTimeOffset(local).ToUnixTimeSeconds();

    private static readonly UsageAlertsConfig Weekdays = new() { LeftoverAbove = 40, BudgetWeekdaysOnly = true, DayStartHour = 4 };
    private static readonly UsageAlertsConfig EveryDay = new() { LeftoverAbove = 40, BudgetWeekdaysOnly = false, DayStartHour = 4 };

    /// <summary>One reading, taken an hour before "now", of a week that resets at reset.</summary>
    private static UsageHistory At(DateTime now, double used, DateTime reset) =>
        new(new HistoryFile { Samples = [new Sample { T = Unix(now) - 3600, P5 = 1, R5 = Unix(now) + 3600, P7 = used, R7 = Unix(reset) }] });

    [Fact]
    public void The_day_before_a_Tuesday_evening_reset_it_says_what_is_left_once()
    {
        var history = At(Local(23, 10), 37, Local(24, 18));
        var (title, text) = history.PendingLeftoverAlert(Local(23, 10), Weekdays)!.Value;
        Assert.Equal("Ainda tens 63% do limite semanal", title);
        Assert.Equal("Renova amanhã às 18:00 e o que sobrar não passa para a semana seguinte.", text);
        Assert.Null(history.PendingLeftoverAlert(Local(23, 15), Weekdays));
        Assert.Null(history.PendingLeftoverAlert(Local(24, 9), Weekdays));
    }

    [Theory]
    // Monday 30 at 09:00, working days only: Thursday and Friday are the last two (Monday morning is no day to work in).
    [InlineData(26, 10, 30, 9, true, "segunda às 09:00")]
    [InlineData(25, 10, 30, 9, true, null)]
    // The same reset counting every day: Saturday and Sunday.
    [InlineData(28, 10, 30, 9, false, "segunda às 09:00")]
    [InlineData(27, 10, 30, 9, false, null)]
    // Friday 27 at 17:00: Thursday and Friday.
    [InlineData(26, 9, 27, 17, true, "amanhã às 17:00")]
    [InlineData(25, 23, 27, 17, true, null)]
    // Sunday 29 at 20:00, every day: Saturday and Sunday.
    [InlineData(28, 8, 29, 20, false, "amanhã às 20:00")]
    // Thursday 26 at 02:00: the night belongs to Wednesday, so Tuesday and Wednesday.
    [InlineData(24, 12, 26, 2, true, "quinta às 02:00")]
    public void Two_days_before_each_account_s_own_reset(int day, int hour, int resetDay, int resetHour, bool weekdaysOnly, string? when)
    {
        var reset = Local(resetDay, resetHour);
        var alert = At(Local(day, hour), 20, reset).PendingLeftoverAlert(Local(day, hour), weekdaysOnly ? Weekdays : EveryDay);
        if (when is null) Assert.Null(alert);
        else Assert.Equal($"Renova {when} e o que sobrar não passa para a semana seguinte.", alert!.Value.Text);
    }

    [Fact]
    public void A_widget_started_on_the_last_day_still_says_it()
    {
        var (_, text) = At(Local(24, 9), 30, Local(24, 18)).PendingLeftoverAlert(Local(24, 9), Weekdays)!.Value;
        Assert.StartsWith("Renova hoje às 18:00", text);
    }

    [Theory]
    [InlineData(61, 10)]   // 39% left: under the threshold
    [InlineData(20, 17)]   // 17:30, half an hour before the reset: too late to be of use
    public void It_stays_quiet_otherwise(double used, int hour)
    {
        Assert.Null(At(Local(24, hour, 30), used, Local(24, 18)).PendingLeftoverAlert(Local(24, hour, 30), Weekdays));
    }

    [Fact]
    public void Zero_turns_it_off()
    {
        Assert.Null(At(Local(23, 10), 0, Local(24, 18)).PendingLeftoverAlert(Local(23, 10), new UsageAlertsConfig { LeftoverAbove = 0 }));
    }

    [Fact]
    public void Each_week_gets_its_own()
    {
        var file = new HistoryFile { Samples = [new Sample { T = Unix(Local(23, 9)), P7 = 10, R7 = Unix(Local(24, 18)) }] };
        var history = new UsageHistory(file);
        Assert.NotNull(history.PendingLeftoverAlert(Local(23, 10), Weekdays));

        // The next week, read a few seconds off (the reset moves a little between readings).
        file.Samples.Add(new Sample { T = Unix(Local(30, 9)), P7 = 5, R7 = Unix(Local(24, 18)) + 7 * 86400 + 3 });
        Assert.NotNull(history.PendingLeftoverAlert(Local(30, 10), Weekdays));
        Assert.Null(history.PendingLeftoverAlert(Local(30, 11), Weekdays));
    }
}
