using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Folio.Editor;
using Folio.Services;
using ICSharpCode.AvalonEdit.Document;

namespace Folio;

/// <summary>Tabs: anlegen, wählen, schließen, Sitzung sichern und wiederherstellen.</summary>
public partial class MainWindow
{
    sealed class TabChip
    {
        public Border Root = null!;
        public TextBlock Title = null!;
        public Ellipse Dot = null!;
        public Button Close = null!;
    }

    readonly List<DocumentTab> _tabs = new();
    readonly Dictionary<DocumentTab, TabChip> _chips = new();
    readonly Dictionary<DocumentTab, long> _sessionRevision = new();
    readonly Dictionary<DocumentTab, long> _revision = new();
    readonly Stack<string> _closedFiles = new();
    readonly DispatcherTimer _sessionTimer;
    DocumentTab? _current;

    const double TabMaxWidth = 220, TabMinWidth = 72;

    // ───────────────────────────── Anlegen und Wählen ─────────────────────────────

    DocumentTab NewTab(bool animate = true)
    {
        var tab = DocumentTab.CreateNew();
        AddTab(tab, animate);
        Select(tab);
        return tab;
    }

    void AddTab(DocumentTab tab, bool animate = true)
    {
        _tabs.Add(tab);
        _revision[tab] = 0;
        tab.Document.TextChanged += (_, _) => _revision[tab] = _revision.GetValueOrDefault(tab) + 1;
        tab.Changed += () => Dispatcher.BeginInvoke(DispatcherPriority.Background, () => RefreshChip(tab));
        var chip = CreateChip(tab);
        _chips[tab] = chip;
        TabPanel.Children.Add(chip.Root);
        RefreshChip(tab);
        FitTabs(); // setzt die Zielbreite aller Tabs
        if (animate)
        {
            // wächst von 0 auf seine Breite; danach gilt wieder der normale Wert (FillBehavior.Stop)
            var d = TimeSpan.FromMilliseconds(240);
            chip.Root.BeginAnimation(FrameworkElement.MaxWidthProperty, new DoubleAnimation(0, chip.Root.MaxWidth, d) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
            chip.Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d) { FillBehavior = FillBehavior.Stop });
        }
        ScheduleSessionSave();
    }

    TabChip CreateChip(DocumentTab tab)
    {
        var chip = new TabChip();
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        chip.Dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        chip.Dot.SetResourceReference(Shape.FillProperty, "AccentBrush");
        g.Children.Add(chip.Dot);

        chip.Title = new TextBlock { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(chip.Title, 1);
        g.Children.Add(chip.Title);

        chip.Close = new Button { Style = (Style)FindResource("IconButton"), Content = "", FontSize = 8.5, Width = 22, Height = 22, Margin = new Thickness(6, 0, 0, 0), Opacity = 0, ToolTip = "Schließen (Strg+W)" };
        chip.Close.Click += (_, _) => _ = CloseTab(tab);
        Grid.SetColumn(chip.Close, 2);
        g.Children.Add(chip.Close);

        chip.Root = new Border
        {
            Child = g,
            Height = 34,
            Padding = new Thickness(12, 0, 6, 0),
            Margin = new Thickness(0, 0, 2, 0),
            CornerRadius = new CornerRadius(9),
            Background = Brushes.Transparent,
            MinWidth = 0,
            MaxWidth = TabMaxWidth,
            Cursor = Cursors.Hand,
            ToolTip = null,
        };
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(chip.Root, true);
        chip.Root.MouseEnter += (_, _) => RefreshChip(tab);
        chip.Root.MouseLeave += (_, _) => RefreshChip(tab);
        chip.Root.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 1) Select(tab); };
        chip.Root.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle) { _ = CloseTab(tab); e.Handled = true; }
        };
        chip.Root.SizeChanged += (_, _) => { if (tab == _current) MoveTabHighlight(true); };
        return chip;
    }

    void RefreshChip(DocumentTab tab)
    {
        if (!_chips.TryGetValue(tab, out var chip)) return;
        bool selected = tab == _current;
        bool hover = chip.Root.IsMouseOver;
        chip.Title.Text = tab.Title;
        chip.Title.SetResourceReference(TextBlock.ForegroundProperty, selected ? "TextBrush" : "TextDimBrush");
        chip.Root.ToolTip = tab.FilePath ?? "Neue Notiz – noch nicht gespeichert";
        bool dirty = tab.IsDirty;
        // Punkt = ungespeichert; beim Überfahren (oder im aktiven Tab) wird daraus das Schließen-Kreuz
        bool showClose = hover || (selected && !dirty);
        chip.Dot.Visibility = dirty && !hover ? Visibility.Visible : Visibility.Collapsed;
        chip.Close.Opacity = showClose ? 1 : 0;
        chip.Close.IsHitTestVisible = showClose;
        if (selected || !hover) chip.Root.Background = Brushes.Transparent;
        else chip.Root.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
        if (tab == _current) Title = $"{tab.Title}{(dirty ? " •" : "")} – Folio";
    }

    /// <summary>Viele Tabs: alle gleichmäßig schmaler, damit sie in die Leiste passen.</summary>
    void FitTabs()
    {
        if (_tabs.Count == 0 || TabStrip.ActualWidth <= 0) return;
        double avail = TabStrip.ActualWidth - 44;
        double each = Math.Clamp(avail / _tabs.Count - 2, TabMinWidth, TabMaxWidth);
        foreach (var chip in _chips.Values) chip.Root.MaxWidth = each; // laufende Einblend-Animation bleibt unberührt
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => MoveTabHighlight(false));
    }

    void Select(DocumentTab tab)
    {
        if (_current == tab) return;
        var previous = _current;
        if (previous != null) RememberView(previous);
        _current = tab;
        if (_infoForTab) HideInfo(); // Installieren- und Update-Hinweise bleiben beim Tabwechsel stehen

        _editor.Show(tab.Document, tab.Extension, tab.Mode);
        _editor.WrapCode = _settings.WordWrap;
        _editor.Placeholder = tab.IsUntitled ? "Einfach losschreiben …" : null;
        UpdateEditorPadding();
        SyncModeButtons();
        int caret = Math.Clamp(tab.Caret, 0, tab.Document.TextLength);
        _editor.CaretOffset = caret;
        double scroll = tab.Scroll;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => _editor.JumpTo(scroll));

        foreach (var t in _tabs) RefreshChip(t);
        MoveTabHighlight(previous != null);
        if (previous != null) PlaySwitch();
        UpdateStatus();
        if (tab.ExternalChangePending) ShowExternalChange(tab);
        _editor.Focus();
        ScheduleSessionSave();
    }

    void SelectRelative(int dir)
    {
        if (_current == null || _tabs.Count < 2) return;
        int i = _tabs.IndexOf(_current);
        Select(_tabs[(i + dir + _tabs.Count) % _tabs.Count]);
    }

    /// <summary>Tabwechsel: der neue Inhalt blendet kurz und weich ein.</summary>
    void PlaySwitch()
    {
        EditorHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = EaseOut });
        EditorShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = EaseOut });
    }

    void MoveTabHighlight(bool animate)
    {
        if (_current == null || !_chips.TryGetValue(_current, out var chip) || chip.Root.ActualWidth <= 0)
        {
            TabHighlight.Opacity = 0;
            return;
        }
        var p = chip.Root.TranslatePoint(new Point(0, 0), TabStrip);
        if (TabHighlight.Opacity < 1)
        {
            TabHighlight.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
            animate = false;
        }
        Slide(TabHighlight, p.X, chip.Root.ActualWidth, animate ? 280 : 0);
    }

    void RememberView(DocumentTab tab)
    {
        if (_editor.Document != tab.Document) return;
        tab.Caret = _editor.CaretOffset;
        tab.Scroll = _editor.CurrentScroll;
    }

    // ───────────────────────────── Schließen ─────────────────────────────

    async Task CloseTab(DocumentTab? tab)
    {
        if (tab == null) return;
        if (tab.IsDirty)
        {
            if (tab != _current) Select(tab);
            string name = tab.IsUntitled ? "diese Notiz" : $"„{tab.Title}“";
            int r = await ShowDialog(tab.IsUntitled ? "Notiz speichern?" : "Änderungen speichern?",
                tab.IsUntitled
                    ? "Die Notiz wurde noch nicht gespeichert. Ohne Speichern geht ihr Inhalt verloren."
                    : $"Die Änderungen an {name} sind noch nicht gespeichert.",
                null, "Speichern", "Nicht speichern", "Abbrechen");
            if (r == 0) { if (!await Save(tab)) return; }
            else if (r != 1) return;
        }
        RemoveTab(tab);
    }

    void RemoveTab(DocumentTab tab)
    {
        int idx = _tabs.IndexOf(tab);
        if (idx < 0) return;
        if (tab.FilePath != null) _closedFiles.Push(tab.FilePath);
        _tabs.RemoveAt(idx);
        _revision.Remove(tab);
        _sessionRevision.Remove(tab);
        SessionStore.DeleteContent(tab.Id);

        if (_chips.Remove(tab, out var chip))
        {
            chip.Root.IsHitTestVisible = false;
            var d = TimeSpan.FromMilliseconds(180);
            var shrink = new DoubleAnimation(chip.Root.ActualWidth, 0, d) { EasingFunction = EaseOut };
            shrink.Completed += (_, _) => { TabPanel.Children.Remove(chip.Root); FitTabs(); };
            chip.Root.BeginAnimation(FrameworkElement.MaxWidthProperty, shrink);
            chip.Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, d));
        }

        if (_tabs.Count == 0)
        {
            _current = null;
            NewTab();
        }
        else if (tab == _current)
        {
            _current = null;
            Select(_tabs[Math.Min(idx, _tabs.Count - 1)]);
        }
        ScheduleSessionSave();
    }

    void ReopenClosedTab()
    {
        while (_closedFiles.Count > 0)
        {
            string f = _closedFiles.Pop();
            if (File.Exists(f)) { OpenFile(f); return; }
        }
    }

    // ───────────────────────────── Sitzung ─────────────────────────────

    void ScheduleSessionSave()
    {
        if (_snapshotPath != null) return;
        _sessionTimer.Stop();
        _sessionTimer.Start();
    }

    void SaveSession()
    {
        if (_snapshotPath != null) return;
        if (_current != null) RememberView(_current);
        var state = new SessionState { Active = _current?.Id };
        foreach (var t in _tabs)
        {
            bool dirty = t.IsDirty;
            // Inhalt nur schreiben, wenn er sich seit dem letzten Sichern geändert hat
            if (dirty && (!_sessionRevision.TryGetValue(t, out var saved) || saved != _revision.GetValueOrDefault(t) || !File.Exists(SessionStore.ContentFile(t.Id))))
            {
                try
                {
                    SessionStore.SaveContent(t.Id, t.Document.Text);
                    _sessionRevision[t] = _revision.GetValueOrDefault(t);
                }
                catch (Exception ex)
                {
                    AppPaths.Log($"Sitzungsinhalt {t.Id}: " + ex.Message);
                }
            }
            if (t.IsUntitled && !dirty) continue; // leere neue Notiz nicht merken
            state.Tabs.Add(new SessionTab
            {
                Id = t.Id,
                Path = t.FilePath,
                Dirty = dirty,
                Encoding = t.Encoding.WebName,
                Bom = t.Encoding.GetPreamble().Length > 0,
                Caret = t.Caret,
                Scroll = t.Scroll,
                Mode = t.Mode.ToString().ToLowerInvariant(),
                FileTime = t.FileTime,
            });
        }
        SessionStore.Save(state);
    }

    void RestoreSession()
    {
        var state = SessionStore.Load();
        DocumentTab? active = null;
        foreach (var st in state.Tabs)
        {
            try
            {
                string? text = null;
                System.Text.Encoding enc = TextFiles.Utf8NoBom;
                DateTime? fileTime = st.FileTime;
                if (st.Dirty) text = SessionStore.LoadContent(st.Id);
                if (text == null && st.Path != null && File.Exists(st.Path))
                {
                    var loaded = TextFiles.Read(st.Path);
                    text = loaded.Text;
                    enc = loaded.Encoding;
                    fileTime = File.GetLastWriteTimeUtc(st.Path);
                }
                else if (st.Path != null) enc = EncodingFromSession(st);
                if (text == null) continue; // weder Sitzungsinhalt noch Datei – nichts zu retten

                var doc = new TextDocument(text) { FileName = st.Path };
                doc.UndoStack.ClearAll();
                var tab = new DocumentTab(st.Id, doc, st.Path)
                {
                    Encoding = enc,
                    Caret = st.Caret,
                    Scroll = st.Scroll,
                    Mode = Enum.TryParse<EditorMode>(st.Mode, true, out var m) ? m : EditorMode.Live,
                    FileTime = fileTime,
                    RestoredDirty = st.Dirty && st.Path != null,
                };
                AddTab(tab, animate: false);
                _sessionRevision[tab] = 0; // Inhalt liegt schon in der Sitzung
                if (st.Id == state.Active) active = tab;
            }
            catch (Exception ex)
            {
                AppPaths.Log($"Tab {st.Id} nicht wiederherstellbar: " + ex.Message);
            }
        }
        if (_tabs.Count > 0) Select(active ?? _tabs[^1]);
    }

    static System.Text.Encoding EncodingFromSession(SessionTab st)
    {
        try
        {
            var e = System.Text.Encoding.GetEncoding(st.Encoding);
            if (e is System.Text.UTF8Encoding) return new System.Text.UTF8Encoding(st.Bom);
            return e;
        }
        catch { return TextFiles.Utf8NoBom; }
    }
}
