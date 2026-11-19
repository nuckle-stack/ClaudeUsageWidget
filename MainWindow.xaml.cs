using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace ClaudeUsageWidget;

public partial class MainWindow : Window
{
    private const double TrackWidth = 64;

    private static readonly Brush Ok = Frozen("#4ADE80");
    private static readonly Brush Warn = Frozen("#FACC15");
    private static readonly Brush Crit = Frozen("#F87171");
    private static readonly Brush Orange = Frozen("#FB923C");
    private static readonly Brush Dim = Frozen("#99FFFFFF");
    private readonly UsageHistory _history = new();

    private readonly AutoHello _hello = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Forms.NotifyIcon _tray = new();
    private readonly Dictionary<string, Drawing.Icon> _icons = new();

    private Config _cfg = Json.Load<Config>(Paths.Config);
    private Limits _limits = new();
    private DateTime _limitsStamp;
    private static readonly Color NormalBack = Hex("#F015151A");
    private static readonly Color NormalBorder = Hex("#30FFFFFF");
    private readonly AlertTracker _alerts = new();
    private readonly ContextWatch _context = new();
    private AlertKind? _pulsing;
    private SessionsWindow? _sessions;
    private DateTime _sessionsClosed;

    public MainWindow()
    {
        InitializeComponent();
        ApplyConfig();
        SetupTray();

        SourceInitialized += (_, _) => HideFromAltTab();
        Root.ContextMenu.Opened += (_, _) => FillQuietMenu();
        FillQuietMenu();
        Loaded += (_, _) => { PlaceWindow(); Tick(); };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    // ---- refresh -----------------------------------------------------------

    private async void Tick()
    {
        ReloadLimitsIfChanged();
        Ingest();
        Render();
        if (_cfg.AlwaysOnTop && !Topmost) Topmost = true;

        await CheckAlertsAsync();

        await _hello.CheckAsync(_cfg, _limits);
        Ingest();
        Render();
    }

    // ---- do not disturb ----------------------------------------------------

    private static readonly (string Label, int Minutes)[] QuietChoices =
        [("30 minutos", 30), ("1 hora", 60), ("2 horas", 120), ("Até eu desligar", -1)];

    /// <summary>True while notifications and sounds are muted. The widget still pulses.</summary>
    private bool Quiet
    {
        get
        {
            if (_cfg.QuietUntil == -1) return true;
            if (_cfg.QuietUntil == 0) return false;
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() < _cfg.QuietUntil) return true;
            SetQuiet(0);   // the period just ran out
            return false;
        }
    }

    private void SetQuiet(int minutes)
    {
        _cfg.QuietUntil = minutes switch
        {
            0 => 0,
            < 0 => -1,
            _ => DateTimeOffset.UtcNow.AddMinutes(minutes).ToUnixTimeSeconds(),
        };
        Json.Save(Paths.Config, _cfg);
        Render();
    }

    private string QuietLabel() => _cfg.QuietUntil switch
    {
        -1 => "Não incomodar: ligado",
        0 => "Não incomodar",
        var until => $"Não incomodar: até às {DateTimeOffset.FromUnixTimeSeconds(until).LocalDateTime:HH:mm}",
    };

    /// <summary>The submenu entries, shared by the widget's menu and the tray's.</summary>
    private IEnumerable<(string Label, Action Run)> QuietActions()
    {
        if (Quiet) yield return ("Desligar", () => SetQuiet(0));
        foreach (var (label, minutes) in QuietChoices)
            yield return (label, () => SetQuiet(minutes));
    }

    private void OnToggleSound(object sender, RoutedEventArgs e) => ToggleSound();

    private void ToggleSound()
    {
        _cfg.Alerts.Sound = !_cfg.Alerts.Sound;
        Json.Save(Paths.Config, _cfg);
        // Switching it on plays the sound, so you hear what an alert sounds like.
        if (_cfg.Alerts.Sound) System.Media.SystemSounds.Asterisk.Play();
    }

