namespace ClaudeUsageWidget.Tests;

/// <summary>Reading `claude -p --output-format stream-json --verbose` output.</summary>
public class ClaudeOutputTests
{
    // Captured from Claude Code 2.1.329 on 2026-11-20 (ids blanked).
    private const string RateLimitEvent =
        """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1795137600,"rateLimitType":"five_hour","overageStatus":"allowed","overageResetsAt":1796083200,"isUsingOverage":false,"unifiedWindows":{"five_hour":{"utilization":0.07,"resetsAt":1795137600,"status":"allowed"},"seven_day":{"utilization":0.05,"resetsAt":1795543200,"status":"allowed"}}},"uuid":"X","session_id":"X"}""";

    // The result event, shortened; note "type" comes after the usage figures.
    private const string Result =
        """{"duration_api_ms":735,"stop_reason":"end_turn","session_id":"X","usage":{"iterations":[{"type":"message"}]},"type":"result","subtype":"success","is_error":false,"result":"Hello!"}""";

    private const string Init = """{"type":"system","subtype":"init","tools":["Bash"]}""";

    [Fact]
    public void ParseLimits_reads_both_windows()
    {
        var limits = AutoHello.ParseLimits(string.Join('\n', Init, RateLimitEvent, Result))!;

        Assert.Equal(7, limits.FiveHour!.UsedPercentage!.Value, precision: 6);
        Assert.Equal(1795137600, limits.FiveHour.ResetsAt);
        Assert.Equal(5, limits.SevenDay!.UsedPercentage!.Value, precision: 6);
        Assert.Equal(1795543200, limits.SevenDay.ResetsAt);
    }

    [Fact]
    public void ParseLimits_takes_the_last_event()
    {
        var later = RateLimitEvent.Replace("\"utilization\":0.07", "\"utilization\":0.08");
        Assert.Equal(8, AutoHello.ParseLimits(RateLimitEvent + "\n" + later)!.FiveHour!.UsedPercentage!.Value, precision: 6);
    }

    [Fact]
    public void ParseLimits_without_an_event_or_with_junk_is_null()
    {
        Assert.Null(AutoHello.ParseLimits(Init + "\n" + Result));
        Assert.Null(AutoHello.ParseLimits("""{"type":"rate_limit_event", broken"""));
        Assert.Null(AutoHello.ParseLimits(""));
    }

    [Fact]
    public void ResultError_finds_the_result_event_wherever_type_is()
    {
        Assert.False(AutoHello.ResultError(Result));
        Assert.True(AutoHello.ResultError(Result.Replace("\"is_error\":false", "\"is_error\":true")));
    }

    [Theory]
    [InlineData(Init)]
    [InlineData(RateLimitEvent)]
    [InlineData("""{"type":"user","message":{"content":[{"type":"tool_result","content":"result"}]}}""")]
    [InlineData("""{"type":5,"result":"x"}""")]
    [InlineData("""["result"]""")]
    [InlineData("not json \"result\"")]
    public void ResultError_is_null_for_other_lines(string line)
    {
        Assert.Null(AutoHello.ResultError(line));
    }

    [Fact]
    public void FailureDetail_prefers_stderr()
    {
        Assert.Equal("boom", AutoHello.FailureDetail("  boom \n", Init));
    }

    [Fact]
    public void FailureDetail_explains_from_the_result_event()
    {
        var failed = """{"type":"result","subtype":"error_during_execution","is_error":true,"result":"Not logged in · Please run /login"}""";
        Assert.Equal("error_during_execution: Not logged in · Please run /login",
            AutoHello.FailureDetail("", Init + "\n" + failed));
    }

    [Fact]
    public void FailureDetail_falls_back_to_the_last_line_or_says_there_was_nothing()
    {
        Assert.Equal(Init, AutoHello.FailureDetail("", Init));
        Assert.Equal("sem saída", AutoHello.FailureDetail("", ""));
        Assert.Equal(500, AutoHello.FailureDetail(new string('x', 900), "").Length);
    }
}
