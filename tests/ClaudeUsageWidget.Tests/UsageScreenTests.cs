using System.Text.Json;

namespace ClaudeUsageWidget.Tests;

/// <summary>Reading what Claude Code's /usage last showed, and raising the statusline's figures to it.</summary>
public class UsageScreenTests
{
    private const long Five = 1795399800;   // 2026-11-23T02:10:00Z
    private const long Week = 1795543200;   // 2026-11-24T18:00:00Z

    // From Claude Code 2.1.330's ~/.claude.json on 2026-11-22, shortened (ids made up).
    private const string Config = """
        {
          "numStartups": 12,
          "oauthAccount": { "accountUuid": "acc-1", "organizationUuid": "org-1" },
          "cachedUsageUtilization": {
            "fetchedAtMs": 1795383115690,
            "accountUuid": "acc-1",
            "utilization": {
              "five_hour": { "utilization": 5, "resets_at": "2026-11-23T02:10:00.948015+00:00", "used_dollars": null },
              "seven_day": { "utilization": 30, "resets_at": "2026-11-24T18:00:00.948035+00:00", "used_dollars": null },
              "seven_day_opus": null,
              "extra_usage": { "is_enabled": true, "utilization": null },
              "seven_day_breakdown": {
                "as_of": "2026-11-22T21:31:55.690515+00:00",
                "window_started_at": "2026-11-17T18:00:00.948035+00:00",
                "rows": [
                  { "key": "claude_code", "display_name": "Claude Code", "percent": 94, "percent_of_limit": 28 },
                  { "key": "chat", "display_name": "Chats", "percent": 0, "percent_of_limit": 0 },
                  { "key": "cowork", "display_name": "Cowork", "percent": 6, "percent_of_limit": 2 },
                  { "key": "other", "display_name": "Other", "percent": 0, "percent_of_limit": 0 }
                ]
              }
            }
          }
        }
        """;

