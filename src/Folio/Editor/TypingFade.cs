using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Folio.Visuals;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace Folio.Editor;

/// <summary>
/// Frisch getippte Zeichen blenden weich ein: Eine Ebene über dem Text legt einen Schleier in Papierfarbe über die
/// neuen Zeichen, der in kurzer Zeit verschwindet. Pro Bild wird nur diese Ebene neu gezeichnet – die Textzeilen
/// selbst bleiben unangetastet (ein Neuaufbau der Zeile in jedem Bild kostete ein Vielfaches).
/// Die Bereiche wandern mit dem Text mit (TextSegmentCollection).
/// </summary>
public sealed class TypingFade : UIElement
{
    sealed class FadeSegment : TextSegment
    {
        public double Born;
    }

    readonly FolioEditor _editor;
    readonly TextView _view;
    TextSegmentCollection<FadeSegment>? _segments;
    readonly List<FadeSegment> _active = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    bool _subscribed;

    public const double Duration = 0.14;

    public TypingFade(FolioEditor editor, TextView view)
    {
        _editor = editor;
        _view = view;
        IsHitTestVisible = false;
    }

    double Now => _clock.Elapsed.TotalSeconds;

    /// <summary>Neues Dokument (Tabwechsel): alte Bereiche verwerfen.</summary>
    public void Attach(TextDocument? doc)
    {
        _active.Clear();
        _segments = doc != null ? new TextSegmentCollection<FadeSegment>(doc) : null;
        Stop();
        InvalidateVisual();
    }

    public void Add(int offset, int length)
    {
        if (_segments == null || length <= 0) return;
        var s = new FadeSegment { StartOffset = offset, Length = length, Born = Now };
        _segments.Add(s);
        _active.Add(s);
        if (!_subscribed)
        {
            _subscribed = true;
            AnimationTicker.SubscribeFrame(OnFrame);
        }
    }

    void Stop()
    {
        if (!_subscribed) return;
        _subscribed = false;
        AnimationTicker.UnsubscribeFrame(OnFrame);
    }

    void OnFrame(double dt)
    {
        double now = Now;
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var s = _active[i];
            if (s.Length == 0 || now - s.Born >= Duration)
            {
                _segments?.Remove(s);
                _active.RemoveAt(i);
            }
        }
        if (_active.Count == 0) Stop();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_active.Count == 0 || !_view.VisualLinesValid || _view.Document == null) return;
        var t = ThemeManager.Current;
        var a = _editor.Analysis;
        double now = Now;
        foreach (var s in _active)
        {
            double x = Math.Clamp((now - s.Born) / Duration, 0, 1);
            double veil = Math.Pow(1 - x, 2) * 0.88; // Deckkraft des Schleiers: fällt schnell, läuft weich aus
            if (veil < 0.01 || s.Length == 0) continue;
            var line = _view.Document.GetLineByOffset(Math.Min(s.StartOffset, _view.Document.TextLength));
            var info = a.Line(line.LineNumber);
            bool code = info is { Kind: MdLineKind.Code or MdLineKind.CodeFence }
                        || HasStyle(a, s.StartOffset, MdStyle.Code);
            var color = ThemeManager.WithAlpha(code && _editor.LiveFormatting ? t.CodeBack : t.Paper, (byte)(255 * veil));
            var brush = new SolidColorBrush(color);
            var st = _editor.StyleOfLine(line.LineNumber);
            foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(_view, s))
            {
                // nur die Texthöhe abdecken (nicht den Zeilenabstand), etwas Luft für Unterlängen und Akzente
                double h = st.Ascent + st.Descent;
                var rect = new Rect(r.X - 0.5, r.Bottom - st.PadBelow - h - 1, r.Width + 1, h + 2);
                dc.DrawRectangle(brush, null, rect);
            }
        }
    }

    static bool HasStyle(MarkdownAnalysis a, int offset, MdStyle style)
    {
        foreach (var s in a.SpansIn(offset, offset + 1))
            if ((s.Style & style) != 0) return true;
        return false;
    }
}
