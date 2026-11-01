using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using Folio.Visuals;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace Folio.Editor;

/// <summary>
/// Ersetzt im Live-Modus Zeichen durch Gezeichnetes: Steuerzeichen werden unsichtbar, „- [ ]“ wird eine Checkbox,
/// „-“ ein Punkt. Am Zeilenende sitzt ein unsichtbarer Abstandshalter, der die Zeile höher macht – AvalonEdit selbst
/// kennt keinen Zeilenabstand.
/// </summary>
public sealed class MarkdownElementGenerator : VisualLineElementGenerator
{
    readonly FolioEditor _editor;
    readonly List<(int Start, int Length, MdWidgetKind? Widget)> _items = new();
    int _lineStart, _lineEnd;
    int _lineNumber;
    // Diagramm in dieser Zeile: ganzer Block ersetzt (_diagramLength > 0) oder Vorschau am Zeilenende
    string? _diagramSource;
    int _diagramLength, _diagramHash;
    bool _diagramPreview;

    public MarkdownElementGenerator(FolioEditor editor) => _editor = editor;

    public override void StartGeneration(ITextRunConstructionContext context)
    {
        base.StartGeneration(context);
        _diagramSource = null;
        _diagramLength = 0;
        _diagramPreview = false;
        _items.Clear();
        var line = context.VisualLine.FirstDocumentLine;
        _lineNumber = line.LineNumber;
        _lineStart = line.Offset;
        _lineEnd = line.EndOffset; // LastDocumentLine ist hier noch nicht gesetzt (keine Faltung in Folio)
        if (!_editor.LiveFormatting) return;

        var a = _editor.Analysis;
        bool revealed = _editor.IsRevealed(_lineNumber);
        int ls = line.Offset, le = line.EndOffset;

        var info = a.Line(_lineNumber);
        if (info is { IsDiagram: true, Closed: true, Kind: MdLineKind.CodeFence } && TryDiagram(context, info, revealed)) return;

        // Zäune von Codeblöcken: ``` bzw. ~~~ ausblenden (die Sprache bleibt als kleines Etikett stehen)
        if (!revealed && info is { Kind: MdLineKind.CodeFence })
        {
            var doc = context.Document;
            int p = ls;
            while (p < le && doc.GetCharAt(p) == ' ') p++;
            int f = p;
            while (f < le && (doc.GetCharAt(f) == '`' || doc.GetCharAt(f) == '~')) f++;
            while (f < le && doc.GetCharAt(f) == ' ') f++;
            if (f > ls) _items.Add((ls, f - ls, null));
        }

        if (!revealed)
        {
            foreach (var s in a.SpansIn(ls, le))
                if ((s.Style & MdStyle.Marker) != 0) _items.Add((Math.Max(s.Start, ls), Math.Min(s.End, le) - Math.Max(s.Start, ls), null));
        }

        // Punkte und Checkboxen bleiben auch in der Cursorzeile – nur wenn der Cursor direkt darin steht, kommt der Text.
        int caret = _editor.CaretOffsetForWidgets;
        foreach (var w in a.Widgets)
        {
            if (w.Start + w.Length <= ls) continue;
            if (w.Start >= le) break;
            if (_editor.Mode == EditorMode.Live && caret >= w.Start && caret <= w.Start + w.Length) continue;
            _items.Add((w.Start, w.Length, w.Kind));
        }

        _items.Sort((x, y) => x.Start.CompareTo(y.Start));
        // Überlappungen entfernen (z. B. Zitat-Zeichen vor einer Aufgabe)
        for (int i = 1; i < _items.Count; i++)
        {
            var prev = _items[i - 1];
            if (_items[i].Start < prev.Start + prev.Length) { _items.RemoveAt(i); i--; }
        }
    }

    /// <summary>Diagramm-Block: am öffnenden Zaun (Cursor draußen) den ganzen Block durch das Bild ersetzen;
    /// am schließenden Zaun (Cursor drin, Live-Modus) die Vorschau darunter hängen.</summary>
    bool TryDiagram(ITextRunConstructionContext context, MdLine info, bool revealed)
    {
        var doc = context.Document;
        if (info.BlockLast <= info.BlockFirst || info.BlockLast > doc.LineCount) return false;
        var first = doc.GetLineByNumber(info.BlockFirst);
        var last = doc.GetLineByNumber(info.BlockLast);
        int contentStart = first.NextLine?.Offset ?? last.Offset;
        string source = contentStart < last.Offset ? doc.GetText(contentStart, last.Offset - contentStart) : "";
        if (!revealed && _lineNumber == info.BlockFirst)
        {
            _diagramSource = source;
            _diagramLength = last.EndOffset - first.Offset;
            _diagramHash = info.BlockHash;
            return true;
        }
        if (revealed && _lineNumber == info.BlockLast && _editor.Mode == EditorMode.Live)
        {
            _diagramSource = source;
            _diagramHash = info.BlockHash;
            _diagramPreview = true;
            return true; // Zaun bleibt sichtbar (Cursorzeile), am Zeilenende kommt die Vorschau
        }
        return false;
    }

