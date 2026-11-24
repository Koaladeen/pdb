using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Mastercam.App;
using Mastercam.App.Types;
using Mastercam.IO;
using Mastercam.Support;

namespace PdbBridge
{
    /// <summary>PDB bridge inside Mastercam: lets the PDB Helper open files in THIS running
    /// Mastercam instead of starting a new one. Set it as "Start/Ende - Systemstart" add-in, or
    /// run it once per session (Alt+C / Add-In ausführen). Running it again shows its status.</summary>
    public class PdbBridgeApp : NetHook3App
    {
        static readonly object Gate = new object();
        static Thread? server;
        static Control? uiThread;          // created on Mastercam's UI thread; used to marshal calls
        static volatile bool stopping;

        public override MCamReturn Init(int param)
        {
            Start();
            return MCamReturn.NoErrors;
        }

        public override MCamReturn Run(int param)
        {
            if (Start()) return MCamReturn.NoErrors; // first start (e.g. at Mastercam startup): silent
            MessageBox.Show(
                "PDB Bridge ist aktiv.\n\nDer PDB Helper öffnet Dateien in diesem Mastercam-Fenster.\n" +
                $"Verbindung: {BridgeProtocol.PipeName(Process.GetCurrentProcess().Id)}",
                "PDB Bridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return MCamReturn.NoErrors;
        }

        public override MCamReturn Close(int param)
        {
            stopping = true;
            return MCamReturn.NoErrors;
        }

        /// <summary>Starts the listener once. Returns true if it was started by this call.</summary>
        static bool Start()
        {
            lock (Gate)
            {
                if (server != null) return false;
                // Init/Run are called on Mastercam's UI thread: a control created here lets the
                // listener thread run Mastercam API calls on the right thread.
                uiThread = new Control();
                uiThread.CreateControl();
                _ = uiThread.Handle;
                stopping = false;
                server = new Thread(Listen) { IsBackground = true, Name = "PdbBridge" };
                server.Start();
                Log("started " + BridgeProtocol.PipeName(Process.GetCurrentProcess().Id));
                return true;
            }
        }

        static void Listen()
        {
            var name = BridgeProtocol.PipeName(Process.GetCurrentProcess().Id);
            while (!stopping)
            {
                try
                {
                    using (var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.None))
                    {
                        pipe.WaitForConnection();
                        var reader = new StreamReader(pipe, new UTF8Encoding(false));
                        var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                        var request = reader.ReadLine() ?? "";
                        writer.WriteLine(Handle(request));
                        try { pipe.WaitForPipeDrain(); } catch (IOException) { }
                    }
                }
                catch (Exception ex)
                {
                    Log("listener: " + ex.Message);
                    Thread.Sleep(500);
                }
            }
        }

