using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Folio.Services;

/// <summary>
/// Installation ohne Setup-Programm: Folio.exe kopiert sich nach %LOCALAPPDATA%\Programs\Folio, legt Verknüpfungen an,
/// trägt sich unter „Installierte Apps“ und bei „Öffnen mit“ für .md/.txt ein (nur für den aktuellen Benutzer, ohne Adminrechte).
/// Die Standard-App für diese Dateitypen wird nie umgestellt – das entscheidet der Benutzer in Windows selbst.
/// </summary>
public static class Installer
{
    static bool Test => AppPaths.InstallTestRoot != null;
    static string Classes => Test ? @"Software\FolioTest\Classes" : @"Software\Classes";
    static string UninstallKey => Test ? @"Software\FolioTest\Uninstall\Folio" : @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Folio";
    const string ProgId = "Folio.Document";
    static readonly string[] Extensions = { ".md", ".markdown", ".txt" };

    static string StartMenuLink => Test
        ? Path.Combine(AppPaths.InstallTestRoot!, "StartMenu", "Folio.lnk")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Folio.lnk");
    static string DesktopLink => Test
        ? Path.Combine(AppPaths.InstallTestRoot!, "Desktop", "Folio.lnk")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Folio.lnk");

    public static bool IsRunningInstalled =>
        string.Equals(Path.GetFullPath(AppPaths.ExePath), Path.GetFullPath(AppPaths.InstalledExe), StringComparison.OrdinalIgnoreCase);

    public static bool IsInstalled => File.Exists(AppPaths.InstalledExe);

    public static bool HasDesktopLink => File.Exists(DesktopLink);

    public static string? InstalledVersion
    {
        get
        {
            if (!IsInstalled) return null;
            try
            {
                var v = FileVersionInfo.GetVersionInfo(AppPaths.InstalledExe);
                return $"{v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}";
            }
            catch { return null; }
        }
    }

    /// <summary>Ist die gerade laufende Datei neuer als die installierte?</summary>
    public static bool RunningIsNewer
    {
        get
        {
            var inst = InstalledVersion;
            if (inst == null) return false;
            return System.Version.TryParse(AppPaths.Version, out var a) && System.Version.TryParse(inst, out var b) && a > b;
        }
    }

    public static void Install(bool desktopLink)
    {
        Directory.CreateDirectory(AppPaths.InstallDir);
        File.Copy(AppPaths.ExePath, AppPaths.InstalledExe, true);
        string exe = AppPaths.InstalledExe;
        // Die Markierung „aus dem Internet heruntergeladen“ (Zone.Identifier) kopiert Windows mit. Der Benutzer hat die Datei
        // bereits bewusst gestartet – die installierte Kopie soll nicht bei jedem Start erneut die SmartScreen-Warnung zeigen.
        DeleteFileW(exe + ":Zone.Identifier");

        CreateShortcut(StartMenuLink, exe);
        if (desktopLink) CreateShortcut(DesktopLink, exe);
        else if (File.Exists(DesktopLink)) File.Delete(DesktopLink);

        RegisterOpenWith(exe);

        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", "Folio");
            k.SetValue("DisplayVersion", AppPaths.Version);
            k.SetValue("DisplayIcon", exe + ",0");
            k.SetValue("Publisher", "Folio");
            k.SetValue("InstallLocation", AppPaths.InstallDir);
            k.SetValue("UninstallString", $"\"{exe}\" --uninstall");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
            k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        }
    }

    public static void SetDesktopLink(bool on)
    {
        if (on) CreateShortcut(DesktopLink, AppPaths.InstalledExe);
        else TryDelete(DesktopLink);
    }

    static void RegisterOpenWith(string exe)
    {
        using (var p = Registry.CurrentUser.CreateSubKey($@"{Classes}\{ProgId}"))
        {
            p.SetValue("", "Folio-Dokument");
            using (var icon = p.CreateSubKey("DefaultIcon")) icon.SetValue("", exe + ",0");
            using var cmd = p.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", $"\"{exe}\" \"%1\"");
        }
        foreach (var ext in Extensions)
        {
            using var k = Registry.CurrentUser.CreateSubKey($@"{Classes}\{ext}\OpenWithProgids");
            k.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }
    }

    static void UnregisterOpenWith()
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\{ProgId}", false);
        foreach (var ext in Extensions)
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey($@"{Classes}\{ext}\OpenWithProgids", true);
                k?.DeleteValue(ProgId, false);
            }
            catch { }
        }
    }

    /// <summary>Entfernt Verknüpfungen und Einträge. Die Exe löscht ein kurzer cmd-Nachlauf, sobald Folio beendet ist.
    /// Einstellungen und Sitzung unter %APPDATA%\Folio bleiben erhalten – ebenso alle Notizen.</summary>
    public static void Uninstall()
    {
        TryDelete(StartMenuLink);
        TryDelete(DesktopLink);
        UnregisterOpenWith();
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);

        if (IsInstalled)
        {
            string cmd = $"/c ping 127.0.0.1 -n 3 > nul & del /f /q \"{AppPaths.InstalledExe}\" & rmdir \"{AppPaths.InstallDir}\"";
            Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        }
    }

    public static void LaunchInstalled(string? args = null)
    {
        Process.Start(new ProcessStartInfo(AppPaths.InstalledExe, args ?? "") { UseShellExecute = false, WorkingDirectory = AppPaths.InstallDir });
    }

    static void CreateShortcut(string lnkPath, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);
        Type? t = Type.GetTypeFromProgID("WScript.Shell");
        if (t == null) return;
        dynamic shell = Activator.CreateInstance(t)!;
        dynamic lnk = shell.CreateShortcut(lnkPath);
        lnk.TargetPath = target;
        lnk.WorkingDirectory = Path.GetDirectoryName(target);
        lnk.IconLocation = target + ",0";
        lnk.Description = "Folio – Notizen und Markdown";
        lnk.Save();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern bool DeleteFileW(string path);

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
