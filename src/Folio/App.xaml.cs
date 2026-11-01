using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Folio.Services;

namespace Folio;

public partial class App : Application
{
    readonly string[] _args;
    Mutex? _mutex;

    public App() : this(Environment.GetCommandLineArgs().Skip(1).ToArray()) { }

    public App(string[] args) => _args = args;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        // Pinsel stehen nicht in Theme.xaml – auch Dialoge vor dem Hauptfenster brauchen sie
        Visuals.ThemeManager.Apply(Visuals.FolioTheme.Light);

        if (_args.Contains("--uninstall"))
        {
            if (_args.Contains("--quiet")) { QuietAction(Installer.Uninstall); return; }
            RunUninstall();
            return;
        }
        if (_args.Contains("--install"))
        {
            // Stille Installation, z. B. für Skripte: Folio.exe --install [--desktop]
            QuietAction(() => Installer.Install(_args.Contains("--desktop")));
            return;
        }

        var files = new List<string>();
        string? snapshot = null;
        int snapshotDelay = 2500;
        for (int i = 0; i < _args.Length; i++)
        {
            string a = _args[i];
            if (a == "--snapshot" && i + 1 < _args.Length) snapshot = _args[++i];
            else if (a == "--snapshot-delay" && i + 1 < _args.Length && int.TryParse(_args[++i], out var ms)) snapshotDelay = ms;
            else if (a == "--apply-update") { i++; }
            else if (!a.StartsWith("--"))
            {
                string p = a.Trim('"');
                if (File.Exists(p)) files.Add(Path.GetFullPath(p));
            }
        }

        // Update anwenden: Folio.exe --apply-update <pid> – wartet, bis das alte Folio beendet ist, installiert sich und startet neu.
        int applyIdx = Array.IndexOf(_args, "--apply-update");
        if (applyIdx >= 0)
        {
            ApplyUpdate(applyIdx + 1 < _args.Length && int.TryParse(_args[applyIdx + 1], out var pid) ? pid : 0);
            return;
        }

        // Nur ein Folio pro Benutzer: ein zweiter Start öffnet seine Dateien als Tabs im laufenden Fenster –
        // oder, wenn diese Datei eine neuere Version ist, bietet das laufende Folio das Update an.
        _mutex = new Mutex(true, "Local\\" + AppPaths.PipeName, out bool created);
        if (!created && snapshot == null)
        {
            bool newer = !Installer.IsRunningInstalled && Installer.RunningIsNewer;
            bool sent = newer
                ? IpcServer.Send(AppPaths.PipeName, "U\t" + AppPaths.ExePath)
                : files.Count == 0
                    ? IpcServer.Send(AppPaths.PipeName, "O\t")
                    : files.All(f => IpcServer.Send(AppPaths.PipeName, "O\t" + f));
            if (sent)
            {
                Shutdown();
                return;
            }
        }

        var settings = AppSettings.Load();
        Visuals.ThemeManager.Apply(Visuals.FolioTheme.ById(settings.Theme));
        var win = new MainWindow(settings, files, snapshot, snapshotDelay);
        MainWindow = win;
        win.Show();
    }

    void RunUninstall()
    {
        var r = MessageBox.Show(
            "Folio wirklich deinstallieren?\n\nEntfernt werden das Programm, die Verknüpfungen und der Eintrag bei „Öffnen mit“.\n" +
            "Deine Notizen, Einstellungen und die Sitzung unter %APPDATA%\\Folio bleiben erhalten.",
            "Folio deinstallieren", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r == MessageBoxResult.Yes)
        {
            try
            {
                Installer.Uninstall();
                MessageBox.Show("Folio wurde entfernt.", "Folio", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Deinstallation fehlgeschlagen:\n" + ex.Message, "Folio", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        Shutdown();
    }

    void ApplyUpdate(int oldPid)
    {
        try
        {
            if (oldPid > 0)
            {
                try { System.Diagnostics.Process.GetProcessById(oldPid).WaitForExit(20000); } catch (ArgumentException) { }
            }
            bool desktop = Installer.HasDesktopLink;
            Exception? last = null;
            for (int i = 0; i < 10; i++)
            {
                try { Installer.Install(desktop); last = null; break; }
                catch (Exception ex) { last = ex; Thread.Sleep(500); } // Datei kann kurz noch gesperrt sein
            }
            if (last != null) throw last;
            Installer.LaunchInstalled();
        }
        catch (Exception ex)
        {
            AppPaths.Log("Update fehlgeschlagen: " + ex);
            MessageBox.Show("Das Update konnte nicht installiert werden:\n" + ex.Message, "Folio", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        Shutdown();
    }

    void QuietAction(Action action)
    {
        int code = 0;
        try { action(); }
        catch (Exception ex)
        {
            AppPaths.Log("Installation/Deinstallation: " + ex);
            code = 1;
        }
        Shutdown(code);
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppPaths.Log("Unbehandelter Fehler: " + e.Exception);
        e.Handled = true;
        // Fehler schon beim Aufbau: nicht unsichtbar weiterlaufen.
        if (MainWindow == null || !MainWindow.IsLoaded)
        {
            // Testläufe dürfen nie einen Dialog auf dem Bildschirm stehen lassen
            if (AppPaths.InstanceKey == "test") { Shutdown(1); return; }
            MessageBox.Show("Folio konnte nicht starten:\n\n" + e.Exception.Message + "\n\nDetails stehen in " + AppPaths.LogFile,
                "Folio", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
