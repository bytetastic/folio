using System;
using System.IO;
using System.Text;

namespace Folio.Services;

/// <summary>Gelesene Datei samt der Kodierung, in der sie wieder gespeichert wird.</summary>
public sealed record LoadedText(string Text, Encoding Encoding);

/// <summary>
/// Lesen und Schreiben von Textdateien. Erkennt BOMs (UTF-8/16/32), sonst gültiges UTF-8, sonst Windows-1252.
/// Gespeichert wird in derselben Kodierung – eine Datei ohne BOM bekommt auch keins.
/// </summary>
public static class TextFiles
{
    public static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    static TextFiles() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static LoadedText Read(string path) => Decode(File.ReadAllBytes(path));

    public static LoadedText Decode(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
            return new LoadedText(new UTF8Encoding(false).GetString(b, 3, b.Length - 3), new UTF8Encoding(true));
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xFE && b[2] == 0 && b[3] == 0)
            return new LoadedText(new UTF32Encoding(false, true).GetString(b, 4, b.Length - 4), new UTF32Encoding(false, true));
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
            return new LoadedText(new UnicodeEncoding(false, true).GetString(b, 2, b.Length - 2), new UnicodeEncoding(false, true));
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
            return new LoadedText(new UnicodeEncoding(true, true).GetString(b, 2, b.Length - 2), new UnicodeEncoding(true, true));
        try
        {
            return new LoadedText(StrictUtf8.GetString(b), Utf8NoBom);
        }
        catch (DecoderFallbackException)
        {
            var ansi = CodePagesEncoding(1252);
            return new LoadedText(ansi.GetString(b), ansi);
        }
    }

    static Encoding CodePagesEncoding(int codePage)
    {
        try { return Encoding.GetEncoding(codePage); }
        catch (Exception) { return Encoding.Latin1; }
    }

    /// <summary>Atomar schreiben: erst in eine Nachbardatei, dann ersetzen – ein Absturz hinterlässt nie eine halbe Datei.</summary>
    public static void Write(string path, string text, Encoding encoding)
    {
        string tmp = path + ".folio-tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var pre = encoding.GetPreamble();
            fs.Write(pre, 0, pre.Length);
            var bytes = encoding.GetBytes(text);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(true);
        }
        if (!File.Exists(path)) { File.Move(tmp, path); return; }
        try { File.Replace(tmp, path, null, true); }
        catch (IOException) { File.Move(tmp, path, true); } // z. B. Netzlaufwerke ohne Replace-Unterstützung
    }

    public static string DisplayName(Encoding e) => e switch
    {
        UTF8Encoding u => u.GetPreamble().Length > 0 ? "UTF-8 mit BOM" : "UTF-8",
        UnicodeEncoding u => u.GetPreamble().Length > 0 && u.GetPreamble()[0] == 0xFE ? "UTF-16 BE" : "UTF-16 LE",
        UTF32Encoding => "UTF-32",
        _ => e.CodePage == 1252 ? "ANSI (1252)" : e.WebName.ToUpperInvariant(),
    };
}
