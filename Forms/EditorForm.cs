using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using SnapTool.Core;
using SnapTool.Rendering;

namespace SnapTool.Forms;

internal sealed class EditorForm : Form
{
    private const int BtnSize = 40;

    private readonly Bitmap _baseImage;
    private readonly List<Annotation> _annotations = new();
    private readonly Canvas _canvas;
    private readonly Panel _canvasHost;
    private readonly Dictionary<ToolType, Panel> _toolButtons = new();

    private readonly System.Windows.Forms.Timer _tipTimer = new() { Interval = 150 };
    private HoverTip? _hoverTip;
    private Control? _tipTarget;
    private string _tipText = "";

    private const int TopGap = 10;
    private const int EdgeMargin = 8;

    private FloatingToolbarForm _toolbarForm = null!;
    private Panel _restoreButton = null!;
    private Panel? _colorButton;
    private Panel? _hoveredButton;

    // Overflow: toolbar items beyond what the current window size can fit get reparented (not
    // recreated) into a small flyout popup, keeping every button's live event wiring intact.
    private readonly List<Control> _toolbarMovableItems = new();
    private readonly List<Control> _toolbarTrailingPinned = new();
    private readonly HashSet<Control> _toolbarProtectedItems = new();
    private int _toolbarScrollOffset;
    private Panel _overflowButton = null!;
    private FlowLayoutPanel? _overflowFlow;
    private FloatingToolbarForm? _overflowPopup;

    // Contextual mini-bar shown above/below whichever single annotation is currently selected.
    private FloatingToolbarForm? _elementBar;
    private Annotation? _elementBarTarget;

    private ToolType _currentTool = ToolType.Rectangle;
    private Color _currentColor = Color.Red;
    private int _currentThickness = 4;

    private Point _dragStart;
    private Annotation? _inProgress;
    private TextBox? _activeTextBox;
    private string? _savePath;

    private const float ArrowHandleRadius = 5f;
    private const int ArrowHandleHitRadius = 10;

    private enum ArrowHandle { Start, Mid, End }

    /// <summary>8 corner/edge resize handles shared by every bounds-based geometric shape
    /// (rectangle/ellipse/highlight) via <see cref="IResizableBounds"/>.</summary>
    private enum BoundsHandle { TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left }

    private enum LineEndpoint { Start, End }

    private readonly List<Annotation> _selectedAnnotations = new();
    private bool _movingSelection;
    private Point _moveDragStart;
    private ArrowHandle? _draggingArrowHandle;
    private BoundsHandle? _draggingBoundsHandle;
    private Rectangle _resizeStartBounds;
    private LineEndpoint? _draggingLineEndpoint;

    private bool _marqueeActive;
    private Point _marqueeStart;
    private Point _marqueeEnd;

    // Marquee tool: a persistent rectangular pixel-region selection (image-space), independent of
    // annotation selection above, so it can be cut/copied/deleted/pasted across editor windows.
    private Rectangle? _pixelSelection;
    private bool _pixelSelecting;
    private Point _pixelSelectStart;
    private Point _pixelSelectEnd;

    private bool _panning;
    private Point _panStartMouseScreen;
    private Point _panStartCanvasLocation;

    private const double MinZoom = 0.05;
    private const double MaxZoom = 8.0;
    private const double ZoomWheelStep = 1.15;
    private double _zoom = 1.0;
    private Point _activeTextImagePosition;

    private const int EdgeButtonRadius = 20;
    private ToolbarEdge _toolbarEdge;
    private bool _draggingRestoreButton;
    private bool _restoreButtonMoved;
    private Point _restoreDragStartMouseScreen;

    public EditorForm(Bitmap baseImage, string? savePath = null)
    {
        // Marquee-delete punches a transparent hole in the base image, which only works if it
        // actually has an alpha channel — a JPEG-loaded capture (re-opened from disk) may not.
        _baseImage = EnsureArgb(baseImage);
        _savePath = savePath;
        _toolbarEdge = AppSettings.Load().DefaultToolbarPosition;
        Text = "SnapTool - Capture";
        StartPosition = FormStartPosition.CenterScreen;
        Icon = TrayIcons.AppIcon;
        BackColor = Theme.ContentBg;

        _tipTimer.Tick += (_, _) =>
        {
            _tipTimer.Stop();
            if (_tipTarget != null) ShowTip(_tipTarget, _tipText);
        };

        _canvas = new Canvas(this) { Location = new Point(0, 0), Cursor = Cursors.Cross };
        _canvasHost = new Panel { Dock = DockStyle.Fill, AutoScroll = false, BackColor = Color.FromArgb(45, 45, 48) };
        _canvasHost.Paint += (_, e) => DrawCheckerboard(e.Graphics, _canvasHost.ClientRectangle);
        _canvasHost.Resize += (_, _) => CenterCanvas();
        _canvasHost.MouseWheel += (_, e) => HandleZoomWheel(e, e.Location);
        _canvasHost.Controls.Add(_canvas);
        Controls.Add(_canvasHost);

        _restoreButton = BuildRestoreButton();
        Controls.Add(_restoreButton);
        _restoreButton.BringToFront();

        _toolbarForm = BuildToolbarForm();
        _toolbarForm.Owner = this;
        _toolbarForm.MouseWheel += (_, e) => HandleToolbarWheel(e);

        // The toolbar now hides its own overflow into a flyout when the window is too narrow/short
        // to show every button, so the window floor no longer has to be sized around the toolbar's
        // full content — a flat, sane minimum is enough.
        const int minClientWidth = 480, minClientHeight = 320;
        MinimumSize = SizeFromClientSize(new Size(minClientWidth, minClientHeight));

        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        int w = Math.Min(_baseImage.Width + 40, screen.Width - 60);
        int h = Math.Min(_baseImage.Height + 80, screen.Height - 60);
        ClientSize = new Size(Math.Max(w, minClientWidth), Math.Max(h, minClientHeight));

        // Fit the whole screenshot inside the window it opened on, even when it was captured on a
        // larger/higher-res monitor than the one displaying the editor right now. Never upscale past 100%.
        _zoom = Math.Min(1.0, Math.Min((double)ClientSize.Width / _baseImage.Width, (double)ClientSize.Height / _baseImage.Height));
        ApplyCanvasZoomSize();

        Move += (_, _) => { PositionToolbar(); PositionElementBar(); if (_overflowPopup != null) PositionOverflowPopup(); };
        Resize += (_, _) => { ApplyOverflow(); PositionRestoreButton(); PositionElementBar(); };
        Shown += (_, _) => { ApplyOverflow(); _toolbarForm.Show(); PositionRestoreButton(); CenterCanvas(); };

        CenterCanvas();
        UpdateToolSelectionVisuals();
        KeyPreview = true;
        KeyDown += EditorForm_KeyDown;
    }

    /// <summary>Toolbar always stays centered on whichever edge is currently selected — no free dragging of the bar itself.</summary>
    private void PositionToolbar()
    {
        Point loc = _toolbarEdge switch
        {
            ToolbarEdge.Bottom => new Point(Math.Max(8, (ClientSize.Width - _toolbarForm.Width) / 2), ClientSize.Height - _toolbarForm.Height - TopGap),
            ToolbarEdge.Left => new Point(TopGap, Math.Max(8, (ClientSize.Height - _toolbarForm.Height) / 2)),
            ToolbarEdge.Right => new Point(ClientSize.Width - _toolbarForm.Width - TopGap, Math.Max(8, (ClientSize.Height - _toolbarForm.Height) / 2)),
            _ => new Point(Math.Max(8, (ClientSize.Width - _toolbarForm.Width) / 2), TopGap)
        };
        _toolbarForm.Location = PointToScreen(loc);
    }

    /// <summary>Sizes, positions, and clips the restore tab to sit flush against whichever edge is
    /// currently selected, centered along that edge.</summary>
    private void PositionRestoreButton()
    {
        int d = EdgeButtonRadius * 2;
        Size size;
        Point loc;
        switch (_toolbarEdge)
        {
            case ToolbarEdge.Bottom:
                size = new Size(d, EdgeButtonRadius);
                loc = new Point(Math.Max(8, (ClientSize.Width - d) / 2), ClientSize.Height - EdgeButtonRadius);
                break;
            case ToolbarEdge.Left:
                size = new Size(EdgeButtonRadius, d);
                loc = new Point(0, Math.Max(8, (ClientSize.Height - d) / 2));
                break;
            case ToolbarEdge.Right:
                size = new Size(EdgeButtonRadius, d);
                loc = new Point(ClientSize.Width - EdgeButtonRadius, Math.Max(8, (ClientSize.Height - d) / 2));
                break;
            default:
                size = new Size(d, EdgeButtonRadius);
                loc = new Point(Math.Max(8, (ClientSize.Width - d) / 2), 0);
                break;
        }

        _restoreButton.Size = size;
        _restoreButton.Location = loc;
        _restoreButton.Region?.Dispose();
        _restoreButton.Region = new Region(BuildEdgeTabPath(_toolbarEdge, EdgeButtonRadius));
        _restoreButton.Invalidate();
    }

    /// <summary>Which edge of the canvas host a point is nearest to, used to snap the restore tab
    /// (and the toolbar it reopens) while it's being dragged.</summary>
    private ToolbarEdge NearestEdge(Point hostPoint)
    {
        var size = _canvasHost.ClientSize;
        if (size.Width <= 0 || size.Height <= 0) return _toolbarEdge;

        int distTop = hostPoint.Y;
        int distBottom = size.Height - hostPoint.Y;
        int distLeft = hostPoint.X;
        int distRight = size.Width - hostPoint.X;

        int min = Math.Min(Math.Min(distTop, distBottom), Math.Min(distLeft, distRight));
        if (min == distTop) return ToolbarEdge.Top;
        if (min == distBottom) return ToolbarEdge.Bottom;
        return min == distLeft ? ToolbarEdge.Left : ToolbarEdge.Right;
    }

