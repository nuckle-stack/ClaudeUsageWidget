using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace ClaudeUsageWidget;

/// <summary>
/// Sends a one-line prompt through the Claude Code CLI. That does two jobs:
/// the CLI reports the real rate limits in its output (the only source when
/// Claude is used from the desktop app, where no statusline runs), and when
/// the 5-hour window has expired the prompt starts a new one.
/// </summary>
public class AutoHello
{
    private const int WindowSeconds = 5 * 3600;
    private static readonly int[] BackoffSeconds = [60, 300, 900];

    private readonly State _state = Json.Load<State>(Paths.State);
    private Limits _ping = Json.Load<Limits>(Paths.Ping);
    private long _activity;
    private DateTime _activityChecked;

    public bool Running { get; private set; }

    /// <summary>The last hello's reading: the only one that says whether extra usage is on.</summary>
    public Limits Ping => _ping;

    /// <summary>The freshest limits: the statusline feed or the last ping.</summary>
    public Limits Current(Limits statusline) =>
        statusline.UpdatedAt >= _ping.UpdatedAt ? statusline : _ping;

    public async Task CheckAsync(Config cfg, Limits statusline)
    {
        if (Running) return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now < _state.NextAttemptAt) return;

        var cur = Current(statusline);
        var resetsAt = cur.FiveHour?.ResetsAt ?? 0;
        // A minute of slack so we never land just before the reset.
        var expired = resetsAt == 0 || now >= resetsAt + 60;

        // Start a new window: only when the user asked for it, and only in hours.
        if (expired && cfg.AutoHello.Enabled && ShouldStart(cfg.AutoHello, resetsAt, DateTime.Now))
        {
            await SendAsync(cfg);
            return;
        }

        if (cfg.RefreshMinutes <= 0) return;

        // A limit just reached 100%: from here Claude either stops or goes on
        // with paid extra usage, and only the CLI's own output says which. Ask
        // once per window, straight away, rather than at the next refresh.
        if (FullWindow(cur) is { } full && full != _state.FullPingFor && _ping.UpdatedAt < cur.UpdatedAt)
        {
            _state.FullPingFor = full;
            await SendAsync(cfg);
            return;
        }

