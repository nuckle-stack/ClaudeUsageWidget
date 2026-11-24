namespace ClaudeUsageWidget.Tests;

public class ForecastTests
{
    private const long Hour = 3600, Day = 86400;
    private static readonly long Now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static Sample Five(long t, double pct, long resetsAt) => new() { T = t, P5 = pct, R5 = resetsAt };
    private static Sample Week(long t, double pct, long resetsAt) =>
        new() { T = t, P5 = 0, R5 = t + Hour, P7 = pct, R7 = resetsAt };

    private static UsageHistory With(params Sample[] samples) => new(new HistoryFile { Samples = samples.ToList() });

    [Fact]
    public void No_readings_no_projection()
    {
        Assert.Null(With().Project(fiveHour: true));
    }

    [Fact]
    public void Five_hour_pace_from_the_last_hour_runs_out_before_the_reset()
    {
        // 10% -> 30% in 30 minutes = 40%/h; 70% left = 1h45, reset in 3h.
        var reset = Now + 3 * Hour;
        var p = With(Five(Now - 1800, 10, reset), Five(Now, 30, reset)).Project(true)!;

        Assert.True(p.Reliable);
        Assert.Equal(30, p.Percent);
        Assert.Equal(100, p.AtReset);
        Assert.Equal(Now + (long)(1.75 * Hour), p.ExhaustAt!.Value.ToUnixTimeSeconds(), tolerance: 1);
    }

    [Fact]
    public void Slow_pace_lands_below_100_at_the_reset()
    {
        // 10% -> 12% in an hour = 2%/h; reset in 4h -> 20%.
        var reset = Now + 4 * Hour;
        var p = With(Five(Now - Hour, 10, reset), Five(Now, 12, reset)).Project(true)!;

        Assert.Null(p.ExhaustAt);
        Assert.Equal(20, p.AtReset, precision: 6);
    }

    [Fact]
    public void A_single_reading_is_a_guess_from_when_the_window_opened()
    {
        // Window opened 2h ago (5h window, reset in 3h), 20% used: 10%/h -> 50% at reset.
        var reset = Now + 3 * Hour;
        var p = With(Five(Now, 20, reset)).Project(true)!;

        Assert.False(p.Reliable);
        Assert.Equal(50, p.AtReset, precision: 6);
    }

    [Fact]
    public void Readings_from_the_previous_window_are_ignored()
    {
        // Last window ended at 90%; the new one started at 0 and is at 5% after an hour.
        var oldReset = Now - 600;
        var reset = Now + 4 * Hour;
        var p = With(Five(Now - 2 * Hour, 90, oldReset), Five(Now - Hour, 0, reset), Five(Now, 5, reset)).Project(true)!;

        Assert.True(p.Reliable);
        Assert.Null(p.ExhaustAt);
        Assert.Equal(25, p.AtReset, precision: 6);
    }

    [Fact]
    public void Usage_going_down_counts_as_no_use()
    {
        var reset = Now + 3 * Hour;
        var p = With(Five(Now - Hour, 40, reset), Five(Now, 35, reset)).Project(true)!;

        Assert.Null(p.ExhaustAt);
        Assert.Equal(35, p.AtReset);
    }

    [Fact]
    public void Exhausted_now_when_at_100()
    {
        var reset = Now + Hour;
        var p = With(Five(Now - 1800, 90, reset), Five(Now, 100, reset)).Project(true)!;

        Assert.Equal(Now, p.ExhaustAt!.Value.ToUnixTimeSeconds());
        Assert.Equal("esgotado", With(Five(Now - 1800, 90, reset), Five(Now, 100, reset)).Describe(true));
    }

    [Fact]
    public void Weekly_pace_is_not_taken_from_less_than_a_day()
    {
        // The false alarm of 2026-11-19: 1% -> 2% in one hour, carried on for the
        // 5 days left, "ran out" in 4 days. Under a day of data, the weekly pace is
        // the average since the window opened: 2% in 2 days -> 7% at the reset.
        var reset = Now + 5 * Day;
        var p = With(Week(Now - Hour, 1, reset), Week(Now, 2, reset)).Project(false)!;

        Assert.False(p.Reliable);
        Assert.Null(p.ExhaustAt);
        Assert.Equal(7, p.AtReset, precision: 6);
    }

