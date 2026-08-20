using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using SnapTool.Capture;
using SnapTool.Core;
using SnapTool.Rendering;

namespace SnapTool.Forms;

internal sealed class MainForm : Form
{
    private const int HOTKEY_REGION = 1;
    private const int HOTKEY_FULLSCREEN = 2;
    private const int HOTKEY_GIF = 3;

    private enum HotkeyTarget { Region, Fullscreen, Gif }

    private readonly AppSettings _settings;
    private readonly HotkeyWindow _hotkeys;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolTip _hintTip = new();
    private bool _allowExit;
    private bool _capturing;

    private readonly Panel _contentHost;
    private readonly FlowLayoutPanel _historyFlow;
    private readonly TextBox _folderTextBox;
    private readonly TextBox _regionHotkeyBox;
    private readonly TextBox _fullscreenHotkeyBox;
    private readonly TextBox _gifHotkeyBox;
    private readonly Panel _regionCaptureBtn;
    private readonly Panel _fullscreenCaptureBtn;
    private readonly Panel _gifCaptureBtn;
    private readonly Label _statusLabel;

    private TextBox? _recordingBox;
    private HotkeyTarget _recordingTarget;

    private const int GifMaxSeconds = 60;
    private readonly List<Bitmap> _gifFrames = new();
    private readonly System.Windows.Forms.Timer _gifTimer = new() { Interval = 125 };
    private Rectangle _gifRegionBounds;
    private bool _gifPaused;
    private TimeSpan _gifAccumulatedElapsed;
    private DateTime _gifSegmentStartedAt;
    private GifRecordingBar? _gifBar;
    private RecordingBorderForm? _gifBorder;
    private CapturePreviewToast? _captureToast;

    private Panel? _activeNavButton;
    private Panel? _hoveredSideButton;
    private readonly Panel _capturesPanel;
    private readonly Panel _settingsPanel;
    private readonly Panel _capturesNavBtn;
    private readonly Panel _settingsNavBtn;
    private readonly Label _capturesNavLabel;

    private readonly HashSet<string> _selectedPaths = new(StringComparer.OrdinalIgnoreCase);
    private string? _selectionAnchorPath;
    private bool _marqueeActive;
    private Point _marqueeStart;
    private Point _marqueeEnd;

    public MainForm()
    {
        _settings = AppSettings.Load();
        Directory.CreateDirectory(_settings.ScreenshotsFolder);

        Text = "SnapTool";
        Icon = TrayIcons.AppIcon;
        BackColor = TerminalTheme.Background;
        MinimumSize = new Size(700, 480);
        Size = new Size(860, 580);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font(MonoFont.FamilyName, 9f);

        var menuBar = BuildMenuBar();
        var (sidePanel, regionBtn, fullscreenBtn, gifBtn, capturesNavBtn, settingsNavBtn, capturesNavLabel) = BuildSidePanel();
        _regionCaptureBtn = regionBtn;
        _fullscreenCaptureBtn = fullscreenBtn;
        _gifCaptureBtn = gifBtn;
        _capturesNavBtn = capturesNavBtn;
        _settingsNavBtn = settingsNavBtn;
        _capturesNavLabel = capturesNavLabel;
        var statusBar = BuildStatusBar(out _statusLabel);

        _contentHost = new Panel { Dock = DockStyle.Fill, BackColor = TerminalTheme.Background, Padding = new Padding(14) };

        (_capturesPanel, _historyFlow) = BuildCapturesPanel();
        (_settingsPanel, _folderTextBox, _regionHotkeyBox, _fullscreenHotkeyBox, _gifHotkeyBox) = BuildSettingsPanel();

        foreach (var p in new[] { _capturesPanel, _settingsPanel })
        {
            p.Dock = DockStyle.Fill;
            p.Visible = false;
            _contentHost.Controls.Add(p);
        }

        // Dock order matters: add Fill first, then Bottom/Left, then Top controls last
        // (the last-added Top control ends up flush against the true top edge).
        Controls.Add(_contentHost);
        Controls.Add(statusBar);
        Controls.Add(sidePanel);
        Controls.Add(menuBar);
        MainMenuStrip = menuBar;

        _hotkeys = new HotkeyWindow();
        _hotkeys.HotkeyPressed += id =>
        {
            if (id == HOTKEY_REGION) CaptureRegion();
            else if (id == HOTKEY_FULLSCREEN) CaptureFullScreen();
            else if (id == HOTKEY_GIF)
            {
                // Same shortcut starts a recording and, pressed again, stops it — the only way to end
                // one without touching the mouse, which matters since it's usually aimed at another app.
                if (_gifTimer.Enabled || _gifPaused) StopGifRecording();
                else RecordGifRegion();
            }
        };
        RegisterHotkeysFromSettings();

        _trayIcon = BuildTrayIcon();
        _gifTimer.Tick += GifTimer_Tick;

        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (_capturesPanel.Visible && e.KeyCode is Keys.Delete && _selectedPaths.Count > 0)
            {
                e.SuppressKeyPress = true;
                DeletePaths(_selectedPaths.ToList());
            }
        };

