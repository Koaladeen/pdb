using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace PdbHelper;

/// <summary>What the helper needs from Mastercam. Swappable, e.g. for tests.</summary>
public interface IMastercam
{
    void Open(string file);
    bool StartEmpty();

    /// <summary>Is the file open in a Mastercam on this PC? Unknown when a Mastercam with the
    /// bridge does not answer (busy, e.g. calculating toolpaths) - never close a file then.</summary>
    OpenState State(string file);

    /// <summary>Machine definition (.mcam-mmd) of the file if it is open in a Mastercam with
    /// the bridge; null if that cannot be told.</summary>
    string? MachineOf(string file);

    /// <summary>Switch the open file to another machine definition. Null = done, else the error.</summary>
    string? SetMachine(string file, string machineDefinition);

    /// <summary>Save the open file under a new path. Null = done, else the error.</summary>
    string? SaveAs(string file, string target);

    /// <summary>New operation: create the .mcam (new file, machine group from the machine
    /// definition, saved at target) - starting Mastercam if needed.</summary>
    NewFileOutcome CreateNew(string target, string machineDefinition);
}

public enum OpenState { Closed, Open, Unknown }

/// <summary>Error null = created. UserSaves = Mastercam runs without the bridge: the user saves
/// the new file under the path himself, so the lock stays for a while.</summary>
public sealed record NewFileOutcome(string? Error, bool UserSaves = false);

/// <summary>Opening files in Mastercam and finding out whether a file is still open.
/// With the PDB bridge add-in loaded in Mastercam, files open in the running Mastercam and the
/// bridge reports the open file exactly. Without it: new Mastercam window, and the window title
/// is used to notice closing.</summary>
public sealed class Mastercam(HelperConfig config) : IMastercam
{
    /// <summary>Asks which Mastercam to start when none is running (set by the tray app, which
    /// shows the window). Null answer = cancelled.</summary>
    public static Func<IReadOnlyList<MastercamInstall>, MastercamInstall?>? ChooseVersion { get; set; }

