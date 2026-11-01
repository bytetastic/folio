using System;
using System.Linq;
using Folio.Diagrams;
using static Folio.Tests.Program;

namespace Folio.Tests;

/// <summary>Tests für Mermaid-Leser, Zeiten und kritischen Pfad.</summary>
public static class DiagramTests
{
    public static void Register(Action<string, Action> run)
    {
        run("Dauer lesen", DurationParse);
        run("Dauer ausgeben", DurationFormat);
        run("Ablauf: Formen und Kanten", FlowShapes);
        run("Ablauf: Kette, & und Text an Kanten", FlowChains);
        run("Ablauf: Bahnen (subgraph)", FlowLanes);
        run("Ablauf: Zeiten und kritischer Pfad", FlowTimes);
        run("Ablauf: Schleife wird Rückkante", FlowLoop);
        run("Ablauf: Fehler mit Zeilenangabe", FlowError);
        run("Sequenz: Teilnehmer, Nachrichten, Blöcke", Sequence);
        run("Balkendiagramm", XyChart);
        run("Tortendiagramm", Pie);
        run("Unbekannte Art", Unknown);
        run("Mermaid-Kopf mit title", FrontTitle);
        run("BPMN: Bahnen-Beispiel vollständig und verknüpft", BpmnOrder);
        run("BPMN: ohne Bahnen, Schleife, Sonderzeichen", BpmnPlain);
    }

    static void DurationParse()
    {
        Eq(5.0, Durations.Parse("5 s")!.Value.TotalSeconds, "5 s");
        Eq(120.0, Durations.Parse("2 min")!.Value.TotalSeconds, "2 min");
        Eq(5400.0, Durations.Parse("1,5 h")!.Value.TotalSeconds, "1,5 h");
        Eq(5400.0, Durations.Parse("1 h 30 min")!.Value.TotalSeconds, "1 h 30 min");
        Eq(172800.0, Durations.Parse("2 Tage")!.Value.TotalSeconds, "2 Tage");
        Eq(180.0, Durations.Parse("3")!.Value.TotalSeconds, "ohne Einheit = Minuten");
        Eq(null, Durations.Parse("bald"), "unlesbar");
    }

    static void DurationFormat()
    {
        Eq("45 s", Durations.Format(TimeSpan.FromSeconds(45)), "s");
        Eq("2 min 5 s", Durations.Format(TimeSpan.FromSeconds(125)), "min s");
        Eq("1 h 30 min", Durations.Format(TimeSpan.FromMinutes(90)), "h min");
        Eq("1 Tag 2 h", Durations.Format(TimeSpan.FromHours(26)), "Tag");
    }

    static FlowChart Flow(string src)
    {
        var d = MermaidParser.Parse(src);
        if (d is DiagramError e) throw new Exception("Fehler: " + e.Message);
        return (FlowChart)d;
    }

    static void FlowShapes()
    {
        var c = Flow("flowchart LR\n A([Start]) --> B[Schritt]\n B --> C{Frage?}\n C --> D[(Datenbank)]\n D --> E((Kreis))\n E --> F(rund)\n F --> G[[Unterprozess]]");
        Eq(FlowDir.LeftRight, c.Direction, "Richtung");
        Eq("Stadium,Rect,Diamond,Database,Circle,Round,Subroutine", string.Join(",", c.Nodes.Select(n => n.Shape)), "Formen");
        Eq("Start|Schritt|Frage?|Datenbank|Kreis|rund|Unterprozess", string.Join("|", c.Nodes.Select(n => n.Label)), "Texte");
        Eq(6, c.Edges.Count, "Kanten");
        Eq(0, c.Warnings.Count, "Warnungen");
    }

    static void FlowChains()
    {
        var c = Flow("graph TD\n A-->B-->C\n A & B --> D\n C -->|ja| E\n C -- nein --> F\n E -.-> G\n F ==> G\n H[\"Text mit [Klammern]\"]");
        Eq("A>B,B>C,A>D,B>D,C>E,C>F,E>G,F>G", string.Join(",", c.Edges.Select(e => e.From + ">" + e.To)), "Kanten");
        Eq("ja", c.Edges.First(e => e.To == "E").Label, "Text |ja|");
        Eq("nein", c.Edges.First(e => e.To == "F").Label, "Text -- nein -->");
        Eq(EdgeStyle.Dotted, c.Edges.First(e => e.From == "E").Style, "gepunktet");
        Eq(EdgeStyle.Thick, c.Edges.First(e => e.From == "F").Style, "dick");
        Eq("Text mit [Klammern]", c.ById["H"].Label, "Anführungszeichen");
        Eq(0, c.Warnings.Count, "Warnungen");
    }

