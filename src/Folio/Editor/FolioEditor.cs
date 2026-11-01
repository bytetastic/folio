using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Folio.Visuals;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;

namespace Folio.Editor;

public enum EditorMode { Live, Source, Read }

/// <summary>Maße einer Zeile: Schriftgröße, Ober-/Unterlänge der Schrift und Abstand darüber/darunter.</summary>
public readonly record struct LineStyle(double FontSize, double Ascent, double Descent, double PadAbove, double PadBelow);

/// <summary>
/// Der Folio-Editor: AvalonEdit mit Formatierung beim Tippen (Markdown), eigenem gleitendem Cursor,
/// weich einblendenden Zeichen und weichem Scrollen.
/// </summary>
public sealed class FolioEditor : TextEditor
{
    /// <summary>Ab dieser Länge wird nicht mehr live formatiert (jeder Tastendruck analysiert das ganze Dokument).</summary>
    const int LiveLimit = 400_000;

    readonly MarkdownColorizer _colorizer;
    readonly MarkdownElementGenerator _generator;
    readonly MarkdownBackground _background;
    readonly TypingFade _fade;
    readonly SmoothCaret _caret;
    readonly MarkdownGutter _gutter;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly Dictionary<int, double> _checkAnim = new();
    readonly Dictionary<(Color, double, bool), Pen> _pens = new();
    readonly Dictionary<Color, SolidColorBrush> _brushes = new();

    TextDocument? _doc;
    bool _typing;
    int _revealFirst = -1, _revealLast = -1;
    double _revealTime = -10;
    bool _tickRunning;
    ScrollViewer? _scroll;
    double _scrollTarget;
    bool _scrolling;
    double _lastSetOffset = double.NaN;

    public MarkdownAnalysis Analysis { get; private set; } = MarkdownAnalysis.Empty;
    public bool IsMarkdown { get; private set; }
    public EditorMode Mode { get; private set; } = EditorMode.Live;
    public bool LiveFormatting => IsMarkdown && Mode != EditorMode.Source && (_doc?.TextLength ?? 0) <= LiveLimit;
    /// <summary>Grauer Hinweistext in einer leeren Notiz.</summary>
    public string? Placeholder { get; set; }

    public TextDecorationCollection LinkUnderline { get; private set; } = new();
    public TextDecorationCollection DoneStrike { get; private set; } = new();

    public event Action? CaretMoved;
    public event Action<string>? LinkActivated;
    public event Action<int>? ZoomRequested;

    /// <summary>Messschalter (FOLIO_PERF=nofade,nocaret,…) – schalten Teile ab, um ihre Kosten zu sehen.</summary>
    internal static bool Perf(string flag) => PerfFlags.Contains(flag);
    static readonly string PerfFlags = Environment.GetEnvironmentVariable("FOLIO_PERF") ?? "";

    /// <summary>Nur für Messungen (FOLIO_PERF=timing): Zeit in der Markdown-Analyse.</summary>
    public static double AnalysisMs;

    double Now => _clock.Elapsed.TotalSeconds;

    public FolioEditor()
    {
        Options.EnableHyperlinks = false;
        Options.EnableEmailHyperlinks = false;
        Options.HighlightCurrentLine = false;
        Options.AllowScrollBelowDocument = true;
        Options.EnableRectangularSelection = true;
        Options.CutCopyWholeLine = true;
        Options.IndentationSize = 4;
        Options.HideCursorWhileTyping = false;
        ShowLineNumbers = false;
        WordWrap = true;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Background = Brushes.Transparent;
        FontFamily = Typography.Body;
        FontSize = 16;

        var area = TextArea;
        area.SelectionCornerRadius = 4;
        area.SelectionBorder = null;
        area.SelectionForeground = null;
        area.Caret.CaretBrush = Brushes.Transparent; // eigener Cursor (SmoothCaret)
        area.FocusVisualStyle = null;

        _generator = new MarkdownElementGenerator(this);
        _colorizer = new MarkdownColorizer(this);
        _background = new MarkdownBackground(this);
        var view = area.TextView;
        _fade = new TypingFade(this, view);
        view.ElementGenerators.Add(_generator);
        view.LineTransformers.Add(_colorizer);
        view.InsertLayer(_fade, KnownLayer.Text, LayerInsertionPosition.Above); // Schleier über frisch getippten Zeichen
        view.BackgroundRenderers.Add(_background);
        _gutter = new MarkdownGutter(this);
        _caret = new SmoothCaret(this, view);
        view.InsertLayer(_caret, KnownLayer.Caret, LayerInsertionPosition.Above);

        area.Caret.PositionChanged += (_, _) => OnCaretChanged();
        area.SelectionChanged += (_, _) => UpdateReveal();
        area.TextEntering += (_, _) => _typing = true;
        area.TextEntered += (_, _) => _typing = false;
        area.PreviewMouseLeftButtonDown += OnAreaMouseDown;
        area.MouseMove += OnAreaMouseMove;
        area.PreviewMouseMove += OnAreaPreviewMouseMove;
        area.PreviewMouseLeftButtonUp += OnAreaPreviewMouseUp;
        area.MouseLeave += (_, _) => { if (_dragDiagram == null) SetHover(null, false); };
        area.PreviewKeyDown += OnAreaKeyDown;
        IsKeyboardFocusWithinChanged += (_, _) => UpdateCaretVisibility();
        DocumentChanged += (_, _) => AttachDocument(Document);
        PreviewMouseWheel += OnWheel;
        ThemeManager.Changed += ApplyTheme;
        ApplyTheme();
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (_scroll != null) _scroll.ScrollChanged -= OnScrollChanged;
        _scroll = Template.FindName("PART_ScrollViewer", this) as ScrollViewer;
        if (_scroll != null) _scroll.ScrollChanged += OnScrollChanged;
    }

