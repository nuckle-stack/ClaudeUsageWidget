using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace ClaudeUsageWidget;

/// <summary>What an install does beyond copying the files.</summary>
public record InstallOptions(string Dir, bool? AutoStart, bool Check, bool Launch);

/// <summary>How the install went: whether the limits can be read, and what to show about it.</summary>
public record InstallResult(bool? LimitsOk, string Detail, string? DiagnosticsPath);

/// <summary>
/// Installs, updates and removes the widget, from the widget itself: the zip
/// holds ClaudeUsageWidget.exe and ClaudeUsageFeed.exe, and running the
/// first from there installs both. Nothing needs PowerShell. It puts the exes
/// in %LOCALAPPDATA%\Programs\ClaudeUsageWidget (no admin rights), points
/// Claude Code's statusline and Notification hook at the feed (keeping a
/// statusline that was already there: the feed calls it), and can start the
/// widget with Windows. settings.json is backed up before every change.
/// </summary>
public static class Installer
{
    public const string ExeName = "ClaudeUsageWidget.exe";
    public const string FeedName = "ClaudeUsageFeed.exe";
    /// <summary>Written in the install folder: an exe next to it is the installed one, not a package to install.</summary>
    private const string Marker = "installed.txt";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "ClaudeUsageWidget";
    private static readonly UTF8Encoding Utf8 = new(false);

    public static string DefaultDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ClaudeUsageWidget");

    private static string ClaudeDir => Path.GetDirectoryName(Paths.Dir)!;
    private static string SettingsPath => Path.Combine(ClaudeDir, "settings.json");
    private static string WrappedPath => Path.Combine(Paths.Dir, "wrapped-command.txt");

    /// <summary>
    /// Started from an unpacked zip (the feed next to it, no install marker):
    /// a double click there means "install".
    /// </summary>
    public static bool RunningFromPackage()
    {
        var dir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
        return dir.Length > 0 && File.Exists(Path.Combine(dir, FeedName)) && !File.Exists(Path.Combine(dir, Marker));
    }

    /// <summary>The version installed in dir ("1.7.0"), or null when none is.</summary>
    public static string? InstalledVersion(string dir)
    {
        var exe = Path.Combine(dir, ExeName);
        if (!File.Exists(exe)) return null;
        var v = FileVersionInfo.GetVersionInfo(exe).ProductVersion ?? "";
        var plus = v.IndexOf('+');
        return plus > 0 ? v[..plus] : v.Length > 0 ? v : "?";
    }

