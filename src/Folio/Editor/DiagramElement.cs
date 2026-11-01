using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using Folio.Diagrams;
using Folio.Visuals;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace Folio.Editor;

/// <summary>
/// Ein ```mermaid-Block als gezeichnetes Diagramm. Zwei Arten:
/// <list type="bullet">
/// <item>Ersatz: verdeckt den ganzen Block (vom öffnenden bis zum schließenden Zaun), solange der Cursor draußen ist.</item>
/// <item>Vorschau: hängt beim Bearbeiten unter dem schließenden Zaun (Dokumentlänge 0) und zeigt den Stand live.</item>
/// </list>
/// </summary>
public sealed class DiagramElement : VisualLineElement
{
    readonly FolioEditor _editor;
    readonly string _source;
    readonly bool _preview;
    readonly int _hash;

    public DiagramElement(FolioEditor editor, string source, int documentLength, int hash, bool preview) : base(1, documentLength)
    {
        _editor = editor;
        _source = source;
        _preview = preview;
        _hash = hash;
    }

    public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) =>
        new DiagramRun(this, TextRunProperties, context.TextView);

    public override int GetNextCaretPosition(int visualColumn, System.Windows.Documents.LogicalDirection direction, CaretPositioningMode mode)
    {
        if (_preview)
        {
            // nur vor der Vorschau halten (= Ende der Zaunzeile)
            if (direction == System.Windows.Documents.LogicalDirection.Forward) return visualColumn < VisualColumn ? VisualColumn : -1;
            return visualColumn > VisualColumn ? VisualColumn : -1;
        }
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

    sealed class DiagramRun : TextEmbeddedObject
    {
        readonly DiagramElement _el;
        readonly TextRunProperties _props;
        readonly TextView _view;
        DiagramPicture? _pic;
        double _scale = 1, _width;

        const double Top = 10, Bottom = 14, PreviewTop = 12;

        public DiagramRun(DiagramElement el, TextRunProperties props, TextView view)
        {
            _el = el;
            _props = props;
            _view = view;
        }

        public override LineBreakCondition BreakBefore => _el._preview ? LineBreakCondition.BreakAlways : LineBreakCondition.BreakPossible;
        public override LineBreakCondition BreakAfter => LineBreakCondition.BreakPossible;
        public override bool HasFixedSize => true;
        public override CharacterBufferReference CharacterBufferReference => default;
        public override int Length => 1;
        public override TextRunProperties Properties => _props;

        double TopSpace => _el._preview ? PreviewTop + 16 : Top;

        void Prepare(double available)
        {
            // Breite: ganze Textspalte. Nicht der Rest der Zeile – die Vorschau beginnt nach ihrem Umbruch ganz links.
            double width = _view.ActualWidth > 0 ? _view.ActualWidth - 8 : available is > 0 and < 1e6 ? available : 600;
            _width = Math.Max(160, width - 2);
            _pic = _el._editor.GetDiagram(_el._source, _width);
            _scale = _pic.Size.Width > _width ? _width / _pic.Size.Width : 1;
        }

        double Height => TopSpace + _pic!.Size.Height * _scale + Bottom;

        public override TextEmbeddedObjectMetrics Format(double remainingParagraphWidth)
        {
            Prepare(remainingParagraphWidth);
            return new TextEmbeddedObjectMetrics(_width, Height, Height);
        }

        public override Rect ComputeBoundingBox(bool rightToLeft, bool sideways)
        {
            if (_pic == null) Prepare(_view.ActualWidth);
            return new Rect(0, -Height, _width, Height);
        }

        public override void Draw(DrawingContext dc, Point origin, bool rightToLeft, bool sideways)
        {
            if (_pic == null) Prepare(_view.ActualWidth);
            var pic = _pic!;
            double top = origin.Y - Height;
            if (_el._preview)
            {
                var t = ThemeManager.Current;
                var label = new FormattedText("Vorschau", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    Typography.Face(Typography.Body, FontStyles.Normal, FontWeights.SemiBold), 11, _el._editor.Brush(t.TextFaint),
                    VisualTreeHelper.GetDpi(_view).PixelsPerDip);
                dc.DrawText(label, new Point(origin.X + 2, top + PreviewTop - 2));
            }
            double age = _el._preview ? 99 : _el._editor.DiagramAge(_el._hash);
            dc.PushTransform(new TranslateTransform(origin.X, top + TopSpace));
            if (_scale < 1) dc.PushTransform(new ScaleTransform(_scale, _scale));
            foreach (var (drawing, delay) in pic.Parts)
            {
                double x = (age - delay) / DiagramPicture.PartFade;
                if (x <= 0) continue;
                if (x >= 1) { dc.DrawDrawing(drawing); continue; }
                double e = 1 - Math.Pow(1 - x, 3);
                dc.PushOpacity(e);
                dc.PushTransform(new TranslateTransform(0, (1 - e) * 8));
                dc.DrawDrawing(drawing);
                dc.Pop();
                dc.Pop();
            }
            if (_scale < 1) dc.Pop();
            dc.Pop();
        }
    }
}