        // Refresh the numbers: only when Claude has been used since the last
        // reading, so an idle machine sends nothing and a ping never opens a
        // window the user did not already open.
        if (now - cur.UpdatedAt < cfg.RefreshMinutes * 60L) return;
        var activity = await LastActivityAsync();
        if (activity > cur.UpdatedAt && now - activity < WindowSeconds)
            await SendAsync(cfg);
    }

    public async Task<bool> SendAsync(Config cfg)
    {
        if (Running) return false;
        Running = true;
        try
        {
            // Try each Claude Code we can find, the one that worked last time
            // first: a CLI that is installed but not logged in should not stop
            // the desktop app's or the VS Code extension's copy from working.
            var (ok, detail, limits, used) = (false, "Claude Code não encontrado (nem CLI, nem app Desktop, nem extensão do VS Code).", (Limits?)null, "");
            var failures = new List<string>();
            foreach (var (path, source) in Candidates(cfg.ClaudePath).OrderBy(c => c.Path == _lastGood ? 0 : 1))
            {
                bool timedOut;
                (ok, detail, limits, timedOut) = await RunClaudeAsync(cfg, path);
                if (ok) { _lastGood = path; used = source; break; }
                failures.Add($"[{source}] {detail}");
                // A hang is the network or the machine, not this copy of Claude
                // Code: the next copy would hang just as long.
                if (timedOut) break;
            }
            if (!ok && failures.Count > 0) detail = string.Join(" | ", failures);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (ok)
            {
                // No limits in the output: assume a window that began just now.
                _ping = limits ?? new Limits
                {
                    Estimated = true,
                    FiveHour = new LimitWindow { UsedPercentage = 0, ResetsAt = now + WindowSeconds },
                    SevenDay = _ping.SevenDay,
                };
                _ping.UpdatedAt = now;
                Json.Save(Paths.Ping, _ping);
                _state.Attempts = 0;
                _state.NextAttemptAt = 0;
                Log.Write(limits is null
                    ? $"Hello enviado ({used}); sem limites na resposta, a usar estimativa."
                    : $"Hello enviado ({used}); 5h {limits.FiveHour?.UsedPercentage:0}%, 7d {limits.SevenDay?.UsedPercentage:0}%.");
            }
            else
            {
                _state.Attempts++;
                _state.NextAttemptAt = now + BackoffSeconds[Math.Min(_state.Attempts, BackoffSeconds.Length) - 1];
                Log.Write($"Hello falhou (tentativa {_state.Attempts}): {detail}");
            }
            Json.Save(Paths.State, _state);
            return ok;
        }
        finally { Running = false; }
    }

    /// <summary>The reset of a still-running window that is used up, or null.</summary>
    public static long? FullWindow(Limits cur)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var w in new[] { cur.FiveHour, cur.SevenDay })
            if (w is { UsedPercentage: >= 100, ResetsAt: { } r } && r > now) return r;
        return null;
    }

    /// <summary>Whether to open a new window now, given that the last one (ending at resetsAt) has expired.</summary>
    public static bool ShouldStart(AutoHelloConfig cfg, long resetsAt, DateTime local) =>
        cfg.Mode == "schedule" ? ScheduleDue(cfg, resetsAt, local) : InAllowedHours(cfg, local);

    /// <summary>
    /// Schedule mode: true when one of today's start times has passed and no
    /// window has been opened for it yet. Windows begin on the hour, so a window
    /// that began up to an hour before a start time counts as serving it. A
    /// start time missed because the old window was still running (or the PC was
    /// off) is served as soon as possible afterwards, never on a later day.
    /// </summary>
    public static bool ScheduleDue(AutoHelloConfig cfg, long resetsAt, DateTime local)
    {
        if (cfg.WeekdaysOnly && local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;

        var lastStart = resetsAt > 0
            ? DateTimeOffset.FromUnixTimeSeconds(resetsAt - WindowSeconds).LocalDateTime
            : DateTime.MinValue;
        foreach (var slot in ParseSchedule(cfg.Schedule))
        {
            var at = local.Date + slot;
            if (at <= local && (lastStart == DateTime.MinValue || at > lastStart.AddHours(1))) return true;
        }
        return false;
    }

    /// <summary>The valid "HH:mm" entries of a schedule, in order; invalid ones are skipped.</summary>
    public static List<TimeSpan> ParseSchedule(string schedule) =>
        schedule.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => TimeSpan.TryParse(part, out var t) && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1) ? t : (TimeSpan?)null)
            .Where(t => t is not null).Select(t => t!.Value)
            .Distinct().Order().ToList();

    public static bool InAllowedHours(AutoHelloConfig cfg, DateTime local)
    {
        if (cfg.WeekdaysOnly && local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        if (!TimeSpan.TryParse(cfg.From, out var from) || !TimeSpan.TryParse(cfg.To, out var to)) return true;
        var t = local.TimeOfDay;
        return from <= to ? t >= from && t < to : t >= from || t < to;
    }

    /// <summary>
    /// When a Claude Code session (terminal or desktop app) last wrote to its
    /// transcript, in unix seconds. Our own pings don't persist a session, so
    /// they never count. Rescanned at most once a minute.
    /// </summary>
    private async Task<long> LastActivityAsync()
    {
        if (DateTime.UtcNow - _activityChecked < TimeSpan.FromMinutes(1)) return _activity;
        _activityChecked = DateTime.UtcNow;
        _activity = await Task.Run(() =>
        {
            try
            {
                var projects = Path.Combine(Path.GetDirectoryName(Paths.Dir)!, "projects");
                if (!Directory.Exists(projects)) return 0L;
                var latest = Directory.EnumerateDirectories(projects)
                    .SelectMany(d => new DirectoryInfo(d).EnumerateFiles("*.jsonl"))
                    .Select(f => f.LastWriteTimeUtc)
                    .DefaultIfEmpty(DateTime.UnixEpoch)
                    .Max();
                return new DateTimeOffset(latest, TimeSpan.Zero).ToUnixTimeSeconds();
            }
            catch { return 0L; }
        });
        return _activity;
    }

    /// <summary>A hello answers in seconds; this is for a slow first start, not a stuck one.</summary>
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(90);

    private static async Task<(bool Ok, string Detail, Limits? Limits, bool TimedOut)> RunClaudeAsync(Config cfg, string claude)
    {
        try
        {
            // On a fresh install nothing has created the folder yet.
            Directory.CreateDirectory(Paths.Dir);
            var psi = new ProcessStartInfo
            {
                FileName = claude,
                WorkingDirectory = Paths.Dir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                // Claude Code writes UTF-8; the default (the console code page) garbles "·" and accents.
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            string[] args =
            [
                "-p", string.IsNullOrWhiteSpace(cfg.AutoHello.Message) ? "hello" : cfg.AutoHello.Message,
                "--model", string.IsNullOrWhiteSpace(cfg.AutoHello.Model) ? "haiku" : cfg.AutoHello.Model,
                "--no-session-persistence", "--disable-slash-commands", "--strict-mcp-config",
                // The user's hooks are for their sessions, not for this ping, and
                // one that waits on something (a prompt, a server) hangs it.
                "--settings", """{"disableAllHooks":true}""",
                // The rate limits only appear in the event stream.
                "--output-format", "stream-json", "--verbose",
            ];
            foreach (var a in args) psi.ArgumentList.Add(a);

            // If the widget was launched from inside a Claude Code session, don't
            // let the child think it is nested in that session.
            foreach (var key in psi.Environment.Keys.ToList())
                if (key.StartsWith("CLAUDECODE", StringComparison.OrdinalIgnoreCase) ||
                    key.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase))
                    psi.Environment.Remove(key);
            // No update check, telemetry or plugin sync: none is needed for one
            // message, and each can stall on a slow or filtered network. Plugin
            // sync runs git, which with no window to ask in waits forever for
            // credentials unless told not to ask.
            psi.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

            using var p = Process.Start(psi)!;
            p.StandardInput.Close();
            var stderr = p.StandardError.ReadToEndAsync();

            // Read line by line and stop at the "result" event: everything needed
            // has arrived by then. Waiting for the process to exit, or for the
            // pipe to close, is not enough on its own: a child process left
            // behind keeps the pipe open after claude itself has exited.
            var output = new System.Text.StringBuilder();
            bool? isError = null;   // set by the result event
            using var cts = new CancellationTokenSource(HelloTimeout);
            try
            {
                while (await p.StandardOutput.ReadLineAsync(cts.Token) is { } line)
                {
                    output.AppendLine(line);
                    if ((isError = ResultError(line)) is not null) break;
                }
                // Give it a moment to exit by itself, for the exit code.
                using var grace = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                grace.CancelAfter(TimeSpan.FromSeconds(10));
                try { await p.WaitForExitAsync(grace.Token); }
                catch (OperationCanceledException) when (!cts.IsCancellationRequested) { }
            }
            catch (OperationCanceledException)
            {
                KillTree(p);
                var seen = output.Length > 0 ? $" (última saída: {FailureDetail("", output.ToString())})" : " (sem saída)";
                return (false, $"sem resposta em {HelloTimeout.TotalSeconds:0} s{seen}", null, true);
            }
            KillTree(p);

            var text = output.ToString();
            var exitCode = p.HasExited ? p.ExitCode : 0;
            // The result event says whether the turn failed even when the
            // process is still lingering.
            if (exitCode == 0 && isError == false) return (true, "ok", ParseLimits(text), false);

            var err = stderr.IsCompleted ? await stderr : "";
            var why = exitCode != 0 ? $"exit {exitCode}" : isError == true ? "erro" : "terminou sem resposta";
            return (false, $"{why}: {FailureDetail(err, text)}", null, false);
        }
        catch (Exception ex) { return (false, ex.Message, null, false); }
    }

    /// <summary>
    /// For the final "result" event of a stream-json run: whether the turn
    /// failed. Null for any other line. Its "type" sits mid-line, after the
    /// usage figures, so the line is parsed rather than matched as text.
    /// </summary>
    public static bool? ResultError(string line)
    {
        if (!line.Contains("\"result\"")) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object ||
                !e.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                type.GetString() != "result") return null;
            return e.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return null; }
    }

    private static void KillTree(Process p)
    {
        try { if (!p.HasExited) p.Kill(true); } catch { }
    }

    /// <summary>
    /// The part of a failed run worth logging. stdout opens with a long "init"
    /// event, so the cause is in stderr, in the final "result" event, or failing
    /// those in the last line.
    /// </summary>
    public static string FailureDetail(string stderr, string stdout)
    {
        var text = stderr.Trim();
        if (text.Length == 0)
        {
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            text = lines.Reverse().Select(ResultText).FirstOrDefault(t => t.Length > 0)
                   ?? (lines.Length > 0 ? lines[^1] : "sem saída");
        }
        return text.Length > 500 ? text[..500] : text;

        static string ResultText(string line)
        {
            if (!line.Contains("\"result\"")) return "";
            try
            {
                using var doc = JsonDocument.Parse(line);
                var e = doc.RootElement;
                if (!e.TryGetProperty("type", out var type) || type.GetString() != "result") return "";
                var parts = new List<string>();
                // "success" also shows up on errors (is_error: true), so it says nothing.
                if (e.TryGetProperty("subtype", out var sub) && sub.ValueKind == JsonValueKind.String &&
                    sub.GetString() != "success")
                    parts.Add(sub.GetString()!);
                if (e.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String)
                    parts.Add(r.GetString()!);
                if (e.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array)
                    parts.AddRange(errs.EnumerateArray().Select(x => x.ToString()));
                return string.Join(": ", parts.Where(s => s.Length > 0));
            }
            catch (JsonException) { return ""; }
        }
    }

    /// <summary>
    /// Reads the last rate_limit_event of a stream-json run:
    /// rate_limit_info.unifiedWindows.{five_hour,seven_day}.{utilization 0..1, resetsAt},
    /// and the extra-usage state beside them (isUsingOverage, overageStatus,
    /// rateLimitType and resetsAt of the binding limit).
    /// </summary>
    public static Limits? ParseLimits(string output)
    {
        Limits? found = null;
        foreach (var line in output.Split('\n'))
        {
            if (!line.Contains("\"rate_limit_event\"")) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("rate_limit_info", out var info) ||
                    !info.TryGetProperty("unifiedWindows", out var windows)) continue;
                var limits = new Limits
                {
                    FiveHour = Window(windows, "five_hour"),
                    SevenDay = Window(windows, "seven_day"),
                    Overage = new OverageInfo
                    {
                        Using = info.TryGetProperty("isUsingOverage", out var u) && u.ValueKind == JsonValueKind.True,
                        Status = Text(info, "overageStatus"),
                        LimitType = Text(info, "rateLimitType"),
                        ResetsAt = info.TryGetProperty("resetsAt", out var r) && r.TryGetInt64(out var at) ? at : 0,
                    },
                };
                if (limits.FiveHour is not null) found = limits;
            }
            catch (JsonException) { }
            catch (InvalidOperationException) { }   // a field of an unexpected type
        }
        return found;

        static string Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        static LimitWindow? Window(JsonElement windows, string name)
        {
            if (!windows.TryGetProperty(name, out var w) ||
                !w.TryGetProperty("resetsAt", out var r) || !r.TryGetInt64(out var resetsAt)) return null;
            var used = w.TryGetProperty("utilization", out var u) && u.TryGetDouble(out var v) ? v * 100 : 0;
            return new LimitWindow { UsedPercentage = used, ResetsAt = resetsAt };
        }
    }

    /// <summary>The Claude Code that worked last, tried first next time.</summary>
    private static string? _lastGood;

    /// <summary>
    /// Every Claude Code executable on this machine, best first, with where it
    /// came from: the configured path, the standalone CLI, then the newest copy
    /// bundled with the VS Code extension and with the desktop app. The bundled
    /// ones are found by searching, as their folders change with every update.
    /// </summary>
    public static List<(string Path, string Source)> Candidates(string configured)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var found = new List<(string, string)>();

        void Add(string? path, string source)
        {
            if (path is null || !File.Exists(path)) return;
            var full = Path.GetFullPath(path);
            if (!found.Any(f => string.Equals(f.Item1, full, StringComparison.OrdinalIgnoreCase))) found.Add((full, source));
        }

        if (!string.IsNullOrWhiteSpace(configured))
            Add(Path.IsPathRooted(configured) ? configured : OnPath(configured), "CLI");
        Add(Path.Combine(home, ".local", "bin", "claude.exe"), "CLI");
        Add(OnPath("claude"), "CLI");

        // VS Code (and its forks) extension: <ext>\anthropic.claude-code-<ver>[-<platform>]\...\claude.exe
        foreach (var editor in new[] { ".vscode", ".vscode-insiders", ".cursor", ".windsurf" })
            Add(Newest(Search(Path.Combine(home, editor, "extensions"), "anthropic.claude-code-*", 5)).FirstOrDefault(), "VS Code");

        // Desktop app: Store install keeps it under Claude-Data\<package>\claude-code\<ver>\<hash>\,
        // the regular install under %APPDATA%\Claude\claude-code\<ver>\...
        var desktopRoots = new List<string> { Path.Combine(roaming, "Claude", "claude-code") };
        if (Directory.Exists(Path.Combine(local, "Claude-Data")))
            desktopRoots.AddRange(SafeDirs(Path.Combine(local, "Claude-Data")).Select(d => Path.Combine(d, "claude-code")));
        // Only the newest: older versions left behind by updates would fail the same way.
        Add(Newest(desktopRoots.SelectMany(r => Search(r, "*", 3))).FirstOrDefault(), "Desktop");

        return found;
    }

    /// <summary>claude.exe files under the matching subfolders of root, at most depth levels down.</summary>
    private static IEnumerable<string> Search(string root, string pattern, int depth)
    {
        if (!Directory.Exists(root)) return [];
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = depth, IgnoreInaccessible = true };
            return Directory.EnumerateDirectories(root, pattern)
                .SelectMany(d => Directory.EnumerateFiles(d, "claude.exe", options))
                .ToList();
        }
        catch { return []; }
    }

    /// <summary>Newest first, by the version found in the path (e.g. ...\2.1.326\...), then by date.</summary>
    private static IEnumerable<string> Newest(IEnumerable<string> files) =>
        files.OrderByDescending(f => f.Split('\\', '/', '-')
                .Select(p => Version.TryParse(p, out var v) ? v : null)
                .LastOrDefault(v => v is not null) ?? new Version(0, 0))
            .ThenByDescending(f => { try { return File.GetLastWriteTimeUtc(f); } catch { return DateTime.MinValue; } });

    private static IEnumerable<string> SafeDirs(string root)
    {
        try { return Directory.EnumerateDirectories(root).ToList(); }
        catch { return []; }
    }

    private static string? OnPath(string name)
    {
        var file = Path.HasExtension(name) ? name : name + ".exe";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim().Trim('"'), file);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }
}
