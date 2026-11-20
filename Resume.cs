using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace ClaudeUsageWidget;

/// <summary>
/// Picks a past session up where it stopped: a new terminal in the folder
/// it started in, running "claude --resume &lt;id&gt;". Claude Code finds the
/// conversation by that folder, so the folder matters. A tab in Windows
/// Terminal when there is one (the way most people run Claude Code), a plain
/// console otherwise.
/// </summary>
public static class Resume
{
    /// <summary>Starts it; on failure, says why (in Portuguese, for the popup).</summary>
    public static (bool Ok, string Detail) Open(string sessionId, string cwd, string configuredClaude)
    {
        if (!Regex.IsMatch(sessionId, "^[A-Za-z0-9-]{8,64}$")) return (false, "Sessão sem identificador válido.");
        // The terminal CLI first; a copy bundled with VS Code or the desktop app resumes just as well.
        if (AutoHello.Candidates(configuredClaude).Select(c => c.Path).FirstOrDefault() is not { } claude)
            return (false, "Claude Code não encontrado neste PC.");

        var dir = Directory.Exists(cwd) ? cwd : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        try
        {
            ProcessStartInfo psi;
            if (File.Exists(wt))
            {
                psi = new ProcessStartInfo(wt) { UseShellExecute = false };
                foreach (var arg in TerminalArguments(dir, claude, sessionId)) psi.ArgumentList.Add(arg);
            }
            else
            {
                // A GUI process starting a console program without a shell gets it a console of its own.
                psi = new ProcessStartInfo("cmd.exe", ConsoleArguments(claude, sessionId)) { UseShellExecute = false, WorkingDirectory = dir };
            }
            psi.WorkingDirectory = dir;
            // A widget started from inside a Claude Code session must not pass that on.
            foreach (var key in psi.Environment.Keys.ToList())
                if (key.StartsWith("CLAUDECODE", StringComparison.OrdinalIgnoreCase) ||
                    key.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase))
                    psi.Environment.Remove(key);
            Process.Start(psi);
            return (true, File.Exists(wt) ? "Windows Terminal" : "consola");
        }
        catch (Exception ex) { return (false, $"Não deu para abrir o terminal: {ex.Message}"); }
    }

    /// <summary>
    /// A new tab in the open Windows Terminal window (or a new window if none):
    /// wt -w 0 new-tab -d &lt;dir&gt; &lt;claude&gt; --resume &lt;id&gt;.
    /// </summary>
    internal static string[] TerminalArguments(string dir, string claude, string sessionId) =>
        ["-w", "0", "new-tab", "-d", SafeDir(dir), claude, "--resume", sessionId];

    /// <summary>cmd /k ""claude" --resume id": cmd drops the outer quotes, the path keeps its own.</summary>
    internal static string ConsoleArguments(string claude, string sessionId) =>
        $"/k \"\"{claude}\" --resume {sessionId}\"";

    /// <summary>
    /// A folder as an argument: "D:\" would end in \" once quoted, which
    /// reads as an escaped quote; a trailing backslash goes, and a bare drive
    /// becomes "D:\.".
    /// </summary>
    internal static string SafeDir(string dir)
    {
        var trimmed = dir.TrimEnd('\\', '/');
        return trimmed.EndsWith(':') ? trimmed + @"\." : trimmed;
    }
}