    // ───────────────────────────── Dokument und Modus ─────────────────────────────

    /// <summary>Dokument anzeigen. <paramref name="extension"/> bestimmt Markdown oder Syntaxfarben (z. B. ".md", ".json").</summary>
    public void Show(TextDocument doc, string? extension, EditorMode mode)
    {
        string ext = (extension ?? ".md").ToLowerInvariant();
        IsMarkdown = ext is ".md" or ".markdown" or ".mdown" or ".mkd" or ".mdx";
        Mode = IsMarkdown ? mode : mode == EditorMode.Read ? EditorMode.Read : EditorMode.Source;
        SyntaxHighlighting = IsMarkdown ? null : Highlighting.For(ext);
        _revealFirst = _revealLast = -1;
        _checkAnim.Clear();
        Document = doc; // löst AttachDocument aus
        ApplyModeLayout();
        UpdateReveal();
    }

    public void SetMode(EditorMode mode)
    {
        if (!IsMarkdown && mode == EditorMode.Live) mode = EditorMode.Source;
        if (Mode == mode) return;
        Mode = mode;
        _revealFirst = _revealLast = -1;
        ApplyModeLayout();
        UpdateReveal();
        SyncDiagramCollapses();
        TextArea.TextView.Redraw();
    }

    void ApplyModeLayout()
    {
        IsReadOnly = Mode == EditorMode.Read;
        FontFamily = IsMarkdown && Mode != EditorMode.Source ? Typography.Body : Typography.Mono;
        ShowLineNumbers = !IsMarkdown;
        if (ShowLineNumbers) StyleLineNumbers();
        bool gutter = LiveFormatting;
        if (gutter && !TextArea.LeftMargins.Contains(_gutter)) TextArea.LeftMargins.Insert(0, _gutter);
        else if (!gutter) TextArea.LeftMargins.Remove(_gutter);
        WordWrap = IsMarkdown || WrapCode;
        UpdateCaretVisibility();
    }

    /// <summary>Gleitender Cursor und weich einblendende Zeichen (abschaltbar im Menü).</summary>
    public bool SmoothTyping
    {
        get => !_caret.Snap;
        set => _caret.Snap = !value;
    }

    /// <summary>Zeilenumbruch für Nicht-Markdown-Dateien (Markdown bricht immer um).</summary>
    public bool WrapCode { get; set; } = true;

    void StyleLineNumbers()
    {
        LineNumbersForeground = Brush(ThemeManager.Current.TextFaint);
        // Die gepunktete Trennlinie von AvalonEdit unsichtbar machen, nur Abstand behalten
        foreach (var m in TextArea.LeftMargins)
            if (m is System.Windows.Shapes.Line line)
            {
                line.Stroke = Brushes.Transparent;
                line.Margin = new Thickness(6, 0, 8, 0);
            }
    }

    void AttachDocument(TextDocument? doc)
    {
        _collapsed.Clear(); // Einklappungen gehören zum alten Dokument (AvalonEdit verwirft sie mit ihm)
        if (_doc != null) _doc.Changed -= OnDocChanged;
        _doc = doc;
        if (_doc != null) _doc.Changed += OnDocChanged;
        _fade.Attach(doc);
        Reanalyze();
        SyncDiagramCollapses();
    }

    void Reanalyze()
    {
        Analysis = IsMarkdown && _doc != null && _doc.TextLength <= LiveLimit
            ? MarkdownAnalysis.Analyze(_doc.Text)
            : MarkdownAnalysis.Empty;
    }

    void OnDocChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (IsMarkdown)
        {
            long t0 = Stopwatch.GetTimestamp();
            var old = Analysis;
            Reanalyze();
            SyncDiagramCollapses();
            RedrawChangedLines(old, Analysis);
            AnalysisMs += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
        if (_typing && SmoothTyping && !Perf("nofade") && e.InsertionLength is > 0 and <= 64) _fade.Add(e.Offset, e.InsertionLength);
    }

    /// <summary>
    /// Nach einer Änderung nur die sichtbaren Zeilen neu aufbauen, deren Formatierung sich geändert hat
    /// (die bearbeitete Zeile selbst baut AvalonEdit ohnehin neu). Ändert sich die Zeilenzahl, alles.
    /// </summary>
    void RedrawChangedLines(MarkdownAnalysis old, MarkdownAnalysis now)
    {
        var view = TextArea.TextView;
        if (old.LineCount != now.LineCount || old == MarkdownAnalysis.Empty || now == MarkdownAnalysis.Empty || !view.VisualLinesValid)
        {
            view.Redraw();
            return;
        }
        foreach (var vl in view.VisualLines)
        {
            int n = vl.FirstDocumentLine.LineNumber;
            if (old.LineSignature(n) != now.LineSignature(n)) view.Redraw(vl);
        }
    }

    // ───────────────────────────── Zeilenmaße ─────────────────────────────

    public LineStyle StyleOfLine(int lineNumber)
    {
        double fs = FontSize;
        if (!LiveFormatting)
        {
            var (a0, d0) = Typography.Metrics(FontFamily, fs);
            return new LineStyle(fs, a0, d0, 0, 0);
        }
        var info = Analysis.Line(lineNumber);
        if (info is { Kind: MdLineKind.Heading })
        {
            double size = fs * Typography.HeadingScale(info.Level);
            var (a, d) = Typography.Metrics(Typography.Display, size);
            double above = fs * (info.Level <= 2 ? 0.95 : 0.6);
            if (lineNumber == 1) above = fs * 0.2;
            return new LineStyle(size, a, d, above, fs * 0.3);
        }
        if (info is { Kind: MdLineKind.Code or MdLineKind.CodeFence or MdLineKind.Table })
        {
            double size = fs * Typography.CodeScale;
            var (a, d) = Typography.Metrics(Typography.Mono, size);
            double pad = Math.Max(0, (fs * 1.6 - a - d) / 2);
            return new LineStyle(size, a, d, pad, pad);
        }
        {
            var (a, d) = Typography.Metrics(Typography.Body, fs);
            double pad = Math.Max(0, (fs * 1.72 - a - d) / 2);
            return new LineStyle(fs, a, d, pad, pad);
        }
    }

