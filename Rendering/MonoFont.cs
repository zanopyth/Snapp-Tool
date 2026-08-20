using System.Drawing.Text;
using System.Linq;

namespace SnapTool.Rendering;

/// <summary>Resolves the best available monospace font on this machine for the terminal-style main window.</summary>
internal static class MonoFont
{
    private static readonly string[] Candidates = { "Cascadia Mono", "Cascadia Code", "Consolas", "Courier New" };
    private static string? _resolved;

    public static string FamilyName
    {
        get
        {
            if (_resolved != null) return _resolved;

            using var installed = new InstalledFontCollection();
            var names = installed.Families.Select(f => f.Name).ToHashSet(System.StringComparer.OrdinalIgnoreCase);

            _resolved = Candidates.FirstOrDefault(c => names.Contains(c)) ?? "Consolas";
            return _resolved;
        }
    }
}
