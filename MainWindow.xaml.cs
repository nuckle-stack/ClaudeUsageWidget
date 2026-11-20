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
    private readonly OverageWatch _overage = new();
    private AlertKind? _pulsing;
    private SessionsWindow? _sessions;
    private DateTime _sessionsClosed;
    /// <summary>What a click on the notification on screen does; replaced by each new one.</summary>
    private Action? _onNotificationClick;

    public MainWindow()
    {
        InitializeComponent();
        ApplyConfig();
        SetupTray();

        SourceInitialized += (_, _) =>
        {
            HideFromAltTab();
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(OnWindowMessage);
            RegisterHotkey();
        };
        Root.ContextMenu.Opened += (_, _) => { FillQuietMenu(); UpdateMenu.Header = UpdateLabel(); };
        FillQuietMenu();
        Loaded += (_, _) =>
        {
            PlaceWindow();
            Tick();
            // Read the week of transcripts now, so the sessions list opens with it.
            _ = UsageLedger.Shared.RefreshAsync(TimeSpan.Zero);
            AnnounceUpdated();
        };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        // The first look a minute after starting (not during the rush of a
        // login), then every few hours.
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(6);
            await CheckUpdatesAsync(manual: false);
        };
        _updateTimer.Start();
        Updater.CleanUp();

        // How long it took to appear: on a slow or busy PC the first start of
        // a new version (unpacked, scanned by the antivirus) can take a while.
        ContentRendered += (_, _) =>
        {
            var took = DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime;
            Log.Write($"Arranque: widget visível {took.TotalSeconds:0.0} s depois de o processo começar.");
        };

        // Opened again while running (Start menu, a double click): show this one.
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, App.ShowSignalName);
        new Thread(() => { while (show.WaitOne()) Dispatcher.BeginInvoke(ShowHere); }) { IsBackground = true }.Start();
    }

    // ---- where it is -------------------------------------------------------------

    /// <summary>Visible, on a screen, in front: when asked from outside.</summary>
    private void ShowHere()
    {
        if (!IsVisible) Show();
        if (!OnAScreen()) ResetPosition();
        Topmost = false;
        Topmost = true;
        Log.Write("Widget mostrado a pedido.");
    }

    /// <summary>Back to the default spot, beside the notifications, and shown.</summary>
    private void ResetPosition()
    {
        _cfg.Left = null;
        _cfg.Top = null;
        Json.Save(Paths.Config, _cfg);
        if (!IsVisible) Show();
        PlaceWindow();
        Log.Write($"Posição reposta: {Left:0},{Top:0}.");
    }

    /// <summary>Whether any part of the window is on a monitor (a saved spot can be on one no longer there).</summary>
    private bool OnAScreen()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        return hwnd == IntPtr.Zero || MonitorFromWindow(hwnd, 0) != IntPtr.Zero;
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    /// <summary>For the diagnostics: where the window is and what the screens are.</summary>
    private string WindowStatus()
    {
        var screens = string.Join("; ", Forms.Screen.AllScreens.Select(s =>
            $"{s.Bounds.Width}x{s.Bounds.Height} em {s.Bounds.X},{s.Bounds.Y}{(s.Primary ? " (principal)" : "")}"));
        var area = SystemParameters.WorkArea;
        return $"{(IsVisible ? "visível" : "escondida")}{(OnAScreen() ? "" : ", FORA DOS ECRÃS")} em {Left:0},{Top:0} " +
               $"({ActualWidth:0}x{ActualHeight:0}); área de trabalho {area.Width:0}x{area.Height:0} em {area.Left:0},{area.Top:0}; " +
               $"ecrãs: {screens}";
    }

    // ---- updates -----------------------------------------------------------

    private readonly Updater _updater = new();
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _updating;

    private string UpdateLabel() =>
        _updater.Available is { } u ? $"Instalar a versão {u.Version.ToString(3)}…" : "Procurar versão nova";

    private void OnUpdate(object sender, RoutedEventArgs e) => UpdateFromMenu();

    private async void UpdateFromMenu()
    {
        if (_updater.Available is { } u) InstallUpdate(u);
        else await CheckUpdatesAsync(manual: true);
    }

    /// <summary>Looks on GitHub; a new version is announced once, or every time when asked from the menu.</summary>
    private async Task CheckUpdatesAsync(bool manual)
    {
        if (!manual && !_cfg.CheckUpdates) return;
        try
        {
            if (await _updater.CheckAsync() is not { } update)
            {
                if (manual) Notify("Claude Usage", $"Tens a versão mais recente ({Updater.Current.ToString(3)}).", Forms.ToolTipIcon.Info, 4000, null);
                return;
            }
            if (!manual && !_updater.ShouldAnnounce(update)) return;
            Log.Write($"Versão nova no GitHub: {update.Version.ToString(3)}.");
            Notify("Versão nova do Claude Usage",
                $"A {update.Version.ToString(3)} está disponível (tens a {Updater.Current.ToString(3)}). Clica aqui para a instalar.",
                Forms.ToolTipIcon.Info, 8000, () => InstallUpdate(update));
        }
        catch (Exception ex)
        {
            Log.Write($"Procura de versão nova falhou: {ex.Message}");
            if (manual) Notify("Claude Usage", "Não deu para ver se há versão nova. Ver widget.log.", Forms.ToolTipIcon.Warning, 4000, null);
        }
    }

    /// <summary>
    /// On the user's go-ahead: download and check the zip, start its installer
    /// and get out of its way. The installer starts the new version.
    /// </summary>
    private async void InstallUpdate(UpdateInfo update)
    {
        if (_updating) return;
        var version = update.Version.ToString(3);
        var notes = update.Notes.Length > 0 ? $"\n\n{update.Notes}" : "";
        // Asked from a notification or the tray, the widget is not the active
        // window, and its question could open behind the others.
        if (!IsVisible) Show();
        WindowFocus.BringToFront(new WindowInteropHelper(this).Handle);
        var answer = MessageBox.Show(this,
            $"Instalar a versão {version} do Claude Usage agora?\n\nO widget fecha-se e volta a abrir sozinho em poucos segundos. As definições ficam.{notes}",
            "Versão nova do Claude Usage", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        _updating = true;
        try
        {
            Notify("Claude Usage", $"A descarregar a versão {version} ({update.Size / 1048576.0:0} MB)…", Forms.ToolTipIcon.Info, 4000, null);
            var folder = await _updater.DownloadAsync(update);
            Updater.RunInstaller(folder, SettingsWindow.IsAutoStart());
            Log.Write($"Atualização para {version}: instalador iniciado (registo em update.log).");
            Exit();
        }
        catch (Exception ex)
        {
            _updating = false;
            Log.Write($"Atualização para {version} falhou: {ex}");
            Notify("Não deu para atualizar", $"{ex.Message} Ver widget.log.", Forms.ToolTipIcon.Warning, 6000, null);
        }
    }

    /// <summary>After an update, says so once; a click opens the release notes.</summary>
    private void AnnounceUpdated()
    {
        if (_updater.JustUpdatedFrom() is not { } from) return;
        var now = Updater.Current.ToString(3);
        Log.Write($"Atualizado de {from} para {now}.");
        Notify("Claude Usage atualizado", $"Estás agora na versão {now}. Clica para ver as novidades.", Forms.ToolTipIcon.Info, 6000,
            () => OpenUrl($"https://github.com/{Updater.Repo}/releases/tag/v{now}"));
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write($"Não deu para abrir {url}: {ex.Message}"); }
    }

    /// <summary>For the diagnostics.</summary>
    private string UpdateStatus()
    {
        if (!_cfg.CheckUpdates) return "desligadas";
        var last = _updater.LastCheck == default ? "ainda não procurou" : $"procurou às {_updater.LastCheck.ToLocalTime():HH:mm}";
        return $"ligadas · {last}" + (_updater.Available is { } u ? $" · há a {u.Version.ToString(3)}" : "");
    }

    // ---- refresh -----------------------------------------------------------

    private async void Tick()
    {
        ReloadLimitsIfChanged();
        Ingest();
        Render();
        if (_cfg.AlwaysOnTop && !Topmost) Topmost = true;

        await CheckAlertsAsync();
        await CheckFormatAsync();

        await _hello.CheckAsync(_cfg, _limits);
        Ingest();
        CheckLimitState();
        Render();
    }

    /// <summary>Paid extra usage starting, and a used-up limit coming back.</summary>
    private void CheckLimitState()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var alerts = _history.PendingRenewals(now).Select(a => (a.Title, a.Text, Forms.ToolTipIcon.Info)).ToList();
        if (_overage.Check(_hello.Ping, now) is { } extra) alerts.Add((extra.Title, extra.Text, Forms.ToolTipIcon.Warning));
        foreach (var (title, text, icon) in alerts)
        {
            Log.Write($"Aviso de limite{(Quiet ? " [silenciado]" : "")}: {title} — {text}");
            if (Quiet) continue;
            Notify(title, text, icon, 8000, OpenSessions);
            if (_cfg.Alerts.Sound) System.Media.SystemSounds.Exclamation.Play();
        }
        CloseWeek(now);
    }

    // ---- the week ------------------------------------------------------------

    private readonly WeekLog _weeks = new();
    private WeekWindow? _weekWindow;
    private bool _closingWeek;

    /// <summary>
    /// When the first reading of a new week comes in, sums up the week that
    /// ended (while its readings are all still kept) and says how it went.
    /// </summary>
    private async void CloseWeek(long now)
    {
        // Checked every few seconds, so the cheap question first.
        if (_closingWeek || _weeks.Unsummed(_history.Samples, now) == 0) return;
        _closingWeek = true;
        try
        {
            // The use by project comes from the transcripts: read what is new first.
            await UsageLedger.Shared.RefreshAsync(TimeSpan.FromMinutes(1));
            if (_weeks.Close(_history.Samples.ToList(), UsageLedger.Shared.Summarize, now) is not { } week) return;
            var text = WeekReport.Message(week, _weeks.Before(week.ResetAt));
            Log.Write($"Semana fechada{(Quiet ? " [silenciado]" : "")}: {text}");
            if (Quiet) return;
            Notify("Resumo da semana", text, Forms.ToolTipIcon.Info, 10000, () => OpenWeek(showLast: true));
        }
        catch (Exception ex) { Log.Write($"Falha no resumo da semana: {ex.Message}"); }
        finally { _closingWeek = false; }
    }

    private void OnWeek(object sender, RoutedEventArgs e) => OpenWeek(showLast: false);

    /// <summary>The week running and the one before, in a popup above the widget.</summary>
    internal async void OpenWeek(bool showLast)
    {
        if (_weekWindow is not null) return;
        if (!IsVisible) Show();
        await UsageLedger.Shared.RefreshAsync(TimeSpan.FromSeconds(20));
        if (_weekWindow is not null) return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var samples = _history.Samples.ToList();
        var resetAt = samples.LastOrDefault(s => s.R7 > 0)?.R7 ?? 0;
        var current = resetAt > 0 ? WeekReport.Build(samples, resetAt, UsageLedger.Shared.Summarize(resetAt - 7 * 86400, now + 1), now) : null;
        var last = resetAt > 0 ? _weeks.Before(resetAt) : _weeks.Weeks.LastOrDefault();

        _weekWindow = new WeekWindow(this, current, last, _weeks, showLast);
        _weekWindow.Closed += (_, _) => _weekWindow = null;
        _weekWindow.Show();
        WindowFocus.BringToFront(new WindowInteropHelper(_weekWindow).EnsureHandle());
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
        var alerts = _history.PendingAlerts(_cfg.UsageAlerts);
        if (_history.PendingBudgetAlert(DateTime.Now, _cfg.UsageAlerts) is { } budget) alerts.Add(budget);
        foreach (var (title, text) in alerts)
        {
            Log.Write($"Aviso de uso{(Quiet ? " [silenciado]" : "")}: {title} — {text}");
            if (Quiet) continue;
            Notify(title, text, Forms.ToolTipIcon.Warning, 8000, OpenSessions);
            if (_cfg.Alerts.Sound) System.Media.SystemSounds.Exclamation.Play();
        }
    }

    private DailyBudget? TodayBudget() => _history.Budget(DateTime.Now, _cfg.UsageAlerts.BudgetWeekdaysOnly, _cfg.UsageAlerts.DayStartHour);

    /// <summary>The pace lines shown in the tooltip and the sessions panel, and today's share of the week.</summary>
    private string ForecastText()
    {
        var lines = new List<string>();
        if (_history.Describe(true) is { Length: > 0 } five) lines.Add("5h: " + five);
        if (_history.Describe(false) is { Length: > 0 } seven) lines.Add("7d: " + seven);
        if (UsageHistory.BudgetText(TodayBudget()) is { Length: > 0 } today) lines.Add(today);
        return string.Join("\n", lines);
    }

    /// <summary>For the sessions panel: each line, and whether it warns (a limit running out, or today over its share).</summary>
    private List<(string Line, bool Warn)> ForecastPanel()
    {
        var lines = new List<(string, bool)>();
        foreach (var (fiveHour, label) in new[] { (true, "5h: "), (false, "7d: ") })
            if (_history.Describe(fiveHour) is { Length: > 0 } pace) lines.Add((label + pace, pace.Contains("esgota")));
        var budget = TodayBudget();
        if (UsageHistory.BudgetText(budget) is { Length: > 0 } today) lines.Add((today, budget!.Over));
        return lines;
    }

    // ---- Claude Code's formats ---------------------------------------------

    private readonly FormatWatch _format = new();
    private DateTime _formatChecked;
    /// <summary>The Claude Code version last seen in a session file; kept when no session is open.</summary>
    private string _claudeVersion = "";

    /// <summary>Once a minute: does the widget still understand what Claude Code writes?</summary>
    private async Task CheckFormatAsync()
    {
        if (DateTime.UtcNow - _formatChecked < TimeSpan.FromMinutes(1)) return;
        _formatChecked = DateTime.UtcNow;
        try
        {
            var claudeDir = Path.GetDirectoryName(Paths.Dir)!;
            await UsageLedger.Shared.RefreshAsync(TimeSpan.FromMinutes(1));
            var (limitsAt, lost) = (_limits.UpdatedAt, _hello.LostLimits);
            var observations = await Task.Run(() => FormatWatch.Observe(claudeDir, limitsAt, lost));
            if (observations.Sessions.Version.Length > 0) _claudeVersion = observations.Sessions.Version;

            var announce = _format.Update(observations, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), _claudeVersion);
            if (announce.Count == 0) return;

            var text = FormatWatch.Message(announce, _claudeVersion);
            Log.Write($"Formato do Claude Code {_claudeVersion}{(Quiet ? " [silenciado]" : "")}: {string.Join(", ", announce.Select(p => p.Key))} — {text}");
            if (Quiet) return;
            // A click looks for a widget that understands the new format.
            Notify("O widget deixou de perceber o Claude Code", text, Forms.ToolTipIcon.Warning, 10000, UpdateFromMenu);
        }
        catch (Exception ex) { Log.Write($"Falha a verificar o formato do Claude Code: {ex.Message}"); }
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
                foreach (var alert in _alerts.Update(_cfg.Alerts, sessions))
                    Announce(alert, sessions.FirstOrDefault(s => s.SessionId == alert.SessionId));
            if (_cfg.ContextAlerts.Enabled)
                foreach (var (title, text, session) in _context.Update(_cfg.ContextAlerts, sessions))
                {
                    Log.Write($"Aviso de contexto{(Quiet ? " [silenciado]" : "")}: {title} — {text.Replace("\n", " — ")}");
                    if (Quiet) continue;
                    Notify(title, text, Forms.ToolTipIcon.Warning, 8000, () => GoTo(session.Pid, session.Name, session.SessionId));
                    if (_cfg.Alerts.Sound) System.Media.SystemSounds.Exclamation.Play();
                }
        }
        catch (Exception ex) { Log.Write($"Falha nos avisos: {ex.Message}"); }
        UpdatePulse();
    }

    private void Announce(Alert a, SessionInfo? session)
    {
        var input = a.Kind == AlertKind.Input;
        var text = SessionStatus.AlertText(a, session);
        Log.Write($"Aviso ({(input ? "precisa de ti" : "terminou")}){(Quiet ? " [silenciado]" : "")}: {a.Name} — {text}");
        if (Quiet) return;
        Notify(input ? "Claude precisa de ti" : "Claude terminou",
            $"{a.Name}\n{text}",
            input ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info, 6000,
            () => GoTo(a.Pid, a.Name, a.SessionId));
        if (_cfg.Alerts.Sound)
            (input ? System.Media.SystemSounds.Exclamation : System.Media.SystemSounds.Asterisk).Play();
    }

    /// <summary>Shows a notification; a click on it while it is on screen runs onClick.</summary>
    private void Notify(string title, string text, Forms.ToolTipIcon icon, int ms, Action? onClick)
    {
        // Notifications don't say which one was clicked, so keep only the
        // latest's action: it is the one on screen.
        _onNotificationClick = onClick;
        _tray.ShowBalloonTip(ms, title, text, icon);
    }

    /// <summary>Brings a session's window to the front, and counts its alert as seen.</summary>
    private void GoTo(int pid, string name, string sessionId)
    {
        var host = WindowFocus.Focus(pid, name);
        Log.Write(host is null ? $"Foco: janela da sessão \"{name}\" não encontrada." : $"Foco: sessão \"{name}\" em {host}.");
        if (host is null) { OpenSessions(); return; }
        _alerts.Ack(sessionId);
        UpdatePulse();
        Render();
    }

    /// <summary>
    /// The global shortcut: to the session waiting for you longest (input
    /// before finished tasks); pressed again, to the next one. With nobody
    /// waiting, it opens the sessions list.
    /// </summary>
    private void GoToWaiting()
    {
        var waiting = _alerts.Waiting();
        // The one already in front has been seen: move on to the next.
        var next = waiting.FirstOrDefault(a => !WindowFocus.IsForeground(a.Pid, a.Name));
        if (next is not null) GoTo(next.Pid, next.Name, next.SessionId);
        else if (waiting.Count == 0) OpenSessions();
    }

    /// <summary>Opens the sessions list in front, even when asked from outside the widget.</summary>
    private void OpenSessions()
    {
        if (!IsVisible) Show();
        ToggleSessions();
        if (_sessions is not null) WindowFocus.BringToFront(new WindowInteropHelper(_sessions).EnsureHandle());
    }

    // ---- global shortcut ---------------------------------------------------

    private const int HotkeyId = 0xC1A;
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            GoToWaiting();
        }
        return IntPtr.Zero;
    }

    /// <summary>(Re)registers the configured shortcut; says so once if another program has it.</summary>
    private void RegisterHotkey()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        UnregisterHotKey(hwnd, HotkeyId);
        GoToWaitingMenu.InputGestureText = "";
        _hotkeyState = "nenhum";
        if (string.IsNullOrWhiteSpace(_cfg.Hotkey)) return;

        if (!Hotkey.TryParse(_cfg.Hotkey, out var mods, out var vk))
        {
            _hotkeyState = $"\"{_cfg.Hotkey}\" (inválido)";
            Log.Write($"Atalho inválido nas definições: \"{_cfg.Hotkey}\".");
            return;
        }
        var name = Hotkey.Format(mods, vk);
        if (!RegisterHotKey(hwnd, HotkeyId, mods | MOD_NOREPEAT, vk))
        {
            _hotkeyState = $"{name} (ocupado por outro programa)";
            Log.Write($"Atalho {name} já está a ser usado por outro programa.");
            Notify("Claude Usage", $"O atalho {name} já está a ser usado por outro programa. Escolhe outro nas Definições.",
                Forms.ToolTipIcon.Warning, 6000, OpenSettings);
            return;
        }
        GoToWaitingMenu.InputGestureText = name;
        _hotkeyState = $"{name} (ativo)";
    }

    /// <summary>The shortcut and whether it works, for the diagnostics.</summary>
    private string _hotkeyState = "nenhum";

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

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
        // Near the top: the tray cuts its tooltip at 127 characters.
        if (_hello.Ping.Overage?.ActiveAt(DateTimeOffset.UtcNow.ToUnixTimeSeconds()) == true) tip += "\nUso extra pago: ativo";
        var waiting = _alerts.Waiting();
        if (waiting.Count > 0)
        {
            var oldest = waiting.Min(a => a.RaisedAt);
            var since = DateTime.UtcNow - oldest;
            tip += $"\nÀ tua espera: {waiting.Count}" + (since.TotalMinutes >= 1 ? $" (há {FormatLeft(since)})" : "");
        }
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
        // Windows opens its notifications in the bottom-right corner, right over
        // a widget there. Once, a widget left in that corner (the old default)
        // moves to their left; dragged back afterwards, it stays where it is put.
        if (!_cfg.ClearOfToasts)
        {
            if (Placement.InToastCorner(_cfg.Left, _cfg.Top, ActualWidth, ActualHeight, area))
                _cfg.Left = Placement.BesideToasts(ActualWidth, area);
            _cfg.ClearOfToasts = true;
            Json.Save(Paths.Config, _cfg);
        }
        var left = _cfg.Left ?? Placement.BesideToasts(ActualWidth, area);
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
        _sessions = new SessionsWindow(this, _cfg, _alerts, ForecastPanel, _history, () => _hello.Current(_limits));
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
    private void OnGoToWaiting(object sender, RoutedEventArgs e) => GoToWaiting();
    private void OnSettings(object sender, RoutedEventArgs e) => OpenSettings();
    private void OnHelloNow(object sender, RoutedEventArgs e) => HelloNow();
    private void OnCopyDiagnostics(object sender, RoutedEventArgs e) => CopyDiagnostics();
    private void OnHide(object sender, RoutedEventArgs e) => Hide();
    private void OnExit(object sender, RoutedEventArgs e) => Exit();

    /// <summary>From the settings: Claude Code's settings put back, then the files go once this process has exited.</summary>
    private void UninstallAndExit()
    {
        var dir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? Installer.DefaultDir;
        var steps = new List<string>();
        try { Installer.Uninstall(dir, steps.Add); }
        catch (Exception ex) { steps.Add($"Não deu para remover tudo: {ex.Message}"); }
        MessageBox.Show(string.Join("\n", steps), "Claude Usage Widget", MessageBoxButton.OK, MessageBoxImage.Information);
        Installer.RemoveFilesAfterExit(dir);
        Exit();
    }

    private void OpenSettings()
    {
        var dlg = new SettingsWindow(_cfg);
        if (dlg.ShowDialog() != true)
        {
            if (dlg.UninstallRequested) UninstallAndExit();
            return;
        }
        _cfg = dlg.Result;
        Json.Save(Paths.Config, _cfg);
        ApplyConfig();
        RegisterHotkey();
        Render();
    }

    private async void HelloNow()
    {
        if (_hello.Running) return;
        var task = _hello.SendAsync(_cfg);
        Render();
        var ok = await task;
        Render();
        Notify("Claude Usage",
            ok ? "Limites atualizados." : "Atualização falhou. Ver widget.log.",
            ok ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning, 4000, null);
    }

    private bool _diagnosing;

    /// <summary>
    /// Copies a report of what the widget sees here (no session names or
    /// messages), for whoever helps when something does not work. Also saved
    /// to a file, in case the clipboard is busy or the message goes by e-mail.
    /// </summary>
    private async void CopyDiagnostics()
    {
        if (_diagnosing) return;
        _diagnosing = true;
        try
        {
            var sessions = await Task.Run(SessionScanner.Scan);
            var text = await Diagnostics.BuildAsync(new Diagnostics.Live(
                _cfg, _limits, _hello.Ping, _hello.Running, _hello.Backoff, _hotkeyState, sessions, _alerts.Waiting().Count, UpdateStatus(), _format.Status(), WindowStatus()));
            Directory.CreateDirectory(Paths.Dir);
            File.WriteAllText(Diagnostics.ReportPath, text);

            var copied = false;
            // Another program can hold the clipboard for a moment.
            for (var i = 0; i < 5 && !copied; i++)
            {
                try { Clipboard.SetText(text); copied = true; }
                catch (ExternalException) { await Task.Delay(100); }
            }
            Log.Write($"Diagnóstico gerado{(copied ? " e copiado" : "")}.");
            Notify(copied ? "Diagnóstico copiado" : "Diagnóstico guardado",
                copied
                    ? "Cola-o (Ctrl+V) numa mensagem para quem te ajuda. Não leva nomes de sessões nem mensagens."
                    : $"Não deu para copiar. Está em {Diagnostics.ReportPath}.",
                Forms.ToolTipIcon.Info, 6000, null);
        }
        catch (Exception ex)
        {
            Log.Write($"Falha no diagnóstico: {ex}");
            Notify("Claude Usage", "Não deu para gerar o diagnóstico. Ver widget.log.", Forms.ToolTipIcon.Warning, 4000, null);
        }
        finally { _diagnosing = false; }
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
        menu.Items.Add("Repor posição do widget", null, (_, _) => ResetPosition());
        menu.Items.Add("Resumo da semana", null, (_, _) => OpenWeek(showLast: false));
        var goTo = new Forms.ToolStripMenuItem("Ir para a sessão à tua espera", null, (_, _) => GoToWaiting());
        menu.Items.Add(goTo);
        var quiet = new Forms.ToolStripMenuItem("Não incomodar");
        menu.Items.Add(quiet);
        var sound = new Forms.ToolStripMenuItem("Som dos avisos", null, (_, _) => ToggleSound());
        menu.Items.Add(sound);
        var update = new Forms.ToolStripMenuItem("Procurar versão nova", null, (_, _) => UpdateFromMenu());
        menu.Opening += (_, _) =>
        {
            FillQuietTray(quiet);
            sound.Checked = _cfg.Alerts.Sound;
            goTo.ShortcutKeyDisplayString = GoToWaitingMenu.InputGestureText;
            update.Text = UpdateLabel();
        };
        menu.Items.Add("Definições…", null, (_, _) => OpenSettings());
        menu.Items.Add("Atualizar agora", null, (_, _) => HelloNow());
        menu.Items.Add("Copiar diagnóstico", null, (_, _) => CopyDiagnostics());
        menu.Items.Add(update);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => Exit());

        _tray.ContextMenuStrip = menu;
        _tray.Icon = IconFor("ok");
        _tray.Text = "Claude Usage";
        _tray.Visible = true;
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ToggleVisible(); };
        // Only fires while the notification is on screen, not from the notification centre later.
        _tray.BalloonTipClicked += (_, _) =>
        {
            var click = _onNotificationClick;
            _onNotificationClick = null;
            click?.Invoke();
        };
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
