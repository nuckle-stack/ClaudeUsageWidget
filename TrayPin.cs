using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClaudeUsageWidget;

/// <summary>
/// Whether the tray icon sits on the taskbar or inside the "^" with the
/// hidden ones. Windows 11 keeps that per program in
/// HKCU\Control Panel\NotifyIconSettings (a key per icon, with the program's
/// path and IsPromoted), the same switch as Settings > Personalisation >
/// Taskbar > Other system tray icons; the taskbar follows it at once.
/// The key appears when the icon is first shown. Older Windows have no such
/// keys, and then nothing is changed.
/// </summary>
internal static class TrayPin
{
    internal const string Key = @"Control Panel\NotifyIconSettings";

    /// <summary>On the taskbar (true), in the "^" (false), or not known to Windows yet (null).</summary>
    public static bool? IsPinned(string exe, string key = Key)
    {
        bool? pinned = null;
        foreach (var (sub, _) in Entries(exe, key))
        {
            using var entry = Registry.CurrentUser.OpenSubKey($@"{key}\{sub}");
            pinned = (pinned ?? false) || entry?.GetValue("IsPromoted") is int and not 0;
        }
        return pinned;
    }

    /// <summary>Puts the icon on the taskbar or back in the "^"; false when Windows has no entry for it yet.</summary>
    public static bool Pin(string exe, bool on, string key = Key)
    {
        var found = false;
        foreach (var (sub, _) in Entries(exe, key))
        {
            try
            {
                using var entry = Registry.CurrentUser.OpenSubKey($@"{key}\{sub}", writable: true);
                if (entry is null) continue;
                entry.SetValue("IsPromoted", on ? 1 : 0, RegistryValueKind.DWord);
                found = true;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                Log.Write($"Não deu para mudar o ícone junto ao relógio: {ex.Message}");
            }
        }
        return found;
    }

    /// <summary>The entries whose program is this exe.</summary>
    private static IEnumerable<(string Sub, string Path)> Entries(string exe, string key)
    {
        using var root = Registry.CurrentUser.OpenSubKey(key);
        if (root is null) yield break;
        foreach (var sub in root.GetSubKeyNames())
        {
            using var entry = root.OpenSubKey(sub);
            if (entry?.GetValue("ExecutablePath") is string path && SameProgram(path, exe)) yield return (sub, path);
        }
    }

    /// <summary>
    /// Windows writes the path as is, or with a known folder in front as its
    /// id ("{6D809377-…}\WindowsApps\…" for Program Files).
    /// </summary>
    internal static bool SameProgram(string stored, string exe, Func<Guid, string?>? folder = null)
    {
        folder ??= KnownFolder;
        var path = stored;
        if (stored.StartsWith('{') && stored.IndexOf('}') is var end and > 0 &&
            Guid.TryParse(stored[..(end + 1)], out var id) && folder(id) is { } dir)
            path = dir + stored[(end + 1)..];
        return string.Equals(Normal(path), Normal(exe), StringComparison.OrdinalIgnoreCase);
    }

    private static string Normal(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception) { return path; }
    }

    private static string? KnownFolder(Guid id)
    {
        try
        {
            if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var ptr) != 0) return null;
            try { return Marshal.PtrToStringUni(ptr); }
            finally { Marshal.FreeCoTaskMem(ptr); }
        }
        catch (Exception) { return null; }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
}
