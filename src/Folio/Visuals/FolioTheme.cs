using System;
using System.Windows;
using System.Windows.Media;

namespace Folio.Visuals;

/// <summary>
/// Farbpalette eines Themas. Folio hat genau zwei: Hell (Standard) und Dunkel.
/// Eine Akzentfarbe („Tinte“), sonst nur Abstufungen von Papier und Text.
/// </summary>
public sealed class FolioTheme
{
    public string Id { get; init; } = "";
    public bool IsDark { get; init; }

    /// <summary>Fensterhintergrund hinter Tabs und Statusleiste (wenn kein Mica verfügbar ist).</summary>
    public Color Window { get; init; }
    /// <summary>Schreibfläche.</summary>
    public Color Paper { get; init; }
    public Color PaperBorder { get; init; }
    public Color Text { get; init; }
    public Color TextDim { get; init; }
    public Color TextFaint { get; init; }
    public Color Heading { get; init; }
    public Color Accent { get; init; }
    public Color AccentHover { get; init; }
    public Color OnAccent { get; init; }
    /// <summary>Zweitfarbe nur fürs Logo-Lesezeichen und erledigte Häkchen-Glanz.</summary>
    public Color Amber { get; init; }
    public Color Hover { get; init; }
    public Color Pressed { get; init; }
    public Color Selection { get; init; }
    public Color CodeBack { get; init; }
    public Color CodeText { get; init; }
    public Color QuoteBar { get; init; }
    public Color Rule { get; init; }
    public Color Danger { get; init; }

    public static readonly FolioTheme Light = new()
    {
        Id = "hell",
        IsDark = false,
        Window = C("#EEF0F4"),
        Paper = C("#FFFFFF"),
        PaperBorder = C("#E3E6EC"),
        Text = C("#1E222A"),
        TextDim = C("#5D6573"),
        TextFaint = C("#9AA1AD"),
        Heading = C("#121621"),
        Accent = C("#3D5AD6"),
        AccentHover = C("#3249B8"),
        OnAccent = C("#FFFFFF"),
        Amber = C("#F0A73A"),
        Hover = C("#0F1E2A4A"),
        Pressed = C("#1A1E2A4A"),
        Selection = C("#553D5AD6"),
        CodeBack = C("#F3F5F9"),
        CodeText = C("#2C3446"),
        QuoteBar = C("#C5CFF4"),
        Rule = C("#DDE1E8"),
        Danger = C("#C42B1C"),
    };

    public static readonly FolioTheme Dark = new()
    {
        Id = "dunkel",
        IsDark = true,
        Window = C("#191B20"),
        Paper = C("#202228"),
        PaperBorder = C("#2C2F37"),
        Text = C("#E3E6EC"),
        TextDim = C("#A3AAB6"),
        TextFaint = C("#6C7380"),
        Heading = C("#F3F5F9"),
        Accent = C("#8C9FFF"),
        AccentHover = C("#A3B2FF"),
        OnAccent = C("#11131A"),
        Amber = C("#F3B65A"),
        Hover = C("#16FFFFFF"),
        Pressed = C("#22FFFFFF"),
        Selection = C("#668C9FFF"),
        CodeBack = C("#292C34"),
        CodeText = C("#D5DAE3"),
        QuoteBar = C("#46508A"),
        Rule = C("#363A44"),
        Danger = C("#E5584A"),
    };

    public static FolioTheme ById(string? id) => id == Dark.Id ? Dark : Light;

    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}

/// <summary>Setzt die Pinsel eines Themas in die App-Ressourcen. Alles in XAML hängt per DynamicResource daran.</summary>
public static class ThemeManager
{
    public static FolioTheme Current { get; private set; } = FolioTheme.Light;

    public static event Action? Changed;

    public static void Apply(FolioTheme t)
    {
        Current = t;
        var r = Application.Current.Resources;
        Set(r, "WindowBrush", t.Window);
        Set(r, "PaperBrush", t.Paper);
        Set(r, "PaperBorderBrush", t.PaperBorder);
        Set(r, "TextBrush", t.Text);
        Set(r, "TextDimBrush", t.TextDim);
        Set(r, "TextFaintBrush", t.TextFaint);
        Set(r, "AccentBrush", t.Accent);
        Set(r, "AccentHoverBrush", t.AccentHover);
        Set(r, "OnAccentBrush", t.OnAccent);
        Set(r, "AmberBrush", t.Amber);
        Set(r, "HoverBrush", t.Hover);
        Set(r, "PressedBrush", t.Pressed);
        Set(r, "DangerBrush", t.Danger);
        Set(r, "Accent14Brush", WithAlpha(t.Accent, 0x14));
        Set(r, "Accent24Brush", WithAlpha(t.Accent, 0x24));
        Set(r, "ShadowColorBrush", t.IsDark ? Color.FromRgb(0, 0, 0) : Color.FromRgb(0x1E, 0x2A, 0x4A));
        r["ShadowColor"] = t.IsDark ? Color.FromRgb(0, 0, 0) : Color.FromRgb(0x1E, 0x2A, 0x4A);
        r["ShadowOpacity"] = t.IsDark ? 0.45 : 0.10;
        Changed?.Invoke();
    }

    public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    static void Set(ResourceDictionary r, string key, Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        r[key] = b;
    }
}
