using System.Text.Json.Nodes;

namespace ClaudeUsageWidget.Tests;

/// <summary>Installing without PowerShell: Claude Code's settings and the files.</summary>
public class InstallerTests
{
    private const string Feed = @"C:\Users\Ana Silva\AppData\Local\Programs\ClaudeUsageWidget\ClaudeUsageFeed.exe";
    private const string Status = "\"" + Feed + "\" statusline";
    private const string Notify = "\"" + Feed + "\" notify";
    private const string OldFeed = "powershell -ExecutionPolicy Bypass -NoProfile -File \"C:\\Users\\Ana Silva\\.claude\\usage-widget\\feed.ps1\"";
    private const string OldNotify = "powershell -ExecutionPolicy Bypass -NoProfile -File \"C:\\Users\\Ana Silva\\.claude\\usage-widget\\notify.ps1\"";

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    private static string? StatusCommand(JsonObject s) => s["statusLine"]?["command"]?.GetValue<string>();

    private static List<string> NotifyCommands(JsonObject s) =>
        (s["hooks"]?["Notification"] as JsonArray ?? [])
            .SelectMany(e => e!["hooks"]!.AsArray().Select(h => h!["command"]!.GetValue<string>()))
            .ToList();

    // ---- wiring ------------------------------------------------------------------------------

    [Fact]
    public void A_fresh_Claude_Code_gets_the_statusline_and_the_hook()
    {
        var s = new JsonObject();
        var r = Installer.Wire(s, Feed, alreadyWrapping: false);
        Assert.True(r.Changed);
        Assert.Null(r.WrapCommand);
        Assert.Equal(Status, StatusCommand(s));
        Assert.Equal("command", s["statusLine"]!["type"]!.GetValue<string>());
        Assert.Equal([Notify], NotifyCommands(s));
        Assert.Contains("Statusline: instalado.", r.Notes);
    }

    [Fact]
    public void The_PowerShell_scripts_are_replaced_by_the_feed()
    {
        var s = Parse($$"""
            {
              "statusLine": { "type": "command", "command": {{JsonValue.Create(OldFeed).ToJsonString()}} },
              "hooks": { "Notification": [ { "hooks": [ { "type": "command", "command": {{JsonValue.Create(OldNotify).ToJsonString()}} } ] } ] }
            }
            """);
        var r = Installer.Wire(s, Feed, alreadyWrapping: true);
        Assert.True(r.Changed);
        Assert.Null(r.WrapCommand);   // wrapped-command.txt stays as it was
        Assert.Equal(Status, StatusCommand(s));
        Assert.Equal([Notify], NotifyCommands(s));
        Assert.Contains(r.Notes, n => n.StartsWith("Statusline: passa do PowerShell") && n.Contains("continua a ser chamado"));
    }