    // ───────────────────────────── Aufdecken der Steuerzeichen ─────────────────────────────

    /// <summary>Zeigt diese Zeile ihre Markdown-Zeichen? (Cursorzeile, markierte Zeilen, der Codeblock unter dem Cursor)</summary>
    public bool IsRevealed(int lineNumber) => Mode switch
    {
        EditorMode.Source => true,
        EditorMode.Read => false,
        _ => lineNumber >= _revealFirst && lineNumber <= _revealLast,
    };

    /// <summary>Deckkraft frisch aufgedeckter Steuerzeichen (blenden kurz ein).</summary>
    public double RevealAlpha(int lineNumber)
    {
        if (Mode != EditorMode.Live || !IsRevealed(lineNumber)) return 1;
        double x = (Now - _revealTime) / 0.18;
        return x >= 1 ? 1 : 0.15 + 0.85 * Math.Clamp(x, 0, 1);
    }

    public int CaretOffsetForWidgets => IsKeyboardFocusWithin ? CaretOffset : -1;

    void OnCaretChanged()
    {
        UpdateReveal();
        // Punkt/Checkbox der Cursorzeile neu zeichnen (Text erscheint, wenn der Cursor direkt daran steht)
        if (IsMarkdown && Document != null)
        {
            var line = Document.GetLineByOffset(CaretOffset);
            if (Analysis.HasWidgetIn(line.Offset, line.EndOffset)) TextArea.TextView.Redraw(line);
        }
        _caret.RetargetAfterLayout();
        KeepCaretAwayFromEdges();
        CaretMoved?.Invoke();
    }

    void UpdateReveal()
    {
        if (Document == null || Mode != EditorMode.Live || !IsMarkdown) return;
        var sel = TextArea.Selection;
        int first, last;
        if (!sel.IsEmpty)
        {
            first = sel.StartPosition.Line;
            last = sel.EndPosition.Line;
            if (first > last) (first, last) = (last, first);
        }
        else first = last = TextArea.Caret.Line;
        var info = Analysis.Line(TextArea.Caret.Line);
        if (info is { BlockFirst: > 0 })
        {
            first = Math.Min(first, info.BlockFirst);
            last = Math.Max(last, info.BlockLast);
        }
        if (first == _revealFirst && last == _revealLast) return;
        int oldFirst = _revealFirst, oldLast = _revealLast;
        _revealFirst = first;
        _revealLast = last;
        _revealTime = Now;
        SyncDiagramCollapses();
        RedrawLines(oldFirst, oldLast);
        RedrawLines(first, last);
        StartTick();
    }

    void RedrawLines(int first, int last)
    {
        if (first < 1 || Document == null) return;
        last = Math.Min(last, Document.LineCount);
        for (int n = first; n <= last; n++) TextArea.TextView.Redraw(Document.GetLineByNumber(n));
    }

    // ───────────────────────────── Diagramme ─────────────────────────────

    readonly Dictionary<(string Source, int Width, string Theme, double Size), Diagrams.DiagramPicture> _diagramCache = new();
    readonly Dictionary<int, double> _diagramShown = new();

    /// <summary>Gezeichnetes Diagramm aus dem Zwischenspeicher (Schlüssel: Quelltext, Breite in 24-px-Stufen, Thema, Schriftgröße).</summary>
    public Diagrams.DiagramPicture GetDiagram(string source, double maxWidth)
    {
        var key = (source, (int)(maxWidth / 24), ThemeManager.Current.Id, FontSize);
        if (_diagramCache.TryGetValue(key, out var pic)) return pic;
        if (_diagramCache.Count > 48) _diagramCache.Clear();
        var diagram = Diagrams.MermaidParser.Parse(source);
        var renderer = new Diagrams.DiagramRenderer(ThemeManager.Current, VisualTreeHelper.GetDpi(this).PixelsPerDip, FontSize);
        try { pic = renderer.Render(diagram); }
        catch (Exception ex)
        {
            Services.AppPaths.Log("Diagramm: " + ex);
            pic = renderer.Render(new Diagrams.DiagramError("Beim Zeichnen ist ein Fehler aufgetreten: " + ex.Message));
        }
        _diagramCache[key] = pic;
        return pic;
    }

    /// <summary>Sekunden, seit dieses Diagramm (Inhalt) zum ersten Mal gezeigt wurde – für das gestaffelte Einblenden.</summary>
    public double DiagramAge(int hash)
    {
        double now = Now;
        if (!_diagramShown.TryGetValue(hash, out var t0))
        {
            _diagramShown[hash] = t0 = now;
            StartTick();
        }
        return now - t0;
    }

    /// <summary>Wird der Block dieser Zeile gerade als Bild gezeigt (statt als Quelltext)?</summary>
    public bool IsDiagramRendered(MdLine? info) =>
        info is { IsDiagram: true, Closed: true } && LiveFormatting && !IsRevealed(info.BlockFirst) && info.BlockLast > info.BlockFirst;

    readonly List<(DocumentLine First, int LastNumber, CollapsedLineSection Section)> _collapsed = new();

