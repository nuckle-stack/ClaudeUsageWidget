using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace ClaudeUsageWidget.Tests;

/// <summary>The icon next to the clock: the 5-hour percentage, or a dot.</summary>
public class TrayIconTests
{
    private static bool Near(Drawing.Color a, Drawing.Color b) =>
        a.A > 200 && Math.Abs(a.R - b.R) < 24 && Math.Abs(a.G - b.G) < 24 && Math.Abs(a.B - b.B) < 24;

    private static List<(int X, int Y)> Painted(Drawing.Bitmap bmp, Drawing.Color color)
    {
        var found = new List<(int, int)>();
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                if (Near(bmp.GetPixel(x, y), color)) found.Add((x, y));
        return found;
    }

    [Theory]
    [InlineData(4, "ok", 16)]
    [InlineData(42, "ok", 16)]
    [InlineData(75, "warn", 20)]
    [InlineData(100, "crit", 16)]
    [InlineData(100, "crit", 32)]
    public void The_number_is_drawn_in_its_level_s_colour_and_fits(int percent, string level, int size)
    {
        using var bmp = TrayIcon.Draw(percent, level, size);
        Assert.Equal(size, bmp.Width);
        Assert.Equal(size, bmp.Height);

        var digits = Painted(bmp, TrayIcon.ColorOf(level));
        Assert.True(digits.Count >= size, $"only {digits.Count} pixels of the digits");
        // Inside the square, clear of its edges, even "100" in 16 pixels.
        Assert.DoesNotContain(digits, p => p.X == 0 || p.Y == 0 || p.X == size - 1 || p.Y == size - 1);
        // On the dark square, whose corners are rounded off.
        Assert.True(Near(bmp.GetPixel(size / 2, 1), TrayIcon.Back) || Near(bmp.GetPixel(1, size / 2), TrayIcon.Back));
        Assert.Equal(0, bmp.GetPixel(0, 0).A);
    }

    [Fact]
    public void Without_a_number_it_is_the_dot()
    {
        using var bmp = TrayIcon.Draw(null, "warn", 16);
        Assert.True(Near(bmp.GetPixel(8, 8), TrayIcon.ColorOf("warn")));
        Assert.Equal(0, bmp.GetPixel(0, 0).A);
    }

    [Fact]
    public void A_variable_font_s_overlapping_strokes_are_filled()
    {
        // The 4's two strokes cross; a hole there reads as a broken digit.
        using var four = TrayIcon.Draw(4, "ok", 64);
        var painted = Painted(four, TrayIcon.ColorOf("ok"));
        var rows = painted.GroupBy(p => p.Y).Where(r => r.Count() >= 20).ToList();
        Assert.NotEmpty(rows);   // the crossbar, in one piece
    }

    [Fact]
    public void It_is_redrawn_only_when_the_state_changes()
    {
        using var tray = new Forms.NotifyIcon();
        var icon = new TrayIcon();
        icon.Show(tray, 42, "ok");
        var first = tray.Icon;
        icon.Show(tray, 42, "ok");
        Assert.Same(first, tray.Icon);
        icon.Show(tray, 43, "ok");
        Assert.NotSame(first, tray.Icon);
        icon.Show(tray, 43, "warn");
        Assert.NotNull(tray.Icon);
    }
}
