using System.Windows;
using Microsoft.Win32;

namespace ClaudeUsageWidget;

public partial class SettingsWindow : Window
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "ClaudeUsageWidget";

    private readonly Config _original;
    public Config Result { get; private set; }

    public SettingsWindow(Config cfg)
    {
        InitializeComponent();
        _original = cfg;
        Result = cfg;

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
        HotkeyBox.Text = cfg.Hotkey;
        Levels.Text = string.Join(", ", cfg.UsageAlerts.Levels);
        ForecastAlert.IsChecked = cfg.UsageAlerts.Forecast;
        BudgetAlert.IsChecked = cfg.UsageAlerts.DailyBudget;
        BudgetWeekdays.IsChecked = cfg.UsageAlerts.BudgetWeekdaysOnly;
        DayStartHour.Text = cfg.UsageAlerts.DayStartHour.ToString();
        ContextEnabled.IsChecked = cfg.ContextAlerts.Enabled;
        QualityBelow.Text = cfg.ContextAlerts.QualityBelow.ToString();
        FillAbove.Text = cfg.ContextAlerts.FillAbove.ToString();
        AutoStart.IsChecked = IsAutoStart();
        CheckUpdates.IsChecked = cfg.CheckUpdates;
        HideIdle.Text = cfg.HideIdleDesktopMinutes.ToString();

        HelloEnabled.Checked += (_, _) => SyncEnabled();
        HelloEnabled.Unchecked += (_, _) => SyncEnabled();
        SyncEnabled();
    }

    private void SyncEnabled() => HelloOptions.IsEnabled = HelloEnabled.IsChecked == true;

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!TimeSpan.TryParse(From.Text, out var from) || !TimeSpan.TryParse(To.Text, out var to) ||
            from.TotalHours >= 24 || to.TotalHours > 24)
        {
            Fail("Horas no formato HH:mm, por exemplo 08:00 e 20:00.");
            return;
        }
        var scheduled = ModeSchedule.IsChecked == true;
        var slots = AutoHello.ParseSchedule(Schedule.Text);
        // Check each entry on its own: "7:00" and "07:00" are the same time, not an error.
        var invalid = Schedule.Text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Any(part => AutoHello.ParseSchedule(part).Count == 0);
        if (invalid || (scheduled && slots.Count == 0))
        {
            Fail("Horas marcadas no formato HH:mm, separadas por vírgulas (ex. 07:00, 12:00, 17:00).");
            return;
        }
        if (!int.TryParse(WarnAt.Text, out var warn) || !int.TryParse(CritAt.Text, out var crit) ||
            warn < 1 || crit > 100 || warn >= crit)
        {
            Fail("Limiares entre 1 e 100, com o amarelo abaixo do vermelho.");
            return;
        }

        if (!int.TryParse(RefreshMinutes.Text, out var refresh) || refresh < 0 || refresh > 1440)
        {
            Fail("Intervalo de atualização entre 0 e 1440 minutos.");
            return;
        }

        if (!int.TryParse(MinTask.Text, out var minTask) || minTask < 0 ||
            !int.TryParse(Remind.Text, out var remind) || remind < 0 ||
            !int.TryParse(StallMinutes.Text, out var stall) || stall < 0 || stall > 1440)
        {
            Fail("Avisos: segundos e minutos têm de ser números a partir de 0 (tarefa sem avançar: até 1440).");
            return;
        }

        var hotkey = "";
        if (!string.IsNullOrWhiteSpace(HotkeyBox.Text))
        {
            if (!Hotkey.TryParse(HotkeyBox.Text, out var mods, out var vk))
            {
                Fail("Atalho: teclas modificadoras e uma tecla, por exemplo Ctrl+Alt+C ou Ctrl+Shift+F9 (vazio = sem atalho).");
                return;
            }
            hotkey = Hotkey.Format(mods, vk);
        }

        var levels = new List<int>();
        foreach (var part in Levels.Text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, out var level) || level < 1 || level > 100)
            {
                Fail("Avisos de uso: percentagens entre 1 e 100, separadas por vírgulas (ex. 80, 95).");
                return;
            }
            levels.Add(level);
        }

        if (!int.TryParse(DayStartHour.Text, out var dayStart) || dayStart < 0 || dayStart > 12)
        {
            Fail("O dia começa a uma hora entre 0 e 12 (ex. 4 para as 4:00).");
            return;
        }

        if (!int.TryParse(QualityBelow.Text, out var qualityBelow) || qualityBelow < 0 || qualityBelow > 100 ||
            !int.TryParse(FillAbove.Text, out var fillAbove) || fillAbove < 0 || fillAbove > 100)
        {
            Fail("Avisos de contexto: valores entre 0 e 100 (0 desliga esse aviso).");
            return;
        }

        if (!int.TryParse(HideIdle.Text, out var hideIdle) || hideIdle < 0 || hideIdle > 10080)
        {
            Fail("Esconder sessões do Desktop: minutos entre 0 e 10080 (0 = nunca).");
            return;
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
        };

        SetAutoStart(AutoStart.IsChecked == true);
        DialogResult = true;
    }

    /// <summary>Set when the user asked to remove the widget; the main window does it, then exits.</summary>
    public bool UninstallRequested { get; private set; }

    private void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                "Remover o Claude Usage Widget?\n\nO statusline e o aviso do Claude Code voltam ao que eram antes, e o widget, as definições e o histórico dele são apagados.",
                "Claude Usage Widget", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        UninstallRequested = true;
        DialogResult = false;
    }

    private void Fail(string message)
    {
        Error.Text = message;
        Error.Visibility = Visibility.Visible;
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
