namespace ClaudeUsageWidget.Tests;

/// <summary>Today's share of the weekly limit.</summary>
public class BudgetTests
{
    // Wednesday 2026-11-18, local time; the week resets on Tuesday the 24th at 18:00.
    private static DateTime Local(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0, DateTimeKind.Local);
    private static long Unix(DateTime local) => new DateTimeOffset(local).ToUnixTimeSeconds();
    private static readonly long Reset = Unix(Local(24, 18));

    private static Sample Week(DateTime at, double pct, long? reset = null) =>
        new() { T = Unix(at), P5 = 1, R5 = Unix(at) + 3600, P7 = pct, R7 = reset ?? Reset };

    private static UsageHistory With(params Sample[] samples) => new(new HistoryFile { Samples = samples.ToList() });

    [Fact]
    public void Shares_what_was_left_at_midnight_over_the_working_days()
    {
        // 20% used by Tuesday night, 26% now. Days: Wed (today), Thu, Fri, Mon, Tue (reset at 18:00) = 5.
        var b = With(Week(Local(17, 23), 20), Week(Local(18, 10), 26)).Budget(Local(18, 10), weekdaysOnly: true)!;

        Assert.Equal(5, b.Days);
        Assert.Equal(16, b.Allowance, precision: 6);
        Assert.Equal(6, b.UsedToday, precision: 6);
        Assert.True(b.Reliable);
        Assert.False(b.Over);
        Assert.Equal("Hoje: 6% de 16% do limite semanal", UsageHistory.BudgetText(b));
    }

    [Fact]
    public void Weekends_count_when_asked()
    {
        var b = With(Week(Local(17, 23), 20), Week(Local(18, 10), 26)).Budget(Local(18, 10), weekdaysOnly: false)!;
        Assert.Equal(7, b.Days);
        Assert.Equal(80.0 / 7, b.Allowance, precision: 6);
    }

    [Theory]
    [InlineData(18, 10, 18, true, 5)]    // Wed -> Tue 18:00: Wed Thu Fri Mon Tue
    [InlineData(21, 10, 18, true, 3)]    // a Saturday: today counts anyway, then Mon, Tue
    [InlineData(24, 9, 18, true, 1)]     // the reset day itself
    [InlineData(18, 10, 9, true, 4)]     // reset Tue 09:00: Tuesday is not a day to work in
    [InlineData(18, 10, 18, false, 7)]
    public void DaysToShare(int today, int hour, int resetHour, bool weekdaysOnly, int expected)
    {
        Assert.Equal(expected, UsageHistory.DaysToShare(Local(today, hour), Local(24, resetHour), weekdaysOnly));
    }

    [Fact]
    public void A_week_that_began_today_starts_from_zero()
    {
        // Reset this Tuesday at 18:00; the new week runs to next Tuesday.
        var next = Unix(Local(24, 18)) + 7 * 86400;
        var b = With(Week(Local(24, 19), 3, next)).Budget(Local(24, 20), weekdaysOnly: false)!;

        Assert.True(b.Reliable);
        Assert.Equal(3, b.UsedToday, precision: 6);
        Assert.Equal(100.0 / 8, b.Allowance, precision: 6);   // today + 7 days to next Tuesday 18:00
    }

    [Fact]
    public void Without_a_reading_from_before_midnight_it_is_an_estimate()
    {
        // The widget was off overnight: today starts from its first reading.
        var b = With(Week(Local(18, 9), 30), Week(Local(18, 11), 34)).Budget(Local(18, 11), weekdaysOnly: true)!;

        Assert.False(b.Reliable);
        Assert.Equal(4, b.UsedToday, precision: 6);
        Assert.StartsWith("Hoje: ≈ 4%", UsageHistory.BudgetText(b));
    }

    [Fact]
    public void Readings_of_the_previous_week_do_not_count()
    {
        // The previous week ended yesterday at 18:00 at 90%; this one was at 2% by night.
        var lastWeek = Reset - 7 * 86400;
        var b = With(Week(Local(17, 17), 90, lastWeek), Week(Local(17, 23), 2), Week(Local(18, 10), 5))
            .Budget(Local(18, 10), weekdaysOnly: true)!;
        Assert.True(b.Reliable);
        Assert.Equal(3, b.UsedToday, precision: 6);
        Assert.Equal(98.0 / 5, b.Allowance, precision: 6);
    }

    [Fact]
    public void No_budget_without_a_running_week()
    {
        Assert.Null(With().Budget(Local(18, 10), true));
        Assert.Null(With(Week(Local(17, 10), 50, Unix(Local(18, 9)))).Budget(Local(18, 10), true));
        Assert.Equal("", UsageHistory.BudgetText(null));
    }

    [Fact]
    public void Tiny_numbers_read_as_under_one_percent()
    {
        var b = With(Week(Local(17, 23), 20), Week(Local(18, 10), 20.3)).Budget(Local(18, 10), true)!;
        Assert.Equal("Hoje: <1% de 16% do limite semanal", UsageHistory.BudgetText(b));
    }

    // ---- the alert ------------------------------------------------------------

    private static readonly UsageAlertsConfig On = new() { DailyBudget = true, BudgetWeekdaysOnly = true };

    [Fact]
    public void Over_budget_is_announced_once_a_day()
    {
        var file = new HistoryFile { Samples = [Week(Local(17, 23), 20), Week(Local(18, 10), 38)] };
        var history = new UsageHistory(file);

        var (title, text) = history.PendingBudgetAlert(Local(18, 10), On)!.Value;
        Assert.Equal("Parte de hoje do limite semanal gasta", title);
        Assert.Contains("Hoje já usaste 18% do limite semanal, mais do que os 16%", text);
        Assert.Contains("próximos 4 dias", text);
        Assert.Null(history.PendingBudgetAlert(Local(18, 15), On));

        // Thursday: a new day, judged afresh (38% at midnight, 62 over 4 days = 15.5% each).
        file.Samples.Add(Week(Local(19, 12), 55));
        Assert.NotNull(history.PendingBudgetAlert(Local(19, 12), On));
    }

    [Fact]
    public void No_alert_under_budget_when_off_or_on_an_estimate()
    {
        var under = new UsageHistory(new HistoryFile { Samples = [Week(Local(17, 23), 20), Week(Local(18, 10), 30)] });
        Assert.Null(under.PendingBudgetAlert(Local(18, 10), On));

        var over = new HistoryFile { Samples = [Week(Local(17, 23), 20), Week(Local(18, 10), 60)] };
        Assert.Null(new UsageHistory(over).PendingBudgetAlert(Local(18, 10), new UsageAlertsConfig { DailyBudget = false }));

        var guess = new UsageHistory(new HistoryFile { Samples = [Week(Local(18, 9), 20), Week(Local(18, 11), 60)] });
        Assert.Null(guess.PendingBudgetAlert(Local(18, 11), On));
    }
}
