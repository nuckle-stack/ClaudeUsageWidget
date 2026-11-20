using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClaudeUsageWidget;

/// <summary>
/// Brings the window that hosts a Claude Code session to the front. The
/// session process itself has no window: it runs inside a terminal or under
/// the desktop app, so we walk up its parents until one owns a window.
/// </summary>
public static class WindowFocus
{
    // Reaching one of these means we walked past the session's own host.
    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
        { "explorer", "sihost", "svchost", "services", "wininit", "winlogon", "ClaudeUsageWidget" };

    /// <summary>Returns the name of the process whose window was focused, or null if none was found.</summary>
    public static string? Focus(int pid)
    {
        foreach (var p in Ancestors(pid))
        {
            using (p)
            {
                var hwnd = p.MainWindowHandle;
                if (hwnd == IntPtr.Zero) continue;
                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
                return p.ProcessName;
            }
        }
        return null;
    }

    /// <summary>
    /// Whether the session is on screen in front of the user: the foreground
    /// window belongs to the process tree hosting it and shows its title.
    /// The process alone is not enough: one Windows Terminal process holds
    /// every window and tab, and only the active tab's title reaches the window
    /// caption. Without a title to match (or in the desktop app, whose caption
    /// is just "Claude") the answer is no, so an alert is never lost.
    /// </summary>
    public static bool IsForeground(int pid, string title)
    {
        if (title.Length == 0) return false;
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        GetWindowThreadProcessId(fg, out var fgPid);

        var hosted = false;
        foreach (var p in Ancestors(pid))
            using (p)
                if (p.Id == fgPid) hosted = true;
        if (!hosted) return false;

        var caption = new System.Text.StringBuilder(512);
        GetWindowText(fg, caption, caption.Capacity);
        // Claude Code puts a status glyph ("✳", a spinner) before the title. Compare
        // the rest whole: one title is often the start of another
        // ("Estado do projeto" / "Estado do projeto claudewidget").
        var text = caption.ToString();
        var start = 0;
        while (start < text.Length && !char.IsLetterOrDigit(text[start])) start++;
        text = text[start..].Trim();
        return string.Equals(text, title.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The process and its parents, nearest first, up to the session's host. Callers dispose them.</summary>
    private static IEnumerable<Process> Ancestors(int pid)
    {
        var seen = new HashSet<int>();
        DateTime? childStart = null;
        for (var id = pid; id > 4 && seen.Add(id);)
        {
            Process p;
            try
            {
                p = Process.GetProcessById(id);
                // A "parent" that started after its child is a reused pid, not a parent.
                if (Stop.Contains(p.ProcessName) || (childStart is { } cs && p.StartTime > cs))
                {
                    p.Dispose();
                    yield break;
                }
                childStart = p.StartTime;
                id = ParentOf(p);
            }
            catch { yield break; }
            yield return p;
        }
    }

    private static int ParentOf(Process p)
    {
        var info = new ProcessBasicInformation();
        return NtQueryInformationProcess(p.Handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0
            ? (int)info.InheritedFromUniqueProcessId
            : 0;
    }

    private const int SW_RESTORE = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr process, int informationClass, ref ProcessBasicInformation info, int length, out int returned);

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);
}
