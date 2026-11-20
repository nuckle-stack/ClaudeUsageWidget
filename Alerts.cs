using System.IO;
using System.Text;
using System.Text.Json;

namespace ClaudeUsageWidget;

public enum AlertKind { Done, Input }

public class Alert
{
    public string SessionId { get; init; } = "";
    /// <summary>The session's process, to bring its window to the front.</summary>
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    public AlertKind Kind { get; init; }
    /// <summary>When the widget raised it: how long the session has been waiting for you.</summary>
    public DateTime RaisedAt { get; init; }
    public string Message { get; init; } = "";
    /// <summary>The session's statusUpdatedAt when the alert was raised.</summary>
    public long StatusStamp { get; init; }
    public bool Acked { get; set; }
    public DateTime LastNotified { get; set; }
    /// <summary>Reminders sent after the first announcement.</summary>
    public int Reminders { get; set; }
}

/// <summary>
/// Announces, once per session, when its context has degraded (low quality
/// score) or is nearly full. Both numbers come from the token-optimizer
/// plugin's cache, so without the plugin this never fires. A condition that
/// clears (after compacting, say) is armed again.
/// </summary>
public class ContextWatch
{
    private readonly HashSet<string> _lowQuality = new();
    private readonly HashSet<string> _nearlyFull = new();

    /// <summary>New context alerts, each with its session (to go to it when the alert is clicked).</summary>
    public List<(string Title, string Text, SessionInfo Session)> Update(ContextAlertsConfig cfg, List<SessionInfo> sessions)
    {
        var result = new List<(string, string, SessionInfo)>();
        var live = sessions.Where(s => !s.IsSdk && s.SessionId.Length > 0).ToList();

        foreach (var s in live)
        {
            var low = cfg.QualityBelow > 0 && s.Quality is { } q && q < cfg.QualityBelow;
            if (!low) _lowQuality.Remove(s.SessionId);
            else if (_lowQuality.Add(s.SessionId))
                result.Add(("Contexto degradado",
                    $"{s.Name}\nQualidade {Math.Round(s.Quality!.Value)}. Compacta ou abre uma sessão nova.", s));

            var full = cfg.FillAbove > 0 && s.FillPct is { } f && f >= cfg.FillAbove;
            if (!full) _nearlyFull.Remove(s.SessionId);
            else if (_nearlyFull.Add(s.SessionId))
                result.Add(("Contexto quase cheio",
                    $"{s.Name}\n{(s.FillEstimated ? "Cerca de " : "")}{Math.Round(s.FillPct!.Value)}% ocupado. Compacta ou abre uma sessão nova.", s));
        }

        var ids = live.Select(s => s.SessionId).ToHashSet();
        _lowQuality.IntersectWith(ids);
        _nearlyFull.IntersectWith(ids);
        return result;
    }
}

/// <summary>
/// Turns session activity into alerts. "Done" comes from a session going
/// busy -> idle after a task long enough to matter. "Input" comes from the
/// Notification hook (events.jsonl, written by notify.ps1) or from a session
/// status that is neither busy nor idle.
/// </summary>
public class AlertTracker
{
    private readonly Dictionary<string, (string Status, long BusySince)> _last = new();
    private readonly Dictionary<string, Alert> _alerts = new();
    private bool _primed;
    private long _offset;

    public AlertTracker()
    {
        // Events written before the widget started are old news.
        try { if (File.Exists(Paths.Events)) _offset = new FileInfo(Paths.Events).Length; } catch { }
    }

    public Alert? For(string sessionId) => _alerts.GetValueOrDefault(sessionId);

    /// <summary>The kind to signal on the widget; Input outranks Done. Null when nothing is pending.</summary>
    public AlertKind? Pending
    {
        get
        {
            var open = _alerts.Values.Where(a => !a.Acked).ToList();
            if (open.Count == 0) return null;
            return open.Any(a => a.Kind == AlertKind.Input) ? AlertKind.Input : AlertKind.Done;
        }
    }

    public void AckAll()
    {
        foreach (var a in _alerts.Values) a.Acked = true;
    }

    public void Ack(string sessionId)
    {
        if (_alerts.TryGetValue(sessionId, out var a)) a.Acked = true;
    }

    /// <summary>
    /// The sessions waiting for you, the one to go to first: not yet seen
    /// before seen, needing input before finished, then the longest waiting.
    /// An alert stays here, seen or not, until its session moves on.
    /// </summary>
    public List<Alert> Waiting() =>
        _alerts.Values
            .OrderBy(a => a.Acked)
            .ThenBy(a => a.Kind == AlertKind.Input ? 0 : 1)
            .ThenBy(a => a.RaisedAt)
            .ToList();

    public void Clear() => _alerts.Clear();

    /// <summary>Feeds the latest scan; returns the alerts to announce now (new ones and due reminders).</summary>
    public List<Alert> Update(AlertsConfig cfg, List<SessionInfo> sessions)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var live = sessions.Where(s => !s.IsSdk && s.SessionId.Length > 0).ToDictionary(s => s.SessionId);

