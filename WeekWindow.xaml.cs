using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ClaudeUsageWidget;

/// <summary>
/// What a week of the weekly limit went on: the week running, or the one
/// before it. A popup above the widget, like the sessions list; it closes when
/// it loses focus.
/// </summary>
public partial class WeekWindow : Window
{
    /// <summary>A label, a bar and a value: a day, or a project.</summary>
    public record BarRow(string Label, double BarWidth, Brush BarBrush, string Value, string Tip);

    private const double BarMax = 120;
    private const double TotalWidth = 294;
    private static readonly Brush Blue = Frozen("#60A5FA");
    private static readonly Brush Orange = Frozen("#FB923C");
    private static readonly Brush Bright = Frozen("#F2FFFFFF");
    private static readonly Brush Dim = Frozen("#80FFFFFF");

    private readonly Window _anchor;
    private readonly WeekSummary? _current;
    private readonly WeekSummary? _last;
    private readonly WeekLog _log;
    private bool _showLast;

    public WeekWindow(Window anchor, WeekSummary? current, WeekSummary? last, WeekLog log, bool showLast)
    {
        InitializeComponent();
        _anchor = anchor;
        _current = current;
        _last = last;
        _log = log;
        _showLast = showLast && last is not null;

        Loaded += (_, _) => { Place(); Activate(); };
        SizeChanged += (_, _) => Place();
        Deactivated += (_, _) => { try { Close(); } catch (InvalidOperationException) { } };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Render();
    }

    private void Render()
    {
        var week = _showLast ? _last : _current;
        TabNow.Foreground = _showLast ? Dim : Bright;
        TabLast.Foreground = _showLast ? Bright : Dim;
        TabLast.Visibility = _last is null ? Visibility.Collapsed : Visibility.Visible;

        var any = week is not null;
        foreach (var e in new UIElement[] { Total, Compare, (UIElement)TotalBar.Parent, DayList, SourceBox, ProjectBox, FiveHour })
            e.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        Empty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        Note.Visibility = Visibility.Collapsed;
        if (week is null)
        {
            Period.Text = "";
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Period.Text = $"{When(week.Start)} → {When(week.ResetAt)}" + (week.ResetAt > now ? " · em curso" : "");
        Total.Text = $"{WeekReport.Pct(week.Used)} do limite semanal";
        var before = _showLast ? _log.Before(week.ResetAt) : _last;
        Compare.Text = before is null ? "" : $"Semana anterior: {WeekReport.Pct(before.Used)}";
        Compare.Visibility = before is null ? Visibility.Collapsed : Visibility.Visible;
        TotalBar.Width = TotalWidth * Math.Clamp(week.Used, 0, 100) / 100;

        var heaviest = week.Heaviest;
        var top = Math.Max(1, week.Days.Count == 0 ? 1 : week.Days.Max(d => d.Percent));
        DayList.ItemsSource = week.Days.Select(d => new BarRow(
            WeekReport.DayLabel(d.Date), BarMax * d.Percent / top, d == heaviest ? Orange : Blue, WeekReport.Pct(d.Percent),
            $"{WeekReport.DayName(d.Date)}, {d.Date:dd/MM}: {WeekReport.Pct(d.Percent)} do limite semanal")).ToList();

        RenderSources(week, now);

        ProjectBox.Visibility = week.Projects.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ProjectList.ItemsSource = week.Projects.Select(p => new BarRow(
            p.Name, BarMax * p.Share, Blue, WeekReport.Pct(p.Share * 100), $"{p.Name}: {WeekReport.Pct(p.Share * 100)} do uso neste PC")).ToList();

        FiveHour.Text = week.FiveHourMaxed switch
        {
            0 => "A janela de 5h não esgotou.",
            1 => "A janela de 5h esgotou uma vez.",
            var n => $"A janela de 5h esgotou {n} vezes.",
        };
        if (week.Gaps)
        {
            Note.Text = "O widget esteve desligado no início da semana: o primeiro dia que viu inclui o uso de antes.";
            Note.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// /usage's breakdown, when it was opened that week; for the week running
    /// without one, how to get it. It only changes when /usage is opened.
    /// </summary>
    private void RenderSources(WeekSummary week, long now)
    {
        var running = week.ResetAt > now;
        SourceBox.Visibility = week.Sources.Count > 0 || running ? Visibility.Visible : Visibility.Collapsed;
        if (week.Sources.Count == 0)
        {
            SourceList.ItemsSource = null;
            SourceWhen.Text = "Abre o /usage no Claude Code para ver aqui quanto veio do claude.ai, do Cowork e de outros PCs.";
            return;
        }
        var top = Math.Max(1, week.Sources.Max(s => s.Points));
        SourceList.ItemsSource = week.Sources.Select(s => new BarRow(
            s.Name, BarMax * s.Points / top, Blue, WeekReport.Pct(s.Points),
            $"{s.Name}: {WeekReport.Pct(s.Points)} do limite semanal ({WeekReport.Pct(s.Share)} do uso da semana)")).ToList();
        SourceWhen.Text = $"Segundo o /usage de {When(week.SourcesAsOf)}." +
                          (running && now - week.SourcesAsOf > 3600 ? " Abre-o outra vez para atualizar." : "");
    }

    private static string When(long unix) =>
        DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime is var t ? $"{WeekReport.DayLabel(t)}/{t:MM} {t:HH:mm}" : "";

    private void OnTabNow(object sender, MouseButtonEventArgs e) { _showLast = false; Render(); }
    private void OnTabLast(object sender, MouseButtonEventArgs e) { _showLast = true; Render(); }

    /// <summary>Above the widget, right edges aligned; below it when there is no room.</summary>
    private void Place()
    {
        var left = _anchor.Left + _anchor.ActualWidth - ActualWidth;
        var top = _anchor.Top - ActualHeight - 6;
        if (top < SystemParameters.VirtualScreenTop) top = _anchor.Top + _anchor.ActualHeight + 6;
        Left = Math.Max(SystemParameters.VirtualScreenLeft, left);
        Top = top;
    }

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
