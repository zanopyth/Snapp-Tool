using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using SnapTool.Rendering;

namespace SnapTool.Forms;

/// <summary>
/// A borderless, non-activating, semi-transparent owned window used for the editor's floating tool palette.
/// Uses Form.Opacity (a real layered-window alpha blend against whatever is behind it) rather than a plain
/// child Panel, since a Panel's semi-transparent fill can't see through to sibling controls in WinForms.
/// </summary>
internal sealed class FloatingToolbarForm : Form
{
    public int CornerRadius { get; set; } = 16;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_NOACTIVATE = 0x08000000;
            const int WS_EX_TOOLWINDOW = 0x00000080;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    public FloatingToolbarForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.SidebarBg;
        Opacity = 0.75;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    /// <summary>
    /// Sizes the form tightly around <paramref name="content"/> plus Padding. Form.AutoSize with
    /// AutoSizeMode.GrowAndShrink is unreliable for shrinking borderless forms in practice (it was
    /// leaving a large empty margin around the actual toolbar content), so this sizes explicitly instead.
    /// </summary>
    public void FitToContent(Control content)
    {
        content.Location = new Point(Padding.Left, Padding.Top);
        var preferred = content.PreferredSize;
        ClientSize = new Size(preferred.Width + Padding.Horizontal, preferred.Height + Padding.Vertical);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        UpdateRegion();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateRegion();
    }

    private void UpdateRegion()
    {
        if (Width > 0 && Height > 0)
        {
            using var path = Geometry.RoundedRect(new Rectangle(0, 0, Width, Height), CornerRadius);
            Region = new Region(path);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width <= 1 || Height <= 1) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Geometry.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius);
        using var border = new Pen(Theme.Border, 1f);
        e.Graphics.DrawPath(border, path);
    }
}
