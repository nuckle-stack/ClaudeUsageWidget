using System.Windows;

namespace ClaudeUsageWidget.Tests;

/// <summary>Keeping the widget out from under Windows' notifications.</summary>
public class PlacementTests
{
    // A 1920x1080 screen with a 42-pixel taskbar; the widget is 199 x 48.
    private static readonly Rect Area = new(0, 0, 1920, 1038);
    private const double W = 199, H = 48;

    [Fact]
    public void By_default_it_sits_left_of_the_notifications()
    {
        var left = Placement.BesideToasts(W, Area);
        Assert.Equal(1920 - 380 - 199 - 16, left);
        Assert.False(Placement.InToastCorner(left, Area.Bottom - H - 16, W, H, Area));
    }

    [Theory]
    [InlineData(1704, 974, true)]    // the old default corner
    [InlineData(1600, 974, true)]    // partly in it
    [InlineData(1704, 300, false)]   // up the right edge: notifications don't reach there
    [InlineData(100, 974, false)]    // bottom left
    public void Only_a_widget_where_notifications_open_is_moved(double left, double top, bool moved)
    {
        Assert.Equal(moved, Placement.InToastCorner(left, top, W, H, Area));
    }

    [Fact]
    public void Without_a_saved_position_there_is_nothing_to_move()
    {
        Assert.False(Placement.InToastCorner(null, null, W, H, Area));
    }

    [Fact]
    public void A_narrow_screen_keeps_it_on_screen()
    {
        Assert.Equal(16, Placement.BesideToasts(W, new Rect(0, 0, 500, 700)));
    }
}
