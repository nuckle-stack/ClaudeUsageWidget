using System.Globalization;

namespace ClaudeUsageWidget;

/// <summary>One reading of both limits, taken at T (unix seconds).</summary>
public class Sample
{
    public long T { get; set; }
    public double P5 { get; set; }
    public long R5 { get; set; }
    public double P7 { get; set; }
    public long R7 { get; set; }
}

/// <summary>What has already been announced for one window, so it is said once.</summary>
public class AlertMark
{
    public long ResetsAt { get; set; }
    public int Level { get; set; }
    public bool Forecast { get; set; }
}

public class HistoryFile
{
    public List<Sample> Samples { get; set; } = new();
    public AlertMark Mark5 { get; set; } = new();
    public AlertMark Mark7 { get; set; } = new();
    /// <summary>The used-up window (its resetsAt) whose renewal was already announced.</summary>
    public long Renewed5 { get; set; }
    public long Renewed7 { get; set; }
    /// <summary>The day (yyyy-MM-dd, local) whose over-budget alert was already given.</summary>
    public string BudgetDay { get; set; } = "";
}

/// <summary>Today's slice of the weekly limit, in percentage points of it.</summary>
/// <param name="UsedToday">Weekly usage added since local midnight.</param>
/// <param name="Allowance">What was left at midnight, shared out over the days to the reset.</param>
/// <param name="Days">The days that share it: today and the (working) days after it.</param>
/// <param name="Reliable">False when there is no reading from before midnight to start from.</param>
public record DailyBudget(double UsedToday, double Allowance, int Days, long ResetsAt, bool Reliable)
{
    public bool Over => UsedToday > Allowance;
}

/// <param name="ExhaustAt">When usage reaches 100% at the current pace, if that is before the reset.</param>
/// <param name="AtReset">Projected usage at the reset, capped at 100.</param>
/// <param name="Reliable">False when the pace is only a guess from a single reading.</param>
public record Projection(double Percent, long ResetsAt, DateTimeOffset? ExhaustAt, double AtReset, bool Reliable);

