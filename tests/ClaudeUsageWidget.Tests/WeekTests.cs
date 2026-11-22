namespace ClaudeUsageWidget.Tests;

/// <summary>The weekly summary.</summary>
public class WeekTests
{
    // The week runs from Tuesday 17 Nov 2026 18:00 to Tuesday 24 Nov 18:00, local time.
    private static DateTime Local(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0, DateTimeKind.Local);
    private static long Unix(DateTime local) => new DateTimeOffset(local).ToUnixTimeSeconds();
    private static readonly long Reset = Unix(Local(24, 18));
    private static readonly long LastReset = Reset - 7 * 86400;

    private static Sample S(DateTime at, double p7, long? r7 = null, double p5 = 10, long r5 = 0) =>
        new() { T = Unix(at), P7 = p7, R7 = r7 ?? Reset, P5 = p5, R5 = r5 == 0 ? Unix(at) + 3600 : r5 };

    private static Breakdown Use(params (string Name, double Cost)[] projects) =>
        new(projects.Sum(p => p.Cost), projects.Select(p => new ProjectUse(p.Name, p.Name, p.Cost, [])).ToList());

    private static readonly Sample[] AWeek =
    [
        S(Local(17, 17), 95, r7: LastReset),   // the week before: not this one
        S(Local(17, 19), 1),
        S(Local(17, 23), 4),
        S(Local(18, 10), 9),
        S(Local(18, 22), 22),    // Wednesday: 4 -> 22 = 18
        S(Local(19, 12), 30),    // Thursday: 8
        S(Local(23, 15), 41),    // Monday: 11 (Fri, Sat, Sun nothing)
        S(Local(24, 17), 45),    // Tuesday: 4
    ];

    [Fact]
    public void Each_day_gets_what_the_weekly_limit_grew_by_that_day()
    {
        var week = WeekReport.Build(AWeek, Reset, Use(), Unix(Local(24, 20)))!;
        Assert.Equal(45, week.Used);
        Assert.Equal(8, week.Days.Count);   // Tuesday evening to the next Tuesday afternoon
        Assert.Equal([4, 18, 8, 0, 0, 0, 11, 4], week.Days.Select(d => d.Percent));
        Assert.Equal(Local(18, 0), week.Heaviest!.Date);
        Assert.False(week.Gaps);
    }

    [Fact]
    public void A_week_still_running_stops_at_now()
    {
        var week = WeekReport.Build(AWeek[..5], Reset, Use(), Unix(Local(19, 9)))!;
        Assert.Equal(3, week.Days.Count);   // Tuesday, Wednesday, Thursday so far
        Assert.Equal(22, week.Used);
        Assert.Equal(0, week.Days[^1].Percent);
    }

    [Fact]
    public void Projects_are_shares_with_the_small_ones_together()
    {
        var week = WeekReport.Build(AWeek, Reset, Use(("A", 50), ("B", 20), ("C", 10), ("D", 10), ("E", 6), ("F", 4)), Unix(Local(24, 20)))!;
        Assert.Equal(["A", "B", "C", "D", "outros"], week.Projects.Select(p => p.Name));
        Assert.Equal(0.5, week.Projects[0].Share, precision: 6);
        Assert.Equal(0.1, week.Projects[^1].Share, precision: 6);
    }

    [Fact]
    public void Five_hour_windows_that_ran_out_are_counted_once_each()
    {
        var r1 = Unix(Local(18, 15));
        var r2 = Unix(Local(19, 20));
        Sample[] samples =
        [
            S(Local(18, 11), 10, p5: 80, r5: r1), S(Local(18, 12), 12, p5: 100, r5: r1), S(Local(18, 13), 14, p5: 100, r5: r1),
            S(Local(19, 16), 20, p5: 99.7, r5: r2),
            S(Local(20, 10), 25, p5: 40),
        ];
        Assert.Equal(2, WeekReport.Build(samples, Reset, Use(), Unix(Local(24, 20)))!.FiveHourMaxed);
    }

    [Fact]
    public void A_widget_off_at_the_start_leaves_the_first_days_unknown()
    {
        Sample[] samples = [S(Local(20, 9), 30), S(Local(20, 18), 34)];
        var week = WeekReport.Build(samples, Reset, Use(), Unix(Local(24, 20)))!;
        Assert.True(week.Gaps);
        Assert.Equal(34, week.Days.Single(d => d.Date == Local(20, 0)).Percent);
    }

    [Fact]
    public void No_readings_no_summary()
    {
        Assert.Null(WeekReport.Build([S(Local(17, 17), 95, r7: LastReset)], Reset, Use(), Unix(Local(24, 20))));
    }

    // ---- the words ---------------------------------------------------------------------

    [Fact]
    public void The_message_says_how_the_week_went()
    {
        var week = WeekReport.Build(AWeek, Reset, Use(("ClaudeUsageWidget", 70), ("PadelManager", 30)), Unix(Local(24, 20)))!;
        week.FiveHourMaxed = 2;
        Assert.Equal("Usaste 45% do limite semanal (na anterior, 62%). Dia mais pesado: quarta (18%). " +
                     "Mais usado: ClaudeUsageWidget (70%). A janela de 5h esgotou 2 vezes.",
            WeekReport.Message(week, new WeekSummary { Used = 62 }));
    }