    const string Order = @"flowchart LR
  subgraph Kunde
    A([Bestellung aufgeben]) --> B[Zahlungsart wählen]
  end
  subgraph Web [Onlineshop]
    C[Bestellung prüfen]
  end
  subgraph Lager[Lagersystem]
    D[(Bestand abfragen)]
  end
  subgraph Versand
    E[Ware zusammenstellen] --> F{Alles vorrätig?}
    F -->|ja| G([Paket verschickt])
    F -->|nein| H[Ware nachbestellen]
  end
  B --> C --> D --> E
  H -.-> D
  %% zeit B 5 s
  %% zeit C 10 s
  %% zeit D 20 s; E 30 s
  %% zeit G 1 min
  %% liegezeit H D 4 h";

    static void FlowLanes()
    {
        var c = Flow(Order);
        Eq("Kunde,Web,Lager,Versand", string.Join(",", c.Lanes.Select(l => l.Id)), "Bahnen");
        Eq("Onlineshop", c.Lanes[1].Title, "Bahntitel [..]");
        Eq("Lagersystem", c.Lanes[2].Title, "Bahntitel ohne Leerzeichen");
        Eq("Kunde,Kunde,Web,Lager,Versand,Versand,Versand,Versand", string.Join(",", c.Nodes.Select(n => n.Lane)), "Zuordnung");
    }

    static void FlowTimes()
    {
        var c = Flow(Order);
        Eq(0, c.Warnings.Count, "Warnungen: " + string.Join("; ", c.Warnings));
        Eq(30.0, c.ById["E"].Time!.Value.TotalSeconds, "zeit mit ;");
        Eq(4.0, c.Edges.First(e => e.From == "H" && e.To == "D").Wait!.Value.TotalHours, "liegezeit");
        Eq(125.0, c.TotalWork.TotalSeconds, "Summe Bearbeitung");
        // Pfad A→B→C→D→E→F→G = 5+10+20+30+60 = 125 s (H→D ist Rückkante und zählt nicht)
        Eq(125.0, c.CriticalPath.TotalSeconds, "längster Pfad");
        Eq("A,B,C,D,E,F,G", string.Join(",", c.Nodes.Where(n => n.Critical).Select(n => n.Id)), "kritische Schritte");
        Eq(true, c.Edges.First(e => e.From == "H").Back, "H→D läuft zurück");
    }

    static void FlowLoop()
    {
        var c = Flow("flowchart TD\n A --> B --> C --> B\n C --> D");
        Eq(true, c.Edges.First(e => e.From == "C" && e.To == "B").Back, "Rückkante");
        Eq(false, c.Edges.First(e => e.From == "B" && e.To == "C").Back, "Vorwärts");
    }

    static void FlowError()
    {
        var d = MermaidParser.Parse("flowchart TD\n A[offen --> B");
        if (d is not DiagramError e) throw new Exception("Fehler erwartet");
        if (!e.Message.Contains("Zeile 2")) throw new Exception("Zeilenangabe fehlt: " + e.Message);
    }

    static void Sequence()
    {
        var d = MermaidParser.Parse(@"sequenceDiagram
  autonumber
  participant M as Onlineshop
  actor U as Mensch
  M->>A: Neue Bestellung
  A-->>M: Danke
  alt vollständig
    A->>S: speichern
  else fehlt
    A-)U: Rückfrage
  end
  Note over A,S: nur intern
  %% zeit 3 2 s");
        if (d is not SequenceDiagram s) throw new Exception("Sequenz erwartet: " + (d as DiagramError)?.Message);
        Eq("M,U,A,S", string.Join(",", s.Participants.Select(p => p.Id)), "Teilnehmer");
        Eq("Onlineshop", s.Participants[0].Label, "as");
        Eq(true, s.Participants[1].IsActor, "actor");
        Eq(true, s.AutoNumber, "autonumber");
        Eq("Message,Message,BlockStart,Message,BlockElse,Message,BlockEnd,Note", string.Join(",", s.Items.Select(i => i.Kind)), "Elemente");
        Eq(true, s.Items[1].Dashed, "gestrichelt");
        Eq(")", s.Items[5].Arrow, "asynchron");
        Eq(2.0, s.Items[3].Time!.Value.TotalSeconds, "Zeit an Nachricht 3");
        Eq(0, s.Warnings.Count, "Warnungen: " + string.Join("; ", s.Warnings));
    }

