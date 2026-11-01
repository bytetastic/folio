using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Folio.Services;

namespace Folio;

/// <summary>Installieren und Aktualisieren (wie bei Axon: Prüfung nur beim Öffnen, GitHub releases/latest).</summary>
public partial class MainWindow
{
    UpdateInfo? _update;
    bool _updating;

    /// <summary>Nicht installierte Kopie (z. B. frisch heruntergeladen): einmal anbieten, Folio zu installieren.</summary>
    async Task MaybeOfferInstall()
    {
        if (AppPaths.InstanceKey != "main" && AppPaths.InstallTestRoot == null) return;
        if (Installer.IsRunningInstalled || Installer.IsInstalled || _settings.InstallPromptDismissed) return;
        await Task.Delay(900);
        ShowInfo("", "Folio installieren?",
            "Mit Startmenü-Eintrag, „Öffnen mit“ für .md/.txt und automatischen Updates. Ohne Adminrechte.",
            null,
            ("Später", () => { _settings.InstallPromptDismissed = true; _settings.Save(); HideInfo(); }, false),
            ("Installieren …", () => _ = AskInstall(), true));
    }

    async Task AskInstall()
    {
        var desktop = new CheckBox { Content = "Verknüpfung auf dem Desktop", IsChecked = true, Style = (Style)FindResource("DialogCheck") };
        int r0 = await ShowDialog("Folio installieren",
            "Folio kopiert sich nach %LOCALAPPDATA%\\Programs\\Folio, legt einen Startmenü-Eintrag an und erscheint bei „Öffnen mit“ für .md- und .txt-Dateien. Deine Standard-Apps ändert Folio nicht.",
            new UIElement[] { desktop }, "Installieren", "Abbrechen");
        if (r0 != 0) return;
        try
        {
            Installer.Install(desktop.IsChecked == true);
        }
        catch (Exception ex)
        {
            AppPaths.Log("Installation: " + ex);
            await ShowDialog("Installation fehlgeschlagen", ex.Message, null, "OK");
            return;
        }
        HideInfo();
        int r = await ShowDialog("Folio ist installiert",
            "Du findest Folio jetzt im Startmenü. Soll die installierte Version gleich übernehmen? Deine offenen Tabs bleiben erhalten.",
            null, "Jetzt wechseln", "Später");
        if (r != 0) return;
        if (_current != null) RememberView(_current);
        SaveSession();
        try { Installer.LaunchInstalled(); } catch (Exception ex) { AppPaths.Log("Start installierte Version: " + ex.Message); return; }
        Close();
    }

    /// <summary>Desktop-Verknüpfung der installierten Version an- oder abschalten (Menü).</summary>
    void SetDesktopLink(bool on)
    {
        try
        {
            Installer.SetDesktopLink(on);
            ShowToast(on ? "Verknüpfung auf dem Desktop angelegt" : "Verknüpfung vom Desktop entfernt");
        }
        catch (Exception ex)
        {
            AppPaths.Log("Desktop-Verknüpfung: " + ex.Message);
            _ = ShowDialog("Verknüpfung nicht möglich", ex.Message, null, "OK");
        }
    }

    /// <summary>Einmal beim Öffnen: bei GitHub nach einer neueren Version fragen. Ohne Netz oder bei Fehlern passiert nichts.</summary>
    async Task CheckForUpdate()
    {
        bool test = Environment.GetEnvironmentVariable("FOLIO_UPDATE_TEST") == "1";
        if (!_settings.UpdateCheck || (AppPaths.InstanceKey != "main" && !test)) return;
        await Task.Delay(TimeSpan.FromSeconds(test ? 1 : 3)); // Start nicht ausbremsen
        var info = await UpdateChecker.CheckAsync();
        if (info == null) return;
        _update = info;
        AppPaths.Log($"Update verfügbar: {info.Version}");
        UpdateButton.Content = $"Update {info.Version}";
        UpdateButton.Visibility = Visibility.Visible;
        UpdateButton.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(500)));
        ShowInfo("", $"Folio {info.Version} ist verfügbar", NotesExcerpt(info.Notes), null,
            ("Später", HideInfo, false),
            ("Jetzt aktualisieren", () => _ = OfferUpdate(skipQuestion: true), true));
        if (Environment.GetEnvironmentVariable("FOLIO_TEST_ACCEPT_UPDATE") == "1") await OfferUpdate(skipQuestion: true);
    }

    async Task OfferUpdate(bool skipQuestion = false)
    {
        var info = _update;
        if (info == null || _updating) return;

        if (!Installer.IsRunningInstalled)
        {
            int choice = await ShowDialog($"Folio {info.Version} ist verfügbar",
                "Diese Kopie von Folio ist nicht installiert. Lade die neue Version von GitHub herunter und starte sie – sie bietet dann die Installation an.",
                null, "Download-Seite öffnen", "Später");
            if (choice == 0) OpenLink(info.PageUrl);
            return;
        }

        if (!skipQuestion)
        {
            int r = await ShowDialog($"Folio {info.Version} ist verfügbar",
                $"Du nutzt Version {AppPaths.Version}.\n{NotesExcerpt(info.Notes)}\n\nFolio lädt die neue Version (≈ {info.Size / 1_048_576.0:0} MB) und startet kurz neu. Offene Tabs bleiben erhalten.",
                null, "Jetzt aktualisieren", "Später");
            if (r != 0) return;
        }

        _updating = true;
        HideInfo();
        UpdateButton.IsEnabled = false;
        UpdateButton.Content = "Lädt … 0 %";
        try
        {
            var progress = new Progress<double>(p => UpdateButton.Content = $"Lädt … {p * 100:0} %");
            string exe = await UpdateChecker.DownloadAsync(info, progress);
            UpdateButton.Content = "Startet neu …";
            if (!await HandOverToUpdate(exe)) ResetUpdateButton();
        }
        catch (Exception ex)
        {
            AppPaths.Log("Update-Download: " + ex);
            ResetUpdateButton();
            await ShowDialog("Update fehlgeschlagen", ex.Message + "\n\nDu kannst die neue Version auch von der GitHub-Seite herunterladen.", null, "OK");
        }
    }

    /// <summary>Eine neuere Folio.exe wurde gestartet, während dieses Folio läuft.</summary>
    async Task OfferLocalUpdate(string exe)
    {
        BringToFront();
        int r = await ShowDialog("Neue Version gestartet",
            "Du hast eine neuere Folio.exe geöffnet. Soll sie die installierte Version ersetzen? Offene Tabs bleiben erhalten.",
            null, "Aktualisieren", "Abbrechen");
        if (r == 0) await HandOverToUpdate(exe);
    }

    void ResetUpdateButton()
    {
        _updating = false;
        UpdateButton.IsEnabled = true;
        UpdateButton.Content = _update != null ? $"Update {_update.Version}" : "Update";
    }

    async Task<bool> HandOverToUpdate(string newExe)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(newExe, $"--apply-update {Environment.ProcessId}") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            await ShowDialog("Update fehlgeschlagen", ex.Message, null, "OK");
            return false;
        }
        Close(); // sichert Sitzung und Fenster
        return true;
    }

    /// <summary>Die ersten Zeilen der Release-Notizen, ohne Markdown-Zeichen.</summary>
    static string NotesExcerpt(string notes)
    {
        var lines = notes.Replace("\r", "").Split('\n')
            .Select(l => l.Trim().TrimStart('#', '-', '*', ' ').Replace("**", "").Replace("`", ""))
            .Where(l => l.Length > 0)
            .Take(4)
            .ToList();
        return string.Join("\n", lines);
    }
}
