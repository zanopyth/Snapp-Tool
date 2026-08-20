using System.Drawing;
using System.Windows.Forms;

namespace SnapTool.Core;

internal static class TrayIcons
{
    private static Icon? _appIcon;

    /// <summary>The app's own compiled-in icon (set via ApplicationIcon in the csproj).</summary>
    public static Icon AppIcon =>
        _appIcon ??= Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
}
