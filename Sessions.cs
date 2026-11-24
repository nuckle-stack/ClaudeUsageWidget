using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace ClaudeUsageWidget;

public class SessionInfo
{
    public int Pid { get; init; }
    public string SessionId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Cwd { get; init; } = "";
    /// <summary>"Desktop", "CLI", "SDK", or the raw entrypoint for anything else.</summary>
    public string Kind { get; init; } = "";
    public string Status { get; init; } = "";
    public DateTimeOffset? StartedAt { get; init; }
    /// <summary>When Status last changed, unix milliseconds; 0 if unknown.</summary>
    public long StatusUpdatedAt { get; init; }

    // From the token-optimizer plugin's per-session cache; null without it.
    public double? Quality { get; init; }
    public string? Grade { get; init; }

    /// <summary>How full the context is, in percent; null before the first answer.</summary>
    public double? FillPct { get; init; }
    /// <summary>True when the window's size behind FillPct is a guess (no statusline for this session).</summary>
    public bool FillEstimated { get; init; }
    /// <summary>Tokens in context and the window's size, when known.</summary>
    public long ContextTokens { get; init; }
    public long ContextSize { get; init; }

    /// <summary>The tool call it is waiting on, if any: what it is doing now, or what it needs you for.</summary>
    public Doing? Doing { get; init; }

    /// <summary>
    /// When its conversation last moved on (unix seconds): the newest
    /// timestamped line in the transcript. 0 when there is no transcript to read.
    /// </summary>
    public long ProgressAt { get; init; }

    public bool IsSdk => Kind == "SDK";

    /// <summary>
    /// Working, as Claude Code itself shows it: "busy", or "shell" while it
    /// runs a command. Only "waiting" means it is blocked on you.
    /// </summary>
    public bool Working => Status is "busy" or "shell";

    /// <summary>What a "waiting" session waits for, as Claude Code words it; empty when it does not say.</summary>
    public string WaitingFor { get; init; } = "";
}

/// <summary>
/// Lists the Claude Code sessions running on this machine. Claude Code keeps
/// one file per live session in ~/.claude/sessions/&lt;pid&gt;.json; the format is
/// internal to it, so every field is read defensively.
/// </summary>
public static class SessionScanner
{
    /// <summary>
    /// What the last scan found in the session files, for FormatWatch: files
    /// that are there but say nothing it knows mean Claude Code changed them.
    /// </summary>
    public record Health(int Files, int WithIds, int Alive, int AliveWithStatus, string Version);

    public static Health LastHealth { get; private set; } = new(0, 0, 0, 0, "");

    public static List<SessionInfo> Scan()
    {
        var result = new List<SessionInfo>();
        var claudeDir = Path.GetDirectoryName(Paths.Dir)!;
        var dir = Path.Combine(claudeDir, "sessions");
        if (!Directory.Exists(dir))
        {
            LastHealth = new(0, 0, 0, 0, "");
            return result;
        }

        int files = 0, withIds = 0, alive = 0, withStatus = 0;
        var version = "";
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            // "*.json" also matches longer extensions on Windows.
            if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            files++;
            try
            {
                using var doc = JsonDocument.Parse(ReadShared(file));
                var root = doc.RootElement;
                if (!root.TryGetProperty("pid", out var pidEl) || !pidEl.TryGetInt32(out var pid)) continue;
                if (Str(root, "sessionId").Length > 0) withIds++;
                // A crashed session leaves its file behind, and the pid can be reused.
                if (!IsAlive(pid, Long(root, "procStart"))) continue;
                alive++;
                if (Str(root, "status").Length > 0) withStatus++;
                if (Newer(Str(root, "version"), version)) version = Str(root, "version");

                var sessionId = Str(root, "sessionId");
                var cwd = Str(root, "cwd");
                var name = Transcripts.Title(claudeDir, sessionId, cwd, Str(root, "name"), Str(root, "nameSource"));
                if (name.Length == 0) name = Path.GetFileName(cwd.TrimEnd('\\', '/'));
                var started = Long(root, "startedAt");

                var (quality, grade, pluginFill) = ReadQuality(claudeDir, sessionId);
                // Claude Code's own window beats the plugin's figure; the plugin's beats a guessed window.
                var ctx = ContextWindow.For(claudeDir, sessionId, cwd);
                var fill = ctx is { Exact: true } ? ctx.Percent : pluginFill ?? ctx?.Percent;
                result.Add(new SessionInfo
                {
                    Pid = pid,
                    SessionId = sessionId,
                    Name = name,
                    Cwd = cwd,
                    Kind = KindOf(Str(root, "entrypoint")),
                    Status = Str(root, "status"),
                    WaitingFor = Str(root, "waitingFor"),
                    StatusUpdatedAt = Long(root, "statusUpdatedAt"),
                    StartedAt = started > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(started) : null,
                    Quality = quality,
                    Grade = grade,
                    FillPct = fill,
                    FillEstimated = ctx is { Exact: false } && pluginFill is null,
                    ContextTokens = ctx?.Tokens ?? 0,
                    ContextSize = ctx?.Size ?? 0,
                    Doing = Transcripts.Activity(claudeDir, sessionId, cwd),
                    ProgressAt = Transcripts.ProgressAt(claudeDir, sessionId, cwd),
                });
            }
            catch { }
        }

