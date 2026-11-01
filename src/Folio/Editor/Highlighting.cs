using System.Collections.Generic;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;

namespace Folio.Editor;

/// <summary>Syntaxfarben für Nicht-Markdown-Dateien aus den eingebauten AvalonEdit-Definitionen, im Dunkelmodus aufgehellt.</summary>
public static class Highlighting
{
    static readonly Dictionary<HighlightingColor, Color?> Originals = new();

    public static IHighlightingDefinition? For(string extension)
    {
        string ext = extension switch
        {
            ".json" or ".jsonc" => ".js",
            ".ps1" or ".psm1" or ".psd1" => ".ps1",
            ".csproj" or ".props" or ".targets" or ".xaml" or ".config" or ".manifest" or ".svg" => ".xml",
            ".htm" => ".html",
            ".ts" or ".tsx" or ".jsx" or ".mjs" => ".js",
            _ => extension,
        };
        var def = HighlightingManager.Instance.GetDefinitionByExtension(ext);
        if (def != null) Adapt(def, Visuals.ThemeManager.Current.IsDark);
        return def;
    }

    /// <summary>Die Standardfarben sind für weißen Grund gemacht – im Dunkelmodus werden sie aufgehellt.</summary>
    public static void Adapt(IHighlightingDefinition def, bool dark)
    {
        foreach (var hc in def.NamedHighlightingColors)
        {
            if (!Originals.TryGetValue(hc, out var orig))
            {
                orig = (hc.Foreground as SimpleHighlightingBrush)?.GetColor(null);
                Originals[hc] = orig;
            }
            if (orig is not { } c) continue;
            if (hc.IsFrozen) continue;
            hc.Foreground = new SimpleHighlightingBrush(dark ? Lighten(c) : c);
        }
    }

    static Color Lighten(Color c)
    {
        // Richtung Weiß mischen, Farbton bleibt erkennbar
        const double k = 0.45;
        return Color.FromRgb((byte)(c.R + (255 - c.R) * k), (byte)(c.G + (255 - c.G) * k), (byte)(c.B + (255 - c.B) * k));
    }
}
