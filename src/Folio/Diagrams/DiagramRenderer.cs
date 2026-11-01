using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Folio.Visuals;

namespace Folio.Diagrams;

/// <summary>Ein gezeichnetes Diagramm: Teile mit Startverzögerung (für das gestaffelte Einblenden).</summary>
public sealed class DiagramPicture
{
    public Size Size;
    public List<(Drawing Drawing, double Delay)> Parts { get; } = new();
    public double IntroLength => Parts.Count == 0 ? 0 : Parts.Max(p => p.Delay) + PartFade;
    public const double PartFade = 0.28;
}

/// <summary>
/// Zeichnet Diagramme nativ (WPF-Drawings, einmal erzeugt und eingefroren).
/// Farben kommen aus dem aktuellen Thema; alles skaliert mit der Schriftgröße des Editors.
/// </summary>
public sealed class DiagramRenderer
{
    readonly FolioTheme _t;
    readonly double _ppd;
    readonly double _fs;
    readonly Typeface _ui, _uiBold, _mono, _icons;

    public DiagramRenderer(FolioTheme theme, double pixelsPerDip, double baseFontSize)
    {
        _t = theme;
        _ppd = pixelsPerDip;
        _fs = Math.Clamp(baseFontSize * 0.8, 11, 16);
        _ui = new Typeface(Editor.Typography.Body, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _uiBold = new Typeface(Editor.Typography.Body, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        _mono = new Typeface(Editor.Typography.Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _icons = new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    }

    public DiagramPicture Render(Diagram d) => d switch
    {
        FlowChart f => RenderFlow(f),
        SequenceDiagram s => RenderSequence(s),
        XyChart x => RenderXy(x),
        PieChart p => RenderPie(p),
        DiagramError e => RenderError(e.Message),
        _ => RenderError("Unbekanntes Diagramm"),
    };

    // ───────────────────────────── Werkzeuge ─────────────────────────────

    Color A(Color c, double a) => Color.FromArgb((byte)Math.Clamp(a * 255, 0, 255), c.R, c.G, c.B);

    static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    static Pen P(Color c, double w, bool dashed = false, bool round = true)
    {
        var p = new Pen(B(c), w);
        if (dashed) p.DashStyle = new DashStyle(new[] { 3.5, 3.0 }, 0);
        if (round) { p.StartLineCap = p.EndLineCap = PenLineCap.Round; p.LineJoin = PenLineJoin.Round; }
        p.Freeze();
        return p;
    }

    FormattedText Text(string s, double size, Color c, Typeface? face = null, double maxWidth = 0, TextAlignment align = TextAlignment.Left)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face ?? _ui, size, B(c), _ppd)
        {
            TextAlignment = align,
        };
        if (maxWidth > 0) ft.MaxTextWidth = maxWidth;
        return ft;
    }

    Size Measure(string s, double maxWidth)
    {
        var ft = Text(s, _fs, _t.Text, null, maxWidth, TextAlignment.Center);
        return new Size(Math.Min(maxWidth, ft.WidthIncludingTrailingWhitespace), ft.Height);
    }

    static Drawing Freeze(DrawingGroup g) { g.Freeze(); return g; }

    /// <summary>Kleines Zeit-Abzeichen „⏱ 2 min“, zentriert um <paramref name="center"/>.</summary>
    void TimeBadge(DrawingContext dc, Point center, TimeSpan t, bool critical, bool wait = false)
    {
        string label = Durations.Format(t);
        var icon = Text(wait ? "" : "", _fs * 0.78, critical ? _t.Accent : wait ? _t.Amber : _t.TextDim, _icons);
        var ft = Text(label, _fs * 0.82, critical ? _t.Accent : wait ? Mix(_t.Amber, _t.Text, 0.35) : _t.TextDim, critical ? _uiBold : _ui);
        double w = icon.Width + 4 + ft.Width + 14, h = ft.Height + 4;
        var r = new Rect(center.X - w / 2, center.Y - h / 2, w, h);
        dc.DrawRoundedRectangle(B(_t.Paper), P(critical ? A(_t.Accent, 0.6) : wait ? A(_t.Amber, 0.7) : _t.PaperBorder, 1), r, h / 2, h / 2);
        dc.DrawText(icon, new Point(r.X + 7, r.Y + (h - icon.Height) / 2 + 0.5));
        dc.DrawText(ft, new Point(r.X + 7 + icon.Width + 4, r.Y + 2));
    }

    DiagramPicture Frame(Drawing content, Size contentSize, string? title, string? footer, IEnumerable<string> warnings, List<(Drawing, double)>? parts = null)
    {
        // Rahmen: Titel oben, Inhalt, Fußzeile (Zeiten) und Hinweise unten
        const double pad = 18;
        var pic = new DiagramPicture();
        double y = pad;
        var top = new DrawingGroup();
        using (var dc = top.Open())
        {
            if (!string.IsNullOrWhiteSpace(title))
            {
                var ft = Text(title!, _fs * 1.12, _t.Heading, _uiBold);
                dc.DrawText(ft, new Point(pad, y));
                y += ft.Height + 12;
            }
        }
        double contentTop = y;
        y += contentSize.Height;
        var bottom = new DrawingGroup();
        double width = Math.Max(contentSize.Width + 2 * pad, 260);
        using (var dc = bottom.Open())
        {
            if (!string.IsNullOrWhiteSpace(footer))
            {
                y += 14;
                var ft = Text(footer!, _fs * 0.88, _t.TextDim, null, width - 2 * pad);
                dc.DrawLine(P(_t.Rule, 1), new Point(pad, y - 7), new Point(width - pad, y - 7));
                dc.DrawText(ft, new Point(pad, y));
                y += ft.Height;
            }
            var w = warnings.ToList();
            if (w.Count > 0)
            {
                y += 8;
                string msg = w.Count == 1 ? "Hinweis: " + w[0] : $"{w.Count} Hinweise – zuerst: {w[0]}";
                var ft = Text(msg, _fs * 0.8, Mix(_t.Amber, _t.Text, 0.4), null, width - 2 * pad);
                dc.DrawText(ft, new Point(pad, y));
                y += ft.Height;
            }
        }
        y += pad;

        var card = new DrawingGroup();
        using (var dc = card.Open())
        {
            dc.DrawRoundedRectangle(B(_t.IsDark ? Mix(_t.Paper, _t.CodeBack, 0.6) : Mix(_t.Paper, _t.CodeBack, 0.45)), P(_t.PaperBorder, 1), new Rect(0.5, 0.5, width - 1, y - 1), 12, 12);
        }
        pic.Parts.Add((Freeze(card), 0));
        pic.Parts.Add((Freeze(top), 0.02));
        var shift = new TranslateTransform(pad, contentTop);
        shift.Freeze();
        if (parts == null)
        {
            var g = new DrawingGroup { Transform = shift };
            g.Children.Add(content);
            pic.Parts.Add((Freeze(g), 0.06));
        }
        else
        {
            foreach (var (d, delay) in parts)
            {
                var g = new DrawingGroup { Transform = shift };
                g.Children.Add(d);
                pic.Parts.Add((Freeze(g), delay));
            }
        }
        double last = pic.Parts.Max(p => p.Delay);
        pic.Parts.Add((Freeze(bottom), last + 0.05));
        pic.Size = new Size(width, y);
        return pic;
    }

    DiagramPicture RenderError(string message)
    {
        var g = new DrawingGroup();
        var ft = Text(message, _fs * 0.92, _t.TextDim, null, 520);
        var icon = Text("", _fs, _t.Amber, _icons);
        using (var dc = g.Open())
        {
            dc.DrawText(icon, new Point(0, 1));
            dc.DrawText(ft, new Point(icon.Width + 10, 0));
        }
        return Frame(Freeze(g), new Size(ft.Width + icon.Width + 10, Math.Max(ft.Height, icon.Height)), "Diagramm nicht lesbar", null, Array.Empty<string>());
    }

    // ───────────────────────────── Ablaufdiagramm ─────────────────────────────

    DiagramPicture RenderFlow(FlowChart c)
    {
        if (c.Nodes.Count == 0) return RenderError("Keine Schritte gefunden – z. B. A[Start] --> B[Ende]");
        var geo = FlowLayout.Layout(c, Measure);
        var parts = new List<(Drawing, double)>();
        bool horiz = c.Horizontal;

        // Bahnen
        if (geo.Lanes.Count > 0)
        {
            var lanes = new DrawingGroup();
            using (var dc = lanes.Open())
            {
                for (int i = 0; i < geo.Lanes.Count; i++)
                {
                    var (lane, band, header) = geo.Lanes[i];
                    var full = horiz ? new Rect(0, band.Y, geo.Bounds.Width, band.Height) : new Rect(band.X, 0, band.Width, geo.Bounds.Height);
                    dc.DrawRoundedRectangle(B(A(i % 2 == 0 ? _t.Accent : _t.TextFaint, _t.IsDark ? 0.07 : 0.045)), null, Inset(full, 2), 8, 8);
                    var hdr = Inset(header, 2);
                    dc.DrawRoundedRectangle(B(A(_t.Accent, _t.IsDark ? 0.14 : 0.09)), null, hdr, 8, 8);
                    if (lane.Title.Length > 0)
                    {
                        var ft = Text(lane.Title, _fs * 0.9, _t.TextDim, _uiBold, Math.Max(20, hdr.Width - 14), TextAlignment.Center);
                        ft.MaxLineCount = horiz ? 4 : 1;
                        ft.Trimming = TextTrimming.CharacterEllipsis;
                        dc.DrawText(ft, new Point(hdr.X + 7, hdr.Y + (hdr.Height - ft.Height) / 2));
                    }
                }
            }
            parts.Add((Freeze(lanes), 0.0));
        }

        double LayerDelay(FlowNode n) => 0.08 + n.Layer * 0.07 + (n.Index % 4) * 0.015;

        // Kanten zuerst (liegen unter den Kästen), jede blendet mit ihrem Ziel ein
        foreach (var path in geo.Edges)
        {
            var e = path.Edge;
            var g = new DrawingGroup();
            using (var dc = g.Open()) DrawEdge(dc, path, c);
            parts.Add((Freeze(g), LayerDelay(c.ById[e.To]) + 0.06));
        }

        foreach (var n in c.Nodes)
        {
            var g = new DrawingGroup();
            using (var dc = g.Open()) DrawNode(dc, n, c.HasTimes);
            parts.Add((Freeze(g), LayerDelay(n)));
        }

        string? footer = null;
        if (c.HasTimes)
        {
            var bits = new List<string> { $"Bearbeitung gesamt {Durations.Format(c.TotalWork)}" };
            if (c.TotalWait > TimeSpan.Zero) bits.Add($"Liegezeit gesamt {Durations.Format(c.TotalWait)}");
            bits.Add($"Durchlaufzeit (längster Pfad, blau) {Durations.Format(c.CriticalPath)}");
            footer = string.Join("   ·   ", bits);
        }
        var content = new DrawingGroup();
        return Frame(content, geo.Bounds.Size, c.Title, footer, c.Warnings, parts);
    }

    static Rect Inset(Rect r, double d) => new(r.X + d, r.Y + d, Math.Max(0, r.Width - 2 * d), Math.Max(0, r.Height - 2 * d));

    void DrawNode(DrawingContext dc, FlowNode n, bool hasTimes)
    {
        var r = new Rect(n.X - n.Width / 2, n.Y - n.Height / 2, n.Width, n.Height);
        bool crit = n.Critical && hasTimes;
        Color stroke = crit ? _t.Accent : A(_t.Accent, 0.5);
        double sw = crit ? 2 : 1.3;
        Color fill = Mix(_t.Paper, _t.Accent, _t.IsDark ? 0.10 : 0.06);
        switch (n.Shape)
        {
            case NodeShape.Diamond:
            {
                fill = Mix(_t.Paper, _t.Amber, _t.IsDark ? 0.14 : 0.12);
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(new Point(n.X, r.Top), true, true);
                    ctx.LineTo(new Point(r.Right, n.Y), true, true);
                    ctx.LineTo(new Point(n.X, r.Bottom), true, true);
                    ctx.LineTo(new Point(r.Left, n.Y), true, true);
                }
                g.Freeze();
                dc.DrawGeometry(B(fill), P(crit ? _t.Accent : A(_t.Amber, 0.85), sw), g);
                break;
            }
            case NodeShape.Circle:
                dc.DrawEllipse(B(Mix(_t.Paper, _t.Accent, 0.16)), P(stroke, sw), new Point(n.X, n.Y), n.Width / 2, n.Height / 2);
                break;
            case NodeShape.Stadium:
                dc.DrawRoundedRectangle(B(Mix(_t.Paper, _t.Accent, _t.IsDark ? 0.18 : 0.13)), P(stroke, sw), r, r.Height / 2, r.Height / 2);
                break;
            case NodeShape.Round:
                dc.DrawRoundedRectangle(B(fill), P(stroke, sw), r, 14, 14);
                break;
            case NodeShape.Database:
            {
                double ey = 7;
                var body = new Rect(r.X, r.Y + ey, r.Width, r.Height - 2 * ey);
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(new Point(r.Left, body.Top), true, true);
                    ctx.LineTo(new Point(r.Left, body.Bottom), true, true);
                    ctx.ArcTo(new Point(r.Right, body.Bottom), new Size(r.Width / 2, ey), 0, false, SweepDirection.Counterclockwise, true, true);
                    ctx.LineTo(new Point(r.Right, body.Top), true, true);
                    ctx.ArcTo(new Point(r.Left, body.Top), new Size(r.Width / 2, ey), 0, false, SweepDirection.Counterclockwise, true, true);
                }
                g.Freeze();
                dc.DrawGeometry(B(fill), P(stroke, sw), g);
                dc.DrawEllipse(null, P(stroke, sw), new Point(n.X, body.Top), r.Width / 2, ey);
                break;
            }
            case NodeShape.Subroutine:
                dc.DrawRoundedRectangle(B(fill), P(stroke, sw), r, 6, 6);
                dc.DrawLine(P(stroke, 1.1), new Point(r.Left + 8, r.Top), new Point(r.Left + 8, r.Bottom));
                dc.DrawLine(P(stroke, 1.1), new Point(r.Right - 8, r.Top), new Point(r.Right - 8, r.Bottom));
                break;
            default:
                dc.DrawRoundedRectangle(B(fill), P(stroke, sw), r, 8, 8);
                break;
        }
        double maxW = n.Shape == NodeShape.Diamond ? n.Width * 0.62 : n.Width - 2 * 12;
        var ft = Text(n.Label, _fs, _t.Text, crit ? _uiBold : _ui, maxW, TextAlignment.Center);
        double ty = n.Y - ft.Height / 2 + (n.Shape == NodeShape.Database ? 4 : 0) - (n.Time != null && n.Shape != NodeShape.Diamond ? 4 : 0);
        dc.DrawText(ft, new Point(n.X - maxW / 2, ty));
        if (n.Time is { } t) TimeBadge(dc, new Point(n.X, r.Bottom), t, crit);
    }

    void DrawEdge(DrawingContext dc, EdgePath p, FlowChart c)
    {
        var e = p.Edge;
        bool crit = e.Critical && c.HasTimes;
        Color col = crit ? _t.Accent : _t.IsDark ? A(_t.TextDim, 0.75) : A(_t.TextDim, 0.7);
        double w = e.Style == EdgeStyle.Thick ? 2.6 : crit ? 2 : 1.4;
        var pen = P(col, w, e.Style == EdgeStyle.Dotted);

        // Spitze etwas vor dem Ziel enden lassen, damit die Linie nicht durch die Pfeilspitze schaut
        var tangentEnd = p.P3 - p.C2;
        if (tangentEnd.Length < 0.01) tangentEnd = p.P3 - p.P0;
        tangentEnd.Normalize();
        double head = 8 + w;
        var end = e.ArrowEnd ? p.P3 - tangentEnd * (head * 0.7) : p.P3;
        var tangentStart = p.C1 - p.P0;
        if (tangentStart.Length < 0.01) tangentStart = p.P3 - p.P0;
        tangentStart.Normalize();
        var start = e.ArrowStart ? p.P0 + tangentStart * (head * 0.7) : p.P0;

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.BezierTo(p.C1, p.C2, end, true, true);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
        if (e.ArrowEnd) Arrow(dc, p.P3, tangentEnd, head, col);
        if (e.ArrowStart) Arrow(dc, p.P0, -tangentStart, head, col);

        // Beschriftung und Liegezeit
        var labelAt = p.LabelAt;
        if (!string.IsNullOrEmpty(e.Label))
        {
            var ft = Text(e.Label!, _fs * 0.84, _t.TextDim, null, 150, TextAlignment.Center);
            double lw = Math.Min(150, ft.Width) + 12, lh = ft.Height + 4;
            double ly = labelAt.Y - lh / 2 - (e.Wait != null ? 11 : 0);
            var r = new Rect(labelAt.X - lw / 2, ly, lw, lh);
            dc.DrawRoundedRectangle(B(_t.Paper), P(_t.PaperBorder, 1), r, 6, 6);
            dc.DrawText(ft, new Point(labelAt.X - 75, r.Y + 2));
        }
        if (e.Wait is { } wt) TimeBadge(dc, new Point(labelAt.X, labelAt.Y + (string.IsNullOrEmpty(e.Label) ? 0 : 11)), wt, false, wait: true);
    }

    void Arrow(DrawingContext dc, Point tip, Vector dir, double size, Color col)
    {
        var n = new Vector(-dir.Y, dir.X);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(tip, true, true);
            ctx.LineTo(tip - dir * size + n * size * 0.48, true, true);
            ctx.LineTo(tip - dir * size * 0.72, true, true);
            ctx.LineTo(tip - dir * size - n * size * 0.48, true, true);
        }
        g.Freeze();
        dc.DrawGeometry(B(col), null, g);
    }

    // ───────────────────────────── Sequenzdiagramm ─────────────────────────────

    DiagramPicture RenderSequence(SequenceDiagram s)
    {
        var parts = new List<(Drawing, double)>();
        double boxH = 40;
        // Breiten der Teilnehmer
        foreach (var p in s.Participants)
        {
            var ft = Text(p.Label, _fs, _t.Text, _uiBold, 180, TextAlignment.Center);
            p.Width = Math.Max(104, Math.Min(180, ft.Width) + 28);
        }
        // Abstände: Nachbarn so weit auseinander, dass die Nachrichtentexte dazwischen passen
        int n = s.Participants.Count;
        var idx = s.Participants.Select((p, i) => (p.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var need = new double[Math.Max(1, n)];
        for (int i = 1; i < n; i++) need[i] = (s.Participants[i - 1].Width + s.Participants[i].Width) / 2 + 36;
        foreach (var m in s.Items.Where(m => m.Kind == SeqKind.Message))
        {
            int a = idx[m.From], b = idx[m.To];
            double tw = Text(m.Text, _fs * 0.9, _t.Text, null, 0).Width + (m.Time != null ? 70 : 0) + 30;
            if (a == b) { if (a + 1 < n) need[a + 1] = Math.Max(need[a + 1], tw + 50); continue; }
            int lo = Math.Min(a, b), hi = Math.Max(a, b);
            double have = 0;
            for (int i = lo + 1; i <= hi; i++) have += need[i];
            if (have < tw)
                for (int i = lo + 1; i <= hi; i++) need[i] += (tw - have) / (hi - lo);
        }
        double x = s.Participants.Count > 0 ? s.Participants[0].Width / 2 + 4 : 0;
        for (int i = 0; i < n; i++)
        {
            if (i > 0) x += need[i];
            s.Participants[i].X = x;
        }
        double width = n == 0 ? 200 : s.Participants[^1].X + s.Participants[^1].Width / 2 + 4;

        // Zeilen
        double y = boxH + 26;
        var rows = new List<(SeqItem Item, double Y, double H)>();
        foreach (var it in s.Items)
        {
            double h = it.Kind switch
            {
                SeqKind.Message => (it.From == it.To ? 46 : 34) + 10,
                SeqKind.Note => Text(it.Text, _fs * 0.9, _t.Text, null, 220).Height + 22,
                SeqKind.BlockStart => 30,
                SeqKind.BlockElse => 28,
                _ => 14,
            };
            rows.Add((it, y, h));
            y += h;
        }
        double bottom = y + 8;

        // Teilnehmer und Lebenslinien
        var heads = new DrawingGroup();
        using (var dc = heads.Open())
        {
            foreach (var p in s.Participants)
            {
                dc.DrawLine(P(A(_t.TextFaint, 0.8), 1.2, dashed: true), new Point(p.X, boxH), new Point(p.X, bottom));
                var r = new Rect(p.X - p.Width / 2, 0, p.Width, boxH);
                dc.DrawRoundedRectangle(B(Mix(_t.Paper, _t.Accent, p.IsActor ? 0.16 : 0.08)), P(A(_t.Accent, 0.55), 1.3), r, 9, 9);
                var ft = Text(p.Label, _fs, _t.Text, _uiBold, p.Width - 16, TextAlignment.Center);
                ft.MaxLineCount = 2;
                ft.Trimming = TextTrimming.CharacterEllipsis;
                double tx = p.X - (p.Width - 16) / 2;
                if (p.IsActor)
                {
                    var icon = Text("", _fs, _t.Accent, _icons);
                    dc.DrawText(icon, new Point(r.X + 9, (boxH - icon.Height) / 2));
                }
                dc.DrawText(ft, new Point(tx, (boxH - ft.Height) / 2));
            }
        }
        parts.Add((Freeze(heads), 0.02));

        // Blöcke (loop/alt/opt …) als Rahmen über alle Teilnehmer
        var blocks = new DrawingGroup();
        using (var dc = blocks.Open())
        {
            var open = new Stack<(SeqItem Item, double Y)>();
            foreach (var (it, ry, rh) in rows)
            {
                if (it.Kind == SeqKind.BlockStart) open.Push((it, ry));
                else if (it.Kind == SeqKind.BlockElse && open.Count > 0)
                {
                    double inset = (open.Count - 1) * 8;
                    dc.DrawLine(P(A(_t.TextFaint, 0.7), 1, dashed: true), new Point(inset - 6, ry + 4), new Point(width - inset + 6, ry + 4));
                    if (it.Text.Length > 0) dc.DrawText(Text($"[{it.Text}]", _fs * 0.82, _t.TextDim), new Point(inset + 4, ry + 8));
                }
                else if (it.Kind == SeqKind.BlockEnd && open.Count > 0)
                {
                    var (start, sy) = open.Pop();
                    double inset = open.Count * 8;
                    var r = new Rect(inset - 6, sy, width - 2 * inset + 12, ry + rh - sy - 4);
                    dc.DrawRoundedRectangle(B(A(_t.Accent, 0.035)), P(A(_t.Accent, 0.35), 1.1), r, 8, 8);
                    var tag = Text(start.BlockType, _fs * 0.8, _t.Accent, _uiBold);
                    var tagR = new Rect(r.X, r.Y, tag.Width + 16, tag.Height + 6);
                    dc.DrawRoundedRectangle(B(A(_t.Accent, 0.14)), null, tagR, 8, 8);
                    dc.DrawText(tag, new Point(tagR.X + 8, tagR.Y + 3));
                    if (start.Text.Length > 0) dc.DrawText(Text($"[{start.Text}]", _fs * 0.82, _t.TextDim), new Point(tagR.Right + 8, tagR.Y + 3));
                }
            }
        }
        parts.Insert(0, (Freeze(blocks), 0.0));

        // Nachrichten und Notizen, nacheinander
        int k = 0;
        var total = TimeSpan.Zero;
        foreach (var (it, ry, rh) in rows)
        {
            if (it.Kind is not (SeqKind.Message or SeqKind.Note)) continue;
            var g = new DrawingGroup();
            using (var dc = g.Open())
            {
                if (it.Kind == SeqKind.Note) DrawNote(dc, s, it, ry, rh, idx);
                else
                {
                    DrawMessage(dc, s, it, ry, rh, idx);
                    if (it.Time is { } t) total += t;
                }
            }
            parts.Add((Freeze(g), 0.12 + k++ * 0.05));
        }

        string? footer = s.Items.Any(i => i.Time != null) ? $"Summe der Zeiten {Durations.Format(total)}" : null;
        return Frame(new DrawingGroup(), new Size(width, bottom), s.Title, footer, s.Warnings, parts);
    }

    void DrawMessage(DrawingContext dc, SequenceDiagram s, SeqItem m, double y, double h, Dictionary<string, int> idx)
    {
        var a = s.Participants[idx[m.From]];
        var b = s.Participants[idx[m.To]];
        Color col = _t.IsDark ? A(_t.TextDim, 0.85) : A(_t.TextDim, 0.8);
        var pen = P(col, 1.4, m.Dashed);
        double lineY = y + h - 12;
        var label = Text(m.Text, _fs * 0.9, _t.Text);
        if (a == b)
        {
            double lx = a.X, w = 36;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(lx, lineY - 22), false, false);
                ctx.BezierTo(new Point(lx + w, lineY - 22), new Point(lx + w, lineY), new Point(lx + 6, lineY), true, true);
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
            ArrowHead(dc, new Point(lx + 1, lineY), new Vector(-1, 0), m.Arrow, col);
            dc.DrawText(label, new Point(lx + w + 8, lineY - 12 - label.Height / 2));
            Extras(dc, s, m, new Point(lx - 2, lineY - 22), new Point(lx + w + 12 + label.Width, lineY - 11), col);
            return;
        }
        double dir = Math.Sign(b.X - a.X);
        var from = new Point(a.X, lineY);
        var to = new Point(b.X - dir * 2, lineY);
        dc.DrawLine(pen, from, to - new Vector(dir * 5, 0));
        ArrowHead(dc, to, new Vector(dir, 0), m.Arrow, col);
        double mid = (a.X + b.X) / 2;
        dc.DrawText(label, new Point(mid - label.Width / 2, lineY - label.Height - 3));
        Extras(dc, s, m, from, new Point(mid + label.Width / 2 + 8, lineY - label.Height / 2 - 3), col);
    }

    /// <summary>Nummer am Anfang der Nachricht (autonumber) und Zeit-Abzeichen hinter dem Text.</summary>
    void Extras(DrawingContext dc, SequenceDiagram s, SeqItem m, Point start, Point afterText, Color col)
    {
        if (s.AutoNumber)
        {
            var num = Text(m.Number.ToString(), _fs * 0.72, _t.OnAccent, _uiBold);
            double r = Math.Max(num.Width, num.Height) / 2 + 3;
            dc.DrawEllipse(B(_t.Accent), null, start, r, r);
            dc.DrawText(num, new Point(start.X - num.Width / 2, start.Y - num.Height / 2));
        }
        if (m.Time is { } t)
        {
            string lbl = Durations.Format(t);
            double w = Text(lbl, _fs * 0.82, _t.TextDim).Width + 30;
            TimeBadge(dc, new Point(afterText.X + w / 2, afterText.Y), t, false);
        }
    }

    void ArrowHead(DrawingContext dc, Point tip, Vector dir, string kind, Color col)
    {
        var n = new Vector(-dir.Y, dir.X);
        double s = 9;
        if (kind.StartsWith("x"))
        {
            var pen = P(col, 1.6);
            dc.DrawLine(pen, tip - dir * 4 + n * 5 - dir * 5, tip + dir * 1 - n * 5 - dir * 5);
            dc.DrawLine(pen, tip - dir * 4 - n * 5 - dir * 5, tip + dir * 1 + n * 5 - dir * 5);
            return;
        }
        if (kind.StartsWith(">>"))
        {
            Arrow(dc, tip, dir, s, col);
            return;
        }
        // offene Spitze (-> und asynchron -) )
        var p = P(col, 1.5);
        dc.DrawLine(p, tip, tip - dir * s + n * s * 0.5);
        dc.DrawLine(p, tip, tip - dir * s - n * s * 0.5);
    }

    void DrawNote(DrawingContext dc, SequenceDiagram s, SeqItem it, double y, double h, Dictionary<string, int> idx)
    {
        var a = s.Participants[idx[it.From]];
        var b = s.Participants[idx[it.To]];
        var ft = Text(it.Text, _fs * 0.9, _t.Text, null, 220, TextAlignment.Center);
        double w = Math.Max(Math.Min(220, ft.Width) + 24, it.NoteSide == "over" ? Math.Abs(b.X - a.X) + 60 : 0);
        double cx = it.NoteSide switch
        {
            "left" => a.X - w / 2 - 10,
            "right" => a.X + w / 2 + 10,
            _ => (a.X + b.X) / 2,
        };
        var r = new Rect(cx - w / 2, y + 4, w, h - 10);
        dc.DrawRoundedRectangle(B(Mix(_t.Paper, _t.Amber, _t.IsDark ? 0.2 : 0.16)), P(A(_t.Amber, 0.75), 1.1), r, 7, 7);
        dc.DrawText(ft, new Point(cx - 110, r.Y + (r.Height - ft.Height) / 2));
    }

    // ───────────────────────────── Balken- und Liniendiagramm ─────────────────────────────

    Color[] Palette => new[]
    {
        _t.Accent, _t.Amber, Color.FromRgb(0x2B, 0xB3, 0x9A), Color.FromRgb(0xE0, 0x60, 0x7E),
        Color.FromRgb(0x8E, 0x6B, 0xE8), Color.FromRgb(0x4C, 0xA7, 0xE0), Color.FromRgb(0x9A, 0xA1, 0xAD),
    };

    DiagramPicture RenderXy(XyChart c)
    {
        var parts = new List<(Drawing, double)>();
        int cats = c.Categories.Count;
        var all = c.Series.SelectMany(s => s.Values).ToList();
        double min = c.YMin ?? Math.Min(0, all.Min()), max = c.YMax ?? all.Max();
        if (max <= min) max = min + 1;
        var (tickStep, niceMax) = NiceScale(min, max);
        if (c.YMax == null) max = niceMax;

        double plotH = 220, axisW = 46, slot = Math.Max(56, Math.Min(110, 560.0 / Math.Max(1, cats)));
        double plotW = slot * cats;
        double width = axisW + plotW + 10, height = plotH + 44 + (c.XLabel != null ? 20 : 0);
        double Y(double v) => plotH - (v - min) / (max - min) * plotH;

        var axes = new DrawingGroup();
        using (var dc = axes.Open())
        {
            for (double v = min; v <= max + tickStep / 1000; v += tickStep)
            {
                double y = Y(v) + 8;
                dc.DrawLine(P(_t.Rule, 1), new Point(axisW, y), new Point(axisW + plotW, y));
                var ft = Text(FormatNum(v), _fs * 0.78, _t.TextFaint);
                dc.DrawText(ft, new Point(axisW - 8 - ft.Width, y - ft.Height / 2));
            }
            if (c.YLabel != null)
            {
                var ft = Text(c.YLabel, _fs * 0.8, _t.TextDim);
                dc.PushTransform(new RotateTransform(-90, 8, 8 + plotH / 2));
                dc.DrawText(ft, new Point(8 - ft.Width / 2, 8 + plotH / 2 - ft.Height - 4));
                dc.Pop();
            }
            for (int i = 0; i < cats; i++)
            {
                var ft = Text(c.Categories[i], _fs * 0.85, _t.TextDim, null, slot - 6, TextAlignment.Center);
                ft.MaxLineCount = 2;
                ft.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawText(ft, new Point(axisW + i * slot + 3, plotH + 14));
            }
            if (c.XLabel != null)
            {
                var ft = Text(c.XLabel, _fs * 0.8, _t.TextDim);
                dc.DrawText(ft, new Point(axisW + plotW / 2 - ft.Width / 2, plotH + 44));
            }
        }
        parts.Add((Freeze(axes), 0));

        var bars = c.Series.Where(s => !s.IsLine).ToList();
        var lines = c.Series.Where(s => s.IsLine).ToList();
        var pal = Palette;
        for (int i = 0; i < cats; i++)
        {
            var g = new DrawingGroup();
            using (var dc = g.Open())
            {
                double groupW = slot * 0.62, bw = bars.Count == 0 ? 0 : groupW / bars.Count;
                for (int s = 0; s < bars.Count; s++)
                {
                    if (i >= bars[s].Values.Count) continue;
                    double v = bars[s].Values[i];
                    double x0 = axisW + i * slot + (slot - groupW) / 2 + s * bw;
                    double y0 = Y(Math.Max(v, min)) + 8, y1 = Y(Math.Max(min, Math.Min(0, max))) + 8;
                    if (min > 0) y1 = Y(min) + 8;
                    var r = new Rect(x0 + 2, Math.Min(y0, y1), Math.Max(2, bw - 4), Math.Abs(y1 - y0));
                    var col = pal[s % pal.Length];
                    var brush = new LinearGradientBrush(col, Mix(col, _t.Paper, 0.25), 90);
                    brush.Freeze();
                    dc.DrawRoundedRectangle(brush, null, r, Math.Min(5, r.Width / 2), Math.Min(5, r.Width / 2));
                    var ft = Text(FormatNum(v), _fs * 0.78, _t.TextDim, _uiBold);
                    dc.DrawText(ft, new Point(r.X + r.Width / 2 - ft.Width / 2, r.Y - ft.Height - 2));
                }
            }
            parts.Add((Freeze(g), 0.08 + i * 0.06));
        }
        for (int s = 0; s < lines.Count; s++)
        {
            var col = pal[(bars.Count + s) % pal.Length];
            var g = new DrawingGroup();
            using (var dc = g.Open())
            {
                var pts = lines[s].Values.Select((v, i) => new Point(axisW + i * slot + slot / 2, Y(v) + 8)).ToList();
                for (int i = 1; i < pts.Count; i++) dc.DrawLine(P(col, 2.2), pts[i - 1], pts[i]);
                foreach (var pt in pts) dc.DrawEllipse(B(_t.Paper), P(col, 2), pt, 4, 4);
            }
            parts.Add((Freeze(g), 0.1 + cats * 0.06));
        }
        return Frame(new DrawingGroup(), new Size(width, height), c.Title, null, c.Warnings, parts);
    }

    static (double Step, double Max) NiceScale(double min, double max)
    {
        double range = max - min;
        double rough = range / 5;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        double norm = rough / mag;
        double step = (norm < 1.5 ? 1 : norm < 3 ? 2 : norm < 7 ? 5 : 10) * mag;
        return (step, Math.Ceiling(max / step) * step);
    }

    static string FormatNum(double v) => Math.Abs(v - Math.Round(v)) < 1e-9 ? v.ToString("N0", CultureInfo.CurrentCulture) : v.ToString("0.##", CultureInfo.CurrentCulture);

    // ───────────────────────────── Torte ─────────────────────────────

    DiagramPicture RenderPie(PieChart c)
    {
        var parts = new List<(Drawing, double)>();
        double total = c.Slices.Sum(s => Math.Max(0, s.Value));
        if (total <= 0) return RenderError("Alle Werte sind 0.");
        double R = 82, r = 48, cx = R + 4, cy = R + 4;
        var pal = Palette;
        double angle = -90;
        int i = 0;
        foreach (var (label, value) in c.Slices)
        {
            double sweep = Math.Max(0, value) / total * 360;
            var g = new DrawingGroup();
            using (var dc = g.Open())
            {
                var geo = Donut(new Point(cx, cy), R, r, angle, sweep);
                dc.DrawGeometry(B(pal[i % pal.Length]), P(_t.Paper, 2), geo);
            }
            parts.Add((Freeze(g), 0.05 + i * 0.07));
            angle += sweep;
            i++;
        }
        // Legende
        var legend = new DrawingGroup();
        double lx = 2 * R + 34, ly = 4, maxW = 0;
        using (var dc = legend.Open())
        {
            i = 0;
            foreach (var (label, value) in c.Slices)
            {
                double pct = Math.Max(0, value) / total * 100;
                string txt = c.ShowData ? $"{label}  {pct:0} %  ({FormatNum(value)})" : $"{label}  {pct:0} %";
                var ft = Text(txt, _fs * 0.9, _t.Text);
                dc.DrawRoundedRectangle(B(pal[i % pal.Length]), null, new Rect(lx, ly + ft.Height / 2 - 5, 10, 10), 3, 3);
                dc.DrawText(ft, new Point(lx + 18, ly));
                maxW = Math.Max(maxW, ft.Width + 18);
                ly += ft.Height + 8;
                i++;
            }
        }
        parts.Add((Freeze(legend), 0.1));
        double width = lx + maxW + 6, height = Math.Max(2 * R + 8, ly);
        return Frame(new DrawingGroup(), new Size(width, height), c.Title, null, c.Warnings, parts);
    }

    static Geometry Donut(Point c, double R, double r, double startDeg, double sweepDeg)
    {
        if (sweepDeg >= 359.99)
        {
            var full = new CombinedGeometry(GeometryCombineMode.Exclude, new EllipseGeometry(c, R, R), new EllipseGeometry(c, r, r));
            full.Freeze();
            return full;
        }
        Point Pt(double rad, double deg) => new(c.X + rad * Math.Cos(deg * Math.PI / 180), c.Y + rad * Math.Sin(deg * Math.PI / 180));
        bool large = sweepDeg > 180;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(Pt(R, startDeg), true, true);
            ctx.ArcTo(Pt(R, startDeg + sweepDeg), new Size(R, R), 0, large, SweepDirection.Clockwise, true, true);
            ctx.LineTo(Pt(r, startDeg + sweepDeg), true, true);
            ctx.ArcTo(Pt(r, startDeg), new Size(r, r), 0, large, SweepDirection.Counterclockwise, true, true);
        }
        g.Freeze();
        return g;
    }
}
