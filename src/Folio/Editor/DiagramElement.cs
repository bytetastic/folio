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
/// Breite Diagramme werden höchstens auf <see cref="MinScale"/> verkleinert; was dann nicht passt, lässt sich in der
/// Karte waagerecht verschieben (Umschalt+Mausrad oder die Leiste darunter). Oben rechts öffnet ein Knopf den Viewer.
/// </summary>
public sealed class DiagramElement : VisualLineElement
{
    /// <summary>Kleiner als das wird ein Diagramm im Text nie – sonst ist die Schrift nicht mehr lesbar.</summary>
    public const double MinScale = 0.7;

    readonly FolioEditor _editor;

    public string Source { get; }
    public bool IsPreview { get; }
    public int Hash { get; }

    /// <summary>Zuletzt erzeugter Lauf – hält die gezeichneten Flächen für Mausklicks fest.</summary>
    internal DiagramRun? Run { get; private set; }

    public DiagramElement(FolioEditor editor, string source, int documentLength, int hash, bool preview) : base(1, documentLength)
    {
        _editor = editor;
        Source = source;
        IsPreview = preview;
        Hash = hash;
    }

    public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) =>
        Run = new DiagramRun(this, TextRunProperties, context.TextView);

    public override int GetNextCaretPosition(int visualColumn, System.Windows.Documents.LogicalDirection direction, CaretPositioningMode mode)
    {
        if (IsPreview)
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

    internal sealed class DiagramRun : TextEmbeddedObject
    {
        readonly DiagramElement _el;
        readonly TextRunProperties _props;
        readonly TextView _view;
        DiagramPicture? _pic;
        double _scale = 1, _width, _contentW;
        bool _overflow;

        const double Top = 10, Bottom = 14, PreviewTop = 12, TrackGap = 6, TrackH = 4, HintH = 18;

        // Flächen in Koordinaten der Textzeile (für Mausklicks im Editor), gesetzt beim Zeichnen
        public Rect LocalBounds { get; private set; }
        public Rect LocalContent { get; private set; }
        public Rect LocalButton { get; private set; }
        public Rect LocalTrack { get; private set; }
        public Rect LocalHint { get; private set; }
        public bool Overflow => _overflow;
        public double ScrollMax => Math.Max(0, _contentW - _width);
        public double ContentWidth => _contentW;
        public double VisibleWidth => _width;

        public DiagramRun(DiagramElement el, TextRunProperties props, TextView view)
        {
            _el = el;
            _props = props;
            _view = view;
        }

        public override LineBreakCondition BreakBefore => _el.IsPreview ? LineBreakCondition.BreakAlways : LineBreakCondition.BreakPossible;
        public override LineBreakCondition BreakAfter => LineBreakCondition.BreakPossible;
        public override bool HasFixedSize => true;
        public override CharacterBufferReference CharacterBufferReference => default;
        public override int Length => 1;
        public override TextRunProperties Properties => _props;

        double TopSpace => _el.IsPreview ? PreviewTop + 16 : Top;
        bool Scaled => _scale < 0.985;
        double BottomSpace => Bottom + (_overflow ? TrackGap + TrackH : 0) + (Scaled ? HintH : 0);

        void Prepare(double available)
        {
            // Breite: ganze Textspalte. Nicht der Rest der Zeile – die Vorschau beginnt nach ihrem Umbruch ganz links.
            double width = _view.ActualWidth > 0 ? _view.ActualWidth - 8 : available is > 0 and < 1e6 ? available : 600;
            _width = Math.Max(160, width - 2);
            _pic = _el._editor.GetDiagram(_el.Source, _width);
            double fit = _pic.Size.Width > 0 ? _width / _pic.Size.Width : 1;
            _scale = fit >= 1 ? 1 : Math.Max(fit, MinScale);
            _contentW = _pic.Size.Width * _scale;
            _overflow = _contentW > _width + 0.5;
        }

        double Height => TopSpace + _pic!.Size.Height * _scale + BottomSpace;

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
            var ed = _el._editor;
            var t = ThemeManager.Current;
            double ppd = VisualTreeHelper.GetDpi(_view).PixelsPerDip;
            double top = origin.Y - Height;
            if (_el.IsPreview)
            {
                var label = new FormattedText("Vorschau", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    Typography.Face(Typography.Body, FontStyles.Normal, FontWeights.SemiBold), 11, ed.Brush(t.TextFaint), ppd);
                dc.DrawText(label, new Point(origin.X + 2, top + PreviewTop - 2));
            }

            var content = new Rect(origin.X, top + TopSpace, Math.Min(_contentW, _width), pic.Size.Height * _scale);
            double off = _overflow ? Math.Clamp(ed.DiagramScroll(_el.Hash), 0, ScrollMax) : 0;
            double age = _el.IsPreview ? 99 : ed.DiagramAge(_el.Hash);

            if (_overflow) dc.PushClip(new RectangleGeometry(new Rect(content.X - 1, content.Y - 1, content.Width + 2, content.Height + 2)));
            dc.PushTransform(new TranslateTransform(origin.X - off, content.Y));
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

            double y = content.Bottom;
            LocalTrack = Rect.Empty;
            if (_overflow)
            {
                dc.Pop(); // Clip
                // weiche Kanten dort, wo noch etwas verborgen ist
                const double fadeW = 28;
                if (off > 0.5) dc.DrawRectangle(ed.EdgeFade(true), null, new Rect(content.X, content.Y, fadeW, content.Height));
                if (off < ScrollMax - 0.5) dc.DrawRectangle(ed.EdgeFade(false), null, new Rect(content.Right - fadeW, content.Y, fadeW, content.Height));
                // Leiste: zeigt, welcher Ausschnitt sichtbar ist – lässt sich ziehen
                var track = new Rect(content.X, y + TrackGap, content.Width, TrackH);
                bool hot = ed.HoverDiagram == _el.Hash;
                dc.DrawRoundedRectangle(ed.Brush(t.TextFaint, 0.18), null, track, TrackH / 2, TrackH / 2);
                double thumbW = Math.Max(36, track.Width * _width / _contentW);
                double thumbX = track.X + (ScrollMax > 0 ? off / ScrollMax : 0) * (track.Width - thumbW);
                dc.DrawRoundedRectangle(hot ? ed.Brush(t.Accent, 0.8) : ed.Brush(t.TextDim, 0.45), null, new Rect(thumbX, track.Y, thumbW, TrackH), TrackH / 2, TrackH / 2);
                LocalTrack = new Rect(track.X, track.Y - 6, track.Width, TrackH + 12);
                y = track.Bottom;
            }

            LocalHint = Rect.Empty;
            if (Scaled)
            {
                string msg = $"Verkleinert auf {Math.Round(_scale * 100):0} %"
                             + (_overflow ? " · Umschalt+Mausrad verschiebt" : "") + " · Vergrößern";
                var icon = new FormattedText("", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    Typography.IconFace, 10.5, ed.Brush(t.Accent), ppd);
                var ft = new FormattedText(msg, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    Typography.Face(Typography.Body, FontStyles.Normal, FontWeights.Normal), 11.5, ed.Brush(t.TextFaint), ppd);
                double hy = y + 5;
                dc.DrawText(ft, new Point(content.X + 2, hy));
                dc.DrawText(icon, new Point(content.X + 2 + ft.Width + 5, hy + (ft.Height - icon.Height) / 2 + 1));
                LocalHint = new Rect(content.X, hy - 2, ft.Width + icon.Width + 12, ft.Height + 4);
            }

            // Knopf oben rechts (beim Überfahren, immer bei verkleinerten Diagrammen)
            var btn = new Rect(content.Right - 34, content.Y + 8, 26, 26);
            if (ed.HoverDiagram == _el.Hash || Scaled)
            {
                bool hotBtn = ed.HoverDiagramButton && ed.HoverDiagram == _el.Hash;
                dc.DrawRoundedRectangle(hotBtn ? ed.Brush(t.Accent, 0.14) : ed.Brush(t.Paper), ed.Pen(hotBtn ? t.Accent : t.PaperBorder, 1, false),
                    btn, 7, 7);
                var icon = new FormattedText("", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    Typography.IconFace, 12, ed.Brush(hotBtn ? t.Accent : t.TextDim), ppd);
                dc.DrawText(icon, new Point(btn.X + (btn.Width - icon.Width) / 2, btn.Y + (btn.Height - icon.Height) / 2 + 0.5));
            }

            // Alle Flächen liegen in Koordinaten der Textzeile (origin kommt von AvalonEdit relativ zu ihr)
            LocalBounds = new Rect(origin.X, top, _width, Height);
            LocalContent = content;
            LocalButton = btn;
            Drawn = true;
            ed.NoteDiagramDrawn(_el.Hash, this);
        }

        /// <summary>Wurde dieser Lauf schon gezeichnet (erst dann stimmen die Flächen)?</summary>
        public bool Drawn { get; private set; }
    }
}
