using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Xml;
using System.Xml.Linq;

namespace Folio.Diagrams;

/// <summary>
/// Schreibt ein Ablaufdiagramm als BPMN 2.0 XML (.bpmn) – neutral, ohne Camunda-7- oder -8-Erweiterungen, damit beide
/// Modeler es öffnen. Bahnen werden Lanes in einem Pool, Rauten exklusive Gateways, Start/Ende (abgerundet, ohne
/// Eingang bzw. Ausgang) Ereignisse, alles andere einfache Tasks. Zeiten landen als Dokumentation am Element.
/// Positionen stammen aus derselben Anordnung wie in Folio, mit BPMN-Standardgrößen und immer von links nach rechts.
/// Ergebnis ist ein Modell zum Weiterarbeiten, kein ausführbarer Prozess (isExecutable="false").
/// </summary>
public static class BpmnExport
{
    static readonly XNamespace Bpmn = "http://www.omg.org/spec/BPMN/20100524/MODEL";
    static readonly XNamespace BpmnDi = "http://www.omg.org/spec/BPMN/20100524/DI";
    static readonly XNamespace Dc = "http://www.omg.org/spec/DD/20100524/DC";
    static readonly XNamespace Di = "http://www.omg.org/spec/DD/20100524/DI";

    enum Kind { Task, Start, End, Gateway }

