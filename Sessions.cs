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
                var name = Str(root, "name");
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
