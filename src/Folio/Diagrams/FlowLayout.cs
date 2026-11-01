using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Folio.Diagrams;

/// <summary>Geometrie einer Kante: kubische Bézierkurve und Lage der Beschriftung.</summary>
public sealed class EdgePath
{
    public FlowEdge Edge = null!;
    public Point P0, C1, C2, P3;
    public Point LabelAt;
}

/// <summary>Ergebnis der Anordnung.</summary>
public sealed class FlowGeometry
{
    public Rect Bounds;
    public List<EdgePath> Edges { get; } = new();
    /// <summary>Bahnen als Rechtecke samt Titelfläche.</summary>
    public List<(FlowLane Lane, Rect Band, Rect Header)> Lanes { get; } = new();
}

/// <summary>
/// Ordnet ein Ablaufdiagramm in Ebenen an (vereinfachtes Sugiyama-Verfahren):
/// Ebenen nach längstem Weg, Reihenfolge per Schwerpunkt-Verfahren, Bahnen als Bänder quer zur Flussrichtung,
/// Kanten als weiche Bézierkurven mit verteilten Anschlusspunkten.
/// </summary>
public static class FlowLayout
{
    const double PadX = 14, PadY = 9, MinW = 92, MaxTextW = 168, MinH = 40;
    const double CrossGap = 22, LanePad = 16, LaneHeaderW = 112, LaneHeaderH = 30;

