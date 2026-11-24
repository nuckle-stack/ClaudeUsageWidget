using System.IO;
using System.Text.Json.Serialization;

namespace ClaudeUsageWidget;

/// <summary>One day's share of a week's limit, in percentage points of it.</summary>
public record DayUse(DateTime Date, double Percent);

/// <summary>A project's share of a week's Claude Code use on this PC (0..1).</summary>
public record ProjectShare(string Name, double Share);

/// <summary>What one week of the weekly limit went on.</summary>
public class WeekSummary
{
    /// <summary>When the week ends: its reset (unix seconds).</summary>
    public long ResetAt { get; set; }
    /// <summary>How much of the weekly limit it used, in percent.</summary>
    public double Used { get; set; }
    public List<DayUse> Days { get; set; } = new();
    public List<ProjectShare> Projects { get; set; } = new();
    /// <summary>How many 5-hour windows ran out during the week.</summary>
    public int FiveHourMaxed { get; set; }
    /// <summary>The widget was off at the start of the week: the first days it saw carry what came before.</summary>
    public bool Gaps { get; set; }
    /// <summary>
    /// Where the use came from, by /usage (Claude Code on any PC, claude.ai,
    /// Cowork...), as of SourcesAsOf; empty when /usage was not opened that week.
    /// </summary>
    public List<SourceUse> Sources { get; set; } = new();
    public long SourcesAsOf { get; set; }

    [JsonIgnore] public long Start => ResetAt - 7 * 86400;
    [JsonIgnore] public DayUse? Heaviest => Days.Where(d => d.Percent >= 0.5).MaxBy(d => d.Percent);
}

/// <summary>One week in the list of weeks: the running one reads "esta semana".</summary>
public record WeekLine(WeekSummary Week, string Label, string Tip, bool Running);

public class WeeksFile
{
    public List<WeekSummary> Weeks { get; set; } = new();
}

public static class WeekReport
{
    private const int ProjectsShown = 4;

    /// <summary>
    /// Sums up the week that ends at resetAt from the readings of the weekly
    /// limit (the week's own: the reset moves a few seconds between readings)
    /// and the use by project. A week still running is summed up to now.
    /// </summary>
    public static WeekSummary? Build(IReadOnlyList<Sample> samples, long resetAt, Breakdown use, long now)
    {
        var week = samples.Where(s => Math.Abs(s.R7 - resetAt) <= 60).OrderBy(s => s.T).ToList();
        if (week.Count == 0) return null;
        var start = resetAt - 7 * 86400;

        // The weekly percentage only grows within a week, so a moment's value
        // is the last reading at or before it; before the first, nothing was used.
        double At(long t) => week.LastOrDefault(s => s.T <= t)?.P7 ?? 0;

        var days = new List<DayUse>();
        var end = Math.Min(resetAt, now);
        for (var day = Local(start).Date; Unix(day) < end; day = day.AddDays(1))
        {
            var from = Math.Max(start, Unix(day));
            var to = Math.Min(end, Unix(day.AddDays(1)));
            days.Add(new DayUse(day, Math.Max(0, At(to) - At(from))));
        }

        var total = use.Total;
        var projects = total <= 0 ? new List<ProjectShare>() : use.Projects.Take(ProjectsShown).Select(p => new ProjectShare(p.Name, p.Cost / total)).ToList();
        var rest = total <= 0 ? 0 : use.Projects.Skip(ProjectsShown).Sum(p => p.Cost) / total;
        if (rest > 0) projects.Add(new ProjectShare("outros", rest));

        // A 5-hour window is one reset; it ran out when any of its readings did.
        var maxed = samples.Where(s => s.T >= start && s.T < resetAt && s.R5 > 0)
            .GroupBy(s => s.R5 / 60)
            .Count(g => g.Max(s => s.P5) >= 99.5);

        return new WeekSummary
        {
            ResetAt = resetAt,
            Used = week.Max(s => s.P7),
            Days = days,
            Projects = projects,
            FiveHourMaxed = maxed,
            Gaps = week[0].T - start > 86400 && week[0].P7 >= 1,
        };
    }