    public override int GetFirstInterestedOffset(int startOffset)
    {
        if (_diagramSource != null && !_diagramPreview) return startOffset <= _lineStart ? _lineStart : -1;
        foreach (var it in _items)
            if (it.Start >= startOffset) return it.Start;
        return _editor.LiveFormatting && startOffset <= _lineEnd ? _lineEnd : -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        if (_diagramSource != null)
        {
            if (!_diagramPreview && offset == _lineStart)
                return new DiagramElement(_editor, _diagramSource, _diagramLength, _diagramHash, preview: false);
            if (_diagramPreview && offset == _lineEnd)
                return new DiagramElement(_editor, _diagramSource, 0, _diagramHash, preview: true);
        }
        foreach (var it in _items)
        {
            if (it.Start != offset) continue;
            if (it.Widget is { } kind)
                return new WidgetElement(_editor, kind, it.Length, _lineNumber);
            return new HiddenElement(it.Length);
        }
        if (offset == _lineEnd && _editor.LiveFormatting)
        {
            var st = _editor.StyleOfLine(_lineNumber);
            return new SpacerElement(st.Ascent + st.PadAbove, st.Descent + st.PadBelow);
        }
        return null;
    }
}

/// <summary>Unsichtbare Zeichen: belegen Text im Dokument, aber keinen Platz auf dem Bildschirm.</summary>
public sealed class HiddenElement : VisualLineElement
{
    public HiddenElement(int documentLength) : base(1, documentLength) { }

    public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) => new TextHidden(1);

    // Kein Halt für den Cursor innerhalb – wer hier klickt, landet vor oder hinter den Zeichen.
    public override int GetNextCaretPosition(int visualColumn, System.Windows.Documents.LogicalDirection direction, CaretPositioningMode mode)
    {
        if (direction == System.Windows.Documents.LogicalDirection.Forward)
            return visualColumn < VisualColumn ? VisualColumn : -1;
        return visualColumn > VisualColumn + VisualLength ? VisualColumn + VisualLength : -1;
    }
}

/// <summary>Breitenloser Abstandshalter am Zeilenende, der Ober- und Unterlänge der Zeile vergrößert.</summary>
public sealed class SpacerElement : VisualLineElement
{
    readonly double _ascent, _descent;

    public SpacerElement(double ascent, double descent) : base(1, 0)
    {
        _ascent = ascent;
        _descent = descent;
    }

    public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) =>
        new SpacerRun(_ascent, _descent, TextRunProperties);

    public override int GetNextCaretPosition(int visualColumn, System.Windows.Documents.LogicalDirection direction, CaretPositioningMode mode)
    {
        // Nur die Position vor dem Abstandshalter (= Zeilenende) ist ein Cursorhalt.
        if (direction == System.Windows.Documents.LogicalDirection.Forward)
            return visualColumn < VisualColumn ? VisualColumn : -1;
        return visualColumn > VisualColumn ? VisualColumn : -1;
    }

    sealed class SpacerRun : TextEmbeddedObject
    {
        readonly double _ascent, _descent;
        readonly TextRunProperties _props;

        public SpacerRun(double ascent, double descent, TextRunProperties props)
        {
            _ascent = ascent;
            _descent = descent;
            _props = props;
        }

        public override LineBreakCondition BreakBefore => LineBreakCondition.BreakRestrained;
        public override LineBreakCondition BreakAfter => LineBreakCondition.BreakPossible;
        public override bool HasFixedSize => true;
        public override CharacterBufferReference CharacterBufferReference => default;
        public override int Length => 1;
        public override TextRunProperties Properties => _props;

        public override TextEmbeddedObjectMetrics Format(double remainingParagraphWidth) =>
            new(0, _ascent + _descent, _ascent);

        public override Rect ComputeBoundingBox(bool rightToLeft, bool sideways) => new(0, -_ascent, 0, _ascent + _descent);

        public override void Draw(DrawingContext drawingContext, Point origin, bool rightToLeft, bool sideways) { }
    }
}

/// <summary>Aufzählungspunkt oder Checkbox – gezeichnet statt der Zeichen „-“ bzw. „- [ ]“.</summary>
public sealed class WidgetElement : VisualLineElement
{
    readonly FolioEditor _editor;
    public MdWidgetKind Kind { get; }
    public int LineNumber { get; }

    public WidgetElement(FolioEditor editor, MdWidgetKind kind, int documentLength, int lineNumber) : base(1, documentLength)
    {
        _editor = editor;
        Kind = kind;
        LineNumber = lineNumber;
    }

