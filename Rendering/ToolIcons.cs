using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace SnapTool.Rendering;

/// <summary>Small stroke-style glyph icons for the editor toolbar, drawn on the fly so no image assets are needed.</summary>
internal static class ToolIcons
{
    private const int Size = 24;

    public static Bitmap For(ToolType tool) => tool switch
    {
        ToolType.Select => Build(g =>
        {
            var pts = new[]
            {
                new PointF(5f, 3f), new PointF(5f, 17.5f), new PointF(8.7f, 14f),
                new PointF(11.2f, 19.6f), new PointF(13.6f, 18.4f), new PointF(11.1f, 13.1f),
                new PointF(16.2f, 13.1f)
            };
            using var brush = new SolidBrush(Color.White);
            using var pen = new Pen(Color.FromArgb(255, 30, 30, 34), 1.3f) { LineJoin = LineJoin.Round };
            g.FillPolygon(brush, pts);
            g.DrawPolygon(pen, pts);
        }),
        ToolType.Marquee => Build(g =>
        {
            using var pen = new Pen(Color.White, 1.6f) { DashStyle = DashStyle.Dash };
            using var path = RoundedRect(new RectangleF(4, 5, 16, 14), 2.5f);
            g.DrawPath(pen, path);
        }),
        ToolType.Hand => Build(g =>
        {
            using var pen = LinePen();
            using var palm = RoundedRect(new RectangleF(4.5f, 11f, 15f, 8.5f), 3.5f);
            g.DrawPath(pen, palm);
            for (int i = 0; i < 4; i++)
            {
                float x = 6.3f + i * 3.4f;
                float h = i == 0 ? 7f : 9f - i * 0.4f;
                using var finger = RoundedRect(new RectangleF(x, 12f - h, 2.4f, h), 1.2f);
                g.DrawPath(pen, finger);
            }
        }),
        ToolType.Rectangle => Build(g =>
        {
            using var pen = LinePen();
            using var path = RoundedRect(new RectangleF(4, 6, 16, 12), 3);
            g.DrawPath(pen, path);
        }),
        ToolType.Ellipse => Build(g =>
        {
            using var pen = LinePen();
            g.DrawEllipse(pen, 4, 5, 16, 14);
        }),
        ToolType.Line => Build(g =>
        {
            using var pen = LinePen();
            g.DrawLine(pen, 4, 19, 20, 5);
        }),
        ToolType.Arrow => Build(g =>
        {
            using var pen = LinePen();
            pen.CustomEndCap = new AdjustableArrowCap(2.6f, 3f);
            g.DrawLine(pen, 4, 19, 18, 5);
        }),
        ToolType.Highlight => Build(g =>
        {
            using var path = RoundedRect(new RectangleF(4, 8, 16, 9), 3);
            using var brush = new SolidBrush(Color.FromArgb(210, 250, 204, 21));
            g.FillPath(brush, path);
        }),
        ToolType.Pen => Build(g =>
        {
            using var pen = LinePen();
            var pts = new[] { new Point(4, 18), new Point(8, 10), new Point(12, 15), new Point(16, 6), new Point(20, 10) };
            g.DrawCurve(pen, pts, 0.6f);
        }),
        ToolType.Text => Build(g =>
        {
            using var font = new Font("Segoe UI", 13f, FontStyle.Bold);
            using var brush = new SolidBrush(Color.White);
            const string s = "A";
            var measured = g.MeasureString(s, font);
            g.DrawString(s, font, brush, (Size - measured.Width) / 2f, (Size - measured.Height) / 2f - 1);
        }),
        _ => Build(_ => { })
    };

    public static Bitmap Undo() => Build(g =>
    {
        using var pen = LinePen();
        g.DrawArc(pen, 5, 6, 13, 13, -40, 260);
        using var brush = new SolidBrush(Color.White);
        g.FillPolygon(brush, new[] { new Point(4, 7), new Point(10, 6), new Point(7, 12) });
    });

    public static Bitmap Copy() => Build(g =>
    {
        using var pen = LinePen();
        using var back = RoundedRect(new RectangleF(8, 4, 12, 13), 2.5f);
        g.DrawPath(pen, back);
        using var frontBg = new SolidBrush(Color.FromArgb(255, 40, 40, 46));
        using var front = RoundedRect(new RectangleF(4, 8, 12, 13), 2.5f);
        g.FillPath(frontBg, front);
        g.DrawPath(pen, front);
    });

    public static Bitmap Save() => Build(g =>
    {
        using var pen = LinePen();
        pen.CustomEndCap = new AdjustableArrowCap(3f, 3f);
        g.DrawLine(pen, 12, 4, 12, 14);

        using var trayPen = LinePen();
        g.DrawLines(trayPen, new[] { new PointF(5, 13), new PointF(5, 20), new PointF(19, 20), new PointF(19, 13) });
    });

