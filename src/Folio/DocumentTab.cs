using System;
using System.IO;
using System.Text;
using Folio.Editor;
using Folio.Services;
using ICSharpCode.AvalonEdit.Document;

namespace Folio;

/// <summary>Ein offener Tab: Dokument, Datei, Kodierung und wo der Cursor stand.</summary>
public sealed class DocumentTab
{
    public string Id { get; }
    public TextDocument Document { get; }
    public string? FilePath { get; private set; }
    public Encoding Encoding { get; set; } = TextFiles.Utf8NoBom;
    public EditorMode Mode { get; set; } = EditorMode.Live;
    public int Caret { get; set; }
    public double Scroll { get; set; }
    /// <summary>Änderungszeit der Datei beim letzten Laden/Speichern.</summary>
    public DateTime? FileTime { get; set; }
    /// <summary>Aus der Sitzung wiederhergestellt mit ungespeicherten Änderungen (der Rückgängig-Verlauf kennt sie nicht).</summary>
    public bool RestoredDirty { get; set; }
    /// <summary>Datei wurde außerhalb von Folio geändert, während der Tab Änderungen hatte – Hinweis steht aus.</summary>
    public bool ExternalChangePending { get; set; }

    /// <summary>Wird gesetzt, sobald sich Titel oder Änderungsstatus geändert haben könnten.</summary>
    public event Action? Changed;

    public DocumentTab(string id, TextDocument doc, string? path)
    {
        Id = id;
        Document = doc;
        FilePath = path;
        Document.UndoStack.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UndoStack.IsOriginalFile)) Changed?.Invoke();
        };
        Document.TextChanged += (_, _) => { if (FilePath == null) Changed?.Invoke(); };
    }

    public static DocumentTab CreateNew() => new(Guid.NewGuid().ToString("N")[..12], new TextDocument(), null);

    public bool IsUntitled => FilePath == null;

    public bool IsDirty => IsUntitled ? Document.TextLength > 0 : RestoredDirty || !Document.UndoStack.IsOriginalFile;

    public string Extension => FilePath != null ? Path.GetExtension(FilePath) : ".md";

    /// <summary>Dateiname, bei neuen Notizen die erste Zeile (ohne #), sonst „Neue Notiz“.</summary>
    public string Title
    {
        get
        {
            if (FilePath != null) return Path.GetFileName(FilePath);
            bool inCode = false;
            foreach (var line in Document.Lines)
            {
                string raw = Document.GetText(line).Trim();
                // Codeblöcke und Diagramme sind kein Titel („```mermaid“ als Tabname)
                if (raw.StartsWith("```") || raw.StartsWith("~~~")) { inCode = !inCode; continue; }
                if (inCode) continue;
                string t = raw.TrimStart('#', '>', '-', '*', ' ').Trim();
                if (t.Length == 0) continue;
                return t.Length > 40 ? t[..40].TrimEnd() + "…" : t;
            }
            return "Neue Notiz";
        }
    }

    /// <summary>Vorschlag für den Dateinamen beim ersten Speichern.</summary>
    public string SuggestedFileName
    {
        get
        {
            string t = Title == "Neue Notiz" ? "Notiz " + DateTime.Now.ToString("yyyy-MM-dd") : Title.TrimEnd('…');
            foreach (char c in Path.GetInvalidFileNameChars()) t = t.Replace(c, ' ');
            t = t.Trim().TrimEnd('.');
            if (t.Length > 60) t = t[..60].Trim();
            return (t.Length == 0 ? "Notiz" : t) + ".md";
        }
    }

    public void MarkSaved(string path, DateTime fileTime)
    {
        bool renamed = FilePath != path;
        FilePath = path;
        FileTime = fileTime;
        RestoredDirty = false;
        ExternalChangePending = false;
        Document.UndoStack.MarkAsOriginalFile();
        if (renamed) Document.FileName = path;
        Changed?.Invoke();
    }

    public void RaiseChanged() => Changed?.Invoke();
}
