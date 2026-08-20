using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace SnapTool.Rendering;

internal enum ToolType
{
    Select,
    Hand,
    Marquee,
    Rectangle,
    Ellipse,
    Line,
    Arrow,
    Highlight,
    Pen,
    Text
}

internal abstract class Annotation
{
    public Color Color { get; set; }
    public int Thickness { get; set; }

    /// <summary>0-100. Applied as an alpha multiplier on top of whatever alpha <see cref="Color"/> already carries.</summary>
    public int Opacity { get; set; } = 100;

    public abstract void Draw(Graphics g);
    public abstract bool HitTest(Point p);
    public abstract void Move(int dx, int dy);
    public abstract Rectangle GetBounds();
    public abstract Annotation Clone();

    protected Color Tinted(Color c) => Opacity >= 100
        ? c
        : Color.FromArgb((int)Math.Round(c.A * (Opacity / 100.0)), c.R, c.G, c.B);

    /// <summary>Flat, near-black, low-alpha — the soft drop shadow every geometric shape draws just
    /// behind its main stroke for a bit of modern depth instead of sitting perfectly flat.</summary>
    protected Color ShadowColor => Color.FromArgb((int)Math.Round(55 * (Opacity / 100.0)), 15, 15, 20);
}

/// <summary>Implemented by shapes whose geometry is a plain axis-aligned <see cref="Bounds"/> rectangle
/// (rectangle/ellipse/highlight), so the editor can offer 8 generic corner/edge resize handles for all
/// of them through one shared code path instead of one per shape type.</summary>
internal interface IResizableBounds
{
    Rectangle Bounds { get; set; }
}

/// <summary>Implemented by shapes defined by two endpoints (line), so the editor can offer generic
/// Start/End resize handles. Arrow deliberately does not implement this — it already has its own
/// richer Start/Mid/End handle system with a bend point.</summary>
internal interface IResizableEndpoints
{
    Point Start { get; set; }
    Point End { get; set; }
}

internal sealed class RectangleAnnotation : Annotation, IResizableBounds
{
    public Rectangle Bounds { get; set; }

    public override void Draw(Graphics g)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        float radius = Math.Min(Math.Min(Bounds.Width, Bounds.Height) * 0.1f, 10f);

        using (var shadowPath = Geometry.RoundedRect(new Rectangle(Bounds.X + 2, Bounds.Y + 3, Bounds.Width, Bounds.Height), radius))
        using (var shadowPen = new Pen(ShadowColor, Thickness) { LineJoin = LineJoin.Round })
            g.DrawPath(shadowPen, shadowPath);

        using var path = Geometry.RoundedRect(Bounds, radius);
        using var pen = new Pen(Tinted(Color), Thickness) { LineJoin = LineJoin.Round };
        g.DrawPath(pen, path);
    }

    public override bool HitTest(Point p) => Rectangle.Inflate(Bounds, 6, 6).Contains(p);
    public override void Move(int dx, int dy) => Bounds = new Rectangle(Bounds.X + dx, Bounds.Y + dy, Bounds.Width, Bounds.Height);
    public override Rectangle GetBounds() => Bounds;
    public override Annotation Clone() => new RectangleAnnotation { Color = Color, Thickness = Thickness, Opacity = Opacity, Bounds = Bounds };
}

internal sealed class HighlightAnnotation : Annotation, IResizableBounds
{
    public Rectangle Bounds { get; set; }

    public override void Draw(Graphics g)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        using var brush = new SolidBrush(Tinted(Color.FromArgb(90, Color)));
        float radius = Math.Min(Math.Min(Bounds.Width, Bounds.Height) * 0.1f, 10f);
        using var path = Geometry.RoundedRect(Bounds, radius);
        g.FillPath(brush, path);
    }

    public override bool HitTest(Point p) => Bounds.Contains(p);
    public override void Move(int dx, int dy) => Bounds = new Rectangle(Bounds.X + dx, Bounds.Y + dy, Bounds.Width, Bounds.Height);
    public override Rectangle GetBounds() => Bounds;
    public override Annotation Clone() => new HighlightAnnotation { Color = Color, Thickness = Thickness, Opacity = Opacity, Bounds = Bounds };
}

internal sealed class EllipseAnnotation : Annotation, IResizableBounds
{
    public Rectangle Bounds { get; set; }

    public override void Draw(Graphics g)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;

        var shadowBounds = new Rectangle(Bounds.X + 2, Bounds.Y + 3, Bounds.Width, Bounds.Height);
        using (var shadowPen = new Pen(ShadowColor, Thickness))
            g.DrawEllipse(shadowPen, shadowBounds);

