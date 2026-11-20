using System.Diagnostics;

namespace ClaudeUsageWidget.Tests;

/// <summary>Noticing that Claude Code changed the files the widget reads.</summary>
public class FormatWatchTests
{
    private const long Now = 1_795_200_000;
    private static readonly SessionScanner.Health Fine = new(3, 3, 2, 2, "2.1.328");

    private static FormatObservations Seen(
        SessionScanner.Health? sessions = null, (int, long)? withoutUsage = null, long lastUsedAt = Now - 30,
        FeedKind feed = FeedKind.Widget, (long, long?)[]? cli = null, long limitsAt = Now - 10, bool helloLost = false) =>
        new(sessions ?? Fine, withoutUsage ?? (0, 2_000), lastUsedAt, feed,
            cli ?? [(Now - 300, Now - 290)], limitsAt, helloLost);

    private static string[] Keys(FormatObservations o) => FormatWatch.Check(o, Now).Select(p => p.Key).ToArray();

    [Fact]
    public void A_healthy_machine_has_nothing_to_report()
    {
        Assert.Empty(Keys(Seen()));
    }

    [Fact]
    public void A_quiet_machine_is_not_a_broken_one()
    {
        // No sessions, nothing answered for hours, no statusline runs: all as expected.
        Assert.Empty(Keys(Seen(sessions: new(0, 0, 0, 0, ""), lastUsedAt: Now - 7200, cli: [], limitsAt: Now - 7200)));
    }

    // ---- session files -----------------------------------------------------------------

    [Theory]
    [InlineData(2, 0, 0, 0)]   // files without pid or sessionId
    [InlineData(2, 2, 1, 0)]   // a live session without a status
    public void Session_files_without_the_fields_the_widget_reads(int files, int withIds, int alive, int withStatus)
    {
        Assert.Equal(["sessions"], Keys(Seen(sessions: new(files, withIds, alive, withStatus, "2.1.340"))));
    }

    [Fact]
    public void Answers_but_no_session_files_means_they_moved()
    {
        Assert.Equal(["sessions-missing"], Keys(Seen(sessions: new(0, 0, 0, 0, ""), lastUsedAt: Now - 30, cli: [])));
        Assert.Empty(Keys(Seen(sessions: new(0, 0, 0, 0, ""), lastUsedAt: Now - 600, cli: [])));
    }

    // ---- transcripts ----------------------------------------------------------------------

    [Theory]
    [InlineData(20, 50_000, true)]
    [InlineData(19, 50_000, false)]
    [InlineData(0, 8_000_000, true)]   // even the "assistant" type is gone
    [InlineData(0, 3_000_000, false)]  // one huge tool result before an interruption
    public void Answers_whose_usage_is_not_understood(int answers, long bytes, bool broken)
    {
        Assert.Equal(broken, Keys(Seen(withoutUsage: (answers, bytes))).Contains("transcripts"));
    }

    // ---- statusline ------------------------------------------------------------------------

    [Fact]
    public void A_terminal_answer_with_no_statusline_run_after_it()
    {
        // Answered 5 minutes ago; neither file written since.
        Assert.Equal(["statusline"], Keys(Seen(cli: [(Now - 300, Now - 2000)], limitsAt: Now - 2000)));
        // Context written, limits not: the limits moved.
        Assert.Equal(["statusline-limits"], Keys(Seen(cli: [(Now - 300, Now - 290)], limitsAt: Now - 2000)));
        // Limits written, context not: the context moved.
        Assert.Equal(["statusline-context"], Keys(Seen(cli: [(Now - 300, null)], limitsAt: Now - 290)));
    }

    [Fact]
    public void An_account_without_limits_is_not_a_change()
    {
        // API-key accounts never get rate limits in the statusline: limits.json was never written.
        Assert.Empty(Keys(Seen(cli: [(Now - 300, Now - 290)], limitsAt: 0)));
    }

    [Fact]
    public void A_statusline_of_ones_own_writes_no_context_files()
    {
        Assert.Empty(Keys(Seen(feed: FeedKind.Own, cli: [(Now - 300, null)], limitsAt: Now - 290)));
        Assert.Empty(Keys(Seen(feed: FeedKind.None, cli: [(Now - 300, null)], limitsAt: Now - 5000)));
    }

    [Theory]
    [InlineData(60)]     // too recent: its statusline may not have run yet
    [InlineData(4000)]   // too old to judge by
    public void Only_answers_from_2_to_30_minutes_ago_are_judged(long ago)
    {
        Assert.Empty(Keys(Seen(cli: [(Now - ago, Now - 5000)], limitsAt: Now - 5000)));
    }

    [Fact]
    public void One_terminal_session_with_a_fresh_statusline_is_enough()
    {
        // Another session's statusline ran: the format is fine; that one session may just be stuck.
        Assert.Empty(Keys(Seen(cli: [(Now - 300, Now - 5000), (Now - 400, Now - 390)], limitsAt: Now - 290)));
    }

