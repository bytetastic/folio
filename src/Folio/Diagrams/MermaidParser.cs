using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Folio.Diagrams;

/// <summary>
/// Liest eine Teilmenge von Mermaid: flowchart/graph (mit subgraph als Bahnen), sequenceDiagram, xychart-beta, pie.
/// Zusätzlich Folio-Zeiten als Mermaid-Kommentare, die andere Programme ignorieren:
/// <c>%% zeit B 5 s</c> (Bearbeitungszeit eines Schritts bzw. einer Nachricht-Nummer) und
/// <c>%% liegezeit A B 2 h</c> (Wartezeit zwischen zwei Schritten).
/// </summary>
public static class MermaidParser
{
    public static Diagram Parse(string source)
    {
        var lines = source.Replace("\r", "").Split('\n');
        // Mermaid-Kopf („---\ntitle: Name\n---“) vor dem Diagramm: Titel übernehmen, Zeilen überspringen
        string? frontTitle = null;
        int startAt = 0;
        int firstReal = Array.FindIndex(lines, l => l.Trim().Length > 0);
        if (firstReal >= 0 && lines[firstReal].Trim() == "---")
        {
            int close = Array.FindIndex(lines, firstReal + 1, l => l.Trim() == "---");
            if (close > 0)
            {
                for (int i = firstReal + 1; i < close; i++)
                {
                    var m = Regex.Match(lines[i], @"^\s*title\s*:\s*(.+)$", RegexOptions.IgnoreCase);
                    if (m.Success) frontTitle = Unquote(m.Groups[1].Value.Trim());
                }
                startAt = close + 1;
            }
        }
        int first = Array.FindIndex(lines, startAt, l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("%%"));
        if (first < 0) return new DiagramError("Das Diagramm ist leer.");
        string head = lines[first].Trim();
        string kind = head.Split(' ', 2)[0].ToLowerInvariant();
        try
        {
            Diagram d = kind switch
            {
                "flowchart" or "graph" => ParseFlow(lines, first),
                "sequencediagram" => ParseSequence(lines, first),
                "xychart-beta" or "xychart" => ParseXy(lines, first),
                "pie" => ParsePie(lines, first),
                _ => new DiagramError($"Unbekannte Diagrammart „{head}“. Folio kennt flowchart, sequenceDiagram, xychart-beta und pie."),
            };
            if (d is not DiagramError && frontTitle != null) d.Title ??= frontTitle;
            return d;
        }
        catch (FormatException ex)
        {
            return new DiagramError(ex.Message);
        }
    }

    // ───────────────────────────── Zeiten ─────────────────────────────

