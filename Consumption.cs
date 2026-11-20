using System.IO;
using System.Text;
using System.Text.Json;

namespace ClaudeUsageWidget;

/// <summary>
/// API list prices, used as weights: Anthropic does not publish how a
/// subscription turns tokens into a share of the limit, and what a request
/// would cost on the API is the closest public measure of how heavy it is.
/// </summary>
public static class Pricing
{
    /// <summary>Dollars per million tokens. Cache writes are priced from Input (see Cost).</summary>
    public record Price(double Input, double Output, double CacheRead);

    // From Anthropic's API price list as of 2026-11. Longest prefix first:
    // "claude-opus-5" must not catch "claude-opus-5-5".
    private static readonly (string Prefix, Price Price)[] Table =
    [
        ("claude-fable-5-2", new(8, 40, 0.20)),
        ("claude-mythos-5-2", new(8, 40, 0.20)),
        ("claude-fable-5-1", new(10, 50, 0.25)),
        ("claude-mythos-5-1", new(10, 50, 0.25)),
        ("claude-fable-5", new(10, 50, 1.00)),
        ("claude-mythos-5", new(10, 50, 1.00)),
        ("claude-opus-5-5", new(4, 20, 0.20)),
        ("claude-opus-5", new(5, 25, 0.50)),
        ("claude-opus-4", new(5, 25, 0.50)),
        ("claude-sonnet-5-5", new(2, 10, 0.10)),
        ("claude-sonnet-5", new(2, 10, 0.20)),
        ("claude-sonnet-4", new(3, 15, 0.30)),
        ("claude-haiku-5-5", new(0.10, 0.50, 0.01)),
        ("claude-haiku-4", new(1, 5, 0.10)),
    ];

    /// <summary>The price of a model id; one not listed takes the newest listed model of its family.</summary>
    public static Price For(string model)
    {
        var id = model.ToLowerInvariant();
        foreach (var (prefix, price) in Table)
            if (id.StartsWith(prefix, StringComparison.Ordinal)) return price;
        if (id.Contains("fable") || id.Contains("mythos")) return Listed("claude-fable-5-2");
        if (id.Contains("opus")) return Listed("claude-opus-5-5");
        if (id.Contains("haiku")) return Listed("claude-haiku-5-5");
        return Listed("claude-sonnet-5-5");   // sonnet, or a name we don't know: the middle of the range
    }

    private static Price Listed(string prefix) => Table.First(t => t.Prefix == prefix).Price;

    /// <summary>
    /// What one request would cost on the API, in dollars. Cache writes cost
    /// 1.25x the input price for the 5-minute duration and 2x for the 1-hour one.
    /// </summary>
    public static double Cost(string model, long input, long output, long cacheRead, long write5m, long write1h)
    {
        var p = For(model);
        return (input * p.Input + write5m * p.Input * 1.25 + write1h * p.Input * 2 +
                cacheRead * p.CacheRead + output * p.Output) / 1_000_000;
    }
}

/// <summary>How much one session used in a period, as API-equivalent dollars.</summary>
public record SessionUse(string SessionId, string Title, double Cost);

/// <summary>A project (the folder its sessions started in) and its sessions, heaviest first.</summary>
public record ProjectUse(string Cwd, string Name, double Cost, List<SessionUse> Sessions);

public record Breakdown(double Total, List<ProjectUse> Projects)
{
    public static readonly Breakdown Empty = new(0, []);

    /// <summary>A session's share of the total, 0..1.</summary>
    public double ShareOf(string sessionId)
    {
        if (Total <= 0) return 0;
        foreach (var p in Projects)
            foreach (var s in p.Sessions)
                if (s.SessionId == sessionId) return s.Cost / Total;
        return 0;
    }
}

/// <summary>
/// Who used what: every request in the Claude Code transcripts of this
/// machine, weighted by its API price, so a period's usage can be split by
/// session and project. Transcripts only grow, so each is read incrementally.
/// What it cannot see: claude.ai chats and other computers on the account.
/// </summary>
public class UsageLedger
{
    /// <summary>The widget's one ledger; its first read covers a week of transcripts.</summary>
    public static readonly UsageLedger Shared = new(Path.GetDirectoryName(Paths.Dir)!);