    /// <param name="fixedSize">Feste Größe je Knoten (z. B. BPMN-Standardgrößen beim Export), sonst aus dem Text.</param>
    /// <param name="laneHeader">Breite bzw. Höhe der Titelfläche einer Bahn (Standard: Folio-Ansicht).</param>
    public static FlowGeometry Layout(FlowChart c, Func<string, double, Size> measure, Func<FlowNode, Size?>? fixedSize = null, double? laneHeader = null)
    {
        var g = new FlowGeometry();
        if (c.Nodes.Count == 0) return g;
        bool horiz = c.Horizontal;

        // ── Größen ──
        foreach (var n in c.Nodes)
        {
            var t = measure(n.Label, MaxTextW);
            double w = Math.Max(MinW, t.Width + 2 * PadX), h = Math.Max(MinH, t.Height + 2 * PadY);
            switch (n.Shape)
            {
                case NodeShape.Diamond:
                    w = Math.Max(110, t.Width * 1.45 + 34);
                    h = Math.Max(64, t.Height * 1.6 + 26);
                    break;
                case NodeShape.Circle:
                    w = h = Math.Max(56, Math.Max(t.Width, t.Height) + 30);
                    break;
                case NodeShape.Database:
                    h += 14;
                    break;
                case NodeShape.Stadium:
                    w += 12;
                    break;
            }
            if (n.Time != null && n.Shape != NodeShape.Diamond) h += 8; // Platz für das Zeit-Abzeichen am unteren Rand
            if (fixedSize?.Invoke(n) is { } fs) { w = fs.Width; h = fs.Height; }
            n.Width = w;
            n.Height = h;
        }

        // ── Bahnen ──
        var lanes = c.Lanes.ToList();
        if (lanes.Count > 0 && c.Nodes.Any(n => n.Lane == null))
        {
            var rest = new FlowLane { Id = "\u0000rest", Title = "" };
            lanes.Add(rest);
            foreach (var n in c.Nodes.Where(n => n.Lane == null)) n.Lane = rest.Id;
        }
        var laneIndex = lanes.Select((l, i) => (l.Id, i)).ToDictionary(x => x.Id, x => x.i);
        int LaneOf(FlowNode n) => n.Lane != null && laneIndex.TryGetValue(n.Lane, out var i) ? i : 0;

        // ── Ebenen (Rückkanten zählen nicht) ──
        // Mit Bahnen: Übergabe in eine andere Bahn bleibt in derselben Ebene (die Kante läuft quer hinüber) –
        // so wird ein Ablauf, der durch viele Bahnen wandert, nicht unnötig lang.
        FlowAnalysis.MarkBackEdges(c);
        var fwd = c.Edges.Where(e => !e.Back && e.From != e.To).ToList();
        int Step(FlowEdge e) => lanes.Count > 0 && LaneOf(c.ById[e.From]) != LaneOf(c.ById[e.To]) ? 0 : 1;
        var order = FlowAnalysis.TopoOrder(c);
        foreach (var n in c.Nodes) n.Layer = 0;
        foreach (var n in order)
            foreach (var e in fwd.Where(e => e.From == n.Id))
                c.ById[e.To].Layer = Math.Max(c.ById[e.To].Layer, n.Layer + Step(e));
        // Anfänge, die erst später einmünden, direkt vor ihren Nachfolger ziehen
        foreach (var n in c.Nodes)
        {
            if (fwd.Any(e => e.To == n.Id)) continue;
            var succ = fwd.Where(e => e.From == n.Id).Select(e => c.ById[e.To].Layer - Step(e)).ToList();
            if (succ.Count > 0) n.Layer = Math.Max(0, succ.Min());
        }
        int layers = c.Nodes.Max(n => n.Layer) + 1;

        // ── Reihenfolge innerhalb der Ebenen (Schwerpunkt-Verfahren, Bahnen bleiben zusammen) ──
        var byLayer = Enumerable.Range(0, layers).Select(l => c.Nodes.Where(n => n.Layer == l).OrderBy(LaneOf).ThenBy(n => n.Index).ToList()).ToList();
        var rank = new Dictionary<FlowNode, double>();
        void Rank() { foreach (var L in byLayer) for (int i = 0; i < L.Count; i++) rank[L[i]] = i; }
        Rank();
        for (int iter = 0; iter < 6; iter++)
        {
            bool down = iter % 2 == 0;
            var range = down ? Enumerable.Range(1, Math.Max(0, layers - 1)) : Enumerable.Range(0, Math.Max(0, layers - 1)).Reverse();
            foreach (int l in range)
            {
                var L = byLayer[l];
                var bary = new Dictionary<FlowNode, double>();
                foreach (var n in L)
                {
                    var nb = (down ? fwd.Where(e => e.To == n.Id).Select(e => c.ById[e.From]) : fwd.Where(e => e.From == n.Id).Select(e => c.ById[e.To]))
                        .Where(x => rank.ContainsKey(x)).ToList();
                    bary[n] = nb.Count > 0 ? nb.Average(x => rank[x]) : rank[n];
                }
                byLayer[l] = L.OrderBy(LaneOf).ThenBy(n => bary[n]).ThenBy(n => n.Index).ToList();
                Rank();
            }
        }

        // ── Lage entlang der Flussrichtung ──
        double MainSize(FlowNode n) => horiz ? n.Width : n.Height;
        double CrossSize(FlowNode n) => horiz ? n.Height : n.Width;
        double baseGap = horiz ? 56 : 48;
        var layerPos = new double[layers];
        double pos = 0;
        for (int l = 0; l < layers; l++)
        {
            double thick = byLayer[l].Count > 0 ? byLayer[l].Max(MainSize) : 40;
            bool labeled = fwd.Any(e => c.ById[e.From].Layer == l && (e.Label != null || e.Wait != null));
            bool timed = byLayer[l].Any(n => n.Time != null) && !horiz;
            layerPos[l] = pos + thick / 2;
            pos += thick + baseGap + (labeled ? 22 : 0) + (timed ? 12 : 0);
        }

        // ── Lage quer zur Flussrichtung ──
        var cross = new Dictionary<FlowNode, double>();
        var bandStart = new double[Math.Max(1, lanes.Count)];
        var bandSize = new double[Math.Max(1, lanes.Count)];
        int laneCount = Math.Max(1, lanes.Count);
        for (int li = 0; li < laneCount; li++)
        {
            double need = 0;
            foreach (var L in byLayer)
            {
                var part = L.Where(n => lanes.Count == 0 || LaneOf(n) == li).ToList();
                if (part.Count == 0) continue;
                need = Math.Max(need, part.Sum(CrossSize) + CrossGap * (part.Count - 1));
            }
            bandSize[li] = need + (lanes.Count > 0 ? 2 * LanePad : 0);
            bandStart[li] = li == 0 ? 0 : bandStart[li - 1] + bandSize[li - 1];
        }

        // Anfangslage: in jeder Bahn/Ebene mittig
        foreach (var L in byLayer)
            for (int li = 0; li < laneCount; li++)
            {
                var part = L.Where(n => lanes.Count == 0 || LaneOf(n) == li).ToList();
                if (part.Count == 0) continue;
                double total = part.Sum(CrossSize) + CrossGap * (part.Count - 1);
                double p = bandStart[li] + (bandSize[li] - total) / 2;
                foreach (var n in part) { cross[n] = p + CrossSize(n) / 2; p += CrossSize(n) + CrossGap; }
            }

        // Ohne Bahnen: Knoten zu ihren Nachbarn ziehen (gerade Linien), Abstände bleiben gewahrt
        if (lanes.Count == 0)
        {
            for (int iter = 0; iter < 8; iter++)
            {
                bool down = iter % 2 == 0;
                foreach (var L in down ? byLayer : Enumerable.Reverse(byLayer))
                {
                    var desired = L.Select(n =>
                    {
                        var nb = (down ? fwd.Where(e => e.To == n.Id).Select(e => c.ById[e.From]) : fwd.Where(e => e.From == n.Id).Select(e => c.ById[e.To])).ToList();
                        return nb.Count > 0 ? nb.Average(x => cross[x]) : cross[n];
                    }).ToArray();
                    Place(L, desired, cross, CrossSize, double.NegativeInfinity, double.PositiveInfinity);
                }
            }
        }
        else
        {
            for (int iter = 0; iter < 6; iter++)
            {
                bool down = iter % 2 == 0;
                foreach (var L in down ? byLayer : Enumerable.Reverse(byLayer))
                    for (int li = 0; li < laneCount; li++)
                    {
                        var part = L.Where(n => LaneOf(n) == li).ToList();
                        if (part.Count == 0) continue;
                        var desired = part.Select(n =>
                        {
                            var nb = (down ? fwd.Where(e => e.To == n.Id).Select(e => c.ById[e.From]) : fwd.Where(e => e.From == n.Id).Select(e => c.ById[e.To]))
                                .Where(x => LaneOf(x) == li).ToList();
                            return nb.Count > 0 ? nb.Average(x => cross[x]) : cross[n];
                        }).ToArray();
                        Place(part, desired, cross, CrossSize, bandStart[li] + LanePad, bandStart[li] + bandSize[li] - LanePad);
                    }
            }
        }

        // ── in x/y umrechnen ──
        bool reverse = c.Direction is FlowDir.BottomUp or FlowDir.RightLeft;
        double mainMax = layerPos.Length > 0 ? layerPos[^1] : 0;
        double offMain = lanes.Count > 0 ? laneHeader ?? (horiz ? LaneHeaderW : LaneHeaderH) : 0;
        foreach (var n in c.Nodes)
        {
            double m = reverse ? mainMax - layerPos[n.Layer] : layerPos[n.Layer]; // Mitte der Ebene
            if (horiz) { n.X = m; n.Y = cross[n]; }
            else { n.X = cross[n]; n.Y = m; }
        }
        // verschieben, sodass alles bei (0,0) beginnt (plus Platz für Bahntitel)
        double minX = c.Nodes.Min(n => n.X - n.Width / 2), minY = c.Nodes.Min(n => n.Y - n.Height / 2);
        double maxX = c.Nodes.Max(n => n.X + n.Width / 2), maxY = c.Nodes.Max(n => n.Y + n.Height / 2);
        if (lanes.Count > 0)
        {
            if (horiz) { minY = Math.Min(minY, 0); maxY = Math.Max(maxY, bandStart[^1] + bandSize[^1]); }
            else { minX = Math.Min(minX, 0); maxX = Math.Max(maxX, bandStart[^1] + bandSize[^1]); }
        }
        double padMain = 18;
        double dx = -minX + (horiz ? offMain + padMain : 0), dy = -minY + (horiz ? 0 : offMain + padMain);
        foreach (var n in c.Nodes) { n.X += dx; n.Y += dy; }
        double width = maxX - minX + (horiz ? offMain + 2 * padMain : 0);
        double height = maxY - minY + (horiz ? 0 : offMain + 2 * padMain);

        if (lanes.Count > 0)
        {
            for (int li = 0; li < lanes.Count; li++)
            {
                var lane = lanes[li];
                lane.Start = bandStart[li];
                lane.Size = bandSize[li];
                Rect band = horiz
                    ? new Rect(0, bandStart[li] + dy, width, bandSize[li])
                    : new Rect(bandStart[li] + dx, 0, bandSize[li], height);
                Rect header = horiz
                    ? new Rect(0, band.Y, offMain, band.Height)
                    : new Rect(band.X, 0, band.Width, offMain);
                g.Lanes.Add((lane, band, header));
            }
        }

        RouteEdges(c, g, horiz, reverse);
        g.Bounds = new Rect(0, 0, width, height);
        foreach (var e in g.Edges) g.Bounds.Union(new Rect(e.C1, e.C2));
        // Rückkanten können außen herum laufen: Fläche erweitern, Ursprung bleibt
        if (g.Bounds.X < 0 || g.Bounds.Y < 0)
        {
            double sx = -Math.Min(0, g.Bounds.X), sy = -Math.Min(0, g.Bounds.Y);
            foreach (var n in c.Nodes) { n.X += sx; n.Y += sy; }
            foreach (var e in g.Edges) { e.P0.Offset(sx, sy); e.C1.Offset(sx, sy); e.C2.Offset(sx, sy); e.P3.Offset(sx, sy); e.LabelAt.Offset(sx, sy); }
            for (int i = 0; i < g.Lanes.Count; i++)
            {
                var (l, b, h) = g.Lanes[i];
                b.Offset(sx, sy); h.Offset(sx, sy);
                g.Lanes[i] = (l, b, h);
            }
            g.Bounds = new Rect(0, 0, g.Bounds.Width, g.Bounds.Height);
        }
        return g;
    }