    /// <summary>
    /// Ein gezeichnetes Diagramm verdeckt die Zeilen seines Blocks. AvalonEdit verlangt, dass diese Zeilen offiziell
    /// eingeklappt sind – hier wird der Soll-Zustand (welche Blöcke gerade als Bild erscheinen) hergestellt.
    /// </summary>
    void SyncDiagramCollapses()
    {
        var doc = Document;
        var want = new Dictionary<int, int>(); // erste Zeile → letzte Zeile
        if (doc != null)
            foreach (var b in Analysis.DiagramBlocks)
                if (IsDiagramRendered(b) && b.BlockLast <= doc.LineCount) want[b.BlockFirst] = b.BlockLast;

        for (int i = _collapsed.Count - 1; i >= 0; i--)
        {
            var (first, lastNo, sec) = _collapsed[i];
            bool keep = doc != null && !first.IsDeleted && sec.IsCollapsed
                        && want.TryGetValue(first.LineNumber, out var wantLast) && wantLast == lastNo
                        && sec.Start == first.NextLine && sec.End?.LineNumber == lastNo;
            if (keep) { want.Remove(first.LineNumber); continue; }
            try { sec.Uncollapse(); } catch (Exception) { }
            _collapsed.RemoveAt(i);
        }
        if (doc == null) return;
        foreach (var (firstNo, lastNo) in want)
        {
            var first = doc.GetLineByNumber(firstNo);
            if (first.NextLine == null) continue;
            var sec = TextArea.TextView.CollapseLines(first.NextLine, doc.GetLineByNumber(lastNo));
            _collapsed.Add((first, lastNo, sec));
        }
    }

    /// <summary>Diagramme, deren Einblenden noch läuft, neu zeichnen.</summary>
    bool AnimateDiagrams()
    {
        if (_diagramShown.Count == 0 || Document == null) return false;
        double now = Now;
        bool busy = false;
        var view = TextArea.TextView;
        if (!view.VisualLinesValid) return true;
        foreach (var vl in view.VisualLines)
        {
            var info = Analysis.Line(vl.FirstDocumentLine.LineNumber);
            if (!IsDiagramRendered(info) || !_diagramShown.TryGetValue(info!.BlockHash, out var t0)) continue;
            if (now - t0 < 1.4) { view.Redraw(vl); busy = true; }
        }
        return busy;
    }

    // ───────────────────────────── Diagramme: Verschieben, Knopf, Viewer ─────────────────────────────

    readonly Dictionary<int, double> _diagramScroll = new();
    readonly Dictionary<int, DiagramElement.DiagramRun> _lastDrawn = new();

    /// <summary>Vom Diagramm beim Zeichnen gerufen: seine Flächen für Mausklicks merken.</summary>
    internal void NoteDiagramDrawn(int hash, DiagramElement.DiagramRun run)
    {
        if (_lastDrawn.Count > 64) _lastDrawn.Clear();
        _lastDrawn[hash] = run;
    }
    int? _dragDiagram;
    double _dragStartX, _dragStartOff;

    /// <summary>Viewer für dieses Diagramm öffnen (Quelltext des Blocks).</summary>
    public event Action<string>? DiagramOpenRequested;

    /// <summary>Diagramm unter der Maus (Fingerabdruck) – zeigt Knopf und hervorgehobene Leiste.</summary>
    public int? HoverDiagram { get; private set; }
    public bool HoverDiagramButton { get; private set; }

    /// <summary>Waagerechte Verschiebung eines zu breiten Diagramms (in Bildschirmpixeln).</summary>
    public double DiagramScroll(int hash) => _diagramScroll.TryGetValue(hash, out var v) ? v : 0;

    readonly record struct DiagramHit(DiagramElement Element, DiagramElement.DiagramRun Run, Point Local, VisualLine Line);

    /// <summary>Welches gezeichnete Diagramm liegt unter dem Punkt (in TextView-Koordinaten)?</summary>
    DiagramHit? DiagramAt(Point pInView)
    {
        var view = TextArea.TextView;
        if (!view.VisualLinesValid) view.EnsureVisualLines();
        var vl = view.GetVisualLineFromVisualTop(pInView.Y + view.VerticalOffset);
        if (vl == null) return null;
        foreach (var el in vl.Elements)
        {
            if (el is not DiagramElement de) continue;
            // Ein frisch aufgebautes Element ist evtl. noch nicht gezeichnet → Flächen vom letzten Zeichnen nehmen
            var run = de.Run is { Drawn: true } r ? r : _lastDrawn.GetValueOrDefault(de.Hash);
            if (run == null) continue;
            var tl = vl.GetTextLine(de.VisualColumn);
            double lineTop = vl.GetTextLineVisualYPosition(tl, VisualYPosition.LineTop) - view.VerticalOffset;
            var local = new Point(pInView.X + view.HorizontalOffset, pInView.Y - lineTop);
            if (run.LocalBounds.Contains(local)) return new DiagramHit(de, run, local, vl);
        }
        return null;
    }

    void RedrawDiagram(int hash)
    {
        var view = TextArea.TextView;
        if (!view.VisualLinesValid) { view.Redraw(); return; }
        foreach (var vl in view.VisualLines)
            foreach (var el in vl.Elements)
                if (el is DiagramElement de && de.Hash == hash) { view.Redraw(vl); break; }
    }

    void SetHover(int? hash, bool onButton)
    {
        if (hash == HoverDiagram && onButton == HoverDiagramButton) return;
        var old = HoverDiagram;
        HoverDiagram = hash;
        HoverDiagramButton = onButton;
        if (old is { } o) RedrawDiagram(o);
        if (hash is { } h && h != old) RedrawDiagram(h);
        else if (hash is { } h2) RedrawDiagram(h2);
    }

    void ScrollDiagram(DiagramHit hit, double offset)
    {
        double v = Math.Clamp(offset, 0, hit.Run.ScrollMax);
        if (Math.Abs(v - DiagramScroll(hit.Element.Hash)) < 0.1) return;
        _diagramScroll[hit.Element.Hash] = v;
        RedrawDiagram(hit.Element.Hash);
    }

