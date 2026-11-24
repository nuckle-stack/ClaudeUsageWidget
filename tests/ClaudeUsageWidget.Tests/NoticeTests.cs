using System.Windows;

namespace ClaudeUsageWidget.Tests;

/// <summary>The widget's own notices: which stay, for how long, and how they draw.</summary>
public class NoticeTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_795_000_000);
    private static readonly TimeSpan Life = TimeSpan.FromSeconds(8);

    private static Notice N(string subject, string key = "") => new(NoticeTone.Done, "Terminou", subject, Key: key);

    [Fact]
    public void A_notice_with_the_same_key_takes_the_old_one_s_place()
    {
        var queue = new NoticeQueue();
        var (asked, _) = queue.Add(new(NoticeTone.Input, "Precisa de ti", "Loja", Key: "session:a"), null, Life, T0);
        queue.Add(N("Outra", "session:b"), null, Life, T0);

        // The same session finished: one notice for it, the newest.
        var (done, removed) = queue.Add(N("Loja", "session:a"), null, Life, T0);
        Assert.Same(asked, Assert.Single(removed));
        Assert.Equal(["Outra", "Loja"], queue.Entries.Select(e => e.Notice.Subject));
        Assert.Same(done, queue.Entries[^1]);

        // Without a key, nothing is replaced.
        queue.Add(N("Sem chave"), null, Life, T0);
        queue.Add(N("Sem chave"), null, Life, T0);
        Assert.Equal(4, queue.Entries.Count);
    }

    [Fact]
    public void At_most_four_show_and_the_oldest_makes_room()
    {
        var queue = new NoticeQueue();
        for (var i = 1; i <= 4; i++) queue.Add(N($"n{i}"), null, Life, T0);
        var (_, removed) = queue.Add(N("n5"), null, Life, T0);
        Assert.Equal("n1", Assert.Single(removed).Notice.Subject);
        Assert.Equal(["n2", "n3", "n4", "n5"], queue.Entries.Select(e => e.Notice.Subject));
    }

    [Fact]
    public void Time_only_runs_while_you_are_there()
    {
        var queue = new NoticeQueue();
        queue.Add(N("curto"), null, TimeSpan.FromSeconds(8), T0);
        queue.Add(N("longo"), null, TimeSpan.FromSeconds(20), T0);

        // Away (or pointing at them) for an hour: nothing goes.
        Assert.Empty(queue.Tick(TimeSpan.FromHours(1), counting: false));
        Assert.Equal(2, queue.Entries.Count);

        Assert.Empty(queue.Tick(TimeSpan.FromSeconds(7), counting: true));
        Assert.Equal("curto", Assert.Single(queue.Tick(TimeSpan.FromSeconds(1), counting: true)).Notice.Subject);
        Assert.Equal("longo", Assert.Single(queue.Entries).Notice.Subject);
    }

    [Fact]
    public void A_session_you_went_to_takes_its_notice_away()
    {
        var queue = new NoticeQueue();
        queue.Add(N("Loja", "session:a"), null, Life, T0);
        queue.Add(N("Outra", "session:b"), null, Life, T0);
        queue.Add(N("Semana", "week"), null, Life, T0);

        var gone = queue.Prune(key => key == "session:a");
        Assert.Equal("Loja", Assert.Single(gone).Notice.Subject);
        Assert.Equal(["Outra", "Semana"], queue.Entries.Select(e => e.Notice.Subject));
    }

    [Fact]
    public void What_wants_you_stays_longest()
    {
        Assert.True(NoticeQueue.Life(NoticeTone.Input) > NoticeQueue.Life(NoticeTone.Warn));
        Assert.True(NoticeQueue.Life(NoticeTone.Warn) > NoticeQueue.Life(NoticeTone.Done));
        Assert.Equal(NoticeQueue.Life(NoticeTone.Done), NoticeQueue.Life(NoticeTone.Info));
    }

    [Theory]
    [InlineData(20, "agora")]
    [InlineData(5 * 60 + 10, "há 5 min")]
    [InlineData(65 * 60, "há 1h05")]
    public void Says_how_long_ago(int seconds, string expected)
    {
        Assert.Equal(expected, NoticeQueue.Ago(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(5, null)]   // takes notifications
    [InlineData(1, null)]   // locked or screen saver: the notice waits there for you
    [InlineData(7, null)]
    [InlineData(2, "ecrã inteiro")]
    [InlineData(3, "jogo em ecrã inteiro")]
    [InlineData(4, "apresentação")]
    [InlineData(6, "horas de silêncio")]
    public void Windows_quiet_as_Windows_says_it(int state, string? reason)
    {
        Assert.Equal(reason, WindowsQuiet.ForState(state));
    }

    // ---- the cards ----------------------------------------------------------------------

    [Fact]
    public void Each_kind_of_notice_draws_with_its_parts()
    {
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                var plain = Draw(new(NoticeTone.Done, "Terminou", "Estado do projeto", "Tarefa de 4 min concluída.", "CLI"));
                Assert.Equal(320, plain.ActualWidth);
                Assert.Equal(Visibility.Collapsed, plain.BarBox.Visibility);
                Assert.Equal(Visibility.Collapsed, plain.ActionBox.Visibility);
                Assert.Equal("CLI", plain.Badge.Text);

                var limit = Draw(new(NoticeTone.Info, "Limite semanal", "Ainda tens 62% por usar", "Renova hoje às 18:00.",
                    Bar: new("7d", 38, "38%", "8h36", NoticeTone.Done), Action: "Ver semana"));
                Assert.Equal(Visibility.Visible, limit.BarBox.Visibility);
                Assert.Equal(limit.Track.ActualWidth * 0.38, limit.BarFill.Width, precision: 3);
                Assert.Equal("Ver semana", limit.ActionText.Text);
                Assert.Equal(Visibility.Collapsed, limit.BadgeBox.Visibility);
                Assert.True(limit.ActualHeight > plain.ActualHeight);

                var asked = Draw(new(NoticeTone.Input, "Precisa de ti", "Loja", "Pede autorização para correr git push.", "VS Code", Action: "Ir para a sessão"));
                var fired = 0;
                asked.Activated += () => fired++;
                asked.ActionBox.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0,
                    System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                Assert.Equal(1, fired);   // the button acts once, not again through the card behind it
            }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure is not null) throw failure;
    }

    private static NoticeCard Draw(Notice notice)
    {
        var card = new NoticeCard(notice);
        card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        card.Arrange(new Rect(card.DesiredSize));
        card.UpdateLayout();
        return card;
    }
}