    /// <summary>
    /// Gives the week /usage's breakdown when it is that week's (it started
    /// when the week did, or /usage was opened while the week ran) and newer
    /// than the one it has. True when it did.
    /// </summary>
    public static bool Attach(WeekSummary week, WeekSources? sources)
    {
        if (sources is null || sources.AsOf <= week.SourcesAsOf) return false;
        var ofWeek = Math.Abs(sources.Start - week.Start) <= 3600 || (sources.AsOf >= week.Start && sources.AsOf < week.ResetAt);
        if (!ofWeek) return false;
        week.Sources = sources.Rows;
        week.SourcesAsOf = sources.AsOf;
        return true;
    }

    /// <summary>
    /// The line under where the week went: which /usage it is from; for the
    /// week running without it, whether /usage was opened this week (usageAt:
    /// when it last was, 0 if never) and left it out, or how to get it; empty
    /// for a week that ended without it, and when the last /usage said this
    /// account gets none (withheld): asking for /usage again would not help.
    /// </summary>
    public static string SourcesNote(WeekSummary week, long usageAt, long now, bool withheld = false)
    {
        var running = week.ResetAt > now;
        if (week.Sources.Count > 0)
            return $"Segundo o /usage de {Moment(week.SourcesAsOf)}." +
                   (running && now - week.SourcesAsOf > 3600 ? " Abre-o outra vez para atualizar." : "");
        if (!running || withheld) return "";
        return usageAt >= week.Start
            ? $"O /usage de {Moment(usageAt)} não diz de onde veio o uso, por isso não há nada para mostrar aqui."
            : "Abre o /usage no Claude Code para ver aqui quanto veio do claude.ai, do Cowork e de outros PCs.";
    }

    /// <summary>"ter 24/11 10:30".</summary>
    public static string Moment(long unix) => Local(unix) is var t ? $"{DayLabel(t)}/{t:MM} {t:HH:mm}" : "";

    /// <summary>The notification when a week ends.</summary>
    public static string Message(WeekSummary week, WeekSummary? previous)
    {
        var text = Headline(week);
        if (previous is not null) text += $" (na anterior, {Pct(previous.Used)})";
        return string.Join(" ", new[] { text + "." }.Concat(Facts(week)));
    }

    /// <summary>The notice's main line: "Usaste 45% do limite semanal".</summary>
    public static string Headline(WeekSummary week) => $"Usaste {Pct(week.Used)} do limite semanal";

    /// <summary>The rest of the notice: "Na anterior, 62%. Dia mais pesado: quarta (18%)…".</summary>
    public static string NoticeText(WeekSummary week, WeekSummary? previous) =>
        string.Join(" ", (previous is null ? [] : new[] { $"Na anterior, {Pct(previous.Used)}." }).Concat(Facts(week)));

    private static IEnumerable<string> Facts(WeekSummary week)
    {
        if (week.Heaviest is { } day) yield return $"Dia mais pesado: {DayName(day.Date)} ({Pct(day.Percent)}).";
        if (week.Projects.FirstOrDefault(p => p.Name != "outros") is { } top) yield return $"Mais usado: {top.Name} ({Pct(top.Share * 100)}).";
        var outside = week.Sources.Where(s => s.Key != "claude_code" && s.Points >= 0.5).ToList();
        if (outside.Count > 0) yield return $"Fora do Claude Code: {Pct(outside.Sum(s => s.Points))} ({string.Join(", ", outside.Select(s => s.Name))}).";
        if (week.FiveHourMaxed > 0) yield return week.FiveHourMaxed == 1 ? "A janela de 5h esgotou uma vez." : $"A janela de 5h esgotou {week.FiveHourMaxed} vezes.";
    }

    /// <summary>
    /// The weeks kept and the one running, newest first. Each is labelled by
    /// its own dates: an account's week ends on its own day and hour, and it
    /// can move (after a week without use, say), leaving a gap between two.
    /// </summary>
    public static List<WeekLine> History(IReadOnlyList<WeekSummary> closed, WeekSummary? current, long now)
    {
        var weeks = closed.Where(w => current is null || Math.Abs(w.ResetAt - current.ResetAt) > 3600).ToList();
        if (current is not null) weeks.Add(current);
        return weeks.OrderByDescending(w => w.ResetAt).Select(w =>
        {
            var (from, to) = (Local(w.Start), Local(w.ResetAt));
            var running = w.ResetAt > now;
            var maxed = w.FiveHourMaxed switch
            {
                0 => "a janela de 5h não esgotou",
                1 => "a janela de 5h esgotou uma vez",
                var n => $"a janela de 5h esgotou {n} vezes",
            };
            var tip = $"{DayLabel(from)}/{from:MM} {from:HH:mm} → {DayLabel(to)}/{to:MM} {to:HH:mm}" +
                      $"{(running ? " (em curso)" : "")}: {Pct(w.Used)} do limite semanal; {maxed}.";
            return new WeekLine(w, running ? "esta semana" : $"{from:dd/MM} – {to:dd/MM}", tip, running);
        }).ToList();
    }

