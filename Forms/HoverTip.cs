using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using SnapTool.Rendering;

namespace SnapTool.Forms;

/// <summary>
/// Small themed hover-tooltip popup, used instead of the built-in <see cref="ToolTip"/> component.
/// The native tooltip does not reliably show over controls hosted in a WS_EX_NOACTIVATE floating
/// window (like <see cref="FloatingToolbarForm"/>), and otherwise renders as a plain system flyout
/// that clashes with the app's dark theme.
/// </summary>
internal sealed class HoverTip : Form
{
    private string _text = "";
    private static readonly Font TipFont = new("Segoe UI", 8.5f);

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

    public HoverTip()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.SidebarBg;
        // Without an owner, this floats as an unrelated top-level window with no guaranteed z-order
        // against the (also owned/NOACTIVATE) FloatingToolbarForm, so it could silently render behind
        // it. TopMost makes "hover always shows the hint" actually reliable instead of order-dependent.
        TopMost = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
    }

    public void ShowFor(Control target, string text)
    {
        _text = text;

        Size textSize;
        using (var g = CreateGraphics()) textSize = Size.Ceiling(g.MeasureString(text, TipFont));
        var size = new Size(textSize.Width + 16, textSize.Height + 10);
        ClientSize = size;

        var screen = Screen.FromControl(target).WorkingArea;
        var targetRect = new Rectangle(target.PointToScreen(Point.Empty), target.Size);

        int x = targetRect.X + (targetRect.Width - size.Width) / 2;
        int y = targetRect.Bottom + 8;
        if (y + size.Height > screen.Bottom) y = targetRect.Top - size.Height - 8;
        x = Math.Max(screen.Left + 4, Math.Min(x, screen.Right - size.Width - 4));

        Location = new Point(x, y);
        Invalidate();
        if (!Visible) Show();
    }

    public new void Hide()
    {
        if (Visible) base.Hide();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Geometry.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 6);
        using var bg = new SolidBrush(Theme.SidebarBg);
        e.Graphics.FillPath(bg, path);
        using var border = new Pen(Theme.Border, 1f);
        e.Graphics.DrawPath(border, path);
        using var textBrush = new SolidBrush(Theme.TextPrimary);
        e.Graphics.DrawString(_text, TipFont, textBrush, 8f, 5f);
    }
}
