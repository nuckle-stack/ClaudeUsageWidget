using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClaudeUsageWidget;

/// <summary>Popup listing the live Claude Code sessions; closes when it loses focus.</summary>
public partial class SessionsWindow : Window
{
    /// <summary>One row, already formatted for the template.</summary>
    public record Row(
        int Pid, string SessionId, string Kind, Brush KindBack, Brush KindFore, string Name, string Cwd,
        string Status, Brush StatusBrush, string Quality, Brush QualityBrush, string Fill,
        double Opacity, string Action, string ActionTip, long StatusStamp);

    private const string HideGlyph = "";   // Cancel (×)
    private const string ShowGlyph = "";   // RedEye

    /// <summary>Hidden rows are listed (dimmed) while this is on; it lasts as long as the popup.</summary>
    private bool _showHidden;

    private static readonly Brush Green = Frozen("#4ADE80");
    private static readonly Brush Yellow = Frozen("#FACC15");
    private static readonly Brush Red = Frozen("#F87171");
    private static readonly Brush Grey = Frozen("#80FFFFFF");

    private readonly Window _anchor;
    private readonly Config _cfg;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };

    private readonly AlertTracker _alerts;
    private static readonly Brush Orange = Frozen("#FB923C");

    private readonly Func<string> _forecast;

    private readonly UsageHistory _history;
    private string _read5 = "", _read7 = "";

    public SessionsWindow(Window anchor, Config cfg, AlertTracker alerts, Func<string> forecast, UsageHistory history)
    {
        InitializeComponent();
        _forecast = forecast;
        _history = history;
        // While the pointer is on a chart its readout shows the reading under it.
        Chart5.Hover += text => Read5.Text = text ?? _read5;
        Chart7.Hover += text => Read7.Text = text ?? _read7;
        _anchor = anchor;
        _cfg = cfg;
        _alerts = alerts;

        Loaded += (_, _) => { Place(); Activate(); };
        SizeChanged += (_, _) => Place();
        Deactivated += (_, _) => { _timer.Stop(); try { Close(); } catch (InvalidOperationException) { } };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    private void Refresh()
    {
        var all = SessionScanner.Scan();
        var sdk = all.Count(s => s.IsSdk);
        ForgetStaleHides(all);
        var listed = _cfg.ShowSdkSessions ? all : all.Where(s => !s.IsSdk).ToList();
        var hidden = listed.Count(IsHidden);
        var shown = _showHidden ? listed : listed.Where(s => !IsHidden(s)).ToList();
        var visible = listed.Count - hidden;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _read5 = Plot(Chart5, Read5, true, now - 86400, now, _read5);
        _read7 = Plot(Chart7, Read7, false, now - 7 * 86400, now, _read7);

        var forecast = _forecast();
        Forecast.Text = forecast;
        ForecastBox.Visibility = forecast.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Forecast.Foreground = forecast.Contains("esgota") ? Orange : Grey;

        Header.Text = visible == 1 ? "1 sessão ativa" : $"{visible} sessões ativas";
        Empty.Text = hidden > 0 ? "Todas as sessões estão escondidas." : "Nenhuma sessão do Claude Code ativa.";
        Empty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        List.ItemsSource = shown.Select(ToRow).ToList();

        HiddenToggle.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
        HiddenToggle.Text = _showHidden
            ? "Voltar a esconder"
            : hidden == 1 ? "Mostrar 1 escondida" : $"Mostrar {hidden} escondidas";

        SdkToggle.Visibility = sdk > 0 ? Visibility.Visible : Visibility.Collapsed;
        SdkToggle.Text = _cfg.ShowSdkSessions
            ? "Esconder sessões de plugins"
            : $"Mostrar {sdk} de plugins / automação";
    }

    /// <summary>Feeds one chart and returns its resting readout (latest and peak).</summary>
    private string Plot(Sparkline chart, System.Windows.Controls.TextBlock readout, bool fiveHour, long from, long to, string previous)
    {
        var points = _history.Series(fiveHour, from);
        chart.Set(points, from, to);
        var text = points.Count == 0
            ? "a recolher dados"
            : $"agora {Math.Round(points[^1].Percent)}% · máx {Math.Round(points.Max(p => p.Percent))}%";
        // Leave a hover readout alone; only replace the resting text.
        if (readout.Text.Length == 0 || readout.Text == previous) readout.Text = text;
        return text;
    }

    private Row ToRow(SessionInfo s)
    {
        var (back, fore) = s.Kind switch
        {
            "CLI" => (Frozen("#3360A5FA"), Frozen("#93C5FD")),
            "Desktop" => (Frozen("#33C084FC"), Frozen("#D8B4FE")),
            "SDK" => (Frozen("#22FFFFFF"), Frozen("#B3FFFFFF")),
            _ => (Frozen("#3334D399"), Frozen("#6EE7B7")),
        };

        var status = s.Status switch
        {
            "busy" => "a trabalhar",
            "idle" => "parada",
            "" => "—",
            var other => other,
        };
        var statusBrush = s.Status == "busy" ? Yellow : Grey;
        if (_alerts.For(s.SessionId) is { } alert)
        {
            (status, statusBrush) = alert.Kind == AlertKind.Input
                ? ($"precisa de ti: {alert.Message}", Orange)
                : ("terminou, à tua espera", Green);
        }
        // How long it has been in this state says more than the process age: the
        // desktop app restarts a conversation's process whenever you reopen it.
        if (s.StatusUpdatedAt > 0)
        {
            var since = Age(DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(s.StatusUpdatedAt));
            status += s.Status is "busy" or "idle" && _alerts.For(s.SessionId) is null ? $" há {since}" : $" · há {since}";
        }
        else if (s.StartedAt is { } started)
            status += $" · aberta há {Age(DateTimeOffset.UtcNow - started)}";

        var quality = s.Quality is { } q ? $"Q {Math.Round(q)}" + (s.Grade is { } g ? $" {g}" : "") : "";
        var qBrush = s.Quality switch { null => Grey, < 50 => Red, < 70 => Yellow, _ => Green };
        var fill = s.FillPct is { } f ? $"{Math.Round(f)}% ctx" : "";

        var (action, tip) = ("", "");
        var opacity = 1.0;
        if (_cfg.HiddenSessions.ContainsKey(s.SessionId))
            (action, tip, opacity) = (ShowGlyph, "Mostrar outra vez", 0.5);
        else if (AutoHidden(s))
        {
            (opacity, status) = (0.5, status + " · escondida por inatividade");
        }
        else if (s.SessionId.Length > 0)
            (action, tip) = (HideGlyph, "Esconder até voltar a ter atividade");

        return new Row(s.Pid, s.SessionId, s.Kind, back, fore, s.Name, ShortPath(s.Cwd),
            status, statusBrush, quality, qBrush, fill, opacity, action, tip, s.StatusUpdatedAt);
    }

    // ---- hiding ------------------------------------------------------------

    private bool IsHidden(SessionInfo s) => _cfg.HiddenSessions.ContainsKey(s.SessionId) || AutoHidden(s);

    /// <summary>A desktop-app conversation that has sat idle past the configured time, with nothing to tell.</summary>
    private bool AutoHidden(SessionInfo s) =>
        _cfg.HideIdleDesktopMinutes > 0 && s.Kind == "Desktop" && s.Status == "idle" && s.StatusUpdatedAt > 0 &&
        DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(s.StatusUpdatedAt) >= TimeSpan.FromMinutes(_cfg.HideIdleDesktopMinutes) &&
        _alerts.For(s.SessionId) is not { Acked: false };

    /// <summary>
    /// A hand-hidden session shows again once it has moved on (its status
    /// changed), raised an alert not yet seen, or ended.
    /// </summary>
    private void ForgetStaleHides(List<SessionInfo> all)
    {
        var live = all.Where(s => s.SessionId.Length > 0).DistinctBy(s => s.SessionId).ToDictionary(s => s.SessionId);
        var stale = _cfg.HiddenSessions
            .Where(h => !live.TryGetValue(h.Key, out var s) || s.StatusUpdatedAt != h.Value ||
                        _alerts.For(h.Key) is { Acked: false })
            .Select(h => h.Key).ToList();
        if (stale.Count == 0) return;
        foreach (var id in stale) _cfg.HiddenSessions.Remove(id);
        Json.Save(Paths.Config, _cfg);
    }

    private void OnRowAction(object sender, MouseButtonEventArgs e)
    {
        // Don't let the click reach the row, which would focus the session.
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not Row row || row.SessionId.Length == 0) return;
        if (!_cfg.HiddenSessions.Remove(row.SessionId))
            _cfg.HiddenSessions[row.SessionId] = row.StatusStamp;
        Json.Save(Paths.Config, _cfg);
        Refresh();
    }

    private void OnToggleHidden(object sender, MouseButtonEventArgs e)
    {
        _showHidden = !_showHidden;
        Refresh();
    }

    private static string Age(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d{t.Hours:D2}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h{t.Minutes:D2}";
        return $"{Math.Max(1, (int)t.TotalMinutes)}m";
    }

    private static string ShortPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + path[home.Length..] : path;
    }

    /// <summary>Above the widget, right edges aligned; below it when there is no room.</summary>
    private void Place()
    {
        var left = _anchor.Left + _anchor.ActualWidth - ActualWidth;
        var top = _anchor.Top - ActualHeight - 6;
        if (top < SystemParameters.VirtualScreenTop) top = _anchor.Top + _anchor.ActualHeight + 6;
        Left = Math.Max(SystemParameters.VirtualScreenLeft, left);
        Top = top;
    }

    /// <summary>Clicking a session brings its terminal (or the desktop app) to the front.</summary>
    private void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Row row) return;
        // Focusing another window deactivates this popup, which closes it.
        var host = WindowFocus.Focus(row.Pid, row.Name);
        Log.Write(host is null
            ? $"Foco: janela da sessão \"{row.Name}\" não encontrada."
            : $"Foco: sessão \"{row.Name}\" em {host}.");
        if (host is null) Header.Text = $"Janela de \"{row.Name}\" não encontrada";
    }

    private void OnToggleSdk(object sender, MouseButtonEventArgs e)
    {
        _cfg.ShowSdkSessions = !_cfg.ShowSdkSessions;
        Json.Save(Paths.Config, _cfg);
        Refresh();
    }

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
