using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeUsageWidget;

/// <summary>
/// How full a session's context is, as Claude Code counts it: the tokens the
/// last answer was given with, over the model's window. Exact when Claude Code
/// itself reported the window for this session; otherwise the window is a
/// best guess and the figure an estimate.
/// </summary>
public record ContextFill(double Percent, long Tokens, long Size, bool Exact);

/// <summary>
/// Works out each session's context without any plugin. The tokens come from
/// the transcript, for every kind of session. The window's size does not:
/// the same model runs with 200k or 1M depending on the user's settings, and
/// only Claude Code knows which. Its statusline input says so, and feed.ps1
/// keeps it per session in usage-widget\context\&lt;session id&gt;.json; that only
/// exists for terminal sessions, so the desktop app and VS Code use the size
/// last reported for the same model, or the model's default.
/// </summary>
public static class ContextWindow
{
    public record Report(long Size, long Tokens, string Model, long At);

    private static readonly string Dir = Path.Combine(Paths.Dir, "context");
    private static readonly Dictionary<string, (DateTime Stamp, Report? Report)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _cleaned;

    /// <summary>A week after its last report a session is long gone; its file goes too.</summary>
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(8);

    /// <summary>What the statusline last wrote for this session, if anything.</summary>
    public static Report? Reported(string sessionId) => Reports().GetValueOrDefault(sessionId);

    public static ContextFill? For(string claudeDir, string sessionId, string cwd)
    {
        if (sessionId.Length == 0) return null;
        var reports = Reports();
        return Estimate(Transcripts.LastAnswer(claudeDir, sessionId, cwd), reports.GetValueOrDefault(sessionId), reports.Values);
    }

    /// <summary>
    /// The tokens of the newest of the two readings, over the window Claude
    /// Code reported for this session when it is for the same model; failing
    /// that, the window last reported for that model by any session, or the
    /// model's default. A guess the session has outgrown means a 1M window.
    /// </summary>
    internal static ContextFill? Estimate((long Tokens, string Model, long At)? answer, Report? reported, IEnumerable<Report> all)
    {
        long tokens;
        string model;
        if (answer is { } a && (reported is null || a.At >= reported.At)) (tokens, model) = (a.Tokens, a.Model);
        else if (reported is { Tokens: > 0 }) (tokens, model) = (reported.Tokens, reported.Model);
        else return null;
        if (tokens <= 0) return null;

        long size;
        var exact = reported is { Size: > 0 } && (reported.Model.Length == 0 || model.Length == 0 || reported.Model == model);
        if (exact) size = reported!.Size;
        else
        {
            // After a /model switch the session's own report is for the old model.
            size = all.Where(r => r.Size > 0 && r.Model == model).MaxBy(r => r.At)?.Size ?? DefaultWindow(model);
            if (tokens > size) size = Math.Max(size, 1_000_000);
        }
        return new ContextFill(Math.Clamp(Math.Round(tokens * 100.0 / size), 0, 100), tokens, size, exact);
    }

    /// <summary>
    /// The window Claude Code gives a model unless told otherwise: 1M from the
    /// Opus 4.7 generation on, 200k before (a "[1m]" model asks for 1M).
    /// </summary>
    internal static long DefaultWindow(string model)
    {
        if (model.Contains("[1m]", StringComparison.OrdinalIgnoreCase)) return 1_000_000;
        // claude-opus-5-5, claude-opus-5, claude-sonnet-4-6, claude-haiku-4-5-20251001...
        var m = Regex.Match(model, @"claude-[a-z]+-(\d+)(?:-(\d{1,2}))?(?!\d)");
        if (!m.Success) return 200_000;   // the oldest naming (claude-3-5-sonnet-...), or unknown
        var major = int.Parse(m.Groups[1].Value);
        var minor = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
        return major > 4 || (major == 4 && minor >= 7) ? 1_000_000 : 200_000;
    }

    /// <summary>Every session's last report, re-read only when its file changes.</summary>
    private static Dictionary<string, Report> Reports()
    {
        var result = new Dictionary<string, Report>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(Dir)) return result;
        lock (Cache)
        {
            var clean = DateTime.UtcNow - _cleaned > TimeSpan.FromHours(1);
            if (clean) _cleaned = DateTime.UtcNow;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in SafeFiles())
            {
                try
                {
                    var stamp = File.GetLastWriteTimeUtc(file);
                    if (clean && DateTime.UtcNow - stamp > KeepFor)
                    {
                        File.Delete(file);
                        continue;
                    }
                    seen.Add(file);
                    if (!Cache.TryGetValue(file, out var cached) || cached.Stamp != stamp)
                        Cache[file] = cached = (stamp, Parse(File.ReadAllText(file)));
                    if (cached.Report is { } r) result[Path.GetFileNameWithoutExtension(file)] = r;
                }
                catch { }
            }
            foreach (var gone in Cache.Keys.Where(k => !seen.Contains(k)).ToList()) Cache.Remove(gone);
        }
        return result;
    }

    private static IEnumerable<string> SafeFiles()
    {
        try { return Directory.EnumerateFiles(Dir, "*.json").Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToList(); }
        catch { return []; }
    }

    internal static Report? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object) return null;
            var size = Long(e, "size");
            return size > 0 ? new Report(size, Long(e, "tokens"), Str(e, "model"), Long(e, "updated_at")) : null;
        }
        catch (JsonException) { return null; }

        static long Long(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
        static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }
}
