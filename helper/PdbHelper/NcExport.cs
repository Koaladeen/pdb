using System.Text;
using System.Text.RegularExpressions;

namespace PdbHelper;

/// <summary>Fills the placeholders the post writes into the NC header (#&lt;prefix&gt;..., the prefix
/// comes from the server, e.g. "PDB_"). Pure text work, kept separate so it can be tested without
/// files or a server.</summary>
public static partial class NcHeader
{
    /// <summary>NC files are plain ASCII/ANSI; Latin-1 keeps every byte as it is.</summary>
    public static readonly Encoding FileEncoding = Encoding.Latin1;

    /// <summary>Servers before the prefix setting always used this one.</summary>
    public const string DefaultPrefix = "MDM_";

    /// <summary>Prefixes to look for in a freshly posted file - before the server is asked. The
    /// usual ones plus whatever the server reported for an opened operation (Sessions).</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> KnownPrefixes = new()
    {
        [DefaultPrefix] = true,
        ["PDB_"] = true,
    };

    public static void Remember(string? prefix)
    {
        if (!string.IsNullOrEmpty(prefix)) KnownPrefixes[prefix!] = true;
    }

    static Regex PlaceholderRegex(string prefix) => new("#" + Regex.Escape(prefix) + "[A-Za-z0-9_]+");

    public static bool HasPlaceholders(string text, string prefix) => PlaceholderRegex(prefix).IsMatch(text);

    public static bool HasPlaceholders(string text) => KnownPrefixes.Keys.Any(p => HasPlaceholders(text, p));

    /// <summary>Replaces every known placeholder. Longest names first, so "#PDB_FN" never
    /// eats the start of a longer name like "#PDB_FNX".</summary>
    public static string Fill(string text, IReadOnlyDictionary<string, string> values)
    {
        foreach (var (key, value) in values.OrderByDescending(kv => kv.Key.Length))
            text = text.Replace(key, value, StringComparison.Ordinal);
        return text;
    }

    public static IReadOnlyList<string> Unknown(string text, string prefix = DefaultPrefix) =>
        PlaceholderRegex(prefix).Matches(text).Select(m => m.Value).Distinct().ToList();

    public static string PrefixOf(NcExportResult export) => string.IsNullOrEmpty(export.PlaceholderPrefix) ? DefaultPrefix : export.PlaceholderPrefix;

    /// <summary>Values from the server plus the ones only the helper knows.</summary>
    public static Dictionary<string, string> AllValues(NcExportResult export) =>
        new(export.HeaderValues) { [$"#{PrefixOf(export)}PGM"] = export.ProgramName };
}

/// <summary>Turns a freshly posted NC file into the finished program on the machine share,
/// the usual steps of an NC export.</summary>
public sealed class NcExportService(ServerApi api)
{
    public sealed record Outcome(bool Done, string Message);

    public async Task<Outcome> Process(int operationId, string postedPath)
    {
        if (!File.Exists(postedPath)) return new(false, "");
        var text = await ReadShared(postedPath);
        if (!NcHeader.HasPlaceholders(text))
            return new(false, ""); // already processed (our own write) or a hand-made file

        NcExportResult export;
        try
        {
            export = await api.NcExport(operationId);
        }
        catch (ApiException ex)
        {
            return new(true, $"NC-Export abgelehnt: {ex.Message}");
        }

        var filled = NcHeader.Fill(text, NcHeader.AllValues(export));
        var target = export.Paths["nc_export"];
        var oldPath = export.Paths["nc_old"];
        export.Paths.TryGetValue("nc_info", out var infoPath); // optional (admin setting)

        // 1. previous program on the machine share becomes -old.nc next to the part
        if (File.Exists(target)) File.Copy(target, oldPath, overwrite: true);
        // 2. posted file gets the filled header (no placeholders left -> our watcher ignores it)
        FileOps.WriteAtomic(postedPath, NcHeader.FileEncoding.GetBytes(filled));
        // 3. program and info file to the machine share
        FileOps.WriteAtomic(target, NcHeader.FileEncoding.GetBytes(filled));
        if (!string.IsNullOrEmpty(infoPath) && export.InfoFileText.Length > 0)
            FileOps.WriteAtomic(infoPath, NcHeader.FileEncoding.GetBytes(export.InfoFileText));

        var unknown = NcHeader.Unknown(filled, NcHeader.PrefixOf(export));
        var warning = unknown.Count > 0 ? $" Unbekannte Platzhalter: {string.Join(", ", unknown)}" : "";
        Log.Write($"NC export op {operationId}: {target}{warning}");
        return new(true, $"NC-Programm exportiert: {target}{warning}");
    }

    static async Task<string> ReadShared(string path)
    {
        // Mastercam may still be writing; wait until the file can be read.
        for (var i = 0; ; i++)
        {
            try
            {
                await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs, NcHeader.FileEncoding);
                return await reader.ReadToEndAsync();
            }
            catch (IOException) when (i < 10)
            {
                await Task.Delay(500);
            }
        }
    }
}

public static class FileOps
{
    /// <summary>Write to a temp file next to the target, then rename over it, so a crash or a
    /// network hiccup never leaves a half-written file.</summary>
    public static void WriteAtomic(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".pdbtmp";
        File.WriteAllBytes(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Copy with retries; Mastercam may hold the file for a moment after saving.</summary>
    public static async Task CopyWithRetry(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        for (var i = 0; ; i++)
        {
            try
            {
                var tmp = to + ".pdbtmp";
                await using (var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                    await src.CopyToAsync(dst);
                File.Move(tmp, to, overwrite: true);
                return;
            }
            catch (IOException) when (i < 20)
            {
                await Task.Delay(500);
            }
        }
    }
}
