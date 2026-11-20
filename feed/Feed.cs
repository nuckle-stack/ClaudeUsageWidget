using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ClaudeUsageFeed;

/// <summary>
/// What Claude Code runs for the widget, with its JSON on stdin:
///   statusline: records the rate limits (limits.json) and the session's
///               context (context\&lt;session&gt;.json), then prints the statusline,
///               or hands over to the one that was configured before.
///   notify:     appends a Notification hook event to events.jsonl.
/// Everything goes to %CLAUDE_CONFIG_DIR%|~/.claude\usage-widget, and nothing
/// here ever fails the session: a problem only means a reading is missed.
/// </summary>
public static class Feed
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private const string Esc = "\u001b";

    public static string ClaudeDir =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    // ---- statusline ------------------------------------------------------------

    /// <summary>Records what the statusline JSON says and returns the line to print.</summary>
    public static string Statusline(string input, string claudeDir)
    {
        var widgetDir = Path.Combine(claudeDir, "usage-widget");
        using var doc = TryParse(input);
        var root = doc?.RootElement is { ValueKind: JsonValueKind.Object } r ? r : default;
        if (root.ValueKind == JsonValueKind.Object)
        {
            try { RecordLimits(root, widgetDir); } catch { }
            try { RecordContext(root, widgetDir); } catch { }
        }
        return HandOver(input, widgetDir) ?? OwnLine(root);
    }

    private readonly record struct Window(double Used, long ResetsAt);

    /// <summary>
    /// Several sessions write the same file, and an idle one can carry stale
    /// numbers, so per window: a later reset wins, and within the same window
    /// (resets up to a minute apart) the higher usage wins.
    /// </summary>
    internal static void RecordLimits(JsonElement root, string widgetDir)
    {
        if (!root.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object) return;
        Directory.CreateDirectory(widgetDir);
        var file = Path.Combine(widgetDir, "limits.json");
        using var oldDoc = TryParse(TryRead(file));
        var old = oldDoc?.RootElement is { ValueKind: JsonValueKind.Object } o ? o : default;

        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("updated_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            foreach (var name in new[] { "five_hour", "seven_day" })
            {
                var fresh = Read(limits, name);
                var kept = old.ValueKind == JsonValueKind.Object ? Read(old, name) : null;
                var pick = kept;
                if (fresh is { ResetsAt: > 0 } n)
                {
                    if (kept is not { ResetsAt: > 0 } k) pick = n;
                    else if (n.ResetsAt - k.ResetsAt > 60) pick = n;
                    else if (n.ResetsAt - k.ResetsAt >= -60 && n.Used >= k.Used) pick = n;
                }
                if (pick is { } p)
                {
                    w.WriteStartObject(name);
                    w.WriteNumber("used_percentage", p.Used);
                    w.WriteNumber("resets_at", p.ResetsAt);
                    w.WriteEndObject();
                }
            }
            w.WriteEndObject();
        }
        WriteAtomic(file, buffer.ToArray());

        static Window? Read(JsonElement parent, string name) =>
            parent.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Object
                ? new Window(Number(e, "used_percentage") ?? 0, (long)(Number(e, "resets_at") ?? 0))
                : null;
    }

    /// <summary>
    /// The session's context window, for the widget's "% ctx" column. Only
    /// Claude Code knows the window's size (the same model runs with 200k or
    /// 1M), and the transcript does not record it. One file per session.
    /// </summary>
    internal static void RecordContext(JsonElement root, string widgetDir)
    {
        if (!root.TryGetProperty("context_window", out var ctx) || ctx.ValueKind != JsonValueKind.Object ||
            Number(ctx, "context_window_size") is not { } size) return;
        var session = Text(root, "session_id");
        // It becomes a file name: nothing but a plain id.
        if (session.Length is 0 or > 100 || !session.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return;

        var dir = Path.Combine(widgetDir, "context");
        Directory.CreateDirectory(dir);
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("updated_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            w.WriteString("model", root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.Object ? Text(m, "id") : "");
            w.WriteNumber("size", (long)size);
            w.WriteNumber("tokens", (long)(Number(ctx, "total_input_tokens") ?? 0));
            // Null before the session's first answer.
            if (Number(ctx, "used_percentage") is { } used) w.WriteNumber("used_percentage", used);
            else w.WriteNull("used_percentage");
            w.WriteEndObject();
        }
        WriteAtomic(Path.Combine(dir, session + ".json"), buffer.ToArray());
    }

    /// <summary>
    /// Runs the statusline that was configured before the widget's, with the
    /// same JSON, and returns what it printed; null when there is none or it
    /// failed. The bytes go over raw: a writer would put a BOM in front.
    /// </summary>
    internal static string? HandOver(string input, string widgetDir)
    {
        try
        {
            var file = Path.Combine(widgetDir, "wrapped-command.txt");
            if (!File.Exists(file)) return null;
            var command = File.ReadAllText(file).Trim();
            if (command.Length == 0) return null;

            var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", $"/d /s /c \"{command}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Utf8,
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync();
            var bytes = Utf8.GetBytes(input);
            p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
            p.StandardInput.BaseStream.Close();
            var text = output.GetAwaiter().GetResult();
            p.WaitForExit();
            return text;
        }
        catch { return null; }
    }

    /// <summary>"[Opus 5.5] | 5h: 42%", the 5-hour figure green, yellow from 70%, red from 90%.</summary>
    internal static string OwnLine(JsonElement root)
    {
        var parts = new List<string>();
        if (root.ValueKind != JsonValueKind.Object) return "";
        if (root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object &&
            Text(model, "display_name") is { Length: > 0 } name)
            parts.Add($"{Esc}[36m[{name}]{Esc}[0m");
        if (root.TryGetProperty("rate_limits", out var limits) && limits.ValueKind == JsonValueKind.Object &&
            limits.TryGetProperty("five_hour", out var five) && five.ValueKind == JsonValueKind.Object &&
            Number(five, "used_percentage") is { } pct)
        {
            // Rounded half to even, as feed.ps1 did with [math]::Round.
            var p5 = (int)Math.Round(pct);
            var color = p5 >= 90 ? "31" : p5 >= 70 ? "33" : "32";
            parts.Add($"5h: {Esc}[{color}m{p5}%{Esc}[0m");
        }
        return string.Join(" | ", parts);
    }

    // ---- notify ------------------------------------------------------------------

    /// <summary>One line per notification (a permission request, a question...) in events.jsonl.</summary>
    public static void Notify(string input, string claudeDir)
    {
        using var doc = TryParse(input);
        if (doc?.RootElement is not { ValueKind: JsonValueKind.Object } root) return;
        var session = Text(root, "session_id");
        if (session.Length == 0) return;

        var dir = Path.Combine(claudeDir, "usage-widget");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "events.jsonl");
        // Keep the file small; the widget notices the rotation.
        if (File.Exists(file) && new FileInfo(file).Length > 200 * 1024) File.Delete(file);

        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            w.WriteString("event", Text(root, "hook_event_name"));
            w.WriteString("session_id", session);
            w.WriteString("type", Text(root, "notification_type"));
            w.WriteString("message", Text(root, "message"));
            w.WriteEndObject();
        }
        using var fs = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        fs.Write(buffer.ToArray());
        fs.WriteByte((byte)'\n');
    }

    // ---- helpers -----------------------------------------------------------------

    private static JsonDocument? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonDocument.Parse(text); }
        catch (JsonException) { return null; }
    }

    private static string? TryRead(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Utf8);
            return reader.ReadToEnd();
        }
        catch { return null; }
    }

    /// <summary>
    /// Write then rename, so a reader never sees half a file. Two sessions
    /// writing at once: one rename loses, and the next refresh writes again;
    /// either way no temp file is left behind.
    /// </summary>
    private static void WriteAtomic(string file, byte[] bytes)
    {
        var tmp = $"{file}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, file, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            _ => "",
        } : "";

    private static double? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
}
