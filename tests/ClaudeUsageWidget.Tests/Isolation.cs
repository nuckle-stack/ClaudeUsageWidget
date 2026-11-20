using System.Runtime.CompilerServices;

namespace ClaudeUsageWidget.Tests;

/// <summary>
/// Points the widget at a throwaway config folder before any test touches
/// Paths, so nothing (the log, the events file) reads or writes the real
/// ~/.claude/usage-widget.
/// </summary>
internal static class Isolation
{
    public static readonly string Root = Path.Combine(Path.GetTempPath(), "ClaudeUsageWidget.Tests-" + Environment.ProcessId);

    [ModuleInitializer]
    internal static void Init()
    {
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", Root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        };
    }
}