        foreach (var s in live.Values)
        {
            var had = _last.TryGetValue(s.SessionId, out var prev);
            var stamp = s.StatusUpdatedAt > 0 ? s.StatusUpdatedAt : nowMs;

            // An alert is stale once the session has moved on: any "done" when
            // work resumes, an "input" when the status changed after it.
            if (_alerts.TryGetValue(s.SessionId, out var current) &&
                ((current.Kind == AlertKind.Done && s.Status == "busy") ||
                 (current.Kind == AlertKind.Input && s.StatusUpdatedAt != current.StatusStamp)))
                _alerts.Remove(s.SessionId);

            if (s.Status == "busy")
            {
                _last[s.SessionId] = ("busy", had && prev.Status == "busy" ? prev.BusySince : stamp);
                continue;
            }

            if (_primed && had && prev.Status != s.Status)
            {
                if (s.Status == "idle")
                {
                    var seconds = (stamp - prev.BusySince) / 1000;
                    if (prev.Status == "busy" && seconds >= cfg.MinTaskSeconds)
                        Raise(s, AlertKind.Done, $"Tarefa de {Duration(seconds)} concluída.");
                }
                else if (s.Status.Length > 0)
                {
                    Raise(s, AlertKind.Input, s.Status);
                }
            }
            _last[s.SessionId] = (s.Status, 0);
        }

        foreach (var gone in _last.Keys.Where(id => !live.ContainsKey(id)).ToList())
        {
            _last.Remove(gone);
            _alerts.Remove(gone);
        }

        foreach (var (sessionId, message) in ReadNewEvents())
            if (live.TryGetValue(sessionId, out var s))
                Raise(s, AlertKind.Input, message);

        _primed = true;

        var due = new List<Alert>();
        foreach (var a in _alerts.Values.Where(a => !a.Acked))
        {
            // Looking at the session's window is as good as clicking the widget.
            if (live.TryGetValue(a.SessionId, out var s) && WindowFocus.IsForeground(s.Pid, s.Name))
            {
                a.Acked = true;
                continue;
            }

            var first = a.LastNotified == default;
            // A finished task does not get worse by waiting, so it is said once.
            // A session waiting for input is stuck, so it is repeated, a few times.
            var remind = a.Kind == AlertKind.Input && cfg.RemindMinutes > 0 && a.Reminders < MaxReminders &&
                         Clock() - a.LastNotified >= TimeSpan.FromMinutes(cfg.RemindMinutes);
            if (!first && !remind) continue;
            if (!first) a.Reminders++;
            a.LastNotified = Clock();
            due.Add(a);
        }
        return due;
    }

    internal const int MaxReminders = 3;

    /// <summary>The time reminders are measured against; replaced in tests.</summary>
    internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    private void Raise(SessionInfo s, AlertKind kind, string message)
    {
        // The same thing twice in a row is one alert, not two.
        if (_alerts.TryGetValue(s.SessionId, out var existing) && existing.Kind == kind && !existing.Acked) return;
        _alerts[s.SessionId] = new Alert
        {
            SessionId = s.SessionId,
            Pid = s.Pid,
            Name = s.Name,
            Kind = kind,
            Message = message,
            StatusStamp = s.StatusUpdatedAt,
            RaisedAt = Clock(),
        };
    }

    /// <summary>New Notification-hook lines since the last call, as (session id, message).</summary>
    private List<(string SessionId, string Message)> ReadNewEvents()
    {
        var result = new List<(string, string)>();
        try
        {
            if (!File.Exists(Paths.Events)) return result;
            using var fs = new FileStream(Paths.Events, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < _offset) _offset = 0;   // the hook rotated the file
            if (fs.Length == _offset) return result;
            fs.Seek(_offset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var text = reader.ReadToEnd();
            // Leave a half-written last line for the next pass.
            var end = text.LastIndexOf('\n');
            if (end < 0) return result;
            _offset += Encoding.UTF8.GetByteCount(text[..(end + 1)]);

            foreach (var line in text[..end].Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var e = doc.RootElement;
                    if (Str(e, "event") != "Notification") continue;
                    var message = Str(e, "message");
                    // "Idle, waiting for you" is the end of a task, which the
                    // busy -> idle transition already reports.
                    if (Str(e, "type") == "idle_prompt" ||
                        message.Contains("waiting for your input", StringComparison.OrdinalIgnoreCase)) continue;
                    var id = Str(e, "session_id");
                    if (id.Length > 0) result.Add((id, message.Length > 0 ? message : "À espera de resposta."));
                }
                catch (JsonException) { }
            }
        }
        catch { }
        return result;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Duration(long seconds) =>
        seconds >= 3600 ? $"{seconds / 3600}h{seconds % 3600 / 60:D2}"
        : seconds >= 60 ? $"{seconds / 60} min"
        : $"{seconds} s";
}

/// <summary>
/// Announces, once per used-up window, that Claude has gone on with paid
/// extra usage. Only a hello's output carries that state.
/// </summary>
public class OverageWatch
{
    private long _announced;

    /// <summary>The alert to show now, or null.</summary>
    public (string Title, string Text)? Check(Limits ping, long now)
    {
        if (ping.Overage is not { } o || !o.ActiveAt(now) || _announced == o.ResetsAt) return null;
        _announced = o.ResetsAt;

        var weekly = o.LimitType.StartsWith("seven_day", StringComparison.Ordinal);
        var limit = o.LimitType switch
        {
            "five_hour" => "O limite de 5 horas",
            _ when weekly => "O limite semanal",
            _ => "Um limite de utilização",
        };
        var until = UsageHistory.When(DateTimeOffset.FromUnixTimeSeconds(o.ResetsAt), fiveHour: !weekly);
        return ("Uso extra pago ativo",
            $"{limit} esgotou e o Claude continua com uso extra, cobrado à parte. Volta ao normal {until}.");
    }
}