        using var pen = new Pen(Tinted(Color), Thickness) { LineJoin = LineJoin.Round };
        g.DrawEllipse(pen, Bounds);
    }

    public override bool HitTest(Point p) => Rectangle.Inflate(Bounds, 6, 6).Contains(p);
    public override void Move(int dx, int dy) => Bounds = new Rectangle(Bounds.X + dx, Bounds.Y + dy, Bounds.Width, Bounds.Height);
    public override Rectangle GetBounds() => Bounds;
    public override Annotation Clone() => new EllipseAnnotation { Color = Color, Thickness = Thickness, Opacity = Opacity, Bounds = Bounds };
}

internal sealed class LineAnnotation : Annotation, IResizableEndpoints
{
    public Point Start { get; set; }
    public Point End { get; set; }

    public override void Draw(Graphics g)
    {
        using (var shadowPen = new Pen(ShadowColor, Thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(shadowPen, Start.X + 2, Start.Y + 3, End.X + 2, End.Y + 3);

        using var pen = new Pen(Tinted(Color), Thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, Start, End);
    }

    public override bool HitTest(Point p) => Geometry.DistanceToSegment(p, Start, End) <= Math.Max(Thickness, 6) + 5;
    public override void Move(int dx, int dy)
    {
        Start = new Point(Start.X + dx, Start.Y + dy);
        End = new Point(End.X + dx, End.Y + dy);
    }
    public override Rectangle GetBounds() => Geometry.BoundsOf(Start, End);
    public override Annotation Clone() => new LineAnnotation { Color = Color, Thickness = Thickness, Opacity = Opacity, Start = Start, End = End };
}

internal sealed class ArrowAnnotation : Annotation
{
    public Point Start { get; set; }
    public Point End { get; set; }

    /// <summary>Optional bend handle. Null means a straight arrow; when set, the shaft curves through this point.</summary>
    public Point? MidPoint { get; set; }

    public override void Draw(Graphics g)
    {
        double dx = End.X - Start.X, dy = End.Y - Start.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 0.01)
        {
            using var dot = new SolidBrush(Tinted(Color));
            g.FillEllipse(dot, Start.X - Thickness / 2f, Start.Y - Thickness / 2f, Thickness, Thickness);
            return;
        }

        // Same soft drop-shadow pass every other shape gets, offset a touch below-right of the shaft.
        var shadowStart = new Point(Start.X + 2, Start.Y + 3);
        var shadowEnd = new Point(End.X + 2, End.Y + 3);
        using (var shadowPen = new Pen(ShadowColor, Thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            if (MidPoint is { } shadowMid) g.DrawCurve(shadowPen, new[] { shadowStart, new Point(shadowMid.X + 2, shadowMid.Y + 3), shadowEnd }, 0.5f);
            else g.DrawLine(shadowPen, shadowStart, shadowEnd);
        }

        using (var shaftPen = new Pen(Tinted(Color), Thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            if (MidPoint is { } mid) g.DrawCurve(shaftPen, new[] { Start, mid, End }, 0.5f);
            else g.DrawLine(shaftPen, Start, End);
        }

        // Point the head along the curve's actual tangent at End (the direction from the bend
        // point, if any) rather than the straight Start->End chord, so it reads correctly on a bend.
        var tailPoint = MidPoint ?? Start;
        double angle = Math.Atan2(End.Y - tailPoint.Y, End.X - tailPoint.X);

        float headLen = (float)Math.Min(Math.Clamp(Thickness * 3.2f, 14f, 30f), len * 0.8);
        const double wingAngle = Math.PI / 7.5;

        var wing1 = new PointF(
            (float)(End.X - headLen * Math.Cos(angle - wingAngle)),
            (float)(End.Y - headLen * Math.Sin(angle - wingAngle)));
        var wing2 = new PointF(
            (float)(End.X - headLen * Math.Cos(angle + wingAngle)),
            (float)(End.Y - headLen * Math.Sin(angle + wingAngle)));

        using var headPen = new Pen(Tinted(Color), Thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(headPen, End, wing1);
        g.DrawLine(headPen, End, wing2);
    }

    public override bool HitTest(Point p)
    {
        int tolerance = Math.Max(Thickness, 6) + 8;
        if (MidPoint is { } mid)
            return Geometry.DistanceToSegment(p, Start, mid) <= tolerance || Geometry.DistanceToSegment(p, mid, End) <= tolerance;
        return Geometry.DistanceToSegment(p, Start, End) <= tolerance;
    }

    public override void Move(int dx, int dy)
    {
        Start = new Point(Start.X + dx, Start.Y + dy);
        End = new Point(End.X + dx, End.Y + dy);
        if (MidPoint is { } mid) MidPoint = new Point(mid.X + dx, mid.Y + dy);
    }

    public override Rectangle GetBounds()
    {
        var bounds = Geometry.BoundsOf(Start, End);
        return MidPoint is { } mid ? Rectangle.Union(bounds, new Rectangle(mid, Size.Empty)) : bounds;
    }

    public override Annotation Clone() => new ArrowAnnotation { Color = Color, Thickness = Thickness, Opacity = Opacity, Start = Start, End = End, MidPoint = MidPoint };
}

internal sealed class FreehandAnnotation : Annotation
{
    public List<Point> Points { get; } = new();

    public override void Draw(Graphics g)
    {
        if (Points.Count < 2) return;
        using var pen = new Pen(Tinted(Color), Thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawCurve(pen, Points.ToArray(), 0.5f);
    }

    public override bool HitTest(Point p)
    {
        for (int i = 0; i < Points.Count - 1; i++)
            if (Geometry.DistanceToSegment(p, Points[i], Points[i + 1]) <= Math.Max(Thickness, 6) + 5) return true;
        return false;
    }

    public override void Move(int dx, int dy)
    {
        for (int i = 0; i < Points.Count; i++) Points[i] = new Point(Points[i].X + dx, Points[i].Y + dy);
    }

    public override Rectangle GetBounds() => Geometry.BoundsOfPoints(Points);

    public override Annotation Clone()
    {
        var clone = new FreehandAnnotation { Color = Color, Thickness = Thickness, Opacity = Opacity };
        clone.Points.AddRange(Points);
        return clone;
    }
}

internal sealed class TextAnnotation : Annotation
{
    public Point Position { get; set; }
    public string Text { get; set; } = "";
    public float FontSize { get; set; } = 22f;
    public Size RenderedSize { get; private set; } = new(80, 24);

    public override void Draw(Graphics g)
    {
        using var font = new Font(HandFont.FamilyName, FontSize, FontStyle.Regular);
        var measured = g.MeasureString(Text, font);
        RenderedSize = new Size((int)Math.Ceiling(measured.Width), (int)Math.Ceiling(measured.Height));

        using var path = new GraphicsPath();
        float emSize = g.DpiY * FontSize / 72f;
        path.AddString(Text, font.FontFamily, (int)FontStyle.Regular, emSize, Position, StringFormat.GenericDefault);

        using var outline = new Pen(Tinted(Color.FromArgb(180, 0, 0, 0)), Math.Max(FontSize * 0.12f, 2f)) { LineJoin = LineJoin.Round };
        g.DrawPath(outline, path);
        using var fill = new SolidBrush(Tinted(Color));
        g.FillPath(fill, path);
    }

    public override bool HitTest(Point p) => Rectangle.Inflate(new Rectangle(Position, RenderedSize), 4, 4).Contains(p);
    public override void Move(int dx, int dy) => Position = new Point(Position.X + dx, Position.Y + dy);
    public override Rectangle GetBounds() => new(Position, RenderedSize);
    public override Annotation Clone() => new TextAnnotation { Color = Color, Thickness = Thickness, Opacity = Opacity, Position = Position, Text = Text, FontSize = FontSize };
}

/// <summary>A pasted-in raster chunk (e.g. a region cut/copied from another screenshot window),
/// composited on top of the base image like any other annotation. Owns <see cref="Image"/> and must
/// be disposed by the caller when removed (undo/delete) or when the editor closes.</summary>
internal sealed class ImageAnnotation : Annotation
{
    public required Bitmap Image { get; init; }
    public Rectangle Bounds { get; set; }

    public override void Draw(Graphics g) => g.DrawImage(Image, Bounds);
    public override bool HitTest(Point p) => Bounds.Contains(p);
    public override void Move(int dx, int dy) => Bounds = new Rectangle(Bounds.X + dx, Bounds.Y + dy, Bounds.Width, Bounds.Height);
    public override Rectangle GetBounds() => Bounds;

    // Owns its Bitmap independently of the source, so a duplicate must get its own copy rather than
    // sharing the handle — otherwise disposing one instance (e.g. via undo) would corrupt the other.
    public override Annotation Clone() => new ImageAnnotation { Color = Color, Thickness = Thickness, Opacity = Opacity, Image = new Bitmap(Image), Bounds = Bounds };
}
