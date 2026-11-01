using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Folio.Diagrams;

/// <summary>Zeitangaben wie „5 s“, „2 min“, „1,5 h“, „1 h 30 min“, „2 Tage“ lesen und lesbar ausgeben.</summary>
public static class Durations
{
    static readonly Regex Part = new(@"(\d+(?:[.,]\d+)?)\s*(sekunden|sekunde|sek|sec|s|minuten|minute|min|m|stunden|stunde|std|h|tage|tag|t|d|wochen|woche|wo|w)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Liest eine Dauer. Ohne Einheit gilt Minuten. Null, wenn nichts Lesbares drinsteht.</summary>
    public static TimeSpan? Parse(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        double seconds = 0;
        bool any = false;
        foreach (Match m in Part.Matches(text))
        {
            double v = double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            string u = m.Groups[2].Value.ToLowerInvariant();
            seconds += v * u switch
            {
                "sekunden" or "sekunde" or "sek" or "sec" or "s" => 1,
                "minuten" or "minute" or "min" or "m" => 60,
                "stunden" or "stunde" or "std" or "h" => 3600,
                "tage" or "tag" or "t" or "d" => 86400,
                _ => 604800, // Wochen
            };
            any = true;
        }
        if (!any && double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var plain))
        {
            seconds = plain * 60;
            any = true;
        }
        return any ? TimeSpan.FromSeconds(seconds) : null;
    }

    /// <summary>Kurz und lesbar: „45 s“, „2 min“, „1 h 30 min“, „2 Tage 3 h“.</summary>
    public static string Format(TimeSpan t)
    {
        if (t.TotalSeconds < 1) return t.TotalMilliseconds > 0 ? "< 1 s" : "0 s";
        if (t.TotalMinutes < 1) return $"{Math.Round(t.TotalSeconds):0} s";
        if (t.TotalHours < 1)
        {
            int s = t.Seconds;
            return s == 0 ? $"{(int)t.TotalMinutes} min" : $"{(int)t.TotalMinutes} min {s} s";
        }
        if (t.TotalDays < 1)
        {
            int m = t.Minutes;
            return m == 0 ? $"{(int)t.TotalHours} h" : $"{(int)t.TotalHours} h {m} min";
        }
        int d = (int)t.TotalDays, h = t.Hours;
        string days = d == 1 ? "1 Tag" : $"{d} Tage";
        return h == 0 ? days : $"{days} {h} h";
    }
}
