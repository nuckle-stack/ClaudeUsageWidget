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
    public double? FillPct { get; init; }

    public bool IsSdk => Kind == "SDK";
}

/// <summary>
/// Lists the Claude Code sessions running on this machine. Claude Code keeps
/// one file per live session in ~/.claude/sessions/&lt;pid&gt;.json; the format is
/// internal to it, so every field is read defensively.
/// </summary>
public static class SessionScanner
{
    public static List<SessionInfo> Scan()
    {
        var result = new List<SessionInfo>();
        var claudeDir = Path.GetDirectoryName(Paths.Dir)!;
        var dir = Path.Combine(claudeDir, "sessions");
        if (!Directory.Exists(dir)) return result;

        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            // "*.json" also matches longer extensions on Windows.
            if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var doc = JsonDocument.Parse(ReadShared(file));
                var root = doc.RootElement;
                if (!root.TryGetProperty("pid", out var pidEl) || !pidEl.TryGetInt32(out var pid)) continue;
                // A crashed session leaves its file behind, and the pid can be reused.
                if (!IsAlive(pid, Long(root, "procStart"))) continue;

                var sessionId = Str(root, "sessionId");
                var cwd = Str(root, "cwd");
                var name = Titles.For(claudeDir, sessionId, cwd, Str(root, "name"), Str(root, "nameSource"));
                if (name.Length == 0) name = Path.GetFileName(cwd.TrimEnd('\\', '/'));
                var started = Long(root, "startedAt");

                var (quality, grade, fill) = ReadQuality(claudeDir, sessionId);
                result.Add(new SessionInfo
                {
                    Pid = pid,
                    SessionId = sessionId,
                    Name = name,
                    Cwd = cwd,
                    Kind = KindOf(Str(root, "entrypoint")),
                    Status = Str(root, "status"),
                    StatusUpdatedAt = Long(root, "statusUpdatedAt"),
                    StartedAt = started > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(started) : null,
                    Quality = quality,
                    Grade = grade,
                    FillPct = fill,
                });
            }
            catch { }
        }

        return result
            .OrderBy(s => s.IsSdk)
            .ThenBy(s => s.Status == "busy" ? 0 : 1)
            .ThenByDescending(s => s.StartedAt)
            .ToList();
    }

    private static string KindOf(string entrypoint)
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

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : null;
}

/// <summary>
/// The title a session shows in its terminal tab. The session file only has a
/// name derived from the folder ("claudeusagewidget-79"); the real title lives
/// in the transcript, as "custom-title" lines (/rename) and "ai-title" lines
/// (generated). Transcripts grow to many MB and the list refreshes every few
/// seconds, so each one is read incrementally: only the bytes added since the
/// last pass.
/// </summary>
public static class Titles
{
    private class Entry
    {
        public string? Path;
        public DateTime Resolved;
        public long Offset;
        public string Custom = "";
        public string Ai = "";
    }

    private static readonly Dictionary<string, Entry> Cache = new();

    public static string For(string claudeDir, string sessionId, string cwd, string name, string nameSource)
    {
        if (sessionId.Length == 0) return name;
        // The widget scans on a worker thread, the sessions popup on the UI thread.
        lock (Cache)
        {
            if (!Cache.TryGetValue(sessionId, out var e)) Cache[sessionId] = e = new Entry();
            try { Read(claudeDir, sessionId, cwd, e); } catch { }

            // A name the user gave wins; a name derived from the folder is the last resort.
            if (e.Custom.Length > 0) return e.Custom;
            if (name.Length > 0 && nameSource != "derived") return name;
            return e.Ai.Length > 0 ? e.Ai : name;
        }
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
        foreach (var line in text.Split('\n'))
        {
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
