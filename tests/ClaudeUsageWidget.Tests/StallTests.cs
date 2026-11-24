namespace ClaudeUsageWidget.Tests;

/// <summary>A session working for a long time with nothing new in its conversation.</summary>
public class StallTests
{
    private readonly AlertsConfig _cfg = new() { StallMinutes = 15 };
    private readonly StallWatch _watch = new();
    private const long Now = 1_795_000_000;

    private static readonly Doing GitPush = new("correr git push", null, Now - 3600, "Bash");
    private static readonly Doing Browser = new("usar navigate (claude-in-chrome)", null, Now - 3600, "mcp__claude-in-chrome__navigate");
    private static readonly Doing Subagent = new("delegar \"Find the readers\"", null, Now - 3600, "Agent");

    private static SessionInfo Session(string status = "busy", int progressMinutesAgo = 20, int statusMinutesAgo = 60,
        Doing? doing = null, string id = "s1", long progressAt = -1) => new()
    {
        SessionId = id,
        Name = "Estado do projeto",
        Kind = "CLI",
        Status = status,
        StatusUpdatedAt = (Now - statusMinutesAgo * 60L) * 1000,
        ProgressAt = progressAt >= 0 ? progressAt : Now - progressMinutesAgo * 60L,
        Doing = doing,
    };

    [Fact]
    public void A_session_working_without_moving_on_is_announced_once()
    {
        var stuck = Session(doing: Browser);
        var (title, text, session) = Assert.Single(_watch.Update(_cfg, [stuck], Now));
        Assert.Equal("Tarefa sem avançar", title);
        Assert.Equal("Estado do projeto\nHá 20 min a usar navigate (claude-in-chrome), sem nada de novo na conversa.", text);
        Assert.Same(stuck, session);

        Assert.Empty(_watch.Update(_cfg, [stuck], Now + 60));
        Assert.Empty(_watch.Update(_cfg, [stuck], Now + 3600));
    }

    [Fact]
    public void Without_a_pending_call_it_says_it_is_working()
    {
        var (_, text, _) = Assert.Single(_watch.Update(_cfg, [Session(status: "shell", progressMinutesAgo: 75, statusMinutesAgo: 90)], Now));
        Assert.Contains("Há 1h15 a trabalhar,", text);
    }

    [Fact]
    public void Progress_arms_it_again()
    {
        Assert.Single(_watch.Update(_cfg, [Session(doing: GitPush)], Now));
        // A new line in the conversation: no longer stuck...
        Assert.Empty(_watch.Update(_cfg, [Session(doing: GitPush, progressMinutesAgo: 1)], Now));
        // ...until it stalls again.
        Assert.Single(_watch.Update(_cfg, [Session(doing: GitPush, progressAt: Now - 60)], Now + 16 * 60));
    }

    [Theory]
    [InlineData("busy", 14, 60)]     // not long enough
    [InlineData("idle", 120, 120)]   // stopped, not stuck
    [InlineData("waiting", 120, 120)] // waiting for you is another alert
    [InlineData("busy", 30, 5)]      // the status changed a moment ago
    public void It_stays_quiet_otherwise(string status, int progressMinutesAgo, int statusMinutesAgo)
    {
        Assert.Empty(_watch.Update(_cfg, [Session(status, progressMinutesAgo, statusMinutesAgo)], Now));
    }

    [Fact]
    public void A_subagent_s_work_is_not_taken_for_a_stall()
    {
        // Its progress is written outside the session's conversation.
        Assert.Empty(_watch.Update(_cfg, [Session(progressMinutesAgo: 90, doing: Subagent)], Now));
    }

    [Fact]
    public void Without_a_transcript_there_is_no_telling()
    {
        Assert.Empty(_watch.Update(_cfg, [Session(progressAt: 0)], Now));
    }

    [Fact]
    public void Zero_minutes_turns_it_off()
    {
        Assert.Empty(_watch.Update(new AlertsConfig { StallMinutes = 0 }, [Session(progressMinutesAgo: 300)], Now));
    }

    [Fact]
    public void Automation_sessions_are_left_out()
    {
        var sdk = new SessionInfo { SessionId = "x", Kind = "SDK", Status = "busy", ProgressAt = Now - 7200 };
        Assert.Empty(_watch.Update(_cfg, [sdk], Now));
    }

    [Fact]
    public void Each_session_is_announced_on_its_own()
    {
        var two = _watch.Update(_cfg, [Session(id: "a"), Session(id: "b", doing: GitPush)], Now);
        Assert.Equal(2, two.Count);
        // One closes; the other is still announced already.
        Assert.Empty(_watch.Update(_cfg, [Session(id: "b", doing: GitPush)], Now + 60));
    }
}
