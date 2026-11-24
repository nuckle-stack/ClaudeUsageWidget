using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ClaudeUsageWidget;

/// <summary>
/// The widget's notices, stacked above it (below it when it sits at the top of
/// the screen), right edges aligned. Never takes the focus from what you are
/// typing in. Each notice leaves when its time is up, counted only while you
/// are at the PC and not pointing at it; a click acts and closes it, its ✕
/// just closes it.
/// </summary>
public partial class NoticeWindow : Window
{
    private const double Gap = 8;
    /// <summary>Room the stack needs above the widget; with less, it goes below.</summary>
    private const double RoomAbove = 320;
    /// <summary>No input for this long: you are away, and the notices wait for you.</summary>
    private static readonly TimeSpan Away = TimeSpan.FromSeconds(20);

    private readonly Window _anchor;
    private readonly NoticeQueue _queue = new();
    private readonly Dictionary<NoticeQueue.Entry, NoticeCard> _cards = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private DateTime _lastTick;
    private bool _below;

    public NoticeWindow(Window anchor)
    {
        InitializeComponent();
        _anchor = anchor;
        SourceInitialized += (_, _) => NoActivate();
        SizeChanged += (_, _) => Place();
        _timer.Tick += (_, _) => Tick();
    }

    public void Add(Notice notice, Action? onClick, TimeSpan life)
    {
        var (entry, pushedOff) = _queue.Add(notice, onClick, life, DateTimeOffset.UtcNow);
        foreach (var e in pushedOff) Withdraw(e, fade: false);

        var card = new NoticeCard(notice) { Margin = new Thickness(0, Gap / 2, 0, Gap / 2), Opacity = 0 };
        card.Activated += () =>
        {
            Withdraw(entry, fade: false);
            entry.OnClick?.Invoke();
        };
        card.Dismissed += () => Withdraw(entry, fade: true);
        _cards[entry] = card;

        _below = _anchor.Top - SystemParameters.VirtualScreenTop < RoomAbove;
        if (_below) Stack.Children.Insert(0, card);
        else Stack.Children.Add(card);
        if (!IsVisible)
        {
            Show();
            _lastTick = DateTime.UtcNow;
            _timer.Start();
        }
        Place();
        Fade(card, 1, null);
    }

    /// <summary>Takes away the notices whose key no longer applies (a session you went to yourself).</summary>
    public void Prune(Func<string, bool> stale)
    {
        foreach (var e in _queue.Prune(stale)) Withdraw(e, fade: true);
    }

    private void Tick()
    {
        var now = DateTime.UtcNow;
        var elapsed = now - _lastTick;
        _lastTick = now;
        var counting = !IsMouseOver && IdleTime() < Away;
        foreach (var e in _queue.Tick(elapsed, counting)) Withdraw(e, fade: true);
        foreach (var (e, card) in _cards) card.SetAge(DateTimeOffset.UtcNow - e.At);
    }

    private void Withdraw(NoticeQueue.Entry entry, bool fade)
    {
        _queue.Remove(entry);
        if (!_cards.Remove(entry, out var card)) return;
        card.IsHitTestVisible = false;
        if (fade) Fade(card, 0, () => Take(card));
        else Take(card);
    }

    private void Take(NoticeCard card)
    {
        Stack.Children.Remove(card);
        if (Stack.Children.Count > 0) return;
        _timer.Stop();
        Hide();
    }

    private static void Fade(UIElement e, double to, Action? then)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(to > 0 ? 150 : 220));
        if (then is not null) a.Completed += (_, _) => then();
        e.BeginAnimation(OpacityProperty, a);
    }

    private void Place()
    {
        if (!IsVisible) return;
        Left = Math.Max(SystemParameters.VirtualScreenLeft, _anchor.Left + _anchor.ActualWidth - ActualWidth);
        Top = _below ? _anchor.Top + _anchor.ActualHeight + 6 - Gap / 2 : _anchor.Top - ActualHeight - 6 + Gap / 2;
    }

    // ---- Windows ---------------------------------------------------------------

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>Clicks reach it, but it never becomes the active window, and it stays out of Alt+Tab.</summary>
    private void NoActivate()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    /// <summary>How long since the last key press or mouse move, anywhere.</summary>
    private static TimeSpan IdleTime()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds((uint)Environment.TickCount - info.Time) : TimeSpan.Zero;
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);
}
