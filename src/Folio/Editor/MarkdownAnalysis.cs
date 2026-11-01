using System;
using System.Collections.Generic;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Folio.Editor;

/// <summary>Wie ein Textbereich aussieht. Mehrere Merkmale können zusammenkommen (fett + kursiv).</summary>
[Flags]
public enum MdStyle
{
    None = 0,
    Bold = 1 << 0,
    Italic = 1 << 1,
    Strike = 1 << 2,
    Code = 1 << 3,
    Link = 1 << 4,
    /// <summary>Steuerzeichen (#, **, `, [ ]( ) …) – außerhalb der Cursorzeile unsichtbar.</summary>
    Marker = 1 << 5,
    /// <summary>Gedämpfter Text (HTML, Bild-Adressen).</summary>
    Muted = 1 << 6,
    /// <summary>Text einer erledigten Aufgabe.</summary>
    Done = 1 << 7,
}

/// <summary>Was eine ganze Zeile ist – bestimmt Schrift, Abstand und Hintergrund.</summary>
public enum MdLineKind
{
    Text,
    Heading,
    CodeFence,
    Code,
    Quote,
    Rule,
    Table,
    Html,
}

/// <summary>Ein Bereich im Text (Dokument-Offsets, Ende exklusiv).</summary>
public readonly record struct MdSpan(int Start, int Length, MdStyle Style)
{
    public int End => Start + Length;
}

/// <summary>Ein Element, das statt seiner Zeichen gezeichnet wird (Aufzählungspunkt, Checkbox).</summary>
public enum MdWidgetKind { Bullet, TaskOpen, TaskDone }

public readonly record struct MdWidget(int Start, int Length, MdWidgetKind Kind);

/// <summary>Ein Link: sichtbarer Bereich und Ziel.</summary>
public readonly record struct MdLink(int Start, int Length, string Url);

public sealed class MdLine
{
    public MdLineKind Kind;
    /// <summary>1–6 bei Überschriften.</summary>
    public int Level;
    /// <summary>Verschachtelungstiefe bei Zitaten.</summary>
    public int QuoteDepth;
    /// <summary>Erste und letzte Zeile des Codeblocks, zu dem die Zeile gehört (sonst 0) – der ganze Block zeigt seine Zäune, sobald der Cursor darin steht.</summary>
    public int BlockFirst, BlockLast;
    /// <summary>Codeblock mit Sprache „mermaid“ – wird als Diagramm gezeichnet.</summary>
    public bool IsDiagram;
    /// <summary>Codeblock hat einen schließenden Zaun.</summary>
    public bool Closed;
    /// <summary>Fingerabdruck des Blockinhalts (nur Diagramme) – ändert er sich, wird das Diagramm neu gezeichnet.</summary>
    public int BlockHash;
}

