using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Folio.Diagrams;
using Folio.Editor;
using Folio.Services;
using Microsoft.Win32;

namespace Folio;

/// <summary>Ablaufdiagramm als BPMN 2.0 (.bpmn) exportieren – zum Öffnen im Camunda Modeler.</summary>
public partial class MainWindow
{
    /// <summary>Das Ablaufdiagramm unter (oder über) dem Cursor, sonst das erste im Dokument.</summary>
    (FlowChart Chart, string Name)? FindFlowChart()
    {
        var doc = _editor.Document;
        if (doc == null || !_editor.IsMarkdown) return null;
        var a = _editor.Analysis;
        int caretLine = _editor.TextArea.Caret.Line;
        var blocks = a.DiagramBlocks.Where(b => b.Closed).OrderBy(b => b.BlockFirst).ToList();
        var ordered = blocks.Where(b => b.BlockFirst <= caretLine && caretLine <= b.BlockLast)
            .Concat(blocks.Where(b => b.BlockLast < caretLine).Reverse())
            .Concat(blocks);
        foreach (var b in ordered)
        {
            if (b.BlockLast - b.BlockFirst < 2 || b.BlockLast > doc.LineCount) continue;
            int start = doc.GetLineByNumber(b.BlockFirst + 1).Offset;
            int end = doc.GetLineByNumber(b.BlockLast).Offset;
            if (MermaidParser.Parse(doc.GetText(start, end - start)) is not FlowChart chart || chart.Nodes.Count == 0) continue;
            // Poolname: Diagrammtitel → Überschrift darüber → sonst eine Überschrift weiter unten → Dateiname → „Prozess“
            string? fileName = _current?.FilePath is { } fp ? Path.GetFileNameWithoutExtension(fp) : null;
            return (chart, chart.Title ?? HeadingAbove(b.BlockFirst) ?? HeadingAbove(doc.LineCount + 1) ?? fileName ?? "Prozess");
        }
        return null;
    }

    string? HeadingAbove(int line)
    {
        var doc = _editor.Document;
        for (int n = line - 1; n >= 1; n--)
        {
            if (_editor.Analysis.Line(n) is not { Kind: MdLineKind.Heading }) continue;
            string t = doc.GetText(doc.GetLineByNumber(n)).Trim().TrimStart('#').Trim().TrimEnd('#').Trim();
            if (t.Length > 0) return t;
        }
        return null;
    }

    async Task ExportBpmn(string? targetPath = null)
    {
        var found = FindFlowChart();
        if (found == null)
        {
            await ShowDialog("Kein Ablaufdiagramm gefunden",
                "Exportiert wird ein ```mermaid-Block mit flowchart (oder graph). Setz den Cursor in das Diagramm, das du exportieren willst – oder füge über den Spickzettel (F1) „Ablauf mit Bahnen“ ein.",
                null, "OK");
            return;
        }
        var (chart, name) = found.Value;
        string path;
        if (targetPath != null) path = targetPath;
        else
        {
            string safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? ' ' : c)).Trim();
            var dlg = new SaveFileDialog
            {
                Title = "Als BPMN für Camunda exportieren",
                Filter = "BPMN 2.0 (*.bpmn)|*.bpmn|XML (*.xml)|*.xml",
                FileName = (safe.Length > 0 ? safe : "Prozess") + ".bpmn",
                InitialDirectory = _current?.FilePath is { } p ? Path.GetDirectoryName(p) : NotesFolderOrNull() ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                DefaultExt = ".bpmn",
                AddExtension = true,
            };
            if (dlg.ShowDialog(this) != true) return;
            path = dlg.FileName;
        }
        try
        {
            string xml = BpmnExport.ToXml(chart, name, AppPaths.Version);
            TextFiles.Write(path, xml, TextFiles.Utf8NoBom);
            ShowToast($"Exportiert: {Path.GetFileName(path)} – im Camunda Modeler öffnen");
        }
        catch (Exception ex)
        {
            AppPaths.Log("BPMN-Export: " + ex);
            await ShowDialog("Export fehlgeschlagen", ex.Message, null, "OK");
        }
    }
}
