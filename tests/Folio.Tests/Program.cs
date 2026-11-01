using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Folio.Editor;
using Folio.Services;

namespace Folio.Tests;

/// <summary>Kleine Konsolen-Tests ohne Testframework: Exit-Code 0 = alles grün.</summary>
public static class Program
{
    static int _failed, _passed;

    public static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        foreach (string nl in new[] { "\n", "\r\n" })
        {
            string label = nl == "\n" ? "LF" : "CRLF";
            Run($"Überschrift ({label})", () => Heading(nl));
            Run($"Fett/Kursiv/Code ({label})", () => Inline(nl));
            Run($"Link ({label})", () => Link(nl));
            Run($"Aufgaben und Punkte ({label})", () => Lists(nl));
            Run($"Codeblock ({label})", () => Fenced(nl));
            Run($"Zitat ({label})", () => Quote(nl));
        }
        Run("Offener Codeblock bis Dokumentende", OpenFence);
        Run("Setext-Überschrift", Setext);
        Run("Kodierungen", Encodings);
        DiagramTests.Register(Run);

        Console.WriteLine($"\n{_passed} bestanden, {_failed} fehlgeschlagen");
        return _failed == 0 ? 0 : 1;
    }

    public static void Run(string name, Action test)
    {
        try
        {
            test();
            _passed++;
            Console.WriteLine("  ok   " + name);
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine("  FEHL " + name + ": " + ex.Message);
        }
    }

    public static void Eq<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what}: erwartet «{expected}», war «{actual}»");
    }

    static string Sub(string text, MdSpan s) => text.Substring(s.Start, s.Length);

    static List<string> Texts(string text, MarkdownAnalysis a, MdStyle style) =>
        a.Spans.Where(s => (s.Style & style) != 0).Select(s => Sub(text, s)).ToList();

    static void Heading(string nl)
    {
        string t = "Intro" + nl + "## Titel ##" + nl + "Text";
        var a = MarkdownAnalysis.Analyze(t);
        Eq(MdLineKind.Heading, a.Line(2)?.Kind ?? MdLineKind.Text, "Zeile 2");
        Eq(2, a.Line(2)!.Level, "Ebene");
        Eq(null, a.Line(1), "Zeile 1");
        Eq("## | ##", string.Join("|", Texts(t, a, MdStyle.Marker)), "Steuerzeichen");
    }

    static void Inline(string nl)
    {
        string t = "a **fett** und *kursiv*" + nl + "x `code` ~~weg~~ ***beides***";
        var a = MarkdownAnalysis.Analyze(t);
        Eq("fett|beides", string.Join("|", Texts(t, a, MdStyle.Bold)), "fett");
        Eq("kursiv|**beides**", string.Join("|", Texts(t, a, MdStyle.Italic)), "kursiv");
        Eq("code", string.Join("|", Texts(t, a, MdStyle.Code)), "code");
        Eq("weg", string.Join("|", Texts(t, a, MdStyle.Strike)), "durch");
        Eq("**|**|*|*|`|`|~~|~~|*|**|**|*", string.Join("|", Texts(t, a, MdStyle.Marker)), "Steuerzeichen");
    }

    static void Link(string nl)
    {
        string t = "Text" + nl + "Siehe [Folio](https://example.org/x) und https://example.com";
        var a = MarkdownAnalysis.Analyze(t);
        Eq("Folio|https://example.com", string.Join("|", Texts(t, a, MdStyle.Link)), "Linktext");
        Eq("[|](https://example.org/x)", string.Join("|", Texts(t, a, MdStyle.Marker)), "Steuerzeichen");
        Eq("https://example.org/x", a.Links[0].Url, "Ziel");
        Eq("[Folio](https://example.org/x)", t.Substring(a.Links[0].Start, a.Links[0].Length), "Linkbereich");
    }

    static void Lists(string nl)
    {
        string t = "- [ ] offen" + nl + "- [x] erledigt" + nl + "- punkt" + nl + nl + "1. eins";
        var a = MarkdownAnalysis.Analyze(t);
        Eq(3, a.Widgets.Count, "Anzahl");
        Eq(MdWidgetKind.TaskOpen, a.Widgets[0].Kind, "offen");
        Eq("- [ ]", t.Substring(a.Widgets[0].Start, a.Widgets[0].Length), "offen Text");
        Eq(MdWidgetKind.TaskDone, a.Widgets[1].Kind, "erledigt");
        Eq("- [x]", t.Substring(a.Widgets[1].Start, a.Widgets[1].Length), "erledigt Text");
        Eq(MdWidgetKind.Bullet, a.Widgets[2].Kind, "Punkt");
        Eq("-", t.Substring(a.Widgets[2].Start, a.Widgets[2].Length), "Punkt Text");
        Eq("erledigt", string.Join("|", Texts(t, a, MdStyle.Done)), "erledigt-Text");
        Eq("1.", string.Join("|", Texts(t, a, MdStyle.Muted)), "Nummer");
    }

    static void Fenced(string nl)
    {
        string t = "vorher" + nl + "```cs" + nl + "var x = 1;" + nl + "```" + nl + "nachher";
        var a = MarkdownAnalysis.Analyze(t);
        Eq(MdLineKind.CodeFence, a.Line(2)!.Kind, "Zaun oben");
        Eq(MdLineKind.Code, a.Line(3)!.Kind, "Code");
        Eq(MdLineKind.CodeFence, a.Line(4)!.Kind, "Zaun unten");
        Eq(2, a.Line(3)!.BlockFirst, "Blockanfang");
        Eq(4, a.Line(3)!.BlockLast, "Blockende");
        Eq(null, a.Line(5), "danach");
    }

    static void OpenFence()
    {
        string t = "```\ncode\nmehr";
        var a = MarkdownAnalysis.Analyze(t);
        Eq(MdLineKind.CodeFence, a.Line(1)!.Kind, "Zaun");
        Eq(MdLineKind.Code, a.Line(3)!.Kind, "letzte Zeile ist Code");
    }

    static void Quote(string nl)
    {
        string t = "> zitat" + nl + "> > tiefer" + nl + nl + "normal";
        var a = MarkdownAnalysis.Analyze(t);
        Eq(MdLineKind.Quote, a.Line(1)!.Kind, "Zeile 1");
        Eq(1, a.Line(1)!.QuoteDepth, "Tiefe 1");
        Eq(2, a.Line(2)!.QuoteDepth, "Tiefe 2");
        Eq("> |> > ", string.Join("|", Texts(t, a, MdStyle.Marker)), "Steuerzeichen");
        Eq(null, a.Line(4), "normal");
    }

    static void Setext()
    {
        string t = "Titel\n=====\n\nText";
        var a = MarkdownAnalysis.Analyze(t);
        Eq(1, a.Line(1)!.Level, "Ebene");
        Eq("=====", string.Join("|", Texts(t, a, MdStyle.Marker)), "Unterstreichung");
    }

    static void Encodings()
    {
        string s = "Grüße – ✓";
        Eq(s, TextFiles.Decode(Encoding.UTF8.GetBytes(s)).Text, "UTF-8");
        Eq("UTF-8", TextFiles.DisplayName(TextFiles.Decode(Encoding.UTF8.GetBytes(s)).Encoding), "UTF-8 Name");
        var bom = new UTF8Encoding(true);
        var withBom = bom.GetPreamble().Concat(bom.GetBytes(s)).ToArray();
        var r = TextFiles.Decode(withBom);
        Eq(s, r.Text, "UTF-8 BOM");
        Eq("UTF-8 mit BOM", TextFiles.DisplayName(r.Encoding), "BOM Name");
        var ansi = TextFiles.Decode(new byte[] { 0x47, 0x72, 0xFC, 0xDF, 0x65 }); // „Grüße“ in 1252
        Eq("Grüße", ansi.Text, "ANSI");
        var u16 = new UnicodeEncoding(false, true);
        Eq(s, TextFiles.Decode(u16.GetPreamble().Concat(u16.GetBytes(s)).ToArray()).Text, "UTF-16");
    }
}
