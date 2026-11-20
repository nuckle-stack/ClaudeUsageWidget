namespace ClaudeUsageWidget.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+C", Hotkey.ModControl | Hotkey.ModAlt, 0x43u)]
    [InlineData(" ctrl + alt + c ", Hotkey.ModControl | Hotkey.ModAlt, 0x43u)]
    [InlineData("Control+Shift+F9", Hotkey.ModControl | Hotkey.ModShift, 0x78u)]
    [InlineData("Win+Alt+7", Hotkey.ModWin | Hotkey.ModAlt, 0x37u)]
    [InlineData("F12", 0u, 0x7Bu)]   // a function key may stand alone
    public void Reads_valid_shortcuts(string text, uint mods, uint vk)
    {
        Assert.True(Hotkey.TryParse(text, out var m, out var v));
        Assert.Equal(mods, m);
        Assert.Equal(vk, v);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("C")]                // a bare letter would steal it from every program
    [InlineData("Ctrl+Alt")]         // no key
    [InlineData("Ctrl+C+V")]         // two keys
    [InlineData("Ctrl+Banana")]
    [InlineData("Ctrl+LeftShift")]   // a modifier is not the key
    [InlineData("Ctrl+65")]          // not "A"
    [InlineData("Ctrl++")]
    public void Rejects_the_rest(string? text)
    {
        Assert.False(Hotkey.TryParse(text, out _, out _));
    }

    [Theory]
    [InlineData("alt+ctrl+c", "Ctrl+Alt+C")]
    [InlineData("shift+control+f9", "Ctrl+Shift+F9")]
    [InlineData("win+alt+7", "Alt+Win+7")]
    public void Writes_it_back_the_usual_way(string text, string expected)
    {
        Assert.True(Hotkey.TryParse(text, out var m, out var v));
        Assert.Equal(expected, Hotkey.Format(m, v));
        // And what it writes reads back the same.
        Assert.True(Hotkey.TryParse(expected, out var m2, out var v2));
        Assert.Equal((m, v), (m2, v2));
    }
}
