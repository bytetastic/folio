using System;
using System.Collections.Generic;

namespace Folio.Diagrams;

/// <summary>Ein gelesenes Diagramm. Kennt kein WPF – wird auch von den Tests eingebunden.</summary>
public abstract class Diagram
{
    public string? Title { get; set; }
    /// <summary>Hinweise, die das Zeichnen nicht verhindern (unbekannte Zeilen …): „Zeile 4: …“.</summary>
    public List<string> Warnings { get; } = new();
}

/// <summary>Das Diagramm ließ sich nicht lesen.</summary>
public sealed class DiagramError : Diagram
{
    public string Message { get; }
    public DiagramError(string message) => Message = message;
}

// ───────────────────────────── Ablaufdiagramm ─────────────────────────────

public enum FlowDir { TopDown, LeftRight, BottomUp, RightLeft }

public enum NodeShape { Rect, Round, Stadium, Diamond, Circle, Database, Subroutine }

public enum EdgeStyle { Solid, Dotted, Thick }

public sealed class FlowNode
{
    public string Id { get; init; } = "";
    public string Label { get; set; } = "";
    public NodeShape Shape { get; set; } = NodeShape.Rect;
    /// <summary>Bahn (subgraph), null = keine.</summary>
    public string? Lane { get; set; }
    /// <summary>Bearbeitungszeit des Schritts (aus „%% zeit A 2 min“).</summary>
    public TimeSpan? Time { get; set; }
    public bool Critical { get; set; }
    public int Index { get; init; }

    // ── Layout (vom FlowLayout gesetzt) ──
    public int Layer;
    public double X, Y, Width, Height; // Mittelpunkt + Größe
}

public sealed class FlowEdge
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string? Label { get; set; }
    public EdgeStyle Style { get; init; }
    public bool ArrowEnd { get; init; } = true;
    public bool ArrowStart { get; init; }
    /// <summary>Liegezeit zwischen den Schritten (aus „%% liegezeit A B 2 h“).</summary>
    public TimeSpan? Wait { get; set; }
    public bool Critical { get; set; }
    /// <summary>Kante läuft gegen die Flussrichtung (Schleife).</summary>
    public bool Back { get; set; }
}

public sealed class FlowLane
{
    public string Id { get; init; } = "";
    public string Title { get; set; } = "";
    // ── Layout ──
    public double Start, Size; // Lage quer zur Flussrichtung
}

public sealed class FlowChart : Diagram
{
    public FlowDir Direction { get; set; } = FlowDir.TopDown;
    public List<FlowNode> Nodes { get; } = new();
    public Dictionary<string, FlowNode> ById { get; } = new(StringComparer.Ordinal);
    public List<FlowEdge> Edges { get; } = new();
    public List<FlowLane> Lanes { get; } = new();

    public bool Horizontal => Direction is FlowDir.LeftRight or FlowDir.RightLeft;
    public bool HasTimes { get; set; }
    /// <summary>Summe aller Bearbeitungszeiten / Liegezeiten und Dauer des längsten Pfads.</summary>
    public TimeSpan TotalWork, TotalWait, CriticalPath;
}

// ───────────────────────────── Sequenzdiagramm ─────────────────────────────

public sealed class Participant
{
    public string Id { get; init; } = "";
    public string Label { get; set; } = "";
    public bool IsActor { get; set; }
    // ── Layout ──
    public double X, Width;
}

public enum SeqKind { Message, Note, BlockStart, BlockElse, BlockEnd }

public sealed class SeqItem
{
    public SeqKind Kind { get; init; }
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string Text { get; init; } = "";
    public bool Dashed { get; init; }
    /// <summary>„>>“ gefüllte Spitze, „>“ offene, „x“ Kreuz, „)“ asynchron.</summary>
    public string Arrow { get; init; } = ">>";
    public int Number { get; set; }
    /// <summary>Notiz: "over", "left", "right".</summary>
    public string NoteSide { get; init; } = "over";
    /// <summary>Block: loop, alt, opt, par, critical, break.</summary>
    public string BlockType { get; init; } = "";
    public TimeSpan? Time { get; set; }
}

public sealed class SequenceDiagram : Diagram
{
    public List<Participant> Participants { get; } = new();
    public List<SeqItem> Items { get; } = new();
    public bool AutoNumber { get; set; }
}

// ───────────────────────────── Kennzahlen ─────────────────────────────

public sealed class ChartSeries
{
    public bool IsLine { get; init; }
    public List<double> Values { get; } = new();
}

public sealed class XyChart : Diagram
{
    public List<string> Categories { get; } = new();
    public string? XLabel { get; set; }
    public string? YLabel { get; set; }
    public double? YMin { get; set; }
    public double? YMax { get; set; }
    public List<ChartSeries> Series { get; } = new();
    public bool Horizontal { get; set; }
}

public sealed class PieChart : Diagram
{
    public bool ShowData { get; set; }
    public List<(string Label, double Value)> Slices { get; } = new();
}