        LastHealth = new(files, withIds, alive, withStatus, version);
        return result
            .OrderBy(s => s.IsSdk)
            .ThenBy(s => s.Working ? 0 : 1)
            .ThenByDescending(s => s.StartedAt)
            .ToList();
    }

    internal static string KindOf(string entrypoint)
    {
        if (entrypoint.Contains("desktop", StringComparison.OrdinalIgnoreCase)) return "Desktop";
        if (entrypoint.StartsWith("sdk", StringComparison.OrdinalIgnoreCase)) return "SDK";
        if (entrypoint.Equals("cli", StringComparison.OrdinalIgnoreCase)) return "CLI";
        if (entrypoint.Contains("vscode", StringComparison.OrdinalIgnoreCase)) return "VS Code";
        return entrypoint.Length > 0 ? entrypoint : "?";
    }

    private static bool IsAlive(int pid, long procStartFileTime)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (procStartFileTime <= 0) return true;
            try
            {
                // Same pid but started at another moment = a different process.
                return Math.Abs(p.StartTime.ToFileTimeUtc() - procStartFileTime) < TimeSpan.TicksPerSecond * 5;
            }
            catch { return true; }
        }
        catch { return false; }
    }

    private static (double? Score, string? Grade, double? Fill) ReadQuality(string claudeDir, string sessionId)
    {
        if (sessionId.Length == 0) return default;
        var file = Path.Combine(claudeDir, "token-optimizer", $"quality-cache-{sessionId}.json");
        if (!File.Exists(file)) return default;
        try
        {
            using var doc = JsonDocument.Parse(ReadShared(file));
            var root = doc.RootElement;
            var grade = Str(root, "grade");
            return (Num(root, "score"), grade.Length > 0 ? grade : null, Num(root, "fill_pct"));
        }
        catch { return default; }
    }

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>A whole number, written as a number or as a string: procStart is a FILETIME too big for JavaScript, so it comes as text.</summary>
    internal static long Long(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        return v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s) ? s : 0;
    }

    /// <summary>Whether version a (e.g. "2.1.329") is newer than b; anything beats empty.</summary>
    private static bool Newer(string a, string b) =>
        Version.TryParse(a, out var va) && (!Version.TryParse(b, out var vb) || va > vb);

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : null;
}

/// <summary>
/// What a session's transcript says that its session file does not: the
/// title it shows in its terminal tab, and how big its context was at the
/// last answer. The session file only has a name derived from the folder
/// ("claudeusagewidget-79"); the real title is in "custom-title" lines
/// (/rename) and "ai-title" lines (generated). Transcripts grow to many MB and
/// the list refreshes every few seconds, so each one is read incrementally:
/// only the bytes added since the last pass.
/// </summary>
public static class Transcripts
{
    private class Entry
    {
        public string? Path;
        public DateTime Resolved;
        public long Offset;
        public string Custom = "";
        public string Ai = "";
        public long Tokens;
        public string Model = "";
        public long AnsweredAt;
        /// <summary>The newest timestamp on any line read, unix seconds.</summary>
        public long LastLineAt;
        /// <summary>Tool calls with no result yet, by call id.</summary>
        public readonly Dictionary<string, Doing> Pending = new();
    }

