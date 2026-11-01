using System.Windows;
using System.Windows.Media;
using Folio.Visuals;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace Folio.Editor;

/// <summary>Gibt Markdown seine Gestalt: Überschriften groß, **fett** fett, `code` in Monospace, Links in Tinte …
/// Im Quelltextmodus nur Farben und Gewichte, keine Größen – alle Zeichen bleiben sichtbar.</summary>
public sealed class MarkdownColorizer : DocumentColorizingTransformer
{
    readonly FolioEditor _editor;

    public MarkdownColorizer(FolioEditor editor) => _editor = editor;

    protected override void ColorizeLine(DocumentLine line)
    {
        if (!_editor.IsMarkdown) return;
        var t = ThemeManager.Current;
        var a = _editor.Analysis;
        bool live = _editor.LiveFormatting;
        int n = line.LineNumber;
        int ls = line.Offset, le = line.EndOffset;
        if (le <= ls) return;
        var st = _editor.StyleOfLine(n);
        var info = a.Line(n);

        // ── ganze Zeile ──
        if (info != null)
        {
            switch (info.Kind)
            {
                case MdLineKind.Heading:
                    ChangeLinePart(ls, le, el =>
                    {
                        if (live) el.TextRunProperties.SetFontRenderingEmSize(st.FontSize);
                        el.TextRunProperties.SetTypeface(Typography.Face(live ? Typography.Display : el.TextRunProperties.Typeface.FontFamily, FontStyles.Normal, FontWeights.SemiBold));
                        el.TextRunProperties.SetForegroundBrush(_editor.Brush(live ? t.Heading : t.Accent));
                    });
                    break;
                case MdLineKind.Code:
                case MdLineKind.CodeFence:
                case MdLineKind.Table:
                    ChangeLinePart(ls, le, el =>
                    {
                        el.TextRunProperties.SetTypeface(Typography.Face(Typography.Mono, FontStyles.Normal, FontWeights.Normal));
                        if (live) el.TextRunProperties.SetFontRenderingEmSize(st.FontSize);
                        el.TextRunProperties.SetForegroundBrush(_editor.Brush(info.Kind == MdLineKind.CodeFence ? t.TextFaint : info.Kind == MdLineKind.Table ? t.Text : t.CodeText));
                    });
                    break;
                case MdLineKind.Quote:
                    ChangeLinePart(ls, le, el => el.TextRunProperties.SetForegroundBrush(_editor.Brush(t.TextDim)));
                    break;
                case MdLineKind.Html:
                    ChangeLinePart(ls, le, el => el.TextRunProperties.SetForegroundBrush(_editor.Brush(t.TextFaint)));
                    break;
            }
        }

        // ── Bereiche innerhalb der Zeile ──
        bool inCode = info is { Kind: MdLineKind.Code or MdLineKind.CodeFence };
        double markerAlpha = _editor.RevealAlpha(n);
        foreach (var s in a.SpansIn(ls, le))
        {
            int start = System.Math.Max(s.Start, ls), end = System.Math.Min(s.End, le);
            if (end <= start || inCode) continue;
            var style = s.Style;
            ChangeLinePart(start, end, el =>
            {
                var p = el.TextRunProperties;
                if ((style & MdStyle.Marker) != 0)
                {
                    var c = t.TextFaint;
                    if (markerAlpha < 1) c = ThemeManager.WithAlpha(c, (byte)(c.A * markerAlpha));
                    p.SetForegroundBrush(markerAlpha < 1 ? new SolidColorBrush(c) : _editor.Brush(c));
                    return;
                }
                var face = p.Typeface;
                var family = face.FontFamily;
                var fStyle = face.Style;
                var weight = face.Weight;
                if ((style & MdStyle.Bold) != 0) weight = FontWeights.Bold;
                if ((style & MdStyle.Italic) != 0) fStyle = FontStyles.Italic;
                if ((style & MdStyle.Code) != 0)
                {
                    family = Typography.Mono;
                    if (live) p.SetFontRenderingEmSize(p.FontRenderingEmSize * Typography.CodeScale);
                    p.SetForegroundBrush(_editor.Brush(t.CodeText));
                }
                p.SetTypeface(Typography.Face(family, fStyle, weight));
                if ((style & MdStyle.Link) != 0)
                {
                    p.SetForegroundBrush(_editor.Brush(t.Accent));
                    p.SetTextDecorations(_editor.LinkUnderline);
                }
                if ((style & MdStyle.Strike) != 0) p.SetTextDecorations(TextDecorations.Strikethrough);
                if ((style & MdStyle.Muted) != 0) p.SetForegroundBrush(_editor.Brush(t.TextFaint));
                if ((style & MdStyle.Done) != 0)
                {
                    p.SetForegroundBrush(_editor.Brush(t.TextFaint));
                    p.SetTextDecorations(_editor.DoneStrike);
                }
            });
        }
    }
}