/// <summary>
/// Ergebnis einer Markdown-Analyse: Bereiche mit Stil, ersetzte Elemente und die Art jeder Zeile.
/// Kennt kein WPF – Zeilen sind 1-basiert wie in AvalonEdit.
/// </summary>
public sealed class MarkdownAnalysis
{
    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePreciseSourceLocation()
        .UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough)
        .UseTaskLists()
        .UsePipeTables()
        .UseAutoLinks()
        .Build();

    public static readonly MarkdownAnalysis Empty = new();

    readonly List<MdSpan> _spans = new();
    readonly List<MdWidget> _widgets = new();
    readonly List<MdLink> _links = new();
    readonly HashSet<int> _quoteLines = new();
    readonly Dictionary<int, MdLine> _lines = new();
    int[] _lineStarts = { 0 };
    int _maxSpanLength;

    public IReadOnlyList<MdSpan> Spans => _spans;
    public IReadOnlyList<MdWidget> Widgets => _widgets;
    public IReadOnlyList<MdLink> Links => _links;

    /// <summary>Zeileninfo (1-basiert) oder null für gewöhnliche Textzeilen.</summary>
    public MdLine? Line(int lineNumber) => _lines.TryGetValue(lineNumber, out var l) ? l : null;

    public int LineCount => _lineStarts.Length;

    /// <summary>Alle Diagramm-Blöcke (je die Info ihrer ersten Zeile).</summary>
    public IEnumerable<MdLine> DiagramBlocks
    {
        get
        {
            foreach (var kv in _lines)
                if (kv.Value.IsDiagram && kv.Key == kv.Value.BlockFirst) yield return kv.Value;
        }
    }

    public bool HasWidgetIn(int start, int end)
    {
        foreach (var w in _widgets)
        {
            if (w.Start >= end) return false;
            if (w.Start + w.Length >= start) return true;
        }
        return false;
    }

    /// <summary>Alle Bereiche, die [start, end) berühren. Bereiche sind nach Start sortiert; da keiner länger als
    /// <see cref="_maxSpanLength"/> ist, beginnt die Suche genau dort, wo ein überlappender Bereich frühestens anfangen kann.</summary>
    public IEnumerable<MdSpan> SpansIn(int start, int end)
    {
        int target = start - _maxSpanLength;
        int lo = 0, hi = _spans.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_spans[mid].Start < target) lo = mid + 1; else hi = mid;
        }
        for (int i = lo; i < _spans.Count; i++)
        {
            var s = _spans[i];
            if (s.Start >= end) yield break;
            if (s.End > start) yield return s;
        }
    }

    /// <summary>
    /// Fingerabdruck der Formatierung einer Zeile, relativ zum Zeilenanfang. Gleicher Fingerabdruck vor und nach
    /// einer Änderung = die Zeile muss nicht neu gezeichnet werden.
    /// </summary>
    public int LineSignature(int line)
    {
        if (line < 1 || line > _lineStarts.Length) return 0;
        int ls = _lineStarts[line - 1];
        int le = line < _lineStarts.Length ? _lineStarts[line] : int.MaxValue;
        var h = new HashCode();
        if (_lines.TryGetValue(line, out var l))
        {
            h.Add(l.Kind);
            h.Add(l.Level);
            h.Add(l.QuoteDepth);
            h.Add(l.BlockFirst - line);
            h.Add(l.BlockLast - line);
            h.Add(l.BlockHash);
        }
        foreach (var s in SpansIn(ls, le)) h.Add((s.Start - ls, s.Length, s.Style));
        foreach (var w in _widgets)
        {
            if (w.Start >= le) break;
            if (w.Start + w.Length > ls) h.Add((w.Start - ls, w.Length, w.Kind));
        }
        return h.ToHashCode();
    }

    public static MarkdownAnalysis Analyze(string text)
    {
        var a = new MarkdownAnalysis();
        a.Build(text);
        return a;
    }

    void Build(string text)
    {
        _lineStarts = ComputeLineStarts(text);
        var doc = Markdig.Markdown.Parse(text, Pipeline);
        foreach (var node in doc.Descendants())
        {
            switch (node)
            {
                case HeadingBlock h: AddHeading(text, h); break;
                case FencedCodeBlock f: AddFenced(f); break;
                case CodeBlock c: SetLines(c.Span, MdLineKind.Code); break;
                case QuoteBlock q: AddQuote(text, q); break;
                case ThematicBreakBlock t:
                    SetLines(t.Span, MdLineKind.Rule);
                    _spans.Add(new MdSpan(t.Span.Start, t.Span.Length, MdStyle.Marker));
                    break;
                case Table tb: SetLines(tb.Span, MdLineKind.Table); break;
                case HtmlBlock hb: SetLines(hb.Span, MdLineKind.Html); break;
                case ListItemBlock li: AddListItem(text, li); break;
                case EmphasisInline e: AddEmphasis(e); break;
                case CodeInline ci: AddCodeInline(text, ci); break;
                case LinkInline l: AddLink(text, l); break;
                case AutolinkInline al:
                    if (al.Span.Length >= 2)
                    {
                        _spans.Add(new MdSpan(al.Span.Start, 1, MdStyle.Marker));
                        _spans.Add(new MdSpan(al.Span.Start + 1, al.Span.Length - 2, MdStyle.Link));
                        _links.Add(new MdLink(al.Span.Start, al.Span.Length, al.IsEmail ? "mailto:" + al.Url : al.Url));
                        _spans.Add(new MdSpan(al.Span.End, 1, MdStyle.Marker));
                    }
                    break;
                case HtmlInline hi: _spans.Add(new MdSpan(hi.Span.Start, hi.Span.Length, MdStyle.Muted)); break;
            }
        }
        _spans.RemoveAll(s => s.Length <= 0 || s.Start < 0 || s.End > text.Length);
        _spans.Sort((x, y) => x.Start.CompareTo(y.Start));
        foreach (var s in _spans) _maxSpanLength = Math.Max(_maxSpanLength, s.Length);
        _widgets.Sort((x, y) => x.Start.CompareTo(y.Start));
    }

    static int[] ComputeLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                starts.Add(i + 1);
            }
            else if (c == '\n') starts.Add(i + 1);
        }
        return starts.ToArray();
    }

    /// <summary>1-basierte Zeilennummer eines Offsets.</summary>
    int LineOf(int offset)
    {
        int i = Array.BinarySearch(_lineStarts, offset);
        if (i < 0) i = ~i - 1;
        return i + 1;
    }

    int LineStart(int line) => _lineStarts[Math.Clamp(line - 1, 0, _lineStarts.Length - 1)];

    /// <summary>Offset hinter dem letzten Zeichen der Zeile (vor dem Zeilenumbruch).</summary>
    int LineEnd(string text, int line)
    {
        int e = line < _lineStarts.Length ? _lineStarts[line] : text.Length;
        while (e > LineStart(line) && (text[e - 1] == '\n' || text[e - 1] == '\r')) e--;
        return e;
    }

    MdLine LineInfo(int line)
    {
        if (!_lines.TryGetValue(line, out var l)) _lines[line] = l = new MdLine();
        return l;
    }

    void SetLines(SourceSpan span, MdLineKind kind)
    {
        if (span.IsEmpty) return;
        int first = LineOf(span.Start), last = LineOf(Math.Max(span.Start, span.End));
        for (int n = first; n <= last; n++) LineInfo(n).Kind = kind;
    }

    void AddHeading(string text, HeadingBlock h)
    {
        if (h.Span.IsEmpty) return;
        int start = h.Span.Start;
        if (start < text.Length && text[start] == '#')
        {
            var l = LineInfo(LineOf(start));
            l.Kind = MdLineKind.Heading;
            l.Level = h.Level;
            int p = start;
            while (p < text.Length && text[p] == '#') p++;
            while (p < text.Length && text[p] == ' ') p++;
            _spans.Add(new MdSpan(start, p - start, MdStyle.Marker));
            // optionale schließende # (gehören nicht mehr zum Span der Überschrift – daher bis Zeilenende suchen)
            int end = LineEnd(text, LineOf(start));
            int q = end;
            while (q > p && text[q - 1] == ' ') q--;
            int hashes = q;
            while (hashes > p && text[hashes - 1] == '#') hashes--;
            if (hashes < q && hashes > p && text[hashes - 1] == ' ')
                _spans.Add(new MdSpan(hashes - 1, end - hashes + 1, MdStyle.Marker));
        }
        else
        {
            // Setext: Textzeilen sind die Überschrift, die Unterstreichung (=== / ---) ist Steuerzeichen
            int first = LineOf(start), last = LineOf(h.Span.End);
            for (int n = first; n < last; n++)
            {
                var l = LineInfo(n);
                l.Kind = MdLineKind.Heading;
                l.Level = h.Level;
            }
            var u = LineInfo(last);
            u.Kind = MdLineKind.Heading;
            u.Level = h.Level;
            int us = LineStart(last);
            _spans.Add(new MdSpan(us, h.Span.End + 1 - us, MdStyle.Marker));
        }
    }

    void AddFenced(FencedCodeBlock f)
    {
        if (f.Span.IsEmpty) return;
        int first = LineOf(f.Span.Start), last = LineOf(f.Span.End);
        // Ohne schließenden Zaun endet der Block am Dokumentende – dann ist die letzte Zeile Code, kein Zaun.
        // (Der Span reicht dann nur bis zum öffnenden Zaun; die Inhaltszeilen kennen ihre Zeilennummer.)
        bool closed = f.ClosingFencedCharCount > 0;
        if (f.Lines.Count > 0) last = Math.Max(last, f.Lines.Lines[f.Lines.Count - 1].Line + 1 + (closed ? 1 : 0));
        last = Math.Min(last, _lineStarts.Length);
        bool diagram = string.Equals(f.Info?.Trim(), "mermaid", StringComparison.OrdinalIgnoreCase);
        int hash = diagram ? f.Lines.ToString().GetHashCode() : 0;
        for (int n = first; n <= last; n++)
        {
            var l = LineInfo(n);
            l.Kind = n == first || (closed && n == last) ? MdLineKind.CodeFence : MdLineKind.Code;
            l.BlockFirst = first;
            l.BlockLast = last;
            l.IsDiagram = diagram;
            l.Closed = closed;
            l.BlockHash = hash;
        }
    }

    void AddQuote(string text, QuoteBlock q)
    {
        if (q.Span.IsEmpty) return;
        int first = LineOf(q.Span.Start), last = LineOf(q.Span.End);
        for (int n = first; n <= last; n++)
        {
            // verschachtelte Zitate: die äußere Ebene hat die Zeile schon samt aller > erfasst
            if (!_quoteLines.Add(n)) continue;
            int p = LineStart(n);
            int end = n < _lineStarts.Length ? _lineStarts[n] : text.Length;
            int s = p, depth = 0, markerEnd = p;
            while (s < end)
            {
                char c = text[s];
                if (c == '>') { depth++; s++; markerEnd = s; if (s < end && text[s] == ' ') { s++; markerEnd = s; } }
                else if (c == ' ' && s - p < 4 + depth * 2) s++;
                else break;
            }
            var l = LineInfo(n);
            if (l.Kind == MdLineKind.Text) l.Kind = MdLineKind.Quote;
            l.QuoteDepth = Math.Max(l.QuoteDepth, depth);
            if (markerEnd > p) _spans.Add(new MdSpan(p, markerEnd - p, MdStyle.Marker));
        }
    }

    void AddListItem(string text, ListItemBlock li)
    {
        if (li.Span.IsEmpty || li.Parent is not ListBlock list) return;
        int b = li.Span.Start;
        if (b >= text.Length) return;

        // Aufgabe? Dann steht ein TaskList-Inline am Anfang des ersten Absatzes.
        if (li.Count > 0 && li[0] is ParagraphBlock { Inline: { FirstChild: TaskList task } } para)
        {
            int taskEnd = task.Span.End + 1; // hinter dem ]
            if (taskEnd > b && taskEnd <= text.Length)
            {
                _widgets.Add(new MdWidget(b, taskEnd - b, task.Checked ? MdWidgetKind.TaskDone : MdWidgetKind.TaskOpen));
                if (task.Checked && !para.Span.IsEmpty)
                {
                    int ds = taskEnd;
                    while (ds < text.Length && text[ds] == ' ') ds++;
                    int de = para.Span.End + 1;
                    if (de > ds) _spans.Add(new MdSpan(ds, de - ds, MdStyle.Done));
                }
                return;
            }
        }
        if (!list.IsOrdered && (text[b] == '-' || text[b] == '*' || text[b] == '+'))
            _widgets.Add(new MdWidget(b, 1, MdWidgetKind.Bullet));
        else if (list.IsOrdered)
        {
            int p = b;
            while (p < text.Length && char.IsDigit(text[p])) p++;
            if (p < text.Length && (text[p] == '.' || text[p] == ')')) _spans.Add(new MdSpan(b, p + 1 - b, MdStyle.Muted));
        }
    }

    void AddEmphasis(EmphasisInline e)
    {
        if (e.Span.IsEmpty) return;
        int n = e.DelimiterCount;
        if (e.Span.Length < 2 * n) return;
        MdStyle style = e.DelimiterChar == '~' ? MdStyle.Strike : n >= 2 ? MdStyle.Bold : MdStyle.Italic;
        if (e.DelimiterChar != '~' && n >= 3) style = MdStyle.Bold | MdStyle.Italic;
        _spans.Add(new MdSpan(e.Span.Start, n, MdStyle.Marker));
        _spans.Add(new MdSpan(e.Span.Start + n, e.Span.Length - 2 * n, style));
        _spans.Add(new MdSpan(e.Span.End + 1 - n, n, MdStyle.Marker));
    }

    void AddCodeInline(string text, CodeInline c)
    {
        if (c.Span.IsEmpty) return;
        int n = c.DelimiterCount;
        if (c.Span.Length < 2 * n) return;
        _spans.Add(new MdSpan(c.Span.Start, n, MdStyle.Marker));
        _spans.Add(new MdSpan(c.Span.Start + n, c.Span.Length - 2 * n, MdStyle.Code));
        _spans.Add(new MdSpan(c.Span.End + 1 - n, n, MdStyle.Marker));
    }

    void AddLink(string text, LinkInline l)
    {
        if (l.Span.IsEmpty) return;
        if (l.IsAutoLink)
        {
            _spans.Add(new MdSpan(l.Span.Start, l.Span.Length, MdStyle.Link));
            if (l.Url != null) _links.Add(new MdLink(l.Span.Start, l.Span.Length, l.Url));
            return;
        }
        int start = l.Span.Start, end = l.Span.End + 1;
        int open = l.IsImage ? 2 : 1;
        if (end - start < open + 2 || text[start + open - 1] != '[') return;
        // Ende des Linktexts = das ] vor ( bzw. [
        int labelEnd = l.LabelSpan.IsEmpty ? -1 : l.LabelSpan.End + 1;
        if (labelEnd < start + open || labelEnd >= end || text[labelEnd] != ']')
        {
            labelEnd = text.IndexOf("](", start + open, end - start - open, StringComparison.Ordinal);
            if (labelEnd < 0) return;
        }
        _spans.Add(new MdSpan(start, open, MdStyle.Marker));
        _spans.Add(new MdSpan(start + open, labelEnd - start - open, l.IsImage ? MdStyle.Muted : MdStyle.Link));
        _spans.Add(new MdSpan(labelEnd, end - labelEnd, MdStyle.Marker));
        if (!l.IsImage && l.Url != null) _links.Add(new MdLink(start, end - start, l.Url));
    }
}
