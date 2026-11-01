using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Folio.Editor;
using Folio.Native;
using Folio.Services;
using Folio.Visuals;

namespace Folio;

public partial class MainWindow : Window
{
    readonly AppSettings _settings;
    readonly FolioEditor _editor;
    readonly string? _snapshotPath;
    readonly int _snapshotDelay;
    readonly IReadOnlyList<string> _startFiles;
    readonly IpcServer _ipc;
    readonly DispatcherTimer _statusTimer;
    bool _mica;
    TaskCompletionSource<int>? _dialogResult;
    readonly ICSharpCode.AvalonEdit.Search.SearchPanel _search;

    static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };

    public MainWindow(AppSettings settings, IReadOnlyList<string> files, string? snapshotPath, int snapshotDelay)
    {
        _settings = settings;
        _startFiles = files;
        _snapshotPath = snapshotPath;
        _snapshotDelay = snapshotDelay;
        InitializeComponent();

        _editor = new FolioEditor
        {
            FontSize = Math.Clamp(settings.FontSize, 10, 40),
            SmoothTyping = settings.SmoothTyping,
            Padding = new Thickness(0, 22, 0, 0),
        };
        EditorHost.Children.Add(_editor);
        _search = ICSharpCode.AvalonEdit.Search.SearchPanel.Install(_editor);
        _search.MarkerCornerRadius = 4;
        _editor.CaretMoved += ScheduleStatus;
        _editor.TextChanged += (_, _) => { ScheduleStatus(); ScheduleSessionSave(); };
        _editor.LinkActivated += OpenLink;
        _editor.ZoomRequested += Zoom;
        EditorHost.SizeChanged += (_, _) => UpdateEditorPadding();

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); UpdateStatus(); };
        _sessionTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(900) };
        _sessionTimer.Tick += (_, _) => { _sessionTimer.Stop(); SaveSession(); };

        RestoreWindowBounds();
        SourceInitialized += (_, _) => ApplyWindowFrame();
        Loaded += OnLoaded;
        Closing += OnClosing;
        Activated += (_, _) => { _editor.OnWindowActivationChanged(); CheckExternalChanges(); };
        Deactivated += (_, _) => _editor.OnWindowActivationChanged();
        StateChanged += (_, _) => OnStateChanged();
        PreviewKeyDown += OnPreviewKeyDown;
        Drop += OnDrop;
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };

        MinButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaxButton.Click += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        CloseButton.Click += (_, _) => Close();
        AddTabButton.Click += (_, _) => NewTab();
        ThemeButton.Click += (_, _) => ToggleTheme();
        MenuButton.Click += (_, _) => OpenMenu();
        UpdateButton.Click += async (_, _) => await OfferUpdate();
        InfoClose.Click += (_, _) => HideInfo();
        ModeLive.Checked += (_, _) => SetMode(EditorMode.Live);
        ModeSource.Checked += (_, _) => SetMode(EditorMode.Source);
        ModeRead.Checked += (_, _) => SetMode(EditorMode.Read);
        ModeSwitch.SizeChanged += (_, _) => MoveModeHighlight(false);
        ZoomButton.Click += (_, _) => Zoom(0);
        LineEndingButton.Click += (_, _) => ToggleLineEndings();
        TabStrip.SizeChanged += (_, _) => FitTabs();
        ThemeManager.Changed += OnThemeChanged;

        _ipc = new IpcServer(AppPaths.PipeName);
        _ipc.OpenRequested += path => Dispatcher.BeginInvoke(() =>
        {
            BringToFront();
            if (path.Length > 0) OpenFile(path);
        });
        _ipc.UpdateOffered += exe => Dispatcher.BeginInvoke(async () => await OfferLocalUpdate(exe));
        if (_snapshotPath == null) _ipc.Start();

        if (FolioEditor.Perf("noshadow")) PaperShadow.Effect = null;
        WireHelp();
        WireViewer();
        OnThemeChanged();
        SyncThemeButton();
    }

    // ───────────────────────────── Start ─────────────────────────────

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RestoreSession();
        foreach (var f in _startFiles) OpenFile(f);
        if (_tabs.Count == 0) NewTab(animate: false);
        PlayIntro();
        _editor.Focus();

        if (_snapshotPath != null)
        {
            RunTestScene();
            RunTestInput();
            await Task.Delay(_snapshotDelay);
            SaveSnapshot(_snapshotPath);
            if (MenuPopup.IsOpen) SaveElementSnapshot(MenuCard, Path.ChangeExtension(_snapshotPath, null) + "-menu.png");
            Close();
            return;
        }
        RunTestScene();
        RunTestInput();
        await MaybeOfferInstall();
        await CheckForUpdate();
    }

    /// <summary>Beim Öffnen: Inhalt gleitet von leicht unten herein, das Lesezeichen im Logo fällt hinein.</summary>
    void PlayIntro()
    {
        Shell.Opacity = 0;
        Shell.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = EaseOut });
        ShellShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(420)) { EasingFunction = EaseOut });
        RibbonShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-8, 0, TimeSpan.FromMilliseconds(700))
        {
            BeginTime = TimeSpan.FromMilliseconds(180),
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 },
        });
    }

    /// <summary>Nur für Tests: FOLIO_TEST_INPUT tippt Text Zeichen für Zeichen (\n = Enter, über die Editor-Logik).</summary>
    double _inputMs;
    int _inputCount;

    void RunTestInput()
    {
        if (Environment.GetEnvironmentVariable("FOLIO_TEST_INPUT") is not { Length: > 0 } input) return;
        input = input.Replace("\\n", "\n");
        int i = 0;
        int ms = int.TryParse(Environment.GetEnvironmentVariable("FOLIO_TEST_INPUT_MS"), out var v) ? v : 18;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) =>
        {
            if (i >= input.Length) { t.Stop(); return; }
            char c = input[i++];
            if (c == '\n')
            {
                var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(_editor)!, 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                _editor.TextArea.RaiseEvent(args);
                if (!args.Handled) _editor.TextArea.PerformTextInput("\n");
            }
            else
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                _editor.TextArea.PerformTextInput(c.ToString());
                _inputMs += System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                _inputCount++;
                if (_inputCount % 40 == 0 && FolioEditor.Perf("timing"))
                {
                    AppPaths.Log($"Tippen: {_inputMs / 40:0.00} ms je Zeichen synchron, davon Analyse {FolioEditor.AnalysisMs / 40:0.00} ms");
                    _inputMs = 0;
                    FolioEditor.AnalysisMs = 0;
                }
            }
        };
        t.Start();
    }

    /// <summary>Nur für Testbilder: FOLIO_TEST_SCENE=menu,dialog,source,read,info,newtab,end – Zustände ohne Klicks herstellen.</summary>
    void RunTestScene()
    {
        var scene = (Environment.GetEnvironmentVariable("FOLIO_TEST_SCENE") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var s in scene)
        {
            switch (s)
            {
                case "newtab": NewTab(); break;
                case "source": SetModeFromKey(EditorMode.Source); break;
                case "read": SetModeFromKey(EditorMode.Read); break;
                case "end": _editor.CaretOffset = _editor.Document.TextLength; _editor.ScrollToEnd(); break;
                case "info":
                    ShowInfo("", "Folio installieren?", "Mit Startmenü-Eintrag, „Öffnen mit“ für .md/.txt und automatischen Updates. Ohne Adminrechte.", null,
                        ("Später", HideInfo, false), ("Installieren …", HideInfo, true));
                    break;
                case "dialog": _ = ShowDialog("Wo sollen deine Notizen liegen?",
                    $"Neue Notizen speichert Folio in einem festen Ordner – einmal festlegen, danach reicht Strg+S.\n\nVorschlag: {DefaultNotesFolder}\n\nÄndern kannst du ihn später im Menü.",
                    null, "Diesen Ordner nehmen", "Anderen wählen …", "Abbrechen"); break;
                case "menu": Dispatcher.BeginInvoke(DispatcherPriority.Loaded, OpenMenu); break;
                case "toast": ShowToast("Gespeichert"); break;
                case "save": _ = Save(_current); break;
                case var b when b.StartsWith("bpmn:"): _ = ExportBpmn(b[5..]); break;
                case var i when i.StartsWith("image:"): _ = ExportDiagramImage(i[6..]); break;
                case var v when v.StartsWith("viewer"):
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                    {
                        if (_editor.DiagramNearCaret(d => d is not Diagrams.DiagramError) is { } f) OpenViewer(f.Source);
                        // viewer:zoom=1.5 → nach dem Einpassen auf eine feste Stufe (für Testbilder)
                        if (v.Contains("zoom=") && double.TryParse(v[(v.IndexOf("zoom=") + 5)..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z))
                            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => _viewer?.ZoomBy(z, animate: false)));
                    }));
                    break;
                case var l when l.StartsWith("line") && int.TryParse(l[4..], out var ln):
                    _editor.CaretOffset = _editor.Document.GetLineByNumber(Math.Clamp(ln, 1, _editor.Document.LineCount)).Offset;
                    _editor.ScrollTo(ln, 0);
                    break;
                case "help": if (!_helpOpen) ToggleHelp(animate: false); break;
                case "search": _search.SearchPattern = "Folio"; _search.Open(); break;
            }
        }
    }

    /// <summary>Einzelnes Element (z. B. das Menü im Popup, das nicht im Fensterbild liegt) als PNG.</summary>
    static void SaveElementSnapshot(FrameworkElement el, string path)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(el);
            var rtb = new RenderTargetBitmap((int)(el.ActualWidth * dpi.DpiScaleX) + 2, (int)(el.ActualHeight * dpi.DpiScaleY) + 2, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            rtb.Render(el);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(path);
            enc.Save(fs);
        }
        catch (Exception ex) { AppPaths.Log("Snapshot Element: " + ex.Message); }
    }

    void SaveSnapshot(string path)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(Root);
            var rtb = new RenderTargetBitmap((int)(Root.ActualWidth * dpi.DpiScaleX), (int)(Root.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(ThemeManager.Current.Window), null, new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));
                dc.DrawRectangle(new VisualBrush(Root), null, new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));
            }
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(path);
            enc.Save(fs);
        }
        catch (Exception ex)
        {
            AppPaths.Log("Snapshot: " + ex);
        }
    }

    // ───────────────────────────── Fensterrahmen, Mica ─────────────────────────────

    void ApplyWindowFrame()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int round = 2; // DWMWCP_ROUND
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        _mica = Win32.SupportsMica && _snapshotPath == null && Environment.GetEnvironmentVariable("FOLIO_NO_MICA") != "1";
        if (_mica)
        {
            if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } ct }) ct.BackgroundColor = Colors.Transparent;
            int type = Win32.DWMSBT_MAINWINDOW;
            Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int));
            // Mit bis in den Inhalt verlängertem Rahmen zeichnet Windows sonst seine eigenen Fensterknöpfe über unsere.
            long style = Win32.GetWindowLongPtr(hwnd, Win32.GWL_STYLE).ToInt64();
            Win32.SetWindowLongPtr(hwnd, Win32.GWL_STYLE, new IntPtr(style & ~Win32.WS_SYSMENU));
        }
        ApplyFrameTheme();
    }

    void ApplyFrameTheme()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = ThemeManager.Current.IsDark ? 1 : 0;
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        // Mit Mica ist der Fensterhintergrund durchsichtig – nur ein Hauch Farbe darüber, damit Tabs und Statusleiste ruhig wirken.
        Root.Background = _mica
            ? new SolidColorBrush(ThemeManager.WithAlpha(ThemeManager.Current.Window, (byte)(ThemeManager.Current.IsDark ? 0x60 : 0x50)))
            : (Brush)FindResource("WindowBrush");
    }

    void OnStateChanged()
    {
        bool max = WindowState == WindowState.Maximized;
        // Maximierte Fenster ragen mit WindowChrome um den Rahmen über den Bildschirm hinaus.
        var b = SystemParameters.WindowResizeBorderThickness;
        Root.Margin = max ? new Thickness(b.Left + 4, b.Top + 4, b.Right + 4, b.Bottom + 4) : new Thickness(0);
        MaxButton.Content = max ? "" : "";
        MaxButton.ToolTip = max ? "Verkleinern" : "Maximieren";
        if (WindowState != WindowState.Minimized) _settings.WinMaximized = max;
    }

    void RestoreWindowBounds()
    {
        if (_settings.WinWidth is > 400 && _settings.WinHeight is > 300 && _settings.WinLeft is { } l && _settings.WinTop is { } t)
        {
            var area = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (area.IntersectsWith(new Rect(l, t, _settings.WinWidth.Value, _settings.WinHeight.Value)))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = l;
                Top = t;
                Width = _settings.WinWidth.Value;
                Height = _settings.WinHeight.Value;
            }
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (_settings.WinMaximized) WindowState = WindowState.Maximized;
    }

    void SaveWindowBounds()
    {
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (r.Width > 0 && r.Height > 0)
        {
            _settings.WinLeft = r.Left;
            _settings.WinTop = r.Top;
            _settings.WinWidth = r.Width;
            _settings.WinHeight = r.Height;
        }
        _settings.WinMaximized = WindowState == WindowState.Maximized;
    }

    void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = _settings.WinMaximized ? WindowState.Maximized : WindowState.Normal;
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        _editor.Focus();
    }

    /// <summary>Markdown-Text steht in einer angenehm schmalen Spalte in der Mitte, Code nutzt die ganze Breite.</summary>
    void UpdateEditorPadding()
    {
        // Ist der Spickzettel offen, weicht der Text nach links aus und bleibt im freien Bereich zentriert.
        double reserve = HelpReserve;
        double w = EditorHost.ActualWidth - reserve;
        double side = _editor.IsMarkdown ? Math.Max(36, (w - 760) / 2) : 6;
        // Die Randspalte (Zitatbalken, Codeflächen) liegt links neben dem Text – sie gehört zum Seitenrand.
        double gutter = _editor.LiveFormatting ? MarkdownGutter.GutterWidth : 0;
        _editor.Padding = new Thickness(Math.Max(4, side - gutter), 22, Math.Max(6, side - 12) + reserve, 0);
    }

    // ───────────────────────────── Thema ─────────────────────────────

    void ToggleTheme()
    {
        var next = ThemeManager.Current.IsDark ? FolioTheme.Light : FolioTheme.Dark;
        _settings.Theme = next.Id;
        _settings.Save();

        // Standbild des alten Looks über alles legen, darunter umschalten, dann das Standbild ausblenden
        bool fade = Root.ActualWidth > 0 && IsLoaded;
        if (fade)
        {
            var dpi = VisualTreeHelper.GetDpi(Root);
            var bmp = new RenderTargetBitmap(
                Math.Max(1, (int)Math.Ceiling(Root.ActualWidth * dpi.DpiScaleX)), Math.Max(1, (int)Math.Ceiling(Root.ActualHeight * dpi.DpiScaleY)),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            ThemeFade.Visibility = Visibility.Collapsed;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                // Mica lässt sich nicht abfotografieren – darunter die Fensterfarbe, damit das Standbild nicht durchsichtig ist
                dc.DrawRectangle(new SolidColorBrush(ThemeManager.Current.Window), null, new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));
                dc.DrawRectangle(new VisualBrush(Root), null, new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));
            }
            bmp.Render(dv);
            bmp.Freeze();
            ThemeFade.Source = bmp;
            ThemeFade.Margin = Root.Margin;
            ThemeFade.BeginAnimation(OpacityProperty, null);
            ThemeFade.Opacity = 1;
            ThemeFade.Visibility = Visibility.Visible;
        }

        ThemeManager.Apply(next);

        if (!fade) return;
        var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(520))
        {
            BeginTime = TimeSpan.FromMilliseconds(40),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        anim.Completed += (_, _) =>
        {
            if (ThemeFade.Opacity > 0.001) return; // inzwischen erneut umgeschaltet
            ThemeFade.Visibility = Visibility.Collapsed;
            ThemeFade.Source = null;
        };
        ThemeFade.BeginAnimation(OpacityProperty, anim);
    }

    void OnThemeChanged()
    {
        if (ViewerOpen) RefreshViewer(keepView: true);
        _search.MarkerBrush = new SolidColorBrush(ThemeManager.WithAlpha(ThemeManager.Current.Amber, 0x66));
        ApplyFrameTheme();
        SyncThemeButton();
        DialogDim.Background = new SolidColorBrush(ThemeManager.Current.IsDark ? Color.FromArgb(0x80, 0, 0, 0) : Color.FromArgb(0x40, 0x10, 0x18, 0x28));
        foreach (var t in _tabs) RefreshChip(t);
    }

    void SyncThemeButton()
    {
        bool dark = ThemeManager.Current.IsDark;
        ThemeButton.Content = dark ? "" : "";
        ThemeButton.ToolTip = dark ? "Hell" : "Dunkel";
    }

    // ───────────────────────────── Modus ─────────────────────────────

    void SetMode(EditorMode mode)
    {
        if (_current == null) return;
        _current.Mode = mode;
        _editor.SetMode(mode);
        UpdateEditorPadding();
        MoveModeHighlight(true);
        ScheduleSessionSave();
    }

    void SyncModeButtons()
    {
        var mode = _current?.Mode ?? EditorMode.Live;
        bool md = _editor.IsMarkdown;
        ModeLive.Visibility = md ? Visibility.Visible : Visibility.Collapsed;
        if (!md && mode == EditorMode.Live) mode = EditorMode.Source;
        ModeSource.Content = md ? "Quelltext" : "Bearbeiten";
        ModeLive.IsChecked = mode == EditorMode.Live;
        ModeSource.IsChecked = mode == EditorMode.Source;
        ModeRead.IsChecked = mode == EditorMode.Read;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => MoveModeHighlight(false));
    }

    void MoveModeHighlight(bool animate)
    {
        var target = ModeLive.IsChecked == true ? ModeLive : ModeSource.IsChecked == true ? ModeSource : ModeRead;
        if (!target.IsVisible || target.ActualWidth <= 0) return;
        var p = target.TranslatePoint(new Point(0, 0), ModeSwitch);
        double top = (ModeSwitch.ActualHeight - ModeHighlight.Height) / 2;
        Canvas.SetTop(ModeHighlight, top);
        Slide(ModeHighlight, p.X, target.ActualWidth, animate ? 260 : 0);
    }

    /// <summary>Element auf einer Canvas waagerecht gleiten lassen (Position und Breite).</summary>
    static void Slide(FrameworkElement el, double x, double width, int ms)
    {
        if (ms <= 0 || double.IsNaN(Canvas.GetLeft(el)))
        {
            el.BeginAnimation(Canvas.LeftProperty, null);
            el.BeginAnimation(WidthProperty, null);
            Canvas.SetLeft(el, x);
            el.Width = width;
            return;
        }
        var d = TimeSpan.FromMilliseconds(ms);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        el.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(x, d) { EasingFunction = ease });
        el.BeginAnimation(WidthProperty, new DoubleAnimation(width, d) { EasingFunction = ease });
    }

    // ───────────────────────────── Statusleiste ─────────────────────────────

    void ScheduleStatus()
    {
        _statusTimer.Stop();
        _statusTimer.Start();
        UpdatePosition();
    }

    void UpdatePosition()
    {
        var c = _editor.TextArea.Caret;
        int sel = _editor.SelectionLength;
        StatusPosition.Text = sel > 0 ? $"{sel} Zeichen markiert" : $"Zeile {c.Line}, Spalte {c.Column}";
    }

    static readonly Regex WordRegex = new(@"[\p{L}\p{N}][\p{L}\p{N}'’\-]*", RegexOptions.Compiled);

    void UpdateStatus()
    {
        UpdatePosition();
        var doc = _editor.Document;
        if (doc == null) return;
        int words = doc.TextLength > 2_000_000 ? -1 : WordRegex.Matches(doc.Text).Count;
        StatusWords.Text = words < 0 ? "" : words == 1 ? "1 Wort" : $"{words:N0} Wörter";
        StatusEncoding.Text = _current != null ? TextFiles.DisplayName(_current.Encoding) : "";
        LineEndingButton.Content = DetectLineEnding() == "\n" ? "LF" : "CRLF";
        double zoom = _editor.FontSize / 16.0 * 100;
        ZoomButton.Visibility = Math.Abs(zoom - 100) > 0.5 ? Visibility.Visible : Visibility.Collapsed;
        ZoomButton.Content = $"{zoom:0} %";
    }

    string DetectLineEnding()
    {
        var doc = _editor.Document;
        if (doc == null || doc.LineCount < 2) return Environment.NewLine;
        return doc.GetText(doc.GetLineByNumber(1).EndOffset, doc.GetLineByNumber(1).DelimiterLength) == "\n" ? "\n" : "\r\n";
    }

    void ToggleLineEndings()
    {
        var doc = _editor.Document;
        if (doc == null || _editor.IsReadOnly) return;
        string target = DetectLineEnding() == "\n" ? "\r\n" : "\n";
        doc.BeginUpdate();
        try
        {
            for (int n = doc.LineCount - 1; n >= 1; n--)
            {
                var l = doc.GetLineByNumber(n);
                if (l.DelimiterLength > 0) doc.Replace(l.EndOffset, l.DelimiterLength, target);
            }
        }
        finally { doc.EndUpdate(); }
        UpdateStatus();
        ShowToast(target == "\n" ? "Zeilenende: LF (Linux/macOS)" : "Zeilenende: CRLF (Windows)");
    }

    void Zoom(int dir)
    {
        double size = dir == 0 ? 16 : Math.Clamp(_editor.FontSize + dir, 10, 40);
        if (Math.Abs(size - _editor.FontSize) < 0.01) return;
        _editor.FontSize = size;
        _settings.FontSize = size;
        _settings.Save();
        _editor.TextArea.TextView.Redraw();
        UpdateStatus();
    }

    // ───────────────────────────── Tastatur ─────────────────────────────

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (DialogLayer.Visibility == Visibility.Visible)
        {
            if (key == Key.Escape) { _dialogResult?.TrySetResult(-1); e.Handled = true; }
            return;
        }
        if (ViewerKey(key, mods)) { e.Handled = true; return; }
        if (mods == ModifierKeys.None && key == Key.F1) { ToggleHelp(); e.Handled = true; return; }
        if (mods == ModifierKeys.Control)
        {
            switch (key)
            {
                case Key.N: NewTab(); e.Handled = true; return;
                case Key.O: _ = OpenWithDialog(); e.Handled = true; return;
                case Key.S: _ = Save(_current); e.Handled = true; return;
                case Key.W: _ = CloseTab(_current); e.Handled = true; return;
                case Key.Tab: SelectRelative(1); e.Handled = true; return;
                case Key.B when _editor.IsMarkdown: _editor.ToggleWrap("**"); e.Handled = true; return;
                case Key.I when _editor.IsMarkdown: _editor.ToggleWrap("*"); e.Handled = true; return;
                case Key.E: SetModeFromKey(_current?.Mode == EditorMode.Read ? EditorMode.Live : EditorMode.Read); e.Handled = true; return;
                case Key.D0 or Key.NumPad0: Zoom(0); e.Handled = true; return;
                case Key.OemPlus or Key.Add: Zoom(1); e.Handled = true; return;
                case Key.OemMinus or Key.Subtract: Zoom(-1); e.Handled = true; return;
            }
        }
        if (mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            switch (key)
            {
                case Key.S: _ = SaveAs(_current); e.Handled = true; return;
                case Key.Tab: SelectRelative(-1); e.Handled = true; return;
                case Key.E: SetModeFromKey(_current?.Mode == EditorMode.Source ? EditorMode.Live : EditorMode.Source); e.Handled = true; return;
                case Key.X when _editor.IsMarkdown: _editor.ToggleWrap("~~"); e.Handled = true; return;
                case Key.T: ReopenClosedTab(); e.Handled = true; return;
            }
        }
    }

    void SetModeFromKey(EditorMode mode)
    {
        if (!_editor.IsMarkdown && mode == EditorMode.Live) mode = EditorMode.Source;
        if (mode == EditorMode.Live) ModeLive.IsChecked = true;
        else if (mode == EditorMode.Source) ModeSource.IsChecked = true;
        else ModeRead.IsChecked = true;
    }

    // ───────────────────────────── Menü ─────────────────────────────

    void OpenMenu()
    {
        MenuItems.Children.Clear();
        AddMenu("", "Neue Notiz", "Strg+N", () => NewTab());
        AddMenu("", "Öffnen …", "Strg+O", () => _ = OpenWithDialog());
        AddMenu("", "Speichern", "Strg+S", () => _ = Save(_current));
        AddMenu("", "Speichern unter …", "Strg+Umschalt+S", () => _ = SaveAs(_current));
        AddSeparator();
        AddMenu("", "Notizen-Ordner öffnen", null, OpenNotesFolder);
        AddMenu("", "Ablauf als BPMN exportieren (Camunda) …", null, () => _ = ExportBpmn());
        AddMenu("", "Diagramm vergrößern", null, () =>
        {
            if (_editor.DiagramNearCaret(d => d is not Diagrams.DiagramError) is { } f) OpenViewer(f.Source);
            else _ = ShowDialog("Kein Diagramm gefunden", "Setz den Cursor in einen ```mermaid-Block.", null, "OK");
        });
        AddMenu("", "Diagramm als Bild speichern (PNG/SVG) …", null, () => _ = ExportDiagramImage());
        AddMenu("", "Notizen-Ordner ändern …", null, () => _ = ChangeNotesFolder());
        if (_current?.FilePath != null) AddMenu("", "Im Explorer zeigen", null, () => RevealInExplorer(_current.FilePath));
        AddSeparator();
        if (!_editor.IsMarkdown)
            AddToggle("Zeilen umbrechen", _settings.WordWrap, v => { _settings.WordWrap = v; _settings.Save(); _editor.WrapCode = v; _editor.WordWrap = v; });
        AddToggle("Weiche Animationen beim Schreiben", _settings.SmoothTyping, v => { _settings.SmoothTyping = v; _settings.Save(); _editor.SmoothTyping = v; });
        if (!Installer.IsRunningInstalled)
            AddMenu("", Installer.IsInstalled ? "Diese Version installieren …" : "Folio installieren …", null, () => _ = AskInstall());
        else
            AddToggle("Verknüpfung auf dem Desktop", Installer.HasDesktopLink, v => SetDesktopLink(v));
        AddToggle("Beim Start nach Updates suchen", _settings.UpdateCheck, v => { _settings.UpdateCheck = v; _settings.Save(); });
        AddSeparator();
        var about = new TextBlock
        {
            Text = $"Folio {AppPaths.Version}",
            FontSize = 11.5,
            Foreground = (Brush)FindResource("TextFaintBrush"),
            Margin = new Thickness(18, 6, 12, 6),
        };
        MenuItems.Children.Add(about);

        MenuPopup.IsOpen = true;
        var d = TimeSpan.FromMilliseconds(180);
        MenuCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d));
        MenuScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, d) { EasingFunction = EaseOut });
        MenuScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, d) { EasingFunction = EaseOut });
    }

    void AddMenu(string icon, string text, string? shortcut, Action action)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new TextBlock { Text = icon, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("TextDimBrush") });
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        g.Children.Add(label);
        if (shortcut != null)
        {
            var sc = new TextBlock { Text = shortcut, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("TextFaintBrush"), Margin = new Thickness(12, 0, 0, 0) };
            Grid.SetColumn(sc, 2);
            g.Children.Add(sc);
        }
        var b = new Button { Style = (Style)FindResource("MenuItemButton"), Content = g };
        b.Click += (_, _) => { MenuPopup.IsOpen = false; action(); };
        MenuItems.Children.Add(b);
    }

    void AddToggle(string text, bool value, Action<bool> set)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var check = new TextBlock { Text = value ? "" : "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("AccentBrush") };
        g.Children.Add(check);
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        g.Children.Add(label);
        var b = new Button { Style = (Style)FindResource("MenuItemButton"), Content = g };
        b.Click += (_, _) => { MenuPopup.IsOpen = false; set(!value); };
        MenuItems.Children.Add(b);
    }

    void AddSeparator() =>
        MenuItems.Children.Add(new Border { Height = 1, Margin = new Thickness(12, 5, 12, 5), Background = (Brush)FindResource("PaperBorderBrush") });

    // ───────────────────────────── Dialog, Hinweisleiste, Toast ─────────────────────────────

    Task<int> ShowDialog(string title, string text, IEnumerable<UIElement>? extra, params string[] buttons)
    {
        _dialogResult?.TrySetResult(-1);
        var tcs = new TaskCompletionSource<int>();
        _dialogResult = tcs;
        DialogTitle.Text = title;
        DialogText.Text = text;
        DialogText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DialogExtra.Children.Clear();
        if (extra != null) foreach (var x in extra) DialogExtra.Children.Add(x);
        DialogExtra.Visibility = DialogExtra.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DialogButtons.Children.Clear();
        for (int i = buttons.Length - 1; i >= 0; i--)
        {
            int idx = i;
            var b = new Button
            {
                Content = buttons[i],
                Style = (Style)FindResource(i == 0 ? "PrimaryButton" : "GhostButton"),
                Margin = new Thickness(8, 0, 0, 0),
            };
            b.Click += (_, _) => tcs.TrySetResult(idx);
            DialogButtons.Children.Add(b);
        }
        DialogLayer.Visibility = Visibility.Visible;
        DialogLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        DialogScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.95, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = EaseOut });
        DialogScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.95, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = EaseOut });
        if (DialogButtons.Children.Count > 0) Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => ((Button)DialogButtons.Children[^1]).Focus()));
        return tcs.Task.ContinueWith(t =>
        {
            if (_dialogResult == tcs)
            {
                _dialogResult = null;
                var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(130));
                fade.Completed += (_, _) => { if (_dialogResult == null) DialogLayer.Visibility = Visibility.Collapsed; };
                DialogLayer.BeginAnimation(OpacityProperty, fade);
                _editor.Focus();
            }
            return t.Result;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    Action? _infoOnClose;
    /// <summary>Der Hinweis gehört zum aktuellen Tab (Datei außen geändert) und verschwindet beim Tabwechsel.</summary>
    bool _infoForTab;

    /// <summary>Hinweisleiste oben im Papier. Knöpfe: (Text, Aktion, Hauptknopf?)</summary>
    void ShowInfo(string icon, string title, string text, Action? onClose, params (string Text, Action Act, bool Primary)[] buttons)
    {
        InfoIcon.Text = icon;
        InfoTitle.Text = title;
        InfoText.Text = text;
        InfoText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        InfoButtons.Children.Clear();
        foreach (var (label, act, primary) in buttons)
        {
            var b = new Button { Content = label, Style = (Style)FindResource(primary ? "PrimaryButton" : "GhostButton"), Padding = new Thickness(12, 5, 12, 5), FontSize = 12, Margin = new Thickness(6, 0, 0, 0) };
            b.Click += (_, _) => act();
            InfoButtons.Children.Add(b);
        }
        _infoOnClose = onClose;
        _infoForTab = false;
        bool wasVisible = InfoBar.Visibility == Visibility.Visible;
        InfoBar.Visibility = Visibility.Visible;
        if (!wasVisible)
        {
            InfoBar.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
            InfoShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-14, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = EaseOut });
        }
    }

    void HideInfo()
    {
        if (InfoBar.Visibility != Visibility.Visible) return;
        var close = _infoOnClose;
        _infoOnClose = null;
        var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        a.Completed += (_, _) => { if (InfoBar.Opacity < 0.01) InfoBar.Visibility = Visibility.Collapsed; };
        InfoBar.BeginAnimation(OpacityProperty, a);
        InfoShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-10, TimeSpan.FromMilliseconds(160)));
        close?.Invoke();
    }

    DispatcherTimer? _toastTimer;

    void ShowToast(string text, string icon = "")
    {
        ToastText.Text = text;
        ToastIcon.Text = icon;
        Toast.Visibility = Visibility.Visible;
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        ToastShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = EaseOut });
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1700) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer!.Stop();
            var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(260));
            a.Completed += (_, _) => { if (Toast.Opacity < 0.01) Toast.Visibility = Visibility.Collapsed; };
            Toast.BeginAnimation(OpacityProperty, a);
        };
        _toastTimer.Start();
    }

    static void OpenLink(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "mailto")) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
    }

    // ───────────────────────────── Schließen ─────────────────────────────

    void OnClosing(object? sender, CancelEventArgs e)
    {
        // Nichts fragen: offene Tabs samt ungespeicherter Änderungen bleiben in der Sitzung erhalten.
        if (_current != null) RememberView(_current);
        SaveSession();
        SaveWindowBounds();
        _settings.Save();
        _ipc.Dispose();
    }
}
