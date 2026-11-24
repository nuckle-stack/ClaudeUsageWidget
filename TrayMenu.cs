using System.Runtime.InteropServices;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace ClaudeUsageWidget;

/// <summary>
/// The tray icon's menu (Windows Forms) drawn like the widget's own (WPF):
/// the dark panel with rounded corners, a row lit up under the mouse, an
/// icon on the left and a blue tick for what is on.
/// </summary>
internal static class TrayMenu
{
    internal static readonly Drawing.Color Panel = Drawing.Color.FromArgb(0x16, 0x16, 0x1B);
    internal static readonly Drawing.Color Lit = Drawing.Color.FromArgb(0x2D, 0x2D, 0x32);
    internal static readonly Drawing.Color Edge = Drawing.Color.FromArgb(0x3A, 0x3A, 0x40);
    internal static readonly Drawing.Color Text = Drawing.Color.FromArgb(0xE8, 0xE8, 0xEA);
    internal static readonly Drawing.Color Dim = Drawing.Color.FromArgb(0x9A, 0x9A, 0x9E);
    internal static readonly Drawing.Color Blue = Drawing.Color.FromArgb(0x60, 0xA5, 0xFA);

    /// <summary>The tick of Segoe MDL2 Assets (CheckMark), as in the widget's menu.</summary>
    private static readonly string Tick = ((char)0xE73E).ToString();

    /// <summary>Marks a menu that has the look already.</summary>
    private static readonly object Applied = new();

    /// <summary>Gives a menu (and the submenus added to it later) the widget's look.</summary>
    public static void Apply(Forms.ToolStripDropDown menu)
    {
        menu.Tag = Applied;
        menu.Renderer = new Renderer();
        menu.BackColor = Panel;
        menu.ForeColor = Text;
        menu.Font = new Drawing.Font("Segoe UI", 9f);
        menu.Padding = new Forms.Padding(4, 4, 4, 4);
        menu.HandleCreated += (_, _) => Round(menu.Handle);
        menu.ItemAdded += (_, e) => { if (e.Item is { } item) Item(item); };
        foreach (Forms.ToolStripItem item in menu.Items) Item(item);
    }

    /// <summary>A Segoe MDL2 Assets glyph drawn as the item's icon, by its code (as in the XAML menu: "E713").</summary>
    public static T WithIcon<T>(this T item, string code) where T : Forms.ToolStripItem
    {
        item.Tag = ((char)Convert.ToInt32(code, 16)).ToString();
        return item;
    }

    private static void Item(Forms.ToolStripItem item)
    {
        item.ForeColor = Text;
        if (item is not Forms.ToolStripMenuItem menuItem) return;
        menuItem.Padding = new Forms.Padding(0, 3, 0, 3);
        var drop = menuItem.DropDown;
        // Not by its renderer: a submenu borrows its parent's, but not its colours or corners.
        if (!ReferenceEquals(drop.Tag, Applied)) Apply(drop);
    }

    /// <summary>Windows 11 rounds the corners and draws the edge; older Windows ignore it.</summary>
    private static void Round(IntPtr handle)
    {
        try
        {
            var round = 3; // DWMWCP_ROUNDSMALL
            DwmSetWindowAttribute(handle, 33, ref round, sizeof(int));
            var edge = Edge.R | (Edge.G << 8) | (Edge.B << 16);
            DwmSetWindowAttribute(handle, 34, ref edge, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private sealed class Renderer() : Forms.ToolStripProfessionalRenderer(new Colours())
    {
        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e) =>
            e.Graphics.Clear(Panel);

        // The edge comes from Windows 11 (see Round); a square one would show past the rounded corners.
        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e) { }

        protected override void OnRenderImageMargin(Forms.ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var r = new Drawing.Rectangle(2, 0, e.Item.Width - 4, e.Item.Height);
            using var path = Rounded(r, 4);
            using var brush = new Drawing.SolidBrush(Lit);
            e.Graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.FillPath(brush, path);
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Drawing.Pen(Edge);
            var y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            var shortcut = e.Item is Forms.ToolStripMenuItem m && e.Text == m.ShortcutKeyDisplayString;
            e.TextColor = !e.Item.Enabled ? Drawing.Color.FromArgb(0x60, 0x60, 0x66) : shortcut ? Dim : Text;
            base.OnRenderItemText(e);
            if (shortcut || e.Item.Tag is not string glyph) return;
            using var font = new Drawing.Font("Segoe MDL2 Assets", 9.5f);
            var box = new Drawing.Rectangle(4, 0, 24, e.Item.Height);
            Forms.TextRenderer.DrawText(e.Graphics, glyph, font, box, e.Item.Selected ? Text : Dim,
                Forms.TextFormatFlags.HorizontalCenter | Forms.TextFormatFlags.VerticalCenter | Forms.TextFormatFlags.NoPadding);
        }

        // What is on: a blue tick on the right, where the icon stays on the left.
        protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
        {
            using var font = new Drawing.Font("Segoe MDL2 Assets", 9f);
            var box = new Drawing.Rectangle(e.Item.Width - 30, 0, 22, e.Item.Height);
            Forms.TextRenderer.DrawText(e.Graphics, Tick, font, box, Blue,
                Forms.TextFormatFlags.HorizontalCenter | Forms.TextFormatFlags.VerticalCenter | Forms.TextFormatFlags.NoPadding);
        }

        protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Dim;
            base.OnRenderArrow(e);
        }

        private static Drawing.Drawing2D.GraphicsPath Rounded(Drawing.Rectangle r, int radius)
        {
            var d = radius * 2;
            var path = new Drawing.Drawing2D.GraphicsPath();
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d - 1, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d - 1, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    /// <summary>For whatever the renderer above leaves to the professional one.</summary>
    private sealed class Colours : Forms.ProfessionalColorTable
    {
        public override Drawing.Color ToolStripDropDownBackground => Panel;
        public override Drawing.Color ImageMarginGradientBegin => Panel;
        public override Drawing.Color ImageMarginGradientMiddle => Panel;
        public override Drawing.Color ImageMarginGradientEnd => Panel;
        public override Drawing.Color MenuBorder => Edge;
        public override Drawing.Color MenuItemBorder => Lit;
        public override Drawing.Color MenuItemSelected => Lit;
        public override Drawing.Color SeparatorDark => Edge;
        public override Drawing.Color SeparatorLight => Edge;
        public override Drawing.Color CheckBackground => Panel;
        public override Drawing.Color CheckSelectedBackground => Lit;
        public override Drawing.Color CheckPressedBackground => Lit;
    }
}
