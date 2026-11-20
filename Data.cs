using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeUsageWidget;

public static class Paths
{
    public static readonly string Dir = Path.Combine(
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
        "usage-widget");

    public static readonly string Limits = Path.Combine(Dir, "limits.json");
    /// <summary>Limits read from the CLI's own output on the last hello.</summary>
    public static readonly string Ping = Path.Combine(Dir, "ping.json");
    /// <summary>Lines appended by the Notification hook (notify.ps1).</summary>
    public static readonly string Events = Path.Combine(Dir, "events.jsonl");
    /// <summary>Recent readings, for the pace projection, and what was already announced.</summary>
    public static readonly string History = Path.Combine(Dir, "history.json");
    public static readonly string Config = Path.Combine(Dir, "config.json");
    public static readonly string State = Path.Combine(Dir, "state.json");
    public static readonly string Log = Path.Combine(Dir, "widget.log");
}

public static class Json
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static T Load<T>(string path) where T : new()
    {
        try
        {
            if (File.Exists(path))
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return JsonSerializer.Deserialize<T>(fs, Options) ?? new T();
            }
        }
        catch (Exception ex) { Log.Write($"Falha a ler {Path.GetFileName(path)}: {ex.Message}"); }
        return new T();
    }

    public static void Save<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Paths.Dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
            File.Move(tmp, path, true);
        }
        catch (Exception ex) { Log.Write($"Falha a gravar {Path.GetFileName(path)}: {ex.Message}"); }
    }
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.Dir);
                var info = new FileInfo(Paths.Log);
                if (info.Exists && info.Length > 512 * 1024) File.Delete(Paths.Log);
                File.AppendAllText(Paths.Log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}

/// <summary>One rate-limit window as written by the statusline script.</summary>
public class LimitWindow
{
    [JsonPropertyName("used_percentage")] public double? UsedPercentage { get; set; }
    [JsonPropertyName("resets_at")] public long? ResetsAt { get; set; }
}

public class Limits
{
    [JsonPropertyName("updated_at")] public long UpdatedAt { get; set; }
    [JsonPropertyName("five_hour")] public LimitWindow? FiveHour { get; set; }
    [JsonPropertyName("seven_day")] public LimitWindow? SevenDay { get; set; }
    /// <summary>True when the 5-hour window is a guess, not a reported value.</summary>
    [JsonPropertyName("estimated")] public bool Estimated { get; set; }
    /// <summary>Paid extra usage, as the CLI reports it on a hello. The statusline never carries it.</summary>
    [JsonPropertyName("overage")] public OverageInfo? Overage { get; set; }
}

/// <summary>
/// From a hello's rate_limit_event: whether Claude is going on past a used-up
/// limit with extra usage, billed apart from the subscription.
/// </summary>
public class OverageInfo
{
    [JsonPropertyName("using")] public bool Using { get; set; }
    /// <summary>The account's extra-usage status, e.g. "allowed".</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    /// <summary>The limit that is binding: "five_hour", "seven_day"...</summary>
    [JsonPropertyName("limit_type")] public string LimitType { get; set; } = "";
    /// <summary>When that limit resets, which ends the extra usage (unix seconds).</summary>
    [JsonPropertyName("resets_at")] public long ResetsAt { get; set; }

    public bool ActiveAt(long now) => Using && ResetsAt > now;
}

public class AutoHelloConfig
{
    public bool Enabled { get; set; }
    /// <summary>
    /// "reset": start a new window as soon as the old one expires, inside From-To.
    /// "schedule": start windows at the times in Schedule, so they line up with the day.
    /// </summary>
    public string Mode { get; set; } = "reset";
    /// <summary>Start times for "schedule" mode, e.g. "07:00, 12:00, 17:00".</summary>
    public string Schedule { get; set; } = "07:00, 12:00, 17:00";
    public string From { get; set; } = "08:00";
    public string To { get; set; } = "20:00";
    public bool WeekdaysOnly { get; set; } = true;
    public string Model { get; set; } = "haiku";
    public string Message { get; set; } = "hello";
}

public class AlertsConfig
{
    public bool Enabled { get; set; } = true;
    public bool Sound { get; set; } = true;
    /// <summary>A finished task shorter than this is not worth an alert.</summary>
    public int MinTaskSeconds { get; set; } = 30;
    /// <summary>Repeat an unanswered alert this often. 0 = once only.</summary>
    public int RemindMinutes { get; set; } = 2;
}

public class ContextAlertsConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>Announce when a session's context quality score drops below this. 0 = never.</summary>
    public int QualityBelow { get; set; } = 50;
    /// <summary>Announce when a session's context is this full, in percent. 0 = never.</summary>
    public int FillAbove { get; set; } = 80;
}

public class UsageAlertsConfig
{
    /// <summary>Announce when a limit crosses each of these percentages. Empty = never.</summary>
    public int[] Levels { get; set; } = [80, 95];
    /// <summary>Announce when the current pace would exhaust a limit before it resets.</summary>
    public bool Forecast { get; set; } = true;
    /// <summary>Announce, once a day, when today has used more than its share of the weekly limit.</summary>
    public bool DailyBudget { get; set; } = true;
    /// <summary>Share the weekly limit over working days only (today always counts).</summary>
    public bool BudgetWeekdaysOnly { get; set; } = true;
}

public class Config
{
    public UsageAlertsConfig UsageAlerts { get; set; } = new();
    public ContextAlertsConfig ContextAlerts { get; set; } = new();
    /// <summary>
    /// Do not disturb: no notifications or sounds until this moment (unix
    /// seconds). -1 = until switched off, 0 = off. The widget still pulses.
    /// </summary>
    public long QuietUntil { get; set; }
    public AutoHelloConfig AutoHello { get; set; } = new();
    public AlertsConfig Alerts { get; set; } = new();
    /// <summary>
    /// Minimum minutes between pings that refresh the numbers while Claude is
    /// in use. Each ping is one short message on the hello model. 0 = never.
    /// </summary>
    public int RefreshMinutes { get; set; } = 15;
    /// <summary>Also list plugin/automation (SDK) sessions in the sessions popup.</summary>
    public bool ShowSdkSessions { get; set; }
    /// <summary>
    /// Sessions hidden by hand: session id -> its statusUpdatedAt when hidden.
    /// One shows again once its status changes or it raises a new alert.
    /// </summary>
    public Dictionary<string, long> HiddenSessions { get; set; } = new();
    /// <summary>
    /// Hide desktop-app sessions that have been idle this long. The app keeps a
    /// process (and so a "live" session) for conversations you are not using. 0 = never.
    /// </summary>
    public int HideIdleDesktopMinutes { get; set; } = 30;
    public double Opacity { get; set; } = 0.9;
    public bool AlwaysOnTop { get; set; } = true;
    public int WarnAt { get; set; } = 70;
    public int CritAt { get; set; } = 90;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public string ClaudePath { get; set; } = "claude";
    /// <summary>System-wide shortcut to the session waiting for you, e.g. "Ctrl+Alt+C". Empty = none.</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+C";
}

public class State
{
    /// <summary>Consecutive failed hellos; drives the retry backoff.</summary>
    public int Attempts { get; set; }
    public long NextAttemptAt { get; set; }
    /// <summary>The window (its resetsAt) already pinged on reaching 100%, to learn about extra usage.</summary>
    public long FullPingFor { get; set; }
}