    /// <summary>Maus gedrückt über einem Diagramm: Knopf/Hinweis öffnet den Viewer, die Leiste lässt sich ziehen.
    /// Ein Klick ins Bild selbst bleibt wie bisher (Cursor in den Block → Quelltext bearbeiten).</summary>
    bool DiagramMouseDown(Point p)
    {
        if (DiagramAt(p) is not { } hit) return false;
        var r = hit.Run;
        if (r.LocalButton.Contains(hit.Local) || (!r.LocalHint.IsEmpty && r.LocalHint.Contains(hit.Local)))
        {
            DiagramOpenRequested?.Invoke(hit.Element.Source);
            return true;
        }
        if (r.Overflow && !r.LocalTrack.IsEmpty && r.LocalTrack.Contains(hit.Local))
        {
            // Klick neben den Griff springt dorthin, dann ziehen
            double frac = Math.Clamp((hit.Local.X - r.LocalTrack.X) / Math.Max(1, r.LocalTrack.Width), 0, 1);
            double thumbFrac = r.VisibleWidth / Math.Max(1, r.ContentWidth);
            double cur = DiagramScroll(hit.Element.Hash) / Math.Max(1, r.ScrollMax);
            double thumbStart = cur * (1 - thumbFrac), thumbEnd = thumbStart + thumbFrac;
            if (frac < thumbStart || frac > thumbEnd)
                ScrollDiagram(hit, (frac - thumbFrac / 2) / Math.Max(0.0001, 1 - thumbFrac) * r.ScrollMax);
            _dragDiagram = hit.Element.Hash;
            _dragStartX = p.X;
            _dragStartOff = DiagramScroll(hit.Element.Hash);
            TextArea.CaptureMouse();
            return true;
        }
        return false;
    }

