namespace ClaudeUsageWidget.Tests;

public class AlertTests
{
    private readonly AlertsConfig _cfg = new() { MinTaskSeconds = 30, RemindMinutes = 2 };
    private readonly AlertTracker _tracker = new();
    private DateTime _now = new(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
    private readonly string _id = Guid.NewGuid().ToString();
    private long _stampMs = 1_795_000_000_000;

    public AlertTests() => _tracker.Clock = () => _now;

    /// <summary>
    /// A session in this status, changed `afterSeconds` after the last one.
    /// Pid 0 has no window, so it is never "in front of the user".
    /// </summary>
    private List<SessionInfo> Session(string status, int afterSeconds = 0)
    {
        _stampMs += afterSeconds * 1000L;
        return [new SessionInfo { Pid = 0, SessionId = _id, Name = "Estado do projeto", Kind = "CLI", Status = status, StatusUpdatedAt = _stampMs }];
    }

    private List<Alert> After(TimeSpan wait, List<SessionInfo> sessions)
    {
        _now += wait;
        return _tracker.Update(_cfg, sessions);
    }

    [Fact]
    public void A_finished_task_is_announced_once()
    {
        _tracker.Update(_cfg, Session("busy"));
        var idle = Session("idle", afterSeconds: 90);

        var first = Assert.Single(After(TimeSpan.Zero, idle));
        Assert.Equal(AlertKind.Done, first.Kind);
        Assert.Equal("Tarefa de 1 min concluída.", first.Message);

        for (var i = 0; i < 5; i++) Assert.Empty(After(TimeSpan.FromMinutes(2), idle));
        Assert.Equal(AlertKind.Done, _tracker.Pending);   // still pulsing until seen
    }

    [Fact]
    public void A_short_task_is_not_announced()
    {
        _tracker.Update(_cfg, Session("busy"));
        Assert.Empty(After(TimeSpan.Zero, Session("idle", afterSeconds: 10)));
    }

    [Fact]
    public void A_session_waiting_for_input_is_reminded_three_times()
    {
        _tracker.Update(_cfg, Session("idle"));
        var waiting = Session("waiting", afterSeconds: 5);

        Assert.Equal(AlertKind.Input, Assert.Single(After(TimeSpan.Zero, waiting)).Kind);
        Assert.Empty(After(TimeSpan.FromMinutes(1), waiting));        // not yet
        Assert.Single(After(TimeSpan.FromMinutes(1), waiting));       // 2 min after the first
        for (var i = 1; i < AlertTracker.MaxReminders; i++)
        {
            Assert.Empty(After(TimeSpan.FromMinutes(1), waiting));    // counted from the last one
            Assert.Single(After(TimeSpan.FromMinutes(1), waiting));
        }
        Assert.Empty(After(TimeSpan.FromMinutes(2), waiting));
        Assert.Empty(After(TimeSpan.FromMinutes(10), waiting));
    }

    [Fact]
    public void No_reminders_when_set_to_zero()
    {
        _cfg.RemindMinutes = 0;
        _tracker.Update(_cfg, Session("idle"));
        var waiting = Session("waiting", afterSeconds: 5);

        Assert.Single(After(TimeSpan.Zero, waiting));
        Assert.Empty(After(TimeSpan.FromMinutes(30), waiting));
    }

    [Fact]
    public void Seen_alerts_stop()
    {
        _tracker.Update(_cfg, Session("idle"));
        var waiting = Session("waiting", afterSeconds: 5);
        Assert.Single(After(TimeSpan.Zero, waiting));

        _tracker.AckAll();
        Assert.Null(_tracker.Pending);
        Assert.Empty(After(TimeSpan.FromMinutes(5), waiting));
    }

    [Fact]
    public void Resuming_work_clears_a_finished_task()
    {
        _tracker.Update(_cfg, Session("busy"));
        Assert.Single(After(TimeSpan.Zero, Session("idle", afterSeconds: 60)));

        After(TimeSpan.FromSeconds(5), Session("busy", afterSeconds: 5));
        Assert.Null(_tracker.Pending);
    }

    private static SessionInfo Other(string id, string status, long stampMs) =>
        new() { Pid = 0, SessionId = id, Name = id, Kind = "CLI", Status = status, StatusUpdatedAt = stampMs };

    [Fact]
    public void Waiting_lists_unseen_input_first_then_the_longest_waiting()
    {
        // a: finished first; b: finished later; c: needs input, last of all.
        const long t = 1_795_000_000_000;
        _tracker.Update(_cfg, [Other("a", "busy", t), Other("b", "busy", t), Other("c", "idle", t)]);
        After(TimeSpan.FromMinutes(1), [Other("a", "idle", t + 60_000), Other("b", "busy", t), Other("c", "idle", t)]);
        After(TimeSpan.FromMinutes(1), [Other("a", "idle", t + 60_000), Other("b", "idle", t + 120_000), Other("c", "idle", t)]);
        After(TimeSpan.FromMinutes(1), [Other("a", "idle", t + 60_000), Other("b", "idle", t + 120_000), Other("c", "waiting", t + 180_000)]);

        Assert.Equal(["c", "a", "b"], _tracker.Waiting().Select(a => a.SessionId));

        // Seen ones go to the back, still listed: the session is still waiting.
        _tracker.Ack("c");
        Assert.Equal(["a", "b", "c"], _tracker.Waiting().Select(a => a.SessionId));
        Assert.Equal(AlertKind.Done, _tracker.Pending);
    }

    [Fact]
    public void An_alert_knows_its_process_and_when_it_came()
    {
        _tracker.Update(_cfg, [new SessionInfo { Pid = 4321, SessionId = _id, Name = "x", Kind = "CLI", Status = "idle" }]);
        After(TimeSpan.FromSeconds(5), [new SessionInfo { Pid = 4321, SessionId = _id, Name = "x", Kind = "CLI", Status = "waiting", StatusUpdatedAt = 1 }]);

        var alert = Assert.Single(_tracker.Waiting());
        Assert.Equal(4321, alert.Pid);
        Assert.Equal(_now, alert.RaisedAt);
    }

    [Fact]
    public void Nothing_is_announced_on_the_first_scan()
    {
        // The widget just started: whatever state sessions are in is old news.
        Assert.Empty(_tracker.Update(_cfg, Session("waiting")));
    }

    // ---- Claude Code's statuses ------------------------------------------------------

    [Fact]
    public void A_session_running_a_command_is_working_not_waiting_for_you()
    {
        // "shell" is what Claude Code shows as working, like "busy".
        _tracker.Update(_cfg, Session("idle"));
        Assert.Empty(After(TimeSpan.Zero, Session("shell", afterSeconds: 5)));
        Assert.Empty(After(TimeSpan.FromMinutes(5), Session("shell")));
        Assert.Null(_tracker.Pending);
    }

    [Fact]
    public void A_task_with_commands_in_it_is_timed_from_its_start()
    {
        _tracker.Update(_cfg, Session("idle"));
        After(TimeSpan.Zero, Session("busy", afterSeconds: 1));
        After(TimeSpan.Zero, Session("shell", afterSeconds: 40));
        After(TimeSpan.Zero, Session("busy", afterSeconds: 40));
        var done = Assert.Single(After(TimeSpan.Zero, Session("idle", afterSeconds: 40)));
        Assert.Equal(AlertKind.Done, done.Kind);
        Assert.Equal("Tarefa de 2 min concluída.", done.Message);
    }

    [Fact]
    public void Waiting_says_what_for_when_Claude_Code_does()
    {
        _tracker.Update(_cfg, Session("idle"));
        _stampMs += 5000;
        var waiting = new SessionInfo { Pid = 0, SessionId = _id, Name = "Estado do projeto", Kind = "CLI", Status = "waiting", WaitingFor = "permission", StatusUpdatedAt = _stampMs };
        var alert = Assert.Single(After(TimeSpan.Zero, [waiting]));
        Assert.Equal(AlertKind.Input, alert.Kind);
        Assert.Equal("permission", alert.Message);
    }

    [Fact]
    public void A_status_the_widget_does_not_know_raises_nothing()
    {
        _tracker.Update(_cfg, Session("idle"));
        Assert.Empty(After(TimeSpan.Zero, Session("compacting", afterSeconds: 5)));
    }

    [Theory]
    [InlineData("shell", "", "a correr um comando", Tone.Busy)]
    [InlineData("waiting", "permission", "à tua espera: permission", Tone.Input)]
    [InlineData("waiting", "", "à tua espera", Tone.Input)]
    public void The_list_reads_each_status(string status, string waitingFor, string text, Tone tone)
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(_stampMs);
        var s = new SessionInfo { SessionId = _id, Name = "x", Kind = "CLI", Status = status, WaitingFor = waitingFor, StatusUpdatedAt = _stampMs - 120_000 };
        var (line, lineTone) = SessionStatus.Line(s, null, now);
        Assert.StartsWith(text, line);
        Assert.Equal(tone, lineTone);
    }

    // ---- the shortcut with nobody waiting ------------------------------------------------

    private static SessionInfo Open(string id, int startedMinute, long changedMs) => new()
    {
        SessionId = id, Name = id, Kind = "CLI", Status = "idle",
        StartedAt = new DateTimeOffset(2026, 11, 20, 9, startedMinute, 0, TimeSpan.Zero), StatusUpdatedAt = changedMs,
    };

    [Fact]
    public void The_shortcut_goes_round_the_open_sessions_when_nobody_waits()
    {
        List<SessionInfo> sessions = [Open("b", 20, 500), Open("a", 10, 900), Open("c", 30, 100)];
        // From another program: the one that changed last.
        Assert.Equal("a", MainWindow.NextInTurn(sessions, _ => false)!.SessionId);
        // From a session: the next in the order they were opened, round to the first.
        Assert.Equal("b", MainWindow.NextInTurn(sessions, s => s.SessionId == "a")!.SessionId);
        Assert.Equal("c", MainWindow.NextInTurn(sessions, s => s.SessionId == "b")!.SessionId);
        Assert.Equal("a", MainWindow.NextInTurn(sessions, s => s.SessionId == "c")!.SessionId);
        Assert.Null(MainWindow.NextInTurn([], _ => false));
    }
}
