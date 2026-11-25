using System;
using System.IO;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace Pdb.Install
{
    /// <summary>Settings of one installation, saved as install.json in the program folder. Read by
    /// PDB.exe (start/stop/uninstall), by server\run_pdb.py (data folder, port) and by the next setup
    /// (update keeps the choices).</summary>
    public sealed class InstallConfig
    {
        public string Version { get; set; } = "";
        public string InstallDir { get; set; } = DefaultInstallDir;
        public string DataDir { get; set; } = DefaultDataDir;
        public int Port { get; set; } = 8000;
        public bool Network { get; set; }
        public bool Helper { get; set; } = true;
        public bool Bridge { get; set; } = true;
        public bool DesktopShortcut { get; set; } = true;
        public bool StartMenu { get; set; } = true;
        public bool Autostart { get; set; }
        public string Language { get; set; } = "de";
        public string SuggestMcam { get; set; } = "";
        public string SuggestNc { get; set; } = "";

        /// <summary>Portable: everything in one folder (program next to PDB.exe, data in "data"),
        /// paths in install.json relative to that folder, no Start menu or "Apps" entry.</summary>
        public bool Portable { get; set; }

        static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        public static string DefaultInstallDir => Path.Combine(LocalAppData, "Programs", "PDB");
        // same as the earlier zip package (Start-PDB.cmd): an existing database is found and kept
        public static string DefaultDataDir => Path.Combine(LocalAppData, "PDB", "server");

        [ScriptIgnore] public string FilePath => Path.Combine(InstallDir, "install.json");
        [ScriptIgnore] public string Url => $"http://localhost:{Port}/";
        [ScriptIgnore] public string DbPath => Path.Combine(DataDir, "pdb.sqlite3");
        [ScriptIgnore] public string ServerLog => Path.Combine(DataDir, "server.log");
        [ScriptIgnore] public string PidFile => Path.Combine(DataDir, "server.pid");
        [ScriptIgnore] public string PythonW => Path.Combine(InstallDir, "python", "pythonw.exe");
        [ScriptIgnore] public string Python => Path.Combine(InstallDir, "python", "python.exe");
        [ScriptIgnore] public string ServerDir => Path.Combine(InstallDir, "server");
        [ScriptIgnore] public string HelperExe => Path.Combine(InstallDir, "helper", "PdbHelper.exe");
        [ScriptIgnore] public string LauncherExe => Path.Combine(InstallDir, "PDB.exe");

        public void Save()
        {
            Directory.CreateDirectory(InstallDir);
            File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(this));
        }

        public static InstallConfig? Load(string installDir)
        {
            var path = Path.Combine(installDir, "install.json");
            if (!File.Exists(path)) return null;
            try
            {
                var c = new JavaScriptSerializer().Deserialize<InstallConfig>(File.ReadAllText(path));
                c.InstallDir = installDir.TrimEnd('\\');
                // portable: relative paths count from the folder, wherever it was copied to
                c.DataDir = Resolve(c.InstallDir, c.DataDir);
                if (c.SuggestMcam.Length > 0) c.SuggestMcam = Resolve(c.InstallDir, c.SuggestMcam);
                if (c.SuggestNc.Length > 0) c.SuggestNc = Resolve(c.InstallDir, c.SuggestNc);
                return c;
            }
            catch { return null; }
        }

        static string Resolve(string dir, string path) => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(dir, path));

        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PDB";

        /// <summary>The installation registered in "Apps" for this Windows user, if any.</summary>
        public static InstallConfig? FindInstalled()
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            var dir = key?.GetValue("InstallLocation") as string;
            return string.IsNullOrEmpty(dir) ? null : Load(dir!);
        }
    }
}