    private static Limits? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return UsageScreen.Parse(doc.RootElement);
    }

    private static Limits Reading(long at, double? five, long? fiveResets, double? week = null, long? weekResets = null) => new()
    {
        UpdatedAt = at,
        FiveHour = five is null ? null : new LimitWindow { UsedPercentage = five, ResetsAt = fiveResets },
        SevenDay = week is null ? null : new LimitWindow { UsedPercentage = week, ResetsAt = weekResets },
    };

    [Fact]
    public void Parse_reads_both_windows_and_when()
    {
        var u = Parse(Config)!;

        Assert.Equal(1795383115, u.UpdatedAt);
        Assert.Equal(5, u.FiveHour!.UsedPercentage);
        Assert.Equal(Five, u.FiveHour.ResetsAt);
        Assert.Equal(30, u.SevenDay!.UsedPercentage);
        Assert.Equal(Week, u.SevenDay.ResetsAt);
    }

    [Fact]
    public void ParseWeek_reads_where_the_week_went()
    {
        using var doc = JsonDocument.Parse(Config);
        var w = UsageScreen.ParseWeek(doc.RootElement)!;

        Assert.Equal(Week - 7 * 86400, w.Start);
        Assert.Equal(1795383115, w.AsOf);
        // Nothing went to the chats or elsewhere: left out.
        Assert.Equal([("claude_code", "Claude Code", 28.0, 94.0), ("cowork", "Cowork", 2.0, 6.0)],
            w.Rows.Select(r => (r.Key, r.Name, r.Points, r.Share)));
    }

    private static WeekSources? Sources(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return UsageScreen.ParseWeek(doc.RootElement);
    }

    [Fact]
    public void ParseWeek_is_null_for_another_account_or_without_a_breakdown()
    {
        Assert.Null(Sources(Config.Replace("\"accountUuid\": \"acc-1\", \"organizationUuid\"", "\"accountUuid\": \"acc-2\", \"organizationUuid\"")));
        Assert.Null(Sources(Config.Replace("\"seven_day_breakdown\"", "\"something_else\"")));
        Assert.Null(Sources("""{"cachedUsageUtilization": {"fetchedAtMs": 1, "utilization": {"seven_day_breakdown": {"as_of": "2026-11-22T21:31:55Z", "window_started_at": "2026-11-17T18:00:00Z", "rows": []}}}}"""));
        Assert.Null(Sources("""{"cachedUsageUtilization": {"fetchedAtMs": 1, "utilization": {"seven_day_breakdown": {"as_of": null, "rows": [{"key": "chat", "percent": 5, "percent_of_limit": 1}]}}}}"""));
    }

    [Fact]
    public void ParseWeek_names_an_unknown_place_as_usage_does()
    {
        var w = Sources("""{"cachedUsageUtilization": {"fetchedAtMs": 1, "utilization": {"seven_day_breakdown": {"as_of": "2026-11-22T21:31:55Z", "window_started_at": "2026-11-17T18:00:00Z", "rows": [{"key": "chat", "display_name": "Chats", "percent": 40, "percent_of_limit": 4}, {"key": "new_thing", "display_name": "New thing", "percent": 60, "percent_of_limit": 6}]}}}}""")!;
        Assert.Equal(["New thing", "Conversas (claude.ai)"], w.Rows.Select(r => r.Name));
    }

    [Fact]
    public void Parse_ignores_another_accounts_answer()
    {
        Assert.Null(Parse(Config.Replace("\"accountUuid\": \"acc-1\", \"organizationUuid\"", "\"accountUuid\": \"acc-2\", \"organizationUuid\"")));
        // Without the account to compare with, the answer stands.
        Assert.NotNull(Parse(Config.Replace("\"oauthAccount\": { \"accountUuid\": \"acc-1\", \"organizationUuid\": \"org-1\" },", "")));
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""[]""")]
    [InlineData("""{"cachedUsageUtilization": null}""")]
    [InlineData("""{"cachedUsageUtilization": {"fetchedAtMs": 1795383115690}}""")]
    [InlineData("""{"cachedUsageUtilization": {"fetchedAtMs": "x", "utilization": {"five_hour": {"utilization": 5, "resets_at": "2026-11-23T02:10:00Z"}}}}""")]
    [InlineData("""{"cachedUsageUtilization": {"fetchedAtMs": 1795383115690, "utilization": {"five_hour": null, "seven_day": null}}}""")]
    [InlineData("""{"cachedUsageUtilization": {"fetchedAtMs": 1795383115690, "utilization": {"five_hour": {"utilization": 0, "resets_at": null}}}}""")]
    [InlineData("""{"cachedUsageUtilization": {"fetchedAtMs": 1795383115690, "utilization": {"five_hour": {"utilization": "5", "resets_at": "x"}}}}""")]
    public void Parse_without_a_usable_answer_is_null(string json) => Assert.Null(Parse(json));

    [Fact]
    public void Merge_raises_a_window_to_what_usage_showed()
    {
        // The statusline after /usage, still a point lower: /usage wins, and the time is the newer one.
        var merged = UsageScreen.Merge(Reading(1795383377, 4, Five, 29, Week), Parse(Config));

        Assert.Equal(5, merged.FiveHour!.UsedPercentage);
        Assert.Equal(Five, merged.FiveHour.ResetsAt);
        Assert.Equal(30, merged.SevenDay!.UsedPercentage);
        Assert.Equal(1795383377, merged.UpdatedAt);

        // /usage opened after the last statusline: the reading is as fresh as /usage.
        Assert.Equal(1795383115, UsageScreen.Merge(Reading(1795382536, 4, Five, 29, Week), Parse(Config)).UpdatedAt);
    }

    [Fact]
    public void Merge_keeps_a_reading_that_already_went_past_usage()
    {
        var reading = Reading(1795390000, 12, Five, 31, Week);
        Assert.Same(reading, UsageScreen.Merge(reading, Parse(Config)));
        Assert.Same(reading, UsageScreen.Merge(reading, null));
    }

    [Fact]
    public void Merge_takes_the_later_window_from_either_side()
    {
        var usage = Parse(Config)!;

        // The statusline's 5-hour window ended before /usage was opened in the next one.
        var merged = UsageScreen.Merge(Reading(1795380000, 80, Five - 5 * 3600, 29, Week), usage);
        Assert.Equal(5, merged.FiveHour!.UsedPercentage);
        Assert.Equal(Five, merged.FiveHour.ResetsAt);

        // /usage from the window before: the statusline's newer window stands, even lower.
        var next = Reading(1795400000, 1, Five + 5 * 3600, 31, Week);
        Assert.Same(next, UsageScreen.Merge(next, usage));
    }

    [Fact]
    public void Merge_fills_a_missing_window_and_a_guessed_one()
    {
        var empty = UsageScreen.Merge(new Limits(), Parse(Config));
        Assert.Equal(5, empty.FiveHour!.UsedPercentage);
        Assert.Equal(30, empty.SevenDay!.UsedPercentage);

        // A hello without limits guessed a window that began at the hello; /usage knows better.
        var guess = Reading(1795383000, 0, 1795383000 + 5 * 3600, 29, Week);
        guess.Estimated = true;
        guess.Overage = new OverageInfo { Status = "allowed" };
        var merged = UsageScreen.Merge(guess, Parse(Config));
        Assert.False(merged.Estimated);
        Assert.Equal(Five, merged.FiveHour!.ResetsAt);
        Assert.Equal(5, merged.FiveHour.UsedPercentage);
        Assert.Same(guess.Overage, merged.Overage);

        // /usage from a window that had ended before the hello: the guess stands.
        var later = Reading(Five + 600, 0, Five + 600 + 5 * 3600, 29, Week);
        later.Estimated = true;
        var kept = UsageScreen.Merge(later, Parse(Config));
        Assert.True(kept.Estimated);
        Assert.Same(later.FiveHour, kept.FiveHour);
    }

    [Fact]
    public void TryLoad_tells_no_answer_from_a_file_it_cannot_read()
    {
        var dir = Path.Combine(Isolation.Root, "usage-screen");
        Directory.CreateDirectory(dir);

        Assert.True(UsageScreen.TryLoad(Path.Combine(dir, "missing.json"), out var none));
        Assert.Null(none);

        var good = Path.Combine(dir, "good.json");
        File.WriteAllText(good, Config);
        Assert.True(UsageScreen.TryLoad(good, out var usage));
        Assert.Equal(30, usage!.SevenDay!.UsedPercentage);

        // Claude Code halfway through writing it.
        var half = Path.Combine(dir, "half.json");
        File.WriteAllText(half, Config[..200]);
        Assert.False(UsageScreen.TryLoad(half, out _));
    }

    [Fact]
    public void ConfigFile_follows_CLAUDE_CONFIG_DIR() =>
        Assert.Equal(Path.Combine(Isolation.Root, ".claude.json"), UsageScreen.ConfigFile);
}
