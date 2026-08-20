using System.Drawing.Text;
using System.Linq;

namespace SnapTool.Rendering;

/// <summary>Resolves the best available casual/handwritten-style font on this machine for text annotations.</summary>
internal static class HandFont
{
    private static readonly string[] Candidates = { "Segoe Print", "Comic Sans MS", "Segoe UI" };
    private static string? _resolved;

    public static string FamilyName
    {
        get
        {
            if (_resolved != null) return _resolved;

            using var installed = new InstalledFontCollection();
            var names = installed.Families.Select(f => f.Name).ToHashSet(System.StringComparer.OrdinalIgnoreCase);

            _resolved = Candidates.FirstOrDefault(c => names.Contains(c)) ?? "Segoe UI";
            return _resolved;
        }
    }
}
