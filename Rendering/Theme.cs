using System.Drawing;

namespace SnapTool.Rendering;

/// <summary>Shared color palette so the editor and main window read as one app.</summary>
internal static class Theme
{
    public static readonly Color SidebarBg = Color.FromArgb(28, 28, 32);
    public static readonly Color ContentBg = Color.FromArgb(38, 38, 43);
    public static readonly Color CardBg = Color.FromArgb(52, 52, 59);
    public static readonly Color Accent = Color.FromArgb(124, 58, 237);
    public static readonly Color AccentHover = Color.FromArgb(147, 90, 245);
    public static readonly Color AccentSoftBg = Color.FromArgb(70, 124, 58, 237);
    public static readonly Color TextPrimary = Color.FromArgb(235, 235, 240);
    public static readonly Color TextSecondary = Color.FromArgb(160, 160, 170);
    public static readonly Color Border = Color.FromArgb(58, 58, 65);
}
