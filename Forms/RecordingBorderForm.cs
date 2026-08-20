using System;
using System.Drawing;
using System.Windows.Forms;

namespace SnapTool.Forms;

/// <summary>
/// A colored ring drawn just outside a screen rectangle to mark it as "being recorded". The window's
/// own region is the ring only — the inside is excluded entirely, not just painted transparent — so it
/// is structurally impossible for this indicator to ever appear in a captured frame regardless of
/// timing. Click-through (WS_EX_TRANSPARENT) so it never blocks interaction with whatever's underneath.
/// </summary>
internal sealed class RecordingBorderForm : Form
{
    private const int Thickness = 3;
    private static readonly Color RingColor = Color.FromArgb(230, 60, 24);

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_NOACTIVATE = 0x08000000;
            const int WS_EX_TOOLWINDOW = 0x00000080;
            const int WS_EX_TRANSPARENT = 0x00000020;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    public RecordingBorderForm(Rectangle captureBounds)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = RingColor;
        TopMost = true;
        Bounds = Rectangle.Inflate(captureBounds, Thickness, Thickness);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var outer = new Rectangle(0, 0, Width, Height);
        var inner = new Rectangle(Thickness, Thickness, Width - Thickness * 2, Height - Thickness * 2);
        var region = new Region(outer);
        region.Exclude(inner);
        Region = region;
    }
}
