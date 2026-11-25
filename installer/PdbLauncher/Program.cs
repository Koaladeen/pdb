using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Pdb.Install
{
    /// <summary>PDB.exe - what the shortcuts run:
    ///   PDB.exe               start PDB if needed (small "starting" window) and open it in the browser
    ///   PDB.exe --background  start if needed, no window, no browser (autostart at login)
    ///   PDB.exe --stop        stop PDB and its PDB Helper
    ///   PDB.exe --uninstall [--silent] [--delete-data]   remove PDB (from Windows "Apps")</summary>
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var silent = args.Contains("--silent");
            var cfg = InstallConfig.Load(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'));
            if (cfg == null)
            {
                if (!silent) MessageBox.Show(L.T("PDB ist nicht installiert (install.json fehlt)."), "PDB");
                return 1;
            }
            L.Lang = string.IsNullOrEmpty(cfg.Language) ? L.Detect() : cfg.Language;

            if (args.Contains("--stop"))
            {
                Server.Stop(cfg);
                HelperApp.Stop(cfg.InstallDir);
                if (!silent) MessageBox.Show(L.T("PDB ist beendet."), "PDB", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            if (args.Contains("--uninstall"))
                return cfg.Portable ? Detach(cfg, silent) : Uninstall(cfg, silent, args.Contains("--delete-data"));

            if (cfg.Portable && !PreparePortable(cfg, silent)) return 1;
            var background = args.Contains("--background");
            if (!Server.Healthy(cfg.Port))
            {
                Server.Start(cfg);
                var ok = background ? Server.WaitHealthy(cfg.Port, 90) : WaitWithWindow(cfg.Port);
                if (!ok)
                {
                    if (!background)
                        MessageBox.Show(L.F("PDB konnte nicht gestartet werden.\n\nProtokoll: {0}", cfg.ServerLog), "PDB", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
            }
            if (cfg.Helper) HelperApp.Start(cfg);
            if (!background) Process.Start(cfg.Url);
            return 0;
        }

        /// <summary>Portable folder: what an installation does once, done at every start (cheap) - the
        /// folder may have been copied to another place or PC, or another Windows user starts it.</summary>
        static bool PreparePortable(InstallConfig cfg, bool silent)
        {
            try
            {
                Directory.CreateDirectory(cfg.DataDir);
                File.WriteAllText(Path.Combine(cfg.DataDir, ".write-test"), "");
                File.Delete(Path.Combine(cfg.DataDir, ".write-test"));
            }
            catch (Exception ex)
            {
                if (!silent)
                    MessageBox.Show(L.F("In diesen Ordner kann nicht geschrieben werden:\n{0}\n\nDen PDB-Ordner bitte an einen Ort kopieren, an dem du schreiben darfst (z. B. C:\\PDB oder Dokumente).", cfg.DataDir)
                        + "\n\n" + ex.Message, "PDB", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            // the helper takes the Mastercam bridge from the server's downloads
            var downloads = Path.Combine(cfg.DataDir, "files", "downloads");
            var helperDir = Path.GetDirectoryName(cfg.HelperExe)!;
            if (Directory.Exists(helperDir))
            {
                Directory.CreateDirectory(downloads);
                foreach (var f in Directory.GetFiles(helperDir).Where(f => !f.EndsWith("PdbHelper.exe", StringComparison.OrdinalIgnoreCase)))
                {
                    var target = Path.Combine(downloads, Path.GetFileName(f));
                    if (!File.Exists(target) || File.GetLastWriteTimeUtc(target) != File.GetLastWriteTimeUtc(f)) File.Copy(f, target, true);
                }
            }
            if (cfg.Helper && File.Exists(cfg.HelperExe))
            {
                HelperApp.Configure(cfg);
                Shell.RegisterLinks(cfg.HelperExe); // "Öffnen" on the page -> this folder's helper
            }
            return true;
        }

        /// <summary>Portable: undo what PreparePortable registered for this Windows user. The folder
        /// itself (program and data) stays; deleting it removes PDB completely.</summary>
        static int Detach(InstallConfig cfg, bool silent)
        {
            if (!silent && MessageBox.Show(L.T("PDB wird beendet und von diesem Windows-Benutzer gelöst (Link für „Öffnen“). Der PDB-Ordner mit deinen Daten bleibt unverändert. Fortfahren?"),
                    "PDB", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 1;
            Server.Stop(cfg);
            HelperApp.Stop(cfg.InstallDir);
            Shell.UnregisterLinks(cfg.HelperExe);
            if (!silent)
                MessageBox.Show(L.F("Erledigt. Um PDB ganz zu entfernen, den Ordner löschen:\n{0}\n\nAchtung: darin liegt auch die Datenbank (Ordner „data“).", cfg.InstallDir),
                    "PDB", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        static bool WaitWithWindow(int port)
        {
            var ok = false;
            using var form = new Form
            {
                Text = "PDB",
                FormBorderStyle = FormBorderStyle.FixedToolWindow,
                StartPosition = FormStartPosition.CenterScreen,
                ClientSize = new Size(320, 80),
                Font = new Font("Segoe UI", 9f),
                ControlBox = false,
                ShowInTaskbar = true,
            };
            form.Controls.Add(new Label { Text = L.T("PDB wird gestartet …"), AutoSize = true, Location = new Point(16, 14) });
            form.Controls.Add(new ProgressBar { Style = ProgressBarStyle.Marquee, Location = new Point(16, 40), Width = 288, Height = 18 });
            form.Shown += (_, _) => new Thread(() =>
            {
                ok = Server.WaitHealthy(port, 90);
                form.BeginInvoke((Action)form.Close);
            }) { IsBackground = true }.Start();
            Application.Run(form);
            return ok;
        }

        static int Uninstall(InstallConfig cfg, bool silent, bool deleteData)
        {
            if (!silent)
            {
                if (MessageBox.Show(L.T("PDB wird von diesem PC entfernt (Programm, Verknüpfungen, Autostart). Fortfahren?"), L.T("PDB deinstallieren"),
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 1;
                deleteData = MessageBox.Show(L.F("Auch deine Daten löschen (Datenbank, Sicherungen, Bilder)? Das kann nicht rückgängig gemacht werden.\n\nNein = die Daten bleiben in {0}.", cfg.DataDir),
                    L.T("PDB deinstallieren"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
            }
            Server.Stop(cfg);
            HelperApp.Stop(cfg.InstallDir);
            Shell.RemoveShortcuts();
            Shell.UnregisterLinks(cfg.HelperExe);
            Shell.UnregisterUninstall();
            if (deleteData) DeleteData(cfg.DataDir);

            // the program folder (with this exe) goes once we have ended
            if (File.Exists(cfg.FilePath) && File.Exists(cfg.LauncherExe))
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{cfg.InstallDir}\"")
                    { CreateNoWindow = true, UseShellExecute = false });
            if (!silent)
                MessageBox.Show(L.T("PDB wurde entfernt.") + (deleteData ? "" : "\n\n" + L.F("Deine Daten liegen weiterhin in {0}.", cfg.DataDir)), "PDB",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        /// <summary>Only what PDB itself put into the data folder - never an unknown folder's content.</summary>
        static void DeleteData(string dir)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir).Where(f =>
            {
                var n = Path.GetFileName(f).ToLowerInvariant();
                return n.StartsWith("pdb.sqlite3") || n.StartsWith("server.") || n == "install.log" || n == "launcher.json" || n == "requirements.done";
            }))
                try { File.Delete(f); } catch { }
            foreach (var sub in new[] { "files", ".venv" })
                try { var d = Path.Combine(dir, sub); if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
            try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
        }
    }
}
