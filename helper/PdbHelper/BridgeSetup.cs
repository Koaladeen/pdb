using System.Diagnostics;
using System.Text;

namespace PdbHelper;

/// <summary>One installed Mastercam: year, folder, which bridge build it needs, user config.</summary>
public sealed record MastercamInstall(int Year, string ExeDir, string Flavor, string ConfigPath)
{
    public string Exe => Path.Combine(ExeDir, "Mastercam.exe");
}

/// <summary>Puts the PDB bridge where Mastercam finds it and makes Mastercam load it on start:
/// 1. DLL into a LOCAL folder per user: %LOCALAPPDATA%\PDB\bridge\&lt;net48|net10&gt;\&lt;version&gt;\.
///    Not on the network on purpose: .NET Framework (Mastercam 2025) refuses add-ins from network
///    paths without loadFromRemoteSources, a network hiccup at Mastercam start would lose the
///    bridge, and no PC needs write rights on M:. One folder per version, so an update never
///    touches the DLL a running Mastercam has loaded.
/// 2. "16010. Systemstart" in the user's mcamxm.config = that DLL (only while that Mastercam is
///    closed - Mastercam writes its config on exit; a backup is kept).
/// 3. Removes the first test copy from Program Files\...\chooks.</summary>
public static class BridgeSetup
{
    const string StartupKey = "16010.";

    public static string LocalDir => Path.Combine(Installer.Dir, "bridge");

    /// <summary>Installed Mastercam versions with a bridge build (2025: .NET Framework, 2026+: .NET).
    /// minYear lower (e.g. for "which Mastercam to start?") also lists older ones.</summary>
    public static List<MastercamInstall> Detect(int minYear = 2025)
    {
        var found = new List<MastercamInstall>();
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var dir in Directory.GetDirectories(pf, "Mastercam*"))
        {
            foreach (var exeDir in new[] { dir, Path.Combine(dir, "Mastercam") })
            {
                var exe = Path.Combine(exeDir, "Mastercam.exe");
                var hook = Path.Combine(exeDir, "NETHook3_0.dll");
                if (!File.Exists(exe)) continue;
                var major = FileVersionInfo.GetVersionInfo(exe).FileMajorPart;
                var year = 1998 + major; // 27 -> 2025, 29 -> 2027
                if (year < minYear) continue;
                // bridge builds exist for 2025+ only; older ones get "" (started without bridge)
                var flavor = year < 2025 || !File.Exists(hook) ? "" : ReadsAsNetCore(hook) ? "net10" : "net48";
                var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    $"My Mastercam {year}", "Mastercam", "CONFIG", "mcamxm.config");
                found.Add(new MastercamInstall(year, exeDir, flavor, config));
            }
        }
        return found.OrderByDescending(f => f.Year).ToList();
    }

    static bool ReadsAsNetCore(string dll)
    {
        var text = Encoding.ASCII.GetString(File.ReadAllBytes(dll));
        return text.Contains(".NETCoreApp,Version=");
    }

    public static bool IsRunning(MastercamInstall mi)
    {
        foreach (var p in Process.GetProcessesByName("Mastercam"))
        {
            try
            {
                if (p.MainModule?.FileName is { } f && f.StartsWith(mi.ExeDir, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (Exception)
            {
                return true; // cannot tell: be careful and treat as running
            }
            finally
            {
                p.Dispose();
            }
        }
        return false;
    }

    /// <summary>Brings the bridge for one Mastercam up to date. Returns a short status line.</summary>
    public static async Task<string> Ensure(MastercamInstall mi, Manifest manifest, string serverUrl)
    {
        if (manifest.Bridge is null || !manifest.Bridge.TryGetValue(mi.Flavor, out var release))
            return $"Mastercam {mi.Year}: keine Bridge für {mi.Flavor} auf dem Server";

        // 1. DLL into its local version folder
        var dll = Path.Combine(LocalDir, mi.Flavor, release.Version, "PdbBridge.dll");
        if (!File.Exists(dll))
        {
            var tmp = await Manifest.Download(serverUrl, release);
            Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
            File.Move(tmp, dll, overwrite: true);
            Log.Write($"Bridge {release.Version} ({mi.Flavor}) -> {dll}");
        }
        CleanOldVersions(mi.Flavor, keep: release.Version);

        // 2./3. only with this Mastercam closed
        if (IsRunning(mi)) return $"Mastercam {mi.Year}: Bridge {release.Version} bereit, Start-Einstellung beim nächsten Schließen";
        RemoveTestCopy(mi);
        var changed = SetStartup(mi.ConfigPath, dll);
        return changed switch
        {
            null => $"Mastercam {mi.Year}: Einstellungsdatei nicht gefunden ({mi.ConfigPath})",
            true => $"Mastercam {mi.Year}: Bridge {release.Version} wird beim Start geladen",
            false => $"Mastercam {mi.Year}: Bridge {release.Version} aktuell",
        };
    }

    /// <summary>Removes older bridge versions that no Mastercam has loaded any more (a loaded DLL
    /// cannot be deleted - that one simply stays until next time).</summary>
    static void CleanOldVersions(string flavor, string keep)
    {
        var root = Path.Combine(LocalDir, flavor);
        if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.GetDirectories(root))
        {
            if (Path.GetFileName(dir) == keep) continue;
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception) { /* still loaded in a running Mastercam */ }
        }
    }

    /// <summary>Sets "16010. Systemstart? &lt;dll&gt;" keeping the file's encoding and line breaks.
    /// True = changed, false = already set, null = no config file.</summary>
    public static bool? SetStartup(string configPath, string dll)
    {
        if (!File.Exists(configPath)) return null;
        var bytes = File.ReadAllBytes(configPath);
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        var lines = text.Split('\n');
        var changed = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (!line.TrimStart().StartsWith(StartupKey)) continue;
            var q = line.IndexOf('?');
            if (q < 0) continue;
            var current = line[(q + 1)..].Trim();
            if (string.Equals(current, dll, StringComparison.OrdinalIgnoreCase)) return false;
            if (!current.Contains("PdbBridge", StringComparison.OrdinalIgnoreCase) && !IsNone(current))
            {
                // someone uses Systemstart for another add-in: don't take it away
                Log.Write($"Systemstart in {configPath} is used by {current}; PDB bridge not set");
                return false;
            }
            lines[i] = line[..(q + 1)] + " " + dll + (lines[i].EndsWith('\r') ? "\r" : "");
            changed = true;
            break;
        }
        if (!changed) return false;
        var backup = configPath + ".vor-pdb";
        if (!File.Exists(backup)) File.Copy(configPath, backup);
        var outText = string.Join('\n', lines);
        var body = new UTF8Encoding(false).GetBytes(outText);
        WriteAtomic(configPath, bom ? [0xEF, 0xBB, 0xBF, .. body] : body);
        Log.Write($"Systemstart in {configPath} -> {dll}");
        return true;
    }

    static bool IsNone(string v) => v.Length == 0 || v is "NONE" or "Kein" or "Keine" or "None" or "Aucun" or "Nessuno";

    static void RemoveTestCopy(MastercamInstall mi)
    {
        foreach (var name in new[] { "PdbBridge.ft", "PdbBridge.dll" })
        {
            var f = Path.Combine(mi.ExeDir, "chooks", name);
            try { if (File.Exists(f)) { File.Delete(f); Log.Write($"Removed old test copy {f}"); } }
            catch (Exception ex) { Log.Write($"Could not remove {f}: {ex.Message}"); }
        }
    }

    static void WriteAtomic(string path, byte[] content)
    {
        var tmp = path + ".pdbtmp";
        File.WriteAllBytes(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