    void OnAreaPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragDiagram is not { } hash) return;
        var p = e.GetPosition(TextArea.TextView);
        var hit = FindDiagramRun(hash);
        if (hit != null)
        {
            var r = hit.Value.Run;
            // Griff folgt der Maus: Weg auf der Leiste → Weg im Diagramm
            double ratio = r.ContentWidth / Math.Max(1, r.VisibleWidth);
            ScrollDiagram(hit.Value, _dragStartOff + (p.X - _dragStartX) * ratio);
        }
        e.Handled = true;
    }

    void OnAreaPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragDiagram == null) return;
        _dragDiagram = null;
        TextArea.ReleaseMouseCapture();
        e.Handled = true;
    }

    DiagramHit? FindDiagramRun(int hash)
    {
        var view = TextArea.TextView;
        if (!view.VisualLinesValid) view.EnsureVisualLines();
        foreach (var vl in view.VisualLines)
            foreach (var el in vl.Elements)
                if (el is DiagramElement de && de.Hash == hash && (de.Run is { Drawn: true } ? de.Run : _lastDrawn.GetValueOrDefault(hash)) is { } run)
                    return new DiagramHit(de, run, default, vl);
        return null;
    }

    /// <summary>Umschalt+Mausrad über einem zu breiten Diagramm verschiebt es waagerecht.</summary>
    bool DiagramWheel(MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0 || !LiveFormatting) return false;
        if (DiagramAt(e.GetPosition(TextArea.TextView)) is not { Run.Overflow: true } hit) return false;
        ScrollDiagram(hit, DiagramScroll(hit.Element.Hash) - e.Delta / 120.0 * 90);
        return true;
    }

    /// <summary>Quelltext des Diagramms unter dem Cursor (oder darüber), sonst des ersten im Dokument – für Menü und Viewer.</summary>
    public (string Source, int BlockFirst)? DiagramNearCaret(Func<Diagrams.Diagram, bool>? accept = null)
    {
        var doc = Document;
        if (doc == null || !IsMarkdown) return null;
        int caretLine = TextArea.Caret.Line;
        var blocks = new List<MdLine>();
        foreach (var b in Analysis.DiagramBlocks) if (b.Closed) blocks.Add(b);
        blocks.Sort((x, y) => x.BlockFirst.CompareTo(y.BlockFirst));
        var ordered = new List<MdLine>();
        ordered.AddRange(blocks.FindAll(b => b.BlockFirst <= caretLine && caretLine <= b.BlockLast));
        var above = blocks.FindAll(b => b.BlockLast < caretLine);
        above.Reverse();
        ordered.AddRange(above);
        ordered.AddRange(blocks);
        foreach (var b in ordered)
        {
            if (b.BlockLast - b.BlockFirst < 2 || b.BlockLast > doc.LineCount) continue;
            int start = doc.GetLineByNumber(b.BlockFirst + 1).Offset;
            int end = doc.GetLineByNumber(b.BlockLast).Offset;
            string src = doc.GetText(start, end - start);
            if (accept != null && !accept(Diagrams.MermaidParser.Parse(src))) continue;
            return (src, b.BlockFirst);
        }
        return null;
    }

    // ───────────────────────────── kleine Animationen (Aufdecken, Checkbox) ─────────────────────────────

    public double CheckProgress(int lineNumber)
    {
        if (!_checkAnim.TryGetValue(lineNumber, out var t0)) return 1;
        return Math.Clamp((Now - t0) / 0.34, 0, 1);
    }

    void StartTick()
    {
        if (_tickRunning) return;
        _tickRunning = true;
        AnimationTicker.SubscribeFrame(OnTick);
    }

    void OnTick(double dt)
    {
        bool busy = false;
        double now = Now;
        if (now - _revealTime < 0.2)
        {
            RedrawLines(_revealFirst, _revealLast);
            busy = true;
        }
        if (_checkAnim.Count > 0)
        {
            var done = new List<int>();
            foreach (var (line, t0) in _checkAnim)
            {
                if (Document != null && line <= Document.LineCount) TextArea.TextView.Redraw(Document.GetLineByNumber(line));
                if (now - t0 > 0.36) done.Add(line); else busy = true;
            }
            foreach (var l in done) _checkAnim.Remove(l);
        }
        if (_scrolling) busy |= StepScroll(dt);
        busy |= AnimateDiagrams();
        if (!busy)
        {
            _tickRunning = false;
            AnimationTicker.UnsubscribeFrame(OnTick);
        }
    }

    // ───────────────────────────── Maus: Checkboxen und Links ─────────────────────────────

    WidgetElement? WidgetAt(Point pInView)
    {
        var view = TextArea.TextView;
        var vl = view.GetVisualLineFromVisualTop(pInView.Y + view.VerticalOffset);
        if (vl == null) return null;
        int col = vl.GetVisualColumnFloor(new Point(pInView.X + view.HorizontalOffset, pInView.Y + view.VerticalOffset));
        foreach (var el in vl.Elements)
            if (el is WidgetElement w && w.Kind != MdWidgetKind.Bullet && col >= el.VisualColumn && col < el.VisualColumn + el.VisualLength)
                return w;
        return null;
    }

    string? LinkAt(Point pInView)
    {
        var view = TextArea.TextView;
        var pos = view.GetPositionFloor(new Point(pInView.X + view.HorizontalOffset, pInView.Y + view.VerticalOffset));
        if (pos == null || Document == null) return null;
        int offset = Document.GetOffset(pos.Value.Location);
        foreach (var l in Analysis.Links)
            if (offset >= l.Start && offset < l.Start + l.Length) return l.Url;
        return null;
    }

    void OnAreaMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!LiveFormatting) return;
        var p = e.GetPosition(TextArea.TextView);
        if (DiagramMouseDown(p))
        {
            e.Handled = true;
            return;
        }
        if (WidgetAt(p) is { } w && Mode != EditorMode.Source)
        {
            ToggleTask(w.LineNumber);
            e.Handled = true;
            return;
        }
        if (Mode == EditorMode.Read || (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (LinkAt(p) is { } url)
            {
                LinkActivated?.Invoke(url);
                e.Handled = true;
            }
        }
    }

    void OnAreaMouseMove(object sender, MouseEventArgs e)
    {
        if (!LiveFormatting) { TextArea.Cursor = null; SetHover(null, false); return; }
        if (_dragDiagram != null) return;
        var p = e.GetPosition(TextArea.TextView);
        if (DiagramAt(p) is { } dh)
        {
            var r = dh.Run;
            bool onButton = r.LocalButton.Contains(dh.Local);
            bool active = onButton || (!r.LocalHint.IsEmpty && r.LocalHint.Contains(dh.Local))
                          || (r.Overflow && !r.LocalTrack.IsEmpty && r.LocalTrack.Contains(dh.Local));
            SetHover(dh.Element.Hash, onButton);
            TextArea.Cursor = active ? Cursors.Hand : null;
            return;
        }
        SetHover(null, false);
        bool hand = WidgetAt(p) != null
                    || ((Mode == EditorMode.Read || (Keyboard.Modifiers & ModifierKeys.Control) != 0) && LinkAt(p) != null);
        TextArea.Cursor = hand ? Cursors.Hand : null;
    }

    /// <summary>Aufgabe in der Zeile ab- bzw. wieder anhaken (eine Rückgängig-Stufe). Geht auch im Lesemodus.</summary>
    public void ToggleTask(int lineNumber)
    {
        if (Document == null) return;
        var line = Document.GetLineByNumber(lineNumber);
        foreach (var w in Analysis.Widgets)
        {
            if (w.Start < line.Offset || w.Start >= line.EndOffset || w.Kind == MdWidgetKind.Bullet) continue;
            int bracket = Document.Text.IndexOf('[', w.Start, w.Length);
            if (bracket < 0) return;
            bool wasReadOnly = IsReadOnly;
            IsReadOnly = false;
            Document.Replace(bracket + 1, 1, w.Kind == MdWidgetKind.TaskDone ? " " : "x");
            IsReadOnly = wasReadOnly;
            _checkAnim[lineNumber] = Now;
            StartTick();
            return;
        }
    }

    // ───────────────────────────── Tastatur: Listen fortsetzen, Formatieren ─────────────────────────────

    static readonly Regex ListPrefix = new(@"^(\s*)((?:>\s?)*)(\s*)([-*+]|\d{1,9}[.)])(\s+)(\[[ xX]\]\s+)?", RegexOptions.Compiled);
    static readonly Regex QuotePrefix = new(@"^(\s*)((?:>\s?)+)", RegexOptions.Compiled);

    void OnAreaKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsMarkdown || IsReadOnly || Document == null) return;
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && TextArea.Selection.IsEmpty)
        {
            if (ContinueList()) e.Handled = true;
        }
    }

    /// <summary>Enter in einer Liste/Aufgabe/einem Zitat setzt sie in der nächsten Zeile fort; Enter in einem leeren Punkt beendet die Liste.</summary>
    bool ContinueList()
    {
        var line = Document.GetLineByOffset(CaretOffset);
        string text = Document.GetText(line);
        int col = CaretOffset - line.Offset;
        var m = ListPrefix.Match(text);
        string prefix;
        if (m.Success)
        {
            string marker = m.Groups[4].Value;
            if (char.IsDigit(marker[0]))
            {
                int num = int.Parse(marker[..^1]);
                marker = (num + 1) + marker[^1..];
            }
            string task = m.Groups[6].Success ? "[ ] " : "";
            prefix = m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value + marker + " " + task;
        }
        else
        {
            var q = QuotePrefix.Match(text);
            if (!q.Success) return false;
            prefix = q.Value;
            m = q;
        }
        if (col < m.Length) return false;
        // leerer Punkt → Liste beenden (Zeichen entfernen statt neue Zeile)
        if (text.Trim().Length == m.Value.Trim().Length && col == text.Length)
        {
            Document.Replace(line.Offset, line.Length, "");
            return true;
        }
        string nl = TextUtilities.GetNewLineFromDocument(Document, line.LineNumber);
        Document.Insert(CaretOffset, nl + prefix);
        return true;
    }

    /// <summary>Markierung mit Zeichen umschließen (Strg+B → **, Strg+I → *); erneut drücken entfernt sie.</summary>
    public void ToggleWrap(string marker)
    {
        if (Document == null || IsReadOnly) return;
        int start = SelectionStart, len = SelectionLength;
        int n = marker.Length;
        Document.BeginUpdate();
        try
        {
            if (len > 0 && start >= n && start + len + n <= Document.TextLength
                && Document.GetText(start - n, n) == marker && Document.GetText(start + len, n) == marker)
            {
                Document.Remove(start + len, n);
                Document.Remove(start - n, n);
                Select(start - n, len);
            }
            else if (len == 0)
            {
                Document.Insert(start, marker + marker);
                CaretOffset = start + n;
            }
            else
            {
                Document.Insert(start + len, marker);
                Document.Insert(start, marker);
                Select(start + n, len);
            }
        }
        finally { Document.EndUpdate(); }
    }

    static readonly Regex AnyLinePrefix = new(@"^(\s*)(#{1,6}\s+|>\s?|[-*+]\s+\[[ xX]\]\s+|[-*+]\s+|\d{1,9}[.)]\s+)", RegexOptions.Compiled);

    /// <summary>Zeilenanfang setzen (#, -, 1., - [ ], >). Ein vorhandenes Zeilenzeichen wird ersetzt;
    /// dasselbe noch einmal entfernt es wieder.</summary>
    public void SetLinePrefix(string prefix)
    {
        if (Document == null || IsReadOnly) return;
        var line = Document.GetLineByOffset(CaretOffset);
        string text = Document.GetText(line);
        var m = AnyLinePrefix.Match(text);
        int start = line.Offset + (m.Success ? m.Groups[1].Length : 0);
        int oldLen = m.Success ? m.Groups[2].Length : 0;
        string old = m.Success ? m.Groups[2].Value : "";
        string insert = old.Trim() == prefix.Trim() ? "" : prefix;
        int caretInLine = CaretOffset - line.Offset;
        Document.Replace(start, oldLen, insert);
        // Cursor wandert mit dem Text mit, steht aber mindestens hinter dem neuen Zeichen
        int end = Document.GetLineByNumber(line.LineNumber).EndOffset;
        CaretOffset = Math.Clamp(line.Offset + caretInLine + insert.Length - oldLen, start + insert.Length, end);
    }

    /// <summary>Block (Codeblock, Trennlinie, Tabelle) in eigene Zeilen einfügen. <paramref name="caretAt"/> = Cursor
    /// relativ zum Blockanfang danach, <paramref name="selectLength"/> markiert ab dort (z. B. einen Platzhalter).</summary>
    public void InsertBlock(string block, int caretAt, int selectLength = 0)
    {
        if (Document == null || IsReadOnly) return;
        string nl = TextUtilities.GetNewLineFromDocument(Document, Document.GetLineByOffset(CaretOffset).LineNumber);
        block = block.Replace("\n", nl);
        var line = Document.GetLineByOffset(CaretOffset);
        bool lineEmpty = line.Length == 0 || Document.GetText(line).Trim().Length == 0;
        string before = lineEmpty ? "" : nl;
        int at = lineEmpty ? line.Offset : line.EndOffset;
        if (lineEmpty && line.Length > 0) Document.Remove(line.Offset, line.Length);
        Document.Insert(at, before + block);
        int start = at + before.Length + caretAt;
        if (selectLength > 0) Select(start, selectLength); else CaretOffset = start;
    }

    /// <summary>Link einfügen: Markierung wird zum Linktext, danach ist die Adresse markiert.</summary>
    public void InsertLink()
    {
        if (Document == null || IsReadOnly) return;
        int start = SelectionStart, len = SelectionLength;
        string label = len > 0 ? Document.GetText(start, len) : "Text";
        const string url = "https://";
        Document.Replace(start, len, $"[{label}]({url})");
        if (len > 0) Select(start + label.Length + 3, url.Length);
        else Select(start + 1, label.Length);
    }

    // ───────────────────────────── Scrollen ─────────────────────────────

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            ZoomRequested?.Invoke(e.Delta > 0 ? 1 : -1);
            e.Handled = true;
            return;
        }
        if (DiagramWheel(e)) { e.Handled = true; return; }
        if (_scroll == null || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
        if (!_scrolling) _scrollTarget = _scroll.VerticalOffset;
        int lines = SystemParameters.WheelScrollLines > 0 ? SystemParameters.WheelScrollLines : 3;
        _scrollTarget = Math.Clamp(_scrollTarget - e.Delta / 120.0 * lines * FontSize * 1.7, 0, _scroll.ScrollableHeight);
        BeginScroll();
        e.Handled = true;
    }

    void BeginScroll()
    {
        _scrolling = true;
        StartTick();
    }

    bool StepScroll(double dt)
    {
        if (_scroll == null) { _scrolling = false; return false; }
        double cur = _scroll.VerticalOffset;
        double next = cur + (_scrollTarget - cur) * (1 - Math.Exp(-dt / 0.09));
        if (Math.Abs(_scrollTarget - next) < 0.5) next = _scrollTarget;
        _lastSetOffset = next;
        _scroll.ScrollToVerticalOffset(next);
        if (next == _scrollTarget) { _scrolling = false; return false; }
        return true;
    }

    void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Jemand anderes hat gescrollt (Bildlaufleiste, Bild-ab-Taste) → Animation abbrechen
        if (_scrolling && e.VerticalChange != 0 && Math.Abs(e.VerticalOffset - _lastSetOffset) > 1) _scrolling = false;
        if (Perf("scroll") && e.VerticalChange != 0) Services.AppPaths.Log($"scroll {e.VerticalOffset:0} (Δ {e.VerticalChange:0}, Animation {_scrolling}, Höhe {e.ExtentHeight:0})");
        _caret.InvalidateVisual();
    }

    /// <summary>Beim Schreiben am unteren Rand rechtzeitig weich nachscrollen, statt dass AvalonEdit hart springt.</summary>
    void KeepCaretAwayFromEdges()
    {
        if (_scroll == null || Mouse.LeftButton == MouseButtonState.Pressed || !IsKeyboardFocusWithin) return;
        var view = TextArea.TextView;
        var caretPos = TextArea.Caret.CalculateCaretRectangle();
        double vh = _scroll.ViewportHeight;
        if (vh <= 0) return;
        double margin = Math.Min(vh * 0.22, FontSize * 1.7 * 3);
        double off = _scrolling ? _scrollTarget : _scroll.VerticalOffset;
        double target = off;
        if (caretPos.Bottom > off + vh - margin) target = caretPos.Bottom - vh + margin;
        else if (caretPos.Top < off + margin && off > 0) target = caretPos.Top - margin;
        target = Math.Clamp(target, 0, Math.Max(0, view.DocumentHeight + vh - FontSize * 2));
        if (Math.Abs(target - off) < 1) return;
        if (Perf("scroll")) Services.AppPaths.Log($"Rand-Nachscrollen auf {target:0} (Cursor {caretPos.Top:0}–{caretPos.Bottom:0}, sichtbar {off:0}+{vh:0})");
        _scrollTarget = target;
        BeginScroll();
    }

    /// <summary>Sofort an eine Stelle scrollen (Tabwechsel).</summary>
    public void JumpTo(double offset)
    {
        _scrolling = false;
        ScrollToVerticalOffset(offset);
    }

    public double CurrentScroll => _scroll?.VerticalOffset ?? VerticalOffset;

    // ───────────────────────────── Cursor, Farben ─────────────────────────────

    void UpdateCaretVisibility()
    {
        // FOLIO_ACTIVE=1: nur für Messungen – Testfenster haben oft keinen Fokus
        bool forced = Environment.GetEnvironmentVariable("FOLIO_ACTIVE") == "1";
        bool visible = Mode != EditorMode.Read && (forced || (IsKeyboardFocusWithin && Window.GetWindow(this)?.IsActive != false));
        _caret.SetActive(visible && !Perf("nocaret"));
    }

    /// <summary>Vom Fenster gerufen, wenn es aktiv/inaktiv wird.</summary>
    public void OnWindowActivationChanged() => UpdateCaretVisibility();

    void ApplyTheme()
    {
        _brushes.Clear();
        _pens.Clear();
        _fadeLeft = _fadeRight = null;
        var t = ThemeManager.Current;
        Foreground = Brush(t.Text);
        TextArea.SelectionBrush = Brush(t.Selection);
        LinkUnderline = Underline(t.Accent, 0.4, 1.0);
        DoneStrike = Strike(t.TextFaint);
        if (ShowLineNumbers) StyleLineNumbers();
        if (!IsMarkdown && SyntaxHighlighting != null) Highlighting.Adapt(SyntaxHighlighting, t.IsDark);
        TextArea.TextView.Redraw();
        _caret.InvalidateVisual();
    }

    static TextDecorationCollection Underline(Color c, double alpha, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(ThemeManager.WithAlpha(c, (byte)(255 * alpha))), thickness);
        pen.Freeze();
        var col = new TextDecorationCollection { new TextDecoration(TextDecorationLocation.Underline, pen, 1.5, TextDecorationUnit.Pixel, TextDecorationUnit.Pixel) };
        col.Freeze();
        return col;
    }

    static TextDecorationCollection Strike(Color c)
    {
        var pen = new Pen(new SolidColorBrush(c), 1.2);
        pen.Freeze();
        var col = new TextDecorationCollection { new TextDecoration(TextDecorationLocation.Strikethrough, pen, 0, TextDecorationUnit.Pixel, TextDecorationUnit.Pixel) };
        col.Freeze();
        return col;
    }

    public SolidColorBrush Brush(Color c)
    {
        if (!_brushes.TryGetValue(c, out var b))
        {
            b = new SolidColorBrush(c);
            b.Freeze();
            _brushes[c] = b;
        }
        return b;
    }

    public SolidColorBrush Brush(Color c, double alpha) => Brush(ThemeManager.WithAlpha(c, (byte)Math.Clamp(255 * alpha, 0, 255)));

    LinearGradientBrush? _fadeLeft, _fadeRight;

    /// <summary>Weicher Übergang zur Papierfarbe am Rand eines verschiebbaren Diagramms.</summary>
    public LinearGradientBrush EdgeFade(bool left)
    {
        if (left ? _fadeLeft == null : _fadeRight == null)
        {
            var p = ThemeManager.Current.Paper;
            var solid = ThemeManager.WithAlpha(p, 255);
            var clear = ThemeManager.WithAlpha(p, 0);
            var b = new LinearGradientBrush(left ? solid : clear, left ? clear : solid, 0);
            b.Freeze();
            if (left) _fadeLeft = b; else _fadeRight = b;
        }
        return left ? _fadeLeft! : _fadeRight!;
    }

    public Pen Pen(Color c, double thickness, bool round = false)
    {
        var key = (c, thickness, round);
        if (!_pens.TryGetValue(key, out var p))
        {
            p = new Pen(Brush(c), thickness);
            if (round)
            {
                p.StartLineCap = p.EndLineCap = PenLineCap.Round;
                p.LineJoin = PenLineJoin.Round;
            }
            p.Freeze();
            _pens[key] = p;
        }
        return p;
    }
}
