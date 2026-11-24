using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ClaudeUsageWidget;

/// <summary>
/// The card shown with the mouse over the widget: each limit with when it
/// renews, the pace, what is waiting for you and the widget's state, in the
/// widget's look (the tooltip's own panel comes from Theme.xaml).
/// </summary>
internal static class WidgetTip
{
    /// <summary>A limit's line: "5h", "45%" in its colour, "renova hoje às 15:40 (faltam 2h10)".</summary>
    public record Limit(string Label, string Value, Color Colour, string Detail);

    private static readonly Brush Title = Frozen(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Faint = Frozen(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Body = Frozen(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Bright = Frozen(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Quiet = Frozen(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));

    public static FrameworkElement Build(string corner, IReadOnlyList<Limit> limits,
        IReadOnlyList<(string Line, bool Warn)> pace, IReadOnlyList<(string Text, NoticeTone Tone)> state, string footer)
    {
        var panel = new StackPanel { MinWidth = 240 };

        var head = new Grid();
        head.Children.Add(new TextBlock { Text = "Claude Code", Foreground = Title, FontWeight = FontWeights.SemiBold });
        head.Children.Add(new TextBlock
        {
            Text = corner, Foreground = Faint, FontSize = 10, Margin = new Thickness(16, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(head);

        var rows = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        rows.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var limit in limits)
        {
            var row = rows.RowDefinitions.Count;
            rows.RowDefinitions.Add(new RowDefinition());
            var top = new Thickness(0, row == 0 ? 0 : 3, 0, 0);
            Add(rows, row, 0, new TextBlock { Text = limit.Label, Foreground = Title, FontWeight = FontWeights.SemiBold, Margin = top });
            Add(rows, row, 1, new TextBlock { Text = limit.Value, Foreground = Frozen(limit.Colour), FontWeight = FontWeights.SemiBold, Margin = top });
            Add(rows, row, 2, new TextBlock { Text = limit.Detail, Foreground = Body, Margin = top, TextWrapping = TextWrapping.Wrap });
        }
        panel.Children.Add(rows);

        if (pace.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = "Ritmo", Foreground = Title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 1) });
            foreach (var (line, warn) in pace)
                panel.Children.Add(Dotted(line, warn ? NoticeCard.Colour(NoticeTone.Input) : Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF), Body));
        }

        if (state.Count > 0)
        {
            var first = true;
            foreach (var (text, tone) in state)
            {
                var line = Dotted(text, NoticeCard.Colour(tone), Bright);
                if (first) line.Margin = new Thickness(0, 10, 0, 0);
                first = false;
                panel.Children.Add(line);
            }
        }

        if (footer.Length > 0)
            panel.Children.Add(new TextBlock { Text = footer, Foreground = Quiet, FontSize = 10, Margin = new Thickness(0, 10, 0, 0) });
        return panel;
    }

    /// <summary>A line with a dot in front, in the colour of what it says.</summary>
    private static Grid Dotted(string text, Color dot, Brush foreground)
    {
        var line = new Grid { Margin = new Thickness(0, 3, 0, 0) };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.Children.Add(new Ellipse
        {
            Width = 5, Height = 5, Fill = Frozen(dot), Margin = new Thickness(0, 5, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        });
        var block = new TextBlock { Text = text, Foreground = foreground, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(block, 1);
        line.Children.Add(block);
        return line;
    }

    private static void Add(Grid grid, int row, int column, UIElement child)
    {
        Grid.SetRow(child, row);
        Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
