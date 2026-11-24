using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace Pdb.Install
{
    /// <summary>The PDB server of an installation: python\pythonw.exe server\run_pdb.py install.json.</summary>
    public static class Server
    {
        public static bool Healthy(int port)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create($"http://127.0.0.1:{port}/health");
                req.Timeout = 1500;
                req.Proxy = null;
                using var res = (HttpWebResponse)req.GetResponse();
                return res.StatusCode == HttpStatusCode.OK;
            }
            catch { return false; }
        }

        public static bool PortInUse(int port) =>
            IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);

        public static void Start(InstallConfig c)
        {
            Directory.CreateDirectory(c.DataDir);
            var script = Path.Combine(c.ServerDir, "run_pdb.py");
            Process.Start(new ProcessStartInfo(c.PythonW, $"\"{script}\" \"{c.FilePath}\"")
            {
                WorkingDirectory = c.ServerDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }

        public static bool WaitHealthy(int port, int seconds)
        {
            var until = DateTime.Now.AddSeconds(seconds);
            while (DateTime.Now < until)
            {
                if (Healthy(port)) return true;
                Thread.Sleep(400);
            }
            return false;
        }

        /// <summary>Stops the server this installation started (process id in server.pid).</summary>
        public static void Stop(InstallConfig c)
        {
            try
            {
                if (File.Exists(c.PidFile) && int.TryParse(File.ReadAllText(c.PidFile).Trim(), out var pid))
                {
                    var p = Process.GetProcessById(pid);
                    if (p.ProcessName.StartsWith("python", StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                }
            }
            catch { /* not running any more */ }
            try { File.Delete(c.PidFile); } catch { }
        }
    }

    /// <summary>The PDB Helper (tray app) shipped with the installation.</summary>
    public static class HelperApp
    {
        public static void Start(InstallConfig c)
        {
            if (!File.Exists(c.HelperExe) || Process.GetProcessesByName("PdbHelper").Length > 0) return;
            Process.Start(new ProcessStartInfo(c.HelperExe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(c.HelperExe) });
        }

        /// <summary>Stops only helpers started from this installation's folder.</summary>
        public static void Stop(string installDir)
        {
            foreach (var p in Process.GetProcessesByName("PdbHelper"))
            {
                try
                {
                    if (p.MainModule.FileName.StartsWith(installDir, StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                }
                catch { /* other user / already gone */ }
            }
        }

        static string ConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PDB", "helper.json");

        /// <summary>Points the helper at this PDB (and lets it set up the Mastercam bridge) - unless it
        /// is already configured for another server (e.g. a company PDB), which stays untouched.
        /// Returns a line for the log.</summary>
        public static string Configure(InstallConfig c)
        {
            var ser = new JavaScriptSerializer();
            var d = File.Exists(ConfigPath)
                ? ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(ConfigPath)) ?? new Dictionary<string, object>()
                : new Dictionary<string, object>();
            var url = d.TryGetValue("ServerUrl", out var u) ? u as string : null;
            if (url != null && !IsLocal(url)) return $"PDB Helper stays configured for {url}";
            d["ServerUrl"] = $"http://localhost:{c.Port}";
            if (c.Bridge) d["BridgeManaged"] = true;
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, ser.Serialize(d));
            return $"PDB Helper -> http://localhost:{c.Port}" + (c.Bridge ? " (sets up the Mastercam bridge)" : "");
        }

        static bool IsLocal(string url)
        {
            try
            {
                var host = new Uri(url).Host.ToLowerInvariant();
                return host == "localhost" || host == "127.0.0.1" || host == Environment.MachineName.ToLowerInvariant();
            }
            catch { return true; }
        }
    }

    /// <summary>Shortcuts, pdb:// links, entry in "Apps".</summary>
    public static class Shell
    {
        public static string DesktopDir => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        public static string StartMenuDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "PDB");
        public static string StartupDir => Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        public static readonly string[] StopNames = { "PDB beenden", "Stop PDB", "Arresta PDB", "Sustabdyti PDB" };

        public static void Shortcut(string lnk, string target, string args, string description)
        {
            var type = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic s = shell.CreateShortcut(lnk);
            s.TargetPath = target;
            s.Arguments = args;
            s.WorkingDirectory = Path.GetDirectoryName(target);
            s.Description = description;
            s.IconLocation = target + ",0";
            s.Save();
        }

        public static void RemoveShortcuts()
        {
            foreach (var name in new[] { "PDB" }.Concat(StopNames))
                TryDelete(Path.Combine(DesktopDir, name + ".lnk"));
            TryDelete(Path.Combine(StartupDir, "PDB.lnk"));
            try { if (Directory.Exists(StartMenuDir)) Directory.Delete(StartMenuDir, true); } catch { }
        }

        static void TryDelete(string file) { try { if (File.Exists(file)) File.Delete(file); } catch { } }

        public static void RegisterLinks(string helperExe)
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\pdb");
            key.SetValue("", "URL:PDB Helper");
            key.SetValue("URL Protocol", "");
            using var cmd = key.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", $"\"{helperExe}\" \"%1\"");
        }

        public static void UnregisterLinks(string helperExe)
        {
            using (var cmd = Registry.CurrentUser.OpenSubKey(@"Software\Classes\pdb\shell\open\command"))
            {
                if (cmd?.GetValue("") is not string value || value.IndexOf(helperExe, StringComparison.OrdinalIgnoreCase) < 0) return;
            }
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\pdb", false);
        }

        public static void RegisterUninstall(InstallConfig c)
        {
            long kb = 0;
            try { kb = new DirectoryInfo(c.InstallDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024; } catch { }
            using var key = Registry.CurrentUser.CreateSubKey(InstallConfig.UninstallKey);
            key.SetValue("DisplayName", "PDB");
            key.SetValue("DisplayVersion", c.Version);
            key.SetValue("Publisher", "PDB");
            key.SetValue("InstallLocation", c.InstallDir);
            key.SetValue("DisplayIcon", c.LauncherExe);
            key.SetValue("UninstallString", $"\"{c.LauncherExe}\" --uninstall");
            key.SetValue("QuietUninstallString", $"\"{c.LauncherExe}\" --uninstall --silent");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)Math.Min(kb, int.MaxValue), RegistryValueKind.DWord);
        }

        public static void UnregisterUninstall() => Registry.CurrentUser.DeleteSubKeyTree(InstallConfig.UninstallKey, false);
    }

    public static class Mastercam
    {
        /// <summary>Installed Mastercam versions the bridge supports (2025 and newer).</summary>
        public static List<int> Detect()
        {
            var years = new SortedSet<int>();
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            try
            {
                foreach (var dir in Directory.GetDirectories(pf, "Mastercam*"))
                    foreach (var exeDir in new[] { dir, Path.Combine(dir, "Mastercam") })
                    {
                        var exe = Path.Combine(exeDir, "Mastercam.exe");
                        if (!File.Exists(exe)) continue;
                        var year = 1998 + FileVersionInfo.GetVersionInfo(exe).FileMajorPart;
                        if (year >= 2025) years.Add(year);
                    }
            }
            catch { /* no access */ }
            return years.ToList();
        }
    }
}
