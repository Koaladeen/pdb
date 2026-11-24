using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PdbHelper;

/// <summary>Self-install into %LOCALAPPDATA%\PDB (no admin rights): wherever PdbHelper.exe is
/// started from (download folder, network), it copies itself there and runs from there, so
/// autostart, pdb:// links and updates always point to one place.</summary>
public static class Installer
{
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PDB");
    public static string ExePath => Path.Combine(Dir, "PdbHelper.exe");
    static string OldPath => Path.Combine(Dir, "PdbHelper.old.exe");

    public static Version Current => typeof(Installer).Assembly.GetName().Version ?? new Version(0, 0);

    public static bool IsInstalledCopy =>
        string.Equals(Path.GetFullPath(Environment.ProcessPath!), Path.GetFullPath(ExePath), StringComparison.OrdinalIgnoreCase);

    /// <summary>Copies this exe into the install folder if needed and starts it from there.
    /// True = the installed copy was started, this process should end.</summary>
    public static bool InstallAndRelaunch(string[] args)
    {
        if (IsInstalledCopy) return false;
        try
        {
            Directory.CreateDirectory(Dir);
            if (!File.Exists(ExePath) || VersionOf(ExePath) < Current)
                Replace(Environment.ProcessPath!);
            Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = false, Arguments = string.Join(" ", args.Select(Quote)) });
            Log.Write($"Installed {Current} to {ExePath}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Install failed, running from {Environment.ProcessPath}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Puts a new exe in place. A running exe cannot be overwritten but can be renamed,
    /// so the current one becomes PdbHelper.old.exe (removed on the next start).</summary>
    public static void Replace(string newExe)
    {
        Directory.CreateDirectory(Dir);
        if (File.Exists(ExePath))
        {
            try { File.Delete(OldPath); } catch (Exception) { /* still running old one: keep it */ }
            File.Move(ExePath, OldPath, overwrite: true);
        }
        File.Copy(newExe, ExePath, overwrite: true);
    }

    public static void CleanupOld()
    {
        try { if (File.Exists(OldPath)) File.Delete(OldPath); } catch (Exception) { /* next time */ }
    }

    public static Version VersionOf(string exe)
    {
        var v = FileVersionInfo.GetVersionInfo(exe).FileVersion;
        return Version.TryParse(v, out var parsed) ? parsed : new Version(0, 0);
    }

    static string Quote(string a) => a.Contains(' ') ? $"\"{a}\"" : a;
}

// ---------------------------------------------------------------------- update manifest

public sealed record ReleaseFile(string Version, string File, string Sha256)
{
    [JsonIgnore] public Version Parsed => System.Version.TryParse(Version, out var v) ? v : new System.Version(0, 0);
}

/// <summary>version.json on the server (written by helper/release.sh).</summary>
public sealed record Manifest(
    ReleaseFile? Helper,
    Dictionary<string, ReleaseFile>? Bridge)           // "net48" (Mastercam 2025), "net10" (2026+)
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public static async Task<Manifest?> Fetch(string serverUrl)
    {
        try
        {
            return await Http.GetFromJsonAsync<Manifest>($"{serverUrl.TrimEnd('/')}/downloads/version.json", ServerApi.Json);
        }
        catch (Exception ex)
        {
            Log.Write($"Update check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Downloads a release file and checks its SHA-256. Returns the temp path.</summary>
    public static async Task<string> Download(string serverUrl, ReleaseFile file)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"pdb-{Guid.NewGuid():N}-{file.File}");
        await using (var src = await Http.GetStreamAsync($"{serverUrl.TrimEnd('/')}/downloads/{Uri.EscapeDataString(file.File)}"))
        await using (var dst = File.Create(tmp))
            await src.CopyToAsync(dst);
        await using var read = File.OpenRead(tmp);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(read));
        if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            read.Close();
            File.Delete(tmp);
            throw new InvalidDataException($"Prüfsumme von {file.File} stimmt nicht – Download verworfen.");
        }
        return tmp;
    }
}
