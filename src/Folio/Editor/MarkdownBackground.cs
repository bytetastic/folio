using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Folio.Visuals;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace Folio.Editor;

/// <summary>Hintergründe unter dem Text: Codeblöcke, Inline-Code, Zitatbalken, Trennlinien und der Platzhalter einer leeren Notiz.</summary>
public sealed class MarkdownBackground : IBackgroundRenderer
{
    readonly FolioEditor _editor;

    public MarkdownBackground(FolioEditor editor) => _editor = editor;

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext dc)
    {
        if (!textView.VisualLinesValid) return;
        var t = ThemeManager.Current;
        var lines = textView.VisualLines;
        if (lines.Count == 0) return;

        if (textView.Document is { TextLength: 0 } && _editor.Placeholder is { Length: > 0 } ph)
            DrawPlaceholder(textView, dc, ph, t);

        if (!_editor.LiveFormatting) return;
        var a = _editor.Analysis;
        double width = textView.ActualWidth;
        double off = textView.VerticalOffset;

        // Codeblöcke: zusammenhängende Zeilen als eine abgerundete Fläche
        double blockTop = double.NaN, blockBottom = 0;
        foreach (var vl in lines)
        {
            int n = vl.FirstDocumentLine.LineNumber;
            var info = a.Line(n);
            double top = vl.VisualTop - off, bottom = top + (info is { IsDiagram: true } && n == info.BlockLast && vl.TextLines.Count > 1 ? vl.TextLines[0].Height : vl.Height); // Vorschau unter dem Zaun gehört nicht zur Codefläche
            bool code = info is { Kind: MdLineKind.Code or MdLineKind.CodeFence } && !_editor.IsDiagramRendered(info); // Diagramme haben ihren eigenen Rahmen
            if (code)
            {
                if (double.IsNaN(blockTop)) blockTop = top;
                blockBottom = bottom;
                if (info!.BlockLast == n) FlushBlock();
            }
            else if (!double.IsNaN(blockTop)) FlushBlock();
        }
        if (!double.IsNaN(blockTop)) FlushBlock();

        void FlushBlock()
        {
            // links beginnt die Fläche in der Randspalte (MarkdownGutter) – hier nur rechts abgerundet
            var r = new Rect(-20, blockTop + 2, width + 18, Math.Max(0, blockBottom - blockTop - 4));
            dc.DrawRoundedRectangle(_editor.Brush(t.CodeBack), null, r, 8, 8);
            blockTop = double.NaN;
        }

        foreach (var vl in lines)
        {
            var dl = vl.FirstDocumentLine;
            int n = dl.LineNumber;
            var info = a.Line(n);
            double top = vl.VisualTop - off, bottom = top + (info is { IsDiagram: true } && n == info.BlockLast && vl.TextLines.Count > 1 ? vl.TextLines[0].Height : vl.Height); // Vorschau unter dem Zaun gehört nicht zur Codefläche

            if (info is { Kind: MdLineKind.Rule } && !_editor.IsRevealed(n))
            {
                double y = Math.Round(top + vl.Height / 2) + 0.5;
                dc.DrawLine(_editor.Pen(t.Rule, 1), new Point(0, y), new Point(width, y));
            }
            if (info is { Kind: MdLineKind.Code or MdLineKind.CodeFence }) continue;

            // Inline-Code: weiche Kapseln hinter dem Text
            foreach (var s in a.SpansIn(dl.Offset, dl.EndOffset))
            {
                if ((s.Style & MdStyle.Code) == 0) continue;
                var seg = new TextSegment { StartOffset = Math.Max(s.Start, dl.Offset), EndOffset = Math.Min(s.End, dl.EndOffset) };
                foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(textView, seg))
                {
                    var st = _editor.StyleOfLine(n);
                    // nur die Texthöhe, nicht der Zeilenabstand
                    double h = st.FontSize * 1.3;
                    double y = r.Bottom - st.PadBelow - h + st.FontSize * 0.08;
                    var box = new Rect(r.X - 3, y, r.Width + 6, h);
                    dc.DrawRoundedRectangle(_editor.Brush(t.CodeBack), null, box, 4, 4);
                }
            }
        }
    }

    void DrawPlaceholder(TextView textView, DrawingContext dc, string text, FolioTheme t)
    {
        var vl = textView.VisualLines[0];
        var tl = vl.TextLines[0];
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            Typography.Face(_editor.IsMarkdown ? Typography.Body : Typography.Mono, FontStyles.Normal, FontWeights.Normal),
            _editor.FontSize, _editor.Brush(t.TextFaint), VisualTreeHelper.GetDpi(textView).PixelsPerDip);
        double baseline = vl.VisualTop - textView.VerticalOffset + tl.Baseline;
        dc.DrawText(ft, new Point(2, baseline - ft.Baseline));
    }
}
