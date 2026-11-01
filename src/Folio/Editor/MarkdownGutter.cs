using System;
using System.Windows;
using System.Windows.Media;
using Folio.Visuals;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace Folio.Editor;

/// <summary>
/// Schmale Randspalte links vom Text: trägt die Zitatbalken und den linken Teil der Codeblock-Flächen.
/// So bleibt Fließtext, Zitat und Code an derselben Kante ausgerichtet, und die Flächen ragen sauber in den Rand.
/// </summary>
public sealed class MarkdownGutter : AbstractMargin
{
    readonly FolioEditor _editor;
    public const double GutterWidth = 22;

    public MarkdownGutter(FolioEditor editor) => _editor = editor;

    protected override Size MeasureOverride(Size availableSize) => new(GutterWidth, 0);

    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView != null)
        {
            oldTextView.VisualLinesChanged -= OnChanged;
            oldTextView.ScrollOffsetChanged -= OnChanged;
        }
        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView != null)
        {
            newTextView.VisualLinesChanged += OnChanged;
            newTextView.ScrollOffsetChanged += OnChanged;
        }
        InvalidateVisual();
    }

    void OnChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        var view = TextView;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight)); // Mausklicks treffen
        if (view == null || !view.VisualLinesValid || !_editor.LiveFormatting) return;
        var t = ThemeManager.Current;
        var a = _editor.Analysis;
        double off = view.VerticalOffset;

        double blockTop = double.NaN, blockBottom = 0;
        foreach (var vl in view.VisualLines)
        {
            int n = vl.FirstDocumentLine.LineNumber;
            var info = a.Line(n);
            double top = vl.VisualTop - off, bottom = top + (info is { IsDiagram: true } && n == info.BlockLast && vl.TextLines.Count > 1 ? vl.TextLines[0].Height : vl.Height); // Vorschau unter dem Zaun gehört nicht zur Codefläche

            if (info is { Kind: MdLineKind.Code or MdLineKind.CodeFence } && !_editor.IsDiagramRendered(info))
            {
                if (double.IsNaN(blockTop)) blockTop = top;
                blockBottom = bottom;
                if (info.BlockLast == n) Flush();
            }
            else if (!double.IsNaN(blockTop)) Flush();

            if (info is { QuoteDepth: > 0 })
            {
                for (int d = 0; d < info.QuoteDepth; d++)
                {
                    var bar = new Rect(GutterWidth - 9 - d * 8, top, 3, vl.Height);
                    dc.DrawRoundedRectangle(_editor.Brush(t.QuoteBar), null, bar, 1.5, 1.5);
                }
            }
        }
        if (!double.IsNaN(blockTop)) Flush();

        void Flush()
        {
            // linker Teil der Codefläche: abgerundet, läuft rechts nahtlos in den Text hinein
            var r = new Rect(GutterWidth - 12, blockTop + 2, 40, Math.Max(0, blockBottom - blockTop - 4));
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, GutterWidth, ActualHeight)));
            dc.DrawRoundedRectangle(_editor.Brush(t.CodeBack), null, r, 8, 8);
            dc.Pop();
            blockTop = double.NaN;
        }
    }
}
