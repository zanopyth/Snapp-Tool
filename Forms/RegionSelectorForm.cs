using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using SnapTool.Capture;

namespace SnapTool.Forms;

/// <summary>
/// Fullscreen overlay for region capture. Hovering highlights the window/control under the
/// cursor (snapping to real UI boundaries); a plain click captures that highlighted area,
/// while dragging draws a free-form custom rectangle instead. Up/Down arrows widen or narrow
/// the snap target between the control and its parent windows.
/// </summary>
internal sealed class RegionSelectorForm : Form
{
    private const int DragThreshold = 4;

    private readonly Bitmap _fullImage;
    private readonly Bitmap _dimmedImage;
    private readonly List<(IntPtr Handle, Rectangle Bounds)> _windows;

    private bool _mouseDown;
    private bool _manualDragging;
    private Point _mouseDownPoint;
    private Rectangle _manualSelection;

    private List<Rectangle> _hoverChain = new();
    private int _hoverIndex = -1;
    private Point _lastHoverScreenPoint = new(int.MinValue, int.MinValue);

    public Bitmap? SelectedBitmap { get; private set; }
    public Rectangle SelectedScreenBounds { get; private set; }

    public RegionSelectorForm()
    {
        var bounds = CaptureService.GetVirtualScreenBounds();
        _fullImage = CaptureService.CaptureRect(bounds);
        _dimmedImage = CreateDimmedImage(_fullImage);
        _windows = WindowDetector.GetTopLevelWindows();

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        TopMost = true;
        ShowInTaskbar = false;
        Cursor = Cursors.Cross;
        KeyPreview = true;
        DoubleBuffered = true;
        BackColor = Color.Black;
    }

    private static Bitmap CreateDimmedImage(Bitmap source)
    {
        var dimmed = new Bitmap(source.Width, source.Height);
        using var g = Graphics.FromImage(dimmed);
        g.DrawImage(source, 0, 0);
        using var overlay = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
        g.FillRectangle(overlay, 0, 0, source.Width, source.Height);
        return dimmed;
    }

    private Rectangle ScreenToLocal(Rectangle screenRect)
    {
        var local = new Rectangle(screenRect.X - Bounds.X, screenRect.Y - Bounds.Y, screenRect.Width, screenRect.Height);
        local.Intersect(new Rectangle(Point.Empty, Bounds.Size));
        return local;
    }

    private Rectangle? CurrentHoverRect =>
        (_hoverIndex >= 0 && _hoverIndex < _hoverChain.Count) ? ScreenToLocal(_hoverChain[_hoverIndex]) : null;

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.DrawImage(_dimmedImage, Point.Empty);

        Rectangle? active = _manualDragging ? _manualSelection : CurrentHoverRect;

        if (active is { Width: > 0, Height: > 0 } rect)
        {
            e.Graphics.SetClip(rect);
            e.Graphics.DrawImage(_fullImage, Point.Empty);
            e.Graphics.ResetClip();

            var borderColor = _manualDragging ? Color.DeepSkyBlue : Color.FromArgb(255, 250, 204, 21);
            using var pen = new Pen(borderColor, 2) { DashStyle = _manualDragging ? System.Drawing.Drawing2D.DashStyle.Solid : System.Drawing.Drawing2D.DashStyle.Dash };
            e.Graphics.DrawRectangle(pen, rect);

            var sizeText = $"{rect.Width} x {rect.Height}";
            var textPos = new Point(rect.X, Math.Max(0, rect.Y - 22));
            using var textBrush = new SolidBrush(Color.White);
            using var bgBrush = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
            var textSize = e.Graphics.MeasureString(sizeText, Font);
            e.Graphics.FillRectangle(bgBrush, textPos.X, textPos.Y, textSize.Width + 6, textSize.Height + 2);
            e.Graphics.DrawString(sizeText, Font, textBrush, textPos.X + 3, textPos.Y + 1);
        }

        using var hint = new SolidBrush(Color.FromArgb(220, 255, 255, 255));
        var hintText = _manualDragging
            ? "Drag to select a custom area  •  Esc to cancel"
            : "Click to capture the highlighted area  •  Drag for a custom area  •  ↑/↓ to widen/narrow  •  Esc to cancel";
        e.Graphics.DrawString(hintText, Font, hint, 12, 12);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _mouseDown = true;
        _manualDragging = false;
        _mouseDownPoint = e.Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_mouseDown)
        {
            int dx = Math.Abs(e.X - _mouseDownPoint.X);
            int dy = Math.Abs(e.Y - _mouseDownPoint.Y);

            if (!_manualDragging && (dx > DragThreshold || dy > DragThreshold))
            {
                _manualDragging = true;
            }

            if (_manualDragging)
            {
                _manualSelection = NormalizeRect(_mouseDownPoint, e.Location);
                Invalidate();
            }
            return;
        }

        UpdateHover(PointToScreen(e.Location));
    }

    private void UpdateHover(Point screenPoint)
    {
        int dx = Math.Abs(screenPoint.X - _lastHoverScreenPoint.X);
        int dy = Math.Abs(screenPoint.Y - _lastHoverScreenPoint.Y);
        if (dx < 3 && dy < 3) return;
        _lastHoverScreenPoint = screenPoint;

        var hit = WindowDetector.HitTest(_windows, screenPoint);
        if (hit == null)
        {
            _hoverChain = new List<Rectangle>();
            _hoverIndex = -1;
        }
        else
        {
            _hoverChain = WindowDetector.GetHoverChain(hit.Value.Handle, hit.Value.Bounds, screenPoint);
            _hoverIndex = _hoverChain.Count - 1;
        }
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!_mouseDown || e.Button != MouseButtons.Left) return;
        _mouseDown = false;

        if (_manualDragging)
        {
            _manualDragging = false;
            FinalizeSelection(_manualSelection);
        }
        else if (CurrentHoverRect is { } rect)
        {
            FinalizeSelection(rect);
        }
    }

    private void FinalizeSelection(Rectangle rect)
    {
        if (rect.Width < 3 || rect.Height < 3)
        {
            Invalidate();
            return;
        }

        SelectedBitmap = _fullImage.Clone(rect, _fullImage.PixelFormat);
        SelectedScreenBounds = new Rectangle(rect.X + Bounds.X, rect.Y + Bounds.Y, rect.Width, rect.Height);
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Escape:
                DialogResult = DialogResult.Cancel;
                Close();
                break;
            case Keys.Up:
                if (_hoverChain.Count > 0)
                {
                    _hoverIndex = Math.Max(0, _hoverIndex - 1);
                    Invalidate();
                }
                break;
            case Keys.Down:
                if (_hoverChain.Count > 0)
                {
                    _hoverIndex = Math.Min(_hoverChain.Count - 1, _hoverIndex + 1);
                    Invalidate();
                }
                break;
        }
    }

    private static Rectangle NormalizeRect(Point a, Point b)
    {
        int x = Math.Min(a.X, b.X);
        int y = Math.Min(a.Y, b.Y);
        int w = Math.Abs(a.X - b.X);
        int h = Math.Abs(a.Y - b.Y);
        return new Rectangle(x, y, w, h);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fullImage.Dispose();
            _dimmedImage.Dispose();
        }
        base.Dispose(disposing);
    }
}
