using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Folio.Visuals;

namespace Folio;

/// <summary>Markdown-Spickzettel (F1): was man tippt, wie es aussieht – ein Klick fügt es an der Cursorstelle ein.</summary>
public partial class MainWindow
{
    bool _helpOpen;
    bool _helpBuilt;

    void WireHelp()
    {
        HelpButton.Click += (_, _) => ToggleHelp();
        HelpClose.Click += (_, _) => ToggleHelp();
        ThemeManager.Changed += () => { if (_helpBuilt) BuildHelp(); };
        if (_settings.HelpOpen) ToggleHelp(animate: false);
    }

    void ToggleHelp(bool animate = true)
    {
        _helpOpen = !_helpOpen;
        _settings.HelpOpen = _helpOpen;
        _settings.Save();
        if (!_helpBuilt) BuildHelp();
        var d = TimeSpan.FromMilliseconds(animate ? 280 : 0);
        if (_helpOpen)
        {
            HelpPanel.Visibility = Visibility.Visible;
            HelpPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d));
            HelpShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(36, 0, d) { EasingFunction = EaseOut });
        }
        else
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(animate ? 180 : 0));
            fade.Completed += (_, _) => { if (!_helpOpen) HelpPanel.Visibility = Visibility.Collapsed; };
            HelpPanel.BeginAnimation(OpacityProperty, fade);
            HelpShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(24, TimeSpan.FromMilliseconds(animate ? 180 : 0)));
        }
        HelpButton.SetResourceReference(ForegroundProperty, _helpOpen ? "AccentBrush" : "TextDimBrush");
        UpdateEditorPadding();
        _editor.Focus();
    }

    /// <summary>Platz, den der Spickzettel rechts belegt (der Text weicht ihm aus).</summary>
    double HelpReserve => _helpOpen ? HelpPanel.Width + 20 : 0;

    void BuildHelp()
    {
        _helpBuilt = true;
        HelpItems.Children.Clear();

        Section("Überschriften");
        Row("# Titel", Text("Überschrift 1", 17, FontWeights.SemiBold, display: true), () => _editor.SetLinePrefix("# "));
        Row("## Titel", Text("Überschrift 2", 15, FontWeights.SemiBold, display: true), () => _editor.SetLinePrefix("## "));
        Row("### Titel", Text("Überschrift 3", 13.5, FontWeights.SemiBold, display: true), () => _editor.SetLinePrefix("### "));

        Section("Text");
        Row("**fett**", Text("fett", 13, FontWeights.Bold), () => _editor.ToggleWrap("**"), "Strg+B");
        Row("*kursiv*", Text("kursiv", 13, italic: true), () => _editor.ToggleWrap("*"), "Strg+I");
        Row("~~weg~~", Text("weg", 13, strike: true), () => _editor.ToggleWrap("~~"), "Strg+Umschalt+X");
        Row("`code`", CodeChip("code"), () => _editor.ToggleWrap("`"));
        Row("[Text](https://…)", Text("Text", 13, link: true), () => _editor.InsertLink());

        Section("Listen");
        Row("- Punkt", Prefixed(Dot(), "Punkt"), () => _editor.SetLinePrefix("- "));
        Row("1. Schritt", Prefixed(Text("1.", 13, faint: true), "Schritt"), () => _editor.SetLinePrefix("1. "));
        Row("- [ ] Aufgabe", Prefixed(Box(false), "Aufgabe"), () => _editor.SetLinePrefix("- [ ] "));
        Row("- [x] erledigt", Prefixed(Box(true), "erledigt", done: true), () => _editor.SetLinePrefix("- [x] "));

        Section("Blöcke");
        Row("> Zitat", QuoteSample(), () => _editor.SetLinePrefix("> "));
        Row("```", CodeBlockSample(), () => _editor.InsertBlock("```\n\n```", 4));
        Row("---", RuleSample(), () => _editor.InsertBlock("---\n", 4));
        Row("| A | B |", Text("Tabelle", 13, faint: true), () => _editor.InsertBlock("| Spalte | Spalte |\n| --- | --- |\n| Wert | Wert |", 2, 6));

        Section("Diagramme (Mermaid)");
        Row("flowchart", Text("Ablaufdiagramm", 13), () => InsertTemplate(Diagrams.Templates.Flow));
        Row("subgraph …", Text("Ablauf mit Bahnen", 13), () => InsertTemplate(Diagrams.Templates.Swimlane));
        Row("sequenceDiagram", Text("Sequenzdiagramm", 13), () => InsertTemplate(Diagrams.Templates.Sequence));
        Row("xychart-beta", Text("Balkendiagramm", 13), () => InsertTemplate(Diagrams.Templates.Bars));
        Row("pie", Text("Tortendiagramm", 13), () => InsertTemplate(Diagrams.Templates.Pie));
        Tip("Zeiten: %% zeit B 5 min (Schritt) und %% liegezeit A B 2 h (Warten). Folio zeigt sie an den Kästen und rechnet die Durchlaufzeit aus – andere Programme ignorieren die Zeilen.");
        Row("→ Camunda", Text("Als BPMN exportieren", 13, link: true), () => _ = ExportBpmn());
        Tip("Klick ins Diagramm zeigt den Text, darunter läuft eine Vorschau mit.");
        Tip("Name des Prozesses (z. B. Pool in Camunda): ganz oben im Block ---, title: Name, --- – sonst die Überschrift über dem Diagramm.");

        Section("Vorlagen");
        Row("Steckbrief", Text("Prozess-Steckbrief", 13), () => InsertTemplate(Diagrams.Templates.ProcessProfile));
        Row("SIPOC", Text("Lieferant → Kunde", 13), () => InsertTemplate(Diagrams.Templates.Sipoc));
        Row("RACI", Text("Wer macht was", 13), () => InsertTemplate(Diagrams.Templates.Raci));
        Row("Agent", Text("Agent-Steckbrief", 13), () => InsertTemplate(Diagrams.Templates.AgentProfile));

        Section("Tipps");
        Tip("Enter in einer Liste setzt sie fort, Enter im leeren Punkt beendet sie.");
        Tip("Die Zeichen siehst du nur in der Zeile, in der der Cursor steht.");
        Tip("Strg+E Lesen · Strg+Umschalt+E Quelltext · Strg+F Suchen");
    }

    /// <summary>Vorlage als eigenen Block einfügen; Cursor danach ans Ende, damit das Diagramm sofort gezeichnet erscheint.</summary>
    void InsertTemplate(string template)
    {
        string text = template.Replace("\r\n", "\n").TrimEnd('\n') + "\n";
        _editor.InsertBlock(text + "\n", text.Length + 1);
    }

    // ── Bausteine ──

    void Section(string title) => HelpItems.Children.Add(new TextBlock
    {
        Text = title.ToUpperInvariant(),
        FontSize = 10.5,
        FontWeight = FontWeights.SemiBold,
        Foreground = Res("TextFaintBrush"),
        Margin = new Thickness(18, 14, 0, 4),
    });

    void Row(string syntax, UIElement result, Action insert, string? shortcut = null)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new TextBlock
        {
            Text = syntax,
            FontFamily = Editor.Typography.Mono,
            FontSize = 12,
            Foreground = Res("TextDimBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (result is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(result, 1);
        g.Children.Add(result);
        var b = new Button
        {
            Style = (Style)FindResource("MenuItemButton"),
            Content = g,
            Padding = new Thickness(10, 6, 10, 6),
            ToolTip = shortcut != null ? $"Einfügen ({shortcut})" : "Einfügen",
        };
        b.Click += (_, _) =>
        {
            if (_editor.IsReadOnly) { ShowToast("Im Lesemodus wird nichts eingefügt", ""); return; }
            insert();
            _editor.Focus();
        };
        HelpItems.Children.Add(b);
    }

    void Tip(string text) => HelpItems.Children.Add(new TextBlock
    {
        Text = text,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Res("TextDimBrush"),
        Margin = new Thickness(18, 2, 16, 6),
    });

    Brush Res(string key) => (Brush)FindResource(key);

    TextBlock Text(string s, double size, FontWeight? weight = null, bool italic = false, bool strike = false,
        bool link = false, bool faint = false, bool display = false)
    {
        var t = new TextBlock
        {
            Text = s,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
            Foreground = Res(link ? "AccentBrush" : faint ? "TextFaintBrush" : "TextBrush"),
        };
        if (display) t.FontFamily = (FontFamily)FindResource("DisplayFont");
        if (strike) t.TextDecorations = TextDecorations.Strikethrough;
        if (link) t.TextDecorations = TextDecorations.Underline;
        return t;
    }

    UIElement CodeChip(string s) => new Border
    {
        Background = new SolidColorBrush(ThemeManager.Current.CodeBack),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(5, 1, 5, 1),
        HorizontalAlignment = HorizontalAlignment.Left,
        Child = new TextBlock { Text = s, FontFamily = Editor.Typography.Mono, FontSize = 12, Foreground = new SolidColorBrush(ThemeManager.Current.CodeText) },
    };

    UIElement Dot() => new Ellipse { Width = 5, Height = 5, Fill = Res("TextDimBrush"), Margin = new Thickness(3, 1, 3, 0) };

    UIElement Box(bool done)
    {
        var t = ThemeManager.Current;
        var b = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(3.5),
            BorderThickness = new Thickness(done ? 0 : 1.3),
            BorderBrush = new SolidColorBrush(t.TextFaint),
            Background = new SolidColorBrush(done ? t.Accent : t.Paper),
        };
        if (done)
            b.Child = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 9, Foreground = new SolidColorBrush(t.OnAccent), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        return b;
    }

    UIElement Prefixed(UIElement marker, string text, bool done = false)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        if (marker is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        sp.Children.Add(marker);
        var t = Text(text, 13, faint: done, strike: done);
        t.Margin = new Thickness(7, 0, 0, 0);
        sp.Children.Add(t);
        return sp;
    }

    UIElement QuoteSample()
    {
        var t = ThemeManager.Current;
        return new Border
        {
            BorderBrush = new SolidColorBrush(t.QuoteBar),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(8, 1, 0, 1),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = Text("Zitat", 13, faint: false),
        };
    }

    UIElement CodeBlockSample()
    {
        var t = ThemeManager.Current;
        return new Border
        {
            Background = new SolidColorBrush(t.CodeBack),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4, 8, 4),
            Child = new TextBlock { Text = "Codeblock", FontFamily = Editor.Typography.Mono, FontSize = 12, Foreground = new SolidColorBrush(t.CodeText) },
        };
    }

    UIElement RuleSample() => new Border
    {
        Height = 1,
        Background = new SolidColorBrush(ThemeManager.Current.Rule),
        Margin = new Thickness(0, 0, 20, 0),
    };
}
