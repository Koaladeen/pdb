using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PdbHelper;

/// <summary>Settings in %APPDATA%\PDB\helper.json. The login token is encrypted for the
/// current Windows user (DPAPI), so copying the file to another account does not work.</summary>
public sealed class HelperConfig
{
    public string ServerUrl { get; set; } = "http://localhost:8000"; // a company sets its server via install-workstation.ps1
    public string? Username { get; set; }
    public string? ProtectedToken { get; set; }

    /// <summary>Empty = open .mcam files with the program Windows has associated.</summary>
    public string? MastercamExe { get; set; }

    public int LocalPort { get; set; } = 5017;
    public int HeartbeatSeconds { get; set; } = 60;
    public int CloseCheckSeconds { get; set; } = 5;

    /// <summary>Misses in a row before a file counts as closed in Mastercam.</summary>
    public int CloseAfterMisses { get; set; } = 3;

    /// <summary>Mastercam can take a while to start; no close detection during this time.</summary>
    public int StartupGraceSeconds { get; set; } = 90;

    /// <summary>No longer used (autostart is never switched on by itself); kept so old
    /// helper.json files still load.</summary>
    public bool AutostartInitialized { get; set; }

    /// <summary>Replace its own program file with a newer version from the server. Off by default
    /// (security software flags self-updating programs); IT can switch it on.</summary>
    public bool AutoUpdate { get; set; }

    /// <summary>Last version announced as "available", so it is announced once, not every 10 min.</summary>
    public string? AnnouncedVersion { get; set; }

    /// <summary>User agreed once to "Bridge einrichten"; from then on the helper keeps the bridge
    /// up to date by itself.</summary>
    public bool BridgeManaged { get; set; }

    [JsonIgnore]
    public string? Token
    {
        get
        {
            if (string.IsNullOrEmpty(ProtectedToken)) return null;
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(ProtectedToken), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception)
            {
                return null;
            }
        }
        set
        {
            ProtectedToken = value is null
                ? null
                : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
        }
    }

    [JsonIgnore]
    public string ServerOrigin => new Uri(ServerUrl).GetLeftPart(UriPartial.Authority);

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PDB");
    static string FilePath => Path.Combine(Folder, "helper.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static HelperConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<HelperConfig>(File.ReadAllText(FilePath), Json) ?? new HelperConfig();
        }
        catch (Exception ex)
        {
            Log.Write($"Config could not be read, using defaults: {ex.Message}");
        }
        return MachineDefaults();
    }

    // First start of a user: the server address IT wrote for this PC (install-workstation.ps1).
    static HelperConfig MachineDefaults()
    {
        var config = new HelperConfig();
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PDB", "helper-defaults.json");
            if (File.Exists(path))
            {
                var d = JsonSerializer.Deserialize<HelperConfig>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (!string.IsNullOrWhiteSpace(d?.ServerUrl)) config.ServerUrl = d.ServerUrl.TrimEnd('/');
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Machine defaults could not be read: {ex.Message}");
        }
        return config;
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }
}
