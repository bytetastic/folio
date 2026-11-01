using System;
using System.IO;
using System.Text.Json;

namespace Folio.Services;

public sealed class AppSettings
{
    /// <summary>"hell" (Standard) oder "dunkel".</summary>
    public string Theme { get; set; } = "hell";
    /// <summary>Ordner für neue Notizen; null = beim ersten Speichern fragen.</summary>
    public string? NotesFolder { get; set; }
    /// <summary>Schriftgröße des Fließtexts (Strg+Mausrad ändert sie).</summary>
    public double FontSize { get; set; } = 16;
    /// <summary>Zeilen umbrechen statt waagerecht scrollen (Markdown immer).</summary>
    public bool WordWrap { get; set; } = true;
    /// <summary>Gleitender Cursor und weich einblendende Zeichen.</summary>
    public bool SmoothTyping { get; set; } = true;
    /// <summary>Markdown-Spickzettel rechts eingeblendet (F1).</summary>
    public bool HelpOpen { get; set; }
    /// <summary>Beim Start bei GitHub nach einer neueren Version fragen.</summary>
    public bool UpdateCheck { get; set; } = true;
    public bool InstallPromptDismissed { get; set; }

    public double? WinLeft { get; set; }
    public double? WinTop { get; set; }
    public double? WinWidth { get; set; }
    public double? WinHeight { get; set; }
    public bool WinMaximized { get; set; }

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Options) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            AppPaths.Log("Einstellungen nicht lesbar: " + ex.Message);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            string tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
            File.Move(tmp, AppPaths.SettingsFile, true);
        }
        catch (Exception ex)
        {
            AppPaths.Log("Einstellungen nicht speicherbar: " + ex.Message);
        }
    }
}