    /// <summary>The tool call the session is waiting on (the latest, when several run at once); null when none.</summary>
    public static Doing? Activity(string claudeDir, string sessionId, string cwd)
    {
        if (sessionId.Length == 0) return null;
        lock (Cache)
        {
            var e = Refresh(claudeDir, sessionId, cwd);
            return e.Pending.Values.MaxBy(d => d.Since);
        }
    }

    /// <summary>
    /// When the conversation last moved on (unix seconds), 0 without a
    /// transcript. Only timestamped lines count: an idle session keeps
    /// appending bookkeeping lines (title, mode...), which carry no time.
    /// </summary>
    public static long ProgressAt(string claudeDir, string sessionId, string cwd)
    {
        if (sessionId.Length == 0) return 0;
        lock (Cache) return Refresh(claudeDir, sessionId, cwd).LastLineAt;
    }

    private static readonly Dictionary<string, Entry> Cache = new();

    public static string Title(string claudeDir, string sessionId, string cwd, string name, string nameSource)
    {
        if (sessionId.Length == 0) return name;
        // The widget scans on a worker thread, the sessions popup on the UI thread.
        lock (Cache)
        {
            var e = Refresh(claudeDir, sessionId, cwd);
            // A name the user gave wins; a name derived from the folder is the last resort.
            if (e.Custom.Length > 0) return e.Custom;
            if (name.Length > 0 && nameSource != "derived") return name;
            return e.Ai.Length > 0 ? e.Ai : name;
        }
    }

    /// <summary>The context the last answer was given with (tokens), its model and when (unix seconds); null before any answer.</summary>
    public static (long Tokens, string Model, long At)? LastAnswer(string claudeDir, string sessionId, string cwd)
    {
        if (sessionId.Length == 0) return null;
        lock (Cache)
        {
            var e = Refresh(claudeDir, sessionId, cwd);
            return e.Tokens > 0 ? (e.Tokens, e.Model, e.AnsweredAt) : null;
        }
    }

    private static Entry Refresh(string claudeDir, string sessionId, string cwd)
    {
        if (!Cache.TryGetValue(sessionId, out var e)) Cache[sessionId] = e = new Entry();
        try { Read(claudeDir, sessionId, cwd, e); } catch { }
        return e;
    }

    /// <summary>
    /// How much of an old transcript to read the first time. The title lines are
    /// repeated as the conversation goes on, so the tail has the current one.
    /// </summary>
    private const long FirstReadBytes = 4 * 1024 * 1024;

    private static void Read(string claudeDir, string sessionId, string cwd, Entry e)
    {
        if (e.Path is null || !File.Exists(e.Path))
        {
            // Retry a missing transcript once a minute, not on every scan: a new
            // session has none until its first message.
            if (DateTime.UtcNow - e.Resolved < TimeSpan.FromMinutes(1)) return;
            e.Resolved = DateTime.UtcNow;
            e.Path = Locate(claudeDir, sessionId, cwd);
            e.Offset = 0;
            if (e.Path is null) return;
        }

        using var fs = new FileStream(e.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < e.Offset) e.Offset = 0;   // rewritten
        if (fs.Length == e.Offset) return;
        // Starting mid-file cuts a line in two; the parse below just skips it.
        if (fs.Length - e.Offset > FirstReadBytes) e.Offset = fs.Length - FirstReadBytes;
        fs.Seek(e.Offset, SeekOrigin.Begin);

        var buffer = new byte[fs.Length - e.Offset];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = fs.Read(buffer, read, buffer.Length - read);
            if (n == 0) break;
            read += n;
        }
        // Leave a half-written last line for the next pass.
        var end = Array.LastIndexOf(buffer, (byte)'\n', Math.Max(0, read - 1));
        if (end < 0) return;
        e.Offset += end + 1;