    [Fact]
    public void Weekly_pace_uses_a_full_day_when_there_is_one()
    {
        // 10% -> 40% in a day = 30%/day; 60% left = 2 days, reset in 4.
        var reset = Now + 4 * Day;
        var p = With(Week(Now - Day, 10, reset), Week(Now, 40, reset)).Project(false)!;

        Assert.True(p.Reliable);
        Assert.Equal(Now + 2 * Day, p.ExhaustAt!.Value.ToUnixTimeSeconds(), tolerance: 1);
    }

    [Fact]
    public void Level_alerts_are_said_once_per_level_and_window()
    {
        var reset = Now + 4 * Hour;
        var cfg = new UsageAlertsConfig { Levels = [80, 95], Forecast = false };
        var file = new HistoryFile { Samples = [Five(Now - Hour, 79, reset), Five(Now, 81, reset)] };
        var history = new UsageHistory(file);

        var at81 = Assert.Single(history.PendingAlerts(cfg));
        Assert.Equal("Limite de 5 horas a 81%", at81.Title);
        // Its notice: under the limit's name, with the 5-hour bar, yellow.
        Assert.Equal(("Limite de 5 horas", "Já usaste 81%", true, NoticeTone.Warn), (at81.Kind, at81.Subject, at81.FiveHour, at81.Tone));
        Assert.Empty(history.PendingAlerts(cfg));

        file.Samples.Add(Five(Now + 1, 96, reset));
        var at96 = Assert.Single(history.PendingAlerts(cfg));
        Assert.Equal("Limite de 5 horas a 96%", at96.Title);
        Assert.Equal(NoticeTone.Crit, at96.Tone);
        Assert.Empty(history.PendingAlerts(cfg));
    }

    [Fact]
    public void A_new_window_arms_the_alerts_again()
    {
        var cfg = new UsageAlertsConfig { Levels = [80], Forecast = false };
        var first = Now + 600;
        var file = new HistoryFile { Samples = [Five(Now, 85, first)] };
        var history = new UsageHistory(file);
        Assert.Single(history.PendingAlerts(cfg));

        var next = Now + 5 * Hour;
        file.Samples.Add(Five(Now + 1, 82, next));
        Assert.Single(history.PendingAlerts(cfg));
    }

    [Fact]
    public void Pace_alert_needs_a_reliable_pace_and_is_said_once()
    {
        var reset = Now + 3 * Hour;
        var cfg = new UsageAlertsConfig { Levels = [], Forecast = true };

        // One reading: only a guess, no alert even though it projects past 100%.
        var guess = new UsageHistory(new HistoryFile { Samples = [Five(Now, 60, reset)] });
        Assert.Empty(guess.PendingAlerts(cfg));

        var history = new UsageHistory(new HistoryFile { Samples = [Five(Now - 1800, 10, reset), Five(Now, 30, reset)] });
        var pace = Assert.Single(history.PendingAlerts(cfg));
        Assert.Equal("Limite de 5 horas: ritmo alto", pace.Title);
        Assert.Equal(("Ritmo alto", NoticeTone.Warn), (pace.Subject, pace.Tone));
        Assert.Empty(history.PendingAlerts(cfg));
    }

    [Fact]
    public void Estimated_and_repeated_readings_are_not_recorded()
    {
        var history = new UsageHistory(new HistoryFile());
        var reading = new Limits { UpdatedAt = Now, FiveHour = new LimitWindow { UsedPercentage = 5, ResetsAt = Now + Hour } };

        Assert.False(history.Add(new Limits { UpdatedAt = Now, Estimated = true, FiveHour = reading.FiveHour }));
        Assert.True(history.Add(reading));
        Assert.False(history.Add(reading));
    }
}