    static void XyChart()
    {
        var d = MermaidParser.Parse("xychart-beta\n title \"Durchlaufzeit\"\n x-axis [Vorher, Nachher]\n y-axis \"Minuten\" 0 --> 60\n bar [45, 12]\n line [40, 10]");
        if (d is not XyChart c) throw new Exception("xychart erwartet: " + (d as DiagramError)?.Message);
        Eq("Durchlaufzeit", c.Title, "Titel");
        Eq("Vorher,Nachher", string.Join(",", c.Categories), "Kategorien");
        Eq(60.0, c.YMax, "y max");
        Eq("Minuten", c.YLabel, "y Text");
        Eq(2, c.Series.Count, "Reihen");
        Eq(true, c.Series[1].IsLine, "Linie");
    }

    static void Pie()
    {
        var d = MermaidParser.Parse("pie title Zeitanteile\n \"Bearbeitung\" : 40\n \"Warten\" : 60");
        if (d is not PieChart c) throw new Exception("pie erwartet: " + (d as DiagramError)?.Message);
        Eq("Zeitanteile", c.Title, "Titel");
        Eq(2, c.Slices.Count, "Stücke");
        Eq(60.0, c.Slices[1].Value, "Wert");
    }

    static readonly System.Xml.Linq.XNamespace B = "http://www.omg.org/spec/BPMN/20100524/MODEL";
    static readonly System.Xml.Linq.XNamespace BD = "http://www.omg.org/spec/BPMN/20100524/DI";
    static readonly System.Xml.Linq.XNamespace DC = "http://www.omg.org/spec/DD/20100524/DC";

    /// <summary>Prüft, dass alle Verweise im BPMN-XML auf vorhandene Elemente zeigen und alles eine Darstellung hat.</summary>
    static System.Xml.Linq.XDocument CheckBpmn(string xml)
    {
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        var process = doc.Root!.Element(B + "process") ?? throw new Exception("kein process");
        var flowNodes = process.Elements().Where(e => e.Name.LocalName is "task" or "startEvent" or "endEvent" or "exclusiveGateway").ToList();
        var ids = flowNodes.Select(e => (string)e.Attribute("id")!).ToHashSet();
        var flows = process.Elements(B + "sequenceFlow").ToList();
        foreach (var f in flows)
        {
            if (!ids.Contains((string)f.Attribute("sourceRef")!) || !ids.Contains((string)f.Attribute("targetRef")!))
                throw new Exception("Fluss verweist ins Leere: " + f.Attribute("id"));
        }
        var flowIds = flows.Select(f => (string)f.Attribute("id")!).ToHashSet();
        foreach (var n in flowNodes)
            foreach (var r in n.Elements().Where(e => e.Name.LocalName is "incoming" or "outgoing"))
                if (!flowIds.Contains(r.Value)) throw new Exception("incoming/outgoing unbekannt: " + r.Value);
        var allIds = doc.Descendants().Select(e => (string?)e.Attribute("id")).Where(i => i != null).ToList();
        if (allIds.Count != allIds.Distinct().Count()) throw new Exception("IDs doppelt");
        var plane = doc.Descendants(BD + "BPMNPlane").Single();
        var shapeRefs = plane.Elements(BD + "BPMNShape").Select(s => (string)s.Attribute("bpmnElement")!).ToHashSet();
        foreach (var id in ids) if (!shapeRefs.Contains(id)) throw new Exception("keine Darstellung für " + id);
        foreach (var e in plane.Elements(BD + "BPMNEdge"))
        {
            int wp = e.Elements().Count(x => x.Name.LocalName == "waypoint");
            if (wp < 2) throw new Exception("Kante mit weniger als 2 Punkten: " + e.Attribute("bpmnElement"));
        }
        if (plane.Elements(BD + "BPMNEdge").Count() != flows.Count) throw new Exception("nicht jeder Fluss hat eine Kante");
        if (xml.Contains("NaN") || xml.Contains("Infinity")) throw new Exception("ungültige Koordinaten");
        return doc;
    }

