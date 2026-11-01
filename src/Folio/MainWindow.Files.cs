using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Folio.Services;
using ICSharpCode.AvalonEdit.Document;
using Microsoft.Win32;

namespace Folio;

/// <summary>Dateien: öffnen, speichern, Notizen-Ordner, Änderungen von außen.</summary>
public partial class MainWindow
{
    const string FileFilter = "Markdown und Text (*.md;*.markdown;*.txt)|*.md;*.markdown;*.txt|Markdown (*.md)|*.md;*.markdown|Textdateien (*.txt)|*.txt|Alle Dateien (*.*)|*.*";

    async Task OpenWithDialog()
    {
        var dlg = new OpenFileDialog
        {
            Filter = FileFilter,
            Multiselect = true,
            InitialDirectory = _current?.FilePath is { } p ? Path.GetDirectoryName(p) : NotesFolderOrNull() ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var f in dlg.FileNames) OpenFile(f);
        await Task.CompletedTask;
    }

    void OpenFile(string path)
    {
        try
        {
            path = Path.GetFullPath(path);
            var existing = _tabs.FirstOrDefault(t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (existing != null) { Select(existing); return; }
            var info = new FileInfo(path);
            if (info.Length > 64L * 1024 * 1024)
            {
                _ = ShowDialog("Datei zu groß", $"„{info.Name}“ ist {info.Length / 1048576.0:0} MB groß. Folio öffnet Dateien bis 64 MB.", null, "OK");
                return;
            }
            var loaded = TextFiles.Read(path);
            var doc = new TextDocument(loaded.Text) { FileName = path };
            doc.UndoStack.ClearAll();
            var tab = new DocumentTab(Guid.NewGuid().ToString("N")[..12], doc, path)
            {
                Encoding = loaded.Encoding,
                FileTime = info.LastWriteTimeUtc,
            };
            // Leere, unberührte neue Notiz wird durch die geöffnete Datei ersetzt (wie im Windows-Editor)
            var blank = _current is { IsUntitled: true } c && c.Document.TextLength == 0 && _tabs.Count == 1 ? _current : null;
            AddTab(tab);
            Select(tab);
            if (blank != null) RemoveTab(blank);
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Öffnen {path}: " + ex);
            _ = ShowDialog("Datei lässt sich nicht öffnen", ex.Message, null, "OK");
        }
    }

    /// <summary>Speichern. Neue Notizen landen direkt im Notizen-Ordner (beim ersten Mal wird er festgelegt).</summary>
    async Task<bool> Save(DocumentTab? tab)
    {
        if (tab == null) return false;
        if (tab.IsUntitled)
        {
            string? folder = await EnsureNotesFolder();
            if (folder == null) return false;
            string path = UniquePath(folder, tab.SuggestedFileName);
            if (!WriteTab(tab, path)) return false;
            ShowToast($"Gespeichert in {Path.GetFileName(folder)}\\{Path.GetFileName(path)}");
            return true;
        }
        if (!WriteTab(tab, tab.FilePath!)) return false;
        ShowToast("Gespeichert");
        return true;
    }

    async Task<bool> SaveAs(DocumentTab? tab)
    {
        if (tab == null) return false;
        var dlg = new SaveFileDialog
        {
            Filter = FileFilter,
            FileName = tab.IsUntitled ? tab.SuggestedFileName : Path.GetFileName(tab.FilePath!),
            InitialDirectory = tab.FilePath is { } p ? Path.GetDirectoryName(p) : NotesFolderOrNull() ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            AddExtension = true,
            DefaultExt = ".md",
        };
        if (dlg.ShowDialog(this) != true) return false;
        bool wasMarkdown = _editor.IsMarkdown;
        if (!WriteTab(tab, dlg.FileName)) return false;
        // Endung kann sich geändert haben (z. B. .txt → .md): Darstellung anpassen
        if (tab == _current && wasMarkdown != IsMarkdownPath(dlg.FileName))
        {
            RememberView(tab);
            _current = null;
            Select(tab);
        }
        ShowToast("Gespeichert");
        await Task.CompletedTask;
        return true;
    }

    static bool IsMarkdownPath(string p) => Path.GetExtension(p).ToLowerInvariant() is ".md" or ".markdown" or ".mdown" or ".mkd" or ".mdx";

    bool WriteTab(DocumentTab tab, string path)
    {
        try
        {
            TextFiles.Write(path, tab.Document.Text, tab.Encoding);
            tab.MarkSaved(path, File.GetLastWriteTimeUtc(path));
            if (tab == _current) _editor.Placeholder = null;
            SessionStore.DeleteContent(tab.Id);
            UpdateStatus();
            ScheduleSessionSave();
            return true;
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Speichern {path}: " + ex);
            _ = ShowDialog("Speichern fehlgeschlagen", $"{ex.Message}\n\nDein Text ist nicht verloren – er bleibt in Folio erhalten.", null, "OK");
            return false;
        }
    }

    static string UniquePath(string folder, string fileName)
    {
        string name = Path.GetFileNameWithoutExtension(fileName), ext = Path.GetExtension(fileName);
        string p = Path.Combine(folder, fileName);
        for (int i = 2; File.Exists(p); i++) p = Path.Combine(folder, $"{name} ({i}){ext}");
        return p;
    }

    // ───────────────────────────── Notizen-Ordner ─────────────────────────────

    string? NotesFolderOrNull() => _settings.NotesFolder is { Length: > 0 } f && Directory.Exists(f) ? f : null;

    static string DefaultNotesFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Notizen");

    async Task<string?> EnsureNotesFolder()
    {
        if (NotesFolderOrNull() is { } f) return f;
        string suggestion = DefaultNotesFolder;
        int r = await ShowDialog("Wo sollen deine Notizen liegen?",
            $"Neue Notizen speichert Folio in einem festen Ordner – einmal festlegen, danach reicht Strg+S.\n\nVorschlag: {suggestion}\n\nÄndern kannst du ihn später im Menü.",
            null, "Diesen Ordner nehmen", "Anderen wählen …", "Abbrechen");
        string? folder = r switch
        {
            0 => suggestion,
            1 => PickFolder(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            _ => null,
        };
        if (folder == null) return null;
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            await ShowDialog("Ordner lässt sich nicht anlegen", ex.Message, null, "OK");
            return null;
        }
        _settings.NotesFolder = folder;
        _settings.Save();
        return folder;
    }

    async Task ChangeNotesFolder()
    {
        string? folder = PickFolder(NotesFolderOrNull() ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        if (folder == null) return;
        _settings.NotesFolder = folder;
        _settings.Save();
        ShowToast($"Notizen-Ordner: {Path.GetFileName(folder)}");
        await Task.CompletedTask;
    }

    string? PickFolder(string initial)
    {
        var dlg = new OpenFolderDialog { Title = "Ordner für Notizen wählen", InitialDirectory = initial };
        return dlg.ShowDialog(this) == true ? dlg.FolderName : null;
    }

    void OpenNotesFolder()
    {
        if (NotesFolderOrNull() is { } f) OpenInExplorer(f);
        else _ = EnsureNotesFolder().ContinueWith(t => { if (t.Result is { } nf) Dispatcher.BeginInvoke(() => OpenInExplorer(nf)); });
    }

    static void OpenInExplorer(string folder)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = false }); } catch { }
    }

    static void RevealInExplorer(string file)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = false }); } catch { }
    }

    // ───────────────────────────── Änderungen von außen ─────────────────────────────

    /// <summary>Beim Zurückkehren ins Fenster: Wurde eine offene Datei woanders geändert? Unveränderte Tabs laden still neu.</summary>
    void CheckExternalChanges()
    {
        foreach (var tab in _tabs.ToList())
        {
            if (tab.FilePath == null || tab.FileTime == null) continue;
            DateTime now;
            try
            {
                if (!File.Exists(tab.FilePath)) continue;
                now = File.GetLastWriteTimeUtc(tab.FilePath);
            }
            catch { continue; }
            if (now == tab.FileTime) continue;
            if (!tab.IsDirty)
            {
                Reload(tab);
                if (tab == _current) ShowToast($"„{tab.Title}“ neu geladen", "");
            }
            else
            {
                tab.ExternalChangePending = true;
                tab.FileTime = now; // nur einmal melden
                if (tab == _current) ShowExternalChange(tab);
            }
        }
    }

    void ShowExternalChange(DocumentTab tab)
    {
        ShowInfo("", "Datei wurde außerhalb von Folio geändert",
            "Du hast hier ungespeicherte Änderungen. Welche Fassung soll gelten?",
            () => tab.ExternalChangePending = false,
            ("Neu laden", () => { Reload(tab); HideInfo(); }, false),
            ("Meine behalten", () => HideInfo(), true));
        _infoForTab = true;
    }

    void Reload(DocumentTab tab)
    {
        try
        {
            var loaded = TextFiles.Read(tab.FilePath!);
            int caret = tab == _current ? _editor.CaretOffset : tab.Caret;
            if (tab.Document.Text != loaded.Text) tab.Document.Replace(0, tab.Document.TextLength, loaded.Text);
            tab.Encoding = loaded.Encoding;
            tab.MarkSaved(tab.FilePath!, File.GetLastWriteTimeUtc(tab.FilePath!));
            if (tab == _current) _editor.CaretOffset = Math.Min(caret, tab.Document.TextLength);
            SessionStore.DeleteContent(tab.Id);
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Neu laden {tab.FilePath}: " + ex.Message);
        }
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] items) return;
        foreach (var f in items.Where(File.Exists).Take(20)) OpenFile(f);
        e.Handled = true;
    }
}
