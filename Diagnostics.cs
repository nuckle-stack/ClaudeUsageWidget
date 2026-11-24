using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ClaudeUsageWidget;

/// <summary>
/// A plain-text report of what the widget sees on this PC, to paste into a
/// message when something does not work on someone else's machine. It leaves
/// out what is private: no session names, prompts or messages, only the names
/// of environment variables and settings (never their values), and the user's
/// folders written as %USERPROFILE%, %LOCALAPPDATA% and %APPDATA%.
/// </summary>
public static class Diagnostics
{
    /// <summary>What only the running widget knows.</summary>
    public record Live(
        Config Config, Limits Statusline, Limits Ping, bool HelloRunning,
        (int Failures, long NextAttemptAt) Backoff, string Hotkey, List<SessionInfo> Sessions, int Waiting,
        string Updates = "", string Format = "", string Window = "");

    public static readonly string ReportPath = Path.Combine(Paths.Dir, "diagnostico.txt");

    private const int LogLines = 40;

    /// <summary>How each Claude Code found answers --version. Replaced in tests, so they start no real one.</summary>
    internal static Func<string, Task<string>> VersionOf = RunVersionAsync;

    public static async Task<string> BuildAsync(Live live)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claudeDir = Path.GetDirectoryName(Paths.Dir)!;
        var candidates = AutoHello.Candidates(live.Config.ClaudePath);
        var versions = await Task.WhenAll(candidates.Select(c => VersionOf(c.Path)));
        await UsageLedger.Shared.RefreshAsync(TimeSpan.FromMinutes(1));

        var sb = new StringBuilder();
        void Line(string text) => sb.AppendLine(text);
        void Section(string title) => sb.AppendLine().AppendLine($"== {title} ==");

        Line($"Diagnóstico do Claude Usage Widget, {DateTime.Now:dd/MM/yyyy HH:mm} (UTC{DateTimeOffset.Now:zzz})");

