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

    [Fact]
    public void Nothing_is_announced_on_the_first_scan()
    {
        // The widget just started: whatever state sessions are in is old news.
        Assert.Empty(_tracker.Update(_cfg, Session("waiting")));
    }
}
