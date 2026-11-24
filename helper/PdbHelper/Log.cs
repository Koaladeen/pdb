namespace PdbHelper;

/// <summary>Simple daily log in %APPDATA%\PDB\logs - the first place to look when something
/// did not happen as expected.</summary>
public static class Log
{
    static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            var dir = Path.Combine(HelperConfig.Folder, "logs");
            Directory.CreateDirectory(dir);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}";
            lock (Gate) File.AppendAllText(Path.Combine(dir, $"helper-{DateTime.Now:yyyy-MM-dd}.log"), line);
        }
        catch
        {
            // logging must never break the app
        }
    }

    public static string FolderPath => Path.Combine(HelperConfig.Folder, "logs");
}
