using System.Runtime.InteropServices;

namespace ClaudeUsageWidget;

/// <summary>What a notice is about, which sets its colour.</summary>
public enum NoticeTone
{
    /// <summary>Green: a task finished, a limit renewed.</summary>
    Done,
    /// <summary>Orange, like the widget's pulse: a session needs you.</summary>
    Input,
    /// <summary>Yellow: worth a look (a pace, a stall, a full context).</summary>
    Warn,
    /// <summary>Red: a limit nearly or fully used.</summary>
    Crit,
    /// <summary>Blue: news, not trouble (the week, a new version).</summary>
    Info,
}

/// <summary>A bar like the widget's own: "7d ━━━━── 38%  8h36".</summary>
public record NoticeBar(string Label, double Percent, string Value, string Time, NoticeTone Fill);

/// <summary>
/// One of the widget's own notices, drawn like the widget rather than as a
/// Windows notification.
/// </summary>
/// <param name="Kind">The small heading: "Terminou", "Limite semanal".</param>
/// <param name="Subject">The main line: the session, or the news itself.</param>
/// <param name="Badge">The session's kind (CLI, Desktop, VS Code), as in the sessions list.</param>
/// <param name="Action">The button's label, only where there is something to do; a click anywhere does the same.</param>
/// <param name="Key">A newer notice with the same key takes the place of the one on screen.</param>
public record Notice(NoticeTone Tone, string Kind, string Subject, string Text = "", string Badge = "",
    NoticeBar? Bar = null, string Action = "", string Key = "");

/// <summary>
/// A usage alert as the history decides it: the title and text it is logged
/// with, and how its notice reads.
/// </summary>
/// <param name="FiveHour">Which limit's bar goes with it; null for none.</param>
public readonly record struct UsageAlert(string Title, string Text, bool? FiveHour, string Kind, string Subject, NoticeTone Tone)
{
    public void Deconstruct(out string title, out string text)
    {
        title = Title;
        text = Text;
    }
}

/// <summary>
/// The notices on screen, newest last, and how long each has left. Time only
/// runs while you are at the PC and not pointing at them, like Windows' own,
/// so nothing goes by while you are away.
/// </summary>
public class NoticeQueue
{
    public const int MaxShown = 4;

    public sealed class Entry(Notice notice, Action? onClick, TimeSpan life, DateTimeOffset at)
    {
        public Notice Notice { get; } = notice;
        public Action? OnClick { get; } = onClick;
        public TimeSpan Left { get; internal set; } = life;
        public DateTimeOffset At { get; } = at;
    }

    private readonly List<Entry> _entries = new();

    public IReadOnlyList<Entry> Entries => _entries;

    /// <summary>Adds a notice; returns those it pushes off: the one with its key, and the oldest beyond the limit.</summary>
    public (Entry Added, List<Entry> Removed) Add(Notice notice, Action? onClick, TimeSpan life, DateTimeOffset now)
    {
        var removed = notice.Key.Length > 0 ? _entries.Where(e => e.Notice.Key == notice.Key).ToList() : new List<Entry>();
        foreach (var e in removed) _entries.Remove(e);
        var entry = new Entry(notice, onClick, life, now);
        _entries.Add(entry);
        while (_entries.Count > MaxShown)
        {
            removed.Add(_entries[0]);
            _entries.RemoveAt(0);
        }
        return (entry, removed);
    }

    /// <summary>Counts down while counting is true; returns the notices whose time is up.</summary>
    public List<Entry> Tick(TimeSpan elapsed, bool counting)
    {
        if (!counting) return new List<Entry>();
        foreach (var e in _entries) e.Left -= elapsed;
        var done = _entries.Where(e => e.Left <= TimeSpan.Zero).ToList();
        foreach (var e in done) _entries.Remove(e);
        return done;
    }

    public bool Remove(Entry entry) => _entries.Remove(entry);

    /// <summary>Takes away the notices whose key is out of date (a session you already went to).</summary>
    public List<Entry> Prune(Func<string, bool> stale)
    {
        var gone = _entries.Where(e => e.Notice.Key.Length > 0 && stale(e.Notice.Key)).ToList();
        foreach (var e in gone) _entries.Remove(e);
        return gone;
    }

    /// <summary>How long a notice stays, counted while you are at the PC: longer when it wants something of you.</summary>
    public static TimeSpan Life(NoticeTone tone) => TimeSpan.FromSeconds(tone switch
    {
        NoticeTone.Input => 20,
        NoticeTone.Crit => 15,
        NoticeTone.Warn => 12,
        _ => 8,
    });

    /// <summary>"agora", "há 5 min", "há 1h05": a notice can wait a long while for you.</summary>
    public static string Ago(TimeSpan t) =>
        t.TotalMinutes < 1 ? "agora"
        : t.TotalHours < 1 ? $"há {(int)t.TotalMinutes} min"
        : $"há {(int)t.TotalHours}h{t.Minutes:D2}";
}

/// <summary>
/// Whether Windows wants quiet now: its own notifications stay hidden in
/// these cases, so the widget's do too.
/// </summary>
public static class WindowsQuiet
{
    /// <summary>Why Windows is quiet ("não incomodar", "ecrã inteiro"...), or null when notices may show.</summary>
    public static string? Reason()
    {
        try
        {
            if (FocusProfile() is > 0) return "não incomodar";
            if (SHQueryUserNotificationState(out var state) == 0) return ForState(state);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException) { }
        return null;
    }

    /// <summary>
    /// SHQueryUserNotificationState's answer in words. A locked screen or a
    /// screen saver (1) is not quiet: the notice waits there for you.
    /// </summary>
    internal static string? ForState(int state) => state switch
    {
        2 => "ecrã inteiro",
        3 => "jogo em ecrã inteiro",
        4 => "apresentação",
        6 => "horas de silêncio",
        _ => null,
    };

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    // "Do not disturb" (Focus assist before Windows 11) is not in that answer.
    // Windows keeps it in a notification-facility state, read the way other
    // tray apps do: 0 = off, 1 = priority only, 2 = alarms only. The widget
    // is never a priority app, so anything but 0 means quiet.
    private const ulong QuietHoursProfile = 0x0D83063EA3BF1C75;

    [DllImport("ntdll.dll")]
    private static extern int NtQueryWnfStateData(ref ulong stateName, IntPtr typeId, IntPtr explicitScope,
        out uint changeStamp, out int buffer, ref uint bufferSize);

    private static int? FocusProfile()
    {
        var name = QuietHoursProfile;
        uint size = sizeof(int);
        return NtQueryWnfStateData(ref name, IntPtr.Zero, IntPtr.Zero, out _, out var profile, ref size) == 0 ? profile : null;
    }
}