    /// <summary>A flush half-disc "tab": flat diameter sits exactly on the window edge, the curved
    /// side bulges into the canvas — no rectangular corners anywhere on the visible shape.</summary>
    private static GraphicsPath BuildEdgeTabPath(ToolbarEdge edge, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        switch (edge)
        {
            case ToolbarEdge.Top: path.AddPie(0, -radius, d, d, 0, 180); break;
            case ToolbarEdge.Bottom: path.AddPie(0, 0, d, d, 180, 180); break;
            case ToolbarEdge.Left: path.AddPie(-radius, 0, d, d, 270, 180); break;
            case ToolbarEdge.Right: path.AddPie(0, 0, d, d, 90, 180); break;
        }
        return path;
    }

    /// <summary>Small chevron pointing the same way the tab bulges — the direction the toolbar will expand.</summary>
    private static void DrawEdgeChevron(Graphics g, Pen pen, ToolbarEdge edge, int width, int height)
    {
        float cx = width / 2f, cy = height / 2f;
        const float a = 4.2f, b = 2.4f;
        PointF[] pts = edge switch
        {
            ToolbarEdge.Top => new[] { new PointF(cx - a, cy - b), new PointF(cx, cy + b), new PointF(cx + a, cy - b) },
            ToolbarEdge.Bottom => new[] { new PointF(cx - a, cy + b), new PointF(cx, cy - b), new PointF(cx + a, cy + b) },
            ToolbarEdge.Left => new[] { new PointF(cx - b, cy - a), new PointF(cx + b, cy), new PointF(cx - b, cy + a) },
            ToolbarEdge.Right => new[] { new PointF(cx + b, cy - a), new PointF(cx - b, cy), new PointF(cx + b, cy + a) },
            _ => Array.Empty<PointF>()
        };
        if (pts.Length > 0) g.DrawLines(pen, pts);
    }

    // ---- Hover tooltips (custom-drawn; the native ToolTip is unreliable over the non-activating toolbar) ----

    private void AttachTooltip(Control control, string text)
    {
        control.MouseEnter += (_, _) =>
        {
            _tipTarget = control;
            _tipText = text;
            _tipTimer.Stop();
            _tipTimer.Start();
        };
        control.MouseLeave += (_, _) =>
        {
            _tipTimer.Stop();
            _tipTarget = null;
            HideTip();
        };
        control.MouseDown += (_, _) =>
        {
            _tipTimer.Stop();
            HideTip();
        };
    }

    private void ShowTip(Control target, string text)
    {
        _hoverTip ??= new HoverTip { Owner = this };
        _hoverTip.ShowFor(target, text);
    }

    private void HideTip() => _hoverTip?.Hide();

    private void CenterCanvas()
    {
        int x = _canvasHost.ClientSize.Width > _canvas.Width ? (_canvasHost.ClientSize.Width - _canvas.Width) / 2 : 0;
        int y = _canvasHost.ClientSize.Height > _canvas.Height ? (_canvasHost.ClientSize.Height - _canvas.Height) / 2 : 0;
        _canvas.Location = new Point(x, y);
    }

    /// <summary>Keeps at least a sliver of the image within the visible board while panning, so it can't be dragged out of reach.</summary>
    private Point ClampCanvasToHost(Point loc)
    {
        const int minVisible = 60;
        int minX = minVisible - _canvas.Width;
        int maxX = _canvasHost.ClientSize.Width - minVisible;
        int minY = minVisible - _canvas.Height;
        int maxY = _canvasHost.ClientSize.Height - minVisible;

        int x = maxX >= minX ? Math.Clamp(loc.X, minX, maxX) : loc.X;
        int y = maxY >= minY ? Math.Clamp(loc.Y, minY, maxY) : loc.Y;
        return new Point(x, y);
    }

