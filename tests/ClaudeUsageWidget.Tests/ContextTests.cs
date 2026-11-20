namespace ClaudeUsageWidget.Tests;

/// <summary>How full a session's context is, without the token-optimizer plugin.</summary>
public class ContextTests
{
    private readonly string _claudeDir = Path.Combine(Isolation.Root, "context-" + Guid.NewGuid().ToString("N"));
    private const string Cwd = @"D:\Home Work\My Project";
    private static readonly long Now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private string Transcript(string sessionId)
    {
        var dir = Path.Combine(_claudeDir, "projects", "D--Home-Work-My-Project");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, sessionId + ".jsonl");
    }

    /// <summary>An assistant line as Claude Code writes it, the usage of the request it answered.</summary>
    private static string Answer(long input, long cacheWrite, long cacheRead, long at, string model = "claude-opus-5-5", bool sidechain = false) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["isSidechain"] = sidechain,
            ["type"] = "assistant",
            ["message"] = new Dictionary<string, object>
            {
                ["id"] = Guid.NewGuid().ToString("N"),
                ["model"] = model,
                ["usage"] = new Dictionary<string, object>
                {
                    ["input_tokens"] = input,
                    ["cache_creation_input_tokens"] = cacheWrite,
                    ["cache_read_input_tokens"] = cacheRead,
                    ["output_tokens"] = 500,
                },
            },
            ["timestamp"] = DateTimeOffset.FromUnixTimeSeconds(at).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["sessionId"] = "s",
        }) + "\n";

    // ---- the window's size --------------------------------------------------------

    [Theory]
    [InlineData("claude-opus-5-5", 1_000_000)]
    [InlineData("claude-opus-5", 1_000_000)]
    [InlineData("claude-fable-5-2", 1_000_000)]
    [InlineData("claude-haiku-5-5", 1_000_000)]
    [InlineData("claude-opus-4-7", 1_000_000)]
    [InlineData("claude-sonnet-4-6", 200_000)]
    [InlineData("claude-sonnet-4-6[1m]", 1_000_000)]
    [InlineData("claude-haiku-4-5-20251001", 200_000)]
    [InlineData("claude-opus-4-20250514", 200_000)]
    [InlineData("claude-3-5-sonnet-20241022", 200_000)]
    [InlineData("", 200_000)]
    public void Default_window_per_model(string model, long expected)
    {
        Assert.Equal(expected, ContextWindow.DefaultWindow(model));
    }

    // ---- putting the readings together -------------------------------------------

    private static readonly ContextWindow.Report[] None = [];

    [Fact]
    public void The_window_reported_for_the_session_makes_it_exact()
    {
        // The statusline said 1M; the transcript has a later answer at 300k.
        var reported = new ContextWindow.Report(1_000_000, 247_316, "claude-opus-5-5", Now - 60);
        var fill = ContextWindow.Estimate((300_000, "claude-opus-5-5", Now), reported, [reported])!;
        Assert.True(fill.Exact);
        Assert.Equal(30, fill.Percent);
        Assert.Equal(300_000, fill.Tokens);
    }

    [Fact]
    public void A_200k_window_fills_five_times_faster()
    {
        var reported = new ContextWindow.Report(200_000, 150_000, "claude-sonnet-4-6", Now);
        var fill = ContextWindow.Estimate(null, reported, [reported])!;
        Assert.True(fill.Exact);
        Assert.Equal(75, fill.Percent);
    }

    [Fact]
    public void Without_a_statusline_the_size_comes_from_another_session_of_the_same_model()
    {
        // A desktop session; a terminal session on the same model reported 200k.
        var other = new ContextWindow.Report(200_000, 10_000, "claude-opus-5-5", Now - 600);
        var fill = ContextWindow.Estimate((120_000, "claude-opus-5-5", Now), null, [other])!;
        Assert.False(fill.Exact);
        Assert.Equal(60, fill.Percent);
    }

    [Fact]
    public void With_nothing_reported_the_model_default_is_used()
    {
        var fill = ContextWindow.Estimate((120_000, "claude-opus-5-5", Now), null, None)!;
        Assert.False(fill.Exact);
        Assert.Equal(1_000_000, fill.Size);
        Assert.Equal(12, fill.Percent);
    }

    [Fact]
    public void A_guess_the_session_has_outgrown_means_a_1M_window()
    {
        var fill = ContextWindow.Estimate((450_000, "claude-sonnet-4-6", Now), null, None)!;
        Assert.Equal(1_000_000, fill.Size);
        Assert.Equal(45, fill.Percent);
    }

    [Fact]
    public void After_a_model_switch_the_old_report_does_not_apply()
    {
        // Reported on Sonnet 4.6 (200k); the session now runs Opus 5.5.
        var reported = new ContextWindow.Report(200_000, 100_000, "claude-sonnet-4-6", Now - 60);
        var fill = ContextWindow.Estimate((100_000, "claude-opus-5-5", Now), reported, [reported])!;
        Assert.False(fill.Exact);
        Assert.Equal(1_000_000, fill.Size);
    }

    [Fact]
    public void No_answer_yet_no_figure()
    {
        // The statusline runs before the first answer, with no tokens.
        var reported = new ContextWindow.Report(1_000_000, 0, "claude-opus-5-5", Now);
        Assert.Null(ContextWindow.Estimate(null, reported, [reported]));
        Assert.Null(ContextWindow.Estimate(null, null, None));
    }

    [Fact]
    public void Reports_that_carry_no_window_are_ignored()
    {
        Assert.Null(ContextWindow.Parse("""{"updated_at":1,"model":"m","size":0,"tokens":5}"""));
        Assert.Null(ContextWindow.Parse("not json"));
        Assert.Equal(new ContextWindow.Report(1_000_000, 247_316, "claude-opus-5-5", 1795165741),
            ContextWindow.Parse("""{"updated_at":1795165741,"model":"claude-opus-5-5","size":1000000,"tokens":247316,"used_percentage":25}"""));
    }

    // ---- reading the transcript ---------------------------------------------------

    [Fact]
    public void The_last_answer_counts_everything_the_request_sent()
    {
        var id = Guid.NewGuid().ToString();
        // The real numbers of a turn: 2 new, 173 cache-written, 247,141 cached = 247,316.
        File.WriteAllText(Transcript(id), Answer(2, 3634, 241_636, Now - 60) + Answer(2, 173, 247_141, Now - 10));
        var answer = Transcripts.LastAnswer(_claudeDir, id, Cwd)!.Value;
        Assert.Equal(247_316, answer.Tokens);
        Assert.Equal("claude-opus-5-5", answer.Model);
        Assert.Equal(Now - 10, answer.At);
    }

    [Fact]
    public void Subagents_and_error_lines_are_not_the_session_context()
    {
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(Transcript(id),
            Answer(10, 0, 100_000, Now - 60) +
            Answer(5, 0, 20_000, Now - 30, sidechain: true) +
            Answer(0, 0, 0, Now - 20, model: "<synthetic>"));
        Assert.Equal(100_010, Transcripts.LastAnswer(_claudeDir, id, Cwd)!.Value.Tokens);
    }

    [Fact]
    public void New_answers_are_picked_up_as_the_transcript_grows()
    {
        var id = Guid.NewGuid().ToString();
        var path = Transcript(id);
        File.WriteAllText(path, Answer(1, 0, 50_000, Now - 60));
        Assert.Equal(50_001, Transcripts.LastAnswer(_claudeDir, id, Cwd)!.Value.Tokens);
        File.AppendAllText(path, Answer(1, 0, 80_000, Now - 5));
        Assert.Equal(80_001, Transcripts.LastAnswer(_claudeDir, id, Cwd)!.Value.Tokens);
    }

    [Fact]
    public void No_answers_no_context()
    {
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(Transcript(id), """{"type":"user","message":{"content":"hi"}}""" + "\n");
        Assert.Null(Transcripts.LastAnswer(_claudeDir, id, Cwd));
    }

    [Fact]
    public void Statusline_report_and_transcript_together()
    {
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(Transcript(id), Answer(2, 173, 49_825, Now - 5, model: "claude-sonnet-4-6"));
        var dir = Path.Combine(Paths.Dir, "context");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, id + ".json"),
            $$"""{"updated_at":{{Now - 30}},"model":"claude-sonnet-4-6","size":200000,"tokens":40000,"used_percentage":20}""");

        var fill = ContextWindow.For(_claudeDir, id, Cwd)!;
        Assert.True(fill.Exact);
        Assert.Equal(25, fill.Percent);   // 50,000 of 200k
    }

    // ---- the alert -------------------------------------------------------------

    [Fact]
    public void An_estimated_alert_says_so()
    {
        var watch = new ContextWatch();
        var cfg = new ContextAlertsConfig { FillAbove = 80 };
        var exact = new SessionInfo { SessionId = "a", Name = "A", Kind = "CLI", FillPct = 85 };
        var guess = new SessionInfo { SessionId = "b", Name = "B", Kind = "Desktop", FillPct = 90, FillEstimated = true };

        var alerts = watch.Update(cfg, [exact, guess]);
        Assert.Contains(alerts, a => a.Text == "A\n85% ocupado. Compacta ou abre uma sessão nova.");
        Assert.Contains(alerts, a => a.Text == "B\nCerca de 90% ocupado. Compacta ou abre uma sessão nova.");
        Assert.Empty(watch.Update(cfg, [exact, guess]));   // once each
    }
}