    public void Open(string file)
    {
        var pid = Bridge.Find();
        if (pid is { } p)
        {
            // The open may wait for "save current file?" in Mastercam: don't block the caller.
            _ = Task.Run(() =>
            {
                var answer = Bridge.Send(p, $"OPEN\t{file}", readTimeoutMs: 10 * 60_000);
                Log.Write($"Opened via bridge in Mastercam {p}: {file} -> {answer ?? "no answer"}");
            });
            return;
        }
        if (!AnyRunning())
        {
            // nothing running: ask which version (unless one is set as "always")
            if (StartExe() is not { } chosen)
            {
                Log.Write($"Mastercam start cancelled: {file}");
                return;
            }
            Process.Start(new ProcessStartInfo(chosen, $"\"{file}\"") { UseShellExecute = false });
            Log.Write($"Started {chosen} with {file}");
            return;
        }
        if (ExePath() is { } exe)
            Process.Start(new ProcessStartInfo(exe, $"\"{file}\"") { UseShellExecute = false });
        else
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); // Windows file association
        Log.Write($"Opened in new Mastercam (running one has no bridge): {file}");
    }

    /// <summary>For an operation without a file yet: start Mastercam empty, or just bring an
    /// open Mastercam with the bridge to the front.</summary>
    public bool StartEmpty()
    {
        if (Bridge.Find() is { } pid)
        {
            Bridge.Send(pid, "PING"); // the bridge brings nothing to front on PING; activate here
            Bridge.Activate(pid);
            return true;
        }
        var exe = AnyRunning() ? ExePath() ?? RunningExe() : StartExe();
        if (exe is null) return false;
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
        return true;
    }

    /// <summary>Mastercam.exe to start: the one set as "always", else ask (one version installed:
    /// no question).</summary>
    string? StartExe()
    {
        if (ExePath() is { } fixedExe) return fixedExe;
        var installs = BridgeSetup.Detect(minYear: 2018);
        if (installs.Count == 0) return null;
        if (installs.Count == 1) return installs[0].Exe;
        var chosen = ChooseVersion?.Invoke(installs);
        return chosen?.Exe;
    }

    static bool AnyRunning()
    {
        var ps = Process.GetProcessesByName("Mastercam");
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }

    /// <summary>Asks every running Mastercam: one with the bridge says exactly which file it has
    /// open; one with the bridge loaded that does not answer is busy (Unknown); one without the
    /// bridge is judged by its window title.</summary>
    public OpenState State(string file)
    {
        var pids = Bridge.Candidates();
        if (pids.Count == 0) return OpenState.Closed;
        var pipes = Bridge.Pipes();
        var unsure = false;
        foreach (var pid in pids)
        {
            var answer = Bridge.Send(pid, "CURRENT", connectTimeoutMs: 500, readTimeoutMs: 3000);
            if (answer is not null && answer.StartsWith("OK"))
            {
                if (SameFile(answer.Split('\t', 2).ElementAtOrDefault(1) ?? "", file)) return OpenState.Open;
                continue; // this Mastercam has another file (or none) open
            }
            if (pipes.Contains(pid))
            {
                unsure = true; // bridge loaded but busy
                continue;
            }
            if (TitleOf(pid).Contains(Path.GetFileNameWithoutExtension(file), StringComparison.OrdinalIgnoreCase)) return OpenState.Open;
        }
        return unsure ? OpenState.Unknown : OpenState.Closed;
    }

    /// <summary>Same path, or the same file name: PDB file names are unique (00084197.mcam) and
    /// Mastercam may report a drive letter where PDB has the network name, or the other way round.</summary>
    internal static bool SameFile(string current, string file) =>
        SamePath(current, file)
        || (current.Length > 0 && string.Equals(Path.GetFileName(current), Path.GetFileName(file), StringComparison.OrdinalIgnoreCase));

    static string TitleOf(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.MainWindowTitle;
        }
        catch (ArgumentException) { return ""; }         // ended meanwhile
        catch (InvalidOperationException) { return ""; }
    }

    public string? MachineOf(string file)
    {
        if (BridgeFor(file) is not { } pid) return null;
        var answer = Bridge.Send(pid, "MACHINE");
        return answer is not null && answer.StartsWith("OK") ? answer.Split('\t', 2).ElementAtOrDefault(1) ?? "" : null;
    }

    public string? SetMachine(string file, string machineDefinition)
    {
        if (BridgeFor(file) is not { } pid) return "Datei ist in keinem Mastercam mit PDB-Bridge geöffnet";
        var answer = Bridge.Send(pid, $"SETMACHINE\t{machineDefinition}", readTimeoutMs: 120_000);
        return answer is not null && answer.StartsWith("OK") ? null : Error(answer);
    }

    public string? SaveAs(string file, string target)
    {
        if (BridgeFor(file) is not { } pid) return "Datei ist in keinem Mastercam mit PDB-Bridge geöffnet";
        var answer = Bridge.Send(pid, $"SAVEAS\t{target}", readTimeoutMs: 300_000);
        return answer is not null && answer.StartsWith("OK") ? null : Error(answer);
    }

    public NewFileOutcome CreateNew(string target, string machineDefinition)
    {
        var pid = Bridge.Find();
        if (pid is null)
        {
            if (AnyRunning()) return new("Mastercam läuft ohne PDB-Bridge – Datei bitte selbst speichern unter: " + target, UserSaves: true);
            if (StartExe() is not { } exe) return new("Mastercam-Start abgebrochen");
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
            Log.Write($"Started {exe} for a new file, waiting for the bridge");
            var until = DateTime.Now.AddMinutes(3); // Mastercam takes a while (licence, add-ins)
            while (pid is null && DateTime.Now < until)
            {
                Thread.Sleep(2000);
                pid = Bridge.Find();
            }
            if (pid is null) return new("Mastercam ist gestartet, aber ohne PDB-Bridge – Datei bitte selbst speichern unter: " + target, UserSaves: true);
        }
        var answer = Bridge.Send(pid.Value, $"NEWFILE\t{target}\t{machineDefinition}", readTimeoutMs: 10 * 60_000);
        Log.Write($"New file via bridge: {target} ({machineDefinition}) -> {answer ?? "no answer"}");
        return new(answer is not null && answer.StartsWith("OK") ? null : Error(answer));
    }

    static string Error(string? answer)
    {
        if (answer is null) return "Mastercam antwortet nicht";
        var text = answer.Split('\t', 2).ElementAtOrDefault(1) ?? answer;
        // an older bridge in Mastercam does not know newer commands (e.g. NEWFILE before 0.3.0)
        return text.StartsWith("Unbekannter Befehl", StringComparison.Ordinal)
            ? "Die PDB-Bridge in Mastercam ist zu alt für diese Funktion – bitte die aktuelle PdbBridge.dll einspielen und Mastercam neu starten"
            : text;
    }

    /// <summary>The Mastercam (with bridge) that has exactly this file open.</summary>
    static int? BridgeFor(string file)
    {
        foreach (var pid in Bridge.Candidates())
        {
            var answer = Bridge.Send(pid, "CURRENT", connectTimeoutMs: 300);
            if (answer is not null && answer.StartsWith("OK") && SamePath(answer.Split('\t', 2).ElementAtOrDefault(1) ?? "", file))
                return pid;
        }
        return null;
    }

    static bool SamePath(string a, string b)
    {
        if (a.Length == 0) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    string? ExePath() =>
        !string.IsNullOrWhiteSpace(config.MastercamExe) && File.Exists(config.MastercamExe) ? config.MastercamExe : null;

    static string? RunningExe()
    {
        foreach (var p in Process.GetProcessesByName("Mastercam"))
        {
            try { return p.MainModule?.FileName; }
            catch (Exception) { /* access denied or ended */ }
            finally { p.Dispose(); }
        }
        return null;
    }
}

