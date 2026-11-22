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
/// holds ClaudeUsageWidget.exe (with its dll and its own .NET in runtime\) and
/// ClaudeUsageFeed.exe, and running the first from there installs it all.
/// Nothing needs PowerShell. It puts the files in
/// %LOCALAPPDATA%\Programs\ClaudeUsageWidget (no admin rights), points
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
        var package = Path.GetDirectoryName(source)!;
        if (!File.Exists(Path.Combine(package, FeedName))) throw new FileNotFoundException($"Falta o {FeedName} ao lado do {ExeName}. Extrai o zip todo.");
        if (AutoHello.Candidates("claude").Count == 0)
            step("Aviso: Claude Code não encontrado. Instala-o e faz /login; sem ele o widget não recebe dados.");

        // 1. The files.
        var exe = Path.Combine(o.Dir, ExeName);
        var feed = Path.Combine(o.Dir, FeedName);
        Directory.CreateDirectory(o.Dir);
        CleanUpOld(o.Dir);
        StopWidget(exe);
        RemoveOldExtractions();
        if (!SamePath(package, o.Dir))
        {
            var (copied, kept) = CopyPackage(package, o.Dir);
            Log.Write($"Instalação: {copied} ficheiro(s) copiados, {kept} já estavam iguais.");
        }
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

        // 3. Start with Windows, and from the Start menu.
        if (o.AutoStart is { } on)
        {
            SetAutoStart(on ? exe : null);
            step(on ? "Arranque com o Windows: ligado." : "Arranque com o Windows: desligado.");
        }
        try
        {
            CreateShortcut(exe);
            step("Atalho no menu Iniciar: \"Claude Usage Widget\".");
        }
        catch (Exception ex) { Log.Write($"Atalho no menu Iniciar falhou: {ex.Message}"); }

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
        if (ShortcutTarget() is { } target && SamePath(target, Path.Combine(dir, ExeName)))
        {
            try { File.Delete(Shortcut); } catch { }
            step("Atalho no menu Iniciar: removido.");
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

    /// <summary>The folder next to the exe that holds the widget's own copy of .NET (see pack.ps1).</summary>
    public const string RuntimeDir = "runtime";

    /// <summary>
    /// What an install copies from the package, relative to it: the widget
    /// (exe, dll and their .json), the feed, and the .NET under runtime\.
    /// Named one by one: the package may sit among the user's own files.
    /// </summary>
    internal static List<string> PackageFiles(string package)
    {
        var app = Path.GetFileNameWithoutExtension(ExeName) + ".";
        var files = Directory.EnumerateFiles(package)
            .Select(Path.GetFileName)
            .Where(n => n!.StartsWith(app, StringComparison.OrdinalIgnoreCase) || n.Equals(FeedName, StringComparison.OrdinalIgnoreCase))
            .Select(n => n!)
            .ToList();
        var runtime = Path.Combine(package, RuntimeDir);
        if (Directory.Exists(runtime))
            files.AddRange(Directory.EnumerateFiles(runtime, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(package, f)));
        return files;
    }

    /// <summary>
    /// Copies the package into the install folder. A file already there with
    /// the same content is left alone: .NET does not change from one version
    /// of the widget to the next, nor does the feed until its code does (it
    /// builds to the same bytes, only newer), and every file written anew is
    /// one more for the antivirus to look at before it runs. Whatever runtime\
    /// holds that the package does not (an older .NET) goes.
    /// </summary>
    internal static (int Copied, int Kept) CopyPackage(string package, string dir)
    {
        var (copied, kept) = (0, 0);
        var files = PackageFiles(package);
        foreach (var rel in files)
        {
            var (from, to) = (Path.Combine(package, rel), Path.Combine(dir, rel));
            var (a, b) = (new FileInfo(from), new FileInfo(to));
            if (b.Exists && a.Length == b.Length && (a.LastWriteTimeUtc == b.LastWriteTimeUtc || SameContent(from, to)))
            {
                kept++;
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            CopyReplacing(from, to);
            Unblock(to);
            copied++;
        }

        var runtime = Path.Combine(dir, RuntimeDir);
        if (Directory.Exists(runtime))
        {
            var wanted = files.Select(f => Path.GetFullPath(Path.Combine(dir, f))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(runtime, "*", SearchOption.AllDirectories).ToList())
                if (!wanted.Contains(Path.GetFullPath(f)))
                    try { File.Delete(f); } catch { }
            foreach (var d in Directory.EnumerateDirectories(runtime, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
        }
        return (copied, kept);
    }

    private static bool SameContent(string a, string b)
    {
        try
        {
            using var x = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var y = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var (bx, by) = (new byte[81920], new byte[81920]);
            while (true)
            {
                var n = x.ReadAtLeast(bx, bx.Length, throwOnEndOfStream: false);
                if (y.ReadAtLeast(by, by.Length, throwOnEndOfStream: false) != n || !bx.AsSpan(0, n).SequenceEqual(by.AsSpan(0, n))) return false;
                if (n < bx.Length) return true;
            }
        }
        catch { return false; }
    }

    private static void CleanUpOld(string dir)
    {
        foreach (var old in Directory.EnumerateFiles(dir, "*.old-*", SearchOption.AllDirectories))
            try { File.Delete(old); } catch { }
    }

    /// <summary>
    /// Up to 1.8 the widget was a single exe, which unpacked part of .NET into
    /// %TEMP%\.net\ClaudeUsageWidget on each new version's first start and
    /// never removed it. Nothing uses those folders any more.
    /// </summary>
    private static void RemoveOldExtractions()
    {
        var root = new DirectoryInfo(Path.Combine(Path.GetTempPath(), ".net", Path.GetFileNameWithoutExtension(ExeName)));
        if (!root.Exists) return;
        foreach (var d in root.EnumerateDirectories())
            try { d.Delete(recursive: true); } catch { }
        try { root.Delete(); } catch { }
    }

    /// <summary>A file from a downloaded zip carries a "from the internet" mark; installed, it is ours.</summary>
    private static void Unblock(string file)
    {
        try { File.Delete(file + ":Zone.Identifier"); } catch { }
    }

    /// <summary>
    /// Stops every other copy of the widget, from any folder: one left running
    /// from an unpacked zip, or a hello stuck from an old install, keeps the
    /// single-instance lock, and the new widget would close itself on seeing it.
    /// </summary>
    private static void StopWidget(string exe)
    {
        foreach (var other in OtherCopies())
        {
            try
            {
                using var p = Process.GetProcessById(other.Pid);
                // That process only, never its tree: an update's installer is a
                // child of the widget it replaces, and would kill itself with it.
                p.Kill();
                p.WaitForExit(5000);
                Log.Write($"Instalação: fechada outra cópia do widget (pid {other.Pid}, {other.Path}).");
            }
            catch { }
        }
        // Gone for good before going on: a widget still closing keeps the
        // single-instance lock, and the new one would give way to it.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (OtherCopies().Count > 0 && DateTime.UtcNow < deadline) Thread.Sleep(200);
    }

    /// <summary>The other ClaudeUsageWidget processes running: their id, exe and start.</summary>
    public static List<(int Pid, string Path, DateTime Started)> OtherCopies()
    {
        var copies = new List<(int, string, DateTime)>();
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
        {
            using (p)
            {
                if (p.Id == Environment.ProcessId) continue;
                string path;
                DateTime started;
                try { path = p.MainModule?.FileName ?? "?"; } catch { path = "?"; }
                try { started = p.StartTime; } catch { started = default; }
                copies.Add((p.Id, path, started));
            }
        }
        return copies;
    }

    /// <summary>Whether this exe is the one an install put in place (not a package, not a build folder).</summary>
    public static bool IsInstalledCopy(string exe) =>
        File.Exists(Path.Combine(Path.GetDirectoryName(exe) ?? "", Marker));

    // ---- Start menu ------------------------------------------------------------------

    private static string Shortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Claude Usage Widget.lnk");

    /// <summary>The exe the Start menu entry opens, or null when there is none.</summary>
    private static string? ShortcutTarget()
    {
        if (!File.Exists(Shortcut)) return null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            try { return (string)shell.CreateShortcut(Shortcut).TargetPath; }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }
        catch { return null; }
    }

    /// <summary>A Start menu entry, to open the widget again after "Sair".</summary>
    private static void CreateShortcut(string exe)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell indisponível.");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            var link = shell.CreateShortcut(Shortcut);
            link.TargetPath = exe;
            link.WorkingDirectory = Path.GetDirectoryName(exe);
            link.Description = "Limites, sessões e avisos do Claude Code";
            link.Save();
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
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
