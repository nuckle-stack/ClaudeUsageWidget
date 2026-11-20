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
        double Opacity, string Action, string ActionTip, long StatusStamp, string Use, string UseTip);

    /// <summary>One project in the usage breakdown.</summary>
    public record UseRow(string Name, double BarWidth, string Percent, string Detail);

    private const double UseBarWidth = 96;
    private static readonly System.Globalization.CultureInfo Pt = System.Globalization.CultureInfo.GetCultureInfo("pt-PT");

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

    private readonly Func<Limits> _limits;
    /// <summary>The usage breakdown on show: the 5-hour window, or the week.</summary>
    private bool _useWeek;
    private Breakdown _use = Breakdown.Empty;

    public SessionsWindow(Window anchor, Config cfg, AlertTracker alerts, Func<string> forecast, UsageHistory history,
        Func<Limits> limits)
    {
        InitializeComponent();
        _forecast = forecast;
        _history = history;
        _limits = limits;
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
        RefreshUse();
        var all = SessionScanner.Scan();
        var sdk = all.Count(s => s.IsSdk);
        ForgetStaleHides(all);
        var listed = _cfg.ShowSdkSessions ? all : all.Where(s => !s.IsSdk).ToList();
        var hidden = listed.Count(IsHidden);
        // Sessions waiting for you first, in the order the shortcut visits them;
        // the rest keep the scanner's order (working first, then newest).
        var order = _alerts.Waiting().Select((a, i) => (a.SessionId, i)).ToDictionary(x => x.SessionId, x => x.i);
        listed = listed.OrderBy(s => order.GetValueOrDefault(s.SessionId, int.MaxValue)).ToList();
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

    // ---- usage by project ----------------------------------------------------

    /// <summary>Reads new transcript lines in the background (at most every 20 s), then redraws.</summary>
    private void RefreshUse()
    {
        var reading = UsageLedger.Shared.RefreshAsync(TimeSpan.FromSeconds(20));
        // The rows' own share column catches up on the next tick (3 s).
        if (!reading.IsCompleted) reading.ContinueWith(_ => Dispatcher.BeginInvoke(RenderUse));
        RenderUse();
    }

    private void RenderUse()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var cur = _limits();
        var (from, to) = _useWeek ? Span(cur.SevenDay, 7 * 86400, now) : Span(cur.FiveHour, 5 * 3600, now);
        _use = UsageLedger.Shared.Summarize(from, to);

        UseBox.Visibility = UsageLedger.Shared.RefreshedAt == default ? Visibility.Collapsed : Visibility.Visible;
        UseTitle.Text = _useWeek ? "Consumo · esta semana" : "Consumo · esta janela de 5h";
        UseTab5.Foreground = _useWeek ? Grey : Frozen("#F2FFFFFF");
        UseTab7.Foreground = _useWeek ? Frozen("#F2FFFFFF") : Grey;

        var rows = new List<UseRow>();
        if (_use.Total > 0)
        {
            const int shown = 4;
            foreach (var p in _use.Projects.Take(shown))
                rows.Add(new UseRow(p.Name, UseBarWidth * p.Cost / _use.Total, Pct(p.Cost),
                    string.Join("\n", p.Sessions.Select(s => $"{s.Title} — {Pct(s.Cost)}"))));
            var rest = _use.Projects.Skip(shown).ToList();
            if (rest.Count > 0)
            {
                var cost = rest.Sum(p => p.Cost);
                rows.Add(new UseRow(rest.Count == 1 ? rest[0].Name : $"outros {rest.Count} projetos",
                    UseBarWidth * cost / _use.Total, Pct(cost),
                    string.Join("\n", rest.Select(p => $"{p.Name} — {Pct(p.Cost)}"))));
            }
        }
        UseList.ItemsSource = rows;
        UseNote.Text = _use.Total > 0
            ? $"Estimativa pelo custo equivalente na API (≈ ${_use.Total.ToString("0.00", Pt)})"
            : _useWeek ? "Sem uso do Claude Code neste PC esta semana." : "Sem uso do Claude Code neste PC nesta janela.";
    }

    private string Pct(double cost)
    {
        var p = 100 * cost / _use.Total;
        return p is > 0 and < 1 ? "<1%" : $"{Math.Round(p)}%";
    }

    /// <summary>The running window, or the last 5 h / 7 days when its reset is unknown or past.</summary>
    private static (long From, long To) Span(LimitWindow? w, long length, long now) =>
        w?.ResetsAt is { } r && r > now ? (r - length, r) : (now - length, now + 1);

    private void OnUseTab5(object sender, MouseButtonEventArgs e) { _useWeek = false; Refresh(); }
    private void OnUseTab7(object sender, MouseButtonEventArgs e) { _useWeek = true; Refresh(); }

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

        // Its share of the usage on show above (the 5-hour window or the week).
        var share = _use.ShareOf(s.SessionId);
        var use = share <= 0 ? "" : (share < 0.01 ? "<1%" : $"{Math.Round(share * 100)}%") + (_useWeek ? " sem." : " 5h");
        var useTip = _useWeek ? "Parte do consumo desta semana neste PC (estimativa)"
                              : "Parte do consumo desta janela de 5h neste PC (estimativa)";

        return new Row(s.Pid, s.SessionId, s.Kind, back, fore, s.Name, ShortPath(s.Cwd),
            status, statusBrush, quality, qBrush, fill, opacity, action, tip, s.StatusUpdatedAt, use, useTip);
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