        Section("Widget");
        var exe = Environment.ProcessPath ?? "";
        var built = File.Exists(exe) ? $" · exe de {File.GetLastWriteTime(exe):dd/MM/yyyy HH:mm}" : "";
        Line($"Versão: {Version()}{built}");
        Line($"Exe: {exe}");
        Line($"Windows {Environment.OSVersion.Version} {RuntimeInformation.OSArchitecture} · .NET {Environment.Version} · {CultureInfo.CurrentUICulture.Name}");
        // The widget's own (runtime\ next to the exe) or, in a build folder, the machine's.
        Line($".NET em: {RuntimeEnvironment.GetRuntimeDirectory()}");
        Line($"Dados: {Paths.Dir}" + (Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } ? " (por CLAUDE_CONFIG_DIR)" : ""));
        Line($"Arranque com o Windows: {(AutoStarts() ? "sim" : "não")}");

        Section("Claude Code");
        if (candidates.Count == 0) Line("Nenhum encontrado (nem CLI, nem app Desktop, nem extensão do VS Code).");
        for (var i = 0; i < candidates.Count; i++)
        {
            var (path, source) = candidates[i];
            var last = string.Equals(path, AutoHello.LastGood, StringComparison.OrdinalIgnoreCase) ? " · usado no último hello" : "";
            Line($"{source}: {path} · {versions[i]}{last}");
        }
        if (live.Config.ClaudePath is { Length: > 0 } configured && configured != "claude") Line($"Caminho nas definições: {configured}");
        Line($"Login do CLI (.credentials.json): {(File.Exists(Path.Combine(claudeDir, ".credentials.json")) ? "existe" : "não existe")}");
        var vars = EnvironmentNames().ToList();
        Line(vars.Count > 0
            ? $"Variáveis de ambiente (só os nomes): {string.Join(", ", vars)}"
            : "Variáveis de ambiente: nenhuma das que mudam a conta, o servidor ou a rede.");
        if (SessionScanner.LastHealth.Version is { Length: > 0 } running) Line($"Versão nas sessões abertas: {running}");
        Line($"Formato dos ficheiros: {(live.Format.Length > 0 ? live.Format : "não verificado (o widget não estava aberto)")}");

        Section("settings.json");
        foreach (var line in SettingsLines(claudeDir)) Line(line);

        Section("Leituras");
        Line("Statusline (limits.json): " + (live.Statusline.UpdatedAt > 0
            ? $"{Ago(live.Statusline.UpdatedAt, now)} · {Windows(live.Statusline)}"
            : "nunca escreveu (o statusline não corre, ou ainda nenhuma sessão no terminal respondeu)."));
        Line("Último hello (ping.json): " + (live.Ping.UpdatedAt > 0
            ? $"{Ago(live.Ping.UpdatedAt, now)} · {Windows(live.Ping)}{(live.Ping.Estimated ? " · estimativa, sem limites na resposta" : "")}{Overage(live.Ping.Overage)}"
            : "nenhum."));
        var usageRead = UsageScreen.TryLoad(UsageScreen.ConfigFile, out var shown, out var split, out var missing);
        Line("Último /usage (.claude.json): " + (!usageRead
            ? "não se consegue ler."
            : shown is null ? "nenhum (aparece depois de abrir /usage no Claude Code)."
            : $"{Ago(shown.UpdatedAt, now)} · {Windows(shown)}"));
        if (usageRead && shown is not null) Line("De onde veio o uso (/usage): " + Split(split, missing, live, now));
        Line("Hello: " + (live.HelloRunning ? "a correr agora."
            : live.Backoff.Failures == 0 ? "sem falhas."
            : $"{live.Backoff.Failures} falha(s) seguidas; próxima tentativa {At(live.Backoff.NextAttemptAt)}."));
        Line($"Último uso do Claude Code neste PC: {Ago(UsageLedger.Shared.LastUsedAt, now)}");
        var history = Json.Load<HistoryFile>(Paths.History);
        Line($"Histórico (history.json): {history.Samples.Count} leituras" +
             (history.Samples.Count > 0 ? $", a última {Ago(history.Samples[^1].T, now)}" : ""));
        var events = new FileInfo(Paths.Events);
        Line("Pedidos do hook (events.jsonl): " + (events.Exists
            ? $"{events.Length / 1024.0:0.#} KB, alterado {Ago(new DateTimeOffset(events.LastWriteTimeUtc).ToUnixTimeSeconds(), now)}"
            : "ainda não existe (nenhuma sessão pediu nada desde a instalação, ou o hook não corre)."));
        var contextDir = new DirectoryInfo(Path.Combine(Paths.Dir, "context"));
        var reports = contextDir.Exists ? contextDir.GetFiles("*.json") : [];
        Line("Janelas de contexto (context\\): " + (reports.Length > 0
            ? $"{reports.Length} sessão(ões), a última {Ago(new DateTimeOffset(reports.Max(f => f.LastWriteTimeUtc)).ToUnixTimeSeconds(), now)}"
            : "nenhuma (o statusline ainda não as escreveu: o \"% ctx\" é estimado)."));

        Section("Definições");
        var cfg = live.Config;
        var hello = cfg.AutoHello;
        Line("Auto-hello: " + (!hello.Enabled ? "desligado"
            : (hello.Mode == "schedule" ? $"ligado, às {hello.Schedule}" : $"ligado, quando a janela renova, das {hello.From} às {hello.To}") +
              (hello.WeekdaysOnly ? ", só dias úteis" : "") + $", modelo {hello.Model}"));
        Line("Atualizar enquanto há uso: " + (cfg.RefreshMinutes > 0 ? $"a cada {cfg.RefreshMinutes} min" : "nunca"));
        Line("Avisos das sessões: " + (cfg.Alerts.Enabled
            ? $"ligados (som {(cfg.Alerts.Sound ? "ligado" : "desligado")}, tarefas a partir de {cfg.Alerts.MinTaskSeconds} s, relembrar a cada {cfg.Alerts.RemindMinutes} min, " +
              $"sem avançar {(cfg.Alerts.StallMinutes > 0 ? $"ao fim de {cfg.Alerts.StallMinutes} min" : "desligado")})"
            : "desligados"));
        var usage = cfg.UsageAlerts;
        Line($"Avisos de uso: níveis {(usage.Levels.Length > 0 ? string.Join(", ", usage.Levels) + "%" : "nenhum")} · ritmo {YesNo(usage.Forecast)} · " +
             $"parte do dia {YesNo(usage.DailyBudget)}{(usage.BudgetWeekdaysOnly ? " (dias úteis)" : " (todos os dias)")} · " +
             $"sobra da semana {(usage.LeftoverAbove > 0 ? $"a partir de {usage.LeftoverAbove}%" : "desligado")}");
        Line($"Avisos de contexto: {(cfg.ContextAlerts.Enabled ? $"qualidade abaixo de {cfg.ContextAlerts.QualityBelow}, ocupação acima de {cfg.ContextAlerts.FillAbove}%" : "desligados")}");
        Line($"Atalho: {live.Hotkey}");
        Line($"Versões novas: {(live.Updates.Length > 0 ? live.Updates : (live.Config.CheckUpdates ? "ligado" : "desligado"))}");
        Line("Não incomodar: " + cfg.QuietUntil switch
        {
            0 => "desligado",
            -1 => "ligado até ser desligado",
            var until when until > now => $"ligado até {At(until)}",
            _ => "desligado",
        });

        Section("Sessões");
        var kinds = live.Sessions.GroupBy(s => s.Kind.Length > 0 ? s.Kind : "?").Select(g => $"{g.Key} {g.Count()}");
        Line($"Abertas: {live.Sessions.Count}" + (live.Sessions.Count > 0 ? $" ({string.Join(", ", kinds)})" : "") + (live.Waiting >= 0 ? $" · à tua espera: {live.Waiting}" : ""));
        var sessionsDir = Path.Combine(claudeDir, "sessions");
        Line($"Ficheiros em sessions\\: {(Directory.Exists(sessionsDir) ? Directory.EnumerateFiles(sessionsDir, "*.json").Count() : 0)}");

        Section("Janela e processos");
        Line("Janela: " + (live.Window.Length > 0 ? live.Window : "desconhecida (relatório feito fora do widget; o \"Copiar diagnóstico\" do widget mostra-a)"));
        // Another copy holds the single-instance lock; a new widget would close itself.
        var copies = Installer.OtherCopies();
        Line(copies.Count == 0
            ? "Outros processos do widget a correr: nenhum."
            : "Outros processos do widget a correr: " + string.Join("; ", copies.Select(c =>
                $"pid {c.Pid}, {c.Path}{(c.Started == default ? "" : $", desde {c.Started:dd/MM HH:mm}")}")));

        Section($"widget.log (últimas {LogLines} linhas)");
        foreach (var line in LogTail(Paths.Log, LogLines)) Line(LogLine(line));

        return Scrub(sb.ToString());
    }

    // ---- settings.json -----------------------------------------------------

    /// <summary>What the widget needs from Claude Code's settings: its statusline, its hook, and what could get in their way.</summary>
    internal static List<string> SettingsLines(string claudeDir)
    {
        var lines = new List<string>();
        var path = Path.Combine(claudeDir, "settings.json");
        var widgetDir = Path.Combine(claudeDir, "usage-widget");
        if (!File.Exists(path))
        {
            lines.Add("Não existe: o statusline e o hook do widget não estão instalados.");
            return lines;
        }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (Exception ex)
        {
            lines.Add($"Não se consegue ler ({ex.GetType().Name}): o Claude Code ignora-o, e com ele o statusline e o hook.");
            return lines;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                lines.Add("Não é um objeto JSON: o Claude Code ignora-o.");
                return lines;
            }

            var command = root.TryGetProperty("statusLine", out var sl) && sl.ValueKind == JsonValueKind.Object
                ? Str(sl, "command") : "";
            lines.Add("Statusline: " + StatusLineKind(command, widgetDir));

            var hookCommands = HookCommands(root, "Notification");
            var hook = hookCommands.Any(c => c.Contains(Installer.FeedName, StringComparison.OrdinalIgnoreCase)) ? "instalado"
                : hookCommands.Any(c => c.Contains("usage-widget", StringComparison.OrdinalIgnoreCase) && c.Contains("notify.ps1", StringComparison.OrdinalIgnoreCase))
                    ? "instalado, ainda pelo PowerShell (notify.ps1)"
                    : "em falta (os pedidos \"precisa de ti\" não chegam ao widget)";
            lines.Add("Hook Notification do widget: " + hook);
            // The feed both point at: it must be there, or Claude Code runs nothing.
            if (hookCommands.Prepend(command).Select(FeedPath).FirstOrDefault(p => p is not null) is { } feed)
                lines.Add($"{Installer.FeedName}: {(File.Exists(feed) ? "existe" : "em falta")} ({feed})");
            if (root.TryGetProperty("disableAllHooks", out var off) && off.ValueKind == JsonValueKind.True)
                lines.Add("disableAllHooks: ligado (nenhum hook corre, nem o do widget)");
            if (root.TryGetProperty("apiKeyHelper", out _)) lines.Add("apiKeyHelper: definido (a conta pode ser por chave de API, sem limites 5h/7d)");
            if (root.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object &&
                env.EnumerateObject().Select(p => p.Name).ToList() is { Count: > 0 } names)
                lines.Add($"env (só os nomes): {string.Join(", ", names)}");
        }

        return lines;
    }

    /// <summary>The commands of one hook event's entries in settings.json.</summary>
    private static List<string> HookCommands(JsonElement root, string hookEvent)
    {
        var commands = new List<string>();
        if (!root.TryGetProperty("hooks", out var hooks) || hooks.ValueKind != JsonValueKind.Object ||
            !hooks.TryGetProperty(hookEvent, out var entries) || entries.ValueKind != JsonValueKind.Array) return commands;
        foreach (var entry in entries.EnumerateArray())
            if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("hooks", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var h in list.EnumerateArray())
                    if (h.ValueKind == JsonValueKind.Object && Str(h, "command") is { Length: > 0 } c) commands.Add(c);
        return commands;
    }

    /// <summary>The ClaudeUsageFeed.exe a command runs ("C:\...\ClaudeUsageFeed.exe" statusline), or null.</summary>
    internal static string? FeedPath(string command)
    {
        var quoted = Regex.Match(command, "\"([^\"]*" + Regex.Escape(Installer.FeedName) + ")\"", RegexOptions.IgnoreCase);
        if (quoted.Success) return quoted.Groups[1].Value;
        var bare = Regex.Match(command, @"(\S*" + Regex.Escape(Installer.FeedName) + ")", RegexOptions.IgnoreCase);
        return bare.Success ? bare.Groups[1].Value : null;
    }

    /// <summary>Whether the statusline feeds the widget, the way the installer decides it.</summary>
    internal static string StatusLineKind(string command, string widgetDir)
    {
        if (command.Length == 0) return "nenhum (os limites só chegam pelos hellos).";
        var wrapping = File.Exists(Path.Combine(widgetDir, "wrapped-command.txt")) ? ", que chama também o statusline que já havia." : ".";
        if (command.Contains(Installer.FeedName, StringComparison.OrdinalIgnoreCase))
            return "o do widget (ClaudeUsageFeed.exe)" + wrapping;
        if (command.Contains("usage-widget", StringComparison.OrdinalIgnoreCase) &&
            command.Contains("feed.ps1", StringComparison.OrdinalIgnoreCase))
            return "o do widget, ainda pelo PowerShell (feed.ps1)" + wrapping;
        var script = Regex.Match(command, "\"([^\"]+\\.ps1)\"|(\\S+\\.ps1)");
        if (script.Success)
        {
            var file = script.Groups[1].Success ? script.Groups[1].Value : script.Groups[2].Value;
            try
            {
                if (File.Exists(file) && File.ReadAllText(file).Contains("usage-widget", StringComparison.OrdinalIgnoreCase))
                    return $"um script próprio que também escreve para o widget ({file}).";
            }
            catch { }
        }
        return $"outro, que não escreve para o widget: {command}";
    }

    // ---- log ---------------------------------------------------------------

    private static List<string> LogTail(string path, int count)
    {
        try
        {
            if (!File.Exists(path)) return ["(vazio)"];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var tail = new Queue<string>();
            while (reader.ReadLine() is { } line)
            {
                tail.Enqueue(line);
                if (tail.Count > count) tail.Dequeue();
            }
            return tail.Count > 0 ? tail.ToList() : ["(vazio)"];
        }
        catch (Exception ex) { return [$"(não se consegue ler: {ex.Message})"]; }
    }

    /// <summary>
    /// A widget.log line without what could identify a session or quote it:
    /// session and context alerts keep their kind but lose their text, and
    /// focus lines lose the session's name.
    /// </summary>
    internal static string LogLine(string line)
    {
        // "yyyy-MM-dd HH:mm:ss message"; anything else continues the entry above (a stack trace).
        if (line.Length < 20 || !DateTime.TryParseExact(line[..19], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return line;
        var (stamp, message) = (line[..20], line[20..]);
        if (message.StartsWith("Aviso (") || message.StartsWith("Aviso de contexto"))
        {
            var colon = message.IndexOf(": ", StringComparison.Ordinal);
            if (colon > 0) message = message[..colon] + ": …";
        }
        else if (message.StartsWith("Foco:"))
            message = Regex.Replace(message, "\"[^\"]*\"", "\"…\"");
        return stamp + message;
    }

    // ---- privacy -----------------------------------------------------------

    internal static string Scrub(string text) => Scrub(text,
        (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%"),
        (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "%APPDATA%"),
        (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%"));

    /// <summary>
    /// Writes each folder by its variable name (the most specific first), as
    /// it appears in a path, in JSON (doubled backslashes) or with forward
    /// slashes; and hides e-mail addresses, which a login error can quote.
    /// </summary>
    internal static string Scrub(string text, params (string Folder, string Name)[] folders)
    {
        foreach (var (folder, name) in folders)
        {
            var path = folder.TrimEnd('\\', '/');
            if (path.Length < 4) continue;   // never a bare drive
            foreach (var form in new[] { path.Replace("\\", "\\\\"), path, path.Replace('\\', '/') })
                text = text.Replace(form, name, StringComparison.OrdinalIgnoreCase);
        }
        return Regex.Replace(text, @"[\w.+-]+@[\w-]+(\.[\w-]+)+", "<email>");
    }

    /// <summary>
    /// The variables that change which account, server or network Claude Code
    /// uses. Only whether they are set: a value can be a key or a password.
    /// </summary>
    internal static IEnumerable<string> EnvironmentNames() =>
        new[]
        {
            "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "CLAUDE_CODE_USE_BEDROCK",
            "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CONFIG_DIR", "HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY", "NODE_EXTRA_CA_CERTS",
        }.Where(n => Environment.GetEnvironmentVariable(n) is { Length: > 0 });

    // ---- helpers -----------------------------------------------------------

    internal static string Version()
    {
        var v = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        // "1.0.0+<full commit hash>": the short hash is enough to find the commit.
        var plus = v.IndexOf('+');
        return plus > 0 && v.Length > plus + 8 ? v[..(plus + 8)] : v;
    }

    private static async Task<string> RunVersionAsync(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            };
            psi.ArgumentList.Add("--version");
            AutoHello.PrepareEnvironment(psi);
            using var p = Process.Start(psi)!;
            p.StandardInput.Close();
            var output = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                AutoHello.KillTree(p);
                return "não respondeu a --version em 10 s";
            }
            // A child left behind can hold the pipe open after claude has exited.
            var text = output.Wait(TimeSpan.FromSeconds(2)) ? output.Result.Trim() : "";
            var first = text.Split('\n')[0].Trim();
            return first.Length == 0 ? $"--version sem resposta (exit {p.ExitCode})" : first.Length > 80 ? first[..80] : first;
        }
        catch (Exception ex) { return "não arranca: " + ex.Message; }
    }

    private static bool AutoStarts()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue("ClaudeUsageWidget") is string;
        }
        catch { return false; }
    }

    private static string Windows(Limits l) => $"5h {Window(l.FiveHour)} · 7d {Window(l.SevenDay)}";

    private static string Window(LimitWindow? w) =>
        w?.ResetsAt is not { } r ? "sem dados" : $"{w.UsedPercentage ?? 0:0}% (renova {At(r)})";

    /// <summary>
    /// /usage's breakdown of the week, by its keys ("claude_code 28 · cowork 2"),
    /// or why there is none; and the week the statusline is in, when it is
    /// another one.
    /// </summary>
    internal static string Split(WeekSources? split, string missing, Live live, long now)
    {
        if (split is null) return missing + ".";
        var text = $"semana desde {At(split.Start)}, {Ago(split.AsOf, now)}: " +
                   string.Join(" · ", split.Rows.Select(r => $"{r.Key} {r.Points:0.#}"));
        if ((live.Statusline.SevenDay?.ResetsAt ?? live.Ping.SevenDay?.ResetsAt) is { } reset &&
            Math.Abs(reset - 7 * 86400 - split.Start) > 3600)
            text += $" (a semana do statusline começou {At(reset - 7 * 86400)})";
        return text;
    }

    private static string Overage(OverageInfo? o) =>
        o is null ? "" : $" · uso extra: {(o.Using ? "em uso" : "não")}{(o.Status.Length > 0 ? $" ({o.Status})" : "")}";

    private static string At(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime.ToString("dd/MM HH:mm");

    internal static string Ago(long unix, long now)
    {
        if (unix <= 0) return "nunca (na última semana)";
        var s = now - unix;
        var when = $" ({At(unix)})";
        if (s < 60) return "agora" + when;
        if (s < 3600) return $"há {s / 60} min" + when;
        if (s < 86400) return $"há {s / 3600} h {s % 3600 / 60:00} min" + when;
        return $"há {s / 86400} dia(s)" + when;
    }

    private static string YesNo(bool on) => on ? "sim" : "não";

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
