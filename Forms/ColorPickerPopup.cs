using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using SnapTool.Rendering;

namespace SnapTool.Forms;

/// <summary>Small borderless popup showing preset colors, anchored under the toolbar's color button.</summary>
internal sealed class ColorPickerPopup : Form
{
    public event Action<Color>? ColorPicked;

    private static readonly Color[] Presets =
    {
        Color.FromArgb(239, 68, 68),   // red
        Color.FromArgb(249, 115, 22),  // orange
        Color.FromArgb(250, 204, 21),  // yellow
        Color.FromArgb(34, 197, 94),   // green
        Color.FromArgb(20, 184, 166),  // teal
        Color.FromArgb(56, 189, 248),  // sky
        Color.FromArgb(59, 130, 246),  // blue
        Color.FromArgb(168, 85, 247),  // purple
        Color.Black,
        Color.White
    };

    public ColorPickerPopup(Color current)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Theme.CardBg;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(1);

        var inner = new Panel { BackColor = Theme.CardBg, AutoSize = true, Padding = new Padding(10, 10, 10, 6) };

        var grid = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoSize = true,
            MaximumSize = new Size(150, 0)
        };
        foreach (var c in Presets) grid.Controls.Add(BuildSwatch(c, current));

        var moreBtn = new Label
        {
            Text = "More colors...",
            AutoSize = true,
            ForeColor = Theme.TextSecondary,
            Font = new Font("Segoe UI", 8f),
            Cursor = Cursors.Hand,
            Margin = new Padding(2, 8, 0, 0)
        };
        moreBtn.Click += (_, _) =>
        {
            using var dlg = new ColorDialog { Color = current };
            if (dlg.ShowDialog() == DialogResult.OK) ColorPicked?.Invoke(dlg.Color);
            Close();
        };

        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
        stack.Controls.Add(grid);
        stack.Controls.Add(moreBtn);

        inner.Controls.Add(stack);
        Controls.Add(inner);

        Deactivate += (_, _) => Close();
    }

    private Panel BuildSwatch(Color color, Color current)
    {
        const int size = 26;
        var panel = new Panel { Size = new Size(size, size), Margin = new Padding(3), Cursor = Cursors.Hand };
        panel.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            e.Graphics.FillEllipse(brush, 1, 1, size - 2, size - 2);
            bool selected = color.ToArgb() == current.ToArgb();
            using var pen = new Pen(selected ? Color.White : Color.FromArgb(90, 255, 255, 255), selected ? 2.2f : 1f);
            e.Graphics.DrawEllipse(pen, 1, 1, size - 2, size - 2);
        };
        panel.Click += (_, _) =>
        {
            ColorPicked?.Invoke(color);
            Close();
        };
        return panel;
    }

    public void ShowAt(Point screenLocation)
    {
        Location = screenLocation;
        Show();
        Activate();
    }
}
