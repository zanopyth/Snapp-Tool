using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using SnapTool.Rendering;

namespace SnapTool.Forms;

/// <summary>
/// Small floating "recording in progress" pill shown while a GIF region recording is active.
/// Non-activating (like <see cref="FloatingToolbarForm"/>) so it never steals focus from whatever
/// app is being recorded; positioned entirely outside the captured rectangle by the caller so it can
/// never end up in a captured frame.
/// </summary>
internal sealed class GifRecordingBar : Form
{
    private readonly Label _timeLabel;
    private readonly Panel _pauseBtn;
    private bool _dotOn = true;
    private bool _paused;

    public event Action? StopRequested;
    public event Action? PauseToggleRequested;

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

    public GifRecordingBar()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.SidebarBg;
        TopMost = true;
        SetStyle(ControlStyles.ResizeRedraw, true);

        var dotBlink = new System.Windows.Forms.Timer { Interval = 500 };
        dotBlink.Tick += (_, _) => { _dotOn = !_dotOn; Invalidate(); };
        dotBlink.Start();
        Disposed += (_, _) => dotBlink.Dispose();

        _timeLabel = new Label
        {
            Text = "0:00",
            ForeColor = Theme.TextPrimary,
            Font = new Font(MonoFont.FamilyName, 9.5f, FontStyle.Bold),
            AutoSize = true,
            BackColor = Color.Transparent,
            Location = new Point(24, 8)
        };
        Controls.Add(_timeLabel);

        _pauseBtn = BuildPill("Pause", Color.FromArgb(210, 148, 96, 0));
        _pauseBtn.Click += (_, _) => PauseToggleRequested?.Invoke();
        Controls.Add(_pauseBtn);

        var stopBtn = BuildPill("Stop", Color.FromArgb(210, 60, 24, 24));
        stopBtn.Click += (_, _) => StopRequested?.Invoke();
        Controls.Add(stopBtn);

        ClientSize = new Size(_timeLabel.Right + 12 + _pauseBtn.Width + 6 + stopBtn.Width + 8, 36);
        _pauseBtn.Location = new Point(ClientSize.Width - stopBtn.Width - 8 - 6 - _pauseBtn.Width, (ClientSize.Height - _pauseBtn.Height) / 2);
        stopBtn.Location = new Point(ClientSize.Width - stopBtn.Width - 8, (ClientSize.Height - stopBtn.Height) / 2);
    }

    private static Panel BuildPill(string text, Color bgColor)
    {
        var btn = new Panel { Size = new Size(58, 22), Cursor = Cursors.Hand, BackColor = Color.Transparent, Tag = text };
        btn.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = Geometry.RoundedRect(new Rectangle(0, 0, btn.Width - 1, btn.Height - 1), 6);
            using var bg = new SolidBrush(bgColor);
            e.Graphics.FillPath(bg, path);
            using var textBrush = new SolidBrush(Color.White);
            using var font = new Font(MonoFont.FamilyName, 8f, FontStyle.Bold);
            var label = (string)btn.Tag;
            var size = e.Graphics.MeasureString(label, font);
            e.Graphics.DrawString(label, font, textBrush, (btn.Width - size.Width) / 2f, (btn.Height - size.Height) / 2f);
        };
        return btn;
    }

    public void SetElapsed(TimeSpan elapsed) => _timeLabel.Text = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}";

    public void SetPaused(bool paused)
    {
        _paused = paused;
        _pauseBtn.Tag = paused ? "Resume" : "Pause";
        _pauseBtn.Invalidate();
        Invalidate();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        using var path = Geometry.RoundedRect(new Rectangle(0, 0, Width, Height), 12);
        Region = new Region(path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using var border = new Pen(Theme.Border, 1f);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        // Paused: a steady amber dot. Recording: a blinking red one.
        if (_paused || _dotOn)
        {
            var color = _paused ? Color.FromArgb(230, 234, 179, 8) : Color.FromArgb(230, 60, 24);
            using var dotBrush = new SolidBrush(color);
            e.Graphics.FillEllipse(dotBrush, 8, ClientSize.Height / 2f - 5, 10, 10);
        }
    }
}
