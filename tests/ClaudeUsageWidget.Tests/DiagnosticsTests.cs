namespace ClaudeUsageWidget.Tests;

/// <summary>The report a colleague pastes when something does not work.</summary>
public class DiagnosticsTests
{
    private readonly string _dir = Path.Combine(Isolation.Root, "diag-" + Guid.NewGuid().ToString("N"));

    private string Settings(string json)
    {
        Directory.CreateDirectory(Path.Combine(_dir, "usage-widget"));
        File.WriteAllText(Path.Combine(_dir, "settings.json"), json);
        return _dir;
    }

    // ---- privacy -------------------------------------------------------------

    [Fact]
    public void Folders_are_written_by_their_variable_in_every_form()
    {
        var text = Diagnostics.Scrub(
            @"Exe: C:\Users\Ana\AppData\Local\Programs\W\w.exe | cwd c:\users\ana\proj | json ""C:\\Users\\Ana\\x"" | C:/Users/Ana/y",
            (@"C:\Users\Ana\AppData\Local", "%LOCALAPPDATA%"), (@"C:\Users\Ana", "%USERPROFILE%"));

        Assert.DoesNotContain("Ana", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"%LOCALAPPDATA%\Programs\W\w.exe", text);
        Assert.Contains(@"%USERPROFILE%\proj", text);
        Assert.Contains(@"""%USERPROFILE%\\x""", text);
        Assert.Contains("%USERPROFILE%/y", text);
    }

    [Fact]
    public void Email_addresses_are_hidden()
    {
        Assert.Equal("Logged in as <email>.", Diagnostics.Scrub("Logged in as ana.silva+work@example.co.uk.", []));
    }

