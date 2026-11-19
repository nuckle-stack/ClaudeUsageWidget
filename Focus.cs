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
        var seen = new HashSet<int>();
        DateTime? childStart = null;
        for (var id = pid; id > 4 && seen.Add(id);)
        {
            int parent;
            try
            {
                using var p = Process.GetProcessById(id);
                if (Stop.Contains(p.ProcessName)) return null;
                // A "parent" that started after its child is a reused pid, not a parent.
                if (childStart is { } cs && p.StartTime > cs) return null;
                childStart = p.StartTime;

                var hwnd = p.MainWindowHandle;
                if (hwnd != IntPtr.Zero)
                {
                    if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                    SetForegroundWindow(hwnd);
                    return p.ProcessName;
                }
                parent = ParentOf(p);
            }
            catch { return null; }
            id = parent;
        }
        return null;
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
}
