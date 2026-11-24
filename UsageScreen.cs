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
/// <summary>
/// One place the weekly limit went, as /usage breaks it down: Claude Code (on
/// any PC), the chats on claude.ai, Cowork... Points of the weekly limit, and
/// share of the week's use (0..100).
/// </summary>
public record SourceUse(string Key, string Name, double Points, double Share);

/// <summary>/usage's breakdown of the week that started at Start (unix seconds), as of AsOf.</summary>
public record WeekSources(long Start, long AsOf, List<SourceUse> Rows);

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
    public static bool TryLoad(string path, out Limits? usage) => TryLoad(path, out usage, out _);

    /// <summary>The same, with /usage's breakdown of the week.</summary>
    public static bool TryLoad(string path, out Limits? usage, out WeekSources? week) => TryLoad(path, out usage, out week, out _);

    /// <summary>The same, with why there is no breakdown (for the diagnostics).</summary>
    public static bool TryLoad(string path, out Limits? usage, out WeekSources? week, out string missing)
    {
        (usage, week, missing) = (null, null, "");
        try
        {
            if (!File.Exists(path)) return true;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            usage = Parse(doc.RootElement);
            week = ParseWeek(doc.RootElement, out missing);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>/usage's last answer (its "utilization" object) and when it came, for this account.</summary>
    private static (JsonElement Answer, double FetchedMs)? Answer(JsonElement root)
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
            !cache.TryGetProperty("utilization", out var answer) || answer.ValueKind != JsonValueKind.Object)
            return null;
        return (answer, ms);
    }

    public static Limits? Parse(JsonElement root)
    {
        if (Answer(root) is not var (windows, ms)) return null;

        var five = Window(windows, "five_hour");
        var week = Window(windows, "seven_day");
        if (five is null && week is null) return null;
        return new Limits { UpdatedAt = (long)(ms / 1000), FiveHour = five, SevenDay = week };
    }

    /// <summary>
    /// seven_day_breakdown: {"as_of", "window_started_at", "rows": [{"key":
    /// "cowork", "display_name": "Cowork", "percent": 6, "percent_of_limit":
    /// 2}]}. The places nothing went to are left out; null when there is none.
    /// </summary>
    public static WeekSources? ParseWeek(JsonElement root) => ParseWeek(root, out _);

    /// <summary>
    /// Why there is none when the answer has the breakdown, but null: the
    /// account does not get one (seen on a colleague's), so opening /usage
    /// again would not bring it.
    /// </summary>
    public const string Withheld = "não vem na resposta do /usage (seven_day_breakdown: null)";

    /// <summary>The same, saying why there is none (for the diagnostics).</summary>
    public static WeekSources? ParseWeek(JsonElement root, out string missing)
    {
        missing = "";
        if (Answer(root) is not var (answer, _))
        {
            missing = "não há resposta do /usage desta conta";
            return null;
        }
        if (!answer.TryGetProperty("seven_day_breakdown", out var b) || b.ValueKind != JsonValueKind.Object)
        {
            missing = b.ValueKind switch
            {
                JsonValueKind.Undefined => "não vem na resposta do /usage (sem seven_day_breakdown)",
                JsonValueKind.Null => Withheld,
                var kind => $"não vem na resposta do /usage (seven_day_breakdown: {kind.ToString().ToLowerInvariant()})",
            };
            return null;
        }
        if (Time(b, "window_started_at") is not { } start || Time(b, "as_of") is not { } asOf ||
            !b.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            missing = "o seven_day_breakdown não tem as datas ou as linhas";
            return null;
        }

        var list = new List<SourceUse>();
        foreach (var r in rows.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object) continue;
            var (points, share) = (Number(r, "percent_of_limit"), Number(r, "percent"));
            if (points <= 0 && share <= 0) continue;
            var key = Text(r, "key") ?? "";
            list.Add(new SourceUse(key, SourceName(key, Text(r, "display_name") ?? ""), points, share));
        }
        if (list.Count == 0) missing = $"o seven_day_breakdown tem {rows.GetArrayLength()} linha(s), todas a 0";
        return list.Count == 0 ? null : new WeekSources(start, asOf, list.OrderByDescending(s => s.Points).ThenByDescending(s => s.Share).ToList());
    }

    /// <summary>The names /usage gives, in Portuguese where they are known.</summary>
    private static string SourceName(string key, string shown) => key switch
    {
        "claude_code" => "Claude Code",
        "chat" => "Conversas (claude.ai)",
        "cowork" => "Cowork",
        "other" => "Outros",
        _ => shown.Length > 0 ? shown : key,
    };

    private static double Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : 0;

    private static long? Time(JsonElement e, string name) =>
        Text(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToUnixTimeSeconds() : null;

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
