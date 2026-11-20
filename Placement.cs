using System.Windows;

namespace ClaudeUsageWidget;

/// <summary>Where the widget sits by default: on the taskbar's edge, but not under the notifications.</summary>
public static class Placement
{
    /// <summary>
    /// How much of the screen's right edge Windows 11 notifications cover
    /// (they are 364 wide, plus a margin), and how high one rises above the
    /// taskbar. Both in device-independent pixels, which scale with the screen
    /// like the notifications do.
    /// </summary>
    public const double ToastWidth = 380;
    public const double ToastHeight = 220;
    private const double Margin = 16;

    /// <summary>Bottom-left of the notifications' lane.</summary>
    public static double BesideToasts(double width, Rect area) =>
        Math.Max(area.Left + Margin, area.Right - ToastWidth - width - Margin);

    /// <summary>Whether a saved position puts the widget, even partly, where notifications open.</summary>
    public static bool InToastCorner(double? left, double? top, double width, double height, Rect area) =>
        left is { } l && top is { } t &&
        l + width > area.Right - ToastWidth && t + height > area.Bottom - ToastHeight;
}
