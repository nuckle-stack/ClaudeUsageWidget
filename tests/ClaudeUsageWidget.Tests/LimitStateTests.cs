namespace ClaudeUsageWidget.Tests;

/// <summary>Used-up limits: paid extra usage, the ping that finds out about it, and the renewal.</summary>
public class LimitStateTests
{
    private const long Hour = 3600, Day = 86400;
    private static readonly long Now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static Sample Five(long t, double pct, long resetsAt) => new() { T = t, P5 = pct, R5 = resetsAt };

    // ---- renewal ------------------------------------------------------------

    [Fact]
    public void A_used_up_window_announces_its_renewal_once()
    {
        var reset = Now - 60;   // reset a minute ago
        var history = new UsageHistory(new HistoryFile { Samples = [Five(reset - Hour, 80, reset), Five(reset - 600, 100, reset)] });

        var (title, _) = Assert.Single(history.PendingRenewals(Now));
        Assert.Equal("Limite de 5 horas renovado", title);
        Assert.Empty(history.PendingRenewals(Now));
        Assert.Empty(history.PendingRenewals(Now + 60));
    }

    [Fact]
    public void Nothing_before_the_reset()
    {
        var reset = Now + 600;
        var history = new UsageHistory(new HistoryFile { Samples = [Five(Now, 100, reset)] });
        Assert.Empty(history.PendingRenewals(Now));
        Assert.Single(history.PendingRenewals(reset + 1));
    }

    [Fact]
    public void A_window_that_never_ran_out_renews_quietly()
    {
        var history = new UsageHistory(new HistoryFile { Samples = [Five(Now - Hour, 97, Now - 60)] });
        Assert.Empty(history.PendingRenewals(Now));
    }

    [Fact]
    public void Rounding_just_below_100_still_counts()
    {
        var history = new UsageHistory(new HistoryFile { Samples = [Five(Now - Hour, 99.6, Now - 60)] });
        Assert.Single(history.PendingRenewals(Now));
    }

    [Fact]
    public void A_reset_long_past_is_not_news()
    {
        // The widget was off when the window came back: stay quiet, and stay quiet after.
        var file = new HistoryFile { Samples = [Five(Now - 3 * Hour, 100, Now - 2 * Hour)] };
        var history = new UsageHistory(file);
        Assert.Empty(history.PendingRenewals(Now));
        Assert.Equal(Now - 2 * Hour, file.Renewed5);
    }

    [Fact]
    public void A_new_window_already_started_does_not_hide_the_renewal()
    {
        // Back to work within seconds of the reset: the newest reading is the
        // new window, but the used-up one before it still gets its message.
        var reset = Now - 30;
        var next = reset + 5 * Hour;
        var history = new UsageHistory(new HistoryFile { Samples = [Five(reset - 600, 100, reset), Five(Now, 1, next)] });
        Assert.Single(history.PendingRenewals(Now));
    }

    [Fact]
    public void The_weekly_limit_renews_too()
    {
        var reset = Now - 60;
        var history = new UsageHistory(new HistoryFile
        {
            Samples = [new Sample { T = reset - Day, P5 = 10, R5 = reset + Hour, P7 = 100, R7 = reset }],
        });
        Assert.Equal("Limite semanal renovado", Assert.Single(history.PendingRenewals(Now)).Title);
    }

    // ---- extra usage --------------------------------------------------------

    private static Limits Ping(bool usingOverage, string type = "five_hour", long? resetsAt = null) => new()
    {
        UpdatedAt = Now,
        FiveHour = new LimitWindow { UsedPercentage = 100, ResetsAt = Now + Hour },
        Overage = new OverageInfo { Using = usingOverage, Status = "allowed", LimitType = type, ResetsAt = resetsAt ?? Now + Hour },
    };

    [Fact]
    public void Extra_usage_is_announced_once_per_window()
    {
        var watch = new OverageWatch();
        var alert = watch.Check(Ping(true), Now);

        Assert.Equal("Uso extra pago ativo", alert!.Value.Title);
        Assert.StartsWith("O limite de 5 horas esgotou", alert.Value.Text);
        Assert.Null(watch.Check(Ping(true), Now + 60));

        // The next window that runs out is news again.
        Assert.NotNull(watch.Check(Ping(true, resetsAt: Now + 6 * Hour), Now + 2 * Hour));
    }

    [Fact]
    public void No_alert_without_extra_usage_or_once_it_is_over()
    {
        var watch = new OverageWatch();
        Assert.Null(watch.Check(Ping(false), Now));
        Assert.Null(watch.Check(Ping(true, resetsAt: Now - 1), Now));
        Assert.Null(watch.Check(new Limits(), Now));
    }

    [Fact]
    public void Names_the_weekly_limit()
    {
        var alert = new OverageWatch().Check(Ping(true, "seven_day", Now + 3 * Day), Now);
        Assert.StartsWith("O limite semanal esgotou", alert!.Value.Text);
    }

    // ---- the ping on reaching 100% -------------------------------------------

    [Fact]
    public void FullWindow_is_the_reset_of_a_running_used_up_window()
    {
        var reading = new Limits
        {
            FiveHour = new LimitWindow { UsedPercentage = 40, ResetsAt = Now + Hour },
            SevenDay = new LimitWindow { UsedPercentage = 100, ResetsAt = Now + 2 * Day },
        };
        Assert.Equal(Now + 2 * Day, AutoHello.FullWindow(reading));

        reading.SevenDay.UsedPercentage = 99;
        Assert.Null(AutoHello.FullWindow(reading));

        // Expired: the limit is back, nothing to ask.
        reading.FiveHour = new LimitWindow { UsedPercentage = 100, ResetsAt = Now - 1 };
        Assert.Null(AutoHello.FullWindow(reading));
    }
}
