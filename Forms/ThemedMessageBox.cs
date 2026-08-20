using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using SnapTool.Core;
using SnapTool.Rendering;

namespace SnapTool.Forms;

internal enum ThemedMessageBoxIcon { Info, Warning, Error }
internal enum ThemedMessageBoxButtons { OK, YesNo }

/// <summary>
/// Drop-in replacement for <see cref="MessageBox"/> that renders in the app's dark terminal theme
/// instead of the stock Windows dialog, while keeping the native title bar (same as <see cref="MainForm"/>).
/// </summary>
internal static class ThemedMessageBox
{
    public static DialogResult Show(IWin32Window owner, string message, string title,
        ThemedMessageBoxButtons buttons, ThemedMessageBoxIcon icon)
    {
        using var dlg = new ThemedMessageBoxForm(message, title, buttons, icon);
        return dlg.ShowDialog(owner);
    }
}

/// <summary>
/// Icon and message are drawn directly onto the form rather than via a <see cref="Label"/> — a plain
/// Label's rendered color isn't reliably under our control (Windows theming/accessibility settings can
/// substitute system colors for standard controls), which was showing up as off-theme text. Owner-drawing
/// it, same idiom as <see cref="HoverTip"/>, guarantees exactly <see cref="TerminalTheme.TextPrimary"/>.
/// </summary>
internal sealed class ThemedMessageBoxForm : Form
{
    private const int IconSize = 32;
    private const int Pad = 24;
    private const int IconTextGap = 20;
    private const int MaxTextWidth = 280;
    private const int ButtonGap = 8;

    private readonly ThemedMessageBoxIcon _icon;
    private readonly string _message;
    private readonly Font _messageFont;
    private readonly Rectangle _iconRect;
    private readonly Rectangle _textRect;

    public ThemedMessageBoxForm(string message, string title, ThemedMessageBoxButtons buttons, ThemedMessageBoxIcon icon)
    {
        _icon = icon;
        _message = message;
        _messageFont = new Font(MonoFont.FamilyName, 9.5f);

        FormBorderStyle = FormBorderStyle.FixedDialog;
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = TerminalTheme.Background;
        Icon = TrayIcons.AppIcon;
        Font = new Font(MonoFont.FamilyName, 9f);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        // Measure at the wrap width, but trust the returned width as-is rather than clamping it back
        // down to MaxTextWidth: an unbreakable token (e.g. a filename with no spaces) can legitimately
        // need more room than the wrap width once WordBreak gives up trying to split it, and clamping
        // here previously cut that overflow off instead of letting the dialog grow to fit it.
        var textSize = TextRenderer.MeasureText(message, _messageFont, new Size(MaxTextWidth, 0), TextFormatFlags.WordBreak | TextFormatFlags.Left);
        int textWidth = textSize.Width;

        _iconRect = new Rectangle(Pad, Pad, IconSize, IconSize);
        int textX = Pad + IconSize + IconTextGap;
        int textY = Math.Max(Pad, Pad + (IconSize - textSize.Height) / 2);
        _textRect = new Rectangle(textX, textY, textWidth, textSize.Height);

        int contentRight = textX + textWidth + Pad;
        int contentBottom = Math.Max(_iconRect.Bottom, _textRect.Bottom) + Pad;

        var dialogButtons = new List<Button>();
        if (buttons == ThemedMessageBoxButtons.YesNo)
        {
            var no = BuildButton("No", accent: false);
            no.DialogResult = DialogResult.No;
            var yes = BuildButton("Yes", accent: true);
            yes.DialogResult = DialogResult.Yes;
            dialogButtons.Add(no);
            dialogButtons.Add(yes);
            AcceptButton = yes;
            CancelButton = no;
        }
        else
        {
            var ok = BuildButton("OK", accent: true);
            ok.DialogResult = DialogResult.OK;
            dialogButtons.Add(ok);
            AcceptButton = ok;
            CancelButton = ok;
        }

        int totalButtonsWidth = 0, maxButtonHeight = 0;
        foreach (var b in dialogButtons)
        {
            totalButtonsWidth += b.PreferredSize.Width;
            maxButtonHeight = Math.Max(maxButtonHeight, b.PreferredSize.Height);
        }
        totalButtonsWidth += ButtonGap * (dialogButtons.Count - 1);

        int clientWidth = Math.Max(Math.Max(contentRight, 260), totalButtonsWidth + Pad * 2);

        // Buttons are listed left-to-right in display order (e.g. [No, Yes]); lay them out right-aligned
        // by walking the list backwards, growing the running x-cursor leftward from the right edge.
        int xCursor = clientWidth - Pad;
        for (int i = dialogButtons.Count - 1; i >= 0; i--)
        {
            var b = dialogButtons[i];
            xCursor -= b.PreferredSize.Width;
            b.Location = new Point(xCursor, contentBottom);
            xCursor -= ButtonGap;
            Controls.Add(b);
        }

        ClientSize = new Size(clientWidth, contentBottom + maxButtonHeight + Pad);
    }

    private Button BuildButton(string text, bool accent)
    {
        var btn = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16, 6, 16, 6),
            FlatStyle = FlatStyle.Flat,
            BackColor = TerminalTheme.Surface1,
            ForeColor = accent ? TerminalTheme.Accent : TerminalTheme.TextPrimary,
            Font = new Font(MonoFont.FamilyName, 9f, accent ? FontStyle.Bold : FontStyle.Regular),
            Cursor = Cursors.Hand,
        };
        btn.FlatAppearance.BorderSize = 1;
        btn.FlatAppearance.BorderColor = accent ? TerminalTheme.Accent : TerminalTheme.Border;
        btn.FlatAppearance.MouseOverBackColor = TerminalTheme.Surface0;
        return btn;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        DrawIcon(e.Graphics);
        TextRenderer.DrawText(e.Graphics, _message, _messageFont, _textRect, TerminalTheme.TextPrimary,
            TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.Top);
    }

    private void DrawIcon(Graphics g)
    {
        var accent = _icon == ThemedMessageBoxIcon.Info ? TerminalTheme.Accent : TerminalTheme.Danger;
        var rect = new Rectangle(_iconRect.X, _iconRect.Y, _iconRect.Width - 1, _iconRect.Height - 1);

        using var circle = new SolidBrush(Color.FromArgb(40, accent));
        g.FillEllipse(circle, rect);
        using var ring = new Pen(accent, 1.5f);
        g.DrawEllipse(ring, rect);

        using var glyphBrush = new SolidBrush(accent);
        using var glyphFont = new Font(MonoFont.FamilyName, 13f, FontStyle.Bold);
        string glyph = _icon switch
        {
            ThemedMessageBoxIcon.Info => "i",
            ThemedMessageBoxIcon.Error => "×",
            _ => "!",
        };
        var size = g.MeasureString(glyph, glyphFont);
        g.DrawString(glyph, glyphFont, glyphBrush,
            rect.X + (rect.Width - size.Width) / 2f, rect.Y + (rect.Height - size.Height) / 2f - 1);
    }
}