    public static Bitmap SaveAs() => Build(g =>
    {
        using var pen = LinePen();
        pen.CustomEndCap = new AdjustableArrowCap(2.6f, 2.6f);
        g.DrawLine(pen, 9, 3, 9, 12);

        using var trayPen = LinePen();
        g.DrawLines(trayPen, new[] { new PointF(3, 11), new PointF(3, 17), new PointF(15, 17), new PointF(15, 11) });

        using var badgeBrush = new SolidBrush(Color.FromArgb(255, 250, 204, 21));
        g.FillEllipse(badgeBrush, 13, 11, 10, 10);
        using var plusPen = new Pen(Color.FromArgb(255, 40, 40, 46), 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(plusPen, 18f, 13.2f, 18f, 17.8f);
        g.DrawLine(plusPen, 15.7f, 15.5f, 20.3f, 15.5f);
    });

    public static Bitmap Close() => Build(g =>
    {
        using var pen = LinePen();
        g.DrawLine(pen, 6, 6, 18, 18);
        g.DrawLine(pen, 18, 6, 6, 18);
    });

    public static Bitmap Duplicate() => Build(g =>
    {
        using var pen = LinePen();
        using var back = RoundedRect(new RectangleF(4, 4, 12, 12), 2.5f);
        g.DrawPath(pen, back);
        using var frontBg = new SolidBrush(Color.FromArgb(255, 40, 40, 46));
        using var front = RoundedRect(new RectangleF(8, 8, 12, 12), 2.5f);
        g.FillPath(frontBg, front);
        g.DrawPath(pen, front);
    });

    public static Bitmap BringToFront() => Build(g =>
    {
        using var backPen = new Pen(Color.FromArgb(140, 255, 255, 255), 1.6f) { LineJoin = LineJoin.Round };
        using var back = RoundedRect(new RectangleF(8, 8, 12, 12), 2.5f);
        g.DrawPath(backPen, back);

        using var frontBg = new SolidBrush(Color.White);
        using var frontBorder = new Pen(Color.FromArgb(255, 30, 30, 34), 1.2f) { LineJoin = LineJoin.Round };
        using var front = RoundedRect(new RectangleF(4, 4, 12, 12), 2.5f);
        g.FillPath(frontBg, front);
        g.DrawPath(frontBorder, front);
    });

    public static Bitmap Trash() => Build(g =>
    {
        using var pen = LinePen();
        g.DrawLine(pen, 5, 7.5f, 19, 7.5f);
        g.DrawLine(pen, 9.5f, 7.5f, 10f, 4.5f);
        g.DrawLine(pen, 14.5f, 7.5f, 14f, 4.5f);
        g.DrawLine(pen, 10f, 4.5f, 14f, 4.5f);

        using var body = new GraphicsPath();
        body.AddLine(6.5f, 7.5f, 7.5f, 20f);
        body.AddLine(7.5f, 20f, 16.5f, 20f);
        body.AddLine(16.5f, 20f, 17.5f, 7.5f);
        g.DrawPath(pen, body);

        g.DrawLine(pen, 10.5f, 10.5f, 11f, 17f);
        g.DrawLine(pen, 13.5f, 10.5f, 13f, 17f);
    });

    public static Bitmap ThreeDots() => Build(g =>
    {
        using var brush = new SolidBrush(Color.White);
        float y = Size / 2f - 1.6f;
        g.FillEllipse(brush, 4f, y, 3.2f, 3.2f);
        g.FillEllipse(brush, 10.4f, y, 3.2f, 3.2f);
        g.FillEllipse(brush, 16.8f, y, 3.2f, 3.2f);
    });

    public static Bitmap ChevronUp() => Build(g =>
    {
        using var pen = LinePen();
        g.DrawLines(pen, new[] { new PointF(5f, 15f), new PointF(12f, 8f), new PointF(19f, 15f) });
    });

    public static Bitmap ChevronDown() => Build(g =>
    {
        using var pen = LinePen();
        g.DrawLines(pen, new[] { new PointF(5f, 8f), new PointF(12f, 15f), new PointF(19f, 8f) });
    });

    public static Bitmap ChevronLeft() => Build(g =>
    {
        using var pen = LinePen();
        g.DrawLines(pen, new[] { new PointF(15f, 5f), new PointF(8f, 12f), new PointF(15f, 19f) });
    });

    public static Bitmap ChevronRight() => Build(g =>
    {
        using var pen = LinePen();
        g.DrawLines(pen, new[] { new PointF(8f, 5f), new PointF(15f, 12f), new PointF(8f, 19f) });
    });

    private static Pen LinePen() => new(Color.White, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Bitmap Build(Action<Graphics> draw)
    {
        var bmp = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        draw(g);
        return bmp;
    }
}
