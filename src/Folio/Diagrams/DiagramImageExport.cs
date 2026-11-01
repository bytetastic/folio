using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Folio.Diagrams;

/// <summary>
/// Diagramme als Bild: PNG (Pixel, doppelte Auflösung) und SVG (Vektor, eigener Umsetzer der WPF-Zeichnung).
/// Braucht nur WPF-Grundtypen – kein Thema, kein Editor –, damit es auch in den Tests läuft.
/// Text wird im SVG als Umriss geschrieben: sieht überall gleich aus, auch ohne die Schrift.
/// </summary>
public static class DiagramImageExport
{
    /// <summary>Alle Teile vollständig (ohne Einblenden) zu einer Zeichnung zusammensetzen.</summary>
    public static Drawing Compose(IEnumerable<Drawing> parts)
    {
        var g = new DrawingGroup();
        foreach (var d in parts) g.Children.Add(d);
        g.Freeze();
        return g;
    }

    /// <summary>PNG mit <paramref name="scale"/>-facher Auflösung. Die DPI-Angabe passt dazu, damit Word &amp; Co.
    /// das Bild in seiner natürlichen Größe einsetzen – nur eben gestochen scharf.</summary>
    public static byte[] ToPng(Drawing drawing, Size size, double scale = 2)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) dc.DrawDrawing(drawing);
        int w = Math.Max(1, (int)Math.Ceiling(size.Width * scale));
        int h = Math.Max(1, (int)Math.Ceiling(size.Height * scale));
        var rtb = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public static string ToSvg(Drawing drawing, Size size)
    {
        var w = new SvgWriter();
        w.Write(drawing, 1);
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{N(size.Width)}\" height=\"{N(size.Height)}\" viewBox=\"0 0 {N(size.Width)} {N(size.Height)}\">\n");
        if (w.Defs.Length > 0) sb.Append("<defs>\n").Append(w.Defs).Append("</defs>\n");
        sb.Append(w.Body);
        sb.Append("</svg>\n");
        return sb.ToString();
    }

    static string N(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);

    sealed class SvgWriter
    {
        public readonly StringBuilder Defs = new();
        public readonly StringBuilder Body = new();
        int _ids;

        public void Write(Drawing d, int depth)
        {
            switch (d)
            {
                case DrawingGroup g: Group(g, depth); break;
                case GeometryDrawing gd: Shape(gd.Geometry, gd.Brush, gd.Pen, depth); break;
                case GlyphRunDrawing gr when gr.GlyphRun != null:
                    Shape(gr.GlyphRun.BuildGeometry(), gr.ForegroundBrush, null, depth);
                    break;
                // Bilder und Videos kommen in Diagrammen nicht vor
            }
        }

        void Group(DrawingGroup g, int depth)
        {
            if (g.Children.Count == 0) return;
            var attrs = new StringBuilder();
            if (g.Transform != null && !g.Transform.Value.IsIdentity)
            {
                var m = g.Transform.Value;
                attrs.Append($" transform=\"matrix({N(m.M11)} {N(m.M12)} {N(m.M21)} {N(m.M22)} {N(m.OffsetX)} {N(m.OffsetY)})\"");
            }
            if (g.Opacity < 0.999) attrs.Append($" opacity=\"{N(g.Opacity)}\"");
            if (g.ClipGeometry != null)
            {
                string id = "c" + ++_ids;
                Defs.Append($"<clipPath id=\"{id}\"><path d=\"{PathData(g.ClipGeometry)}\"/></clipPath>\n");
                attrs.Append($" clip-path=\"url(#{id})\"");
            }
            if (attrs.Length == 0)
            {
                foreach (var c in g.Children) Write(c, depth);
                return;
            }
            Indent(depth).Append("<g").Append(attrs).Append(">\n");
            foreach (var c in g.Children) Write(c, depth + 1);
            Indent(depth).Append("</g>\n");
        }

        void Shape(Geometry? geo, Brush? fill, Pen? pen, int depth)
        {
            if (geo == null || geo.IsEmpty()) return;
            var (fillPaint, fillOpacity) = Paint(fill);
            var (strokePaint, strokeOpacity) = pen != null && pen.Thickness > 0 ? Paint(pen.Brush) : (null, 1);
            if (fillPaint == null && strokePaint == null) return;
            string d = PathData(geo, out var rule);
            if (d.Length == 0) return;
            var sb = Indent(depth).Append("<path d=\"").Append(d).Append('"');
            sb.Append(" fill=\"").Append(fillPaint ?? "none").Append('"');
            if (fillPaint != null && fillOpacity < 0.999) sb.Append($" fill-opacity=\"{N(fillOpacity)}\"");
            if (fillPaint != null && rule == FillRule.EvenOdd) sb.Append(" fill-rule=\"evenodd\"");
            if (strokePaint != null)
            {
                sb.Append($" stroke=\"{strokePaint}\" stroke-width=\"{N(pen!.Thickness)}\"");
                if (strokeOpacity < 0.999) sb.Append($" stroke-opacity=\"{N(strokeOpacity)}\"");
                if (pen.DashStyle is { Dashes.Count: > 0 } ds && pen.DashStyle != DashStyles.Solid)
                    sb.Append(" stroke-dasharray=\"").Append(string.Join(" ", ds.Dashes.Select(x => N(x * pen.Thickness)))).Append('"');
                if (pen.StartLineCap == PenLineCap.Round) sb.Append(" stroke-linecap=\"round\"");
                else if (pen.StartLineCap == PenLineCap.Square) sb.Append(" stroke-linecap=\"square\"");
                if (pen.LineJoin == PenLineJoin.Round) sb.Append(" stroke-linejoin=\"round\"");
                else if (pen.LineJoin == PenLineJoin.Bevel) sb.Append(" stroke-linejoin=\"bevel\"");
            }
            sb.Append("/>\n");
        }

        (string? Paint, double Opacity) Paint(Brush? b)
        {
            switch (b)
            {
                case SolidColorBrush s when s.Color.A > 0 && s.Opacity > 0:
                    return (Hex(s.Color), s.Color.A / 255.0 * s.Opacity);
                case LinearGradientBrush lg when lg.GradientStops.Count > 0:
                {
                    string id = "g" + ++_ids;
                    string units = lg.MappingMode == BrushMappingMode.Absolute ? "userSpaceOnUse" : "objectBoundingBox";
                    Defs.Append($"<linearGradient id=\"{id}\" gradientUnits=\"{units}\" x1=\"{N(lg.StartPoint.X)}\" y1=\"{N(lg.StartPoint.Y)}\" x2=\"{N(lg.EndPoint.X)}\" y2=\"{N(lg.EndPoint.Y)}\">");
                    foreach (var st in lg.GradientStops.OrderBy(x => x.Offset))
                    {
                        Defs.Append($"<stop offset=\"{N(st.Offset)}\" stop-color=\"{Hex(st.Color)}\"");
                        if (st.Color.A < 255) Defs.Append($" stop-opacity=\"{N(st.Color.A / 255.0)}\"");
                        Defs.Append("/>");
                    }
                    Defs.Append("</linearGradient>\n");
                    return ($"url(#{id})", lg.Opacity);
                }
                default:
                    return (null, 1);
            }
        }

        static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        StringBuilder Indent(int depth) => Body.Append(' ', depth * 2);
    }

    static string PathData(Geometry g) => PathData(g, out _);

    /// <summary>Beliebige WPF-Geometrie als SVG-Pfad. Rechtecke, Ellipsen, Kombinationen und Streams laufen über
    /// <see cref="PathGeometry.CreateFromGeometry"/>; eine Geometrie-Transformation wird in die Punkte eingerechnet.</summary>
    static string PathData(Geometry g, out FillRule rule)
    {
        var pg = PathGeometry.CreateFromGeometry(g);
        rule = pg.FillRule;
        var m = pg.Transform?.Value ?? Matrix.Identity;
        var sb = new StringBuilder();
        string P(Point p) { p = m.Transform(p); return N(p.X) + " " + N(p.Y); }
        foreach (var f in pg.Figures)
        {
            sb.Append('M').Append(P(f.StartPoint));
            foreach (var s in f.Segments)
            {
                switch (s)
                {
                    case LineSegment l: sb.Append(" L").Append(P(l.Point)); break;
                    case PolyLineSegment pl: foreach (var p in pl.Points) sb.Append(" L").Append(P(p)); break;
                    case BezierSegment bz: sb.Append(" C").Append(P(bz.Point1)).Append(' ').Append(P(bz.Point2)).Append(' ').Append(P(bz.Point3)); break;
                    case PolyBezierSegment pb:
                        for (int i = 0; i + 2 < pb.Points.Count; i += 3)
                            sb.Append(" C").Append(P(pb.Points[i])).Append(' ').Append(P(pb.Points[i + 1])).Append(' ').Append(P(pb.Points[i + 2]));
                        break;
                    case QuadraticBezierSegment q: sb.Append(" Q").Append(P(q.Point1)).Append(' ').Append(P(q.Point2)); break;
                    case PolyQuadraticBezierSegment pq:
                        for (int i = 0; i + 1 < pq.Points.Count; i += 2)
                            sb.Append(" Q").Append(P(pq.Points[i])).Append(' ').Append(P(pq.Points[i + 1]));
                        break;
                    case ArcSegment a:
                        // Skalierung der Transformation auf die Radien anwenden (in Diagrammen nur gleichmäßig)
                        double sx = Math.Sqrt(m.M11 * m.M11 + m.M12 * m.M12), sy = Math.Sqrt(m.M21 * m.M21 + m.M22 * m.M22);
                        sb.Append(" A").Append(N(a.Size.Width * sx)).Append(' ').Append(N(a.Size.Height * sy)).Append(' ')
                          .Append(N(a.RotationAngle)).Append(' ').Append(a.IsLargeArc ? '1' : '0').Append(' ')
                          .Append(a.SweepDirection == SweepDirection.Clockwise ? '1' : '0').Append(' ').Append(P(a.Point));
                        break;
                }
            }
            if (f.IsClosed) sb.Append(" Z");
            sb.Append(' ');
        }
        return sb.ToString().Trim();
    }
}