    private const long KeepSeconds = 8 * 86400;

    private record Request(string SessionId, string Cwd, long At, double Cost);

    private readonly string _claudeDir;
    private readonly object _gate = new();
    /// <summary>By message id: Claude Code writes one line per content block, each with the full usage.</summary>
    private readonly Dictionary<string, Request> _requests = new();
    private readonly Dictionary<string, long> _offsets = new(StringComparer.OrdinalIgnoreCase);
    private Task? _refreshing;
    private long _lastUsedAt;

    public UsageLedger(string claudeDir) => _claudeDir = claudeDir;

    public DateTime RefreshedAt { get; private set; }

    /// <summary>
    /// When the model last answered a Claude Code session on this machine
    /// (unix seconds; 0 = not in the last week). A transcript's date says less:
    /// an idle session appends bookkeeping lines to it (its title, its mode)
    /// with nobody using it.
    /// </summary>
    public long LastUsedAt { get { lock (_gate) return _lastUsedAt; } }

    /// <summary>Reads new transcript lines in the background, unless that was done in the last maxAge.</summary>
    public Task RefreshAsync(TimeSpan maxAge)
    {
        lock (_gate)
        {
            if (_refreshing is { IsCompleted: false }) return _refreshing;
            if (DateTime.UtcNow - RefreshedAt < maxAge) return Task.CompletedTask;
            return _refreshing = Task.Run(() => Refresh(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        }
    }

    /// <summary>Reads what the transcripts gained since the last call.</summary>
    public void Refresh(long now)
    {
        // Even a failed pass counts as a refresh: the next one waits its turn.
        try { Read(now); }
        finally { lock (_gate) RefreshedAt = DateTime.UtcNow; }
    }

    private void Read(long now)
    {
        var projects = Path.Combine(_claudeDir, "projects");
        var found = new List<Request>();
        var ids = new List<string>();
        if (Directory.Exists(projects))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(projects, "*.jsonl", SearchOption.AllDirectories).ToList(); }
            catch { files = []; }
            var since = DateTimeOffset.FromUnixTimeSeconds(now - KeepSeconds).UtcDateTime;
            foreach (var file in files)
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < since) continue;
                    long offset;
                    lock (_gate) offset = _offsets.GetValueOrDefault(file);
                    var end = ReadFrom(file, offset, (id, r) => { ids.Add(id); found.Add(r); });
                    lock (_gate) _offsets[file] = end;
                }
                catch { }
            }
        }

        lock (_gate)
        {
            // The same message can be in two files (a resumed or forked session): count it once.
            for (var i = 0; i < ids.Count; i++) _requests.TryAdd(ids[i], found[i]);
            foreach (var r in found) _lastUsedAt = Math.Max(_lastUsedAt, r.At);
            foreach (var old in _requests.Where(r => r.Value.At < now - KeepSeconds).Select(r => r.Key).ToList())
                _requests.Remove(old);
        }
    }

    /// <summary>
    /// Usage between from and to (unix seconds), by project and session,
    /// heaviest first. Session titles come from the transcripts, as in the
    /// sessions list.
    /// </summary>
    public Breakdown Summarize(long from, long to)
    {
        List<Request> all;
        lock (_gate) all = _requests.Values.ToList();

        // A session belongs to the folder it started in, even if Claude moved elsewhere.
        var home = all.GroupBy(r => r.SessionId)
            .ToDictionary(g => g.Key, g => g.MinBy(r => r.At)!.Cwd);

        var sessions = all.Where(r => r.At >= from && r.At < to)
            .GroupBy(r => r.SessionId)
            .Select(g => (Id: g.Key, Cwd: home[g.Key], Cost: g.Sum(r => r.Cost)))
            .Where(s => s.Cost > 0)
            .ToList();

        var projects = sessions.GroupBy(s => ProjectKey(s.Cwd), StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProjectUse(
                g.Key,
                ProjectName(g.Key),
                g.Sum(s => s.Cost),
                g.OrderByDescending(s => s.Cost)
                 .Select(s => new SessionUse(s.Id, TitleOf(s.Id, s.Cwd), s.Cost))
                 .ToList()))
            .OrderByDescending(p => p.Cost)
            .ToList();
        return new Breakdown(projects.Sum(p => p.Cost), projects);
    }

    private string TitleOf(string sessionId, string cwd)
    {
        var title = Titles.For(_claudeDir, sessionId, cwd, "", "derived");
        return title.Length > 0 ? title : "sessão " + sessionId[..Math.Min(8, sessionId.Length)];
    }

    private const string DesktopScratch = "::desktop-scratch";

    /// <summary>
    /// The desktop app runs a conversation with no project in a throwaway
    /// folder of its own (…\Claude-Data\…\scratch…); those are one group, not
    /// one project per conversation.
    /// </summary>
    private static string ProjectKey(string cwd) =>
        cwd.Contains("Claude-Data", StringComparison.OrdinalIgnoreCase) &&
        cwd.Contains("scratch", StringComparison.OrdinalIgnoreCase)
            ? DesktopScratch
            : cwd;

    private static string ProjectName(string key)
    {
        if (key == DesktopScratch) return "App Desktop (sem projeto)";
        var name = Path.GetFileName(key.TrimEnd('\\', '/'));
        return name.Length > 0 ? name : key.Length > 0 ? key : "?";
    }

    /// <summary>
    /// Reads whole lines from offset on, in chunks (a week of transcripts can
    /// be large), and hands over each request found. Returns the offset after
    /// the last complete line: a half-written one is read next time.
    /// </summary>
    private static long ReadFrom(string path, long offset, Action<string, Request> onRequest)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < offset) offset = 0;   // rewritten
        fs.Seek(offset, SeekOrigin.Begin);

        var buffer = new byte[1 << 20];
        var pending = new MemoryStream();
        int read;
        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                ReadOnlySpan<byte> line;
                if (pending.Length > 0)
                {
                    pending.Write(buffer, start, i - start);
                    line = pending.GetBuffer().AsSpan(0, (int)pending.Length);
                }
                else line = buffer.AsSpan(start, i - start);

                offset += line.Length + 1;
                if (Parse(line) is { } r) onRequest(r.Id, r.Request);
                pending.SetLength(0);
                start = i + 1;
            }
            pending.Write(buffer, start, read - start);
        }
        return offset;
    }

    private static readonly byte[] UsageKey = "\"usage\""u8.ToArray();
    private static readonly byte[] AssistantKey = "\"assistant\""u8.ToArray();

    /// <summary>One assistant line: its message id and the request's weight. Null for anything else.</summary>
    private static (string Id, Request Request)? Parse(ReadOnlySpan<byte> line)
    {
        // Most lines are user turns and tool output: skip them before parsing.
        if (line.IndexOf(UsageKey) < 0 || line.IndexOf(AssistantKey) < 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(line.ToArray());
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object || Str(e, "type") != "assistant" ||
                !e.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object ||
                !m.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return null;

            var id = Str(m, "id");
            var sessionId = Str(e, "sessionId");
            if (id.Length == 0 || sessionId.Length == 0) return null;
            if (!DateTimeOffset.TryParse(Str(e, "timestamp"), out var at)) return null;

            var write = Num(u, "cache_creation_input_tokens");
            long write5m = write, write1h = 0;
            if (u.TryGetProperty("cache_creation", out var cc) && cc.ValueKind == JsonValueKind.Object)
            {
                write1h = Num(cc, "ephemeral_1h_input_tokens");
                write5m = Math.Max(0, write - write1h);
            }
            var cost = Pricing.Cost(Str(m, "model"), Num(u, "input_tokens"), Num(u, "output_tokens"),
                Num(u, "cache_read_input_tokens"), write5m, write1h);
            if (cost <= 0) return null;   // e.g. "<synthetic>" error lines
            return (id, new Request(sessionId, Str(e, "cwd"), at.ToUnixTimeSeconds(), cost));
        }
        catch (JsonException) { return null; }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static long Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}
