using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Folio.Services;

/// <summary>Ein Tab, wie er zwischen zwei Starts gespeichert wird.</summary>
public sealed class SessionTab
{
    public string Id { get; set; } = "";
    /// <summary>Datei auf der Platte; null = neue, noch nie gespeicherte Notiz.</summary>
    public string? Path { get; set; }
    /// <summary>Inhalt weicht von der Datei ab (bzw. neue Notiz mit Text) – dann liegt er unter session\&lt;Id&gt;.txt.</summary>
    public bool Dirty { get; set; }
    public string Encoding { get; set; } = "utf-8";
    public bool Bom { get; set; }
    public int Caret { get; set; }
    public double Scroll { get; set; }
    public string Mode { get; set; } = "live";
    /// <summary>Änderungszeit der Datei beim Laden (um Änderungen von außen zu erkennen).</summary>
    public DateTime? FileTime { get; set; }
}

public sealed class SessionState
{
    public List<SessionTab> Tabs { get; set; } = new();
    public string? Active { get; set; }
}

/// <summary>
/// Sitzung: welche Tabs offen sind und der Inhalt ungespeicherter Tabs. Alles atomar geschrieben,
/// damit ein Absturz oder Stromausfall nie eine halbe Datei hinterlässt.
/// </summary>
public static class SessionStore
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static SessionState Load()
    {
        try
        {
            if (File.Exists(AppPaths.SessionFile))
                return JsonSerializer.Deserialize<SessionState>(File.ReadAllText(AppPaths.SessionFile), Options) ?? new SessionState();
        }
        catch (Exception ex)
        {
            AppPaths.Log("Sitzung nicht lesbar: " + ex.Message);
        }
        return new SessionState();
    }

    public static string ContentFile(string id) => System.IO.Path.Combine(AppPaths.SessionDir, id + ".txt");

    public static string? LoadContent(string id)
    {
        try
        {
            string f = ContentFile(id);
            return File.Exists(f) ? File.ReadAllText(f, TextFiles.Utf8NoBom) : null;
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Sitzungsinhalt {id} nicht lesbar: " + ex.Message);
            return null;
        }
    }

    public static void SaveContent(string id, string text) => TextFiles.Write(ContentFile(id), text, TextFiles.Utf8NoBom);

    public static void DeleteContent(string id)
    {
        try { File.Delete(ContentFile(id)); } catch { }
    }

    public static void Save(SessionState state)
    {
        try
        {
            string tmp = AppPaths.SessionFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, Options));
            File.Move(tmp, AppPaths.SessionFile, true);
            // verwaiste Inhaltsdateien aufräumen (Tabs, die es nicht mehr gibt)
            var keep = state.Tabs.Where(t => t.Dirty).Select(t => t.Id + ".txt").ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.GetFiles(AppPaths.SessionDir, "*.txt"))
                if (!keep.Contains(System.IO.Path.GetFileName(f))) { try { File.Delete(f); } catch { } }
        }
        catch (Exception ex)
        {
            AppPaths.Log("Sitzung nicht speicherbar: " + ex.Message);
        }
    }
}