        static string Handle(string request)
        {
            var parts = request.Split(new[] { '\t' }, 2);
            var cmd = parts[0].Trim().ToUpperInvariant();
            try
            {
                switch (cmd)
                {
                    case "PING":
                        return "OK\t" + typeof(PdbBridgeApp).Assembly.GetName().Version + "\t" + OnUi(() => FileManager.CurrentFileName ?? "");
                    case "CURRENT":
                        return "OK\t" + OnUi(() => FileManager.CurrentFileName ?? "");
                    case "OPEN":
                        var path = parts.Length > 1 ? parts[1].Trim() : "";
                        if (path.Length == 0 || !File.Exists(path)) return "ERR\tDatei nicht gefunden: " + path;
                        var ok = OnUi(() =>
                        {
                            BringToFront();
                            // true = Mastercam asks whether to save the current file first
                            return FileManager.Open(true, path);
                        });
                        Log("open " + path + " -> " + ok);
                        return ok ? "OK\t" + path : "ERR\tNicht geöffnet (abgebrochen?)";

                    case "MACHINE":
                        // machine definition (.mcam-mmd) of the active machine group, "" if none
                        return "OK\t" + OnUi(() => GroupManager.GetMachineNameInActiveGroup() ?? "");

                    case "SETMACHINE":
                    {
                        var mmd = parts.Length > 1 ? parts[1].Trim() : "";
                        if (mmd.Length == 0 || !File.Exists(mmd)) return "ERR\tMaschinendefinition nicht gefunden: " + mmd;
                        var changed = OnUi(() => SetMachine(mmd));
                        Log("setmachine " + mmd + " -> " + changed + " group(s)");
                        return changed > 0 ? "OK\t" + changed : "ERR\tKeine Maschinengruppe umgestellt";
                    }

                    case "NEWFILE":
                    {
                        // NEWFILE <target .mcam> <machine definition (optional)>
                        var args = (parts.Length > 1 ? parts[1] : "").Split('\t');
                        var target = args[0].Trim();
                        var mmd = args.Length > 1 ? args[1].Trim() : "";
                        if (target.Length == 0) return "ERR\tKein Zielpfad";
                        if (File.Exists(target)) return "ERR\tDatei existiert schon: " + target;
                        if (mmd.Length > 0 && !File.Exists(mmd)) return "ERR\tMaschinendefinition nicht gefunden: " + mmd;
                        var created = OnUi(() =>
                        {
                            BringToFront();
                            if (!FileManager.New(true)) return "Neue Datei abgebrochen";
                            if (mmd.Length > 0 && !MachineDefManager.CreateMachineGroup(ProductFor(mmd), mmd))
                                return "Maschinengruppe nicht angelegt";
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            return FileManager.SaveAs(target, true) ? null : "Nicht gespeichert";
                        });
                        Log("newfile " + target + " (" + mmd + ") -> " + (created ?? "ok"));
                        return created == null ? "OK\t" + target : "ERR\t" + created;
                    }

                    case "SAVEAS":
                    {
                        var target = parts.Length > 1 ? parts[1].Trim() : "";
                        if (target.Length == 0) return "ERR\tKein Zielpfad";
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        var saved = OnUi(() => FileManager.SaveAs(target, true));
                        Log("saveas " + target + " -> " + saved);
                        return saved ? "OK\t" + target : "ERR\tNicht gespeichert: " + target;
                    }
                    default:
                        return "ERR\tUnbekannter Befehl: " + cmd;
                }
            }
            catch (Exception ex)
            {
                Log("handle " + cmd + ": " + ex);
                return "ERR\t" + ex.Message.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
            }
        }

        /// <summary>Puts the machine definition into every machine group of the open file
        /// (a file can hold several). Returns how many groups were switched.</summary>
        static int SetMachine(string mmd)
        {
            var groups = new System.Collections.Generic.List<Mastercam.Support.Group>();
            GroupManager.GetAllGroups(groups, true);
            var count = 0;
            foreach (var g in groups)
                if (g != null && g.IsMachineGroup() && GroupManager.SetMachineInGroup(g, mmd)) count++;
            if (count == 0 && GroupManager.SetMachineInActiveGroup(mmd)) count = 1;
            return count;
        }

        /// <summary>Product from the machine definition's extension.</summary>
        static Mastercam.Database.Types.ProductType ProductFor(string mmd)
        {
            var ext = Path.GetExtension(mmd).ToLowerInvariant();
            if (ext == ".mcam-lmd") return Mastercam.Database.Types.ProductType.Lathe;
            if (ext == ".mcam-rmd") return Mastercam.Database.Types.ProductType.Router;
            if (ext == ".mcam-wmd") return Mastercam.Database.Types.ProductType.Wire;
            return Mastercam.Database.Types.ProductType.Mill;
        }

        static T OnUi<T>(Func<T> f)
        {
            var c = uiThread;
            if (c == null || c.IsDisposed) throw new InvalidOperationException("Bridge nicht bereit");
            return (T)c.Invoke(f);
        }

        static void BringToFront()
        {
            var main = Process.GetCurrentProcess().MainWindowHandle;
            if (main == IntPtr.Zero) return;
            if (IsIconic(main)) ShowWindow(main, 9 /* SW_RESTORE */);
            SetForegroundWindow(main);
        }

        static void Log(string message)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PDB", "logs");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "bridge-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + Process.GetCurrentProcess().Id + "] " + message + Environment.NewLine);
            }
            catch
            {
                // never disturb Mastercam because of logging
            }
        }

        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    }
}