    [Fact]
    public void A_hello_that_lost_its_limits()
    {
        Assert.Equal(["hello"], Keys(Seen(helloLost: true)));
    }

    // ---- waiting and announcing -------------------------------------------------------------

    private readonly string _state = Path.Combine(Isolation.Root, "format-" + Guid.NewGuid().ToString("N") + ".json");
    private static readonly FormatObservations Broken = Seen(withoutUsage: (40, 100_000));

    [Fact]
    public void A_problem_counts_after_five_minutes_and_is_told_once_per_version()
    {
        var watch = new FormatWatch(_state);
        Assert.Empty(watch.Update(Broken, Now, "2.1.340"));
        Assert.Equal("a confirmar: o uso de tokens nas conversas", watch.Status());
        Assert.Empty(watch.Update(Broken, Now + 240, "2.1.340"));

        var told = watch.Update(Broken, Now + 300, "2.1.340");
        Assert.Equal("transcripts", Assert.Single(told).Key);
        Assert.StartsWith("não reconhece o uso de tokens nas conversas (desde ", watch.Status());
        Assert.Empty(watch.Update(Broken, Now + 360, "2.1.340"));
        // Nor after a restart, once confirmed again.
        var restarted = new FormatWatch(_state);
        Assert.Empty(restarted.Update(Broken, Now + 400, "2.1.340"));
        Assert.Empty(restarted.Update(Broken, Now + 700, "2.1.340"));
        Assert.Single(restarted.Confirmed);

        // A newer Claude Code that is still broken is worth saying again.
        var later = new FormatWatch(_state);
        later.Update(Broken, Now + 1000, "2.1.341");
        Assert.Single(later.Update(Broken, Now + 1300, "2.1.341"));
    }

    [Fact]
    public void A_problem_that_goes_away_starts_over()
    {
        var watch = new FormatWatch(_state);
        watch.Update(Broken, Now, "2.1.340");
        watch.Update(Seen(), Now + 120, "2.1.340");   // fine again
        Assert.Equal("tudo reconhecido", watch.Status());
        Assert.Empty(watch.Update(Broken, Now + 300, "2.1.340"));   // the five minutes count anew
    }

    [Fact]
    public void The_message_says_what_and_since_which_version()
    {
        var one = FormatWatch.Check(Broken, Now);
        Assert.Equal("Com o Claude Code 2.1.340, o widget já não reconhece o uso de tokens nas conversas. " +
                     "O consumo, o contexto ocupado e o que cada sessão está a fazer podem falhar. " +
                     "Clica para procurar uma versão nova do widget.", FormatWatch.Message(one, "2.1.340"));

        var two = FormatWatch.Check(Seen(withoutUsage: (40, 0), helloLost: true), Now);
        Assert.Equal("O widget já não reconhece o uso de tokens nas conversas e os limites na resposta do Claude Code. " +
                     "Clica para procurar uma versão nova do widget.", FormatWatch.Message(two, ""));
    }

    [Theory]
    [InlineData("powershell -ExecutionPolicy Bypass -NoProfile -File \"C:\\Users\\x\\.claude\\usage-widget\\feed.ps1\"", FeedKind.Widget)]
    [InlineData("npx ccstatusline", FeedKind.None)]
    [InlineData("", FeedKind.None)]
    public void Which_statusline_feeds_the_widget(string command, FeedKind expected)
    {
        Assert.Equal(expected, FormatWatch.FeedOfCommand(command));
    }

    // ---- the session files themselves ----------------------------------------------------

    [Fact]
    public void Session_scan_counts_what_it_understood_and_reads_procStart_as_text()
    {
        var dir = Path.Combine(Isolation.Root, "sessions");
        Directory.CreateDirectory(dir);
        try
        {
            using var me = Process.GetCurrentProcess();
            var start = me.StartTime.ToFileTimeUtc().ToString();
            // Claude Code writes procStart as a string: a FILETIME is too big for a JavaScript number.
            File.WriteAllText(Path.Combine(dir, "1.json"),
                $$"""{"pid":{{me.Id}},"sessionId":"s-alive","cwd":"D:\\p","procStart":"{{start}}","version":"2.1.400","entrypoint":"cli","status":"busy"}""");
            // The same pid, started at another moment: a reused pid, not that session.
            File.WriteAllText(Path.Combine(dir, "2.json"),
                $$"""{"pid":{{me.Id}},"sessionId":"s-gone","cwd":"D:\\p","procStart":"123456789","version":"2.1.300","status":"idle"}""");
            File.WriteAllText(Path.Combine(dir, "3.json"), """{"process":1,"id":"x"}""");

            var sessions = SessionScanner.Scan();
            Assert.Equal("s-alive", Assert.Single(sessions).SessionId);
            Assert.Equal(new SessionScanner.Health(3, 2, 1, 1, "2.1.400"), SessionScanner.LastHealth);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
