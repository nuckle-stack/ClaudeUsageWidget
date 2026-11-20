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

    /// <summary>
    /// Brings the session's window to the front; returns the host process name,
    /// or null if none was found. One Windows Terminal process owns every
    /// terminal window, so its "main window" is the same whichever session was
    /// asked for: the right one is picked by its caption (the active tab's
    /// title) or, for a tab in the background, by selecting that tab.
    /// </summary>
    public static string? Focus(int pid, string title)
    {
        foreach (var p in Ancestors(pid))
        {
            using (p)
            {
                var windows = WindowsOf(p.Id);
                if (windows.Count == 0) continue;
                var hwnd = windows.FirstOrDefault(w => SameTitle(Caption(w), title));
                if (hwnd == IntPtr.Zero) hwnd = SelectTab(windows, title);
                if (hwnd == IntPtr.Zero) hwnd = p.MainWindowHandle != IntPtr.Zero ? p.MainWindowHandle : windows[0];
                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
                return p.ProcessName;
            }
        }
        return null;
    }

    /// <summary>The visible top-level windows of a process, with a caption.</summary>
    private static List<IntPtr> WindowsOf(int pid)
    {
        var result = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == pid && IsWindowVisible(hwnd) && GetWindow(hwnd, GW_OWNER) == IntPtr.Zero &&
                Caption(hwnd).Length > 0)
                result.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>
    /// Finds the tab with this title in one of the windows and selects it,
    /// through UI Automation (Windows Terminal exposes its tabs that way).
    /// Returns the window that holds it.
    /// </summary>
    private static IntPtr SelectTab(List<IntPtr> windows, string title)
    {
        if (title.Trim().Length == 0) return IntPtr.Zero;
        var isTab = new System.Windows.Automation.PropertyCondition(
            System.Windows.Automation.AutomationElement.ControlTypeProperty,
            System.Windows.Automation.ControlType.TabItem);
        foreach (var w in windows)
        {
            try
            {
                var tabs = System.Windows.Automation.AutomationElement.FromHandle(w)
                    .FindAll(System.Windows.Automation.TreeScope.Descendants, isTab);
                foreach (System.Windows.Automation.AutomationElement tab in tabs)
                {
                    if (!SameTitle(tab.Current.Name, title) ||
                        !tab.TryGetCurrentPattern(System.Windows.Automation.SelectionItemPattern.Pattern, out var pattern))
                        continue;
                    ((System.Windows.Automation.SelectionItemPattern)pattern).Select();
                    return w;
                }
            }
            catch { }
        }
        return IntPtr.Zero;
    }

    private static string Caption(IntPtr hwnd)
    {
        var text = new System.Text.StringBuilder(512);
        GetWindowText(hwnd, text, text.Capacity);
        return text.ToString();
    }

    /// <summary>
    /// Whether a caption or tab name shows this session title. Claude Code puts
    /// a status glyph ("✳", a spinner) before the title, so that is skipped; the
    /// rest is compared whole, as one title is often the start of another
    /// ("Estado do projeto" / "Estado do projeto claudewidget").
    /// </summary>
    internal static bool SameTitle(string caption, string title)
    {
        if (title.Trim().Length == 0) return false;
        var start = 0;
        while (start < caption.Length && !char.IsLetterOrDigit(caption[start])) start++;
        return string.Equals(caption[start..].Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase);
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
        return hosted && SameTitle(Caption(fg), title);
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

    private const uint GW_OWNER = 4;
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
}