    /// <summary>Whether Windows starts this exe at sign-in.</summary>
    private static bool AutoStartsFrom(string exe)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunName) is string value && SamePath(value.Trim('"', ' '), exe);
        }
        catch { return false; }
    }

    public static bool AutoStarts()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunName) is string;
        }
        catch { return false; }
    }

    // ---- install -----------------------------------------------------------------

    public static async Task<InstallResult> InstallAsync(InstallOptions o, Action<string> step)
    {
        var source = Environment.ProcessPath ?? throw new InvalidOperationException("Sem caminho para o executável.");
        var sourceFeed = Path.Combine(Path.GetDirectoryName(source)!, FeedName);
        if (!File.Exists(sourceFeed)) throw new FileNotFoundException($"Falta o {FeedName} ao lado do {ExeName}. Extrai o zip todo.");
        if (AutoHello.Candidates("claude").Count == 0)
            step("Aviso: Claude Code não encontrado. Instala-o e faz /login; sem ele o widget não recebe dados.");

        // 1. The exes.
        var exe = Path.Combine(o.Dir, ExeName);
        var feed = Path.Combine(o.Dir, FeedName);
        Directory.CreateDirectory(o.Dir);
        CleanUpOld(o.Dir);
        StopWidget(exe);
        if (!SamePath(source, exe)) CopyReplacing(source, exe);
        if (!SamePath(sourceFeed, feed)) CopyReplacing(sourceFeed, feed);
        foreach (var f in new[] { exe, feed }) Unblock(f);
        File.WriteAllText(Path.Combine(o.Dir, Marker), "Instalado pelo Claude Usage Widget. Este ficheiro marca a pasta onde ele está instalado.\n");
        step($"Widget: {exe}");

        // 2. Claude Code's statusline and Notification hook.
        Directory.CreateDirectory(Paths.Dir);
        var settings = ReadSettings();
        var wire = Wire(settings, feed, File.Exists(WrappedPath));
        if (wire.WrapCommand is { } wrapped) File.WriteAllText(WrappedPath, wrapped, Utf8);
        if (wire.Changed && SaveSettings(settings) is { } backup) step($"Cópia de segurança: {backup}");
        foreach (var note in wire.Notes) step(note);
        // The PowerShell scripts the feed replaces.
        foreach (var old in new[] { "feed.ps1", "notify.ps1" })
            try { File.Delete(Path.Combine(Paths.Dir, old)); } catch { }

        // 3. Start with Windows.
        if (o.AutoStart is { } on)
        {
            SetAutoStart(on ? exe : null);
            step(on ? "Arranque com o Windows: ligado." : "Arranque com o Windows: desligado.");
        }

        // 4. Can the widget read the limits? One short message through any
        //    Claude Code it finds; before the launch, so the widget starts with it.
        InstallResult result = new(null, "", null);
        if (o.Check)
        {
            step("A testar a leitura dos limites (um \"hello\" curto no Haiku; até 90 s)…");
            var hello = new AutoHello();
            var ok = await hello.SendAsync(Json.Load<Config>(Paths.Config));
            string? report = null;
            if (!ok)
            {
                // A report for whoever handed over the widget, so they can see why.
                try
                {
                    var text = await Diagnostics.BuildAsync(new Diagnostics.Live(Json.Load<Config>(Paths.Config), Json.Load<Limits>(Paths.Limits),
                        hello.Ping, false, hello.Backoff, "(a instalar)", SessionScanner.Scan(), -1));
                    File.WriteAllText(Diagnostics.ReportPath, text);
                    report = Diagnostics.ReportPath;
                }
                catch (Exception ex) { Log.Write($"Diagnóstico na instalação falhou: {ex.Message}"); }
            }
            result = new(ok, hello.LastResult, report);
            step(ok ? $"Limites: OK. {hello.LastResult}" : $"Limites: não deu. {hello.LastResult}");
        }

        // 5. Go. From the install folder, so this one (the zip's) is not kept in use.
        if (o.Launch)
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = o.Dir });
            step("Widget iniciado (no fundo do ecrã, à direita).");
        }
        return result;
    }

    // ---- uninstall ---------------------------------------------------------------

    /// <summary>
    /// Puts Claude Code's settings back and stops starting with Windows. The
    /// files go with RemoveFilesAfterExit, the caller's last act.
    /// </summary>
    public static void Uninstall(string dir, Action<string> step)
    {
        StopWidget(Path.Combine(dir, ExeName));
        if (File.Exists(SettingsPath))
        {
            try
            {
                var settings = ReadSettings();
                var wrapped = File.Exists(WrappedPath) ? File.ReadAllText(WrappedPath).Trim() : null;
                var (changed, notes) = Unwire(settings, wrapped);
                if (changed && SaveSettings(settings) is { } backup) step($"Cópia de segurança: {backup}");
                foreach (var note in notes) step(note);
            }
            catch (InvalidDataException ex) { step(ex.Message); }
        }
        // Only this install's entry: another folder's widget keeps starting.
        if (AutoStartsFrom(Path.Combine(dir, ExeName)))
        {
            SetAutoStart(null);
            step("Arranque com o Windows: desligado.");
        }
        step("O widget e os dados dele são apagados ao fechar esta janela.");
    }

    /// <summary>
    /// Deletes the program folder and the widget's data a few seconds after
    /// this process exits: it may be running from that folder, and it logs to
    /// the other. Call it last, right before exiting.
    /// </summary>
    public static void RemoveFilesAfterExit(string dir)
    {
        var folders = new[] { dir, Paths.Dir }.Where(Directory.Exists).ToList();
        if (folders.Count == 0) return;
        var command = "ping -n 4 127.0.0.1 >nul & " + string.Join(" & ", folders.Select(f => $"rmdir /s /q \"{f}\""));
        Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c \"{command}\"") { UseShellExecute = false, CreateNoWindow = true });
    }

    // ---- Claude Code's settings ------------------------------------------------------

    /// <summary>Who the statusline command belongs to.</summary>
    internal enum Owner { None, Feed, OldScript, OwnScript, Other }

    internal static Owner OwnerOf(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return Owner.None;
        if (command.Contains(FeedName, StringComparison.OrdinalIgnoreCase)) return Owner.Feed;
        if (command.Contains("usage-widget", StringComparison.OrdinalIgnoreCase) &&
            command.Contains("feed.ps1", StringComparison.OrdinalIgnoreCase)) return Owner.OldScript;
        // A statusline of the user's own that already writes the widget's files.
        return FormatWatch.FeedOfCommand(command) == FeedKind.Own ? Owner.OwnScript : Owner.Other;
    }

    internal record WireResult(bool Changed, List<string> Notes, string? WrapCommand);

    /// <summary>
    /// Points the statusline and the Notification hook at the feed. A
    /// statusline of someone else's is kept: it goes to wrapped-command.txt
    /// (WrapCommand) and the feed hands over to it. The widget's own old
    /// PowerShell commands are replaced; other hooks are left alone.
    /// </summary>
    internal static WireResult Wire(JsonObject settings, string feedExe, bool alreadyWrapping)
    {
        var notes = new List<string>();
        var changed = false;
        string? wrap = null;
        var statusCommand = $"\"{feedExe}\" statusline";

        var statusLine = settings["statusLine"] as JsonObject;
        var current = Text(statusLine?["command"]);
        switch (OwnerOf(current))
        {
            case Owner.Feed:
                // The same feed, maybe from another folder (a custom install dir).
                if (current != statusCommand)
                {
                    statusLine!["command"] = statusCommand;
                    changed = true;
                }
                notes.Add("Statusline: já ligado ao widget.");
                break;
            case Owner.OldScript:
                statusLine!["type"] = "command";
                statusLine["command"] = statusCommand;
                changed = true;
                notes.Add("Statusline: passa do PowerShell para o ClaudeUsageFeed.exe" +
                          (alreadyWrapping ? "; o statusline que já havia continua a ser chamado." : "."));
                break;
            case Owner.OwnScript:
                notes.Add("Statusline: o script existente já alimenta o widget; não foi alterado.");
                break;
            case Owner.Other:
                wrap = current;
                statusLine!["type"] = "command";
                statusLine["command"] = statusCommand;
                changed = true;
                notes.Add("Statusline: o que já havia foi mantido e passa a ser chamado pelo do widget.");
                break;
            default:
                if (statusLine is null) settings["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = statusCommand };
                else
                {
                    statusLine["type"] = "command";
                    statusLine["command"] = statusCommand;
                }
                changed = true;
                notes.Add("Statusline: instalado.");
                break;
        }

        var notifyCommand = $"\"{feedExe}\" notify";
        if (settings["hooks"] is not JsonObject hooks) settings["hooks"] = hooks = new JsonObject();
        if (hooks["Notification"] is not JsonArray entries) hooks["Notification"] = entries = new JsonArray();
        var ours = entries.Where(IsOurHook).ToList();
        if (ours.Count == 1 && Commands(ours[0]).SequenceEqual([notifyCommand]))
            notes.Add("Aviso \"precisa de ti\": já instalado.");
        else
        {
            foreach (var entry in ours) entries.Remove(entry);
            entries.Add(new JsonObject { ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = notifyCommand }) });
            changed = true;
            notes.Add(ours.Count > 0
                ? "Aviso \"precisa de ti\": passa do PowerShell para o ClaudeUsageFeed.exe."
                : "Aviso \"precisa de ti\": instalado (vale para as sessões abertas a partir de agora).");
        }
        return new WireResult(changed, notes, wrap);
    }

    /// <summary>The widget's statusline and hook out; the statusline that was there before back in.</summary>
    internal static (bool Changed, List<string> Notes) Unwire(JsonObject settings, string? wrapped)
    {
        var notes = new List<string>();
        var changed = false;
        if (settings["statusLine"] is JsonObject statusLine && OwnerOf(Text(statusLine["command"])) is Owner.Feed or Owner.OldScript)
        {
            if (wrapped is { Length: > 0 })
            {
                statusLine["command"] = wrapped;
                notes.Add("Statusline anterior reposto.");
            }
            else
            {
                settings.Remove("statusLine");
                notes.Add("Statusline do widget removido.");
            }
            changed = true;
        }
        if (settings["hooks"] is JsonObject hooks && hooks["Notification"] is JsonArray entries)
        {
            var ours = entries.Where(IsOurHook).ToList();
            foreach (var entry in ours) entries.Remove(entry);
            if (ours.Count > 0)
            {
                if (entries.Count == 0) hooks.Remove("Notification");
                if (hooks.Count == 0) settings.Remove("hooks");
                notes.Add("Aviso \"precisa de ti\" removido.");
                changed = true;
            }
        }
        return (changed, notes);
    }

    /// <summary>A Notification entry whose commands are the widget's: the feed, or the old notify.ps1.</summary>
    private static bool IsOurHook(JsonNode? entry) => Commands(entry).Any(c =>
        c.Contains(FeedName, StringComparison.OrdinalIgnoreCase) ||
        (c.Contains("usage-widget", StringComparison.OrdinalIgnoreCase) && c.Contains("notify.ps1", StringComparison.OrdinalIgnoreCase)));

    private static IEnumerable<string> Commands(JsonNode? entry) =>
        entry is JsonObject o && o["hooks"] is JsonArray list
            ? list.Select(h => h is JsonObject ho ? Text(ho["command"]) : "").Where(c => c.Length > 0)
            : [];

    private static string Text(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    /// <summary>Claude Code's settings.json; empty when there is none. Refuses to touch one it cannot read.</summary>
    internal static JsonObject ReadSettings()
    {
        if (!File.Exists(SettingsPath)) return new JsonObject();
        var raw = File.ReadAllText(SettingsPath);
        if (string.IsNullOrWhiteSpace(raw)) return new JsonObject();
        try
        {
            return JsonNode.Parse(raw, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                as JsonObject ?? throw new InvalidDataException();
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new InvalidDataException($"O settings.json não é JSON válido e não foi alterado: {SettingsPath}");
        }
    }

    /// <summary>Saves settings.json, after a dated copy of the one there; returns the copy's path.</summary>
    internal static string? SaveSettings(JsonObject settings)
    {
        string? backup = null;
        if (File.Exists(SettingsPath))
        {
            backup = $"{SettingsPath}.bak-usage-widget-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(SettingsPath, backup, overwrite: true);
        }
        else Directory.CreateDirectory(ClaudeDir);
        var json = settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        File.WriteAllText(SettingsPath, json, Utf8);
        return backup;
    }

    // ---- files and processes ------------------------------------------------------------

    /// <summary>
    /// Copies over a file that may be running: Claude Code starts the feed on
    /// every statusline refresh. Windows will not overwrite an exe in use but
    /// will rename it, so the old one steps aside and goes on a later install.
    /// </summary>
    internal static void CopyReplacing(string source, string target)
    {
        try
        {
            File.Copy(source, target, overwrite: true);
        }
        catch (IOException)
        {
            File.Move(target, $"{target}.old-{DateTime.UtcNow.Ticks}");
            File.Copy(source, target);
        }
    }

    private static void CleanUpOld(string dir)
    {
        foreach (var old in Directory.EnumerateFiles(dir, "*.old-*"))
            try { File.Delete(old); } catch { }
    }

    /// <summary>A file from a downloaded zip carries a "from the internet" mark; installed, it is ours.</summary>
    private static void Unblock(string file)
    {
        try { File.Delete(file + ":Zone.Identifier"); } catch { }
    }

    /// <summary>Stops the widget running from that exe (not this process), so it can be replaced.</summary>
    private static void StopWidget(string exe)
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
        {
            try
            {
                if (p.Id == Environment.ProcessId || !SamePath(p.MainModule?.FileName ?? "", exe)) continue;
                p.Kill();
                p.WaitForExit(5000);
            }
            catch { }
            finally { p.Dispose(); }
        }
    }

    private static void SetAutoStart(string? exe)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (exe is not null) key.SetValue(RunName, $"\"{exe}\"");
            else key.DeleteValue(RunName, false);
        }
        catch (Exception ex) { Log.Write($"Falha a alterar o arranque automático: {ex.Message}"); }
    }

    private static bool SamePath(string a, string b) =>
        a.Length > 0 && b.Length > 0 &&
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
