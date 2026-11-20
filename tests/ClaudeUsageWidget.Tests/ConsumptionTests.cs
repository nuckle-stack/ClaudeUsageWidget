namespace ClaudeUsageWidget.Tests;

public class PricingTests
{
    [Fact]
    public void The_longest_matching_model_wins()
    {
        Assert.Equal(4, Pricing.For("claude-opus-5-5").Input);
        Assert.Equal(5, Pricing.For("claude-opus-5").Input);
        Assert.Equal(8, Pricing.For("claude-fable-5-2").Input);
        Assert.Equal(1.00, Pricing.For("claude-fable-5").CacheRead);
    }

    [Theory]
    [InlineData("claude-opus-4-6-20260101", 5)]   // dated ids
    [InlineData("claude-opus-5-5[1m]", 4)]
    [InlineData("CLAUDE-SONNET-5-5", 2)]
    [InlineData("claude-opus-9", 4)]              // unknown: the newest of its family
    [InlineData("claude-haiku-7", 0.10)]
    [InlineData("something-else", 2)]             // unknown family: the middle (Sonnet 5.5)
    public void Unlisted_models_take_their_family(string model, double input)
    {
        Assert.Equal(input, Pricing.For(model).Input);
    }

    [Fact]
    public void Cost_prices_every_kind_of_token()
    {
        // Opus 5.5: $4 in, $20 out, $0.20 cache read; writes 1.25x / 2x of $4.
        var cost = Pricing.Cost("claude-opus-5-5", input: 1_000_000, output: 1_000_000,
            cacheRead: 1_000_000, write5m: 1_000_000, write1h: 1_000_000);
        Assert.Equal(4 + 20 + 0.20 + 5 + 8, cost, precision: 9);
    }

    [Fact]
    public void Opus_weighs_far_more_than_Haiku()
    {
        var opus = Pricing.Cost("claude-opus-5-5", 1000, 1000, 0, 0, 0);
        var haiku = Pricing.Cost("claude-haiku-5-5", 1000, 1000, 0, 0, 0);
        Assert.Equal(40, opus / haiku, precision: 6);
    }
}