    static readonly Regex TimeDirective = new(@"^%%\s*(zeit|liegezeit|dauer|wartezeit)\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ───────────────────────────── Ablaufdiagramm ─────────────────────────────

    static readonly Regex Subgraph = new(@"^subgraph\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex IdChars = new(@"^[\p{L}\p{N}_\-]+", RegexOptions.Compiled);
    // Kanten: optional mit Text dazwischen („-- ja -->“) oder |Text| dahinter
    static readonly Regex EdgeWithText = new(@"^(<)?(--|==|-\.)\s+(.+?)\s+(-{2,}|={2,}|\.-+|-\.+-)(>|o|x)?", RegexOptions.Compiled);
    static readonly Regex EdgeOp = new(@"^(<)?(-\.+-|-{2,}|={2,})(>|o|x)?", RegexOptions.Compiled);

    static FlowChart ParseFlow(string[] lines, int first)
    {
        var chart = new FlowChart();
        var dirToken = lines[first].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault()?.ToUpperInvariant();
        chart.Direction = dirToken switch
        {
            "LR" => FlowDir.LeftRight,
            "RL" => FlowDir.RightLeft,
            "BT" => FlowDir.BottomUp,
            _ => FlowDir.TopDown,
        };
        var laneStack = new Stack<FlowLane>();
        var times = new List<(int Line, string[] Parts, bool Wait)>();

        for (int i = first + 1; i < lines.Length; i++)
        {
            string raw = lines[i].Trim();
            if (raw.Length == 0) continue;
            if (raw.StartsWith("%%"))
            {
                var td = TimeDirective.Match(raw);
                if (td.Success)
                {
                    bool wait = td.Groups[1].Value.ToLowerInvariant() is "liegezeit" or "wartezeit";
                    foreach (var piece in td.Groups[2].Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
                        times.Add((i + 1, piece.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries), wait));
                }
                continue;
            }
            foreach (var stmt0 in SplitStatements(raw))
            {
                string stmt = stmt0.Trim();
                if (stmt.Length == 0) continue;
                string lower = stmt.ToLowerInvariant();
                var sg = Subgraph.Match(stmt);
                if (sg.Success)
                {
                    var (id, title) = SubgraphHeader(sg.Groups[1].Value.Trim());
                    var lane = chart.Lanes.FirstOrDefault(l => l.Id == id) ?? new FlowLane { Id = id, Title = title };
                    if (!chart.Lanes.Contains(lane)) chart.Lanes.Add(lane);
                    laneStack.Push(lane);
                    continue;
                }
                if (lower == "end")
                {
                    if (laneStack.Count > 0) laneStack.Pop();
                    continue;
                }
                if (lower.StartsWith("direction ") || lower.StartsWith("classdef ") || lower.StartsWith("class ") ||
                    lower.StartsWith("style ") || lower.StartsWith("linkstyle ") || lower.StartsWith("click ")) continue;
                ParseChain(chart, stmt, laneStack.Count > 0 ? laneStack.Peek() : null, i + 1);
            }
        }

        // Kanten auf Bahnen (subgraph-IDs) sind in Mermaid erlaubt, hier ohne Ziel – still weglassen
        chart.Edges.RemoveAll(e => !chart.ById.ContainsKey(e.From) || !chart.ById.ContainsKey(e.To));

        foreach (var (line, parts, wait) in times) ApplyFlowTime(chart, line, parts, wait);
        FlowAnalysis.ComputeTimes(chart);
        return chart;
    }

    static (string Id, string Title) SubgraphHeader(string s)
    {
        // subgraph id [Titel] | subgraph "Titel" | subgraph Titel mit Leerzeichen
        var m = Regex.Match(s, @"^([\p{L}\p{N}_\-]+)\s*\[(.*)\]$");
        if (m.Success) return (m.Groups[1].Value, Unquote(m.Groups[2].Value));
        if (s.StartsWith('"')) { string t = Unquote(s); return (t, t); }
        return (s, s);
    }

    static void ApplyFlowTime(FlowChart chart, int line, string[] parts, bool wait)
    {
        if (wait)
        {
            // liegezeit A B 2 h   (auch „A-->B 2 h“)
            if (parts.Length >= 1 && parts[0].Contains("-->"))
            {
                var ab = parts[0].Split("-->");
                parts = new[] { ab[0], ab[1] }.Concat(parts.Skip(1)).ToArray();
            }
            if (parts.Length < 3) { chart.Warnings.Add($"Zeile {line}: liegezeit braucht zwei Schritte und eine Dauer"); return; }
            var edge = chart.Edges.FirstOrDefault(e => e.From == parts[0] && e.To == parts[1]);
            var dur = Durations.Parse(string.Join(' ', parts.Skip(2)));
            if (edge == null) { chart.Warnings.Add($"Zeile {line}: keine Verbindung {parts[0]} → {parts[1]}"); return; }
            if (dur == null) { chart.Warnings.Add($"Zeile {line}: Dauer nicht lesbar"); return; }
            edge.Wait = dur;
            return;
        }
        if (parts.Length < 2) { chart.Warnings.Add($"Zeile {line}: zeit braucht Schritt und Dauer"); return; }
        string id = parts[0].TrimEnd(':', '=');
        if (!chart.ById.TryGetValue(id, out var node)) { chart.Warnings.Add($"Zeile {line}: Schritt „{id}“ gibt es nicht"); return; }
        var d = Durations.Parse(string.Join(' ', parts.Skip(1)).TrimStart(':', '='));
        if (d == null) { chart.Warnings.Add($"Zeile {line}: Dauer nicht lesbar"); return; }
        node.Time = d;
    }

    /// <summary>Teilt an „;“ – aber nicht innerhalb von Beschriftungen in Klammern oder Anführungszeichen.</summary>
    static IEnumerable<string> SplitStatements(string s)
    {
        int depth = 0, start = 0;
        bool quote = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"') quote = !quote;
            else if (!quote && (c == '[' || c == '(' || c == '{')) depth++;
            else if (!quote && (c == ']' || c == ')' || c == '}')) depth = Math.Max(0, depth - 1);
            else if (!quote && depth == 0 && c == ';')
            {
                yield return s[start..i];
                start = i + 1;
            }
        }
        yield return s[start..];
    }

    static void ParseChain(FlowChart chart, string stmt, FlowLane? lane, int line)
    {
        int p = 0;
        List<FlowNode>? previous = null;
        while (p < stmt.Length)
        {
            SkipSpace(stmt, ref p);
            var group = new List<FlowNode>();
            while (true)
            {
                SkipSpace(stmt, ref p);
                var node = ReadNode(chart, stmt, ref p, lane, line);
                if (node == null) break;
                group.Add(node);
                SkipSpace(stmt, ref p);
                if (p < stmt.Length && stmt[p] == '&') { p++; continue; }
                break;
            }
            if (group.Count == 0)
            {
                chart.Warnings.Add($"Zeile {line}: „{stmt[p..].Trim()}“ nicht verstanden");
                return;
            }
            if (previous != null && _pendingEdge != null)
            {
                foreach (var a in previous)
                    foreach (var b in group)
                        chart.Edges.Add(new FlowEdge
                        {
                            From = a.Id, To = b.Id, Label = _pendingEdge.Value.Label, Style = _pendingEdge.Value.Style,
                            ArrowEnd = _pendingEdge.Value.End, ArrowStart = _pendingEdge.Value.Start,
                        });
            }
            previous = group;
            _pendingEdge = null;
            SkipSpace(stmt, ref p);
            if (p >= stmt.Length) break;
            if (!ReadEdge(stmt, ref p))
            {
                chart.Warnings.Add($"Zeile {line}: „{stmt[p..].Trim()}“ nicht verstanden");
                return;
            }
        }
    }

    [ThreadStatic] static (string? Label, EdgeStyle Style, bool End, bool Start)? _pendingEdge;

    static bool ReadEdge(string s, ref int p)
    {
        string rest = s[p..];
        var wt = EdgeWithText.Match(rest);
        if (wt.Success)
        {
            _pendingEdge = (wt.Groups[3].Value.Trim(), StyleOf(wt.Groups[2].Value), wt.Groups[5].Value == ">", wt.Groups[1].Success);
            p += wt.Length;
            return true;
        }
        var op = EdgeOp.Match(rest);
        if (!op.Success) return false;
        p += op.Length;
        string? label = null;
        int q = p;
        SkipSpace(s, ref q);
        if (q < s.Length && s[q] == '|')
        {
            int close = s.IndexOf('|', q + 1);
            if (close > q)
            {
                label = Unquote(s[(q + 1)..close].Trim());
                p = close + 1;
            }
        }
        _pendingEdge = (label, StyleOf(op.Groups[2].Value), op.Groups[3].Value == ">", op.Groups[1].Success);
        return true;
    }

    static EdgeStyle StyleOf(string op) => op.StartsWith("=") ? EdgeStyle.Thick : op.Contains('.') ? EdgeStyle.Dotted : EdgeStyle.Solid;

    static FlowNode? ReadNode(FlowChart chart, string s, ref int p, FlowLane? lane, int line)
    {
        var m = IdChars.Match(s[p..]);
        if (!m.Success) return null;
        // Ein Kantenoperator ist keine ID („--“ passt sonst auf [\-]+)
        if (m.Value.StartsWith("-")) return null;
        string id = m.Value;
        // „A-->B“ ohne Leerzeichen: die ID endet vor dem Operator
        int dash = id.IndexOf("--", StringComparison.Ordinal);
        if (dash < 0) dash = id.IndexOf("-.", StringComparison.Ordinal);
        if (dash > 0) id = id[..dash];
        while (id.Length > 1 && id.EndsWith("-")) id = id[..^1]; // „A-.->B“
        p += id.Length;

        string? label = null;
        NodeShape shape = NodeShape.Rect;
        foreach (var (open, close, sh) in Shapes)
        {
            if (string.CompareOrdinal(s, p, open, 0, open.Length) != 0) continue;
            int start = p + open.Length;
            int end = FindClose(s, start, close);
            if (end < 0) throw new FormatException($"Zeile {line}: „{close}“ fehlt nach „{id}{open}…“");
            label = Unquote(s[start..end].Trim());
            shape = sh;
            p = end + close.Length;
            break;
        }

        if (!chart.ById.TryGetValue(id, out var node))
        {
            node = new FlowNode { Id = id, Label = id, Index = chart.Nodes.Count };
            chart.Nodes.Add(node);
            chart.ById[id] = node;
            node.Lane = lane?.Id;
        }
        else if (node.Lane == null && lane != null) node.Lane = lane.Id;
        if (label != null)
        {
            node.Label = CleanLabel(label);
            node.Shape = shape;
        }
        return node;
    }

    // Reihenfolge wichtig: längere Öffner zuerst
    static readonly (string Open, string Close, NodeShape Shape)[] Shapes =
    {
        ("([", "])", NodeShape.Stadium),
        ("[(", ")]", NodeShape.Database),
        ("[[", "]]", NodeShape.Subroutine),
        ("((", "))", NodeShape.Circle),
        ("{{", "}}", NodeShape.Diamond),
        ("[/", "/]", NodeShape.Rect),
        ("[\\", "\\]", NodeShape.Rect),
        ("[", "]", NodeShape.Rect),
        ("(", ")", NodeShape.Round),
        ("{", "}", NodeShape.Diamond),
        (">", "]", NodeShape.Rect),
    };

    static int FindClose(string s, int start, string close)
    {
        bool quote = false;
        for (int i = start; i <= s.Length - close.Length; i++)
        {
            if (s[i] == '"') quote = !quote;
            if (!quote && string.CompareOrdinal(s, i, close, 0, close.Length) == 0) return i;
        }
        return -1;
    }

    static string Unquote(string s) => s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;

    static string CleanLabel(string s) =>
        Regex.Replace(s, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase).Replace("**", "").Replace("`", "").Trim();

    static void SkipSpace(string s, ref int p)
    {
        while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
    }

    // ───────────────────────────── Sequenzdiagramm ─────────────────────────────

    static readonly Regex ParticipantRe = new(@"^(participant|actor)\s+(.+?)(?:\s+as\s+(.+))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex MessageRe = new(@"^(.+?)\s*(-->>|->>|-->|->|--x|-x|--\)|-\))\s*([+-]?)(.+?)\s*:\s*(.*)$", RegexOptions.Compiled);
    static readonly Regex NoteRe = new(@"^note\s+(over|left of|right of)\s+(.+?)\s*:\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex BlockRe = new(@"^(loop|alt|opt|par|critical|break|rect)\b\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex ElseRe = new(@"^(else|and|option)\b\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static SequenceDiagram ParseSequence(string[] lines, int first)
    {
        var d = new SequenceDiagram();
        int number = 0, depth = 0;
        var times = new List<(int Line, string[] Parts)>();

        Participant Ensure(string id)
        {
            id = id.Trim();
            var p = d.Participants.FirstOrDefault(x => x.Id == id);
            if (p == null) d.Participants.Add(p = new Participant { Id = id, Label = id });
            return p;
        }

        for (int i = first + 1; i < lines.Length; i++)
        {
            string s = lines[i].Trim();
            if (s.Length == 0) continue;
            if (s.StartsWith("%%"))
            {
                var td = TimeDirective.Match(s);
                if (td.Success) times.Add((i + 1, td.Groups[2].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
                continue;
            }
            string lower = s.ToLowerInvariant();
            if (lower == "autonumber" || lower.StartsWith("autonumber ")) { d.AutoNumber = true; continue; }
            if (lower.StartsWith("title ")) { d.Title = s[6..].Trim(); continue; }
            if (lower.StartsWith("activate ") || lower.StartsWith("deactivate ") || lower.StartsWith("box ") || lower == "box") continue;
            if (lower == "end")
            {
                if (depth > 0) { depth--; d.Items.Add(new SeqItem { Kind = SeqKind.BlockEnd }); }
                continue;
            }
            var pm = ParticipantRe.Match(s);
            if (pm.Success)
            {
                var p = Ensure(pm.Groups[2].Value);
                p.IsActor = pm.Groups[1].Value.Equals("actor", StringComparison.OrdinalIgnoreCase);
                if (pm.Groups[3].Success) p.Label = pm.Groups[3].Value.Trim();
                continue;
            }
            var nm = NoteRe.Match(s);
            if (nm.Success)
            {
                var ids = nm.Groups[2].Value.Split(',').Select(x => Ensure(x).Id).ToList();
                d.Items.Add(new SeqItem
                {
                    Kind = SeqKind.Note,
                    From = ids[0],
                    To = ids[^1],
                    Text = CleanLabel(nm.Groups[3].Value),
                    NoteSide = nm.Groups[1].Value.ToLowerInvariant().StartsWith("left") ? "left" : nm.Groups[1].Value.ToLowerInvariant().StartsWith("right") ? "right" : "over",
                });
                continue;
            }
            var bm = BlockRe.Match(s);
            if (bm.Success)
            {
                depth++;
                d.Items.Add(new SeqItem { Kind = SeqKind.BlockStart, BlockType = bm.Groups[1].Value.ToLowerInvariant(), Text = bm.Groups[2].Value.Trim() });
                continue;
            }
            var em = ElseRe.Match(s);
            if (em.Success && depth > 0)
            {
                d.Items.Add(new SeqItem { Kind = SeqKind.BlockElse, Text = em.Groups[2].Value.Trim() });
                continue;
            }
            var mm = MessageRe.Match(s);
            if (mm.Success)
            {
                string op = mm.Groups[2].Value;
                var from = Ensure(mm.Groups[1].Value);
                var to = Ensure(mm.Groups[4].Value);
                d.Items.Add(new SeqItem
                {
                    Kind = SeqKind.Message,
                    From = from.Id,
                    To = to.Id,
                    Text = CleanLabel(mm.Groups[5].Value),
                    Dashed = op.StartsWith("--"),
                    Arrow = op.TrimStart('-'),
                    Number = ++number,
                });
                continue;
            }
            d.Warnings.Add($"Zeile {i + 1}: „{s}“ nicht verstanden");
        }

        foreach (var (line, parts) in times)
        {
            if (parts.Length < 2 || !int.TryParse(parts[0].TrimStart('#').TrimEnd(':'), out int n)) { d.Warnings.Add($"Zeile {line}: zeit <Nummer> <Dauer>"); continue; }
            var msg = d.Items.FirstOrDefault(x => x.Kind == SeqKind.Message && x.Number == n);
            var dur = Durations.Parse(string.Join(' ', parts.Skip(1)));
            if (msg == null || dur == null) { d.Warnings.Add($"Zeile {line}: Nachricht {n} oder Dauer unbekannt"); continue; }
            msg.Time = dur;
        }
        if (d.Participants.Count == 0) throw new FormatException("Keine Teilnehmer gefunden – z. B. A->>B: Hallo");
        return d;
    }

    // ───────────────────────────── Balken/Linien (xychart-beta) ─────────────────────────────

    static XyChart ParseXy(string[] lines, int first)
    {
        var c = new XyChart { Horizontal = lines[first].Contains("horizontal", StringComparison.OrdinalIgnoreCase) };
        for (int i = first + 1; i < lines.Length; i++)
        {
            string s = lines[i].Trim();
            if (s.Length == 0 || s.StartsWith("%%")) continue;
            string lower = s.ToLowerInvariant();
            if (lower.StartsWith("title")) { c.Title = Unquote(s[5..].Trim()); continue; }
            if (lower.StartsWith("x-axis"))
            {
                string rest = s[6..].Trim();
                int b = rest.IndexOf('[');
                if (b >= 0)
                {
                    if (b > 0) c.XLabel = Unquote(rest[..b].Trim());
                    int e = rest.LastIndexOf(']');
                    c.Categories.AddRange(SplitList(rest[(b + 1)..(e > b ? e : rest.Length)]));
                }
                else c.XLabel = Unquote(rest);
                continue;
            }
            if (lower.StartsWith("y-axis"))
            {
                string rest = s[6..].Trim();
                var range = Regex.Match(rest, @"(-?\d+(?:[.,]\d+)?)\s*-->\s*(-?\d+(?:[.,]\d+)?)\s*$");
                if (range.Success)
                {
                    c.YMin = Num(range.Groups[1].Value, i);
                    c.YMax = Num(range.Groups[2].Value, i);
                    rest = rest[..range.Index].Trim();
                }
                if (rest.Length > 0) c.YLabel = Unquote(rest);
                continue;
            }
            if (lower.StartsWith("bar") || lower.StartsWith("line"))
            {
                bool line = lower.StartsWith("line");
                int b = s.IndexOf('['), e = s.LastIndexOf(']');
                if (b < 0 || e < b) throw new FormatException($"Zeile {i + 1}: Werte in [ ] erwartet");
                var series = new ChartSeries { IsLine = line };
                foreach (var v in SplitList(s[(b + 1)..e])) series.Values.Add(Num(v, i));
                c.Series.Add(series);
                continue;
            }
            c.Warnings.Add($"Zeile {i + 1}: „{s}“ nicht verstanden");
        }
        if (c.Series.Count == 0) throw new FormatException("Keine Werte – z. B. bar [12, 30, 8]");
        int n = c.Series.Max(x => x.Values.Count);
        while (c.Categories.Count < n) c.Categories.Add((c.Categories.Count + 1).ToString());
        return c;
    }

    static IEnumerable<string> SplitList(string s) =>
        s.Split(',').Select(x => Unquote(x.Trim())).Where(x => x.Length > 0);

    static double Num(string s, int line)
    {
        if (double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
        throw new FormatException($"Zeile {line + 1}: „{s.Trim()}“ ist keine Zahl");
    }

    // ───────────────────────────── Torte (pie) ─────────────────────────────

    static PieChart ParsePie(string[] lines, int first)
    {
        var c = new PieChart();
        string head = lines[first].Trim();
        if (head.Contains("showData", StringComparison.OrdinalIgnoreCase)) c.ShowData = true;
        int t = head.IndexOf("title", StringComparison.OrdinalIgnoreCase);
        if (t >= 0) c.Title = Unquote(head[(t + 5)..].Trim());
        for (int i = first + 1; i < lines.Length; i++)
        {
            string s = lines[i].Trim();
            if (s.Length == 0 || s.StartsWith("%%")) continue;
            if (s.StartsWith("title", StringComparison.OrdinalIgnoreCase)) { c.Title = Unquote(s[5..].Trim()); continue; }
            if (s.Equals("showData", StringComparison.OrdinalIgnoreCase)) { c.ShowData = true; continue; }
            var m = Regex.Match(s, "^\"(.*)\"\\s*:\\s*(.+)$");
            if (!m.Success) { c.Warnings.Add($"Zeile {i + 1}: „\"Name\" : Wert“ erwartet"); continue; }
            c.Slices.Add((m.Groups[1].Value, Num(m.Groups[2].Value, i)));
        }
        if (c.Slices.Count == 0) throw new FormatException("Keine Werte – z. B. \"Bearbeitung\" : 40");
        return c;
    }
}

/// <summary>Zeiten eines Ablaufs: Summen und längster Pfad (kritischer Pfad).</summary>
public static class FlowAnalysis
{
    public static void ComputeTimes(FlowChart chart)
    {
        chart.HasTimes = chart.Nodes.Any(n => n.Time != null) || chart.Edges.Any(e => e.Wait != null);
        chart.TotalWork = TimeSpan.FromSeconds(chart.Nodes.Sum(n => n.Time?.TotalSeconds ?? 0));
        chart.TotalWait = TimeSpan.FromSeconds(chart.Edges.Sum(e => e.Wait?.TotalSeconds ?? 0));
        MarkBackEdges(chart);
        if (!chart.HasTimes) return;

        // Längster Pfad im azyklischen Teil (Rückkanten zählen nicht): Gewicht = Schrittzeit + Liegezeit
        var order = TopoOrder(chart);
        var best = new Dictionary<string, double>();
        var via = new Dictionary<string, FlowEdge?>();
        foreach (var n in order)
        {
            double own = n.Time?.TotalSeconds ?? 0;
            double bestIn = 0;
            FlowEdge? bestEdge = null;
            foreach (var e in chart.Edges.Where(e => !e.Back && e.To == n.Id && e.From != e.To))
            {
                double v = best[e.From] + (e.Wait?.TotalSeconds ?? 0);
                if (bestEdge == null || v > bestIn) { bestIn = v; bestEdge = e; }
            }
            best[n.Id] = bestIn + own;
            via[n.Id] = bestEdge;
        }
        // bei gleicher Dauer den späteren Schritt nehmen – so reicht der Pfad bis zum Ende, auch wenn das keine Zeit hat
        var end = order.Select((n, i) => (n, i)).OrderByDescending(x => best[x.n.Id]).ThenByDescending(x => x.i).First().n;
        chart.CriticalPath = TimeSpan.FromSeconds(best[end.Id]);
        for (var cur = end; cur != null;)
        {
            cur.Critical = true;
            var e = via[cur.Id];
            if (e == null) break;
            e.Critical = true;
            cur = chart.ById[e.From];
        }
    }

    /// <summary>Kanten, die einen Kreis schließen (Schleifen), als Rückkanten markieren – Tiefensuche in Reihenfolge des Auftretens.</summary>
    public static void MarkBackEdges(FlowChart chart)
    {
        var state = new Dictionary<string, int>(); // 0 neu, 1 aktiv, 2 fertig
        foreach (var e in chart.Edges) e.Back = false;
        void Visit(string id)
        {
            state[id] = 1;
            foreach (var e in chart.Edges.Where(e => e.From == id))
            {
                if (e.From == e.To) { e.Back = true; continue; }
                int s = state.GetValueOrDefault(e.To);
                if (s == 1) e.Back = true;
                else if (s == 0) Visit(e.To);
            }
            state[id] = 2;
        }
        // Zuerst von Knoten ohne Eingang starten (echte Anfänge), dann die übrigen
        var hasIn = chart.Edges.Where(e => e.From != e.To).Select(e => e.To).ToHashSet();
        foreach (var n in chart.Nodes.Where(n => !hasIn.Contains(n.Id))) if (state.GetValueOrDefault(n.Id) == 0) Visit(n.Id);
        foreach (var n in chart.Nodes) if (state.GetValueOrDefault(n.Id) == 0) Visit(n.Id);
    }

    public static List<FlowNode> TopoOrder(FlowChart chart)
    {
        var indeg = chart.Nodes.ToDictionary(n => n.Id, _ => 0);
        foreach (var e in chart.Edges.Where(e => !e.Back && e.From != e.To)) indeg[e.To]++;
        var queue = new Queue<FlowNode>(chart.Nodes.Where(n => indeg[n.Id] == 0));
        var result = new List<FlowNode>();
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            result.Add(n);
            foreach (var e in chart.Edges.Where(e => !e.Back && e.From == n.Id && e.From != e.To))
                if (--indeg[e.To] == 0) queue.Enqueue(chart.ById[e.To]);
        }
        // Sicherheitsnetz: falls doch ein Kreis übrig ist, Rest in Auftrittsreihenfolge
        foreach (var n in chart.Nodes) if (!result.Contains(n)) result.Add(n);
        return result;
    }
}
