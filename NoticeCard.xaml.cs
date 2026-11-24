using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ClaudeUsageWidget;

/// <summary>One notice, drawn like the widget. A click on it (or its button) acts; the ✕ closes it.</summary>
public partial class NoticeCard : UserControl
{
    public event Action? Activated;
    public event Action? Dismissed;

    private readonly double _percent;

    public NoticeCard(Notice notice)
    {
        InitializeComponent();
        var tone = Colour(notice.Tone);
        Stripe.Background = Brush(tone);
        Kind.Text = notice.Kind;
        Kind.Foreground = Brush(tone);
        Frame.BorderBrush = Brush(Color.FromArgb(0x50, tone.R, tone.G, tone.B));

        if (notice.Badge.Length > 0)
        {
            var (back, fore) = BadgeColours(notice.Badge);
            BadgeBox.Background = Brush(back);
            Badge.Foreground = Brush(fore);
            Badge.Text = notice.Badge;
        }
        else BadgeBox.Visibility = Visibility.Collapsed;

        Subject.Text = notice.Subject;
        Text.Text = notice.Text;
        Text.Visibility = notice.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (notice.Bar is { } bar)
        {
            BarBox.Visibility = Visibility.Visible;
            BarLabel.Text = bar.Label;
            BarValue.Text = bar.Value;
            BarTime.Text = bar.Time;
            // The percentage in the bar's colour, as on the widget.
            BarFill.Background = BarValue.Foreground = Brush(Colour(bar.Fill));
            _percent = Math.Clamp(bar.Percent, 0, 100);
            Track.SizeChanged += (_, _) => BarFill.Width = Track.ActualWidth * _percent / 100;
        }

        if (notice.Action.Length > 0)
        {
            ActionBox.Visibility = Visibility.Visible;
            ActionText.Text = notice.Action;
            ActionText.Foreground = Brush(tone);
        }
    }

    /// <summary>"agora", "há 5 min": a notice can wait a while for you.</summary>
    public void SetAge(TimeSpan age) => When.Text = NoticeQueue.Ago(age);

    /// <summary>The widget's own colours, as in the bars and the sessions list.</summary>
    internal static Color Colour(NoticeTone tone) => tone switch
    {
        NoticeTone.Done => Color.FromRgb(0x4A, 0xDE, 0x80),
        NoticeTone.Input => Color.FromRgb(0xFB, 0x92, 0x3C),
        NoticeTone.Warn => Color.FromRgb(0xFA, 0xCC, 0x15),
        NoticeTone.Crit => Color.FromRgb(0xF8, 0x71, 0x71),
        _ => Color.FromRgb(0x60, 0xA5, 0xFA),
    };

    /// <summary>As the sessions list colours each kind of session.</summary>
    private static (Color Back, Color Fore) BadgeColours(string kind) => kind switch
    {
        "CLI" => (Color.FromArgb(0x33, 0x60, 0xA5, 0xFA), Color.FromRgb(0x93, 0xC5, 0xFD)),
        "Desktop" => (Color.FromArgb(0x33, 0xC0, 0x84, 0xFC), Color.FromRgb(0xD8, 0xB4, 0xFE)),
        "SDK" => (Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF), Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)),
        _ => (Color.FromArgb(0x33, 0x34, 0xD3, 0x99), Color.FromRgb(0x6E, 0xE7, 0xB7)),
    };

    private static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private void OnBody(object sender, MouseButtonEventArgs e) => Activated?.Invoke();

    private void OnAction(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Activated?.Invoke();
    }

    private void OnClose(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Dismissed?.Invoke();
    }
}
