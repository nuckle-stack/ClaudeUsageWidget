using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ClaudeUsageWidget;

/// <summary>
/// A small single-series line chart of a limit's usage (0-100%) over time.
/// The line drops to zero where the limit reset. Hovering shows a crosshair
/// and reports the nearest reading through <see cref="Hover"/>.
/// </summary>
public class Sparkline : FrameworkElement
{
    public record Point(long T, double Percent, long ResetsAt);

    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-PT");
    private static readonly Color Series = (Color)ColorConverter.ConvertFromString("#60A5FA");
    private static readonly Pen Line = Frozen(new Pen(new SolidColorBrush(Series), 2) { LineJoin = PenLineJoin.Round });
    private static readonly Brush Area = Frozen(new SolidColorBrush(Color.FromArgb(0x26, Series.R, Series.G, Series.B)));
    private static readonly Brush Dot = Frozen(new SolidColorBrush(Series));
    private static readonly Pen Ring = Frozen(new Pen(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#15151A")), 2));
    private static readonly Pen Grid = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x16, 255, 255, 255)), 1));
    private static readonly Pen Base = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x38, 255, 255, 255)), 1));
    private static readonly Pen Cross = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x50, 255, 255, 255)), 1));

    private List<Point> _points = new();
    private long _from, _to;
    private int _hover = -1;

    /// <summary>The reading under the pointer as text, or null when the pointer leaves.</summary>
    public event Action<string?>? Hover;

    public void Set(List<Point> points, long from, long to)
    {
        _points = points;
        _from = from;
        _to = Math.Max(to, from + 1);
        if (_hover >= points.Count) _hover = -1;
        InvalidateVisual();
    }

    private double X(long t) => Math.Clamp((double)(t - _from) / (_to - _from), 0, 1) * ActualWidth;
    private double Y(double percent) => ActualHeight - 1 - Math.Clamp(percent, 0, 100) / 100 * (ActualHeight - 2);

    protected override void OnRender(DrawingContext dc)
    {
        // Transparent fill so the whole area receives mouse events.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        foreach (var level in new[] { 50.0, 100.0 })
            dc.DrawLine(Grid, new System.Windows.Point(0, Y(level)), new System.Windows.Point(ActualWidth, Y(level)));
        dc.DrawLine(Base, new System.Windows.Point(0, Y(0)), new System.Windows.Point(ActualWidth, Y(0)));
        if (_points.Count == 0) return;

        // One run per limit window: within it the line connects the readings and
        // holds flat to the reset (or to now); the next window starts from zero.
        var runs = new List<List<System.Windows.Point>>();
        List<System.Windows.Point>? run = null;
        for (var i = 0; i < _points.Count; i++)
        {
            var p = _points[i];
            var fresh = i == 0 || Math.Abs(p.ResetsAt - _points[i - 1].ResetsAt) > 60;
            if (fresh)
            {
                run = new List<System.Windows.Point>();
                runs.Add(run);
                // A window that opened inside the chart starts at zero where the last one ended.
                if (i > 0) run.Add(new System.Windows.Point(X(Math.Min(_points[i - 1].ResetsAt, p.T)), Y(0)));
            }
            run!.Add(new System.Windows.Point(X(p.T), Y(p.Percent)));

            var last = i == _points.Count - 1;
            var ends = last || Math.Abs(_points[i + 1].ResetsAt - p.ResetsAt) > 60;
            if (ends)
            {
                var until = last ? Math.Min(_to, p.ResetsAt) : Math.Min(p.ResetsAt, _points[i + 1].T);
                if (until > p.T) run.Add(new System.Windows.Point(X(until), Y(p.Percent)));
            }
        }

        foreach (var points in runs.Where(r => r.Count > 0))
        {
            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                g.BeginFigure(new System.Windows.Point(points[0].X, Y(0)), true, true);
                foreach (var pt in points) g.LineTo(pt, false, false);
                g.LineTo(new System.Windows.Point(points[^1].X, Y(0)), false, false);
            }
            area.Freeze();
            dc.DrawGeometry(Area, null, area);

            var line = new StreamGeometry();
            using (var g = line.Open())
            {
                g.BeginFigure(points[0], false, false);
                g.PolyLineTo(points.Skip(1).ToList(), true, true);
            }
            line.Freeze();
            dc.DrawGeometry(null, Line, line);
        }

        if (_hover >= 0 && _hover < _points.Count)
        {
            var p = _points[_hover];
            var at = new System.Windows.Point(X(p.T), Y(p.Percent));
            dc.DrawLine(Cross, new System.Windows.Point(at.X, 0), new System.Windows.Point(at.X, ActualHeight));
            dc.DrawEllipse(Dot, Ring, at, 4, 4);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_points.Count == 0) return;
        var x = e.GetPosition(this).X;
        var nearest = Enumerable.Range(0, _points.Count).MinBy(i => Math.Abs(X(_points[i].T) - x));
        if (nearest == _hover) return;
        _hover = nearest;
        InvalidateVisual();
        var p = _points[nearest];
        var when = DateTimeOffset.FromUnixTimeSeconds(p.T).LocalDateTime;
        Hover?.Invoke($"{when.ToString("ddd HH:mm", Pt).Replace(".", "")} · {Math.Round(p.Percent)}%");
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = -1;
        InvalidateVisual();
        Hover?.Invoke(null);
    }

    private static T Frozen<T>(T value) where T : Freezable
    {
        value.Freeze();
        return value;
    }
}
