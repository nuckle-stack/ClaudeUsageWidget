using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ClaudeUsageWidget;

/// <summary>
/// What a week of the weekly limit went on: the week running, the one before
/// it, or any of the weeks kept, picked from their list. A popup above the
/// widget, like the sessions list; it closes when it loses focus.
/// </summary>
public partial class WeekWindow : Window
{
    /// <summary>A label, a bar and a value: a day, or a project.</summary>
    public record BarRow(string Label, double BarWidth, Brush BarBrush, string Value, string Tip);

    /// <summary>A week in the list of weeks.</summary>
    public record WeekRow(WeekSummary Week, string Label, double BarWidth, Brush BarBrush, string Value, string Maxed, string Tip);

    private const double BarMax = 120;
    private const double TotalWidth = 294;
    private static readonly Brush Blue = Frozen("#60A5FA");
    private static readonly Brush Faint = Frozen("#8060A5FA");
    private static readonly Brush Orange = Frozen("#FB923C");
    private static readonly Brush Bright = Frozen("#F2FFFFFF");
    private static readonly Brush Dim = Frozen("#80FFFFFF");

    private readonly Window _anchor;
    private readonly WeekSummary? _current;
    private readonly WeekSummary? _last;
    private readonly WeekLog _log;
    /// <summary>When /usage was last opened (unix seconds), 0 if never.</summary>
    private readonly long _usageAt;
    /// <summary>The last /usage said this account gets no breakdown of the week.</summary>
    private readonly bool _withheld;
    /// <summary>The week shown, when not the list.</summary>
    private WeekSummary? _week;
    private bool _list;

    public WeekWindow(Window anchor, WeekSummary? current, WeekSummary? last, WeekLog log, bool showLast, long usageAt = 0, bool withheld = false)
    {
        InitializeComponent();
        _anchor = anchor;
        _current = current;
        _last = last;
        _log = log;
        _usageAt = usageAt;
        _withheld = withheld;
        _week = showLast && last is not null ? last : current;

        Loaded += (_, _) => { Place(); Activate(); };
        SizeChanged += (_, _) => Place();
        Deactivated += (_, _) => { try { Close(); } catch (InvalidOperationException) { } };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Render();
    }

    private static bool Same(WeekSummary? a, WeekSummary? b) =>
        a is not null && b is not null && Math.Abs(a.ResetAt - b.ResetAt) <= 3600;

    private void Render()
    {
        TabNow.Foreground = !_list && (Same(_week, _current) || (_week is null && _current is null)) ? Bright : Dim;
        TabLast.Foreground = !_list && Same(_week, _last) ? Bright : Dim;
        TabWeeks.Foreground = _list ? Bright : Dim;
        TabLast.Visibility = _last is null ? Visibility.Collapsed : Visibility.Visible;
        TabWeeks.Visibility = _log.Weeks.Count > 0 || _current is not null ? Visibility.Visible : Visibility.Collapsed;

        History.Visibility = _list ? Visibility.Visible : Visibility.Collapsed;
        if (_list)
        {
            Detail.Visibility = Empty.Visibility = Visibility.Collapsed;
            RenderWeeks();
            return;
        }

        Heading.Text = "Resumo da semana";
        var week = _week;
        Detail.Visibility = week is not null ? Visibility.Visible : Visibility.Collapsed;
        Empty.Visibility = week is not null ? Visibility.Collapsed : Visibility.Visible;
        Note.Visibility = Visibility.Collapsed;
        if (week is null)
        {
            Period.Text = "";
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Period.Text = $"{WeekReport.Moment(week.Start)} → {WeekReport.Moment(week.ResetAt)}" + (week.ResetAt > now ? " · em curso" : "");
        Total.Text = $"{WeekReport.Pct(week.Used)} do limite semanal";
        var before = _log.Before(week.ResetAt);
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
        SourceWhen.Text = WeekReport.SourcesNote(week, _usageAt, now, _withheld);
        SourceBox.Visibility = SourceWhen.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (week.Sources.Count == 0)
        {
            SourceList.ItemsSource = null;
            return;
        }
        var top = Math.Max(1, week.Sources.Max(s => s.Points));
        SourceList.ItemsSource = week.Sources.Select(s => new BarRow(
            s.Name, BarMax * s.Points / top, Blue, WeekReport.Pct(s.Points),
            $"{s.Name}: {WeekReport.Pct(s.Points)} do limite semanal ({WeekReport.Pct(s.Share)} do uso da semana)")).ToList();
    }

    /// <summary>
    /// Each week kept and the one running, newest first: its use of the
    /// weekly limit and how often the 5-hour window ran out. A click opens it.
    /// </summary>
    private void RenderWeeks()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var closed = _log.Weeks.Where(w => w.ResetAt <= now).ToList();
        var lines = WeekReport.History(closed, _current, now);

        Heading.Text = "Semanas";
        Period.Text = "Uso do limite semanal em cada semana";
        Average.Text = WeekReport.Average(closed);
        Average.Visibility = Average.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        WeekList.ItemsSource = lines.Select(l => new WeekRow(
            l.Week, l.Label, BarMax * Math.Clamp(l.Week.Used, 0, 100) / 100, l.Running ? Faint : Blue,
            WeekReport.Pct(l.Week.Used), l.Week.FiveHourMaxed > 0 ? $"{l.Week.FiveHourMaxed}×" : "", l.Tip)).ToList();

        var notes = new List<string>();
        if (lines.Any(l => l.Week.FiveHourMaxed > 0)) notes.Add("A laranja, as vezes em que a janela de 5h esgotou.");
        if (closed.Count < 2) notes.Add("Cada semana entra aqui quando acaba; o widget guarda as últimas 12.");
        if (lines.Count > 1) notes.Add("Clica numa semana para ver o resumo dela.");
        HistoryNote.Text = string.Join(" ", notes);
        HistoryNote.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTabNow(object sender, MouseButtonEventArgs e) { _week = _current; _list = false; Render(); }
    private void OnTabLast(object sender, MouseButtonEventArgs e) { _week = _last; _list = false; Render(); }
    private void OnTabWeeks(object sender, MouseButtonEventArgs e) { _list = true; Render(); }

    private void OnWeekRow(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: WeekRow row }) return;
        _week = row.Week;
        _list = false;
        Render();
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

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