    /// <summary>Knoten einer Gruppe möglichst an ihre Wunschlage setzen, ohne dass sie sich überlappen oder die Grenzen verlassen.</summary>
    static void Place(List<FlowNode> part, double[] desired, Dictionary<FlowNode, double> cross, Func<FlowNode, double> size, double min, double max)
    {
        int k = part.Count;
        var p = new double[k];
        for (int i = 0; i < k; i++)
        {
            double lo = i == 0 ? min + size(part[i]) / 2 : p[i - 1] + size(part[i - 1]) / 2 + CrossGap + size(part[i]) / 2;
            p[i] = Math.Max(desired[i], lo);
        }
        for (int i = k - 1; i >= 0; i--)
        {
            double hi = i == k - 1 ? max - size(part[i]) / 2 : p[i + 1] - size(part[i + 1]) / 2 - CrossGap - size(part[i]) / 2;
            p[i] = Math.Min(p[i], hi);
        }
        for (int i = 0; i < k; i++) cross[part[i]] = p[i];
    }

    static void RouteEdges(FlowChart c, FlowGeometry g, bool horiz, bool reverse)
    {
        // Richtung der Hauptachse als Vektor
        var dir = horiz ? new Vector(reverse ? -1 : 1, 0) : new Vector(0, reverse ? -1 : 1);
        var side = new Vector(dir.Y == 0 ? 0 : 1, dir.X == 0 ? 0 : 1); // quer dazu

        // Anschlusspunkte verteilen: mehrere Kanten an derselben Seite nebeneinander
        var outSlots = new Dictionary<FlowEdge, double>();
        var inSlots = new Dictionary<FlowEdge, double>();
        foreach (var n in c.Nodes)
        {
            double span = (horiz ? n.Height : n.Width) * 0.5;
            void Spread(List<FlowEdge> list, Dictionary<FlowEdge, double> slots, Func<FlowEdge, FlowNode> other)
            {
                list = list.OrderBy(e => horiz ? other(e).Y : other(e).X).ToList();
                for (int i = 0; i < list.Count; i++)
                    slots[list[i]] = list.Count == 1 ? 0 : (i / (double)(list.Count - 1) - 0.5) * span;
            }
            // nur Kanten zwischen Ebenen – Übergaben quer in eine andere Bahn haben eigene Anschlüsse
            Spread(c.Edges.Where(e => !e.Back && e.From == n.Id && e.To != n.Id && c.ById[e.To].Layer != n.Layer).ToList(), outSlots, e => c.ById[e.To]);
            Spread(c.Edges.Where(e => !e.Back && e.To == n.Id && e.From != n.Id && c.ById[e.From].Layer != n.Layer).ToList(), inSlots, e => c.ById[e.From]);
        }

        Point Port(FlowNode n, Vector towards, double slot)
        {
            // Rand des Knotens in Richtung „towards“ (Haupt- oder Querachse), verschoben um slot entlang der Kante
            var center = new Point(n.X, n.Y);
            double hw = n.Width / 2, hh = n.Height / 2;
            if (n.Shape is NodeShape.Diamond or NodeShape.Circle) slot *= 0.35; // spitze/runde Formen: nahe an der Spitze bleiben
            if (towards.X != 0) return new Point(n.X + Math.Sign(towards.X) * hw, n.Y + slot);
            return new Point(n.X + slot, n.Y + Math.Sign(towards.Y) * hh);
        }

        foreach (var e in c.Edges)
        {
            var a = c.ById[e.From];
            var b = c.ById[e.To];
            var path = new EdgePath { Edge = e };
            if (e.From == e.To)
            {
                // Schleife am Knoten selbst
                var p = Port(a, side, 0);
                path.P0 = p + dir * -8;
                path.P3 = p + dir * 8;
                path.C1 = path.P0 + side * 34;
                path.C2 = path.P3 + side * 34;
            }
            else if (!e.Back && a.Layer != b.Layer)
            {
                path.P0 = Port(a, dir, outSlots.GetValueOrDefault(e));
                path.P3 = Port(b, -dir, inSlots.GetValueOrDefault(e));
                double dist = Math.Abs(Vector.Multiply(path.P3 - path.P0, dir));
                double k = Math.Max(22, dist * 0.48);
                path.C1 = path.P0 + dir * k;
                path.C2 = path.P3 - dir * k;
            }
            else if (!e.Back)
            {
                // gleiche Ebene: seitlich verbinden
                var toward = new Vector(Math.Sign((b.X - a.X) * side.X + (b.Y - a.Y) * side.Y), 0);
                var sv = side * (toward.X == 0 ? 1 : toward.X);
                path.P0 = Port(a, sv, 0);
                path.P3 = Port(b, -sv, 0);
                path.C1 = path.P0 + sv * 30;
                path.C2 = path.P3 - sv * 30;
            }
            else
            {
                // Rückkante: außen an der Seite zurück, damit sie keine Kästen kreuzt
                double outward = 46 + Math.Abs(a.Layer - b.Layer) * 6;
                path.P0 = Port(a, side, 0);
                path.P3 = Port(b, side, 0);
                double far = Math.Max(Vector.Multiply((Vector)path.P0, side), Vector.Multiply((Vector)path.P3, side)) + outward;
                path.C1 = path.P0 + side * (far - Vector.Multiply((Vector)path.P0, side));
                path.C2 = path.P3 + side * (far - Vector.Multiply((Vector)path.P3, side));
            }
            path.LabelAt = Bezier(path, 0.5);
            g.Edges.Add(path);
        }
    }

    public static Point Bezier(EdgePath p, double t)
    {
        double u = 1 - t;
        return new Point(
            u * u * u * p.P0.X + 3 * u * u * t * p.C1.X + 3 * u * t * t * p.C2.X + t * t * t * p.P3.X,
            u * u * u * p.P0.Y + 3 * u * u * t * p.C1.Y + 3 * u * t * t * p.C2.Y + t * t * t * p.P3.Y);
    }
}
