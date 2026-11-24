using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;

namespace Pdb.Install
{
    public sealed class InstallOptions
    {
        public bool ResetDatabase;
        public string? DemoPassword; // set = fill a new database with the sample data
        public bool Start = true;
    }

    public sealed class InstallResult
    {
        public bool NewDatabase;
        public bool Demo;
        public string Log = "";
    }

    /// <summary>The installation itself - used by the wizard and by "PdbSetup.exe --silent".</summary>
    public static class Setup
    {
        public static string Version => typeof(Setup).Assembly.GetName().Version!.ToString(3);
        public static readonly string LogFile = Path.Combine(Path.GetTempPath(), "PDB-Setup.log");

        static void Log(string line)
        {
            try { File.AppendAllText(LogFile, $"{DateTime.Now:HH:mm:ss} {line}\r\n"); } catch { }
        }

        public static InstallResult Install(InstallConfig c, InstallOptions o, Action<int, string> report)
        {
            Log($"== PDB {Version} -> {c.InstallDir}, data {c.DataDir}, port {c.Port}");
            var result = new InstallResult();

            report(2, L.T("PDB wird beendet …"));
            var previous = InstallConfig.FindInstalled();
            foreach (var old in new[] { previous, InstallConfig.Load(c.InstallDir) }.Where(x => x != null))
            {
                Server.Stop(old!);
                HelperApp.Stop(old!.InstallDir);
            }
            HelperApp.Stop(c.InstallDir);

            report(8, L.T("Dateien werden kopiert …"));
            Directory.CreateDirectory(c.InstallDir);
            foreach (var sub in new[] { "python", "server", "helper" })
            {
                var dir = Path.Combine(c.InstallDir, sub);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")
                                 ?? throw new InvalidOperationException("payload.zip missing in this setup"))
            using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
            {
                var entries = zip.Entries.Where(e => e.Name.Length > 0).ToList();
                for (var i = 0; i < entries.Count; i++)
                {
                    var target = Path.Combine(c.InstallDir, entries[i].FullName.Replace('/', '\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entries[i].ExtractToFile(target, overwrite: true);
                    if (i % 200 == 0) report(8 + 52 * i / entries.Count, L.T("Dateien werden kopiert …"));
                }
                Log($"{entries.Count} files");
            }

            report(62, L.T("Einstellungen werden gespeichert …"));
            var docs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PDB");
            c.Version = Version;
            c.SuggestMcam = Path.Combine(docs, "Mastercam");
            c.SuggestNc = Path.Combine(docs, "NC");
            Directory.CreateDirectory(c.DataDir);
            c.Save();
            // left over from the earlier zip package (Start-PDB.cmd): its own Python environment
            foreach (var oldDir in new[] { ".venv" })
                try { var d = Path.Combine(c.DataDir, oldDir); if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }

            report(68, L.T("PDB Helper wird eingerichtet …"));
            // the helper takes the Mastercam bridge (and its own updates) from the server's downloads
            var downloads = Path.Combine(c.DataDir, "files", "downloads");
            Directory.CreateDirectory(downloads);
            var helperDir = Path.GetDirectoryName(c.HelperExe)!;
            foreach (var f in Directory.GetFiles(helperDir).Where(f => !f.EndsWith("PdbHelper.exe", StringComparison.OrdinalIgnoreCase)))
                File.Copy(f, Path.Combine(downloads, Path.GetFileName(f)), true);
            if (c.Helper)
            {
                Log(HelperApp.Configure(c));
                Shell.RegisterLinks(c.HelperExe);
            }
            else
            {
                Shell.UnregisterLinks(c.HelperExe);
            }

            report(74, L.T("Verknüpfungen werden angelegt …"));
            Shell.RemoveShortcuts();
            var stopName = L.T("PDB beenden");
            if (c.DesktopShortcut)
            {
                Shell.Shortcut(Path.Combine(Shell.DesktopDir, "PDB.lnk"), c.LauncherExe, "", L.T("PDB starten und im Browser öffnen"));
                Shell.Shortcut(Path.Combine(Shell.DesktopDir, stopName + ".lnk"), c.LauncherExe, "--stop", L.T("PDB beenden"));
            }
            if (c.StartMenu)
            {
                Directory.CreateDirectory(Shell.StartMenuDir);
                Shell.Shortcut(Path.Combine(Shell.StartMenuDir, "PDB.lnk"), c.LauncherExe, "", L.T("PDB starten und im Browser öffnen"));
                Shell.Shortcut(Path.Combine(Shell.StartMenuDir, stopName + ".lnk"), c.LauncherExe, "--stop", L.T("PDB beenden"));
            }
            if (c.Autostart)
                Shell.Shortcut(Path.Combine(Shell.StartupDir, "PDB.lnk"), c.LauncherExe, "--background", L.T("PDB beim Anmelden starten"));
            Shell.RegisterUninstall(c);

            report(80, L.T("Datenbank wird vorbereitet …"));
            if (o.ResetDatabase && File.Exists(c.DbPath))
            {
                var keep = $"{c.DbPath}.before-reset-{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Move(c.DbPath, keep);
                foreach (var ext in new[] { "-wal", "-shm" })
                    try { File.Delete(c.DbPath + ext); } catch { }
                Log($"old database kept as {keep}");
            }
            result.NewDatabase = !File.Exists(c.DbPath);

            if (o.Start)
            {
                report(85, L.T("PDB wird gestartet …"));
                Server.Start(c);
                if (!Server.WaitHealthy(c.Port, 90))
                    throw new InvalidOperationException(L.F("PDB konnte nicht gestartet werden.\n\nProtokoll: {0}", c.ServerLog));
                if (result.NewDatabase && o.DemoPassword != null)
                {
                    report(92, L.T("Beispieldaten werden angelegt …"));
                    RunDemo(c, o.DemoPassword, docs);
                    result.Demo = true;
                }
                if (c.Helper) HelperApp.Start(c);
            }
            report(100, L.T("Fertig."));
            Log("done");
            try { File.Copy(LogFile, Path.Combine(c.DataDir, "install.log"), true); } catch { }
            return result;
        }

        static void RunDemo(InstallConfig c, string password, string docs)
        {
            var psi = new ProcessStartInfo(c.Python, $"\"{Path.Combine(c.ServerDir, "scripts", "demo_sample.py")}\" http://127.0.0.1:{c.Port}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.EnvironmentVariables["PDB_DEMO_ADMIN_PASSWORD"] = password;
            psi.EnvironmentVariables["PDB_DEMO_MCAM_ROOT"] = c.SuggestMcam;
            psi.EnvironmentVariables["PDB_DEMO_NC_ROOT"] = c.SuggestNc;
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(120000);
            Log(output.Trim());
            if (p.ExitCode != 0) throw new InvalidOperationException(output.Trim());

            // "old programs" to try the import with (placeholders, not real Mastercam files)
            var old = Path.Combine(docs, "Alte Programme");
            foreach (var f in new[] { @"Nordic Robotics\NR-20431_OP1.mcam", @"Nordic Robotics\NR-20431_OP2.mcam", @"Nordic Robotics\NR-20502.mcam",
                                      @"Baltic Marine\BMP-3310_front.mcam", @"Baltic Marine\BMP-3310_back.mcam", @"Haas_VF2\Fixture plate.mcam" })
            {
                var path = Path.Combine(old, f);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (!File.Exists(path)) File.WriteAllText(path, "PDB demo placeholder - not a real Mastercam file");
            }
        }

        /// <summary>PdbSetup.exe --silent [--dir X] [--data Y] [--port N] [--network] [--no-desktop]
        /// [--no-startmenu] [--autostart] [--no-helper] [--no-bridge] [--lang de|en|it|lt] [--reset]
        /// [--demo] (password from PDB_DEMO_ADMIN_PASSWORD) [--no-start]. Exit code 0 = ok.</summary>
        public static int RunSilent(string[] args)
        {
            bool Has(string name) => args.Contains(name);
            var c = FromArgs(InstallConfig.FindInstalled() ?? new InstallConfig(), args);
            var o = new InstallOptions
            {
                ResetDatabase = Has("--reset"),
                DemoPassword = Has("--demo") ? Environment.GetEnvironmentVariable("PDB_DEMO_ADMIN_PASSWORD") : null,
                Start = !Has("--no-start"),
            };
            try
            {
                Install(c, o, (p, s) => Log($"{p,3}% {s}"));
                return 0;
            }
            catch (Exception ex)
            {
                Log("FAILED: " + ex);
                return 1;
            }
        }

        /// <summary>Command line choices (silent install, or preset values in the wizard).</summary>
        public static InstallConfig FromArgs(InstallConfig c, string[] args)
        {
            string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            bool Has(string name) => args.Contains(name);
            c.InstallDir = Arg("--dir") ?? c.InstallDir;
            c.DataDir = Arg("--data") ?? c.DataDir;
            if (int.TryParse(Arg("--port"), out var port)) c.Port = port;
            if (Has("--network")) c.Network = true;
            if (Has("--no-desktop")) c.DesktopShortcut = false;
            if (Has("--no-startmenu")) c.StartMenu = false;
            if (Has("--autostart")) c.Autostart = true;
            if (Has("--no-helper")) c.Helper = false;
            if (Has("--no-bridge")) c.Bridge = false;
            c.Language = L.Lang = Arg("--lang") ?? c.Language;
            return c;
        }
    }
}