    private void FillQuietMenu()
    {
        SoundMenu.IsChecked = _cfg.Alerts.Sound;
        QuietMenu.Header = QuietLabel();
        QuietMenu.Items.Clear();
        foreach (var (label, run) in QuietActions())
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => run();
            QuietMenu.Items.Add(item);
        }
    }

    private void FillQuietTray(Forms.ToolStripMenuItem menu)
    {
        menu.Text = QuietLabel();
        menu.DropDownItems.Clear();
        foreach (var (label, run) in QuietActions())
            menu.DropDownItems.Add(label, null, (_, _) => run());
    }

    /// <summary>Records a new reading and announces any limit it crossed.</summary>
    private void Ingest()
    {
        if (!_history.Add(_hello.Current(_limits))) return;
        foreach (var (title, text) in _history.PendingAlerts(_cfg.UsageAlerts))
        {
            Log.Write($"Aviso de uso{(Quiet ? " [silenciado]" : "")}: {title} — {text}");
            if (Quiet) continue;
            _tray.ShowBalloonTip(8000, title, text, Forms.ToolTipIcon.Warning);
            if (_cfg.Alerts.Sound) System.Media.SystemSounds.Exclamation.Play();
        }
    }

    /// <summary>The pace lines shown in the tooltip and the sessions panel.</summary>
    private string ForecastText()
    {
        var lines = new List<string>();
        if (_history.Describe(true) is { Length: > 0 } five) lines.Add("5h: " + five);
        if (_history.Describe(false) is { Length: > 0 } seven) lines.Add("7d: " + seven);
        return string.Join("\n", lines);
    }

    // ---- alerts ------------------------------------------------------------

    private async Task CheckAlertsAsync()
    {
        if (!_cfg.Alerts.Enabled) _alerts.Clear();
        if (!_cfg.Alerts.Enabled && !_cfg.ContextAlerts.Enabled)
        {
            UpdatePulse();
            return;
        }
        try
        {
            var sessions = await Task.Run(SessionScanner.Scan);
            if (_cfg.Alerts.Enabled)
                foreach (var alert in _alerts.Update(_cfg.Alerts, sessions)) Announce(alert);
            if (_cfg.ContextAlerts.Enabled)
                foreach (var (title, text) in _context.Update(_cfg.ContextAlerts, sessions))
                {
                    Log.Write($"Aviso de contexto{(Quiet ? " [silenciado]" : "")}: {title} — {text.Replace("\n", " — ")}");
                    if (Quiet) continue;
                    _tray.ShowBalloonTip(8000, title, text, Forms.ToolTipIcon.Warning);
                    if (_cfg.Alerts.Sound) System.Media.SystemSounds.Exclamation.Play();
                }
        }
        catch (Exception ex) { Log.Write($"Falha nos avisos: {ex.Message}"); }
        UpdatePulse();
    }

    private void Announce(Alert a)
    {
        var input = a.Kind == AlertKind.Input;
        Log.Write($"Aviso ({(input ? "precisa de ti" : "terminou")}){(Quiet ? " [silenciado]" : "")}: {a.Name} — {a.Message}");
        if (Quiet) return;
        _tray.ShowBalloonTip(6000,
            input ? "Claude precisa de ti" : "Claude terminou",
            $"{a.Name}\n{a.Message}",
            input ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
        if (_cfg.Alerts.Sound)
            (input ? System.Media.SystemSounds.Exclamation : System.Media.SystemSounds.Asterisk).Play();
    }

    /// <summary>Pulses the widget while an alert is unanswered: amber for input, green for done.</summary>
    private void UpdatePulse()
    {
        var want = _alerts.Pending;
        if (want == _pulsing) return;
        _pulsing = want;

        var back = new SolidColorBrush(NormalBack);
        var border = new SolidColorBrush(NormalBorder);
        Root.Background = back;
        Root.BorderBrush = border;
        if (want is null) return;

        var (toBack, toBorder) = want == AlertKind.Input
            ? (Hex("#F0553408"), Hex("#FB923C"))
            : (Hex("#F00E4424"), Hex("#4ADE80"));
        var time = new Duration(TimeSpan.FromMilliseconds(700));
        back.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(toBack, time) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        border.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(toBorder, time) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
    }

    private void ReloadLimitsIfChanged()
    {
        try
        {
            var stamp = File.Exists(Paths.Limits) ? File.GetLastWriteTimeUtc(Paths.Limits) : default;
            if (stamp == _limitsStamp) return;
            _limitsStamp = stamp;
            _limits = Json.Load<Limits>(Paths.Limits);
        }
        catch { }
    }

    private void Render()
    {
        var cur = _hello.Current(_limits);
        var t5 = RenderRow(cur.FiveHour, Fill5, Pct5, Time5, cur.Estimated);
        var t7 = RenderRow(cur.SevenDay, Fill7, Pct7, Time7, false);
        if (!cur.Estimated)
        {
            ShowExhaustion(true, Time5);
            ShowExhaustion(false, Time7);
        }

        var tip = $"Claude Code\n5h: {t5}\n7d: {t7}";
        if (ForecastText() is { Length: > 0 } forecast) tip += "\nRitmo\n" + forecast;
        var quiet = Quiet;
        QuietIcon.Visibility = quiet ? Visibility.Visible : Visibility.Collapsed;
        if (quiet) tip += "\n" + QuietLabel();
        if (_hello.Running) tip += "\nA atualizar…";
        else if (cur.UpdatedAt > 0)
        {
            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(cur.UpdatedAt);
            tip += age.TotalMinutes < 1 ? "\nAtualizado agora" : $"\nAtualizado há {FormatLeft(age)}";
        }
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;
        _tray.Icon = IconFor(LevelOf(Percent(cur.FiveHour)));
        Root.ToolTip = tip + (_cfg.AutoHello.Enabled ? "\nAuto-hello: ligado" : "\nAuto-hello: desligado");
    }

    /// <summary>Paints one row and returns its plain-text summary for the tooltip.</summary>
    private string RenderRow(LimitWindow? w, Border fill, TextBlock pct, TextBlock time, bool estimated)
    {
        time.Foreground = Dim;
        if (w?.ResetsAt is not long resetsAt)
        {
            fill.Width = 0;
            pct.Text = "--";
            time.Text = "";
            return "sem dados";
        }

        var left = DateTimeOffset.FromUnixTimeSeconds(resetsAt) - DateTimeOffset.UtcNow;
        if (left <= TimeSpan.Zero)
        {
            fill.Width = 0;
            pct.Text = "0%";
            pct.Foreground = Ok;
            time.Text = "renovou";
            return "renovou";
        }

        var p = Percent(w);
        var brush = LevelOf(p) switch { "crit" => Crit, "warn" => Warn, _ => Ok };
        fill.Width = Math.Max(p > 0 ? 3 : 0, TrackWidth * p / 100.0);
        fill.Background = brush;
        pct.Foreground = brush;
        pct.Text = $"{p}%";
        time.Text = (estimated ? "~" : "") + FormatLeft(left);
        return $"{p}% (faltam {time.Text})";
    }

    /// <summary>
    /// When the pace would exhaust the limit before it resets, the time column
    /// shows that moment (in orange) instead of the time left to the reset.
    /// </summary>
    private void ShowExhaustion(bool fiveHour, TextBlock time)
    {
        var p = _history.Project(fiveHour);
        var now = DateTimeOffset.UtcNow;
        if (p is not { Reliable: true, ExhaustAt: { } at } || p.Percent >= 100 || now.ToUnixTimeSeconds() >= p.ResetsAt) return;
        time.Text = "⚠" + UsageHistory.Short(at < now ? now : at, fiveHour);
        time.Foreground = Orange;
    }

    private static int Percent(LimitWindow? w) =>
        (int)Math.Clamp(Math.Round(w?.UsedPercentage ?? 0), 0, 100);

    private string LevelOf(int p) => p >= _cfg.CritAt ? "crit" : p >= _cfg.WarnAt ? "warn" : "ok";

    private static string FormatLeft(TimeSpan left)
    {
        if (left.TotalDays >= 1) return $"{(int)left.TotalDays}d{left.Hours:D2}h";
        if (left.TotalHours >= 1) return $"{(int)left.TotalHours}h{left.Minutes:D2}";
        return $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}m";
    }

    // ---- window ------------------------------------------------------------

    private void ApplyConfig()
    {
        Opacity = Math.Clamp(_cfg.Opacity, 0.2, 1.0);
        Topmost = _cfg.AlwaysOnTop;
    }

    private void PlaceWindow()
    {
        var area = SystemParameters.WorkArea;
        var left = _cfg.Left ?? area.Right - ActualWidth - 16;
        var top = _cfg.Top ?? area.Bottom - ActualHeight - 16;

        // A saved position can point at a monitor that is no longer attached.
        var vLeft = SystemParameters.VirtualScreenLeft;
        var vTop = SystemParameters.VirtualScreenTop;
        Left = Math.Clamp(left, vLeft, Math.Max(vLeft, vLeft + SystemParameters.VirtualScreenWidth - ActualWidth));
        Top = Math.Clamp(top, vTop, Math.Max(vTop, vTop + SystemParameters.VirtualScreenHeight - ActualHeight));
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        var (left, top) = (Left, Top);
        DragMove();

        // DragMove returns on mouse-up: no movement means it was a plain click.
        if (Math.Abs(Left - left) < 1 && Math.Abs(Top - top) < 1)
        {
            ToggleSessions();
            return;
        }
        _cfg.Left = Left;
        _cfg.Top = Top;
        Json.Save(Paths.Config, _cfg);
    }

    internal void ToggleSessions()
    {
        // The popup closes itself when it loses focus, which this same click
        // causes; without the time check the click would reopen it at once.
        if (_sessions is not null || DateTime.UtcNow - _sessionsClosed < TimeSpan.FromMilliseconds(300)) return;
        // Looking at the sessions is the acknowledgement: stop pulsing and reminding.
        _alerts.AckAll();
        UpdatePulse();
        _sessions = new SessionsWindow(this, _cfg, _alerts, ForecastText, _history);
        _sessions.Closed += (_, _) => { _sessions = null; _sessionsClosed = DateTime.UtcNow; };
        _sessions.Show();
    }

    private void HideFromAltTab()
    {
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x80;
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() | WS_EX_TOOLWINDOW));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    // ---- commands ----------------------------------------------------------

    private void OnSessions(object sender, RoutedEventArgs e) => ToggleSessions();
    private void OnSettings(object sender, RoutedEventArgs e) => OpenSettings();
    private void OnHelloNow(object sender, RoutedEventArgs e) => HelloNow();
    private void OnHide(object sender, RoutedEventArgs e) => Hide();
    private void OnExit(object sender, RoutedEventArgs e) => Exit();

    private void OpenSettings()
    {
        var dlg = new SettingsWindow(_cfg);
        if (dlg.ShowDialog() != true) return;
        _cfg = dlg.Result;
        Json.Save(Paths.Config, _cfg);
        ApplyConfig();
        Render();
    }

    private async void HelloNow()
    {
        if (_hello.Running) return;
        var task = _hello.SendAsync(_cfg);
        Render();
        var ok = await task;
        Render();
        _tray.ShowBalloonTip(4000, "Claude Usage",
            ok ? "Limites atualizados." : "Atualização falhou. Ver widget.log.",
            ok ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning);
    }

    private void ToggleVisible()
    {
        if (IsVisible) Hide();
        else { Show(); Activate(); }
    }

    private void Exit()
    {
        _timer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        Application.Current.Shutdown();
    }

    // ---- tray --------------------------------------------------------------

    private void SetupTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Mostrar / esconder", null, (_, _) => ToggleVisible());
        var quiet = new Forms.ToolStripMenuItem("Não incomodar");
        menu.Items.Add(quiet);
        var sound = new Forms.ToolStripMenuItem("Som dos avisos", null, (_, _) => ToggleSound());
        menu.Items.Add(sound);
        menu.Opening += (_, _) => { FillQuietTray(quiet); sound.Checked = _cfg.Alerts.Sound; };
        menu.Items.Add("Definições…", null, (_, _) => OpenSettings());
        menu.Items.Add("Atualizar agora", null, (_, _) => HelloNow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => Exit());

        _tray.ContextMenuStrip = menu;
        _tray.Icon = IconFor("ok");
        _tray.Text = "Claude Usage";
        _tray.Visible = true;
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ToggleVisible(); };
    }

    private Drawing.Icon IconFor(string level)
    {
        if (_icons.TryGetValue(level, out var icon)) return icon;

        var color = level switch
        {
            "crit" => Drawing.Color.FromArgb(248, 113, 113),
            "warn" => Drawing.Color.FromArgb(250, 204, 21),
            _ => Drawing.Color.FromArgb(74, 222, 128),
        };
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var back = new Drawing.SolidBrush(Drawing.Color.FromArgb(21, 21, 26));
            using var fore = new Drawing.SolidBrush(color);
            g.FillEllipse(back, 1, 1, 30, 30);
            g.FillEllipse(fore, 8, 8, 16, 16);
        }
        // Cached for the life of the process, so the handle is never released.
        icon = Drawing.Icon.FromHandle(bmp.GetHicon());
        _icons[level] = icon;
        return icon;
    }

    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush(Hex(hex));
        b.Freeze();
        return b;
    }
}
