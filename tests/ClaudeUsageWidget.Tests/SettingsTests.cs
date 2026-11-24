using System.Windows;
using Xunit;

namespace ClaudeUsageWidget.Tests;

/// <summary>
/// The settings window: the version line, and a wrong value taking you to
/// its tab. Never saves: saving would change the Windows start-up entry.
/// </summary>
public class SettingsTests
{
    [Fact]
    public void The_version_line_says_what_GitHub_had()
    {
        var at = new DateTime(2026, 11, 24, 14, 5, 0, DateTimeKind.Local).ToUniversalTime();
        Assert.Equal("A 1.12.0 já saiu: instala-a com o botão direito no widget.",
            SettingsWindow.VersionStatus("1.11.0", new Version(1, 12, 0), at, checking: true));
        // A version found before the search was turned off is still worth saying.
        Assert.Equal("A 1.12.0 já saiu: instala-a com o botão direito no widget.",
            SettingsWindow.VersionStatus("1.11.0", new Version(1, 12, 0), default, checking: false));
        Assert.Equal("A 1.11.0 é a mais recente (procurou às 14:05).",
            SettingsWindow.VersionStatus("1.11.0", null, at, checking: true));
        Assert.StartsWith("Procura um minuto depois", SettingsWindow.VersionStatus("1.11.0", null, default, checking: true));
        Assert.Equal("", SettingsWindow.VersionStatus("1.11.0", null, default, checking: false));
    }

    [Fact]
    public void The_window_reads_back_what_it_was_given_and_sends_you_to_a_wrong_value()
    {
        OnSta(() =>
        {
            var cfg = new Config { Opacity = 0.75, Hotkey = "Ctrl+Alt+C" };
            cfg.UsageAlerts.LeftoverAbove = 35;
            cfg.Alerts.StallMinutes = 20;
            var w = new SettingsWindow(cfg, new Version(1, 12, 0));

            Assert.Equal($"versão {Updater.Current.ToString(3)}", w.VersionNow.Text);
            Assert.Equal(" · há a 1.12.0", w.VersionNew.Text);
            Assert.Equal("75%", w.OpacityValue.Text);
            Assert.Equal(0, w.ShownTab());

            // Unchanged, it reads back the same.
            Assert.True(w.TryRead());
            Assert.Equal(35, w.Result.UsageAlerts.LeftoverAbove);
            Assert.Equal(20, w.Result.Alerts.StallMinutes);
            Assert.Equal(0.75, w.Result.Opacity, precision: 3);
            Assert.Equal("Ctrl+Alt+C", w.Result.Hotkey);
            Assert.Equal(cfg.UsageAlerts.Levels, w.Result.UsageAlerts.Levels);

            // A wrong value on another tab: that tab comes to the front, with the error.
            w.LeftoverAbove.Text = "120";
            Assert.False(w.TryRead());
            Assert.Equal(1, w.ShownTab());
            Assert.Equal(Visibility.Visible, w.Error.Visibility);
            Assert.Contains("reset semanal", w.Error.Text);

            w.LeftoverAbove.Text = "35";
            w.CritAt.Text = "60";   // red below yellow (70)
            Assert.False(w.TryRead());
            Assert.Equal(1, w.ShownTab());
            Assert.Contains("amarelo abaixo do vermelho", w.Error.Text);

            w.CritAt.Text = "90";
            w.Schedule.Text = "7h";
            Assert.False(w.TryRead());
            Assert.Equal(2, w.ShownTab());

            w.Schedule.Text = cfg.AutoHello.Schedule;
            w.HideIdle.Text = "-1";
            Assert.False(w.TryRead());
            Assert.Equal(3, w.ShownTab());
        });
    }

    [Fact]
    public void All_tabs_take_the_height_of_the_tallest()
    {
        OnSta(() =>
        {
            var w = new SettingsWindow(new Config());
            var root = (FrameworkElement)w.Content;
            var heights = new List<double>();
            for (var i = 0; i < 4; i++)
            {
                w.ShowTab(i);
                root.Measure(new Size(w.Width, double.PositiveInfinity));
                heights.Add(root.DesiredSize.Height);
            }
            Assert.All(heights, h => Assert.Equal(heights[0], h, precision: 1));
        });
    }

    private static void OnSta(Action test)
    {
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try { test(); }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure is not null) throw failure;
    }
}
