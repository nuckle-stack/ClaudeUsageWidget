using System.Runtime.InteropServices;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace ClaudeUsageWidget;

/// <summary>
/// The icon next to the clock. It carries the 5-hour window's percentage in
/// the level's colour, so the widget can stay hidden and the number still be
/// seen; without a reading (or with the number turned off) it is a coloured dot.
/// </summary>
public sealed class TrayIcon
{
    private string _key = "";
    private Drawing.Icon? _icon;

    /// <summary>Puts this state on the tray icon; redraws only when it changed.</summary>
    public void Show(Forms.NotifyIcon tray, int? percent, string level)
    {
        // Drawn at the size the taskbar shows it (16 px at 100% scaling): a
        // bigger icon scaled down blurs the digits.
        var size = Math.Clamp(Forms.SystemInformation.SmallIconSize.Width, 16, 64);
        var key = $"{percent}|{level}|{size}";
        if (key == _key && _icon is not null) return;

        using var bmp = Draw(percent, level, size);
        var icon = Drawing.Icon.FromHandle(bmp.GetHicon());
        tray.Icon = icon;
        // The shell keeps its own copy once the icon is set, so the old handle
        // can go: a new number every few minutes would otherwise pile them up.
        if (_icon is not null) DestroyIcon(_icon.Handle);
        _icon = icon;
        _key = key;
    }

    public static Drawing.Color ColorOf(string level) => level switch
    {
        "crit" => Drawing.Color.FromArgb(248, 113, 113),
        "warn" => Drawing.Color.FromArgb(250, 204, 21),
        _ => Drawing.Color.FromArgb(74, 222, 128),
    };

    internal static readonly Drawing.Color Back = Drawing.Color.FromArgb(21, 21, 26);

    /// <summary>The picture: the number on a dark square, or the dot on a dark circle.</summary>
    public static Drawing.Bitmap Draw(int? percent, string level, int size)
    {
        var bmp = new Drawing.Bitmap(size, size, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Drawing.Color.Transparent);
        using var back = new Drawing.SolidBrush(Back);
        using var fore = new Drawing.SolidBrush(ColorOf(level));

        if (percent is not int p)
        {
            g.FillEllipse(back, 0, 0, size - 1, size - 1);
            g.FillEllipse(fore, size / 4f, size / 4f, size / 2f, size / 2f);
            return bmp;
        }

        using (var square = Rounded(size, size / 5f)) g.FillPath(back, square);

        // Winding: a variable font draws a glyph from overlapping outlines (the
        // two strokes of a 4), which the default fill would punch a hole in.
        using var text = new Drawing.Drawing2D.GraphicsPath(Drawing.Drawing2D.FillMode.Winding);
        var (family, style) = Font();
        using (family)
            text.AddString(Math.Clamp(p, 0, 100).ToString(), family, (int)style, size,
                Drawing.PointF.Empty, Drawing.StringFormat.GenericTypographic);
        var bounds = text.GetBounds();
        if (bounds.Width <= 0 || bounds.Height <= 0) return bmp;

        // As big as fits: one digit fills the height, two or three the width.
        var margin = Math.Max(1f, size / 16f);
        var scale = Math.Min((size - 2 * margin) / bounds.Width, size * 0.72f / bounds.Height);
        // Whole pixels for the height and the top-left corner keep the edges
        // sharp. Rounding down, so the width never grows past what fits.
        var height = MathF.Floor(bounds.Height * scale);
        scale = height / bounds.Height;
        var width = bounds.Width * scale;
        using var fit = new Drawing.Drawing2D.Matrix();
        fit.Translate(MathF.Round((size - width) / 2), MathF.Round((size - height) / 2));
        fit.Scale(scale, scale);
        fit.Translate(-bounds.X, -bounds.Y);
        text.Transform(fit);
        g.FillPath(fore, text);
        return bmp;
    }

    /// <summary>
    /// Narrow digits, so that two of them can be tall in 16 pixels: Bahnschrift
    /// (in Windows since 10) semi-condensed, else Segoe UI in bold.
    /// </summary>
    private static (Drawing.FontFamily Family, Drawing.FontStyle Style) Font()
    {
        foreach (var (name, style) in new[] { ("Bahnschrift SemiBold SemiConden", Drawing.FontStyle.Regular), ("Segoe UI", Drawing.FontStyle.Bold) })
        {
            try { return (new Drawing.FontFamily(name), style); }
            catch (ArgumentException) { }
        }
        return (new Drawing.FontFamily(Drawing.Text.GenericFontFamilies.SansSerif), Drawing.FontStyle.Bold);
    }

    private static Drawing.Drawing2D.GraphicsPath Rounded(int size, float radius)
    {
        var d = radius * 2;
        var path = new Drawing.Drawing2D.GraphicsPath();
        path.AddArc(0, 0, d, d, 180, 90);
        path.AddArc(size - d, 0, d, d, 270, 90);
        path.AddArc(size - d, size - d, d, d, 0, 90);
        path.AddArc(0, size - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
}