    /// <summary>"Média das últimas 4: 42% por semana"; empty with fewer than two weeks ended.</summary>
    public static string Average(IReadOnlyList<WeekSummary> closed) =>
        closed.Count < 2 ? "" : $"Média das últimas {closed.Count}: {Pct(closed.Average(w => w.Used))} por semana";

    public static string Pct(double p) => p is > 0 and < 1 ? "<1%" : $"{Math.Round(p)}%";

    // Fixed, not from the culture: Windows' pt-PT data has no short day names
    // ("terça" for both), and the long ones carry "-feira".
    private static readonly string[] Names = ["domingo", "segunda", "terça", "quarta", "quinta", "sexta", "sábado"];
    private static readonly string[] Short = ["dom", "seg", "ter", "qua", "qui", "sex", "sáb"];

    /// <summary>"terça", for the heaviest day.</summary>
    public static string DayName(DateTime d) => Names[(int)d.DayOfWeek];

    /// <summary>"qua 18", for the list.</summary>
    public static string DayLabel(DateTime d) => $"{Short[(int)d.DayOfWeek]} {d.Day}";

    private static DateTime Local(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
    private static long Unix(DateTime local) => new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local)).ToUnixTimeSeconds();
}

/// <summary>
/// The weeks summed up so far (the last 12), in weeks.json. The readings
/// behind a week are kept for 8 days only, so a week is summed up when it
/// ends, while they are all still there.
/// </summary>
public class WeekLog
{
    private const int Keep = 12;
    private readonly string? _path;
    private readonly WeeksFile _file;

    public WeekLog(string? path = null) : this(Json.Load<WeeksFile>(path ?? Path.Combine(Paths.Dir, "weeks.json")), path ?? Path.Combine(Paths.Dir, "weeks.json")) { }

    internal WeekLog(WeeksFile file, string? path)
    {
        _file = file;
        _path = path;
    }

    /// <summary>Oldest first.</summary>
    public IReadOnlyList<WeekSummary> Weeks => _file.Weeks;

    public WeekSummary? Before(long resetAt) => _file.Weeks.Where(w => w.ResetAt < resetAt - 3600).MaxBy(w => w.ResetAt);

    /// <summary>
    /// The reset of the latest week that has ended (the readings moved on to
    /// a later one) and is not summed up yet; 0 when there is none.
    /// </summary>
    public long Unsummed(IReadOnlyList<Sample> samples, long now)
    {
        var current = 0L;
        for (var i = samples.Count - 1; i >= 0 && current == 0; i--) current = samples[i].R7;
        if (current == 0) return 0;
        var ended = 0L;
        foreach (var s in samples)
            if (s.R7 > 0 && s.R7 < current - 3600 && s.R7 <= now && s.R7 > ended) ended = s.R7;
        return ended == 0 || _file.Weeks.Any(w => Math.Abs(w.ResetAt - ended) <= 60) ? 0 : ended;
    }

    /// <summary>
    /// When the readings have moved on to a new week, sums up the one that
    /// ended, once. Returns it when it just ended (to announce); a week found
    /// ended long ago (the widget was off) is kept without a word.
    /// </summary>
    public WeekSummary? Close(IReadOnlyList<Sample> samples, Func<long, long, Breakdown> use, long now, WeekSources? sources = null)
    {
        var ended = Unsummed(samples, now);
        if (ended == 0) return null;

        var week = WeekReport.Build(samples, ended, use(ended - 7 * 86400, ended), now);
        if (week is null) return null;
        // What /usage last said of it, if it was opened that week: it now shows the new one.
        WeekReport.Attach(week, sources);
        _file.Weeks.Add(week);
        _file.Weeks.Sort((a, b) => a.ResetAt.CompareTo(b.ResetAt));
        if (_file.Weeks.Count > Keep) _file.Weeks.RemoveRange(0, _file.Weeks.Count - Keep);
        if (_path is not null) Json.Save(_path, _file);
        return now - ended < 36 * 3600 ? week : null;
    }
}