        var text = System.Text.Encoding.UTF8.GetString(buffer, 0, end);
        var latest = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        foreach (var line in text.Split('\n'))
        {
            Stamp(line, e, latest);
            if (e.Pending.Count > 0 && line.Contains("\"tool_result\"")) Resolve(line, e);
            if (line.Contains("\"usage\"") && line.Contains("\"assistant\""))
            {
                ReadAnswer(line, e);
                continue;
            }
            var custom = line.Contains("\"custom-title\"");
            if (!custom && !line.Contains("\"ai-title\"")) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var key = custom ? "customTitle" : "aiTitle";
                if (!root.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String) continue;
                var title = v.GetString()?.Trim() ?? "";
                if (title.Length == 0) continue;
                if (custom) e.Custom = title; else e.Ai = title;
            }
            catch (JsonException) { }
        }
    }

    /// <summary>
    /// An answer's context, counted as Claude Code counts it for its own
    /// "% used": everything the request sent, new, cache-written and cached.
    /// </summary>
    private static void ReadAnswer(string line, Entry e)
    {
        // A subagent's turns run in a context of their own.
        if (line.Contains("\"isSidechain\":true")) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "type") != "assistant" ||
                !root.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object) return;
            var at = DateTimeOffset.TryParse(Text(root, "timestamp"), out var stamp) ? stamp.ToUnixTimeSeconds() : 0;

            // Claude Code writes one line per block. A tool call waits for its
            // result; thinking or text after it means the model has moved on.
            if (m.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind != JsonValueKind.Object) continue;
                    if (Text(block, "type") != "tool_use") { e.Pending.Clear(); continue; }
                    if (e.Pending.Count > 20) e.Pending.Clear();   // results never seen: a cut-off session
                    e.Pending[Text(block, "id")] = ToolActivity.Describe(Text(block, "name"),
                        block.TryGetProperty("input", out var input) ? input : default, at);
                }

            if (!m.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return;
            var tokens = Count(u, "input_tokens") + Count(u, "cache_creation_input_tokens") + Count(u, "cache_read_input_tokens");
            if (tokens <= 0) return;   // e.g. "<synthetic>" error lines
            e.Tokens = tokens;
            e.Model = Text(m, "model");
            e.AnsweredAt = at;
        }
        catch (JsonException) { }

        static string Text(JsonElement el, string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        static long Count(JsonElement el, string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
    }

    /// <summary>
    /// Moves LastLineAt to the newest "timestamp" on the line. A key inside a
    /// tool's text is escaped (\"timestamp\") and does not match; one inside a
    /// tool's input does, so a time in the future (a date that input was
    /// about) is not taken as progress.
    /// </summary>
    private static void Stamp(string line, Entry e, long latest)
    {
        const string key = "\"timestamp\":\"";
        for (var i = line.IndexOf(key, StringComparison.Ordinal); i >= 0; i = line.IndexOf(key, i + key.Length, StringComparison.Ordinal))
        {
            var start = i + key.Length;
            var end = line.IndexOf('"', start);
            if (end <= start || end - start > 40) continue;
            if (DateTimeOffset.TryParse(line.AsSpan(start, end - start), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var at) &&
                at.ToUnixTimeSeconds() is var s && s <= latest && s > e.LastLineAt)
                e.LastLineAt = s;
        }
    }

    /// <summary>
    /// A user line carrying tool results: those calls are done. The ids are
    /// read off the raw text, since a result can be megabytes of file content.
    /// </summary>
    private static void Resolve(string line, Entry e)
    {
        const string key = "\"tool_use_id\":\"";
        for (var i = line.IndexOf(key, StringComparison.Ordinal); i >= 0; i = line.IndexOf(key, i + key.Length, StringComparison.Ordinal))
        {
            var start = i + key.Length;
            var end = line.IndexOf('"', start);
            if (end > start) e.Pending.Remove(line[start..end]);
        }
    }

    /// <summary>
    /// projects\&lt;cwd with every non-alphanumeric as '-'&gt;\&lt;id&gt;.jsonl, or failing
    /// that (the encoding is Claude Code's, not ours) any project folder holding it.
    /// </summary>
    private static string? Locate(string claudeDir, string sessionId, string cwd)
    {
        var projects = Path.Combine(claudeDir, "projects");
        var file = sessionId + ".jsonl";
        var folder = new string(cwd.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        var direct = Path.Combine(projects, folder, file);
        if (File.Exists(direct)) return direct;
        if (!Directory.Exists(projects)) return null;
        return Directory.EnumerateDirectories(projects)
            .Select(d => Path.Combine(d, file))
            .FirstOrDefault(File.Exists);
    }
}
