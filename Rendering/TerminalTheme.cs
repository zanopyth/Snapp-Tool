using System.Drawing;

namespace SnapTool.Rendering;

/// <summary>
/// Dark terminal/ratatui-style palette for the main window, modeled after Localhost Manager's look
/// (near-black background, monospace type, a single green accent for state/focus). Kept separate from
/// <see cref="Theme"/> (the editor's violet palette) since the two windows are intentionally styled
/// differently rather than sharing one accent.
/// </summary>
internal static class TerminalTheme
{
    public static readonly Color Background = Color.FromArgb(10, 10, 12);
    public static readonly Color PanelBg = Color.FromArgb(16, 16, 19);
    public static readonly Color Surface0 = Color.FromArgb(24, 25, 29);
    public static readonly Color Surface1 = Color.FromArgb(34, 35, 41);
    public static readonly Color Border = Color.FromArgb(42, 43, 49);

    public static readonly Color TextPrimary = Color.FromArgb(220, 222, 224);
    public static readonly Color TextMuted = Color.FromArgb(128, 132, 140);

    public static readonly Color Accent = Color.FromArgb(88, 226, 130);
    public static readonly Color AccentDim = Color.FromArgb(54, 128, 78);
    public static readonly Color AccentSoftBg = Color.FromArgb(40, 88, 226, 130);

    public static readonly Color Danger = Color.FromArgb(224, 108, 108);
}
