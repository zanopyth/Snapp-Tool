using System.Collections.Generic;
using System.Windows.Forms;

namespace SnapTool.Capture;

internal static class HotkeyUtil
{
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;

    public static string ToDisplayString(uint modifiers, Keys key)
    {
        var parts = new List<string>();
        if ((modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & MOD_WIN) != 0) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    /// <summary>Extracts a modifiers+key combo from a KeyEventArgs. Returns false while only modifier keys are held.</summary>
    public static bool TryFromKeyEvent(KeyEventArgs e, out uint modifiers, out Keys key)
    {
        modifiers = 0;
        key = Keys.None;

        var code = e.KeyCode;
        if (code is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin)
            return false;

        if (e.Control) modifiers |= MOD_CONTROL;
        if (e.Alt) modifiers |= MOD_ALT;
        if (e.Shift) modifiers |= MOD_SHIFT;

        if (modifiers == 0) return false;

        key = code;
        return true;
    }
}
