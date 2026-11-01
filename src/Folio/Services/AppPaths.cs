using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Folio.Services;

public static class AppPaths
{
    /// <summary>Datenordner. Für Tests per FOLIO_DATA umlenkbar.</summary>
    public static string DataDir
    {
        get
        {
            string dir = Environment.GetEnvironmentVariable("FOLIO_DATA") is { Length: > 0 } d
                ? d
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Folio");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string SessionFile => Path.Combine(DataDir, "session.json");
    public static string LogFile => Path.Combine(DataDir, "folio.log");

    /// <summary>Inhalte ungespeicherter Tabs (eine Datei pro Tab).</summary>
    public static string SessionDir
    {
        get
        {
            string dir = Path.Combine(DataDir, "session");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Nur für Tests: lenkt Installation, Verknüpfungen und Registry in einen Testordner bzw. -zweig um.</summary>
    public static string? InstallTestRoot => Environment.GetEnvironmentVariable("FOLIO_INSTALL_ROOT") is { Length: > 0 } r ? r : null;

    public static string InstallDir => InstallTestRoot is { } t
        ? Path.Combine(t, "Programs", "Folio")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Folio");
    public static string InstalledExe => Path.Combine(InstallDir, "Folio.exe");

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Folio.exe");

    /// <summary>Instanzname (für Tests per FOLIO_INSTANCE getrennt von einem normal laufenden Folio).</summary>
    public static string InstanceKey => Environment.GetEnvironmentVariable("FOLIO_INSTANCE") is { Length: > 0 } k ? k : "main";

    public static string PipeName
    {
        get
        {
            string raw = $"Folio-{InstanceKey}-{Environment.UserName}-{Process.GetCurrentProcess().SessionId}";
            return new string(raw.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
        }
    }

    public static string Version
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public static void Log(string message)
    {
        try { File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}"); } catch { }
    }
}