        ShowSection(_capturesPanel, _capturesNavBtn);
        RefreshHistory();
        UpdateHotkeyHints();
    }

    // ---- Menu bar ----

    private MenuStrip BuildMenuBar()
    {
        var menu = new MenuStrip
        {
            Dock = DockStyle.Top,
            BackColor = TerminalTheme.PanelBg,
            ForeColor = TerminalTheme.TextPrimary,
            Font = new Font(MonoFont.FamilyName, 9f),
            Renderer = new DarkMenuRenderer(),
            GripStyle = ToolStripGripStyle.Hidden
        };

        var file = new ToolStripMenuItem("File") { ForeColor = TerminalTheme.TextPrimary };
        file.DropDownItems.Add("Open Screenshots Folder", null, (_, _) =>
        {
            Directory.CreateDirectory(_settings.ScreenshotsFolder);
            Process.Start("explorer.exe", _settings.ScreenshotsFolder);
        });
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("Exit", null, (_, _) => { _allowExit = true; Close(); });

        var capture = new ToolStripMenuItem("Capture") { ForeColor = TerminalTheme.TextPrimary };
        capture.DropDownItems.Add("Capture Region", null, (_, _) => CaptureRegion());
        capture.DropDownItems.Add("Capture Full Screen", null, (_, _) => CaptureFullScreen());
        capture.DropDownItems.Add("Record Region (GIF)", null, (_, _) => RecordGifRegion());

        var settings = new ToolStripMenuItem("Settings") { ForeColor = TerminalTheme.TextPrimary };
        settings.DropDownItems.Add("Open Settings", null, (_, _) => ShowSection(_settingsPanel, null));

        var help = new ToolStripMenuItem("Help") { ForeColor = TerminalTheme.TextPrimary };
        help.DropDownItems.Add("About SnapTool", null, (_, _) =>
            ThemedMessageBox.Show(this, "SnapTool\nA simple screenshot capture and annotation tool.", "About", ThemedMessageBoxButtons.OK, ThemedMessageBoxIcon.Info));

        foreach (var top in new[] { file, capture, settings, help })
        {
            foreach (ToolStripItem item in top.DropDownItems)
            {
                if (item is ToolStripMenuItem mi) mi.ForeColor = TerminalTheme.TextPrimary;
            }
            menu.Items.Add(top);
        }

        return menu;
    }

    private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColorTable()) { }
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color MenuStripGradientBegin => TerminalTheme.PanelBg;
        public override Color MenuStripGradientEnd => TerminalTheme.PanelBg;
        public override Color ToolStripDropDownBackground => TerminalTheme.Surface1;
        public override Color ImageMarginGradientBegin => TerminalTheme.Surface1;
        public override Color ImageMarginGradientMiddle => TerminalTheme.Surface1;
        public override Color ImageMarginGradientEnd => TerminalTheme.Surface1;
        public override Color MenuItemSelected => TerminalTheme.Surface0;
        public override Color MenuItemSelectedGradientBegin => TerminalTheme.Surface0;
        public override Color MenuItemSelectedGradientEnd => TerminalTheme.Surface0;
        public override Color MenuItemPressedGradientBegin => TerminalTheme.Surface0;
        public override Color MenuItemPressedGradientMiddle => TerminalTheme.Surface0;
        public override Color MenuItemPressedGradientEnd => TerminalTheme.Surface0;
        public override Color MenuBorder => TerminalTheme.Border;
        public override Color MenuItemBorder => TerminalTheme.Accent;
        public override Color SeparatorDark => TerminalTheme.Border;
        public override Color SeparatorLight => TerminalTheme.Border;
        public override Color ToolStripBorder => TerminalTheme.Border;
    }

    // ---- Side panel (capture actions + section nav, consolidated) ----

    private (Panel, Panel regionBtn, Panel fullscreenBtn, Panel gifBtn, Panel capturesNavBtn, Panel settingsNavBtn, Label capturesLabel) BuildSidePanel()
    {
        var panel = new Panel { Dock = DockStyle.Left, Width = 172, BackColor = TerminalTheme.PanelBg };
        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Padding = new Padding(10, 12, 10, 0)
        };

        stack.Controls.Add(BuildSideDivider("ACTIONS"));
        var regionBtn = BuildSideActionButton("Capture Region", CaptureRegion);
        var fullscreenBtn = BuildSideActionButton("Capture Full Screen", CaptureFullScreen);
        var recordBtn = BuildSideActionButton("Record Region (GIF)", RecordGifRegion);
        var refreshBtn = BuildSideActionButton("Refresh", RefreshHistory);
        var openBtn = BuildSideActionButton("Open Folder", () =>
        {
            Directory.CreateDirectory(_settings.ScreenshotsFolder);
            Process.Start("explorer.exe", _settings.ScreenshotsFolder);
        });
        stack.Controls.Add(regionBtn);
        stack.Controls.Add(fullscreenBtn);
        stack.Controls.Add(recordBtn);
        stack.Controls.Add(refreshBtn);
        stack.Controls.Add(openBtn);

        stack.Controls.Add(BuildSideSpacer());
        stack.Controls.Add(BuildSideDivider("VIEW"));
        var (capturesBtn, capturesLabel) = BuildSideNavButton("Captures", () => ShowSection(_capturesPanel, null));
        var (settingsBtn, _) = BuildSideNavButton("Settings", () => ShowSection(_settingsPanel, null));
        stack.Controls.Add(capturesBtn);
        stack.Controls.Add(settingsBtn);

        panel.Controls.Add(stack);
        return (panel, regionBtn, fullscreenBtn, recordBtn, capturesBtn, settingsBtn, capturesLabel);
    }

    private static Label BuildSideDivider(string label) => new()
    {
        Text = $"── {label} ──",
        ForeColor = TerminalTheme.AccentDim,
        Font = new Font(MonoFont.FamilyName, 7.5f, FontStyle.Bold),
        AutoSize = true,
        Margin = new Padding(2, 0, 0, 4)
    };

    private static Panel BuildSideSpacer() => new() { Size = new Size(1, 14) };

    private Panel BuildSideActionButton(string text, Action onClick)
    {
        var btn = new Panel { Size = new Size(148, 30), Cursor = Cursors.Hand, BackColor = TerminalTheme.PanelBg, Margin = new Padding(0, 0, 0, 2) };
        var lbl = new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = TerminalTheme.TextPrimary,
            Font = new Font(MonoFont.FamilyName, 8.5f),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0)
        };
        btn.Controls.Add(lbl);

        btn.Paint += (_, e) =>
        {
            using var brush = new SolidBrush(_hoveredSideButton == btn ? TerminalTheme.Surface1 : TerminalTheme.PanelBg);
            e.Graphics.FillRectangle(brush, 0, 0, btn.Width, btn.Height);
        };
        btn.MouseEnter += (_, _) => { _hoveredSideButton = btn; btn.Invalidate(); };
        btn.MouseLeave += (_, _) => { _hoveredSideButton = null; btn.Invalidate(); };

        btn.Click += (_, _) => onClick();
        lbl.Click += (_, _) => onClick();
        return btn;
    }

    private (Panel, Label) BuildSideNavButton(string text, Action onClick)
    {
        var btn = new Panel { Size = new Size(148, 32), Cursor = Cursors.Hand, BackColor = TerminalTheme.PanelBg, Margin = new Padding(0, 0, 0, 2) };
        var lbl = new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = TerminalTheme.TextMuted,
            Font = new Font(MonoFont.FamilyName, 9f),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 0, 0)
        };
        btn.Controls.Add(lbl);

        btn.Paint += (_, e) =>
        {
            bool active = _activeNavButton == btn;
            var bg = active ? TerminalTheme.Surface0 : (_hoveredSideButton == btn ? TerminalTheme.Surface1 : TerminalTheme.PanelBg);
            using var brush = new SolidBrush(bg);
            e.Graphics.FillRectangle(brush, 0, 0, btn.Width, btn.Height);
            if (active)
            {
                using var pen = new Pen(TerminalTheme.Accent, 3f);
                e.Graphics.DrawLine(pen, 1, 1, 1, btn.Height - 2);
            }
        };
        btn.MouseEnter += (_, _) => { _hoveredSideButton = btn; btn.Invalidate(); };
        btn.MouseLeave += (_, _) => { _hoveredSideButton = null; btn.Invalidate(); };

        void Activate()
        {
            var previous = _activeNavButton;
            _activeNavButton = btn;
            previous?.Invalidate();
            btn.Invalidate();
            onClick();
        }

        btn.Click += (_, _) => Activate();
        lbl.Click += (_, _) => Activate();
        return (btn, lbl);
    }

    private Button BuildSmallButton(string text, bool accent, Action onClick)
    {
        var btn = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 8, 0),
            Padding = new Padding(10, 5, 10, 5),
            FlatStyle = FlatStyle.Flat,
            BackColor = TerminalTheme.Surface1,
            ForeColor = accent ? TerminalTheme.Accent : TerminalTheme.TextPrimary,
            Font = new Font(MonoFont.FamilyName, 9f, accent ? FontStyle.Bold : FontStyle.Regular),
            Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderSize = 1;
        btn.FlatAppearance.BorderColor = TerminalTheme.Border;
        btn.FlatAppearance.MouseOverBackColor = TerminalTheme.Surface0;
        btn.MouseEnter += (_, _) => btn.FlatAppearance.BorderColor = TerminalTheme.Accent;
        btn.MouseLeave += (_, _) => btn.FlatAppearance.BorderColor = TerminalTheme.Border;
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private void ShowSection(Panel panel, Panel? navBtn)
    {
        foreach (Control c in _contentHost.Controls) c.Visible = false;
        panel.Visible = true;
        panel.BringToFront();

        var resolved = navBtn ?? (panel == _capturesPanel ? _capturesNavBtn : _settingsNavBtn);
        var previous = _activeNavButton;
        _activeNavButton = resolved;
        previous?.Invalidate();
        resolved.Invalidate();
    }

    // ---- Status bar ----

    private Panel BuildStatusBar(out Label statusLabel)
    {
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 26, BackColor = TerminalTheme.PanelBg };
        statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = TerminalTheme.TextMuted,
            Font = new Font(MonoFont.FamilyName, 8.5f),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 10, 0)
        };
        bar.Controls.Add(statusLabel);
        return bar;
    }

    private void UpdateStatusBar(int captureCount)
    {
        var region = HotkeyUtil.ToDisplayString(_settings.RegionModifiers, (Keys)_settings.RegionKey);
        var full = HotkeyUtil.ToDisplayString(_settings.FullscreenModifiers, (Keys)_settings.FullscreenKey);
        var gif = HotkeyUtil.ToDisplayString(_settings.GifModifiers, (Keys)_settings.GifKey);
        _statusLabel.Text = $"{captureCount} capture{(captureCount == 1 ? "" : "s")}  •  {_settings.ScreenshotsFolder}  •  Region: {region}  Full: {full}  GIF: {gif}";
    }

    // ---- Captures tab ----

    private (Panel, FlowLayoutPanel) BuildCapturesPanel()
    {
        var root = new Panel { BackColor = TerminalTheme.Background };
        var historyHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = TerminalTheme.Background };
        var historyFlow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
        historyHost.Controls.Add(historyFlow);
        root.Controls.Add(historyHost);

        // Rubber-band (marquee) selection, like Windows Explorer: drag from empty grid space to
        // select every thumbnail the rectangle touches. Wired to both controls since an empty-space
        // click can land on either (gaps between cards hit historyFlow; space below the last row
        // hits historyHost) — historyHost's coordinates are translated into historyFlow's coordinate
        // space (which doesn't shift with scrolling) so both paths compute the same marquee rect.
        historyFlow.Paint += (_, e) =>
        {
            if (!_marqueeActive) return;
            var rect = NormalizeRect(_marqueeStart, _marqueeEnd);
            using var fill = new SolidBrush(Color.FromArgb(40, TerminalTheme.Accent));
            using var border = new Pen(TerminalTheme.Accent, 1f) { DashStyle = DashStyle.Dash };
            e.Graphics.FillRectangle(fill, rect);
            e.Graphics.DrawRectangle(border, rect);
        };
        historyFlow.MouseDown += (_, e) => StartMarquee(e.Location);
        historyFlow.MouseMove += (_, e) => UpdateMarquee(e.Location);
        historyFlow.MouseUp += (_, _) => EndMarquee();
        historyHost.MouseDown += (_, e) => StartMarquee(historyFlow.PointToClient(historyHost.PointToScreen(e.Location)));
        historyHost.MouseMove += (_, e) => UpdateMarquee(historyFlow.PointToClient(historyHost.PointToScreen(e.Location)));
        historyHost.MouseUp += (_, _) => EndMarquee();

        return (root, historyFlow);
    }

    private void StartMarquee(Point p)
    {
        _marqueeActive = true;
        _marqueeStart = p;
        _marqueeEnd = p;
        if (Control.ModifierKeys == Keys.None)
        {
            _selectedPaths.Clear();
            RefreshCardVisuals();
        }
        _historyFlow.Invalidate();
    }

    private void UpdateMarquee(Point p)
    {
        if (!_marqueeActive) return;
        _marqueeEnd = p;
        var rect = NormalizeRect(_marqueeStart, _marqueeEnd);
        _selectedPaths.Clear();
        foreach (Control c in _historyFlow.Controls)
        {
            if (c.Tag is string path && c.Bounds.IntersectsWith(rect)) _selectedPaths.Add(path);
        }
        RefreshCardVisuals();
        _historyFlow.Invalidate();
    }

    private void EndMarquee()
    {
        if (!_marqueeActive) return;
        _marqueeActive = false;
        _historyFlow.Invalidate();
    }

    private static Rectangle NormalizeRect(Point a, Point b)
    {
        int x = Math.Min(a.X, b.X);
        int y = Math.Min(a.Y, b.Y);
        return new Rectangle(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    // ---- Thumbnail selection ----

    private void SelectOnly(string path)
    {
        _selectedPaths.Clear();
        _selectedPaths.Add(path);
        _selectionAnchorPath = path;
        RefreshCardVisuals();
    }

    private void ToggleSelection(string path)
    {
        if (!_selectedPaths.Add(path)) _selectedPaths.Remove(path);
        _selectionAnchorPath = path;
        RefreshCardVisuals();
    }

    private void SelectRange(string anchorPath, string targetPath)
    {
        var order = _historyFlow.Controls.Cast<Control>().Select(c => c.Tag as string).ToList();
        int i1 = order.IndexOf(anchorPath);
        int i2 = order.IndexOf(targetPath);
        if (i1 < 0 || i2 < 0)
        {
            SelectOnly(targetPath);
            return;
        }

        int lo = Math.Min(i1, i2), hi = Math.Max(i1, i2);
        _selectedPaths.Clear();
        for (int i = lo; i <= hi; i++)
        {
            if (order[i] is string p) _selectedPaths.Add(p);
        }
        RefreshCardVisuals();
    }

    private void RefreshCardVisuals()
    {
        foreach (Control c in _historyFlow.Controls)
        {
            if (c.Tag is not string path) continue;
            c.BackColor = _selectedPaths.Contains(path) ? TerminalTheme.Accent : TerminalTheme.Border;
            foreach (Control child in c.Controls)
            {
                if (child is PictureBox pb) pb.Invalidate();
            }
        }
    }

    private void DeleteSelectedOrPath(string path)
    {
        var targets = _selectedPaths.Contains(path) ? _selectedPaths.ToList() : new List<string> { path };
        DeletePaths(targets);
    }

    private void DeletePaths(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0) return;
        string message = paths.Count == 1
            ? $"Delete \"{Path.GetFileName(paths.First())}\"?"
            : $"Delete {paths.Count} screenshots?";
        var confirm = ThemedMessageBox.Show(this, message, "SnapTool", ThemedMessageBoxButtons.YesNo, ThemedMessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        foreach (var p in paths)
        {
            try { File.Delete(p); } catch { /* already gone or locked */ }
            _selectedPaths.Remove(p);
        }
        RefreshHistory();
    }

    private void UpdateHotkeyHints()
    {
        _hintTip.SetToolTip(_regionCaptureBtn, HotkeyUtil.ToDisplayString(_settings.RegionModifiers, (Keys)_settings.RegionKey));
        _hintTip.SetToolTip(_fullscreenCaptureBtn, HotkeyUtil.ToDisplayString(_settings.FullscreenModifiers, (Keys)_settings.FullscreenKey));
        _hintTip.SetToolTip(_gifCaptureBtn, HotkeyUtil.ToDisplayString(_settings.GifModifiers, (Keys)_settings.GifKey));
        UpdateStatusBar(_historyFlow.Controls.Count);
    }

    private void RefreshHistory()
    {
        _historyFlow.SuspendLayout();
        foreach (Control c in _historyFlow.Controls) c.Dispose();
        _historyFlow.Controls.Clear();

        if (!Directory.Exists(_settings.ScreenshotsFolder))
        {
            _historyFlow.ResumeLayout();
            _capturesNavLabel.Text = "Captures";
            UpdateStatusBar(0);
            return;
        }

        var files = Directory.EnumerateFiles(_settings.ScreenshotsFolder)
            .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTime)
            .Take(60)
            .ToList();

        _selectedPaths.IntersectWith(files);

        foreach (var file in files)
        {
            _historyFlow.Controls.Add(BuildThumbnailCard(file));
        }
        _historyFlow.ResumeLayout();

        _capturesNavLabel.Text = $"Captures ({files.Count})";
        UpdateStatusBar(files.Count);
    }

    private Panel BuildThumbnailCard(string path)
    {
        var card = new Panel
        {
            Tag = path,
            Size = new Size(118, 118),
            Margin = new Padding(5),
            Padding = new Padding(1),
            BackColor = _selectedPaths.Contains(path) ? TerminalTheme.Accent : TerminalTheme.Border,
            Cursor = Cursors.Hand
        };
        var pictureBox = new PictureBox { Dock = DockStyle.Top, Height = 88, SizeMode = PictureBoxSizeMode.Zoom, BackColor = TerminalTheme.Surface1 };

        try
        {
            using var img = Image.FromFile(path);
            pictureBox.Image = new Bitmap(img);
        }
        catch
        {
            // skip unreadable/corrupt files
        }

        var nameLabel = new Label
        {
            Text = Path.GetFileName(path),
            Dock = DockStyle.Fill,
            BackColor = TerminalTheme.Surface1,
            ForeColor = TerminalTheme.TextMuted,
            Font = new Font(MonoFont.FamilyName, 7.5f),
            TextAlign = ContentAlignment.MiddleCenter,
            AutoEllipsis = true
        };

        card.Controls.Add(nameLabel);
        card.Controls.Add(pictureBox);

        // Persistent accent border while selected; hover is just a transient preview of that same color.
        card.MouseEnter += (_, _) => card.BackColor = TerminalTheme.Accent;
        card.MouseLeave += (_, _) => card.BackColor = _selectedPaths.Contains(path) ? TerminalTheme.Accent : TerminalTheme.Border;

        pictureBox.Paint += (_, e) =>
        {
            if (!_selectedPaths.Contains(path)) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var badge = new Rectangle(pictureBox.Width - 20, 4, 16, 16);
            using var badgeBg = new SolidBrush(TerminalTheme.Accent);
            e.Graphics.FillEllipse(badgeBg, badge);
            using var checkPen = new Pen(Color.White, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            e.Graphics.DrawLines(checkPen, new[]
            {
                new PointF(badge.X + 3.5f, badge.Y + 8f),
                new PointF(badge.X + 6.5f, badge.Y + 11.5f),
                new PointF(badge.X + 12.5f, badge.Y + 4.5f)
            });
        };

        void HandleSelectClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                if (!_selectedPaths.Contains(path)) SelectOnly(path);
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            if (ModifierKeys.HasFlag(Keys.Shift) && _selectionAnchorPath != null) SelectRange(_selectionAnchorPath, path);
            else if (ModifierKeys.HasFlag(Keys.Control)) ToggleSelection(path);
            else SelectOnly(path);
        }

        card.MouseDown += (_, e) => HandleSelectClick(e);
        pictureBox.MouseDown += (_, e) => HandleSelectClick(e);
        nameLabel.MouseDown += (_, e) => HandleSelectClick(e);

        bool isGif = Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase);

        void OpenFile()
        {
            try
            {
                if (isGif)
                {
                    // GIFs get a real animated viewer instead of the (static, first-frame-only) editor.
                    var preview = new GifPreviewForm(path);
                    preview.Show();
                    preview.Activate();
                    return;
                }

                using var img = Image.FromFile(path);
                var editor = new EditorForm(new Bitmap(img), path);
                editor.FormClosed += (_, _) => RefreshHistory();
                editor.Show();
                editor.Activate();
            }
            catch (Exception ex)
            {
                ThemedMessageBox.Show(this, $"Couldn't open this file:\n{ex.Message}", "SnapTool", ThemedMessageBoxButtons.OK, ThemedMessageBoxIcon.Error);
            }
        }

        var menu = new ContextMenuStrip();
        menu.Items.Add(isGif ? "Play" : "Open in Editor", null, (_, _) => OpenFile());
        menu.Items.Add("Show in Explorer", null, (_, _) => Process.Start("explorer.exe", $"/select,\"{path}\""));
        menu.Items.Add("Copy Image", null, (_, _) =>
        {
            try
            {
                using var img = Image.FromFile(path);
                Clipboard.SetImage(img);
            }
            catch { /* file may have been moved/deleted */ }
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Delete", null, (_, _) => DeleteSelectedOrPath(path));

        card.ContextMenuStrip = menu;
        pictureBox.ContextMenuStrip = menu;
        nameLabel.ContextMenuStrip = menu;

        card.DoubleClick += (_, _) => OpenFile();
        pictureBox.DoubleClick += (_, _) => OpenFile();

        return card;
    }

    // ---- Settings tab (storage + hotkeys + startup, consolidated) ----

    /// <summary>Settings is a narrow vertical category list (Storage / Hotkeys / GIF Recording / Editor
    /// Toolbar / After Capture / Startup) — same left-accent-bar visual language as the app's own main
    /// Captures/Settings side nav, just nested one level in — over a single content area showing
    /// whichever section is selected. A horizontal tab strip was tried first but clipped/overflowed
    /// once there were more than ~4 categories at the app's normal window width; a vertical list has
    /// no such ceiling — it just grows downward, which is also the pattern most desktop apps (VS Code,
    /// JetBrains, macOS System Settings) use for exactly this "many settings groups" problem.</summary>
    private (Panel, TextBox folderBox, TextBox regionBox, TextBox fullscreenBox, TextBox gifBox) BuildSettingsPanel()
    {
        var root = new Panel { BackColor = TerminalTheme.Background, Dock = DockStyle.Fill };
        var contentHost = new Panel { Dock = DockStyle.Fill, BackColor = TerminalTheme.Background };
        var categoryNav = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            Width = 150,
            BackColor = TerminalTheme.PanelBg,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(8, 10, 8, 10)
        };
        root.Controls.Add(contentHost);
        root.Controls.Add(categoryNav);

        var (storagePanel, folderBox) = BuildStorageSection();
        var (hotkeysPanel, regionBox, fullBox, gifBox) = BuildHotkeysSection();
        var gifRecordingPanel = BuildGifRecordingSection();
        var editorToolbarPanel = BuildEditorToolbarSection();
        var afterCapturePanel = BuildAfterCaptureSection();
        var startupPanel = BuildStartupSection();

        var sections = new (string Label, Panel Panel)[]
        {
            ("Storage", storagePanel),
            ("Hotkeys", hotkeysPanel),
            ("GIF Recording", gifRecordingPanel),
            ("Editor Toolbar", editorToolbarPanel),
            ("After Capture", afterCapturePanel),
            ("Startup", startupPanel),
        };

        foreach (var (_, panel) in sections)
        {
            panel.Visible = panel == storagePanel;
            contentHost.Controls.Add(panel);
        }

        Panel? activeCategoryButton = null;
        Panel activeSectionPanel = storagePanel;

        foreach (var (label, panel) in sections)
        {
            Panel categoryButton = null!;
            categoryButton = BuildSettingsCategoryButton(label, () =>
            {
                if (activeSectionPanel == panel) return;
                activeSectionPanel.Visible = false;
                panel.Visible = true;
                activeSectionPanel = panel;

                var previous = activeCategoryButton;
                activeCategoryButton = categoryButton;
                previous?.Invalidate();
                categoryButton.Invalidate();
            }, () => activeCategoryButton == categoryButton);

            if (panel == storagePanel) activeCategoryButton = categoryButton;
            categoryNav.Controls.Add(categoryButton);
        }

        return (root, folderBox, regionBox, fullBox, gifBox);
    }

    /// <summary>Fixed-size, owner-drawn vertical nav row — same recipe as the main window's own
    /// side-nav buttons (Surface0 fill + 3px accent left bar when active) — for consistency between
    /// the two levels of navigation.</summary>
    private Panel BuildSettingsCategoryButton(string text, Action onClick, Func<bool> isActive)
    {
        var btn = new Panel { Size = new Size(132, 32), Cursor = Cursors.Hand, Margin = new Padding(0, 0, 0, 2) };
        bool hovered = false;

        btn.Paint += (_, e) =>
        {
            bool active = isActive();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var bg = new SolidBrush(active ? TerminalTheme.Surface0 : (hovered ? TerminalTheme.Surface1 : TerminalTheme.PanelBg));
            e.Graphics.FillRectangle(bg, 0, 0, btn.Width, btn.Height);
            if (active)
            {
                using var pen = new Pen(TerminalTheme.Accent, 3f);
                e.Graphics.DrawLine(pen, 1, 1, 1, btn.Height - 2);
            }

            using var font = new Font(MonoFont.FamilyName, 9f);
            using var textBrush = new SolidBrush(active ? TerminalTheme.TextPrimary : TerminalTheme.TextMuted);
            var size = e.Graphics.MeasureString(text, font);
            e.Graphics.DrawString(text, font, textBrush, 12, (btn.Height - size.Height) / 2f);
        };
        btn.MouseEnter += (_, _) => { hovered = true; btn.Invalidate(); };
        btn.MouseLeave += (_, _) => { hovered = false; btn.Invalidate(); };
        btn.Click += (_, _) => onClick();

        return btn;
    }

    private static Panel BuildSectionPanel() => new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = TerminalTheme.Background };

    private (Panel, TextBox folderBox) BuildStorageSection()
    {
        var root = BuildSectionPanel();
        int y = 16;

        var folderLabel = new Label { Text = "Screenshots folder:", ForeColor = TerminalTheme.TextPrimary, AutoSize = true, Location = new Point(0, y + 4) };
        var folderBox = new TextBox
        {
            Location = new Point(150, y),
            Width = 400,
            ReadOnly = true,
            Text = _settings.ScreenshotsFolder,
            BackColor = TerminalTheme.Surface1,
            ForeColor = TerminalTheme.TextPrimary,
            Font = new Font(MonoFont.FamilyName, 9f),
            BorderStyle = BorderStyle.FixedSingle
        };
        var browseBtn = BuildSmallButton("Browse...", accent: false, () =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = _settings.ScreenshotsFolder };
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _settings.ScreenshotsFolder = dlg.SelectedPath;
                _settings.Save();
                folderBox!.Text = dlg.SelectedPath;
                Directory.CreateDirectory(dlg.SelectedPath);
                RefreshHistory();
            }
        });
        browseBtn.Location = new Point(560, y - 2);
        var openBtn = BuildSmallButton("Open", accent: false, () =>
        {
            Directory.CreateDirectory(_settings.ScreenshotsFolder);
            Process.Start("explorer.exe", _settings.ScreenshotsFolder);
        });
        openBtn.Location = new Point(660, y - 2);

        root.Controls.Add(folderLabel);
        root.Controls.Add(folderBox);
        root.Controls.Add(browseBtn);
        root.Controls.Add(openBtn);

        return (root, folderBox);
    }

    private (Panel, TextBox regionBox, TextBox fullscreenBox, TextBox gifBox) BuildHotkeysSection()
    {
        var root = BuildSectionPanel();
        int y = 16;

        var regionLabel = new Label { Text = "Capture Region:", ForeColor = TerminalTheme.TextPrimary, AutoSize = true, Location = new Point(0, y + 4) };
        var regionBox = new TextBox
        {
            Location = new Point(150, y), Width = 180, ReadOnly = true,
            Text = HotkeyUtil.ToDisplayString(_settings.RegionModifiers, (Keys)_settings.RegionKey),
            BackColor = TerminalTheme.Surface1, ForeColor = TerminalTheme.TextPrimary,
            Font = new Font(MonoFont.FamilyName, 9f), BorderStyle = BorderStyle.FixedSingle
        };
        var regionChangeBtn = BuildSmallButton("Change", accent: false, () => BeginRecording(regionBox, HotkeyTarget.Region));
        regionChangeBtn.Location = new Point(340, y - 2);
        root.Controls.Add(regionLabel);
        root.Controls.Add(regionBox);
        root.Controls.Add(regionChangeBtn);
        y += 38;

        var fullLabel = new Label { Text = "Capture Full Screen:", ForeColor = TerminalTheme.TextPrimary, AutoSize = true, Location = new Point(0, y + 4) };
        var fullBox = new TextBox
        {
            Location = new Point(150, y), Width = 180, ReadOnly = true,
            Text = HotkeyUtil.ToDisplayString(_settings.FullscreenModifiers, (Keys)_settings.FullscreenKey),
            BackColor = TerminalTheme.Surface1, ForeColor = TerminalTheme.TextPrimary,
            Font = new Font(MonoFont.FamilyName, 9f), BorderStyle = BorderStyle.FixedSingle
        };
        var fullChangeBtn = BuildSmallButton("Change", accent: false, () => BeginRecording(fullBox, HotkeyTarget.Fullscreen));
        fullChangeBtn.Location = new Point(340, y - 2);
        root.Controls.Add(fullLabel);
        root.Controls.Add(fullBox);
        root.Controls.Add(fullChangeBtn);
        y += 38;

        var gifLabel = new Label { Text = "Record Region (GIF):", ForeColor = TerminalTheme.TextPrimary, AutoSize = true, Location = new Point(0, y + 4) };
        var gifBox = new TextBox
        {
            Location = new Point(150, y), Width = 180, ReadOnly = true,
            Text = HotkeyUtil.ToDisplayString(_settings.GifModifiers, (Keys)_settings.GifKey),
            BackColor = TerminalTheme.Surface1, ForeColor = TerminalTheme.TextPrimary,
            Font = new Font(MonoFont.FamilyName, 9f), BorderStyle = BorderStyle.FixedSingle
        };
        var gifChangeBtn = BuildSmallButton("Change", accent: false, () => BeginRecording(gifBox, HotkeyTarget.Gif));
        gifChangeBtn.Location = new Point(340, y - 2);
        root.Controls.Add(gifLabel);
        root.Controls.Add(gifBox);
        root.Controls.Add(gifChangeBtn);
        y += 44;

        var hideWhileCapturing = new CheckBox
        {
            Text = "Hide SnapTool's window while capturing",
            ForeColor = TerminalTheme.TextPrimary,
            Font = new Font(MonoFont.FamilyName, 9f),
            AutoSize = true,
            Location = new Point(0, y),
            Checked = _settings.HideWindowWhileCapturing
        };
        hideWhileCapturing.CheckedChanged += (_, _) =>
        {
            _settings.HideWindowWhileCapturing = hideWhileCapturing.Checked;
            _settings.Save();
        };
        y += 20;
        var hideWhileCapturingHint = new Label
        {
            Text = "Uncheck to be able to include SnapTool's own window in a capture.",
            ForeColor = TerminalTheme.TextMuted,
            Font = new Font(MonoFont.FamilyName, 7.8f),
            AutoSize = true,
            Location = new Point(0, y)
        };
        root.Controls.Add(hideWhileCapturing);
        root.Controls.Add(hideWhileCapturingHint);

        regionBox.KeyDown += HotkeyTextBox_KeyDown;
        fullBox.KeyDown += HotkeyTextBox_KeyDown;
        gifBox.KeyDown += HotkeyTextBox_KeyDown;

        return (root, regionBox, fullBox, gifBox);
    }

    private Panel BuildGifRecordingSection()
    {
        var root = BuildSectionPanel();
        int y = 16;

        var fpsLabel = new Label { Text = "Frame rate:", ForeColor = TerminalTheme.TextPrimary, AutoSize = true, Location = new Point(0, y + 4) };
        var fpsCombo = new ComboBox
        {
            Location = new Point(150, y),
            Width = 220,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = TerminalTheme.Surface1,
            ForeColor = TerminalTheme.TextPrimary,
            Font = new Font(MonoFont.FamilyName, 9f)
        };
        var fpsOptions = new (int Fps, string Label)[]
        {
            (5, "5 fps — smallest file"),
            (8, "8 fps — standard"),
            (12, "12 fps — smoother motion"),
            (15, "15 fps — highest quality"),
        };
        foreach (var (_, label) in fpsOptions) fpsCombo.Items.Add(label);
        int fpsIndex = Array.FindIndex(fpsOptions, o => o.Fps == _settings.GifFps);
        fpsCombo.SelectedIndex = fpsIndex >= 0 ? fpsIndex : 1;
        fpsCombo.SelectedIndexChanged += (_, _) =>
        {
            _settings.GifFps = fpsOptions[fpsCombo.SelectedIndex].Fps;
            _settings.Save();
        };
        root.Controls.Add(fpsLabel);
        root.Controls.Add(fpsCombo);

        return root;
    }

    private Panel BuildEditorToolbarSection()
    {
        var root = BuildSectionPanel();
        int y = 16;

        var toolbarLabel = new Label { Text = "Default position:", ForeColor = TerminalTheme.TextPrimary, AutoSize = true, Location = new Point(0, y + 16) };
        var toolbarPicker = BuildToolbarPositionPicker(y);
        root.Controls.Add(toolbarLabel);
        root.Controls.Add(toolbarPicker);

        return root;
    }

    private Panel BuildAfterCaptureSection()
    {
        var root = BuildSectionPanel();
        int y = 16;

        var autoSave = new CheckBox
        {
            Text = "Automatically save captures to the Screenshots folder",
            ForeColor = TerminalTheme.TextPrimary,
            Font = new Font(MonoFont.FamilyName, 9f),
            AutoSize = true,
            Location = new Point(0, y),
            Checked = _settings.AutoSaveAfterCapture
        };
        autoSave.CheckedChanged += (_, _) =>
        {
            _settings.AutoSaveAfterCapture = autoSave.Checked;
            _settings.Save();
        };
        y += 28;

        var autoCopy = new CheckBox
        {
            Text = "Automatically copy captures to the clipboard",
            ForeColor = TerminalTheme.TextPrimary,
            Font = new Font(MonoFont.FamilyName, 9f),
            AutoSize = true,
            Location = new Point(0, y),
            Checked = _settings.AutoCopyToClipboard
        };
        autoCopy.CheckedChanged += (_, _) =>
        {
            _settings.AutoCopyToClipboard = autoCopy.Checked;
            _settings.Save();
        };
        y += 36;

        var afterCaptureLabel = new Label { Text = "When a screenshot is captured:", ForeColor = TerminalTheme.TextPrimary, Font = new Font(MonoFont.FamilyName, 9f), AutoSize = true, Location = new Point(0, y) };
        y += 22;
        var afterCapturePicker = BuildAfterCaptureActionPicker(y);

        root.Controls.Add(autoSave);
        root.Controls.Add(autoCopy);
        root.Controls.Add(afterCaptureLabel);
        root.Controls.Add(afterCapturePicker);

        return root;
    }

    private Panel BuildStartupSection()
    {
        var root = BuildSectionPanel();
        int y = 16;

        var startWithWindows = new CheckBox
        {
            Text = "Start SnapTool with Windows",
            ForeColor = TerminalTheme.TextPrimary,
            Font = new Font(MonoFont.FamilyName, 9f),
            AutoSize = true,
            Location = new Point(0, y),
            Checked = _settings.StartWithWindows
        };
        startWithWindows.CheckedChanged += (_, _) =>
        {
            _settings.StartWithWindows = startWithWindows.Checked;
            _settings.Save();
            SetStartWithWindows(startWithWindows.Checked);
        };
        root.Controls.Add(startWithWindows);

        return root;
    }

    /// <summary>Vertical stack of selectable cards (radio-style) choosing what happens right after a
    /// screenshot capture completes — opening the editor immediately, showing a corner preview
    /// that opens the editor on click, or doing nothing beyond the save/copy above.</summary>
    private FlowLayoutPanel BuildAfterCaptureActionPicker(int y)
    {
        var container = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Location = new Point(0, y)
        };

        var buttons = new Dictionary<AfterCaptureAction, Panel>();

        Panel BuildOption(AfterCaptureAction action, string title, string description)
        {
            var btn = new Panel { Size = new Size(460, 44), Cursor = Cursors.Hand, Margin = new Padding(0, 0, 0, 6) };

            btn.Paint += (_, e) =>
            {
                bool selected = _settings.AfterCaptureAction == action;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

                using var bg = new SolidBrush(selected ? TerminalTheme.Surface0 : TerminalTheme.PanelBg);
                e.Graphics.FillRectangle(bg, btn.ClientRectangle);
                using var borderPen = new Pen(selected ? TerminalTheme.Accent : TerminalTheme.Border, selected ? 1.6f : 1f);
                e.Graphics.DrawRectangle(borderPen, 0, 0, btn.Width - 1, btn.Height - 1);

                var dotRect = new Rectangle(12, btn.Height / 2 - 6, 12, 12);
                using var dotPen = new Pen(selected ? TerminalTheme.Accent : TerminalTheme.TextMuted, 1.4f);
                e.Graphics.DrawEllipse(dotPen, dotRect);
                if (selected)
                {
                    using var dotFill = new SolidBrush(TerminalTheme.Accent);
                    e.Graphics.FillEllipse(dotFill, dotRect.X + 3, dotRect.Y + 3, 6, 6);
                }

                using var titleFont = new Font(MonoFont.FamilyName, 9f, FontStyle.Bold);
                using var titleBrush = new SolidBrush(TerminalTheme.TextPrimary);
                e.Graphics.DrawString(title, titleFont, titleBrush, 34, 6);

                using var descFont = new Font(MonoFont.FamilyName, 7.8f);
                using var descBrush = new SolidBrush(TerminalTheme.TextMuted);
                e.Graphics.DrawString(description, descFont, descBrush, 34, 23);
            };

            btn.Click += (_, _) =>
            {
                _settings.AfterCaptureAction = action;
                _settings.Save();
                foreach (var b in buttons.Values) b.Invalidate();
            };

            buttons[action] = btn;
            return btn;
        }

        container.Controls.Add(BuildOption(AfterCaptureAction.OpenEditor, "Open in editor", "Automatically opens the annotation editor after every capture."));
        container.Controls.Add(BuildOption(AfterCaptureAction.ShowPreviewToast, "Show preview popup", "A small thumbnail appears in the corner — click it to open the editor."));
        container.Controls.Add(BuildOption(AfterCaptureAction.DoNothing, "Do nothing", "Just saves/copies the capture silently — no window opens."));

        return container;
    }

    /// <summary>Four segmented buttons, each showing a mini window glyph with an accent bar on the
    /// edge it represents, so the option reads visually rather than just as text.</summary>
    private FlowLayoutPanel BuildToolbarPositionPicker(int y)
    {
        var container = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Location = new Point(150, y)
        };

        var buttons = new Dictionary<ToolbarEdge, Panel>();

        Panel BuildOption(ToolbarEdge edge, string label)
        {
            var btn = new Panel { Size = new Size(72, 46), Cursor = Cursors.Hand, Margin = new Padding(0, 0, 8, 0) };

            btn.Paint += (_, e) =>
            {
                bool selected = _settings.DefaultToolbarPosition == edge;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

                using var bg = new SolidBrush(selected ? TerminalTheme.Surface0 : TerminalTheme.PanelBg);
                e.Graphics.FillRectangle(bg, btn.ClientRectangle);
                using var borderPen = new Pen(selected ? TerminalTheme.Accent : TerminalTheme.Border, selected ? 1.6f : 1f);
                e.Graphics.DrawRectangle(borderPen, 0, 0, btn.Width - 1, btn.Height - 1);

                var win = new Rectangle(btn.Width / 2 - 12, 5, 24, 16);
                using var winPen = new Pen(TerminalTheme.TextMuted, 1f);
                e.Graphics.DrawRectangle(winPen, win);

                using var accentBrush = new SolidBrush(selected ? TerminalTheme.Accent : TerminalTheme.TextMuted);
                Rectangle bar = edge switch
                {
                    ToolbarEdge.Top => new Rectangle(win.X + 2, win.Y + 1, win.Width - 4, 3),
                    ToolbarEdge.Bottom => new Rectangle(win.X + 2, win.Bottom - 4, win.Width - 4, 3),
                    ToolbarEdge.Left => new Rectangle(win.X + 1, win.Y + 2, 3, win.Height - 4),
                    _ => new Rectangle(win.Right - 4, win.Y + 2, 3, win.Height - 4)
                };
                e.Graphics.FillRectangle(accentBrush, bar);

                using var font = new Font(MonoFont.FamilyName, 7.5f);
                using var textBrush = new SolidBrush(selected ? TerminalTheme.TextPrimary : TerminalTheme.TextMuted);
                var measured = e.Graphics.MeasureString(label, font);
                e.Graphics.DrawString(label, font, textBrush, btn.Width / 2f - measured.Width / 2f, 27);
            };

            btn.Click += (_, _) =>
            {
                _settings.DefaultToolbarPosition = edge;
                _settings.Save();
                foreach (var b in buttons.Values) b.Invalidate();
            };

            buttons[edge] = btn;
            return btn;
        }

        foreach (var (edge, label) in new[] { (ToolbarEdge.Top, "Top"), (ToolbarEdge.Bottom, "Bottom"), (ToolbarEdge.Left, "Left"), (ToolbarEdge.Right, "Right") })
            container.Controls.Add(BuildOption(edge, label));

        return container;
    }

    private void BeginRecording(TextBox box, HotkeyTarget target)
    {
        _recordingBox = box;
        _recordingTarget = target;
        box.Text = "Press new shortcut... (Esc to cancel)";
        box.Focus();
    }

    private void HotkeyTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender != _recordingBox) return;
        e.SuppressKeyPress = true;
        e.Handled = true;

        if (e.KeyCode == Keys.Escape)
        {
            RestoreRecordingText();
            return;
        }

        if (!HotkeyUtil.TryFromKeyEvent(e, out var mods, out var key)) return;

        ApplyNewHotkey(_recordingTarget, mods, key);
    }

    private void RestoreRecordingText()
    {
        if (_recordingBox == null) return;
        _recordingBox.Text = _recordingTarget switch
        {
            HotkeyTarget.Region => HotkeyUtil.ToDisplayString(_settings.RegionModifiers, (Keys)_settings.RegionKey),
            HotkeyTarget.Fullscreen => HotkeyUtil.ToDisplayString(_settings.FullscreenModifiers, (Keys)_settings.FullscreenKey),
            _ => HotkeyUtil.ToDisplayString(_settings.GifModifiers, (Keys)_settings.GifKey)
        };
        _recordingBox = null;
    }

    private void ApplyNewHotkey(HotkeyTarget target, uint mods, Keys key)
    {
        int id = target switch { HotkeyTarget.Region => HOTKEY_REGION, HotkeyTarget.Fullscreen => HOTKEY_FULLSCREEN, _ => HOTKEY_GIF };
        _hotkeys.Unregister(id);
        bool ok = _hotkeys.Register(id, mods, (uint)key);

        if (!ok)
        {
            ThemedMessageBox.Show(this, "That shortcut is already in use by another application. Choose a different one.", "SnapTool", ThemedMessageBoxButtons.OK, ThemedMessageBoxIcon.Warning);
            // re-register the previous combo so the hotkey keeps working
            switch (target)
            {
                case HotkeyTarget.Region: _hotkeys.Register(id, _settings.RegionModifiers, (uint)_settings.RegionKey); break;
                case HotkeyTarget.Fullscreen: _hotkeys.Register(id, _settings.FullscreenModifiers, (uint)_settings.FullscreenKey); break;
                default: _hotkeys.Register(id, _settings.GifModifiers, (uint)_settings.GifKey); break;
            }
            RestoreRecordingText();
            return;
        }

        switch (target)
        {
            case HotkeyTarget.Region: _settings.RegionModifiers = mods; _settings.RegionKey = (int)key; break;
            case HotkeyTarget.Fullscreen: _settings.FullscreenModifiers = mods; _settings.FullscreenKey = (int)key; break;
            default: _settings.GifModifiers = mods; _settings.GifKey = (int)key; break;
        }
        _settings.Save();

        if (_recordingBox != null) _recordingBox.Text = HotkeyUtil.ToDisplayString(mods, key);
        _recordingBox = null;
        UpdateHotkeyHints();
    }

    private void RegisterHotkeysFromSettings()
    {
        _hotkeys.Register(HOTKEY_REGION, _settings.RegionModifiers, (uint)_settings.RegionKey);
        _hotkeys.Register(HOTKEY_FULLSCREEN, _settings.FullscreenModifiers, (uint)_settings.FullscreenKey);
        _hotkeys.Register(HOTKEY_GIF, _settings.GifModifiers, (uint)_settings.GifKey);
    }

    // ---- Application settings ----

    private static void SetStartWithWindows(bool enabled)
    {
        const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        if (key == null) return;

        if (enabled)
        {
            key.SetValue("SnapTool", $"\"{Application.ExecutablePath}\"");
        }
        else
        {
            if (key.GetValue("SnapTool") != null) key.DeleteValue("SnapTool");
        }
    }

    // ---- Tray icon ----

    private NotifyIcon BuildTrayIcon()
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            BackColor = TerminalTheme.Surface1,
            Font = new Font(MonoFont.FamilyName, 9f)
        };
        menu.Items.Add("Open SnapTool", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        menu.Items.Add("Capture Region", null, (_, _) => CaptureRegion());
        menu.Items.Add("Capture Full Screen", null, (_, _) => CaptureFullScreen());
        menu.Items.Add("Record Region (GIF)", null, (_, _) => RecordGifRegion());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings", null, (_, _) =>
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            ShowSection(_settingsPanel, null);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            _allowExit = true;
            Close();
        });
        foreach (ToolStripItem item in menu.Items)
        {
            if (item is ToolStripMenuItem mi) mi.ForeColor = TerminalTheme.TextPrimary;
        }

        var icon = new NotifyIcon
        {
            Icon = TrayIcons.AppIcon,
            Text = "SnapTool",
            Visible = true,
            ContextMenuStrip = menu
        };
        icon.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
        return icon;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _trayIcon.Visible = false;
        _hotkeys.Dispose();
        _gifTimer.Dispose();
        foreach (var frame in _gifFrames) frame.Dispose();
        _gifBar?.Close();
        _gifBorder?.Close();
        _captureToast?.Close();

        // Application.Run's message loop ends once this (the main) form closes, but any other
        // still-open top-level windows (editor/preview instances opened via .Show()) are on the
        // same thread and don't independently keep the process alive — Environment.Exit here is
        // just a hard guarantee that closing via the tray always fully ends the process rather
        // than relying on every window/handle being torn down cleanly first.
        base.OnFormClosing(e);
        Environment.Exit(0);
    }

    // ---- Capture flow ----

    private void CaptureRegion()
    {
        if (_capturing || _gifTimer.Enabled) return;
        _capturing = true;
        bool wasVisible = Visible;
        if (_settings.HideWindowWhileCapturing && wasVisible)
        {
            Hide();
            Application.DoEvents();
            Thread.Sleep(180);
        }
        try
        {
            using var selector = new RegionSelectorForm();
            var result = selector.ShowDialog();
            if (wasVisible) { Show(); WindowState = FormWindowState.Normal; Activate(); }
            if (result == DialogResult.OK && selector.SelectedBitmap != null)
            {
                HandleCapture(selector.SelectedBitmap);
            }
        }
        finally
        {
            _capturing = false;
        }
    }

    private void CaptureFullScreen()
    {
        if (_capturing || _gifTimer.Enabled) return;
        _capturing = true;
        bool wasVisible = Visible;
        if (_settings.HideWindowWhileCapturing && wasVisible)
        {
            Hide();
            Application.DoEvents();
            Thread.Sleep(180);
        }
        try
        {
            var bmp = CaptureService.CaptureAllScreens();
            if (wasVisible) { Show(); WindowState = FormWindowState.Normal; Activate(); }
            HandleCapture(bmp);
        }
        finally
        {
            _capturing = false;
        }
    }

    private void HandleCapture(Bitmap bmp)
    {
        string? savePath = null;
        if (_settings.AutoSaveAfterCapture)
        {
            try
            {
                Directory.CreateDirectory(_settings.ScreenshotsFolder);
                savePath = Path.Combine(_settings.ScreenshotsFolder, $"SnapTool_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");
                bmp.Save(savePath, ImageFormat.Png);
            }
            catch
            {
                savePath = null;
            }
        }

        if (_settings.AutoCopyToClipboard)
        {
            Clipboard.SetImage(bmp);
        }

        switch (_settings.AfterCaptureAction)
        {
            case AfterCaptureAction.DoNothing:
                bmp.Dispose();
                break;
            case AfterCaptureAction.ShowPreviewToast:
                ShowCapturePreviewToast(bmp, savePath);
                break;
            default:
                OpenEditorForCapture(bmp, savePath);
                break;
        }

        RefreshHistory();
    }

    private void OpenEditorForCapture(Bitmap bmp, string? savePath)
    {
        var editor = new EditorForm(bmp, savePath);
        editor.FormClosed += (_, _) => RefreshHistory();
        editor.Show();
        editor.Activate();
    }

    private void ShowCapturePreviewToast(Bitmap bmp, string? savePath)
    {
        // Replace any still-open toast from a previous capture rather than stacking them.
        _captureToast?.Close();

        var toast = new CapturePreviewToast(bmp, savePath);
        toast.OpenRequested += (image, path) => OpenEditorForCapture(image, path);
        toast.FormClosed += (_, _) => { if (_captureToast == toast) _captureToast = null; };
        _captureToast = toast;
        toast.Show();
    }

    // ---- GIF region recording ----

    private void RecordGifRegion()
    {
        if (_capturing || _gifTimer.Enabled) return;
        _capturing = true;
        Hide();
        Application.DoEvents();
        Thread.Sleep(180);
        try
        {
            using var selector = new RegionSelectorForm();
            var result = selector.ShowDialog();
            selector.SelectedBitmap?.Dispose(); // only the screen bounds are needed to re-capture repeatedly

            if (result == DialogResult.OK && selector.SelectedScreenBounds is { Width: > 0, Height: > 0 } bounds)
            {
                StartGifRecording(bounds);
            }
            else
            {
                Show();
                WindowState = FormWindowState.Normal;
                Activate();
            }
        }
        finally
        {
            _capturing = false;
        }
    }

    private void StartGifRecording(Rectangle bounds)
    {
        _gifRegionBounds = bounds;
        _gifPaused = false;
        _gifAccumulatedElapsed = TimeSpan.Zero;
        _gifSegmentStartedAt = DateTime.UtcNow;
        foreach (var f in _gifFrames) f.Dispose();
        _gifFrames.Clear();

        // The border sits just outside the region (structurally excluded from covering it — see
        // RecordingBorderForm) and the control bar is positioned outside it too, so neither can ever
        // end up in a captured frame. The main window stays hidden for the whole recording since
        // it's normally aimed at some other app running underneath.
        _gifBorder = new RecordingBorderForm(bounds);
        _gifBorder.Show();

        _gifBar = new GifRecordingBar();
        _gifBar.StopRequested += StopGifRecording;
        _gifBar.PauseToggleRequested += ToggleGifPause;
        PositionGifBar(bounds);
        _gifBar.Show();

        _gifTimer.Interval = GifIntervalMs;
        _gifTimer.Start();
    }

    private int GifIntervalMs => 1000 / Math.Clamp(_settings.GifFps, 1, 30);

    private void PositionGifBar(Rectangle bounds)
    {
        if (_gifBar == null) return;
        var screen = Screen.FromRectangle(bounds).WorkingArea;
        int x = Math.Max(screen.Left, Math.Min(bounds.Left, screen.Right - _gifBar.Width));
        int y = bounds.Bottom + 10 + _gifBar.Height <= screen.Bottom ? bounds.Bottom + 10 : bounds.Top - _gifBar.Height - 10;
        _gifBar.Location = new Point(x, Math.Max(screen.Top, y));
    }

    /// <summary>Total active recording time so far, excluding whatever's currently paused.</summary>
    private TimeSpan GifElapsed => _gifAccumulatedElapsed + (_gifPaused ? TimeSpan.Zero : DateTime.UtcNow - _gifSegmentStartedAt);

    private void ToggleGifPause()
    {
        if (!_gifTimer.Enabled && !_gifPaused) return;

        if (_gifPaused)
        {
            _gifPaused = false;
            _gifSegmentStartedAt = DateTime.UtcNow;
            _gifTimer.Start();
        }
        else
        {
            _gifTimer.Stop();
            _gifAccumulatedElapsed += DateTime.UtcNow - _gifSegmentStartedAt;
            _gifPaused = true;
        }
        _gifBar?.SetPaused(_gifPaused);
        _gifBar?.SetElapsed(GifElapsed);
    }

    private void GifTimer_Tick(object? sender, EventArgs e)
    {
        _gifFrames.Add(CaptureService.CaptureRect(_gifRegionBounds, includeCursor: true));
        var elapsed = GifElapsed;
        _gifBar?.SetElapsed(elapsed);

        if (elapsed.TotalSeconds >= GifMaxSeconds) StopGifRecording();
    }

    private void StopGifRecording()
    {
        if (!_gifTimer.Enabled && !_gifPaused) return;
        _gifTimer.Stop();
        _gifPaused = false;

        _gifBar?.Close();
        _gifBar = null;
        _gifBorder?.Close();
        _gifBorder = null;

        Show();
        WindowState = FormWindowState.Normal;
        Activate();

        if (_gifFrames.Count < 2)
        {
            foreach (var f in _gifFrames) f.Dispose();
            _gifFrames.Clear();
            return;
        }

        // Saved straight into the Screenshots folder alongside regular captures — same convention,
        // same auto-save behavior, no extra "where do you want this" prompt.
        Directory.CreateDirectory(_settings.ScreenshotsFolder);
        var path = Path.Combine(_settings.ScreenshotsFolder, $"SnapTool_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.gif");

        Cursor = Cursors.WaitCursor;
        _statusLabel.Text = "Encoding GIF…";
        Application.DoEvents();
        try
        {
            GifWriter.SaveAnimated(path, _gifFrames, GifIntervalMs);
            RefreshHistory();
        }
        finally
        {
            Cursor = Cursors.Default;
            foreach (var f in _gifFrames) f.Dispose();
            _gifFrames.Clear();
        }
    }
}
