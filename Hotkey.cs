using System.Windows.Input;

namespace ClaudeUsageWidget;

/// <summary>Reads a shortcut written as text, e.g. "Ctrl+Alt+C", for RegisterHotKey.</summary>
public static class Hotkey
{
    public const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8;

    /// <summary>
    /// Modifiers and virtual-key code of a shortcut such as "Ctrl+Alt+C" or
    /// "Shift+F9". It needs one key, and at least one modifier unless the key
    /// is F1-F24: a bare letter would be taken from every other program.
    /// </summary>
    public static bool TryParse(string? text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        Key? key = null;
        foreach (var raw in (text ?? "").Split('+'))
        {
            var part = raw.Trim();
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModControl; continue;
                case "alt": modifiers |= ModAlt; continue;
                case "shift": modifiers |= ModShift; continue;
                case "win" or "windows": modifiers |= ModWin; continue;
            }
            if (key is not null || !TryKey(part, out var k)) return false;
            key = k;
        }
        if (key is not { } found) return false;
        var function = found is >= Key.F1 and <= Key.F24;
        if (modifiers == 0 && !function) return false;
        vk = (uint)KeyInterop.VirtualKeyFromKey(found);
        return vk != 0;
    }

    /// <summary>The same shortcut written the usual way, e.g. "ctrl + alt+c" -> "Ctrl+Alt+C".</summary>
    public static string Format(uint modifiers, uint vk)
    {
        var parts = new List<string>();
        if ((modifiers & ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & ModShift) != 0) parts.Add("Shift");
        if ((modifiers & ModWin) != 0) parts.Add("Win");
        var key = KeyInterop.KeyFromVirtualKey((int)vk);
        parts.Add(key is >= Key.D0 and <= Key.D9 ? ((int)(key - Key.D0)).ToString() : key.ToString());
        return string.Join("+", parts);
    }

    private static bool TryKey(string part, out Key key)
    {
        key = Key.None;
        if (part.Length == 0) return false;
        // "7" is Key.D7; Enum.TryParse would read it as the number 7, another key.
        if (part.Length == 1 && char.IsAsciiDigit(part[0])) { key = Key.D0 + (part[0] - '0'); return true; }
        if (part.All(char.IsAsciiDigit)) return false;
        if (!Enum.TryParse(part, ignoreCase: true, out key) || key == Key.None) return false;
        // Modifier keys on their own are not a key.
        return key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System);
    }
}