public class UsageLedgerTests
{
    private readonly string _dir = Path.Combine(Isolation.Root, "ledger-" + Guid.NewGuid().ToString("N"));
    private static readonly long Now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private string Transcript(string folder, string name)
    {
        var dir = Path.Combine(_dir, "projects", folder);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name + ".jsonl");
    }

    /// <summary>An assistant line as Claude Code writes it; Opus 5.5 by default.</summary>
    private static string Line(string msgId, string session, string cwd, long at, long output = 1_000_000,
        string model = "claude-opus-5-5", long write1h = 0, long write = 0, string block = "text")
    {
        var line = new Dictionary<string, object>
        {
            ["parentUuid"] = "x",
            ["isSidechain"] = false,
            ["type"] = "assistant",
            ["message"] = new Dictionary<string, object>
            {
                ["id"] = msgId,
                ["model"] = model,
                ["role"] = "assistant",
                ["content"] = new[] { new Dictionary<string, object> { ["type"] = block } },
                ["usage"] = new Dictionary<string, object>
                {
                    ["input_tokens"] = 0,
                    ["cache_creation_input_tokens"] = write,
                    ["cache_read_input_tokens"] = 0,
                    ["output_tokens"] = output,
                    ["cache_creation"] = new Dictionary<string, object>
                    {
                        ["ephemeral_1h_input_tokens"] = write1h,
                        ["ephemeral_5m_input_tokens"] = write - write1h,
                    },
                },
            },
            ["requestId"] = "r",
            ["timestamp"] = DateTimeOffset.FromUnixTimeSeconds(at).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["cwd"] = cwd,
            ["sessionId"] = session,
        };
        return System.Text.Json.JsonSerializer.Serialize(line) + "\n";
    }

    private const string User = """{"type":"user","message":{"role":"user","content":"usage of the assistant"},"sessionId":"s1"}""" + "\n";

    private UsageLedger Read()
    {
        var ledger = new UsageLedger(_dir);
        ledger.Refresh(Now);
        return ledger;
    }

    [Fact]
    public void A_message_split_over_several_lines_counts_once()
    {
        // One response = thinking + text + tool_use lines, each with the full usage ($20 of output).
        File.WriteAllText(Transcript("p", "s1"),
            User + Line("m1", "s1", @"D:\p", Now - 60, block: "thinking") + Line("m1", "s1", @"D:\p", Now - 60) +
            Line("m1", "s1", @"D:\p", Now - 60, block: "tool_use"));
        Assert.Equal(20, Read().Summarize(Now - 3600, Now + 1).Total, precision: 6);
    }

    [Fact]
    public void The_same_message_in_two_files_counts_once()
    {
        // A resumed or forked session carries earlier messages into a new file.
        File.WriteAllText(Transcript("p", "s1"), Line("m1", "s1", @"D:\p", Now - 60));
        File.WriteAllText(Transcript("p", "s2"), Line("m1", "s1", @"D:\p", Now - 60) + Line("m2", "s2", @"D:\p", Now - 30));
        Assert.Equal(40, Read().Summarize(Now - 3600, Now + 1).Total, precision: 6);
    }

    [Fact]
    public void One_hour_cache_writes_cost_double_the_input()
    {
        // 1M tokens written: 600k at 1h (2x $4) + 400k at 5m (1.25x $4) = 4.8 + 2.0.
        File.WriteAllText(Transcript("p", "s1"), Line("m1", "s1", @"D:\p", Now - 60, output: 0, write: 1_000_000, write1h: 600_000));
        Assert.Equal(6.8, Read().Summarize(Now - 3600, Now + 1).Total, precision: 6);
    }

    [Fact]
    public void Only_requests_inside_the_period_count()
    {
        File.WriteAllText(Transcript("p", "s1"),
            Line("old", "s1", @"D:\p", Now - 6 * 3600) + Line("new", "s1", @"D:\p", Now - 60));
        var ledger = Read();
        Assert.Equal(20, ledger.Summarize(Now - 5 * 3600, Now + 1).Total, precision: 6);
        Assert.Equal(40, ledger.Summarize(Now - 7 * 86400, Now + 1).Total, precision: 6);
    }

    [Fact]
    public void Splits_by_project_and_session_heaviest_first()
    {
        File.WriteAllText(Transcript("a", "s1"), Line("m1", "s1", @"D:\Work\Alpha", Now - 90) + Line("m2", "s1", @"D:\Work\Alpha", Now - 80));
        File.WriteAllText(Transcript("a", "s2"), Line("m3", "s2", @"D:\Work\Alpha", Now - 70, output: 500_000));
        File.WriteAllText(Transcript("b", "s3"), Line("m4", "s3", @"D:\Work\Beta", Now - 60));

        var b = Read().Summarize(Now - 3600, Now + 1);
        Assert.Equal(["Alpha", "Beta"], b.Projects.Select(p => p.Name));
        Assert.Equal(50, b.Projects[0].Cost, precision: 6);
        Assert.Equal(["s1", "s2"], b.Projects[0].Sessions.Select(s => s.SessionId));
        Assert.Equal(40.0 / 70, b.ShareOf("s1"), precision: 6);
        Assert.Equal(0, b.ShareOf("nobody"));
    }

    [Fact]
    public void A_session_stays_with_the_folder_it_started_in()
    {
        File.WriteAllText(Transcript("p", "s1"),
            Line("m1", "s1", @"D:\Work\Alpha", Now - 90) + Line("m2", "s1", @"D:\Work\Alpha\src", Now - 60));
        var b = Read().Summarize(Now - 3600, Now + 1);
        Assert.Equal("Alpha", Assert.Single(b.Projects).Name);
    }

    [Fact]
    public void Subagent_transcripts_in_subfolders_count_for_their_session()
    {
        File.WriteAllText(Transcript("p", "s1"), Line("m1", "s1", @"D:\p", Now - 90));
        File.WriteAllText(Transcript(Path.Combine("p", "s1", "subagents"), "agent-1"), Line("m2", "s1", @"D:\p", Now - 60, model: "claude-haiku-5-5"));
        var b = Read().Summarize(Now - 3600, Now + 1);
        Assert.Equal(20 + 0.5, Assert.Single(Assert.Single(b.Projects).Sessions).Cost, precision: 6);
    }

    [Fact]
    public void Reads_incrementally_and_waits_for_half_written_lines()
    {
        var path = Transcript("p", "s1");
        File.WriteAllText(path, Line("m1", "s1", @"D:\p", Now - 90));
        var ledger = new UsageLedger(_dir);
        ledger.Refresh(Now);

        var next = Line("m2", "s1", @"D:\p", Now - 60);
        File.AppendAllText(path, next[..40]);
        ledger.Refresh(Now);
        Assert.Equal(20, ledger.Summarize(Now - 3600, Now + 1).Total, precision: 6);

        File.AppendAllText(path, next[40..]);
        ledger.Refresh(Now);
        Assert.Equal(40, ledger.Summarize(Now - 3600, Now + 1).Total, precision: 6);
    }

    [Fact]
    public void Ignores_lines_without_usage_and_junk()
    {
        File.WriteAllText(Transcript("p", "s1"),
            User + "not json \"usage\" \"assistant\"\n" +
            Line("m1", "s1", @"D:\p", Now - 60, model: "<synthetic>", output: 0) +
            Line("m2", "s1", @"D:\p", Now - 50));
        Assert.Equal(20, Read().Summarize(Now - 3600, Now + 1).Total, precision: 6);
    }

    [Fact]
    public void Desktop_conversations_without_a_project_are_one_group()
    {
        var a = @"C:\Users\x\AppData\Local\Claude-Data\claude-abc\scratch\workspaces\1\scratch-2026-11-18-aaa";
        var b = @"C:\Users\x\AppData\Local\Claude-Data\claude-abc\scratch\workspaces\2\scratch-2026-11-19-bbb";
        File.WriteAllText(Transcript("d1", "s1"), Line("m1", "s1", a, Now - 90));
        File.WriteAllText(Transcript("d2", "s2"), Line("m2", "s2", b, Now - 60));
        var project = Assert.Single(Read().Summarize(Now - 3600, Now + 1).Projects);
        Assert.Equal("App Desktop (sem projeto)", project.Name);
        Assert.Equal(2, project.Sessions.Count);
    }

    [Fact]
    public void Sessions_are_named_by_their_transcript_title()
    {
        File.WriteAllText(Transcript("p", "s-title"),
            """{"type":"ai-title","aiTitle":"Estado do projeto","sessionId":"s-title"}""" + "\n" +
            Line("m1", "s-title", @"D:\p", Now - 60));
        File.WriteAllText(Transcript("p", "0123456789abcdef"), Line("m2", "0123456789abcdef", @"D:\p", Now - 50));

        var sessions = Read().Summarize(Now - 3600, Now + 1).Projects.Single().Sessions;
        Assert.Contains(sessions, s => s.Title == "Estado do projeto");
        Assert.Contains(sessions, s => s.Title == "sessão 01234567");
    }

    [Fact]
    public void No_transcripts_no_usage()
    {
        var b = Read().Summarize(Now - 3600, Now + 1);
        Assert.Equal(0, b.Total);
        Assert.Empty(b.Projects);
    }
}
