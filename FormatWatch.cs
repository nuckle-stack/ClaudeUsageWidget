using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeUsageWidget;

/// <summary>Something the widget reads from Claude Code that it no longer understands.</summary>
public record FormatProblem(string Key, string What, string Impact);

/// <summary>Where the statusline's data comes from: the widget's feed.ps1, a script of the user's own that also writes for it, or neither.</summary>
public enum FeedKind { None, Widget, Own }

/// <summary>Everything FormatWatch looks at, gathered by the widget once a minute.</summary>
public record FormatObservations(
    SessionScanner.Health Sessions,
    // Answers and transcript bytes read since the last answer whose usage was understood.
    (int Answers, long Bytes) WithoutUsage,
    // When the model last answered any session (unix seconds).
    long LastUsedAt,
    FeedKind Feed,
    // Per terminal session: when it last answered, and when the statusline last wrote its context file.
    IReadOnlyList<(long AnsweredAt, long? ContextAt)> CliAnswers,
    // When the statusline last wrote limits.json; 0 = never, e.g. an API-key account.
    long LimitsAt,
    // The last hello answered without the limits earlier ones carried.
    bool HelloLostLimits);

/// <summary>
/// Notices when Claude Code changes the files the widget reads. Those
/// formats are Claude Code's own, undocumented, and Claude Code updates
/// itself every few days; a change would leave columns empty or alerts
/// missing with nothing to say why. Each check compares two things that
/// should go together (a live session and the fields it should have; an
/// answer and the statusline run that follows it), so a quiet machine never
/// looks broken. A problem has to last five minutes to count, and is
/// announced once per Claude Code version.
/// </summary>
public class FormatWatch
{
    public static readonly TimeSpan Persist = TimeSpan.FromMinutes(5);

    public class FormatState
    {
        /// <summary>Problem key -> the Claude Code version it was announced for.</summary>
        public Dictionary<string, string> Announced { get; set; } = new();
    }

    private readonly string _statePath;
    private readonly FormatState _state;
    private readonly Dictionary<string, long> _since = new();

    public FormatWatch(string? statePath = null)
    {
        _statePath = statePath ?? Path.Combine(Paths.Dir, "format.json");
        _state = Json.Load<FormatState>(_statePath);
    }

    /// <summary>Problems seen for long enough, with when each was first seen.</summary>
    public List<(FormatProblem Problem, long Since)> Confirmed { get; private set; } = [];

    /// <summary>Problems seen but not yet for long enough.</summary>
    public List<FormatProblem> Suspected { get; private set; } = [];

    /// <summary>Looks again; returns the problems to announce now.</summary>
    public List<FormatProblem> Update(FormatObservations o, long now, string claudeVersion)
    {
        var current = Check(o, now);
        foreach (var gone in _since.Keys.Except(current.Select(p => p.Key)).ToList()) _since.Remove(gone);
        foreach (var p in current) _since.TryAdd(p.Key, now);

        Confirmed = current.Where(p => now - _since[p.Key] >= Persist.TotalSeconds).Select(p => (p, _since[p.Key])).ToList();
        Suspected = current.Where(p => now - _since[p.Key] < Persist.TotalSeconds).ToList();

        var announce = Confirmed.Select(c => c.Problem)
            .Where(p => !_state.Announced.TryGetValue(p.Key, out var v) || v != claudeVersion)
            .ToList();
        if (announce.Count > 0)
        {
            foreach (var p in announce) _state.Announced[p.Key] = claudeVersion;
            Json.Save(_statePath, _state);
        }
        return announce;
    }

    /// <summary>
    /// Gathers what Check looks at. Scans the sessions and reads their
    /// transcripts, so it belongs on a worker thread; the usage ledger should
    /// have been refreshed just before.
    /// </summary>
    public static FormatObservations Observe(string claudeDir, long limitsAt, bool helloLostLimits)
    {
        var sessions = SessionScanner.Scan();
        var answers = sessions.Where(s => s.Kind == "CLI")
            .Select(s => (AnsweredAt: Transcripts.LastAnswer(claudeDir, s.SessionId, s.Cwd)?.At ?? 0, ContextAt: ContextWindow.Reported(s.SessionId)?.At))
            .Where(a => a.AnsweredAt > 0)
            .ToList();
        return new FormatObservations(SessionScanner.LastHealth, UsageLedger.Shared.WithoutUsage, UsageLedger.Shared.LastUsedAt,
            FeedOf(claudeDir), answers, limitsAt, helloLostLimits);
    }

    /// <summary>One look, without waiting for a problem to last: for --diagnostics.</summary>
    public static string Once(FormatObservations o, long now)
    {
        var problems = Check(o, now);
        return problems.Count == 0
            ? "tudo reconhecido (verificação única)"
            : "não reconhece " + string.Join("; ", problems.Select(p => p.What)) + " (verificação única)";
    }