    [Fact]
    public void Someone_else_s_statusline_is_kept_and_called_and_other_hooks_left_alone()
    {
        var s = Parse("""
            {
              "theme": "dark",
              "statusLine": { "type": "command", "command": "npx ccstatusline", "padding": 0 },
              "hooks": {
                "Notification": [ { "matcher": "", "hooks": [ { "type": "command", "command": "say-it.cmd" } ] } ],
                "Stop": [ { "hooks": [ { "type": "command", "command": "beep.cmd" } ] } ]
              }
            }
            """);
        var r = Installer.Wire(s, Feed, alreadyWrapping: false);
        Assert.Equal("npx ccstatusline", r.WrapCommand);
        Assert.Equal(Status, StatusCommand(s));
        Assert.Equal(0, s["statusLine"]!["padding"]!.GetValue<int>());
        Assert.Equal(["say-it.cmd", Notify], NotifyCommands(s));
        Assert.Equal("beep.cmd", s["hooks"]!["Stop"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal("dark", s["theme"]!.GetValue<string>());
    }

    [Fact]
    public void Installing_again_changes_nothing()
    {
        var s = new JsonObject();
        Installer.Wire(s, Feed, false);
        var before = s.ToJsonString();
        var r = Installer.Wire(s, Feed, false);
        Assert.False(r.Changed);
        Assert.Equal(before, s.ToJsonString());
    }

    [Fact]
    public void Another_install_folder_moves_both_commands()
    {
        var s = new JsonObject();
        Installer.Wire(s, @"D:\Old\ClaudeUsageFeed.exe", false);
        var r = Installer.Wire(s, Feed, false);
        Assert.True(r.Changed);
        Assert.Equal(Status, StatusCommand(s));
        Assert.Equal([Notify], NotifyCommands(s));
    }

    [Fact]
    public void A_statusline_of_ones_own_that_feeds_the_widget_is_left_alone()
    {
        var script = Path.Combine(Isolation.Root, $"mine-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(script, "# writes ~/.claude/usage-widget/limits.json");
        var command = $"powershell -File \"{script}\"";
        var s = new JsonObject { ["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = command } };
        var r = Installer.Wire(s, Feed, false);
        Assert.Null(r.WrapCommand);
        Assert.Equal(command, StatusCommand(s));
        Assert.Equal([Notify], NotifyCommands(s));   // the hook goes in anyway
    }

    // ---- unwiring ----------------------------------------------------------------------------

    [Fact]
    public void Uninstalling_puts_the_earlier_statusline_back_and_keeps_other_hooks()
    {
        var s = Parse("""{ "statusLine": { "type": "command", "command": "npx ccstatusline" }, "hooks": { "Notification": [ { "hooks": [ { "type": "command", "command": "say-it.cmd" } ] } ] } }""");
        Installer.Wire(s, Feed, false);
        var (changed, _) = Installer.Unwire(s, "npx ccstatusline");
        Assert.True(changed);
        Assert.Equal("npx ccstatusline", StatusCommand(s));
        Assert.Equal(["say-it.cmd"], NotifyCommands(s));
    }

    [Fact]
    public void Uninstalling_with_nothing_before_leaves_no_trace()
    {
        var s = Parse("""{ "theme": "dark" }""");
        Installer.Wire(s, Feed, false);
        Installer.Unwire(s, null);
        Assert.Equal("""{"theme":"dark"}""", s.ToJsonString());
    }

    [Fact]
    public void Uninstalling_also_clears_the_old_PowerShell_commands()
    {
        var s = Parse($$"""{ "statusLine": { "command": {{JsonValue.Create(OldFeed).ToJsonString()}} }, "hooks": { "Notification": [ { "hooks": [ { "command": {{JsonValue.Create(OldNotify).ToJsonString()}} } ] } ] } }""");
        Installer.Unwire(s, null);
        Assert.Equal("{}", s.ToJsonString());
    }

    [Theory]
    [InlineData("", "None")]
    [InlineData(Status, "Feed")]
    [InlineData(OldFeed, "OldScript")]
    [InlineData("npx ccstatusline", "Other")]
    public void Whose_statusline_it_is(string command, string owner)
    {
        Assert.Equal(owner, Installer.OwnerOf(command).ToString());
    }

    // ---- settings.json on disk -----------------------------------------------------------------

    [Fact]
    public void Settings_with_comments_are_read_and_saved_with_a_backup_and_their_accents()
    {
        var path = Path.Combine(Isolation.Root, "settings.json");
        File.WriteAllText(path, """
            {
              // Claude Code accepts comments
              "theme": "Ação ✳",
              "model": "opus",
            }
            """);
        try
        {
            var s = Installer.ReadSettings();
            Installer.Wire(s, Feed, false);
            var backup = Installer.SaveSettings(s);
            Assert.True(File.Exists(backup));
            var saved = File.ReadAllText(path);
            Assert.Contains("\"Ação ✳\"", saved);        // not \u00E7
            Assert.Contains("ClaudeUsageFeed.exe", saved);
            File.Delete(backup!);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_settings_file_it_cannot_read_is_never_touched()
    {
        var path = Path.Combine(Isolation.Root, "settings.json");
        File.WriteAllText(path, "{ not json");
        try
        {
            var e = Assert.Throws<InvalidDataException>(() => Installer.ReadSettings());
            Assert.Contains("não foi alterado", e.Message);
            Assert.Equal("{ not json", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    // ---- files -------------------------------------------------------------------------------------

    [Fact]
    public void An_exe_in_use_steps_aside_for_the_new_one()
    {
        var dir = Path.Combine(Isolation.Root, "copy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "new.exe");
        var target = Path.Combine(dir, "ClaudeUsageFeed.exe");
        File.WriteAllText(source, "new");
        File.WriteAllText(target, "old");
        // A running exe can be renamed but not overwritten; so can a file open like this.
        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            Installer.CopyReplacing(source, target);
        Assert.Equal("new", File.ReadAllText(target));
        Assert.Single(Directory.GetFiles(dir, "ClaudeUsageFeed.exe.old-*"));
    }

    private static readonly DateTime Built = new(2026, 11, 22, 21, 0, 0, DateTimeKind.Utc);

    /// <summary>A package as pack.ps1 lays it out, unpacked next to some of the user's own files.</summary>
    private static string Package(string dotnet = "10.0.15", string dll = "widget 1")
    {
        var dir = Path.Combine(Isolation.Root, "package-" + Guid.NewGuid().ToString("N"));
        void Put(string rel, string text, DateTime? at = null)
        {
            var path = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            File.SetLastWriteTimeUtc(path, at ?? Built);
        }
        Put("ClaudeUsageWidget.exe", "apphost");
        Put("ClaudeUsageWidget.dll", dll, dll == "widget 1" ? Built : Built.AddDays(1));
        Put("ClaudeUsageWidget.deps.json", "{}");
        Put("ClaudeUsageWidget.runtimeconfig.json", "{}");
        Put("ClaudeUsageFeed.exe", "feed");
        Put("LEIA-ME.txt", "leia");
        Put("install.ps1", "# wrapper");
        Put("fotos-ferias.zip", "not ours");
        Put($@"runtime\host\fxr\{dotnet}\hostfxr.dll", "fxr");
        Put($@"runtime\shared\Microsoft.NETCore.App\{dotnet}\coreclr.dll", "clr");
        Put($@"runtime\shared\Microsoft.WindowsDesktop.App\{dotnet}\wpfgfx_cor3.dll", "wpf");
        return dir;
    }

    [Fact]
    public void Package_files_are_the_widget_the_feed_and_its_dotnet()
    {
        var files = Installer.PackageFiles(Package()).Select(f => f.Replace('\\', '/')).Order().ToList();
        Assert.Equal(
        [
            "ClaudeUsageFeed.exe", "ClaudeUsageWidget.deps.json", "ClaudeUsageWidget.dll", "ClaudeUsageWidget.exe",
            "ClaudeUsageWidget.runtimeconfig.json",
            "runtime/host/fxr/10.0.15/hostfxr.dll",
            "runtime/shared/Microsoft.NETCore.App/10.0.15/coreclr.dll",
            "runtime/shared/Microsoft.WindowsDesktop.App/10.0.15/wpfgfx_cor3.dll",
        ], files);
    }

    [Fact]
    public void An_update_writes_only_what_changed()
    {
        var installed = Path.Combine(Isolation.Root, "installed-" + Guid.NewGuid().ToString("N"));

        Assert.Equal((8, 0), Installer.CopyPackage(Package(), installed));
        Assert.Equal("clr", File.ReadAllText(Path.Combine(installed, @"runtime\shared\Microsoft.NETCore.App\10.0.15\coreclr.dll")));
        Assert.False(File.Exists(Path.Combine(installed, "fotos-ferias.zip")));
        Assert.False(File.Exists(Path.Combine(installed, "LEIA-ME.txt")));

        // The same package again: nothing to write.
        Assert.Equal((0, 8), Installer.CopyPackage(Package(), installed));

        // A new version of the widget, on the same .NET: only its dll.
        Assert.Equal((1, 7), Installer.CopyPackage(Package(dll: "widget 2"), installed));
        Assert.Equal("widget 2", File.ReadAllText(Path.Combine(installed, "ClaudeUsageWidget.dll")));
    }

    [Fact]
    public void A_newer_dotnet_replaces_the_old_one()
    {
        var installed = Path.Combine(Isolation.Root, "installed-" + Guid.NewGuid().ToString("N"));
        Installer.CopyPackage(Package(), installed);
        File.WriteAllText(Path.Combine(installed, "installed.txt"), "marker");

        Installer.CopyPackage(Package(dotnet: "10.0.16"), installed);

        var runtime = Path.Combine(installed, "runtime");
        Assert.Equal(3, Directory.GetFiles(runtime, "*", SearchOption.AllDirectories).Length);
        Assert.Empty(Directory.GetDirectories(runtime, "10.0.15", SearchOption.AllDirectories));
        Assert.True(File.Exists(Path.Combine(runtime, @"host\fxr\10.0.16\hostfxr.dll")));
        // Outside runtime\, nothing of the install's own goes.
        Assert.True(File.Exists(Path.Combine(installed, "installed.txt")));
    }
}