    static void BpmnOrder()
    {
        var xml = BpmnExport.ToXml(Flow(Order), "Bestellung", "0.2.0");
        var doc = CheckBpmn(xml);
        var process = doc.Root!.Element(B + "process")!;
        Eq(1, doc.Descendants(B + "participant").Count(), "Pool");
        Eq("Kunde,Onlineshop,Lagersystem,Versand", string.Join(",", process.Descendants(B + "lane").Select(l => (string)l.Attribute("name")!)), "Lanes");
        Eq(1, process.Elements(B + "startEvent").Count(), "Start");
        Eq(1, process.Elements(B + "endEvent").Count(), "Ende");
        Eq(1, process.Elements(B + "exclusiveGateway").Count(), "Gateway");
        Eq(5, process.Elements(B + "task").Count(), "Tasks");
        Eq(8, process.Elements(B + "sequenceFlow").Count(), "Flüsse");
        Eq(8, process.Descendants(B + "flowNodeRef").Count(), "jeder Schritt in genau einer Lane");
        Eq("ja", (string?)process.Elements(B + "sequenceFlow").First(f => ((string)f.Attribute("targetRef")!).Contains("_G")).Attribute("name"), "Flussname");
        if (!xml.Contains("Bearbeitungszeit: 30 s") || !xml.Contains("Liegezeit: 4 h")) throw new Exception("Zeiten fehlen in der Dokumentation");
        // Lanes liegen im Pool und schließen lückenlos aneinander
        var bounds = doc.Descendants(BD + "BPMNShape").ToDictionary(s => (string)s.Attribute("bpmnElement")!, s => s.Element(DC + "Bounds")!);
        double Y(System.Xml.Linq.XElement b) => (double)b.Attribute("y")!;
        double H(System.Xml.Linq.XElement b) => (double)b.Attribute("height")!;
        var pool = bounds[(string)doc.Descendants(B + "participant").Single().Attribute("id")!];
        var laneBounds = process.Descendants(B + "lane").Select(l => bounds[(string)l.Attribute("id")!]).ToList();
        Eq(Y(pool), Y(laneBounds[0]), "erste Lane oben am Pool");
        Eq(Y(pool) + H(pool), Y(laneBounds[^1]) + H(laneBounds[^1]), "letzte Lane unten am Pool");
        for (int i = 1; i < laneBounds.Count; i++) Eq(Y(laneBounds[i - 1]) + H(laneBounds[i - 1]), Y(laneBounds[i]), $"Lane {i} schließt an");
    }

    static void BpmnPlain()
    {
        var xml = BpmnExport.ToXml(Flow("flowchart TD\n S((Start)) --> A[Prüfen & <freigeben>]\n A --> B{OK?}\n B -->|nein| A\n B -->|ja| E((Ende))"), "Test \"Prozess\"", "0.2.0");
        var doc = CheckBpmn(xml);
        Eq(0, doc.Descendants(B + "participant").Count(), "kein Pool ohne Bahnen");
        Eq("Prüfen & <freigeben>", (string?)doc.Descendants(B + "task").Single().Attribute("name"), "Sonderzeichen");
        Eq(1, doc.Descendants(B + "startEvent").Count(), "Start");
        Eq(1, doc.Descendants(B + "endEvent").Count(), "Ende");
    }

    static void FrontTitle()
    {
        var c = Flow("---\ntitle: Online-Bestellung abwickeln\n---\nflowchart LR\n A --> B");
        Eq("Online-Bestellung abwickeln", c.Title, "Titel");
        Eq(2, c.Nodes.Count, "Schritte");
        var xml = BpmnExport.ToXml(c, c.Title!, "0.2.0");
        if (!xml.Contains("name=\"Online-Bestellung abwickeln\"")) throw new Exception("Titel fehlt im BPMN");
    }

    static void Unknown()
    {
        if (MermaidParser.Parse("gantt\n title x") is not DiagramError) throw new Exception("Fehler erwartet");
    }
}