    public static string ToXml(FlowChart chart, string processName, string exporterVersion)
    {
        // BPMN-Werkzeuge erwarten waagerechte Pools: immer von links nach rechts anordnen
        chart.Direction = FlowDir.LeftRight;
        FlowAnalysis.MarkBackEdges(chart);
        var kind = new Dictionary<FlowNode, Kind>();
        foreach (var n in chart.Nodes)
        {
            bool hasIn = chart.Edges.Any(e => e.To == n.Id && e.From != n.Id);
            bool hasOut = chart.Edges.Any(e => e.From == n.Id && e.To != n.Id);
            kind[n] = n.Shape switch
            {
                NodeShape.Diamond => Kind.Gateway,
                NodeShape.Stadium or NodeShape.Circle when !hasIn => Kind.Start,
                NodeShape.Stadium or NodeShape.Circle when !hasOut => Kind.End,
                _ => Kind.Task,
            };
        }

        bool lanes = chart.Lanes.Count > 0;
        var geo = FlowLayout.Layout(chart,
            (s, max) => new Size(Math.Min(max, s.Length * 7.0), 18),
            n => kind[n] switch
            {
                Kind.Gateway => new Size(50, 50),
                Kind.Start or Kind.End => new Size(36, 36),
                // Standard 100 × 80; lange Texte bekommen etwas mehr Breite, sonst trennt der Modeler mitten im Wort
                _ => new Size(Math.Clamp(LongestWord(n.Label) * 7.5 + 24, 100, 170), 80),
            },
            laneHeader: lanes ? 72 : null);

        // Rand wie im Camunda Modeler üblich
        const double ox = 160, oy = 80;

        var ids = new HashSet<string>(StringComparer.Ordinal);
        string Id(string prefix, string raw)
        {
            string clean = Regex.Replace(raw, @"[^A-Za-z0-9_\-]", "_");
            string id = prefix + "_" + (clean.Length > 0 ? clean : "x");
            string unique = id;
            for (int i = 2; !ids.Add(unique); i++) unique = id + "_" + i;
            return unique;
        }

        var nodeId = chart.Nodes.ToDictionary(n => n, n => Id(kind[n] switch
        {
            Kind.Start => "StartEvent",
            Kind.End => "EndEvent",
            Kind.Gateway => "Gateway",
            _ => "Activity",
        }, n.Id));
        var flowId = new Dictionary<FlowEdge, string>();
        int fi = 1;
        foreach (var e in chart.Edges) flowId[e] = Id("Flow", (fi++).ToString(CultureInfo.InvariantCulture));

        string processId = Id("Process", "1");
        string collabId = Id("Collaboration", "1");
        string participantId = Id("Participant", "1");

        // ── Prozess ──
        var process = new XElement(Bpmn + "process", new XAttribute("id", processId), new XAttribute("name", processName), new XAttribute("isExecutable", "false"));
        var laneIds = new Dictionary<string, string>();
        if (lanes)
        {
            var laneSet = new XElement(Bpmn + "laneSet", new XAttribute("id", Id("LaneSet", "1")));
            foreach (var (lane, _, _) in geo.Lanes)
            {
                string lid = Id("Lane", lane.Id.Replace("\u0000", ""));
                laneIds[lane.Id] = lid;
                var el = new XElement(Bpmn + "lane", new XAttribute("id", lid), new XAttribute("name", lane.Title));
                foreach (var n in chart.Nodes.Where(n => n.Lane == lane.Id)) el.Add(new XElement(Bpmn + "flowNodeRef", nodeId[n]));
                laneSet.Add(el);
            }
            process.Add(laneSet);
        }
        foreach (var n in chart.Nodes)
        {
            string tag = kind[n] switch
            {
                Kind.Start => "startEvent",
                Kind.End => "endEvent",
                Kind.Gateway => "exclusiveGateway",
                _ => "task",
            };
            var el = new XElement(Bpmn + tag, new XAttribute("id", nodeId[n]), new XAttribute("name", n.Label.Replace('\n', ' ')));
            var doc = new List<string>();
            if (n.Time is { } t) doc.Add("Bearbeitungszeit: " + Durations.Format(t));
            if (n.Shape == NodeShape.Database) doc.Add("Datenquelle (in Folio als Datenbank gezeichnet)");
            if (n.Shape == NodeShape.Subroutine) doc.Add("Teilprozess (in Folio als Unterprozess gezeichnet)");
            if (doc.Count > 0) el.Add(new XElement(Bpmn + "documentation", string.Join("\n", doc)));
            foreach (var e in chart.Edges.Where(e => e.To == n.Id)) el.Add(new XElement(Bpmn + "incoming", flowId[e]));
            foreach (var e in chart.Edges.Where(e => e.From == n.Id)) el.Add(new XElement(Bpmn + "outgoing", flowId[e]));
            process.Add(el);
        }
        foreach (var e in chart.Edges)
        {
            var el = new XElement(Bpmn + "sequenceFlow", new XAttribute("id", flowId[e]),
                new XAttribute("sourceRef", nodeId[chart.ById[e.From]]), new XAttribute("targetRef", nodeId[chart.ById[e.To]]));
            if (!string.IsNullOrWhiteSpace(e.Label)) el.Add(new XAttribute("name", e.Label));
            if (e.Wait is { } w) el.Add(new XElement(Bpmn + "documentation", "Liegezeit: " + Durations.Format(w)));
            process.Add(el);
        }

        // ── Darstellung (BPMN DI) ──
        var plane = new XElement(BpmnDi + "BPMNPlane", new XAttribute("id", Id("BPMNPlane", "1")),
            new XAttribute("bpmnElement", lanes ? collabId : processId));
        if (lanes)
        {
            var pool = new Rect(ox, oy, geo.Bounds.Width, geo.Bounds.Height);
            plane.Add(Shape(Id("Shape", participantId), participantId, pool, horizontal: true));
            for (int i = 0; i < geo.Lanes.Count; i++)
            {
                var (lane, band, _) = geo.Lanes[i];
                // Pool-Titel belegt die ersten 30 px; die Lane beginnt dahinter. Die erste und letzte Lane reichen bis an den
                // Poolrand, damit keine Lücke bleibt (Rückkanten laufen unterhalb der Schritte).
                double top = i == 0 ? 0 : band.Y;
                double bottom = i == geo.Lanes.Count - 1 ? geo.Bounds.Height : band.Bottom;
                var r = new Rect(ox + 30, oy + top, geo.Bounds.Width - 30, bottom - top);
                plane.Add(Shape(Id("Shape", laneIds[lane.Id]), laneIds[lane.Id], r, horizontal: true));
            }
        }
        foreach (var n in chart.Nodes)
        {
            var r = new Rect(ox + n.X - n.Width / 2, oy + n.Y - n.Height / 2, n.Width, n.Height);
            var shape = Shape(Id("Shape", nodeId[n]), nodeId[n], r, horizontal: null);
            if (kind[n] == Kind.Gateway) shape.Add(new XAttribute("isMarkerVisible", "true"));
            plane.Add(shape);
        }
        foreach (var path in geo.Edges)
        {
            var edge = new XElement(BpmnDi + "BPMNEdge", new XAttribute("id", Id("Edge", flowId[path.Edge])), new XAttribute("bpmnElement", flowId[path.Edge]));
            foreach (var p in path.Edge.Back && path.Edge.From != path.Edge.To ? BackWaypoints(path, chart) : Waypoints(path)) edge.Add(new XElement(Di + "waypoint", Num("x", ox + p.X), Num("y", oy + p.Y)));
            plane.Add(edge);
        }

        var definitions = new XElement(Bpmn + "definitions",
            new XAttribute(XNamespace.Xmlns + "bpmn", Bpmn),
            new XAttribute(XNamespace.Xmlns + "bpmndi", BpmnDi),
            new XAttribute(XNamespace.Xmlns + "dc", Dc),
            new XAttribute(XNamespace.Xmlns + "di", Di),
            new XAttribute("id", Id("Definitions", "1")),
            new XAttribute("targetNamespace", "http://bpmn.io/schema/bpmn"),
            new XAttribute("exporter", "Folio"),
            new XAttribute("exporterVersion", exporterVersion));
        if (lanes)
            definitions.Add(new XElement(Bpmn + "collaboration", new XAttribute("id", collabId),
                new XElement(Bpmn + "participant", new XAttribute("id", participantId), new XAttribute("name", processName), new XAttribute("processRef", processId))));
        definitions.Add(process);
        definitions.Add(new XElement(BpmnDi + "BPMNDiagram", new XAttribute("id", Id("BPMNDiagram", "1")), plane));

        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, IndentChars = "  ", Encoding = new UTF8Encoding(false), OmitXmlDeclaration = true }))
            new XDocument(definitions).Save(w);
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + sb.ToString().Replace("\r\n", "\n") + "\n";
    }

    static XElement Shape(string id, string element, Rect r, bool? horizontal)
    {
        var s = new XElement(BpmnDi + "BPMNShape", new XAttribute("id", id), new XAttribute("bpmnElement", element));
        if (horizontal != null) s.Add(new XAttribute("isHorizontal", horizontal.Value ? "true" : "false"));
        s.Add(new XElement(Dc + "Bounds", Num("x", r.X), Num("y", r.Y), Num("width", r.Width), Num("height", r.Height)));
        return s;
    }

    static XAttribute Num(string name, double v) => new(name, Math.Round(v).ToString(CultureInfo.InvariantCulture));

    static int LongestWord(string s) => s.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(w => w.Length).DefaultIfEmpty(0).Max();

    /// <summary>
    /// Rücksprung (z. B. Rückfrage → erneut abfragen): unten aus der Quelle heraus, unter den Schritten entlang, dann seitlich
    /// in das Ziel hinein – so kreuzt die Linie keine Aufgaben, wie man es aus BPMN-Diagrammen kennt.
    /// </summary>
    static IEnumerable<Point> BackWaypoints(EdgePath p, FlowChart chart)
    {
        var a = chart.ById[p.Edge.From];
        var b = chart.ById[p.Edge.To];
        double below = chart.Nodes.Where(n => n.Lane == a.Lane).Max(n => n.Y + n.Height / 2) + 18;
        bool fromRight = a.X > b.X;
        double side = fromRight ? b.X + b.Width / 2 : b.X - b.Width / 2;
        double lane = side + (fromRight ? 22 : -22);
        var start = new Point(a.X, a.Y + a.Height / 2);
        var pts = new List<Point> { start, new(a.X, below), new(lane, below), new(lane, b.Y), new(side, b.Y) };
        for (int i = pts.Count - 1; i > 0; i--)
            if ((pts[i] - pts[i - 1]).Length < 0.5) pts.RemoveAt(i);
        return pts;
    }

    /// <summary>Rechtwinkliger Linienzug statt Kurve – so zeichnen BPMN-Werkzeuge Sequenzflüsse.</summary>
    static IEnumerable<Point> Waypoints(EdgePath p)
    {
        var exit = p.C1 - p.P0;
        var entry = p.P3 - p.C2;
        bool exitH = Math.Abs(exit.X) >= Math.Abs(exit.Y);
        bool entryH = Math.Abs(entry.X) >= Math.Abs(entry.Y);
        var pts = new List<Point> { p.P0 };
        if (Math.Abs(p.P0.X - p.P3.X) < 1 || Math.Abs(p.P0.Y - p.P3.Y) < 1) { }
        else if (exitH && entryH)
        {
            double mx = (p.P0.X + p.P3.X) / 2;
            pts.Add(new Point(mx, p.P0.Y));
            pts.Add(new Point(mx, p.P3.Y));
        }
        else if (!exitH && !entryH)
        {
            // senkrecht hinaus und hinein; Rückkanten laufen außen (dort liegen die Kontrollpunkte)
            double my = p.Edge.Back ? p.C1.Y : (p.P0.Y + p.P3.Y) / 2;
            pts.Add(new Point(p.P0.X, my));
            pts.Add(new Point(p.P3.X, my));
        }
        else if (exitH) pts.Add(new Point(p.P3.X, p.P0.Y));
        else pts.Add(new Point(p.P0.X, p.P3.Y));
        if (p.Edge.From == p.Edge.To)
        {
            // Schleife am selben Element: kleines Rechteck nach unten
            pts = new List<Point> { p.P0, p.C1, p.C2, p.P3 };
        }
        else pts.Add(p.P3);
        // doppelte Punkte entfernen
        for (int i = pts.Count - 1; i > 0; i--)
            if ((pts[i] - pts[i - 1]).Length < 0.5) pts.RemoveAt(i);
        return pts;
    }
}