    [Fact]
    public void A_bare_drive_is_never_taken_for_a_folder()
    {
        Assert.Equal(@"C:\Tools\x", Diagnostics.Scrub(@"C:\Tools\x", (@"C:\", "%USERPROFILE%")));
    }

    [Theory]
    [InlineData("2026-11-20 01:32:30 Aviso (precisa de ti): Estado do projeto — Claude needs your permission to use Bash",
                "2026-11-20 01:32:30 Aviso (precisa de ti): …")]
    [InlineData("2026-11-20 01:32:30 Aviso (terminou) [silenciado]: Projeto X — Done",
                "2026-11-20 01:32:30 Aviso (terminou) [silenciado]: …")]
    [InlineData("2026-11-20 01:32:30 Aviso de contexto: Contexto quase cheio — Projeto X — 85%",
                "2026-11-20 01:32:30 Aviso de contexto: …")]
    [InlineData("2026-11-20 01:32:34 Foco: sessão \"Estado do projeto\" em WindowsTerminal.",
                "2026-11-20 01:32:34 Foco: sessão \"…\" em WindowsTerminal.")]
    // Usage alerts and hello results name no session, and are what a diagnosis needs.
    [InlineData("2026-11-20 01:36:05 Aviso de uso: Limite de 5 horas: ritmo alto — Vai em 9%.",
                "2026-11-20 01:36:05 Aviso de uso: Limite de 5 horas: ritmo alto — Vai em 9%.")]
    [InlineData("2026-11-20 02:27:53 Hello falhou (tentativa 1): [CLI] exit 1: {\"error\":\"x\"}",
                "2026-11-20 02:27:53 Hello falhou (tentativa 1): [CLI] exit 1: {\"error\":\"x\"}")]
    [InlineData("   at ClaudeUsageWidget.MainWindow.Tick()", "   at ClaudeUsageWidget.MainWindow.Tick()")]
    public void Log_lines_lose_session_names_and_messages(string line, string expected)
    {
        Assert.Equal(expected, Diagnostics.LogLine(line));
    }

    // ---- settings.json ---------------------------------------------------------

    [Fact]
    public void Settings_with_the_widget_installed()
    {
        var dir = Settings("""
            {
              "statusLine": { "type": "command", "command": "powershell -File \"C:\\x\\.claude\\usage-widget\\feed.ps1\"" },
              "hooks": { "Notification": [ { "hooks": [ { "type": "command", "command": "powershell -File \"C:\\x\\.claude\\usage-widget\\notify.ps1\"" } ] } ] },
              "env": { "ANTHROPIC_BASE_URL": "https://secret.example/gateway", "MY_TOKEN": "s3cr3t" }
            }
            """);
        File.WriteAllText(Path.Combine(dir, "usage-widget", "feed.ps1"), "");

        var lines = Diagnostics.SettingsLines(dir);
        Assert.Contains("Statusline: o feed do widget.", lines);
        Assert.Contains("Hook Notification do widget: instalado", lines);
        Assert.Contains("env (só os nomes): ANTHROPIC_BASE_URL, MY_TOKEN", lines);
        Assert.Contains("Ficheiros do widget: feed.ps1 existe, notify.ps1 em falta", lines);
        // Values never leave the machine.
        Assert.DoesNotContain(lines, l => l.Contains("secret") || l.Contains("s3cr3t"));
    }

    [Fact]
    public void Settings_that_keep_the_widget_from_working()
    {
        var lines = Diagnostics.SettingsLines(Settings("""{ "disableAllHooks": true, "apiKeyHelper": "get-key.cmd" }"""));
        Assert.Contains("Statusline: nenhum (os limites só chegam pelos hellos).", lines);
        Assert.Contains(lines, l => l.StartsWith("Hook Notification do widget: em falta"));
        Assert.Contains(lines, l => l.StartsWith("disableAllHooks: ligado"));
        Assert.Contains(lines, l => l.StartsWith("apiKeyHelper: definido"));
    }

    [Fact]
    public void Settings_that_cannot_be_read()
    {
        Assert.StartsWith("Não se consegue ler", Assert.Single(Diagnostics.SettingsLines(Settings("{ \"statusLine\": "))));
        Assert.StartsWith("Não existe", Assert.Single(Diagnostics.SettingsLines(Path.Combine(_dir, "nowhere"))));
    }

    [Fact]
    public void A_statusline_of_ones_own()
    {
        Directory.CreateDirectory(_dir);
        var own = Path.Combine(_dir, "mine.ps1");
        File.WriteAllText(own, "# also writes ~/.claude/usage-widget/limits.json");
        var other = Path.Combine(_dir, "other.ps1");
        File.WriteAllText(other, "Write-Output 'hi'");

        Assert.StartsWith("um script próprio que também escreve para o widget", Diagnostics.StatusLineKind($"powershell -File \"{own}\"", _dir));
        Assert.StartsWith("outro, que não escreve para o widget", Diagnostics.StatusLineKind($"powershell -File \"{other}\"", _dir));
        Assert.StartsWith("outro", Diagnostics.StatusLineKind("npx ccstatusline", _dir));
    }

    // ---- the whole report --------------------------------------------------------

    [Fact]
    public async Task The_report_covers_each_part_and_names_no_session()
    {
        Diagnostics.VersionOf = _ => Task.FromResult("2.1.0 (Claude Code)");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sessions = new List<SessionInfo>
        {
            new() { Pid = 1, SessionId = "s1", Name = "Projeto secreto", Kind = "CLI" },
            new() { Pid = 2, SessionId = "s2", Name = "Outro projeto", Kind = "CLI" },
            new() { Pid = 3, SessionId = "s3", Name = "Conversa", Kind = "Desktop" },
        };
        var statusline = new Limits
        {
            UpdatedAt = now - 120,
            FiveHour = new LimitWindow { UsedPercentage = 22, ResetsAt = now + 3600 },
            SevenDay = new LimitWindow { UsedPercentage = 31, ResetsAt = now + 86400 },
        };
        var ping = new Limits { UpdatedAt = 0 };

        var text = await Diagnostics.BuildAsync(new Diagnostics.Live(
            new Config(), statusline, ping, false, (2, now + 300), "Ctrl+Alt+C (ativo)", sessions, 1));

        foreach (var section in new[] { "== Widget ==", "== Claude Code ==", "== settings.json ==", "== Leituras ==", "== Definições ==", "== Sessões ==", "== widget.log" })
            Assert.Contains(section, text);
        Assert.Contains("Statusline (limits.json): há 2 min", text);
        Assert.Contains("5h 22%", text);
        Assert.Contains("Último hello (ping.json): nenhum.", text);
        Assert.Contains("Hello: 2 falha(s) seguidas", text);
        Assert.Contains("Abertas: 3 (CLI 2, Desktop 1) · à tua espera: 1", text);
        Assert.Contains("Atalho: Ctrl+Alt+C (ativo)", text);
        Assert.DoesNotContain("Projeto secreto", text);
        Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), text, StringComparison.OrdinalIgnoreCase);
    }
}
