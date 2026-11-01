using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Folio.Diagrams;
using Folio.Visuals;

namespace Folio;

/// <summary>Diagramm-Viewer: ein Diagramm groß über dem Papier – zoomen, verschieben, als PNG/SVG/BPMN speichern.</summary>
public partial class MainWindow
{
    DiagramView? _viewer;
    string? _viewerSource;
    string _viewerName = "";
    FlowChart? _viewerFlow;

    bool ViewerOpen => ViewerLayer.Visibility == Visibility.Visible && _viewerSource != null;

    void WireViewer()
    {
        _viewer = new DiagramView();
        ViewerHost.Children.Add(_viewer);
        _viewer.ZoomChanged += () => ViewerZoomText.Text = $"{Math.Round(_viewer.Zoom * 100):0} %";
        _editor.DiagramOpenRequested += OpenViewer;
        ViewerClose.Click += (_, _) => CloseViewer();
        ViewerFit.Click += (_, _) => _viewer.Fit();
        ViewerActual.Click += (_, _) => _viewer.Actual();
        ViewerZoomIn.Click += (_, _) => _viewer.ZoomBy(1.25);
        ViewerZoomOut.Click += (_, _) => _viewer.ZoomBy(1 / 1.25);
        ViewerPng.Click += async (_, _) => { if (_viewerSource != null) await ExportImage(_viewerSource, _viewerName, "png"); };
        ViewerSvg.Click += async (_, _) => { if (_viewerSource != null) await ExportImage(_viewerSource, _viewerName, "svg"); };
        ViewerBpmn.Click += async (_, _) => { if (_viewerFlow != null) await ExportBpmn(_viewerFlow, _viewerName); };
    }

    /// <summary>Viewer für ein Diagramm (Quelltext des Blocks) öffnen.</summary>
    void OpenViewer(string source)
    {
        if (_viewer == null) return;
        var d = MermaidParser.Parse(source);
        int blockFirst = FindBlockOf(source) ?? 1;
        _viewerSource = source;
        _viewerName = DiagramName(d, blockFirst);
        _viewerFlow = d is FlowChart { Nodes.Count: > 0 } f ? f : null;
        ViewerTitle.Text = _viewerName;
        ViewerBpmn.Visibility = _viewerFlow != null ? Visibility.Visible : Visibility.Collapsed;
        bool wasOpen = ViewerLayer.Visibility == Visibility.Visible;
        ViewerLayer.Visibility = Visibility.Visible;
        RefreshViewer(keepView: false);
        if (!wasOpen)
        {
            var d1 = TimeSpan.FromMilliseconds(170);
            ViewerLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d1));
            // Karte wächst leicht aus der Mitte
            ViewerScale.CenterX = ViewerHost.ActualWidth / 2;
            ViewerScale.CenterY = ViewerHost.ActualHeight / 2;
            ViewerScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = EaseOut });
            ViewerScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = EaseOut });
        }
        // Erst nach dem Layout ist die Größe bekannt → dann einpassen
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { _viewer.Fit(animate: false); _viewer.Focus(); }));
    }

    int? FindBlockOf(string source)
    {
        var doc = _editor.Document;
        if (doc == null) return null;
        foreach (var b in _editor.Analysis.DiagramBlocks.Where(b => b.Closed && b.BlockLast - b.BlockFirst >= 2 && b.BlockLast <= doc.LineCount))
        {
            int start = doc.GetLineByNumber(b.BlockFirst + 1).Offset;
            int end = doc.GetLineByNumber(b.BlockLast).Offset;
            if (doc.GetText(start, end - start) == source) return b.BlockFirst;
        }
        return null;
    }

    /// <summary>Neu zeichnen (z. B. nach Themenwechsel) – Zoom und Ausschnitt bleiben, wenn gewünscht.</summary>
    void RefreshViewer(bool keepView)
    {
        if (_viewer == null || _viewerSource == null) return;
        var t = ThemeManager.Current;
        _viewer.SetDotColor(ThemeManager.WithAlpha(t.TextFaint, (byte)(t.IsDark ? 70 : 90)));
        var pic = _editor.GetDiagram(_viewerSource, 1e5);
        _viewer.SetDrawing(DiagramImageExport.Compose(pic.Parts.Select(p => p.Drawing)), pic.Size, keepView);
    }

    void CloseViewer()
    {
        if (ViewerLayer.Visibility != Visibility.Visible) return;
        _viewer?.Detach();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(130));
        fade.Completed += (_, _) =>
        {
            if (ViewerLayer.Opacity < 0.01)
            {
                ViewerLayer.Visibility = Visibility.Collapsed;
                _viewerSource = null;
            }
        };
        ViewerLayer.BeginAnimation(OpacityProperty, fade);
        _editor.Focus();
    }

    /// <summary>Tasten, solange der Viewer offen ist. Gibt true zurück, wenn die Taste dort hingehört.</summary>
    bool ViewerKey(Key key, ModifierKeys mods)
    {
        if (!ViewerOpen || _viewer == null) return false;
        switch (key)
        {
            case Key.Escape: CloseViewer(); return true;
            case Key.D0 or Key.NumPad0: _viewer.Fit(); return true;
            case Key.D1 or Key.NumPad1: _viewer.Actual(); return true;
            case Key.OemPlus or Key.Add: _viewer.ZoomBy(1.25); return true;
            case Key.OemMinus or Key.Subtract: _viewer.ZoomBy(1 / 1.25); return true;
        }
        // Alles andere nicht an den (verdeckten) Editor weitergeben – außer Fensterkürzeln wie Alt+F4
        return mods != ModifierKeys.Alt;
    }
}