    private static Bitmap EnsureArgb(Bitmap source)
    {
        if (source.PixelFormat == PixelFormat.Format32bppArgb) return source;
        var converted = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(converted)) g.DrawImage(source, Point.Empty);
        source.Dispose();
        return converted;
    }

    private void ApplyCanvasZoomSize()
    {
        _canvas.Size = new Size(
            Math.Max(1, (int)Math.Round(_baseImage.Width * _zoom)),
            Math.Max(1, (int)Math.Round(_baseImage.Height * _zoom)));
        _canvas.Invalidate();
    }

    /// <summary>Zooms in/out around <paramref name="hostAnchor"/> (a point in _canvasHost-local
    /// coordinates) so the image point currently under the cursor stays under the cursor.</summary>
    private void SetZoom(double newZoom, Point hostAnchor)
    {
        newZoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - _zoom) < 0.0001) return;

        double imgX = (hostAnchor.X - _canvas.Location.X) / _zoom;
        double imgY = (hostAnchor.Y - _canvas.Location.Y) / _zoom;

        _zoom = newZoom;
        ApplyCanvasZoomSize();

        var newLoc = new Point(
            (int)Math.Round(hostAnchor.X - imgX * _zoom),
            (int)Math.Round(hostAnchor.Y - imgY * _zoom));
        _canvas.Location = ClampCanvasToHost(newLoc);
        _canvasHost.Invalidate();
        PositionElementBar();
    }

    private void HandleZoomWheel(MouseEventArgs e, Point hostAnchor)
    {
        if (!ModifierKeys.HasFlag(Keys.Control)) return;
        double factor = e.Delta > 0 ? ZoomWheelStep : 1.0 / ZoomWheelStep;
        SetZoom(_zoom * factor, hostAnchor);
    }

    /// <summary>Converts a canvas-local (post-zoom) mouse point into image-pixel coordinates, the
    /// coordinate space every annotation is stored and hit-tested in regardless of current zoom.</summary>
    private Point ToImagePoint(Point canvasPoint) => new(
        (int)Math.Round(canvasPoint.X / _zoom),
        (int)Math.Round(canvasPoint.Y / _zoom));

    private static void DrawCheckerboard(Graphics g, Rectangle area)
    {
        if (area.Width <= 0 || area.Height <= 0) return;
        const int tile = 14;
        using var darkBrush = new SolidBrush(Color.FromArgb(255, 48, 48, 53));
        using var lightBrush = new SolidBrush(Color.FromArgb(255, 58, 58, 64));
        g.FillRectangle(darkBrush, area);
        for (int y = area.Top; y < area.Bottom; y += tile)
        {
            for (int x = area.Left; x < area.Right; x += tile)
            {
                bool alt = ((x - area.Left) / tile + (y - area.Top) / tile) % 2 == 0;
                if (alt) g.FillRectangle(lightBrush, x, y, tile, tile);
            }
        }
    }

    private void ToggleToolbarVisibility(bool show)
    {
        if (show)
        {
            _toolbarForm.Show();
            PositionToolbar();
            _restoreButton.Visible = false;
        }
        else
        {
            _toolbarForm.Hide();
            _restoreButton.Visible = true;
            PositionRestoreButton();
        }

        // Hiding/showing an owned window can otherwise leave the OS's activation state pointing at
        // whatever was behind it, dropping this editor window behind other apps on the monitor.
        Activate();
    }

    private Panel BuildRestoreButton()
    {
        var btn = new Panel { Cursor = Cursors.Hand, Visible = false, BackColor = Color.Transparent };
        AttachTooltip(btn, "Show toolbar (Tab) — drag to another edge to move it");

        btn.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            // Drawn directly onto the button (rather than compositing a pre-rendered bitmap) to avoid
            // GDI+'s dark-fringe artifact when alpha-blending an anti-aliased transparent bitmap.
            using var path = BuildEdgeTabPath(_toolbarEdge, EdgeButtonRadius);
            using var bg = new SolidBrush(_hoveredButton == btn ? Theme.AccentHover : Theme.Accent);
            e.Graphics.FillPath(bg, path);

            using var pen = new Pen(Color.White, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            DrawEdgeChevron(e.Graphics, pen, _toolbarEdge, btn.Width, btn.Height);
        };
        btn.MouseEnter += (_, _) => { _hoveredButton = btn; btn.Invalidate(); };
        btn.MouseLeave += (_, _) => { _hoveredButton = null; btn.Invalidate(); };

        // Click reopens the toolbar; a drag past a small threshold instead snaps the tab (and the
        // toolbar it reopens) to whichever of the four edges is nearest the cursor.
        btn.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            _draggingRestoreButton = true;
            _restoreButtonMoved = false;
            _restoreDragStartMouseScreen = Cursor.Position;
            btn.Capture = true;
        };
        btn.MouseMove += (_, _) =>
        {
            if (!_draggingRestoreButton) return;
            if (!_restoreButtonMoved)
            {
                int ddx = Cursor.Position.X - _restoreDragStartMouseScreen.X;
                int ddy = Cursor.Position.Y - _restoreDragStartMouseScreen.Y;
                if (Math.Abs(ddx) <= 4 && Math.Abs(ddy) <= 4) return;
                _restoreButtonMoved = true;
            }

            var hostPoint = _canvasHost.PointToClient(Cursor.Position);
            var edge = NearestEdge(hostPoint);
            if (edge == _toolbarEdge) return;

            _toolbarEdge = edge;
            RebuildToolbarContent();
            PositionRestoreButton();
        };
        btn.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            bool wasDrag = _restoreButtonMoved;
            _draggingRestoreButton = false;
            btn.Capture = false;
            if (!wasDrag) ToggleToolbarVisibility(true);
        };
        return btn;
    }

    /// <summary>Tab is a "dialog navigation" key: WinForms' default focus-cycling logic consumes it
    /// before it ever reaches KeyDown (the canvas is the form's only tab-stop, so it just re-selects
    /// itself and swallows the keystroke). Intercepting it here, ahead of that logic, is what actually
    /// lets Tab work as the show/hide-toolbar shortcut.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Tab && _activeTextBox == null)
        {
            ToggleToolbarVisibility(!_toolbarForm.Visible);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void EditorForm_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            Close();
            return;
        }

        if (_activeTextBox != null) return;

        if (e.Control && e.KeyCode == Keys.S)
        {
            e.SuppressKeyPress = true;
            if (e.Shift) SaveAs(); else Save();
            return;
        }
        if (e.Control && e.KeyCode == Keys.Z)
        {
            e.SuppressKeyPress = true;
            UndoLast();
            return;
        }
        if (e.Control && e.KeyCode == Keys.C)
        {
            e.SuppressKeyPress = true;
            if (_currentTool == ToolType.Marquee && _pixelSelection.HasValue) CopyPixelSelectionToClipboard();
            else CopyToClipboard();
            return;
        }
        if (e.Control && e.KeyCode == Keys.X)
        {
            e.SuppressKeyPress = true;
            if (_currentTool == ToolType.Marquee && _pixelSelection.HasValue) CutPixelSelection();
            return;
        }
        if (e.Control && e.KeyCode == Keys.V)
        {
            e.SuppressKeyPress = true;
            PasteImageFromClipboard();
            return;
        }
        if (e.Control || e.Alt) return;

        if (e.KeyCode is Keys.Delete or Keys.Back)
        {
            if (_currentTool == ToolType.Select && _selectedAnnotations.Count > 0)
            {
                foreach (var ann in _selectedAnnotations) { DisposeIfImage(ann); _annotations.Remove(ann); }
                _selectedAnnotations.Clear();
                _canvas.Invalidate();
                RefreshElementBar();
                return;
            }
            if (_currentTool == ToolType.Marquee && _pixelSelection.HasValue)
            {
                DeletePixelSelection();
                return;
            }
        }

        ToolType? tool = e.KeyCode switch
        {
            Keys.V => ToolType.Select,
            Keys.G => ToolType.Hand,
            Keys.M => ToolType.Marquee,
            Keys.R => ToolType.Rectangle,
            Keys.E => ToolType.Ellipse,
            Keys.L => ToolType.Line,
            Keys.A => ToolType.Arrow,
            Keys.H => ToolType.Highlight,
            Keys.P => ToolType.Pen,
            Keys.T => ToolType.Text,
            _ => null
        };
        if (tool != null) SelectTool(tool.Value);
    }

    // ---- Floating toolbar (rounded pill, always centered at the top) ----

    private static readonly (ToolType Tool, string Label, char Hotkey)[] ToolDefs =
    {
        (ToolType.Select, "Select / Move", 'V'),
        (ToolType.Hand, "Hand (Pan)", 'G'),
        (ToolType.Marquee, "Marquee Select (cut/copy/paste)", 'M'),
        (ToolType.Rectangle, "Rectangle", 'R'),
        (ToolType.Ellipse, "Ellipse", 'E'),
        (ToolType.Line, "Line", 'L'),
        (ToolType.Arrow, "Arrow", 'A'),
        (ToolType.Highlight, "Highlight", 'H'),
        (ToolType.Pen, "Pen", 'P'),
        (ToolType.Text, "Text", 'T'),
    };

    private FloatingToolbarForm BuildToolbarForm()
    {
        var form = new FloatingToolbarForm { CornerRadius = 14, Padding = new Padding(6, 5, 6, 5) };
        var flow = BuildToolbarContent(_toolbarEdge is ToolbarEdge.Left or ToolbarEdge.Right);
        form.Controls.Add(flow);
        form.FitToContent(flow);
        return form;
    }

    /// <summary>Tears down and rebuilds the toolbar's button row/column for the current
    /// <see cref="_toolbarEdge"/>, since top/bottom needs a horizontal layout and left/right needs
    /// a vertical one. Called whenever the edge changes after the toolbar form already exists.</summary>
    private void RebuildToolbarContent()
    {
        if (_toolbarForm.Controls.Count > 0)
        {
            var old = _toolbarForm.Controls[0];
            _toolbarForm.Controls.Remove(old);
            old.Dispose();
        }

        // The overflow popup (and anything currently parked inside it) belongs to the orientation
        // being torn down — discard it along with everything else rather than trying to migrate it.
        if (_overflowPopup != null)
        {
            var popup = _overflowPopup;
            _overflowPopup = null;
            popup.Close();
            popup.Dispose();
        }
        _overflowFlow = null;

        var flow = BuildToolbarContent(_toolbarEdge is ToolbarEdge.Left or ToolbarEdge.Right);
        _toolbarForm.Controls.Add(flow);
        _toolbarForm.FitToContent(flow);
        ApplyOverflow();
    }

    private static bool IsSeparator(Control c) => c.Tag as string == "separator";

    private FlowLayoutPanel BuildToolbarContent(bool vertical)
    {
        _toolButtons.Clear();
        _toolbarMovableItems.Clear();
        _toolbarTrailingPinned.Clear();
        _toolbarProtectedItems.Clear();
        _toolbarScrollOffset = 0;

        var flow = new FlowLayoutPanel
        {
            FlowDirection = vertical ? FlowDirection.TopDown : FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            BackColor = Color.Transparent
        };

        void AddMovable(Control c) { flow.Controls.Add(c); _toolbarMovableItems.Add(c); }

        foreach (var def in ToolDefs) AddMovable(BuildToolButton(def.Tool, def.Label, def.Hotkey));

        AddMovable(BuildSeparator(vertical));
        _colorButton = BuildColorSwatchButton(() => _currentColor, c => { _currentColor = c; _colorButton?.Invalidate(); });
        AddMovable(_colorButton);
        AddMovable(BuildSeparator(vertical));
        AddMovable(BuildThicknessSelector(vertical));
        AddMovable(BuildSeparator(vertical));
        AddMovable(BuildActionButton(ToolIcons.Undo(), "Undo", "Ctrl+Z", UndoLast));
        AddMovable(BuildSeparator(vertical));
        // Copy is exempt from ever being pushed into overflow — it stays visible ahead of everything
        // else in the movable set, regardless of how little room the window has.
        var copyButton = BuildActionButton(ToolIcons.Copy(), "Copy to clipboard", "Ctrl+C", CopyToClipboard);
        AddMovable(copyButton);
        _toolbarProtectedItems.Add(copyButton);
        AddMovable(BuildActionButton(ToolIcons.Save(), "Save", "Ctrl+S", Save));
        AddMovable(BuildActionButton(ToolIcons.SaveAs(), "Save As...", "Ctrl+Shift+S", SaveAs));
        AddMovable(BuildSeparator(vertical));
        AddMovable(BuildActionButton(ToolIcons.Close(), "Close", "Esc", Close));

        // Trigger for the overflow flyout — always present in the layout, only shown once something
        // has actually been pushed off into it.
        _overflowButton = BuildActionButton(ToolIcons.ThreeDots(), "More tools", "", ToggleOverflowPopup);
        _overflowButton.Visible = false;
        flow.Controls.Add(_overflowButton);

        // Pinned tail — always visible regardless of window size, never eligible for overflow.
        var hideSeparator = BuildSeparator(vertical);
        flow.Controls.Add(hideSeparator);
        _toolbarTrailingPinned.Add(hideSeparator);

        // Points toward whichever edge the toolbar is currently docked to — the direction it will collapse into.
        Bitmap hideIcon = _toolbarEdge switch
        {
            ToolbarEdge.Bottom => ToolIcons.ChevronDown(),
            ToolbarEdge.Left => ToolIcons.ChevronLeft(),
            ToolbarEdge.Right => ToolIcons.ChevronRight(),
            _ => ToolIcons.ChevronUp()
        };
        var hideButton = BuildActionButton(hideIcon, "Hide toolbar", "Tab", () => ToggleToolbarVisibility(false));
        flow.Controls.Add(hideButton);
        _toolbarTrailingPinned.Add(hideButton);

        _overflowFlow = new FlowLayoutPanel
        {
            FlowDirection = vertical ? FlowDirection.TopDown : FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            BackColor = Color.Transparent
        };

        return flow;
    }

    /// <summary>Recomputes how much of the toolbar fits inside the current window and reparents
    /// (never recreates — every button keeps its live click wiring) whatever doesn't fit into the
    /// overflow flyout, so the toolbar itself can never hang off the edge of its own window again.
    /// Protected items (currently just Copy) are excluded from hiding entirely; the rest slide through
    /// a scrollable window driven by <see cref="_toolbarScrollOffset"/> (mouse wheel while hovering).</summary>
    private void ApplyOverflow()
    {
        if (_overflowFlow == null || _toolbarForm.Controls.Count == 0) return;
        var flow = (FlowLayoutPanel)_toolbarForm.Controls[0];
        bool vertical = _toolbarEdge is ToolbarEdge.Left or ToolbarEdge.Right;

        // .Width/.Height, not .PreferredSize — these Panels are explicitly sized (not AutoSize), and
        // PreferredSize doesn't reliably reflect that for plain Controls; it was silently reporting
        // near-zero, so "does it fit" was always true and overflow never actually triggered.
        int Extent(Control c) => vertical
            ? c.Height + c.Margin.Top + c.Margin.Bottom
            : c.Width + c.Margin.Left + c.Margin.Right;

        int hostExtent = vertical ? ClientSize.Height : ClientSize.Width;
        int chromeExtent = vertical ? _toolbarForm.Padding.Vertical : _toolbarForm.Padding.Horizontal;
        int available = Math.Max(0, hostExtent - 2 * EdgeMargin - chromeExtent);

        int pinnedExtent = _toolbarTrailingPinned.Sum(Extent);
        int protectedExtent = _toolbarMovableItems.Where(c => _toolbarProtectedItems.Contains(c)).Sum(Extent);
        var scrollable = _toolbarMovableItems.Where(c => !_toolbarProtectedItems.Contains(c)).ToList();
        int totalMovable = protectedExtent + scrollable.Sum(Extent);

        HashSet<Control> visibleScrollable;
        if (pinnedExtent + totalMovable <= available)
        {
            _toolbarScrollOffset = 0;
            visibleScrollable = scrollable.ToHashSet();
        }
        else
        {
            int budget = Math.Max(0, available - pinnedExtent - protectedExtent - Extent(_overflowButton));
            _toolbarScrollOffset = scrollable.Count == 0 ? 0 : Math.Clamp(_toolbarScrollOffset, 0, scrollable.Count - 1);

            var visibleList = new List<Control>();
            int used = 0;
            for (int i = _toolbarScrollOffset; i < scrollable.Count; i++)
            {
                int e = Extent(scrollable[i]);
                if (used + e > budget) break;
                visibleList.Add(scrollable[i]);
                used += e;
            }
            // Don't leave the window scrolled past the point where it'd show empty trailing space —
            // pull it backward to fill the budget whenever there's room to.
            while (_toolbarScrollOffset > 0)
            {
                int e = Extent(scrollable[_toolbarScrollOffset - 1]);
                if (used + e > budget) break;
                _toolbarScrollOffset--;
                visibleList.Insert(0, scrollable[_toolbarScrollOffset]);
                used += e;
            }
            visibleScrollable = visibleList.ToHashSet();
        }

        var visibleSet = new HashSet<Control>(_toolbarProtectedItems);
        visibleSet.UnionWith(visibleScrollable);
        var kept = _toolbarMovableItems.Where(visibleSet.Contains).ToList();

        // Clean up dangling separators: none at the start/end of the visible run, and never doubled up
        // where whatever used to sit between them just got hidden.
        for (int i = kept.Count - 1; i >= 0; i--)
        {
            if (!IsSeparator(kept[i])) continue;
            bool atEdge = i == 0 || i == kept.Count - 1;
            bool nextToSeparator = (i > 0 && IsSeparator(kept[i - 1])) || (i < kept.Count - 1 && IsSeparator(kept[i + 1]));
            if (atEdge || nextToSeparator) kept.RemoveAt(i);
        }

        bool anyHidden = kept.Count < _toolbarMovableItems.Count;

        flow.SuspendLayout();
        flow.Controls.Clear();
        foreach (var item in kept) flow.Controls.Add(item);
        _overflowButton.Visible = anyHidden;
        flow.Controls.Add(_overflowButton);
        foreach (var pinned in _toolbarTrailingPinned) flow.Controls.Add(pinned);
        flow.ResumeLayout(true);

        var keptSet = kept.ToHashSet();
        _overflowFlow.Controls.Clear();
        foreach (var item in _toolbarMovableItems.Where(c => !keptSet.Contains(c))) _overflowFlow.Controls.Add(item);

        _toolbarForm.FitToContent(flow);
        PositionToolbar();

        if (_overflowPopup != null)
        {
            if (!anyHidden) CloseOverflowPopup();
            else
            {
                // The hidden set may have grown/shrunk further while the flyout was already open —
                // re-fit the shell to its (possibly resized) content before repositioning it.
                _overflowPopup.FitToContent(_overflowFlow);
                PositionOverflowPopup();
            }
        }
    }

    /// <summary>Lets a mouse wheel over the toolbar itself page through whatever's currently
    /// overflowed, without needing to open the flyout — a quicker alternative to it, not a
    /// replacement (the flyout still works and still lists everything currently hidden).</summary>
    private void HandleToolbarWheel(MouseEventArgs e)
    {
        if (!_overflowButton.Visible) return;
        _toolbarScrollOffset += e.Delta < 0 ? 1 : -1;
        if (_toolbarScrollOffset < 0) _toolbarScrollOffset = 0;
        ApplyOverflow();
    }

    private void ToggleOverflowPopup()
    {
        if (_overflowPopup != null) { CloseOverflowPopup(); return; }
        if (_overflowFlow == null || _overflowFlow.Controls.Count == 0) return;

        var popup = new FloatingToolbarForm { CornerRadius = 14, Padding = new Padding(6, 5, 6, 5) };
        popup.Controls.Add(_overflowFlow);
        popup.FitToContent(_overflowFlow);
        _overflowPopup = popup;
        PositionOverflowPopup();
        popup.Show(this);
    }

    /// <summary>Closes the flyout shell but keeps <see cref="_overflowFlow"/> and its buttons alive
    /// (just detached), since the same still-hidden items may need to reappear if the flyout reopens
    /// or the window shrinks again.</summary>
    private void CloseOverflowPopup()
    {
        if (_overflowPopup == null) return;
        var popup = _overflowPopup;
        _overflowPopup = null;
        if (_overflowFlow != null && popup.Controls.Contains(_overflowFlow))
            popup.Controls.Remove(_overflowFlow);
        popup.Close();
        popup.Dispose();
    }

    private void PositionOverflowPopup()
    {
        if (_overflowPopup == null) return;
        var btnScreen = _overflowButton.PointToScreen(Point.Empty);
        Point loc = _toolbarEdge switch
        {
            ToolbarEdge.Bottom => new Point(btnScreen.X, btnScreen.Y - _overflowPopup.Height - 6),
            ToolbarEdge.Left => new Point(btnScreen.X + BtnSize + 6, btnScreen.Y),
            ToolbarEdge.Right => new Point(btnScreen.X - _overflowPopup.Width - 6, btnScreen.Y),
            _ => new Point(btnScreen.X, btnScreen.Y + BtnSize + 6)
        };
        _overflowPopup.Location = loc;
    }

    // ---- Per-element mini-bar (color / thickness / opacity / duplicate / bring-to-front / delete) ----

    /// <summary>Shows (building fresh content if the selection target changed) or hides the mini-bar
    /// to match the current selection. Called after every point where <see cref="_selectedAnnotations"/>
    /// settles on a final value — never mid-drag, since <see cref="HideElementBar"/> is called instead
    /// at the start of any drag to avoid it trailing a shape that's actively moving.</summary>
    private void RefreshElementBar()
    {
        if (_currentTool != ToolType.Select || _selectedAnnotations.Count != 1)
        {
            HideElementBar();
            return;
        }

        var target = _selectedAnnotations[0];
        if (_elementBar == null || _elementBarTarget != target)
        {
            HideElementBar();
            var content = BuildElementBarContent(target);
            var bar = new FloatingToolbarForm { CornerRadius = 12, Padding = new Padding(5, 4, 5, 4) };
            bar.Controls.Add(content);
            bar.FitToContent(content);
            _elementBar = bar;
            _elementBarTarget = target;
            bar.Show(this);
        }

        PositionElementBar();
    }

    private void HideElementBar()
    {
        if (_elementBar == null) return;
        var bar = _elementBar;
        _elementBar = null;
        _elementBarTarget = null;
        bar.Close();
        bar.Dispose();
    }

    /// <summary>Converts an image-space point (the coordinate space every annotation is stored in) to
    /// a screen point, for positioning the mini-bar next to whatever's selected — the inverse of
    /// <see cref="ToImagePoint"/> plus the canvas's own on-screen offset.</summary>
    private Point ImageToScreen(Point imagePoint)
    {
        var canvasLocal = new Point((int)Math.Round(imagePoint.X * _zoom), (int)Math.Round(imagePoint.Y * _zoom));
        var hostLocal = new Point(canvasLocal.X + _canvas.Location.X, canvasLocal.Y + _canvas.Location.Y);
        return _canvasHost.PointToScreen(hostLocal);
    }

    private void PositionElementBar()
    {
        if (_elementBar == null || _elementBarTarget == null) return;

        var bounds = _elementBarTarget.GetBounds();
        var topLeft = ImageToScreen(new Point(bounds.Left, bounds.Top));
        var topRight = ImageToScreen(new Point(bounds.Right, bounds.Top));
        int centerX = (topLeft.X + topRight.X) / 2;

        int barX = centerX - _elementBar.Width / 2;
        int barY = topLeft.Y - _elementBar.Height - 10;
        if (barY < Bounds.Top + 30)
        {
            var bottomLeft = ImageToScreen(new Point(bounds.Left, bounds.Bottom));
            barY = bottomLeft.Y + 10;
        }
        barX = Math.Clamp(barX, Bounds.Left + 4, Math.Max(Bounds.Left + 4, Bounds.Right - _elementBar.Width - 4));
        _elementBar.Location = new Point(barX, barY);
    }

    private FlowLayoutPanel BuildElementBarContent(Annotation ann)
    {
        var flow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            BackColor = Color.Transparent
        };

        bool hasStroke = ann is RectangleAnnotation or EllipseAnnotation or LineAnnotation or ArrowAnnotation or FreehandAnnotation;
        bool hasColorAndOpacity = ann is not ImageAnnotation;

        if (hasColorAndOpacity)
        {
            flow.Controls.Add(BuildColorSwatchButton(() => ann.Color, c => { ann.Color = c; _canvas.Invalidate(); }));
            flow.Controls.Add(BuildSeparator(false));
        }
        if (hasStroke)
        {
            flow.Controls.Add(BuildThicknessSelectorGeneric(false, () => ann.Thickness, v => { ann.Thickness = v; _canvas.Invalidate(); }));
            flow.Controls.Add(BuildSeparator(false));
        }
        if (hasColorAndOpacity)
        {
            flow.Controls.Add(BuildOpacitySlider(() => ann.Opacity, v => { ann.Opacity = v; _canvas.Invalidate(); }));
            flow.Controls.Add(BuildSeparator(false));
        }

        flow.Controls.Add(BuildActionButton(ToolIcons.Duplicate(), "Duplicate", "", () => DuplicateAnnotation(ann)));
        flow.Controls.Add(BuildActionButton(ToolIcons.BringToFront(), "Bring to front", "", () => BringToFrontAnnotation(ann)));
        flow.Controls.Add(BuildActionButton(ToolIcons.Trash(), "Delete", "Del", () => DeleteAnnotation(ann)));

        return flow;
    }

    private void DuplicateAnnotation(Annotation ann)
    {
        if (!_annotations.Contains(ann)) return;
        var clone = ann.Clone();
        clone.Move(14, 14);
        _annotations.Add(clone);
        _selectedAnnotations.Clear();
        _selectedAnnotations.Add(clone);
        _canvas.Invalidate();
        RefreshElementBar();
    }

    private void BringToFrontAnnotation(Annotation ann)
    {
        if (!_annotations.Remove(ann)) return;
        _annotations.Add(ann);
        _canvas.Invalidate();
    }

    private void DeleteAnnotation(Annotation ann)
    {
        DisposeIfImage(ann);
        _annotations.Remove(ann);
        _selectedAnnotations.Remove(ann);
        _canvas.Invalidate();
        RefreshElementBar();
    }

    private static Panel BuildSeparator(bool vertical) => vertical
        ? new Panel { Size = new Size(BtnSize - 10, 1), Margin = new Padding(5, 3, 5, 3), BackColor = Theme.Border, Tag = "separator" }
        : new Panel { Size = new Size(1, BtnSize - 10), Margin = new Padding(3, 5, 3, 5), BackColor = Theme.Border, Tag = "separator" };

    private Panel BuildToolButton(ToolType tool, string label, char hotkey)
    {
        var btn = new Panel { Size = new Size(BtnSize, BtnSize), Margin = new Padding(1), Cursor = Cursors.Hand, BackColor = Color.Transparent };
        var icon = ToolIcons.For(tool);
        AttachTooltip(btn, $"{label} ({hotkey})");

        btn.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            bool selected = _currentTool == tool;
            if (selected || _hoveredButton == btn)
            {
                using var path = Geometry.RoundedRect(new Rectangle(2, 2, BtnSize - 4, BtnSize - 4), 9);
                using var bg = new SolidBrush(selected ? Theme.AccentSoftBg : Theme.CardBg);
                e.Graphics.FillPath(bg, path);
            }
            e.Graphics.DrawImage(icon, (BtnSize - icon.Width) / 2f, (BtnSize - icon.Height) / 2f - 2);

            using var hkFont = new Font("Segoe UI", 6.5f);
            using var hkBrush = new SolidBrush(Theme.TextSecondary);
            e.Graphics.DrawString(hotkey.ToString(), hkFont, hkBrush, BtnSize - 11, BtnSize - 13);
        };
        btn.MouseEnter += (_, _) => { _hoveredButton = btn; btn.Invalidate(); };
        btn.MouseLeave += (_, _) => { _hoveredButton = null; btn.Invalidate(); };
        btn.Click += (_, _) => SelectTool(tool);

        _toolButtons[tool] = btn;
        return btn;
    }

    private Panel BuildActionButton(Bitmap icon, string label, string hotkey, Action onClick)
    {
        var btn = new Panel { Size = new Size(BtnSize, BtnSize), Margin = new Padding(1), Cursor = Cursors.Hand, BackColor = Color.Transparent };
        AttachTooltip(btn, string.IsNullOrEmpty(hotkey) ? label : $"{label} ({hotkey})");

        btn.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (_hoveredButton == btn)
            {
                using var path = Geometry.RoundedRect(new Rectangle(2, 2, BtnSize - 4, BtnSize - 4), 9);
                using var bg = new SolidBrush(Theme.CardBg);
                e.Graphics.FillPath(bg, path);
            }
            e.Graphics.DrawImage(icon, (BtnSize - icon.Width) / 2f, (BtnSize - icon.Height) / 2f);
        };
        btn.MouseEnter += (_, _) => { _hoveredButton = btn; btn.Invalidate(); };
        btn.MouseLeave += (_, _) => { _hoveredButton = null; btn.Invalidate(); };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    /// <summary>Shared by the main toolbar's color button and the per-element mini-bar's color
    /// button — only the getter/setter differ (global "current color" vs. one annotation's Color).</summary>
    private Panel BuildColorSwatchButton(Func<Color> getColor, Action<Color> setColor)
    {
        var btn = new Panel { Size = new Size(BtnSize, BtnSize), Margin = new Padding(1), Cursor = Cursors.Hand, BackColor = Color.Transparent };
        AttachTooltip(btn, "Color");

        btn.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (_hoveredButton == btn)
            {
                using var path = Geometry.RoundedRect(new Rectangle(2, 2, BtnSize - 4, BtnSize - 4), 9);
                using var bg = new SolidBrush(Theme.CardBg);
                e.Graphics.FillPath(bg, path);
            }
            using var swatch = new SolidBrush(getColor());
            e.Graphics.FillEllipse(swatch, BtnSize / 2f - 9, BtnSize / 2f - 9, 18, 18);
            using var ring = new Pen(Color.FromArgb(140, 255, 255, 255), 1.4f);
            e.Graphics.DrawEllipse(ring, BtnSize / 2f - 9, BtnSize / 2f - 9, 18, 18);
        };
        btn.MouseEnter += (_, _) => { _hoveredButton = btn; btn.Invalidate(); };
        btn.MouseLeave += (_, _) => { _hoveredButton = null; btn.Invalidate(); };
        btn.Click += (_, _) =>
        {
            var popup = new ColorPickerPopup(getColor());
            popup.ColorPicked += c => { setColor(c); btn.Invalidate(); };
            popup.ShowAt(btn.PointToScreen(new Point(-4, BtnSize + 4)));
        };
        return btn;
    }

    private void SelectTool(ToolType tool)
    {
        CommitTextBox();
        _currentTool = tool;
        if (tool != ToolType.Select) _selectedAnnotations.Clear();
        if (tool != ToolType.Marquee) { _pixelSelection = null; _pixelSelecting = false; }
        UpdateToolSelectionVisuals();
        _canvas.Cursor = tool switch
        {
            ToolType.Select => Cursors.Default,
            ToolType.Hand => Cursors.Hand,
            _ => Cursors.Cross
        };
        _canvas.Invalidate();
        RefreshElementBar();
    }

    /// <summary>Default arrow cursor for the Select tool; only swaps to a move/resize cursor when hovering something interactive.</summary>
    private void UpdateSelectCursor(Point p)
    {
        if (_selectedAnnotations.Count == 1)
        {
            switch (_selectedAnnotations[0])
            {
                case ArrowAnnotation arrow when HitTestArrowHandle(arrow, p, ArrowHandleHitRadius / _zoom) != null:
                    _canvas.Cursor = Cursors.Hand;
                    return;
                case IResizableBounds rb when HitTestBoundsHandle(rb.Bounds, p, ArrowHandleHitRadius / _zoom) is { } handle:
                    _canvas.Cursor = CursorForBoundsHandle(handle);
                    return;
                case IResizableEndpoints rl when HitTestLineEndpoint(rl, p, ArrowHandleHitRadius / _zoom) != null:
                    _canvas.Cursor = Cursors.Hand;
                    return;
            }
        }
        _canvas.Cursor = HitTestAnnotations(p) != null ? Cursors.SizeAll : Cursors.Default;
    }

    private static ArrowHandle? HitTestArrowHandle(ArrowAnnotation arrow, Point p, double hitRadius)
    {
        if (Geometry.Distance(p, arrow.Start) <= hitRadius) return ArrowHandle.Start;
        if (Geometry.Distance(p, arrow.End) <= hitRadius) return ArrowHandle.End;
        var mid = arrow.MidPoint ?? Geometry.Midpoint(arrow.Start, arrow.End);
        if (Geometry.Distance(p, mid) <= hitRadius) return ArrowHandle.Mid;
        return null;
    }

    /// <summary>Image-space point for one of a bounds rectangle's 8 resize handles.</summary>
    private static Point GetBoundsHandlePoint(Rectangle b, BoundsHandle h) => h switch
    {
        BoundsHandle.TopLeft => new Point(b.Left, b.Top),
        BoundsHandle.Top => new Point(b.Left + b.Width / 2, b.Top),
        BoundsHandle.TopRight => new Point(b.Right, b.Top),
        BoundsHandle.Right => new Point(b.Right, b.Top + b.Height / 2),
        BoundsHandle.BottomRight => new Point(b.Right, b.Bottom),
        BoundsHandle.Bottom => new Point(b.Left + b.Width / 2, b.Bottom),
        BoundsHandle.BottomLeft => new Point(b.Left, b.Bottom),
        _ => new Point(b.Left, b.Top + b.Height / 2) // Left
    };

    private static BoundsHandle? HitTestBoundsHandle(Rectangle bounds, Point p, double hitRadius)
    {
        foreach (BoundsHandle h in Enum.GetValues<BoundsHandle>())
            if (Geometry.Distance(p, GetBoundsHandlePoint(bounds, h)) <= hitRadius) return h;
        return null;
    }

    /// <summary>Resizes from a bounds rectangle captured once at drag-start, not the live-updating one
    /// — so each mouse-move recomputes from a stable reference instead of drifting, and dragging a
    /// corner/edge past its opposite side cleanly flips the rectangle instead of collapsing it.</summary>
    private static Rectangle ResizeBounds(Rectangle start, BoundsHandle handle, Point p)
    {
        int left = start.Left, top = start.Top, right = start.Right, bottom = start.Bottom;
        switch (handle)
        {
            case BoundsHandle.TopLeft: left = p.X; top = p.Y; break;
            case BoundsHandle.Top: top = p.Y; break;
            case BoundsHandle.TopRight: right = p.X; top = p.Y; break;
            case BoundsHandle.Right: right = p.X; break;
            case BoundsHandle.BottomRight: right = p.X; bottom = p.Y; break;
            case BoundsHandle.Bottom: bottom = p.Y; break;
            case BoundsHandle.BottomLeft: left = p.X; bottom = p.Y; break;
            case BoundsHandle.Left: left = p.X; break;
        }
        return Rectangle.FromLTRB(Math.Min(left, right), Math.Min(top, bottom), Math.Max(left, right), Math.Max(top, bottom));
    }

    private static Cursor CursorForBoundsHandle(BoundsHandle h) => h switch
    {
        BoundsHandle.TopLeft or BoundsHandle.BottomRight => Cursors.SizeNWSE,
        BoundsHandle.TopRight or BoundsHandle.BottomLeft => Cursors.SizeNESW,
        BoundsHandle.Top or BoundsHandle.Bottom => Cursors.SizeNS,
        _ => Cursors.SizeWE
    };

    private static LineEndpoint? HitTestLineEndpoint(IResizableEndpoints line, Point p, double hitRadius)
    {
        if (Geometry.Distance(p, line.Start) <= hitRadius) return LineEndpoint.Start;
        if (Geometry.Distance(p, line.End) <= hitRadius) return LineEndpoint.End;
        return null;
    }

    private void UpdateToolSelectionVisuals()
    {
        foreach (var kv in _toolButtons) kv.Value.Invalidate();
    }

    private void UndoLast()
    {
        if (_annotations.Count == 0) return;
        var removed = _annotations[^1];
        DisposeIfImage(removed);
        _annotations.RemoveAt(_annotations.Count - 1);
        _selectedAnnotations.Remove(removed);
        _canvas.Invalidate();
        RefreshElementBar();
    }

    private static void DisposeIfImage(Annotation ann)
    {
        if (ann is ImageAnnotation img) img.Image.Dispose();
    }

    private FlowLayoutPanel BuildThicknessSelector(bool vertical) =>
        BuildThicknessSelectorGeneric(vertical, () => _currentThickness, v => _currentThickness = v);

    /// <summary>Shared by the main toolbar's thickness selector (global "current thickness") and the
    /// per-element mini-bar's (one annotation's own Thickness) — each instance owns its own little
    /// repaint list, so multiple copies of this control can exist at once without interfering.</summary>
    private FlowLayoutPanel BuildThicknessSelectorGeneric(bool vertical, Func<int> getValue, Action<int> setValue)
    {
        var buttons = new List<Panel>();
        var row = new FlowLayoutPanel
        {
            FlowDirection = vertical ? FlowDirection.TopDown : FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Margin = vertical ? new Padding(11, 2, 0, 2) : new Padding(2, 11, 2, 0)
        };

        foreach (var (value, dotSize) in new[] { (2, 5), (4, 9), (8, 13) })
        {
            const int btnSize = 18;
            var panel = new Panel { Size = new Size(btnSize, btnSize), Margin = new Padding(1), Cursor = Cursors.Hand };
            AttachTooltip(panel, value switch { 2 => "Thin", 4 => "Medium", _ => "Thick" });
            panel.Paint += (_, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                if (getValue() == value)
                {
                    using var ring = new Pen(Theme.Accent, 1.6f);
                    const float ringSize = btnSize - 3f;
                    const float ringOff = (btnSize - ringSize) / 2f;
                    e.Graphics.DrawEllipse(ring, ringOff, ringOff, ringSize, ringSize);
                }
                using var brush = new SolidBrush(Color.White);
                float off = (btnSize - dotSize) / 2f;
                e.Graphics.FillEllipse(brush, off, off, dotSize, dotSize);
            };
            panel.Click += (_, _) =>
            {
                setValue(value);
                foreach (var p in buttons) p.Invalidate();
            };
            buttons.Add(panel);
            row.Controls.Add(panel);
        }

        return row;
    }

    /// <summary>Small custom-drawn horizontal slider (0-100, snapped to 5% steps) matching the rest of
    /// the toolbar's fully custom-painted look — a native TrackBar would render with OS chrome that
    /// doesn't fit the dark pill design.</summary>
    private Panel BuildOpacitySlider(Func<int> getValue, Action<int> setValue)
    {
        const int width = 84, height = 18;
        var panel = new Panel { Size = new Size(width, height), Margin = new Padding(4, 11, 4, 11), Cursor = Cursors.Hand };
        AttachTooltip(panel, "Opacity");

        void SetFromMouseX(int mouseX)
        {
            float frac = Math.Clamp(mouseX / (float)width, 0f, 1f);
            int value = Math.Clamp((int)Math.Round(frac * 100 / 5.0) * 5, 5, 100);
            setValue(value);
            panel.Invalidate();
        }

        bool dragging = false;
        panel.MouseDown += (_, e) => { dragging = true; SetFromMouseX(e.X); };
        panel.MouseMove += (_, e) => { if (dragging) SetFromMouseX(e.X); };
        panel.MouseUp += (_, _) => dragging = false;
        panel.MouseLeave += (_, _) => dragging = false;

        panel.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int v = getValue();

            using var trackPath = Geometry.RoundedRect(new Rectangle(0, height / 2 - 3, width, 6), 3);
            using var trackBrush = new SolidBrush(Theme.CardBg);
            e.Graphics.FillPath(trackBrush, trackPath);

            int fillWidth = Math.Max(6, (int)Math.Round(width * v / 100f));
            using var fillPath = Geometry.RoundedRect(new Rectangle(0, height / 2 - 3, fillWidth, 6), 3);
            using var fillBrush = new SolidBrush(Theme.Accent);
            e.Graphics.FillPath(fillBrush, fillPath);

            float thumbX = Math.Clamp(fillWidth, 4, width - 4);
            using var thumbBrush = new SolidBrush(Color.White);
            e.Graphics.FillEllipse(thumbBrush, thumbX - 5, height / 2f - 5, 10, 10);
        };

        return panel;
    }

    // ---- Canvas control ----

    private sealed class Canvas : Control
    {
        private readonly EditorForm _owner;

        public Canvas(EditorForm owner)
        {
            _owner = owner;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            float zoom = (float)_owner._zoom;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            // Downsampling (zoomed out, e.g. fitting an oversized capture) wants smoothing to avoid
            // aliasing; zoomed-in inspection wants crisp source pixels rather than a blurred blow-up.
            e.Graphics.InterpolationMode = zoom < 1f ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;

            // Drawn at a fixed on-screen tile size (before the zoom transform) so a marquee-deleted,
            // now-transparent region of the image reads clearly as "empty" at any zoom level.
            DrawCheckerboard(e.Graphics, new Rectangle(0, 0, Width, Height));

            var state = e.Graphics.Save();
            e.Graphics.ScaleTransform(zoom, zoom);
            e.Graphics.DrawImage(_owner._baseImage, Point.Empty);
            foreach (var ann in _owner._annotations) ann.Draw(e.Graphics);
            _owner._inProgress?.Draw(e.Graphics);

            if (_owner._currentTool == ToolType.Select)
            {
                // Pen widths and handle sizes are divided by zoom so they stay a constant size on
                // screen instead of growing/shrinking with the image underneath them.
                using var pen = new Pen(Theme.Accent, 1.6f / zoom) { DashStyle = DashStyle.Dash };
                foreach (var selected in _owner._selectedAnnotations)
                {
                    int inflate = Math.Max(1, (int)Math.Round(6 / zoom));
                    var bounds = Rectangle.Inflate(selected.GetBounds(), inflate, inflate);
                    e.Graphics.DrawRectangle(pen, bounds);
                }

                if (_owner._selectedAnnotations.Count == 1 && _owner._selectedAnnotations[0] is ArrowAnnotation arrow)
                {
                    using var handleFill = new SolidBrush(Theme.SidebarBg);
                    using var handleRing = new Pen(Theme.Accent, 1.6f / zoom);
                    float r = ArrowHandleRadius / zoom;
                    var mid = arrow.MidPoint ?? Geometry.Midpoint(arrow.Start, arrow.End);
                    foreach (var handle in new[] { arrow.Start, mid, arrow.End })
                    {
                        e.Graphics.FillEllipse(handleFill, handle.X - r, handle.Y - r, r * 2, r * 2);
                        e.Graphics.DrawEllipse(handleRing, handle.X - r, handle.Y - r, r * 2, r * 2);
                    }
                }
                else if (_owner._selectedAnnotations.Count == 1 && _owner._selectedAnnotations[0] is IResizableBounds rb)
                {
                    using var handleFill = new SolidBrush(Theme.SidebarBg);
                    using var handleRing = new Pen(Theme.Accent, 1.6f / zoom);
                    float r = ArrowHandleRadius / zoom;
                    foreach (BoundsHandle h in Enum.GetValues<BoundsHandle>())
                    {
                        var handle = GetBoundsHandlePoint(rb.Bounds, h);
                        e.Graphics.FillEllipse(handleFill, handle.X - r, handle.Y - r, r * 2, r * 2);
                        e.Graphics.DrawEllipse(handleRing, handle.X - r, handle.Y - r, r * 2, r * 2);
                    }
                }
                else if (_owner._selectedAnnotations.Count == 1 && _owner._selectedAnnotations[0] is IResizableEndpoints rl)
                {
                    using var handleFill = new SolidBrush(Theme.SidebarBg);
                    using var handleRing = new Pen(Theme.Accent, 1.6f / zoom);
                    float r = ArrowHandleRadius / zoom;
                    foreach (var handle in new[] { rl.Start, rl.End })
                    {
                        e.Graphics.FillEllipse(handleFill, handle.X - r, handle.Y - r, r * 2, r * 2);
                        e.Graphics.DrawEllipse(handleRing, handle.X - r, handle.Y - r, r * 2, r * 2);
                    }
                }

                if (_owner._marqueeActive)
                {
                    var rect = NormalizeRect(_owner._marqueeStart, _owner._marqueeEnd);
                    using var fillBrush = new SolidBrush(Color.FromArgb(40, Theme.Accent));
                    using var borderPen = new Pen(Theme.Accent, 1f / zoom) { DashStyle = DashStyle.Dash };
                    e.Graphics.FillRectangle(fillBrush, rect);
                    e.Graphics.DrawRectangle(borderPen, rect);
                }
            }

            if (_owner._currentTool == ToolType.Marquee)
            {
                // While actively dragging: a light fill (matches the Select tool's marquee) so the
                // in-progress drag reads clearly. Once released, just the dashed outline is kept so the
                // selected pixels underneath stay fully visible while it's active for cut/copy/delete.
                if (_owner._pixelSelecting)
                {
                    var rect = NormalizeRect(_owner._pixelSelectStart, _owner._pixelSelectEnd);
                    using var fillBrush = new SolidBrush(Color.FromArgb(40, Theme.Accent));
                    using var borderPen = new Pen(Theme.Accent, 1f / zoom) { DashStyle = DashStyle.Dash };
                    e.Graphics.FillRectangle(fillBrush, rect);
                    e.Graphics.DrawRectangle(borderPen, rect);
                }
                else if (_owner._pixelSelection is { } selected)
                {
                    using var borderPen = new Pen(Theme.Accent, 1.6f / zoom) { DashStyle = DashStyle.Dash };
                    e.Graphics.DrawRectangle(borderPen, selected);
                }
            }

            e.Graphics.Restore(state);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            _owner.CanvasMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e) => _owner.CanvasMouseMove(e);
        protected override void OnMouseUp(MouseEventArgs e) => _owner.CanvasMouseUp(e);

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            _owner.HandleZoomWheel(e, new Point(Left + e.X, Top + e.Y));
            base.OnMouseWheel(e);
        }
    }

    private void CanvasMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        CommitTextBox();

        if (_currentTool == ToolType.Hand)
        {
            _panning = true;
            _panStartMouseScreen = Cursor.Position;
            _panStartCanvasLocation = _canvas.Location;
            _canvas.Capture = true;
            return;
        }

        var p = ToImagePoint(e.Location);

        if (_currentTool == ToolType.Marquee)
        {
            _pixelSelecting = true;
            _pixelSelectStart = p;
            _pixelSelectEnd = p;
            _pixelSelection = null;
            _canvas.Invalidate();
            return;
        }

        if (_currentTool == ToolType.Select)
        {
            // Hidden for the duration of any drag (move, arrow-handle bend, or rubber-band marquee) so
            // it doesn't sit stale over a shape that's actively being repositioned; it reappears,
            // repositioned, once CanvasMouseUp settles on a final selection.
            HideElementBar();

            if (_selectedAnnotations.Count == 1 && _selectedAnnotations[0] is ArrowAnnotation selectedArrow)
            {
                var handle = HitTestArrowHandle(selectedArrow, p, ArrowHandleHitRadius / _zoom);
                if (handle != null)
                {
                    _draggingArrowHandle = handle;
                    _canvas.Invalidate();
                    return;
                }
            }
            else if (_selectedAnnotations.Count == 1 && _selectedAnnotations[0] is IResizableBounds selectedBounds)
            {
                var handle = HitTestBoundsHandle(selectedBounds.Bounds, p, ArrowHandleHitRadius / _zoom);
                if (handle != null)
                {
                    _draggingBoundsHandle = handle;
                    _resizeStartBounds = selectedBounds.Bounds;
                    _canvas.Invalidate();
                    return;
                }
            }
            else if (_selectedAnnotations.Count == 1 && _selectedAnnotations[0] is IResizableEndpoints selectedLine)
            {
                var endpoint = HitTestLineEndpoint(selectedLine, p, ArrowHandleHitRadius / _zoom);
                if (endpoint != null)
                {
                    _draggingLineEndpoint = endpoint;
                    _canvas.Invalidate();
                    return;
                }
            }

            var hit = HitTestAnnotations(p);
            if (hit != null)
            {
                // Clicking an item already part of the current multi-selection keeps the whole
                // group selected, so you can drag the group by grabbing any one of its members.
                if (!_selectedAnnotations.Contains(hit))
                {
                    _selectedAnnotations.Clear();
                    _selectedAnnotations.Add(hit);
                }
                _movingSelection = true;
                _moveDragStart = p;
                _canvas.Invalidate();
                return;
            }

            // Clicked empty space: clear selection and start a rubber-band marquee. A plain click
            // with no drag just ends up selecting nothing, matching Windows Explorer's behavior.
            _selectedAnnotations.Clear();
            _marqueeActive = true;
            _marqueeStart = p;
            _marqueeEnd = p;
            _canvas.Invalidate();
            return;
        }

        _dragStart = p;
        _inProgress = _currentTool switch
        {
            ToolType.Rectangle => new RectangleAnnotation { Color = _currentColor, Thickness = _currentThickness, Bounds = new Rectangle(p, Size.Empty) },
            ToolType.Ellipse => new EllipseAnnotation { Color = _currentColor, Thickness = _currentThickness, Bounds = new Rectangle(p, Size.Empty) },
            ToolType.Highlight => new HighlightAnnotation { Color = _currentColor, Thickness = _currentThickness, Bounds = new Rectangle(p, Size.Empty) },
            ToolType.Line => new LineAnnotation { Color = _currentColor, Thickness = _currentThickness, Start = p, End = p },
            ToolType.Arrow => new ArrowAnnotation { Color = _currentColor, Thickness = _currentThickness, Start = p, End = p },
            ToolType.Pen => CreateFreehand(p),
            ToolType.Text => StartTextInput(e.Location, p),
            _ => null
        };
        _canvas.Invalidate();
    }

    private Annotation? HitTestAnnotations(Point p)
    {
        for (int i = _annotations.Count - 1; i >= 0; i--)
            if (_annotations[i].HitTest(p)) return _annotations[i];
        return null;
    }

    /// <summary>Selects every annotation whose bounds intersect the current marquee rectangle (Windows-Explorer-style rubber-band selection).</summary>
    private void UpdateMarqueeSelection()
    {
        var rect = NormalizeRect(_marqueeStart, _marqueeEnd);
        _selectedAnnotations.Clear();
        foreach (var ann in _annotations)
        {
            if (ann.GetBounds().IntersectsWith(rect)) _selectedAnnotations.Add(ann);
        }
    }

    private FreehandAnnotation CreateFreehand(Point start)
    {
        var fh = new FreehandAnnotation { Color = _currentColor, Thickness = _currentThickness };
        fh.Points.Add(start);
        return fh;
    }

    private void CanvasMouseMove(MouseEventArgs e)
    {
        if (_panning)
        {
            var mouseNow = Cursor.Position;
            int dx = mouseNow.X - _panStartMouseScreen.X;
            int dy = mouseNow.Y - _panStartMouseScreen.Y;
            var target = new Point(_panStartCanvasLocation.X + dx, _panStartCanvasLocation.Y + dy);
            _canvas.Location = ClampCanvasToHost(target);
            _canvasHost.Invalidate();
            return;
        }

        var p = ToImagePoint(e.Location);

        if (_currentTool == ToolType.Marquee)
        {
            if (_pixelSelecting)
            {
                _pixelSelectEnd = p;
                _canvas.Invalidate();
            }
            return;
        }

        if (_currentTool == ToolType.Select)
        {
            if (_draggingArrowHandle != null && _selectedAnnotations.Count == 1 && _selectedAnnotations[0] is ArrowAnnotation bendingArrow)
            {
                switch (_draggingArrowHandle)
                {
                    case ArrowHandle.Start: bendingArrow.Start = p; break;
                    case ArrowHandle.End: bendingArrow.End = p; break;
                    case ArrowHandle.Mid: bendingArrow.MidPoint = p; break;
                }
                _canvas.Invalidate();
            }
            else if (_draggingBoundsHandle != null && _selectedAnnotations.Count == 1 && _selectedAnnotations[0] is IResizableBounds resizingBounds)
            {
                resizingBounds.Bounds = ResizeBounds(_resizeStartBounds, _draggingBoundsHandle.Value, p);
                _canvas.Invalidate();
            }
            else if (_draggingLineEndpoint != null && _selectedAnnotations.Count == 1 && _selectedAnnotations[0] is IResizableEndpoints resizingLine)
            {
                if (_draggingLineEndpoint == LineEndpoint.Start) resizingLine.Start = p; else resizingLine.End = p;
                _canvas.Invalidate();
            }
            else if (_movingSelection && _selectedAnnotations.Count > 0)
            {
                int dx = p.X - _moveDragStart.X, dy = p.Y - _moveDragStart.Y;
                foreach (var ann in _selectedAnnotations) ann.Move(dx, dy);
                _moveDragStart = p;
                _canvas.Invalidate();
            }
            else if (_marqueeActive)
            {
                _marqueeEnd = p;
                UpdateMarqueeSelection();
                _canvas.Invalidate();
            }
            else
            {
                UpdateSelectCursor(p);
            }
            return;
        }

        if (_inProgress == null) return;

        switch (_inProgress)
        {
            case RectangleAnnotation r: r.Bounds = NormalizeRect(_dragStart, p); break;
            case EllipseAnnotation el: el.Bounds = NormalizeRect(_dragStart, p); break;
            case HighlightAnnotation h: h.Bounds = NormalizeRect(_dragStart, p); break;
            case LineAnnotation ln: ln.End = p; break;
            case ArrowAnnotation ar: ar.End = p; break;
            case FreehandAnnotation fh: fh.Points.Add(p); break;
        }
        _canvas.Invalidate();
    }

    private void CanvasMouseUp(MouseEventArgs e)
    {
        if (_panning)
        {
            _panning = false;
            _canvas.Capture = false;
            return;
        }

        if (_currentTool == ToolType.Marquee)
        {
            if (_pixelSelecting)
            {
                _pixelSelecting = false;
                var rect = NormalizeRect(_pixelSelectStart, _pixelSelectEnd);
                rect.Intersect(new Rectangle(Point.Empty, _baseImage.Size));
                _pixelSelection = rect.Width > 1 && rect.Height > 1 ? rect : null;
                _canvas.Invalidate();
            }
            return;
        }

        if (_currentTool == ToolType.Select)
        {
            _movingSelection = false;
            _draggingArrowHandle = null;
            _draggingBoundsHandle = null;
            _draggingLineEndpoint = null;
            if (_marqueeActive)
            {
                _marqueeActive = false;
                _canvas.Invalidate();
            }
            RefreshElementBar();
            return;
        }

        if (_inProgress == null) return;

        bool keep = _inProgress switch
        {
            RectangleAnnotation r => r.Bounds.Width > 2 && r.Bounds.Height > 2,
            EllipseAnnotation el => el.Bounds.Width > 2 && el.Bounds.Height > 2,
            HighlightAnnotation h => h.Bounds.Width > 2 && h.Bounds.Height > 2,
            LineAnnotation ln => ln.Start != ln.End,
            ArrowAnnotation ar => ar.Start != ar.End,
            FreehandAnnotation fh => fh.Points.Count > 1,
            _ => false
        };

        if (keep)
        {
            _annotations.Add(_inProgress);
            var drawn = _inProgress;
            SelectTool(ToolType.Select);
            _selectedAnnotations.Add(drawn);
            RefreshElementBar();
        }
        _inProgress = null;
        _canvas.Invalidate();
    }

    private static Rectangle NormalizeRect(Point a, Point b)
    {
        int x = Math.Min(a.X, b.X);
        int y = Math.Min(a.Y, b.Y);
        int w = Math.Abs(a.X - b.X);
        int h = Math.Abs(a.Y - b.Y);
        return new Rectangle(x, y, w, h);
    }

    // ---- Text tool ----

    private Annotation? StartTextInput(Point screenLocation, Point imageLocation)
    {
        _activeTextImagePosition = imageLocation;
        var tb = new TextBox
        {
            Location = screenLocation,
            Font = new Font(HandFont.FamilyName, 14f, FontStyle.Regular),
            ForeColor = _currentColor,
            BorderStyle = BorderStyle.FixedSingle,
            MinimumSize = new Size(140, 24),
            BackColor = Color.White
        };
        tb.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                CommitTextBox();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                var box = _activeTextBox;
                _activeTextBox = null;
                if (box != null)
                {
                    _canvas.Controls.Remove(box);
                    box.Dispose();
                }
            }
        };
        tb.LostFocus += (_, _) => CommitTextBox();

        _activeTextBox = tb;
        _canvas.Controls.Add(tb);
        tb.BringToFront();
        tb.Focus();
        return null;
    }

    private void CommitTextBox()
    {
        var tb = _activeTextBox;
        if (tb == null) return;
        _activeTextBox = null;

        if (!string.IsNullOrWhiteSpace(tb.Text))
        {
            var textAnnotation = new TextAnnotation
            {
                Color = _currentColor,
                Position = _activeTextImagePosition,
                Text = tb.Text,
                FontSize = 22f
            };
            _annotations.Add(textAnnotation);
            SelectTool(ToolType.Select);
            _selectedAnnotations.Add(textAnnotation);
        }
        _canvas.Controls.Remove(tb);
        tb.Dispose();
        _canvas.Invalidate();
    }

    // ---- Output ----

    private Bitmap Flatten()
    {
        var result = new Bitmap(_baseImage.Width, _baseImage.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(result);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.DrawImage(_baseImage, Point.Empty);
        foreach (var ann in _annotations) ann.Draw(g);
        return result;
    }

    /// <summary>Flattens just the given image-space rectangle (base image + annotations within it),
    /// used by the Marquee tool's Cut/Copy so a selected chunk includes whatever's drawn on top of it.</summary>
    private Bitmap FlattenRegion(Rectangle rect)
    {
        var result = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(result);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.DrawImage(_baseImage, new Rectangle(0, 0, rect.Width, rect.Height), rect, GraphicsUnit.Pixel);
        g.TranslateTransform(-rect.X, -rect.Y);
        foreach (var ann in _annotations) ann.Draw(g);
        return result;
    }

    private void CopyPixelSelectionToClipboard()
    {
        if (_pixelSelection is not { } rect) return;
        using var cropped = FlattenRegion(rect);
        Clipboard.SetImage(cropped);
    }

    private void CutPixelSelection()
    {
        if (_pixelSelection == null) return;
        CopyPixelSelectionToClipboard();
        DeletePixelSelection();
    }

    /// <summary>Punches a transparent hole in the base image where the selection was, so the region
    /// can be composited away (e.g. after moving it into another screenshot via cut/paste).</summary>
    private void DeletePixelSelection()
    {
        if (_pixelSelection is not { } rect) return;
        using (var g = Graphics.FromImage(_baseImage))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            using var clearBrush = new SolidBrush(Color.Transparent);
            g.FillRectangle(clearBrush, rect);
        }
        _pixelSelection = null;
        _canvas.Invalidate();
    }

    /// <summary>Pastes whatever image is on the clipboard as a movable annotation layer, landing
    /// centered in the current viewport — this is what lets a region cut/copied in one editor window
    /// be dropped into another to compose multiple screenshots into one.</summary>
    private void PasteImageFromClipboard()
    {
        if (_activeTextBox != null || !Clipboard.ContainsImage()) return;
        using var clipImage = Clipboard.GetImage();
        if (clipImage == null) return;
        var pasted = new Bitmap(clipImage);

        var viewportCenterHost = new Point(_canvasHost.ClientSize.Width / 2, _canvasHost.ClientSize.Height / 2);
        var centerImg = ToImagePoint(new Point(viewportCenterHost.X - _canvas.Location.X, viewportCenterHost.Y - _canvas.Location.Y));
        var bounds = new Rectangle(centerImg.X - pasted.Width / 2, centerImg.Y - pasted.Height / 2, pasted.Width, pasted.Height);

        var pastedAnnotation = new ImageAnnotation { Image = pasted, Bounds = bounds };
        _annotations.Add(pastedAnnotation);
        SelectTool(ToolType.Select);
        _selectedAnnotations.Add(pastedAnnotation);
        _canvas.Invalidate();
        RefreshElementBar();
    }

    private void CopyToClipboard()
    {
        using var flat = Flatten();
        Clipboard.SetImage(flat);
    }

    private void Save()
    {
        if (_savePath == null)
        {
            SaveAs();
            return;
        }

        using var flat = Flatten();
        var format = Path.GetExtension(_savePath).ToLowerInvariant() is ".jpg" or ".jpeg"
            ? ImageFormat.Jpeg
            : ImageFormat.Png;
        flat.Save(_savePath, format);
    }

    private void SaveAs()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "PNG Image|*.png|JPEG Image|*.jpg",
            FileName = Path.GetFileName(_savePath) ?? $"SnapTool_{DateTime.Now:yyyyMMdd_HHmmss}.png"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        using var flat = Flatten();
        var format = dlg.FilterIndex == 2 ? ImageFormat.Jpeg : ImageFormat.Png;
        flat.Save(dlg.FileName, format);
        _savePath = dlg.FileName;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        _toolbarForm.Close();
        _overflowPopup?.Close();
        HideElementBar();
        _tipTimer.Dispose();
        _hoverTip?.Close();
        foreach (var ann in _annotations) DisposeIfImage(ann);
        _baseImage.Dispose();
    }
}
