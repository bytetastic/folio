using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Folio.Editor;

/// <summary>Schriften, Größen und Zeilenabstände des Editors an einer Stelle.</summary>
public static class Typography
{
    public static readonly FontFamily Body = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    public static readonly Typeface IconFace = new(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>Größe einer Überschrift relativ zum Fließtext.</summary>
    public static double HeadingScale(int level) => level switch
    {
        1 => 1.85,
        2 => 1.45,
        3 => 1.22,
        4 => 1.08,
        _ => 1.0,
    };

    /// <summary>Code (Block und Inline) etwas kleiner als der Fließtext – Monospace wirkt sonst zu groß.</summary>
    public const double CodeScale = 0.9;

    static readonly Dictionary<(FontFamily, FontStyle, FontWeight), Typeface> Cache = new();

    public static Typeface Face(FontFamily family, FontStyle style, FontWeight weight)
    {
        var key = (family, style, weight);
        if (!Cache.TryGetValue(key, out var t)) Cache[key] = t = new Typeface(family, style, weight, FontStretches.Normal);
        return t;
    }

    /// <summary>Oberlänge und Unterlänge einer Schrift in Pixeln (für Zeilenhöhe und Cursor).</summary>
    public static (double Ascent, double Descent) Metrics(FontFamily family, double size)
    {
        double asc = family.Baseline * size;
        double desc = (family.LineSpacing - family.Baseline) * size;
        return (asc, desc);
    }
}
