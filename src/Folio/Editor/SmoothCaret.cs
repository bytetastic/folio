using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Folio.Visuals;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace Folio.Editor;

/// <summary>
/// Eigener Cursor statt des harten AvalonEdit-Strichs: gleitet zur neuen Position, blinkt weich (ein- und ausblenden)
/// und bleibt beim Tippen ruhig sichtbar. Läuft nur im gemeinsamen Takt, solange sich etwas bewegt oder blendet –
/// in den ruhigen Phasen des Blinkens wartet ein Timer statt Bild für Bild.
/// </summary>
public sealed class SmoothCaret : UIElement
{
    readonly FolioEditor _editor;
    readonly TextView _view;
    readonly DispatcherTimer _wait;

    // Position in Dokument-Koordinaten (unabhängig vom Scrollen – Scrollen soll den Cursor nicht „nachziehen“)
    Point _pos, _target;
    double _height, _targetHeight;
    bool _hasPos;
    bool _subscribed;
    double _blink;      // Zeit im Blinkzyklus
    double _opacity = 1;
    bool _visible;
    bool _snap;

    /// <summary>Ohne Gleiten: der Cursor springt sofort an die neue Stelle (Einstellung „Weiche Animationen“ aus).</summary>
    public bool Snap { get => _snap; set => _snap = value; }

    const double Tau = 0.034;          // Gleitzeit (s) – kurz genug, dass schnelles Tippen nicht hinterherhinkt
    const double Hold = 0.6;           // nach jeder Bewegung so lange ruhig sichtbar
    const double On = 0.55, Fade = 0.16, Off = 0.3;
    const double Cycle = On + Fade + Off + Fade;

    public SmoothCaret(FolioEditor editor, TextView view)
    {
        _editor = editor;
        _view = view;
        IsHitTestVisible = false;
        _wait = new DispatcherTimer(DispatcherPriority.Render);
        _wait.Tick += (_, _) => { _wait.Stop(); Wake(); };
        _view.ScrollOffsetChanged += (_, _) => InvalidateVisual();
        _view.VisualLinesChanged += (_, _) => Retarget(false);
    }

    /// <summary>Sichtbar, wenn der Editor den Fokus hat und nicht im Lesemodus ist.</summary>
    public void SetActive(bool visible)
    {
        if (_visible == visible) return;
        _visible = visible;
        _blink = 0;
        _opacity = visible ? 1 : 0;
        if (visible) { _hasPos = false; Retarget(true); }
        else { Sleep(); InvalidateVisual(); }
    }

    bool _retargetQueued;

    /// <summary>Wie <see cref="Retarget"/>, aber erst nach dem nächsten Layout – die Zeile ist dann schon neu aufgebaut
    /// und wird nicht ein zweites Mal nur für den Cursor gesetzt.</summary>
    public void RetargetAfterLayout()
    {
        if (_retargetQueued) return;
        _retargetQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _retargetQueued = false;
            Retarget(true);
        });
    }

    /// <summary>Nach Tippen oder Cursorbewegung: neues Ziel, Blinken neu beginnen.</summary>
    public void Retarget(bool restartBlink)
    {
        if (!_visible) return;
        if (!TryGetCaretRect(out var r)) return;
        _target = r.TopLeft;
        _targetHeight = r.Height;
        if (!_hasPos || (_target - _pos).Length > 900)
        {
            _pos = _target;
            _height = _targetHeight;
            _hasPos = true;
        }
        if (restartBlink)
        {
            _blink = -Hold;
            _opacity = 1;
        }
        Wake();
        InvalidateVisual();
    }

    bool TryGetCaretRect(out Rect rect)
    {
        rect = default;
        var caret = _editor.TextArea.Caret;
        var doc = _view.Document;
        if (doc == null) return false;
        var line = doc.GetLineByNumber(Math.Clamp(caret.Line, 1, doc.LineCount));
        VisualLine vl;
        try { vl = _view.GetOrConstructVisualLine(line); }
        catch (InvalidOperationException) { return false; } // mitten im Layout
        int col = Math.Clamp(caret.VisualColumn, 0, vl.VisualLengthWithEndOfLineMarker);
        var tl = vl.GetTextLine(col, caret.IsInVirtualSpace);
        double x = vl.GetTextLineVisualXPosition(tl, col);
        double lineTop = vl.GetTextLineVisualYPosition(tl, VisualYPosition.LineTop);
        double baseline = lineTop + tl.Baseline;
        double size = _editor.StyleOfLine(line.LineNumber).FontSize;
        double h = size * 1.32;
        rect = new Rect(x, baseline - size * 1.02, 2, h);
        return true;
    }

    void Wake()
    {
        if (_subscribed || !_visible) return;
        _wait.Stop();
        _subscribed = true;
        AnimationTicker.SubscribeFrame(OnFrame);
    }

    void Sleep()
    {
        _wait.Stop();
        if (!_subscribed) return;
        _subscribed = false;
        AnimationTicker.UnsubscribeFrame(OnFrame);
    }

    void OnFrame(double dt)
    {
        bool moving = false, jumped = false;
        if (_hasPos)
        {
            double k = 1 - Math.Exp(-dt / Tau);
            var d = _target - _pos;
            if (_snap)
            {
                jumped = _pos != _target || _height != _targetHeight;
                _pos = _target;
                _height = _targetHeight;
            }
            else if (d.Length > 0.6 || Math.Abs(_targetHeight - _height) > 0.6)
            {
                _pos += d * k;
                _height += (_targetHeight - _height) * k;
                moving = true;
            }
            else
            {
                _pos = _target;
                _height = _targetHeight;
            }
        }

        _blink += dt;
        double phase = _blink < 0 ? -1 : _blink % Cycle;
        double op;
        double waitFor = 0;
        if (phase < 0) { op = 1; waitFor = -_blink; }
        else if (phase < On) { op = 1; waitFor = On - phase; }
        else if (phase < On + Fade) op = 1 - Smooth((phase - On) / Fade);
        else if (phase < On + Fade + Off) { op = 0; waitFor = On + Fade + Off - phase; }
        else op = Smooth((phase - On - Fade - Off) / Fade);

        bool changed = moving || jumped || Math.Abs(op - _opacity) > 0.001;
        _opacity = op;
        if (changed) InvalidateVisual();

        // In einer ruhigen Phase und ohne Bewegung: bis zum nächsten Übergang schlafen
        if (!moving && waitFor > 0.03)
        {
            Sleep();
            _wait.Interval = TimeSpan.FromSeconds(waitFor);
            _wait.Start();
        }
    }

    static double Smooth(double x) => x * x * (3 - 2 * x);

    protected override void OnRender(DrawingContext dc)
    {
        if (!_visible || !_hasPos || _opacity <= 0.01) return;
        var off = _view.ScrollOffset;
        var r = new Rect(_pos.X - off.X - 1, _pos.Y - off.Y, 2, _height);
        var t = ThemeManager.Current;
        dc.PushOpacity(_opacity);
        dc.DrawRoundedRectangle(_editor.Brush(t.Accent), null, r, 1, 1);
        dc.Pop();
    }
}