    public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) =>
        new WidgetRun(this, _editor, TextRunProperties);

    public override int GetNextCaretPosition(int visualColumn, System.Windows.Documents.LogicalDirection direction, CaretPositioningMode mode)
    {
        // Wie ein einzelnes Zeichen: davor und dahinter halten.
        if (direction == System.Windows.Documents.LogicalDirection.Forward)
        {
            if (visualColumn < VisualColumn) return VisualColumn;
            if (visualColumn < VisualColumn + VisualLength) return VisualColumn + VisualLength;
            return -1;
        }
        if (visualColumn > VisualColumn + VisualLength) return VisualColumn + VisualLength;
        if (visualColumn > VisualColumn) return VisualColumn;
        return -1;
    }

    sealed class WidgetRun : TextEmbeddedObject
    {
        readonly WidgetElement _el;
        readonly FolioEditor _editor;
        readonly TextRunProperties _props;
        readonly double _size;

        public WidgetRun(WidgetElement el, FolioEditor editor, TextRunProperties props)
        {
            _el = el;
            _editor = editor;
            _props = props;
            _size = props.FontRenderingEmSize;
        }

        bool IsBullet => _el.Kind == MdWidgetKind.Bullet;
        double Width => IsBullet ? _size * 0.95 : _size * 1.32;

        public override LineBreakCondition BreakBefore => LineBreakCondition.BreakPossible;
        public override LineBreakCondition BreakAfter => LineBreakCondition.BreakRestrained;
        public override bool HasFixedSize => true;
        public override CharacterBufferReference CharacterBufferReference => default;
        public override int Length => 1;
        public override TextRunProperties Properties => _props;

        public override TextEmbeddedObjectMetrics Format(double remainingParagraphWidth)
        {
            var (asc, desc) = Typography.Metrics(Typography.Body, _size);
            return new TextEmbeddedObjectMetrics(Width, asc + desc, asc);
        }

        public override Rect ComputeBoundingBox(bool rightToLeft, bool sideways)
        {
            var (asc, desc) = Typography.Metrics(Typography.Body, _size);
            return new Rect(0, -asc, Width, asc + desc);
        }

        public override void Draw(DrawingContext dc, Point origin, bool rightToLeft, bool sideways)
        {
            var t = ThemeManager.Current;
            // Mitte der Kleinbuchstaben – dort sitzen Punkt und Box optisch richtig
            double midY = origin.Y - _size * 0.33;
            if (IsBullet)
            {
                double r = _size * 0.135;
                dc.DrawEllipse(_editor.Brush(t.TextDim), null, new Point(origin.X + _size * 0.32, midY), r, r);
                return;
            }

            double s = _size * 0.92;
            var box = new Rect(origin.X + 1, midY - s / 2, s, s);
            double radius = s * 0.26;
            bool done = _el.Kind == MdWidgetKind.TaskDone;
            double p = _editor.CheckProgress(_el.LineNumber); // 0..1 seit dem letzten Umschalten, 1 = ruhig
            double fill = done ? Ease(p) : 1 - Ease(p);

            // leere Box
            dc.DrawRoundedRectangle(_editor.Brush(t.Paper), _editor.Pen(t.TextFaint, 1.4), box, radius, radius);
            if (fill <= 0.001) return;

            // gefüllte Box wächst aus der Mitte (mit leichtem Überschwingen beim Abhaken)
            double scale = done ? 0.6 + 0.4 * Back(p) : 0.85 + 0.15 * fill;
            var c = box.Location + new Vector(s / 2, s / 2);
            var filled = new Rect(c.X - s * scale / 2, c.Y - s * scale / 2, s * scale, s * scale);
            dc.PushOpacity(fill);
            dc.DrawRoundedRectangle(_editor.Brush(t.Accent), null, filled, radius * scale, radius * scale);
            // Haken: wird beim Abhaken gezeichnet, als würde er geschrieben
            double draw = done ? Math.Clamp((p - 0.25) / 0.75, 0, 1) : 1;
            if (draw > 0) DrawCheck(dc, box, Ease(draw), _editor.Pen(t.OnAccent, Math.Max(1.6, _size * 0.11), round: true));
            dc.Pop();
        }

        static void DrawCheck(DrawingContext dc, Rect box, double amount, Pen pen)
        {
            var a = new Point(box.X + box.Width * 0.27, box.Y + box.Height * 0.53);
            var b = new Point(box.X + box.Width * 0.44, box.Y + box.Height * 0.69);
            var c = new Point(box.X + box.Width * 0.75, box.Y + box.Height * 0.33);
            double l1 = (b - a).Length, l2 = (c - b).Length;
            double len = (l1 + l2) * amount;
            if (len <= l1)
            {
                dc.DrawLine(pen, a, a + (b - a) * (len / l1));
                return;
            }
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(a, false, false);
                ctx.LineTo(b, true, true);
                ctx.LineTo(b + (c - b) * ((len - l1) / l2), true, true);
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
        }

        static double Ease(double x) => 1 - Math.Pow(1 - Math.Clamp(x, 0, 1), 3);

        static double Back(double x)
        {
            x = Math.Clamp(x, 0, 1);
            const double c1 = 1.70158, c3 = c1 + 1;
            return 1 + c3 * Math.Pow(x - 1, 3) + c1 * Math.Pow(x - 1, 2);
        }
    }
}
