using System.Globalization;
using System.IO;
using System.Text.Json;

namespace ClaudeUsageWidget;

/// <summary>
/// What Claude Code's /usage last showed. /usage asks another endpoint than
/// the one whose answers feed the statusline and the hello, and the two do not
/// round alike: the answers carry the use cut to whole percents, so for the
/// same moment /usage can read a point higher (5% against 4%). Claude Code
/// keeps that endpoint's last answer in its global config
/// (cachedUsageUtilization), so the widget can agree with /usage without
/// asking anything itself. It only changes when someone opens /usage.
/// </summary>
public static class UsageScreen
{
    /// <summary>Claude Code's global config: in CLAUDE_CONFIG_DIR when set, else ~/.claude.json.</summary>
    public static string ConfigFile =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
            ? Path.Combine(dir, ".claude.json")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

    /// <summary>
    /// Reads /usage's last answer. False when the file could not be read or
    /// parsed (Claude Code may be writing it): keep what was read before.
    /// True with null when there is none.
    /// </summary>
    public static bool TryLoad(string path, out Limits? usage)
    {
        usage = null;
        try
        {
            if (!File.Exists(path)) return true;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            usage = Parse(doc.RootElement);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static Limits? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("cachedUsageUtilization", out var cache) || cache.ValueKind != JsonValueKind.Object)
            return null;

        // Claude Code drops another account's answer only when it next reads it.
        if (Text(cache, "accountUuid") is { } owner &&
            root.TryGetProperty("oauthAccount", out var account) && account.ValueKind == JsonValueKind.Object &&
            Text(account, "accountUuid") is { } current && owner != current)
            return null;

        if (!cache.TryGetProperty("fetchedAtMs", out var fetched) || fetched.ValueKind != JsonValueKind.Number || !fetched.TryGetDouble(out var ms) ||
            !cache.TryGetProperty("utilization", out var windows) || windows.ValueKind != JsonValueKind.Object)
            return null;

        var five = Window(windows, "five_hour");
        var week = Window(windows, "seven_day");
        if (five is null && week is null) return null;
        return new Limits { UpdatedAt = (long)(ms / 1000), FiveHour = five, SevenDay = week };
    }

    /// <summary>
    /// The statusline's or the hello's reading, raised to what /usage showed
    /// for the same window. Use only grows inside a window, so the higher
    /// figure is never the stale one; a later window wins whoever has it.
    /// </summary>
    public static Limits Merge(Limits reading, Limits? usage)
    {
        if (usage is null) return reading;
        // A hello without limits guessed a window opening at the hello, so it
        // always ends later than the real one; a window /usage saw still
        // running at that time shows the guess was wrong.
        var five = reading.Estimated && usage.FiveHour?.ResetsAt > reading.UpdatedAt
            ? usage.FiveHour
            : Pick(reading.FiveHour, usage.FiveHour);
        var week = Pick(reading.SevenDay, usage.SevenDay);
        if (ReferenceEquals(five, reading.FiveHour) && ReferenceEquals(week, reading.SevenDay)) return reading;
        return new Limits
        {
            UpdatedAt = Math.Max(reading.UpdatedAt, usage.UpdatedAt),
            FiveHour = five,
            SevenDay = week,
            // A 5-hour window /usage reported is no longer a guess.
            Estimated = reading.Estimated && ReferenceEquals(five, reading.FiveHour),
            Overage = reading.Overage,
        };
    }

    private static LimitWindow? Pick(LimitWindow? reading, LimitWindow? usage)
    {
        if (usage?.ResetsAt is not { } u) return reading;
        if (reading?.ResetsAt is not { } r || u > r + 60) return usage;
        if (u < r - 60) return reading;
        return (usage.UsedPercentage ?? 0) > (reading.UsedPercentage ?? 0)
            ? new LimitWindow { UsedPercentage = usage.UsedPercentage, ResetsAt = r }
            : reading;
    }

    /// <summary>{"utilization": 5, "resets_at": "2026-11-23T02:10:00.948+00:00"}; null without a running window.</summary>
    private static LimitWindow? Window(JsonElement windows, string name)
    {
        if (!windows.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object ||
            !w.TryGetProperty("utilization", out var used) || used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var pct) ||
            Text(w, "resets_at") is not { } resets ||
            !DateTimeOffset.TryParse(resets, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
            return null;
        return new LimitWindow { UsedPercentage = pct, ResetsAt = at.ToUnixTimeSeconds() };
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
