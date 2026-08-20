using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using SnapTool.Rendering;

namespace SnapTool.Forms;

/// <summary>
/// Small corner popup shown after a capture when <see cref="Core.AfterCaptureAction.ShowPreviewToast"/>
/// is selected — a thumbnail of the just-taken screenshot that opens the editor on click, or auto-dismisses
/// after a few seconds if ignored. Uses the same non-activating floating-window idiom as
/// <see cref="HoverTip"/>/<see cref="FloatingToolbarForm"/> so it never steals focus from whatever
/// app the user was in when the capture finished.
/// </summary>
internal sealed class CapturePreviewToast : Form
{
    private const int ThumbSize = 140;
    private const int Pad = 10;
    private const int CaptionHeight = 24;

    private readonly Bitmap _fullImage;
    private readonly string? _savePath;
    private readonly Bitmap _thumbnail;
    private readonly System.Windows.Forms.Timer _autoCloseTimer = new() { Interval = 5000 };
    private bool _handedOff;
    private bool _hovering;

    public event Action<Bitmap, string?>? OpenRequested;

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

    public CapturePreviewToast(Bitmap fullImage, string? savePath)
    {
        _fullImage = fullImage;
        _savePath = savePath;
        _thumbnail = BuildThumbnail(fullImage);

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = TerminalTheme.PanelBg;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        ClientSize = new Size(ThumbSize + Pad * 2, ThumbSize + Pad * 2 + CaptionHeight);

        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(screen.Right - Width - 16, screen.Bottom - Height - 16);

        Click += (_, _) => Open();
        MouseEnter += (_, _) => { _hovering = true; _autoCloseTimer.Stop(); };
        MouseLeave += (_, _) => { _hovering = false; _autoCloseTimer.Start(); };
        _autoCloseTimer.Tick += (_, _) => { if (!_hovering) Close(); };
        Shown += (_, _) => _autoCloseTimer.Start();
    }

    private void Open()
    {
        _handedOff = true;
        OpenRequested?.Invoke(_fullImage, _savePath);
        Close();
    }

    private static Bitmap BuildThumbnail(Bitmap source)
    {
        double scale = Math.Min((double)ThumbSize / source.Width, (double)ThumbSize / source.Height);
        int w = Math.Max(1, (int)Math.Round(source.Width * scale));
        int h = Math.Max(1, (int)Math.Round(source.Height * scale));
        var thumb = new Bitmap(w, h);
        using (var g = Graphics.FromImage(thumb))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, 0, 0, w, h);
        }
        return thumb;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Geometry.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 8);
        using var bg = new SolidBrush(TerminalTheme.PanelBg);
        e.Graphics.FillPath(bg, path);
        using var border = new Pen(_hovering ? TerminalTheme.Accent : TerminalTheme.Border, 1.4f);
        e.Graphics.DrawPath(border, path);

        int thumbX = (Width - _thumbnail.Width) / 2;
        int thumbY = Pad + (ThumbSize - _thumbnail.Height) / 2;
        e.Graphics.DrawImage(_thumbnail, thumbX, thumbY);
        using var thumbBorder = new Pen(TerminalTheme.Border, 1f);
        e.Graphics.DrawRectangle(thumbBorder, thumbX, thumbY, _thumbnail.Width - 1, _thumbnail.Height - 1);

        using var font = new Font(MonoFont.FamilyName, 8f);
        using var textBrush = new SolidBrush(TerminalTheme.TextMuted);
        var text = "Click to open";
        var size = e.Graphics.MeasureString(text, font);
        e.Graphics.DrawString(text, font, textBrush, (Width - size.Width) / 2f, Height - CaptionHeight + 2);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _autoCloseTimer.Stop();
        _autoCloseTimer.Dispose();
        _thumbnail.Dispose();
        if (!_handedOff) _fullImage.Dispose();
        base.OnFormClosed(e);
    }
}
