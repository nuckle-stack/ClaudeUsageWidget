using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace ClaudeUsageWidget;

/// <summary>
/// The settings, drawn like the widget: a dark panel with a tab for each
/// part (notices, limits, auto-hello, the widget itself) and the version
/// installed in its heading.
/// </summary>
public partial class SettingsWindow : Window
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "ClaudeUsageWidget";

    private static readonly Brush Shown = Frozen("#F2FFFFFF");
    private static readonly Brush NotShown = Frozen("#80FFFFFF");
    private static readonly Brush Underline = Frozen("#60A5FA");

    private readonly Config _original;
    /// <summary>Where the tray icon was when the window opened: on the taskbar, in the "^", or unknown to Windows.</summary>
    private readonly bool? _pinned;
    public Config Result { get; private set; }

    /// <param name="newer">A newer version found on GitHub, if any.</param>
    /// <param name="lastCheck">When the widget last looked for one (UTC), default if not yet.</param>
    public SettingsWindow(Config cfg, Version? newer = null, DateTime lastCheck = default)
    {
        InitializeComponent();
        _original = cfg;
        Result = cfg;

        var now = Updater.Current.ToString(3);
        VersionNow.Text = $"versão {now}";
        VersionNew.Text = newer is null ? "" : $" · há a {newer.ToString(3)}";
        VersionText.ToolTip = $"Versão instalada: {Diagnostics.Version()}";
        VersionLine.Text = VersionStatus(now, newer, lastCheck, cfg.CheckUpdates);
        if (VersionLine.Text.Length == 0) VersionLine.Visibility = Visibility.Collapsed;

        HelloEnabled.IsChecked = cfg.AutoHello.Enabled;
        From.Text = cfg.AutoHello.From;
        To.Text = cfg.AutoHello.To;
        WeekdaysOnly.IsChecked = cfg.AutoHello.WeekdaysOnly;
        ModeSchedule.IsChecked = cfg.AutoHello.Mode == "schedule";
        ModeReset.IsChecked = cfg.AutoHello.Mode != "schedule";
        Schedule.Text = cfg.AutoHello.Schedule;
        Message.Text = cfg.AutoHello.Message;
        Model.Text = cfg.AutoHello.Model;
        OnTop.IsChecked = cfg.AlwaysOnTop;
        OpacitySlider.Value = cfg.Opacity;
        WarnAt.Text = cfg.WarnAt.ToString();
        CritAt.Text = cfg.CritAt.ToString();
        RefreshMinutes.Text = cfg.RefreshMinutes.ToString();
        AlertsEnabled.IsChecked = cfg.Alerts.Enabled;
        AlertsSound.IsChecked = cfg.Alerts.Sound;
        MinTask.Text = cfg.Alerts.MinTaskSeconds.ToString();
        Remind.Text = cfg.Alerts.RemindMinutes.ToString();
        StallMinutes.Text = cfg.Alerts.StallMinutes.ToString();
        TrayPercent.IsChecked = cfg.TrayPercent;
        _pinned = Environment.ProcessPath is { } exe ? TrayPin.IsPinned(exe) : null;
        TrayPinned.IsChecked = _pinned == true;
        if (_pinned is null)
        {
            // Windows writes the icon's entry a few seconds after it first shows; Windows 10 has none.
            TrayPinned.IsEnabled = false;
            TrayPinned.Opacity = 0.4;
            TrayPinned.ToolTip = "O Windows ainda não tem este ícone registado (fica uns segundos depois de o widget arrancar), ou esta versão do Windows não tem esta opção.";
        }
        HotkeyBox.Text = cfg.Hotkey;
        Levels.Text = string.Join(", ", cfg.UsageAlerts.Levels);
        ForecastAlert.IsChecked = cfg.UsageAlerts.Forecast;
        BudgetAlert.IsChecked = cfg.UsageAlerts.DailyBudget;
        BudgetWeekdays.IsChecked = cfg.UsageAlerts.BudgetWeekdaysOnly;
        DayStartHour.Text = cfg.UsageAlerts.DayStartHour.ToString();
        LeftoverAbove.Text = cfg.UsageAlerts.LeftoverAbove.ToString();
        ContextEnabled.IsChecked = cfg.ContextAlerts.Enabled;
        QualityBelow.Text = cfg.ContextAlerts.QualityBelow.ToString();
        FillAbove.Text = cfg.ContextAlerts.FillAbove.ToString();
        AutoStart.IsChecked = IsAutoStart();
        CheckUpdates.IsChecked = cfg.CheckUpdates;
        HideIdle.Text = cfg.HideIdleDesktopMinutes.ToString();

        HelloEnabled.Checked += (_, _) => SyncEnabled();
        HelloEnabled.Unchecked += (_, _) => SyncEnabled();
        SyncEnabled();
        OpacitySlider.ValueChanged += (_, _) => ShowOpacity();
        ShowOpacity();

        PreviewKeyDown += OnKey;
        ShowTab(0);
    }

    /// <summary>
    /// The line under "look for new versions": what GitHub had the last time
    /// the widget looked. Empty when it does not look and knows of nothing newer.
    /// </summary>
    internal static string VersionStatus(string now, Version? newer, DateTime lastCheck, bool checking)
    {
        if (newer is not null)
            return $"A {newer.ToString(3)} já saiu: instala-a com o botão direito no widget.";
        if (!checking) return "";
        return lastCheck == default
            ? "Procura um minuto depois de o widget arrancar, e depois de 6 em 6 horas."
            : $"A {now} é a mais recente (procurou às {lastCheck.ToLocalTime():HH:mm}).";
    }

    // ---- look ------------------------------------------------------------------------------

    private void SyncEnabled()
    {
        var on = HelloEnabled.IsChecked == true;
        HelloOptions.IsEnabled = on;
        // The texts don't grey out by themselves.
        HelloOptions.Opacity = on ? 1 : 0.4;
    }

    private void ShowOpacity() => OpacityValue.Text = $"{OpacitySlider.Value * 100:0}%";

    internal void ShowTab(int index)
    {
        for (var i = 0; i < Pages.Children.Count; i++)
        {
            var shown = i == index;
            // Hidden, not Collapsed: the tallest page keeps the window's height.
            Pages.Children[i].Visibility = shown ? Visibility.Visible : Visibility.Hidden;
            var tab = (Border)Tabs.Children[i];
            tab.BorderBrush = shown ? Underline : Brushes.Transparent;
            ((TextBlock)tab.Child).Foreground = shown ? Shown : NotShown;
        }
    }

    internal int ShownTab()
    {
        for (var i = 0; i < Pages.Children.Count; i++)
            if (Pages.Children[i].Visibility == Visibility.Visible) return i;
        return 0;
    }

    private void OnTab(object sender, MouseButtonEventArgs e) => ShowTab(Tabs.Children.IndexOf((UIElement)sender));

    /// <summary>Ctrl+Tab and Ctrl+PageDown go to the next tab, as in any tabbed window.</summary>
    private void OnKey(object sender, KeyEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        var step = e.Key switch
        {
            Key.Tab => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1,
            Key.PageDown => 1,
            Key.PageUp => -1,
            _ => 0,
        };
        if (step == 0) return;
        var count = Pages.Children.Count;
        ShowTab((ShownTab() + step + count) % count);
        e.Handled = true;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        // Not from the "x": the drag would take the click that closes.
        if (e.ButtonState == MouseButtonState.Pressed && !ReferenceEquals(e.OriginalSource, CloseGlyph)) DragMove();
    }

    private void OnClose(object sender, MouseButtonEventArgs e) => Close();

    private static Brush Frozen(string color)
    {
        var brush = (Brush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }

    // ---- save ------------------------------------------------------------------------------

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!TryRead()) return;
        SetAutoStart(AutoStart.IsChecked == true);
        if (_pinned is { } was && (TrayPinned.IsChecked == true) != was && Environment.ProcessPath is { } exe)
            TrayPin.Pin(exe, !was);
        DialogResult = true;
    }

    /// <summary>Reads the fields into Result; false, with the error shown and the field in front, when one is wrong.</summary>
    internal bool TryRead()
    {
        if (!TimeSpan.TryParse(From.Text, out var from) || !TimeSpan.TryParse(To.Text, out var to) ||
            from.TotalHours >= 24 || to.TotalHours > 24)
        {
            Fail("Horas no formato HH:mm, por exemplo 08:00 e 20:00.", TimeSpan.TryParse(From.Text, out var f) && f.TotalHours < 24 ? To : From);
            return false;
        }
        var scheduled = ModeSchedule.IsChecked == true;
        var slots = AutoHello.ParseSchedule(Schedule.Text);
        // Check each entry on its own: "7:00" and "07:00" are the same time, not an error.
        var invalid = Schedule.Text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Any(part => AutoHello.ParseSchedule(part).Count == 0);
        if (invalid || (scheduled && slots.Count == 0))
        {
            Fail("Horas marcadas no formato HH:mm, separadas por vírgulas (ex. 07:00, 12:00, 17:00).", Schedule);
            return false;
        }

        const string colours = "Cores das barras: entre 1 e 100, com o amarelo abaixo do vermelho.";
        if (!Int(WarnAt, 1, 99, out var warn)) { Fail(colours, WarnAt); return false; }
        if (!Int(CritAt, 2, 100, out var crit) || warn >= crit) { Fail(colours, CritAt); return false; }

        if (!Int(RefreshMinutes, 0, 1440, out var refresh))
        {
            Fail("Atualização dos limites: entre 0 e 1440 minutos.", RefreshMinutes);
            return false;
        }

        const string alerts = "Avisos: segundos e minutos têm de ser números a partir de 0 (tarefa sem avançar: até 1440).";
        if (!Int(MinTask, 0, int.MaxValue, out var minTask)) { Fail(alerts, MinTask); return false; }
        if (!Int(Remind, 0, int.MaxValue, out var remind)) { Fail(alerts, Remind); return false; }
        if (!Int(StallMinutes, 0, 1440, out var stall)) { Fail(alerts, StallMinutes); return false; }

        var hotkey = "";
        if (!string.IsNullOrWhiteSpace(HotkeyBox.Text))
        {
            if (!Hotkey.TryParse(HotkeyBox.Text, out var mods, out var vk))
            {
                Fail("Atalho: teclas modificadoras e uma tecla, por exemplo Ctrl+Alt+C ou Ctrl+Shift+F9 (vazio = sem atalho).", HotkeyBox);
                return false;
            }
            hotkey = Hotkey.Format(mods, vk);
        }

        var levels = new List<int>();
        foreach (var part in Levels.Text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, out var level) || level < 1 || level > 100)
            {
                Fail("Avisos de uso: percentagens entre 1 e 100, separadas por vírgulas (ex. 80, 95).", Levels);
                return false;
            }
            levels.Add(level);
        }

        if (!Int(DayStartHour, 0, 12, out var dayStart))
        {
            Fail("O dia começa a uma hora entre 0 e 12 (ex. 4 para as 4:00).", DayStartHour);
            return false;
        }

        if (!Int(LeftoverAbove, 0, 100, out var leftover))
        {
            Fail("Aviso antes do reset semanal: uma percentagem entre 0 e 100 (0 = nunca).", LeftoverAbove);
            return false;
        }

        const string context = "Avisos de contexto: valores entre 0 e 100 (0 desliga esse aviso).";
        if (!Int(QualityBelow, 0, 100, out var qualityBelow)) { Fail(context, QualityBelow); return false; }
        if (!Int(FillAbove, 0, 100, out var fillAbove)) { Fail(context, FillAbove); return false; }

        if (!Int(HideIdle, 0, 10080, out var hideIdle))
        {
            Fail("Esconder sessões do Desktop: minutos entre 0 e 10080 (0 = nunca).", HideIdle);
            return false;
        }

        Result = new Config
        {
            HiddenSessions = _original.HiddenSessions,
            HideIdleDesktopMinutes = hideIdle,
            ContextAlerts = new ContextAlertsConfig
            {
                Enabled = ContextEnabled.IsChecked == true,
                QualityBelow = qualityBelow,
                FillAbove = fillAbove,
            },
            UsageAlerts = new UsageAlertsConfig
            {
                Levels = levels.Distinct().Order().ToArray(),
                Forecast = ForecastAlert.IsChecked == true,
                DailyBudget = BudgetAlert.IsChecked == true,
                BudgetWeekdaysOnly = BudgetWeekdays.IsChecked == true,
                DayStartHour = dayStart,
                LeftoverAbove = leftover,
            },
            Alerts = new AlertsConfig
            {
                Enabled = AlertsEnabled.IsChecked == true,
                Sound = AlertsSound.IsChecked == true,
                MinTaskSeconds = minTask,
                RemindMinutes = remind,
                StallMinutes = stall,
            },
            RefreshMinutes = refresh,
            ShowSdkSessions = _original.ShowSdkSessions,
            QuietUntil = _original.QuietUntil,
            AutoHello = new AutoHelloConfig
            {
                Enabled = HelloEnabled.IsChecked == true,
                Mode = scheduled ? "schedule" : "reset",
                Schedule = string.Join(", ", slots.Select(s => s.ToString(@"hh\:mm"))),
                From = from.ToString(@"hh\:mm"),
                To = to.ToString(@"hh\:mm"),
                WeekdaysOnly = WeekdaysOnly.IsChecked == true,
                Message = string.IsNullOrWhiteSpace(Message.Text) ? "hello" : Message.Text.Trim(),
                Model = string.IsNullOrWhiteSpace(Model.Text) ? "haiku" : Model.Text.Trim(),
            },
            Opacity = OpacitySlider.Value,
            AlwaysOnTop = OnTop.IsChecked == true,
            TrayPercent = TrayPercent.IsChecked == true,
            WarnAt = warn,
            CritAt = crit,
            Left = _original.Left,
            Top = _original.Top,
            ClaudePath = _original.ClaudePath,
            Hotkey = hotkey,
            CheckUpdates = CheckUpdates.IsChecked == true,
            ClearOfToasts = _original.ClearOfToasts,
            TrayPinOffered = _original.TrayPinOffered,
        };

        return true;
    }

    /// <summary>Set when the user asked to remove the widget; the main window does it, then exits.</summary>
    public bool UninstallRequested { get; private set; }

    private void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (!DialogWindow.Ask(Dialog.Uninstall, this)) return;
        UninstallRequested = true;
        DialogResult = false;
    }

    private static bool Int(TextBox box, int min, int max, out int value) =>
        int.TryParse(box.Text.Trim(), out value) && value >= min && value <= max;

    /// <summary>Says what is wrong, and takes you to the field: its tab, the cursor in it.</summary>
    private void Fail(string message, TextBox field)
    {
        Error.Text = message;
        Error.Visibility = Visibility.Visible;
        for (DependencyObject? at = field; at is not null; at = LogicalTreeHelper.GetParent(at))
        {
            if (at is not UIElement element || Pages.Children.IndexOf(element) is not (>= 0 and var page)) continue;
            ShowTab(page);
            break;
        }
        field.Focus();
        field.SelectAll();
    }

    internal static bool IsAutoStart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunName) is string;
    }

    private static void SetAutoStart(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on && Environment.ProcessPath is { } exe) key.SetValue(RunName, $"\"{exe}\"");
            else if (!on) key.DeleteValue(RunName, false);
        }
        catch (Exception ex) { Log.Write($"Falha a alterar arranque automático: {ex.Message}"); }
    }
}