    /// <summary>What does not add up right now.</summary>
    public static List<FormatProblem> Check(FormatObservations o, long now)
    {
        var problems = new List<FormatProblem>();
        const string sessionsImpact = "A lista de sessões e os avisos podem falhar.";

        // Session files that are there but carry none of the fields the widget reads.
        var s = o.Sessions;
        if ((s.Files > 0 && s.WithIds == 0) || (s.Alive > 0 && s.AliveWithStatus == 0))
            problems.Add(new("sessions", "as sessões abertas", sessionsImpact));
        // Claude Code is answering, yet no session file anywhere: they moved.
        else if (s.Files == 0 && o.LastUsedAt >= now - 120)
            problems.Add(new("sessions-missing", "onde ficam as sessões abertas", sessionsImpact));

        if (o.WithoutUsage.Answers >= 20 || o.WithoutUsage.Bytes >= 8_000_000)
            problems.Add(new("transcripts", "o uso de tokens nas conversas",
                "O consumo, o contexto ocupado e o que cada sessão está a fazer podem falhar."));

        // A terminal session that answered between 2 and 30 minutes ago has had its statusline run since.
        if (o.Feed != FeedKind.None)
        {
            var answered = o.CliAnswers.Where(a => a.AnsweredAt <= now - 120 && a.AnsweredAt >= now - 1800).ToList();
            if (answered.Count > 0)
            {
                var latest = answered.Max(a => a.AnsweredAt);
                // Only the widget's own feed writes the context files.
                var contextStale = o.Feed == FeedKind.Widget && answered.All(a => a.ContextAt is not { } at || at < a.AnsweredAt - 60);
                // Never written means an account without these limits (API key), not a change.
                var limitsStale = o.LimitsAt > 0 && o.LimitsAt < latest - 60;
                if (contextStale && limitsStale)
                    problems.Add(new("statusline", "os dados do statusline", "Os limites e o contexto deixam de vir do terminal."));
                else if (contextStale)
                    problems.Add(new("statusline-context", "o contexto no statusline", "O \"% ctx\" das sessões do terminal passa a ser estimado."));
                else if (limitsStale)
                    problems.Add(new("statusline-limits", "os limites no statusline", "Os limites só se atualizam pelos hellos."));
            }
        }

        if (o.HelloLostLimits)
            problems.Add(new("hello", "os limites na resposta do Claude Code", "Sem o statusline, os limites de 5h e 7d deixam de se atualizar."));
        return problems;
    }

    /// <summary>The notification: what changed, since which Claude Code, and what to do.</summary>
    public static string Message(IReadOnlyList<FormatProblem> problems, string claudeVersion)
    {
        var whats = problems.Select(p => p.What).ToList();
        var what = whats.Count == 1 ? whats[0] : string.Join(", ", whats.SkipLast(1)) + " e " + whats[^1];
        var since = claudeVersion.Length > 0 ? $"Com o Claude Code {claudeVersion}, o" : "O";
        var impact = problems.Count == 1 ? problems[0].Impact + " " : "";
        return $"{since} widget já não reconhece {what}. {impact}Clica para procurar uma versão nova do widget.";
    }

    /// <summary>For the diagnostics.</summary>
    public string Status()
    {
        if (Confirmed.Count == 0 && Suspected.Count == 0) return "tudo reconhecido";
        var parts = Confirmed.Select(c => $"não reconhece {c.Problem.What} (desde {DateTimeOffset.FromUnixTimeSeconds(c.Since).LocalDateTime:HH:mm})")
            .Concat(Suspected.Select(p => $"a confirmar: {p.What}"));
        return string.Join("; ", parts);
    }

    /// <summary>Which statusline is configured in settings.json, as install.ps1 decides it.</summary>
    public static FeedKind FeedOf(string claudeDir)
    {
        try
        {
            var path = Path.Combine(claudeDir, "settings.json");
            if (!File.Exists(path)) return FeedKind.None;
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!doc.RootElement.TryGetProperty("statusLine", out var sl) || sl.ValueKind != JsonValueKind.Object ||
                !sl.TryGetProperty("command", out var c) || c.ValueKind != JsonValueKind.String) return FeedKind.None;
            return FeedOfCommand(c.GetString() ?? "");
        }
        catch { return FeedKind.None; }
    }

    internal static FeedKind FeedOfCommand(string command)
    {
        if (command.Contains("usage-widget", StringComparison.OrdinalIgnoreCase) &&
            command.Contains("feed.ps1", StringComparison.OrdinalIgnoreCase)) return FeedKind.Widget;
        var script = Regex.Match(command, "\"([^\"]+\\.ps1)\"|(\\S+\\.ps1)");
        if (!script.Success) return FeedKind.None;
        var file = script.Groups[1].Success ? script.Groups[1].Value : script.Groups[2].Value;
        try
        {
            return File.Exists(file) && File.ReadAllText(file).Contains("usage-widget", StringComparison.OrdinalIgnoreCase)
                ? FeedKind.Own : FeedKind.None;
        }
        catch { return FeedKind.None; }
    }
}
