using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClaudeUsageFeed;

namespace ClaudeUsageWidget.Tests;

/// <summary>
/// The statusline command and Notification hook (ClaudeUsageFeed.exe), the
/// same cases feed.ps1 and notify.ps1 were tested for.
/// </summary>
public class FeedTests
{
    private const string Esc = "\u001b";
    private const long R5 = 1795137600;   // a five-hour reset
    private const long R7 = 1795543200;   // a weekly reset

    private readonly string _dir = Path.Combine(Isolation.Root, "feed-" + Guid.NewGuid().ToString("N"));

    private string ConfigDir()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "usage-widget"));
        return _dir;
    }

    private string WidgetFile(string name) => Path.Combine(_dir, "usage-widget", name);

    /// <summary>The statusline JSON Claude Code sends; null leaves a window out.</summary>
    private static string Input((double Pct, long Reset)? five, (double Pct, long Reset)? week, string model = "Opus 5.5")
    {
        var limits = new Dictionary<string, object>();
        if (five is { } f) limits["five_hour"] = new Dictionary<string, object> { ["used_percentage"] = f.Pct, ["resets_at"] = f.Reset };
        if (week is { } w) limits["seven_day"] = new Dictionary<string, object> { ["used_percentage"] = w.Pct, ["resets_at"] = w.Reset };
        return JsonSerializer.Serialize(new Dictionary<string, object> { ["model"] = new Dictionary<string, string> { ["display_name"] = model }, ["rate_limits"] = limits });
    }

    private JsonElement Limits() => JsonDocument.Parse(File.ReadAllText(WidgetFile("limits.json"))).RootElement;

    private void SetLimits((double Pct, long Reset)? five, (double Pct, long Reset)? week)
    {
        var old = new Dictionary<string, object> { ["updated_at"] = 1 };
        if (five is { } f) old["five_hour"] = new Dictionary<string, object> { ["used_percentage"] = f.Pct, ["resets_at"] = f.Reset };
        if (week is { } w) old["seven_day"] = new Dictionary<string, object> { ["used_percentage"] = w.Pct, ["resets_at"] = w.Reset };
        File.WriteAllText(WidgetFile("limits.json"), JsonSerializer.Serialize(old));
    }

    private static double Pct(JsonElement l, string window) => l.GetProperty(window).GetProperty("used_percentage").GetDouble();
    private static long Reset(JsonElement l, string window) => l.GetProperty(window).GetProperty("resets_at").GetInt64();

    // ---- limits.json ---------------------------------------------------------------

    [Fact]
    public void Records_both_windows_and_the_time_of_the_reading()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Feed.Statusline(Input((12.5, R5), (3, R7)), ConfigDir());
        var l = Limits();
        Assert.Equal(12.5, Pct(l, "five_hour"));
        Assert.Equal(R5, Reset(l, "five_hour"));
        Assert.Equal(3, Pct(l, "seven_day"));
        Assert.Equal(R7, Reset(l, "seven_day"));
        Assert.True(l.GetProperty("updated_at").GetInt64() >= before);
    }

    [Fact]
    public void Creates_the_usage_widget_folder_on_a_fresh_machine()
    {
        Directory.CreateDirectory(_dir);
        Feed.Statusline(Input((1, R5), (1, R7)), _dir);
        Assert.True(File.Exists(WidgetFile("limits.json")));
    }

    [Fact]
    public void Leaves_limits_alone_without_rate_limits()
    {
        Feed.Statusline("""{"model":{"display_name":"Opus 5.5"}}""", ConfigDir());
        Assert.False(File.Exists(WidgetFile("limits.json")));
    }

    [Fact]
    public async Task Sessions_writing_at_once_leave_a_valid_file_and_no_temp_files()
    {
        var dir = ConfigDir();
        var json = Input((5, R5), (2, R7));
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => Feed.Statusline(json, dir))));
        Assert.Empty(Directory.GetFiles(Path.Combine(dir, "usage-widget"), "*.tmp"));
        Assert.Equal(5, Pct(Limits(), "five_hour"));
    }

    [Theory]
    [InlineData(10, 0, 20, 20)]       // same window: the higher usage wins
    [InlineData(30, 0, 10, 30)]       // same window: a stale session's lower usage is ignored
    [InlineData(30, 60, 10, 30)]      // resets a minute apart are the same window
    [InlineData(95, 18000, 2, 2)]     // a new window wins even with lower usage
    [InlineData(2, -18000, 95, 2)]    // a reading from the previous window is ignored
    public void Readings_from_several_sessions_merge(double oldPct, long newResetOffset, double newPct, double expected)
    {
        ConfigDir();
        SetLimits((oldPct, R5), null);
        Feed.Statusline(Input((newPct, R5 + newResetOffset), null), _dir);
        Assert.Equal(expected, Pct(Limits(), "five_hour"));
    }

    [Fact]
    public void Keeps_a_window_the_new_reading_does_not_carry()
    {
        ConfigDir();
        SetLimits((10, R5), (6, R7));
        Feed.Statusline(Input((12, R5), null), _dir);
        var l = Limits();
        Assert.Equal(12, Pct(l, "five_hour"));
        Assert.Equal(6, Pct(l, "seven_day"));
        Assert.Equal(R7, Reset(l, "seven_day"));
    }

    [Fact]
    public void Replaces_a_corrupt_limits_file()
    {
        ConfigDir();
        File.WriteAllText(WidgetFile("limits.json"), "{ not json");
        Feed.Statusline(Input((7, R5), (1, R7)), _dir);
        Assert.Equal(7, Pct(Limits(), "five_hour"));
    }

    // ---- the line it prints ----------------------------------------------------------

    [Theory]
    [InlineData(12, "32", 12)]
    [InlineData(69.6, "33", 70)]   // rounds to 70
    [InlineData(89, "33", 89)]
    [InlineData(90, "31", 90)]
    public void Shows_the_model_and_the_5h_usage_in_colour(double pct, string code, int shown)
    {
        Assert.Equal($"{Esc}[36m[Opus 5.5]{Esc}[0m | 5h: {Esc}[{code}m{shown}%{Esc}[0m",
            Feed.Statusline(Input((pct, R5), (1, R7)), ConfigDir()));
    }

    [Fact]
    public void Shows_only_the_model_without_limits_and_keeps_accents()
    {
        Assert.Equal($"{Esc}[36m[Haiku]{Esc}[0m", Feed.Statusline("""{"model":{"display_name":"Haiku"}}""", ConfigDir()));
        Assert.Equal($"{Esc}[36m[Ação ✳]{Esc}[0m", Feed.Statusline("""{"model":{"display_name":"Ação ✳"}}""", ConfigDir()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello {")]
    [InlineData("""{"rate_limits":{"five_hour":"x","seven_day":[1,2]}}""")]
    [InlineData("""[1,2,3]""")]
    [InlineData("""{"context_window":"x","session_id":5}""")]
    public void Never_fails_on_bad_input(string input)
    {
        Feed.Statusline(input, ConfigDir());
        Feed.Notify(input, _dir);
    }

    // ---- handing over to the statusline that was there before ---------------------------

    [Fact]
    public void Passes_the_same_JSON_on_without_a_BOM_and_prints_its_output()
    {
        // findstr "^" prints its input back: what the earlier statusline received.
        ConfigDir();
        File.WriteAllText(WidgetFile("wrapped-command.txt"), "findstr \"^\"");
        var input = Input((40, R5), (9, R7), model: "Sonnet");
        var output = Feed.Statusline(input, _dir);
        Assert.Equal(input, output.TrimEnd('\r', '\n'));
        Assert.Equal(40, Pct(Limits(), "five_hour"));   // still recorded
    }

    [Fact]
    public void Falls_back_to_its_own_line_when_the_file_is_empty()
    {
        ConfigDir();
        File.WriteAllText(WidgetFile("wrapped-command.txt"), "  \r\n");
        Assert.Contains("5h: ", Feed.Statusline(Input((12, R5), null), _dir));
    }

    // ---- context files -----------------------------------------------------------------

    private static string ContextInput(string session, long size = 1_000_000, long tokens = 247_316, double? pct = 25) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["session_id"] = session,
            ["model"] = new Dictionary<string, string> { ["id"] = "claude-opus-5-5", ["display_name"] = "Opus 5.5" },
            ["context_window"] = new Dictionary<string, object?>
            {
                ["total_input_tokens"] = tokens, ["total_output_tokens"] = 199, ["context_window_size"] = size,
                ["used_percentage"] = pct, ["remaining_percentage"] = pct is { } p ? 100 - p : null,
            },
        });

    private JsonElement Context(string session) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "usage-widget", "context", session + ".json"))).RootElement;

    [Fact]
    public void Writes_one_context_file_per_session_with_the_window_Claude_Code_reports()
    {
        Feed.Statusline(ContextInput("a76f3eaa-7b50"), ConfigDir());
        Feed.Statusline(ContextInput("other-session", size: 200_000, tokens: 50_000), _dir);
        var c = Context("a76f3eaa-7b50");
        Assert.Equal(1_000_000, c.GetProperty("size").GetInt64());
        Assert.Equal(247_316, c.GetProperty("tokens").GetInt64());
        Assert.Equal(25, c.GetProperty("used_percentage").GetDouble());
        Assert.Equal("claude-opus-5-5", c.GetProperty("model").GetString());
        Assert.Equal(200_000, Context("other-session").GetProperty("size").GetInt64());
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "usage-widget", "context"), "*.tmp"));
    }

    [Fact]
    public void Records_the_window_before_the_first_answer()
    {
        Feed.Statusline(ContextInput("s1", tokens: 0, pct: null), ConfigDir());
        Assert.Equal(JsonValueKind.Null, Context("s1").GetProperty("used_percentage").ValueKind);
    }

    [Theory]
    [InlineData(@"..\..\evil")]
    [InlineData("a/b")]
    [InlineData("")]
    public void Never_writes_outside_its_folder_whatever_the_session_id(string id)
    {
        Feed.Statusline(ContextInput(id), ConfigDir());
        Assert.False(Directory.Exists(Path.Combine(_dir, "usage-widget", "context")));
        Assert.Empty(Directory.GetFiles(Isolation.Root, "evil*", SearchOption.AllDirectories));
    }

    // ---- the Notification hook ----------------------------------------------------------

    [Fact]
    public void A_notification_becomes_one_line_in_events_jsonl()
    {
        var dir = ConfigDir();
        Feed.Notify("""{"session_id":"s1","hook_event_name":"Notification","notification_type":"permission_prompt","message":"Claude needs your permission to use Bash"}""", dir);
        Feed.Notify("""{"session_id":"s1","hook_event_name":"Notification","message":"Ação ✳"}""", dir);
        var lines = File.ReadAllLines(WidgetFile("events.jsonl"));
        Assert.Equal(2, lines.Length);
        var first = JsonDocument.Parse(lines[0]).RootElement;
        Assert.Equal("s1", first.GetProperty("session_id").GetString());
        Assert.Equal("permission_prompt", first.GetProperty("type").GetString());
        Assert.Equal("Claude needs your permission to use Bash", first.GetProperty("message").GetString());
        Assert.Equal("Ação ✳", JsonDocument.Parse(lines[1]).RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public void A_notification_without_a_session_is_dropped()
    {
        Feed.Notify("""{"message":"x"}""", ConfigDir());
        Assert.False(File.Exists(WidgetFile("events.jsonl")));
    }

    [Fact]
    public void The_events_file_starts_over_past_200_KB()
    {
        ConfigDir();
        File.WriteAllText(WidgetFile("events.jsonl"), new string('x', 210 * 1024));
        Feed.Notify("""{"session_id":"s1","message":"new"}""", _dir);
        Assert.Single(File.ReadAllLines(WidgetFile("events.jsonl")));
    }

    // ---- the program, as Claude Code runs it --------------------------------------------

    private static string? FeedExe()
    {
        var here = Path.Combine(AppContext.BaseDirectory, "ClaudeUsageFeed.exe");
        return File.Exists(here) ? here : null;
    }

    [Fact]
    public void The_program_reads_stdin_prints_UTF8_and_always_exits_0()
    {
        var exe = FeedExe();
        Assert.True(exe is not null, "ClaudeUsageFeed.exe is not next to the tests");
        foreach (var (args, input, expected) in new[]
                 {
                     ("statusline", """{"model":{"display_name":"Ação"}}""", $"{Esc}[36m[Ação]{Esc}[0m"),
                     ("statusline", "not json", ""),
                     ("notify", """{"session_id":"s9","message":"m"}""", ""),
                 })
        {
            var psi = new ProcessStartInfo(exe!, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, StandardOutputEncoding = new UTF8Encoding(false),
            };
            psi.Environment["CLAUDE_CONFIG_DIR"] = ConfigDir();
            using var p = Process.Start(psi)!;
            var bytes = new UTF8Encoding(false).GetBytes(input);
            p.StandardInput.BaseStream.Write(bytes);
            p.StandardInput.BaseStream.Close();
            var output = p.StandardOutput.ReadToEnd();
            Assert.True(p.WaitForExit(30_000));
            Assert.Equal(0, p.ExitCode);
            Assert.Equal(expected, output);
        }
        Assert.True(File.Exists(WidgetFile("events.jsonl")));
    }
}