/// <summary>
/// Keeps the recent readings and projects, at the current pace, whether a
/// limit runs out before it resets.
/// </summary>
public class UsageHistory
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-PT");
    private const int KeepSeconds = 8 * 86400;

    private readonly HistoryFile _file;
    /// <summary>Where to save; null keeps everything in memory (tests).</summary>
    private readonly string? _path;

    public UsageHistory() : this(Json.Load<HistoryFile>(Paths.History), Paths.History) { }

    internal UsageHistory(HistoryFile file, string? path = null)
    {
        _file = file;
        _path = path;
    }

    private void Save()
    {
        if (_path is not null) Json.Save(_path, _file);
    }

    /// <summary>The readings of one limit since a moment, oldest first, for the history chart.</summary>
    public List<Sparkline.Point> Series(bool fiveHour, long since) =>
        _file.Samples
            .Where(s => s.T >= since && Pick(s, fiveHour).ResetsAt > 0)
            .Select(s => new Sparkline.Point(s.T, Pick(s, fiveHour).Percent, Pick(s, fiveHour).ResetsAt))
            .ToList();

    /// <summary>Records a reading. Returns true when it is new.</summary>
    public bool Add(Limits cur)
    {
        if (cur.Estimated || cur.UpdatedAt <= 0 || cur.FiveHour?.ResetsAt is null) return false;
        if (_file.Samples.Count > 0 && _file.Samples[^1].T >= cur.UpdatedAt) return false;

        _file.Samples.Add(new Sample
        {
            T = cur.UpdatedAt,
            P5 = cur.FiveHour.UsedPercentage ?? 0,
            R5 = cur.FiveHour.ResetsAt ?? 0,
            P7 = cur.SevenDay?.UsedPercentage ?? 0,
            R7 = cur.SevenDay?.ResetsAt ?? 0,
        });
        _file.Samples.RemoveAll(s => s.T < cur.UpdatedAt - KeepSeconds);
        Save();
        return true;
    }

    public Projection? Project(bool fiveHour)
    {
        if (_file.Samples.Count == 0) return null;
        var last = _file.Samples[^1];
        var (pct, resetsAt) = Pick(last, fiveHour);
        if (resetsAt <= 0) return null;

        // Pace from the recent past when there is enough of it, else from the
        // whole window, else assume steady use since the window opened.
        // The weekly pace needs at least a full day: an hour of heavy use,
        // carried on through nights and days off, "runs out" in a couple of days.
        var (window, lookback, minSpan) = fiveHour ? (5 * 3600, 3600, 600) : (7 * 86400, 2 * 86400, 86400);
        var same = _file.Samples.Where(s => Math.Abs(Pick(s, fiveHour).ResetsAt - resetsAt) <= 60).ToList();
        var from = same.FirstOrDefault(s => s.T >= last.T - lookback && last.T - s.T >= minSpan)
                   ?? same.FirstOrDefault(s => last.T - s.T >= minSpan);

        double rate;   // percent per second
        var reliable = from is not null;
        if (from is not null) rate = (pct - Pick(from, fiveHour).Percent) / (last.T - from.T);
        else
        {
            var open = last.T - (resetsAt - window);
            rate = open > 0 ? pct / open : 0;
        }
        if (rate < 0) rate = 0;

        var atReset = Math.Min(100, pct + rate * Math.Max(0, resetsAt - last.T));
        DateTimeOffset? exhaust = null;
        if (pct >= 100) exhaust = DateTimeOffset.FromUnixTimeSeconds(last.T);
        else if (rate > 0)
        {
            var at = last.T + (100 - pct) / rate;
            if (at < resetsAt) exhaust = DateTimeOffset.FromUnixTimeSeconds((long)at);
        }
        return new Projection(pct, resetsAt, exhaust, atReset, reliable);
    }

    /// <summary>One line for the tooltip and the panel; empty when there is nothing to say.</summary>
    public string Describe(bool fiveHour)
    {
        var p = Project(fiveHour);
        var now = DateTimeOffset.UtcNow;
        if (p is null || now.ToUnixTimeSeconds() >= p.ResetsAt) return "";

        var guess = p.Reliable ? "" : "≈ ";
        if (p.Percent >= 100) return "esgotado";
        if (p.ExhaustAt is not { } at) return $"~{Math.Round(p.AtReset)}% no reset";

        var early = DateTimeOffset.FromUnixTimeSeconds(p.ResetsAt) - at;
        return $"{guess}a este ritmo esgota {When(at, fiveHour)}, {Span(early)} antes do reset";
    }

    /// <summary>Compact form for the widget's time column, e.g. "16:20" or "qui 14h".</summary>
    public static string Short(DateTimeOffset at, bool fiveHour) =>
        fiveHour ? at.LocalDateTime.ToString("HH:mm") : at.LocalDateTime.ToString("ddd HH", Pt).Replace(".", "") + "h";

    /// <summary>Threshold and pace alerts that have not been announced yet for the current windows.</summary>
    public List<(string Title, string Text)> PendingAlerts(UsageAlertsConfig cfg)
    {
        var result = new List<(string, string)>();
        var changed = false;
        foreach (var fiveHour in new[] { true, false })
        {
            var p = Project(fiveHour);
            if (p is null || DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= p.ResetsAt) continue;
            var mark = fiveHour ? _file.Mark5 : _file.Mark7;
            if (Math.Abs(mark.ResetsAt - p.ResetsAt) > 60)
            {
                mark.ResetsAt = p.ResetsAt;
                mark.Level = 0;
                mark.Forecast = false;
                changed = true;
            }

            var name = fiveHour ? "Limite de 5 horas" : "Limite semanal";
            var reset = "Renova " + When(DateTimeOffset.FromUnixTimeSeconds(p.ResetsAt), fiveHour) + ".";
            var level = cfg.Levels.Where(l => l > 0 && p.Percent >= l).DefaultIfEmpty(0).Max();
            var running = p.Reliable && p.ExhaustAt is not null && p.Percent < 100;

            if (level > mark.Level)
            {
                mark.Level = level;
                // The level alert carries the pace warning when there is one.
                if (running) mark.Forecast = true;
                var extra = running ? " " + Capital(Describe(fiveHour)) + "." : "";
                result.Add(($"{name} a {Math.Round(p.Percent)}%", reset + extra));
                changed = true;
            }
            else if (cfg.Forecast && running && !mark.Forecast)
            {
                mark.Forecast = true;
                result.Add(($"{name}: ritmo alto", $"{Capital(Describe(fiveHour))}. Vai em {Math.Round(p.Percent)}%."));
                changed = true;
            }
        }
        if (changed) Save();
        return result;
    }

    /// <summary>
    /// A used-up limit (seen at 100%) that has just reset: you were blocked, or
    /// paying for extra usage, and no longer are. Said once per window, and only
    /// within an hour of the reset; a widget started long after it stays quiet.
    /// </summary>
    public List<(string Title, string Text)> PendingRenewals(long now)
    {
        var result = new List<(string, string)>();
        var changed = false;
        foreach (var fiveHour in new[] { true, false })
        {
            // The latest window that reached 100% (rounding can leave it a hair short).
            var full = _file.Samples.LastOrDefault(s => Pick(s, fiveHour).ResetsAt > 0 && Pick(s, fiveHour).Percent >= 99.5);
            if (full is null) continue;
            var resetsAt = Pick(full, fiveHour).ResetsAt;
            var announced = fiveHour ? _file.Renewed5 : _file.Renewed7;
            if (now < resetsAt || announced == resetsAt) continue;

            if (fiveHour) _file.Renewed5 = resetsAt; else _file.Renewed7 = resetsAt;
            changed = true;
            if (now - resetsAt > 3600) continue;
            result.Add((fiveHour ? "Limite de 5 horas renovado" : "Limite semanal renovado",
                "Já podes voltar a usar o Claude normalmente."));
        }
        if (changed) Save();
        return result;
    }

    /// <summary>
    /// How much of the weekly limit today may take, and how much it has: what
    /// was left at midnight shared equally over today and the days to the
    /// reset. Null without a running weekly window.
    /// </summary>
    public DailyBudget? Budget(DateTime nowLocal, bool weekdaysOnly)
    {
        var last = _file.Samples.LastOrDefault(s => s.R7 > 0);
        if (last is null) return null;
        var resetsAt = last.R7;
        var now = new DateTimeOffset(nowLocal).ToUnixTimeSeconds();
        if (now >= resetsAt) return null;

        var midnight = new DateTimeOffset(nowLocal.Date).ToUnixTimeSeconds();
        var same = _file.Samples.Where(s => Math.Abs(s.R7 - resetsAt) <= 60).ToList();
        var before = same.LastOrDefault(s => s.T < midnight);
        double start;
        var reliable = true;
        if (before is not null) start = before.P7;
        else if (resetsAt - 7 * 86400 >= midnight) start = 0;   // the week began today
        else
        {
            // No reading from before midnight (the widget was off): the first of
            // today is the best start there is, and it undercounts today.
            start = same.FirstOrDefault(s => s.T >= midnight)?.P7 ?? last.P7;
            reliable = false;
        }

        var days = DaysToShare(nowLocal, DateTimeOffset.FromUnixTimeSeconds(resetsAt).LocalDateTime, weekdaysOnly);
        return new DailyBudget(Math.Max(0, last.P7 - start), Math.Max(0, 100 - start) / days, days, resetsAt, reliable);
    }

    /// <summary>
    /// Today, always (it is the day being spent), then each day up to the
    /// reset, weekends left out when asked; the reset day counts when the reset
    /// comes after noon, leaving most of that day to work in.
    /// </summary>
    internal static int DaysToShare(DateTime todayLocal, DateTime resetLocal, bool weekdaysOnly)
    {
        var days = 1;
        for (var d = todayLocal.Date.AddDays(1); d <= resetLocal.Date; d = d.AddDays(1))
        {
            if (d == resetLocal.Date && resetLocal.TimeOfDay < TimeSpan.FromHours(12)) break;
            if (!weekdaysOnly || d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) days++;
        }
        return days;
    }

    /// <summary>"Hoje: 9% de 14% do limite semanal"; empty without a weekly window.</summary>
    public static string BudgetText(DailyBudget? b)
    {
        if (b is null) return "";
        static string P(double v) => v is > 0 and < 1 ? "<1%" : $"{Math.Round(v)}%";
        return $"Hoje: {(b.Reliable ? "" : "≈ ")}{P(b.UsedToday)} de {P(b.Allowance)} do limite semanal";
    }

    /// <summary>Once a day, when today has taken more than its share of the week.</summary>
    public (string Title, string Text)? PendingBudgetAlert(DateTime nowLocal, UsageAlertsConfig cfg)
    {
        if (!cfg.DailyBudget || Budget(nowLocal, cfg.BudgetWeekdaysOnly) is not { Reliable: true, Over: true } b) return null;
        var day = nowLocal.ToString("yyyy-MM-dd");
        if (_file.BudgetDay == day) return null;
        _file.BudgetDay = day;
        Save();

        var reset = When(DateTimeOffset.FromUnixTimeSeconds(b.ResetsAt), fiveHour: false);
        var left = b.Days - 1;
        var rest = left > 0 ? $" O que gastares a mais sai dos próximos {left} dia{(left == 1 ? "" : "s")}." : "";
        return ("Parte de hoje do limite semanal gasta",
            $"Hoje já usaste {Math.Round(b.UsedToday)}% do limite semanal, mais do que os {Math.Round(b.Allowance)}% " +
            $"que cabem por dia até ao reset ({reset}).{rest}");
    }

    private static (double Percent, long ResetsAt) Pick(Sample s, bool fiveHour) =>
        fiveHour ? (s.P5, s.R5) : (s.P7, s.R7);

    internal static string When(DateTimeOffset at, bool fiveHour)
    {
        var local = at.LocalDateTime;
        return fiveHour || local.Date == DateTime.Today
            ? "às " + local.ToString("HH:mm")
            : local.ToString("ddd", Pt).Replace(".", "") + " às " + local.ToString("HH:mm");
    }

    private static string Span(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d{t.Hours:D2}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h{t.Minutes:D2}";
        return $"{Math.Max(1, (int)t.TotalMinutes)} min";
    }

    private static string Capital(string s) =>
        s.Length == 0 ? s : char.ToUpper(s[0], Pt) + s[1..];
}
