using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Folio.Diagrams;
using Folio.Editor;
using Folio.Services;
using Microsoft.Win32;

namespace Folio;

/// <summary>
/// Diagramme exportieren: Ablaufdiagramme als BPMN 2.0 (.bpmn, zum Öffnen im Camunda Modeler),
/// jedes Diagramm als PNG oder SVG.
/// </summary>
public partial class MainWindow
{
    /// <summary>Name für Pool, Dateiname und Viewer: Diagrammtitel → Überschrift darüber → irgendeine Überschrift → Dateiname → „Prozess“.</summary>
    string DiagramName(Diagram d, int blockFirst)
    {
        var doc = _editor.Document;
        string? fileName = _current?.FilePath is { } fp ? Path.GetFileNameWithoutExtension(fp) : null;
        return d.Title ?? HeadingAbove(blockFirst) ?? HeadingAbove(doc.LineCount + 1) ?? fileName ?? (d is FlowChart ? "Prozess" : "Diagramm");
    }

    /// <summary>Das Ablaufdiagramm unter (oder über) dem Cursor, sonst das erste im Dokument.</summary>
    (FlowChart Chart, string Name)? FindFlowChart()
    {
        if (_editor.DiagramNearCaret(d => d is FlowChart { Nodes.Count: > 0 }) is not { } found) return null;
        var chart = (FlowChart)MermaidParser.Parse(found.Source);
        return (chart, DiagramName(chart, found.BlockFirst));
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

    static string SafeFileName(string name, string fallback)
    {
        string safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? ' ' : c)).Trim();
        return safe.Length > 0 ? safe : fallback;
    }

    string ExportFolder() =>
        _current?.FilePath is { } p ? Path.GetDirectoryName(p)! : NotesFolderOrNull() ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

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
        await ExportBpmn(found.Value.Chart, found.Value.Name, targetPath);
    }

    async Task ExportBpmn(FlowChart chart, string name, string? targetPath = null)
    {
        string path;
        if (targetPath != null) path = targetPath;
        else
        {
            var dlg = new SaveFileDialog
            {
                Title = "Als BPMN für Camunda exportieren",
                Filter = "BPMN 2.0 (*.bpmn)|*.bpmn|XML (*.xml)|*.xml",
                FileName = SafeFileName(name, "Prozess") + ".bpmn",
                InitialDirectory = ExportFolder(),
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

    /// <summary>Diagramm unter dem Cursor als Bild (Menü).</summary>
    async Task ExportDiagramImage(string? targetPath = null)
    {
        if (_editor.DiagramNearCaret(d => d is not DiagramError) is not { } found)
        {
            await ShowDialog("Kein Diagramm gefunden",
                "Gespeichert wird ein ```mermaid-Block. Setz den Cursor in das Diagramm, das du als Bild speichern willst.", null, "OK");
            return;
        }
        var d = MermaidParser.Parse(found.Source);
        await ExportImage(found.Source, DiagramName(d, found.BlockFirst), null, targetPath);
    }

    /// <summary>Als PNG (doppelte Auflösung) oder SVG speichern. <paramref name="format"/> „png“/„svg“ wählt den Typ im
    /// Dialog vor; bei <paramref name="targetPath"/> entscheidet die Endung (für Tests ohne Dialog).</summary>
    async Task ExportImage(string source, string name, string? format, string? targetPath = null)
    {
        string path;
        if (targetPath != null) path = targetPath;
        else
        {
            var dlg = new SaveFileDialog
            {
                Title = "Diagramm als Bild speichern",
                Filter = "PNG-Bild (*.png)|*.png|SVG-Vektorgrafik (*.svg)|*.svg",
                FilterIndex = format == "svg" ? 2 : 1,
                FileName = SafeFileName(name, "Diagramm") + (format == "svg" ? ".svg" : ".png"),
                InitialDirectory = ExportFolder(),
                AddExtension = true,
            };
            if (dlg.ShowDialog(this) != true) return;
            path = dlg.FileName;
        }
        try
        {
            var pic = _editor.GetDiagram(source, 1e5);
            var drawing = DiagramImageExport.Compose(pic.Parts.Select(p => p.Drawing));
            if (Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase))
                TextFiles.Write(path, DiagramImageExport.ToSvg(drawing, pic.Size), TextFiles.Utf8NoBom);
            else
                WriteBytesAtomic(path, DiagramImageExport.ToPng(drawing, pic.Size, 2));
            ShowToast($"Gespeichert: {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            AppPaths.Log("Bild-Export: " + ex);
            await ShowDialog("Speichern fehlgeschlagen", ex.Message, null, "OK");
        }
    }

    static void WriteBytesAtomic(string path, byte[] bytes)
    {
        string tmp = path + ".folio-tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, true);
    }
}