/// <summary>Talks to the PDB bridge add-in (helper/PdbBridge) in a running Mastercam over a
/// local named pipe "PdbBridge-&lt;process id&gt;". Protocol: see PdbBridge/BridgeProtocol.cs.</summary>
public static class Bridge
{
    const string PipePrefix = "PdbBridge-";

    /// <summary>Process ids of the Mastercams that have the bridge loaded (its pipe exists),
    /// whether or not they answer right now.</summary>
    public static HashSet<int> Pipes()
    {
        var ids = new HashSet<int>();
        try
        {
            foreach (var pipe in Directory.EnumerateFiles(@"\\.\pipe\"))
            {
                var name = Path.GetFileName(pipe);
                if (name.StartsWith(PipePrefix, StringComparison.OrdinalIgnoreCase) && int.TryParse(name[PipePrefix.Length..], out var id)) ids.Add(id);
            }
        }
        catch (Exception)
        {
            // pipes cannot be listed: a silent Mastercam then counts as one without the bridge
        }
        return ids;
    }

    /// <summary>Mastercam processes, the one with the frontmost window first.</summary>
    public static List<int> Candidates()
    {
        var mastercams = Process.GetProcessesByName("Mastercam").Select(p => { var id = p.Id; p.Dispose(); return id; }).ToHashSet();
        var ordered = new List<int>();
        if (mastercams.Count == 0) return ordered;
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd))
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                if (mastercams.Contains((int)pid) && !ordered.Contains((int)pid)) ordered.Add((int)pid);
            }
            return true;
        }, IntPtr.Zero);
        ordered.AddRange(mastercams.Where(id => !ordered.Contains(id)));
        return ordered;
    }

    /// <summary>The frontmost Mastercam that has the bridge running, or null.</summary>
    public static int? Find()
    {
        foreach (var pid in Candidates())
            if (Send(pid, "PING", connectTimeoutMs: 500) is { } a && a.StartsWith("OK")) return pid;
        return null;
    }

    /// <summary>One request, one answer line; null if no bridge answers.</summary>
    public static string? Send(int pid, string line, int connectTimeoutMs = 1500, int readTimeoutMs = 15_000)
    {
        try
        {
            // let that Mastercam take the foreground when it opens a file
            AllowSetForegroundWindow(pid);
            using var pipe = new NamedPipeClientStream(".", PipePrefix + pid, PipeDirection.InOut);
            pipe.Connect(connectTimeoutMs);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            var reader = new StreamReader(pipe, new UTF8Encoding(false));
            writer.WriteLine(line);
            var read = reader.ReadLineAsync();
            return read.Wait(readTimeoutMs) ? read.Result : null;
        }
        catch (Exception)
        {
            return null; // no bridge in that Mastercam, or it went away
        }
    }

    public static void Activate(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            var hwnd = p.MainWindowHandle;
            if (hwnd == IntPtr.Zero) return;
            if (IsIconic(hwnd)) ShowWindow(hwnd, 9);
            SetForegroundWindow(hwnd);
        }
        catch (Exception)
        {
            // ended
        }
    }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
}
