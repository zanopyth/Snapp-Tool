using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using SnapTool.Core;
using SnapTool.Rendering;

namespace SnapTool.Forms;

/// <summary>
/// Minimal always-playing viewer for a saved .gif recording. The Editor only ever shows a flattened
/// first frame (it's a static annotation surface, not a player), so this is the one place in the app
/// that actually animates one back.
/// </summary>
internal sealed class GifPreviewForm : Form
{
    private readonly PictureBox _pictureBox;
    private readonly Panel _progressTrack;
    private readonly Label _timeLabel;
    private readonly Image _image;
    private readonly EventHandler _frameChangedHandler;

    private readonly int[] _frameDelaysMs;
    private readonly int _totalDurationMs;
    private int _currentFrameIndex;
    private int _elapsedMs;

    public GifPreviewForm(string path)
    {
        _image = Image.FromFile(path);
        _frameDelaysMs = ReadFrameDelays(_image);
        _totalDurationMs = _frameDelaysMs.Sum();

        Text = $"SnapTool - {Path.GetFileName(path)}";
        Icon = TrayIcons.AppIcon;
        BackColor = TerminalTheme.Background;
        StartPosition = FormStartPosition.CenterScreen;

        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        int w = Math.Min(_image.Width + 40, screen.Width - 80);
        int h = Math.Min(_image.Height + 40 + 28, screen.Height - 80);
        ClientSize = new Size(Math.Max(w, 240), Math.Max(h, 208));
        MinimumSize = SizeFromClientSize(new Size(240, 208));

        _pictureBox = new PictureBox
        {
            Dock = DockStyle.Fill,
            Image = _image,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = TerminalTheme.Background
        };

        var infoBar = new Panel { Dock = DockStyle.Bottom, Height = 28, BackColor = TerminalTheme.PanelBg };

        _progressTrack = new Panel { Dock = DockStyle.Bottom, Height = 4, BackColor = TerminalTheme.Surface1 };
        _progressTrack.Paint += (_, e) =>
        {
            if (_totalDurationMs <= 0) return;
            float frac = Math.Clamp((float)_elapsedMs / _totalDurationMs, 0f, 1f);
            using var fill = new SolidBrush(TerminalTheme.Accent);
            e.Graphics.FillRectangle(fill, 0, 0, _progressTrack.Width * frac, _progressTrack.Height);
        };
        infoBar.Controls.Add(_progressTrack);

        _timeLabel = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = TerminalTheme.TextMuted,
            Font = new Font(MonoFont.FamilyName, 8.5f),
            TextAlign = ContentAlignment.MiddleCenter,
            Text = $"{FormatMs(0)} / {FormatMs(_totalDurationMs)}"
        };
        infoBar.Controls.Add(_timeLabel);

        // Dock order matters: Fill first, Bottom last, so the bottom bar ends up flush against the true bottom edge.
        Controls.Add(_pictureBox);
        Controls.Add(infoBar);

        _frameChangedHandler = OnFrameChanged;
        ImageAnimator.Animate(_image, _frameChangedHandler);

        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    /// <summary>Per-frame delays (ms) from the GIF's own Graphic Control Extensions, so the bar tracks
    /// real playback speed rather than assuming a fixed frame rate.</summary>
    private static int[] ReadFrameDelays(Image image)
    {
        PropertyItem? item = null;
        try { item = image.GetPropertyItem(0x5100); } catch { /* not present on this image */ }

        if (item?.Value is { Length: >= 4 } bytes)
        {
            int count = bytes.Length / 4;
            var delays = new int[count];
            for (int i = 0; i < count; i++)
            {
                int centiseconds = BitConverter.ToInt32(bytes, i * 4);
                delays[i] = Math.Max(centiseconds * 10, 20);
            }
            return delays;
        }

        int frameCount = Math.Max(image.GetFrameCount(FrameDimension.Time), 1);
        var fallback = new int[frameCount];
        Array.Fill(fallback, 100);
        return fallback;
    }

    private void OnFrameChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        ImageAnimator.UpdateFrames(_image);

        if (_frameDelaysMs.Length > 0)
        {
            _currentFrameIndex = (_currentFrameIndex + 1) % _frameDelaysMs.Length;
            _elapsedMs = 0;
            for (int i = 0; i < _currentFrameIndex; i++) _elapsedMs += _frameDelaysMs[i];
            _timeLabel.Text = $"{FormatMs(_elapsedMs)} / {FormatMs(_totalDurationMs)}";
            _progressTrack.Invalidate();
        }

        _pictureBox.Invalidate();
    }

    private static string FormatMs(int ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms);
        return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        ImageAnimator.StopAnimate(_image, _frameChangedHandler);
        base.OnFormClosed(e);
        _image.Dispose();
    }
}
