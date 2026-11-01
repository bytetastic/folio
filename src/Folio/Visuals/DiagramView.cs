using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Folio.Visuals;

/// <summary>
/// Zeichenfläche des Diagramm-Viewers: Mausrad zoomt um die Mausposition, Ziehen verschiebt,
/// Doppelklick wechselt zwischen „Einpassen“ und 100 %. Gezeichnet wird die fertige Vektor-Zeichnung –
/// Text bleibt bei jeder Zoomstufe scharf. Zoom gleitet kurz über den gemeinsamen Takt und schläft danach.
/// </summary>
public sealed class DiagramView : FrameworkElement
{
    Drawing? _drawing;
    Size _size;
    double _zoom = 1, _targetZoom = 1;
    Vector _off, _targetOff;
    Point? _anchorScreen, _anchorContent; // beim Mausrad-Zoom bleibt dieser Punkt unter der Maus
    bool _animating, _fitted = true;
    Point? _dragFrom;
    Vector _dragOff;
    Brush? _dots;

    public const double MinZoom = 0.05, MaxZoom = 8;

    /// <summary>Zoomstufe, auf die gerade zugesteuert wird (für die Anzeige „85 %“).</summary>
    public double Zoom => _targetZoom;
    public event Action? ZoomChanged;

    public DiagramView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
    }

    public void SetDrawing(Drawing drawing, Size size, bool keepView)
    {
        _drawing = drawing;
        _size = size;
        if (!keepView || _fitted) Fit(animate: false);
        InvalidateVisual();
    }

    /// <summary>Punktraster im Hintergrund (in Bildschirmgröße, wandert beim Verschieben nicht mit – ruhiger).</summary>
    public void SetDotColor(Color c)
    {
        var tile = new DrawingGroup();
        using (var dc = tile.Open())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 22, 22));
            var b = new SolidColorBrush(c);
            b.Freeze();
            dc.DrawEllipse(b, null, new Point(11, 11), 1, 1);
        }
        tile.Freeze();
        var brush = new DrawingBrush(tile) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 22, 22), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
        brush.Freeze();
        _dots = brush;
        InvalidateVisual();
    }

    double FitZoom
    {
        get
        {
            if (_size.Width <= 0 || _size.Height <= 0 || ActualWidth <= 0 || ActualHeight <= 0) return 1;
            double z = Math.Min((ActualWidth - 56) / _size.Width, (ActualHeight - 56) / _size.Height);
            return Math.Clamp(z, MinZoom, 2);
        }
    }

    Vector Centered(double z) => new((ActualWidth - _size.Width * z) / 2, (ActualHeight - _size.Height * z) / 2);

    public void Fit(bool animate = true)
    {
        _fitted = true;
        double z = FitZoom;
        SetView(z, Centered(z), null, null, animate);
    }

    /// <summary>100 % – um die Bildmitte (oder einen Punkt, z. B. die Maus beim Doppelklick).</summary>
    public void Actual(Point? at = null, bool animate = true) => ZoomTo(1, at, animate);

    public void ZoomBy(double factor, Point? at = null, bool animate = true) => ZoomTo(_targetZoom * factor, at, animate);

    void ZoomTo(double z, Point? at, bool animate)
    {
        _fitted = false;
        z = Math.Clamp(z, MinZoom, MaxZoom);
        var screen = at ?? new Point(ActualWidth / 2, ActualHeight / 2);
        // Inhaltspunkt unter „screen“ im Zielzustand festhalten
        var content = new Point((screen.X - _targetOff.X) / _targetZoom, (screen.Y - _targetOff.Y) / _targetZoom);
        var off = new Vector(screen.X - content.X * z, screen.Y - content.Y * z);
        SetView(z, off, screen, content, animate);
    }

    void SetView(double z, Vector off, Point? anchorScreen, Point? anchorContent, bool animate)
    {
        _targetZoom = z;
        _targetOff = off;
        _anchorScreen = anchorScreen;
        _anchorContent = anchorContent;
        ZoomChanged?.Invoke();
        if (!animate || !IsVisible)
        {
            _zoom = z;
            _off = off;
            StopAnimation();
            InvalidateVisual();
            return;
        }
        if (!_animating)
        {
            _animating = true;
            AnimationTicker.SubscribeFrame(OnTick);
        }
    }

    void StopAnimation()
    {
        if (!_animating) return;
        _animating = false;
        AnimationTicker.UnsubscribeFrame(OnTick);
    }

    void OnTick(double dt)
    {
        double k = 1 - Math.Exp(-dt / 0.075);
        // Zoom im Logarithmus angleichen – wirkt gleichmäßig, egal ob 20 % oder 400 %
        double lz = Math.Log(_zoom), lt = Math.Log(_targetZoom);
        _zoom = Math.Exp(lz + (lt - lz) * k);
        if (_anchorScreen is { } s && _anchorContent is { } c)
            _off = new Vector(s.X - c.X * _zoom, s.Y - c.Y * _zoom);
        else
            _off += (_targetOff - _off) * k;
        if (Math.Abs(lt - Math.Log(_zoom)) < 0.002 && (_targetOff - _off).Length < 0.5)
        {
            _zoom = _targetZoom;
            _off = _targetOff;
            StopAnimation();
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var full = new Rect(RenderSize);
        dc.DrawRectangle(_dots ?? (Brush)Brushes.Transparent, null, full);
        if (_drawing == null) return;
        dc.PushTransform(new MatrixTransform(_zoom, 0, 0, _zoom, _off.X, _off.Y));
        dc.DrawDrawing(_drawing);
        dc.Pop();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        if (_fitted) Fit(animate: false);
        else
        {
            // Mitte bleibt Mitte
            var d = new Vector((info.NewSize.Width - info.PreviousSize.Width) / 2, (info.NewSize.Height - info.PreviousSize.Height) / 2);
            _off += d;
            _targetOff += d;
            InvalidateVisual();
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        ZoomBy(Math.Pow(1.0018, e.Delta), e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        if (e.ClickCount == 2)
        {
            if (_fitted || Math.Abs(_targetZoom - FitZoom) < 0.01) Actual(p);
            else Fit();
            e.Handled = true;
            return;
        }
        StopAnimation();
        _zoom = _targetZoom;
        _off = _targetOff;
        _dragFrom = p;
        _dragOff = _off;
        CaptureMouse();
        Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragFrom is not { } from) return;
        _fitted = false;
        _off = _targetOff = _dragOff + (e.GetPosition(this) - from);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_dragFrom == null) return;
        _dragFrom = null;
        ReleaseMouseCapture();
        Cursor = null;
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _dragFrom = null;
        Cursor = null;
    }

    /// <summary>Beim Schließen: Takt sicher abmelden.</summary>
    public void Detach() => StopAnimation();

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);
}