    [Fact]
    public void A_quiet_week_says_little()
    {
        var week = new WeekSummary { Used = 0.4, Days = [new(Local(18, 0), 0.4)] };
        Assert.Equal("Usaste <1% do limite semanal.", WeekReport.Message(week, null));
    }

    [Theory]
    [InlineData(18, "quarta", "qua 18")]
    [InlineData(17, "terça", "ter 17")]
    [InlineData(22, "domingo", "dom 22")]
    public void Days_in_Portuguese(int day, string name, string label)
    {
        Assert.Equal(name, WeekReport.DayName(Local(day, 0)));
        Assert.Equal(label, WeekReport.DayLabel(Local(day, 0)));
    }

    // ---- keeping them --------------------------------------------------------------------

    private static Breakdown NoUse(long from, long to) => Breakdown.Empty;

    [Fact]
    public void A_week_is_summed_up_once_when_the_next_one_begins()
    {
        var log = new WeekLog(new WeeksFile(), null);
        var running = AWeek.ToList();
        Assert.Null(log.Close(running, NoUse, Unix(Local(24, 17))));   // still the same week... the one before is old

        // The first reading of the new week.
        running.Add(S(Local(24, 18, 30), 1, r7: Reset + 7 * 86400));
        var now = Unix(Local(24, 18, 31));
        Assert.Equal(Reset, log.Unsummed(running, now));
        var week = log.Close(running, NoUse, now);
        Assert.Equal(Reset, week!.ResetAt);
        Assert.Equal(0, log.Unsummed(running, now));
        Assert.Null(log.Close(running, NoUse, now + 60));
        Assert.Equal(Reset, log.Before(Reset + 7 * 86400)!.ResetAt);
    }

    [Fact]
    public void A_week_found_ended_long_ago_is_kept_quietly()
    {
        var log = new WeekLog(new WeeksFile(), null);
        Sample[] samples = [S(Local(18, 10), 9), S(Local(26, 10), 3, r7: Reset + 7 * 86400)];
        Assert.Null(log.Close(samples, NoUse, Unix(Local(26, 10))));   // two days late: no notification
        Assert.Single(log.Weeks);
    }

    // ---- where the use came from, by /usage ------------------------------------------------

    private static WeekSources Sources(long start, long asOf) =>
        new(start, asOf, [new("claude_code", "Claude Code", 28, 94), new("cowork", "Cowork", 2, 6)]);

    [Fact]
    public void Usage_breakdown_goes_with_its_own_week_and_only_a_newer_one_replaces_it()
    {
        var week = WeekReport.Build(AWeek, Reset, Use(), Unix(Local(22, 21)))!;
        Assert.False(WeekReport.Attach(week, Sources(LastReset - 7 * 86400, Unix(Local(16, 12)))));   // the week before's
        Assert.Empty(week.Sources);

        // /usage's week starts a fraction of a second after the reset.
        Assert.True(WeekReport.Attach(week, Sources(LastReset + 1, Unix(Local(22, 21)))));
        Assert.Equal(["Claude Code", "Cowork"], week.Sources.Select(s => s.Name));
        Assert.False(WeekReport.Attach(week, Sources(LastReset, Unix(Local(20, 9)))));   // older
        Assert.Equal(Unix(Local(22, 21)), week.SourcesAsOf);
    }

    [Fact]
    public void The_message_says_what_went_outside_Claude_Code()
    {
        var week = new WeekSummary { Used = 30, Sources = Sources(LastReset, 1).Rows };
        Assert.Equal("Usaste 30% do limite semanal. Fora do Claude Code: 2% (Cowork).", WeekReport.Message(week, null));
    }

    [Fact]
    public void A_closed_week_keeps_what_usage_said_of_it()
    {
        var path = Path.Combine(Isolation.Root, $"weeks-{Guid.NewGuid():N}.json");
        var samples = AWeek.Append(S(Local(24, 18, 30), 1, r7: Reset + 7 * 86400)).ToList();
        new WeekLog(path).Close(samples, NoUse, Unix(Local(24, 18, 31)), Sources(LastReset, Unix(Local(24, 9))));

        var kept = new WeekLog(path).Weeks.Single();
        Assert.Equal(["Claude Code", "Cowork"], kept.Sources.Select(s => s.Name));
        Assert.Equal(2, kept.Sources[1].Points);
        Assert.Equal(Unix(Local(24, 9)), kept.SourcesAsOf);
    }

    [Fact]
    public void Only_the_last_12_weeks_are_kept()
    {
        var file = new WeeksFile { Weeks = Enumerable.Range(1, 12).Select(i => new WeekSummary { ResetAt = LastReset - i * 7 * 86400 }).ToList() };
        var log = new WeekLog(file, null);
        log.Close([S(Local(18, 10), 9), S(Local(24, 19), 1, r7: Reset + 7 * 86400)], NoUse, Unix(Local(24, 19)));
        Assert.Equal(12, log.Weeks.Count);
        Assert.Equal(Reset, log.Weeks[^1].ResetAt);
    }
}
