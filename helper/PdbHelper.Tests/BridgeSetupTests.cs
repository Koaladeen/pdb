using System.Text;
using PdbHelper;

namespace PdbHelper.Tests;

/// <summary>Editing Mastercam's mcamxm.config: only "16010. Systemstart", encoding and line
/// breaks kept, backup made, another add-in in Systemstart left alone. Works on copies.</summary>
public class BridgeSetupTests
{
    static string Copy(string content, bool bom = true)
    {
        var dir = Directory.CreateTempSubdirectory("pdbcfg").FullName;
        var path = Path.Combine(dir, "mcamxm.config");
        var body = Encoding.UTF8.GetBytes(content);
        File.WriteAllBytes(path, bom ? [0xEF, 0xBB, 0xBF, .. body] : body);
        return path;
    }

    const string Sample =
        "[START/ENDE]\r\n16000. Sprache? 0\r\n16010. Systemstart? Kein\r\n16011. Beendigung? Kein\r\n16016. Beim Beenden abfragen? y\r\n";
    const string Dll = @"M:\Mastercam 2025 shared\Add-Ins\PdbBridge\0.2.0\PdbBridge.dll";

    [Fact]
    public void Sets_only_the_startup_line_and_keeps_the_file_format()
    {
        var cfg = Copy(Sample);
        Assert.True(BridgeSetup.SetStartup(cfg, Dll));
        var bytes = File.ReadAllBytes(cfg);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.Equal(Sample.Replace("16010. Systemstart? Kein", $"16010. Systemstart? {Dll}"), text);
        Assert.True(File.Exists(cfg + ".vor-pdb")); // backup of the original
        Assert.False(BridgeSetup.SetStartup(cfg, Dll)); // second time: nothing to do
    }

    [Fact]
    public void Updates_an_older_bridge_but_never_replaces_another_addin()
    {
        var older = Copy(Sample.Replace("Kein", @"M:\Mastercam 2025 shared\Add-Ins\PdbBridge\0.1.0\PdbBridge.dll"));
        Assert.True(BridgeSetup.SetStartup(older, Dll));
        Assert.Contains(Dll, File.ReadAllText(older));

        var foreign = Copy(Sample.Replace("16010. Systemstart? Kein", @"16010. Systemstart? C:\chooks\Other.dll"));
        Assert.False(BridgeSetup.SetStartup(foreign, Dll));
        Assert.Contains(@"C:\chooks\Other.dll", File.ReadAllText(foreign));
    }

    [Fact]
    public void Works_on_a_copy_of_the_real_config()
    {
        var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Mastercam 2025", "Mastercam", "CONFIG", "mcamxm.config");
        if (!File.Exists(real)) return; // only on a PC with Mastercam 2025
        var original = File.ReadAllBytes(real);
        var copy = Copy("", bom: false);
        File.WriteAllBytes(copy, original);
        var result = BridgeSetup.SetStartup(copy, Dll);
        var after = File.ReadAllBytes(copy);
        Assert.Equal(original, File.ReadAllBytes(real)); // the real one is untouched
        if (result == true)
        {
            // exactly one line differs
            var a = Encoding.UTF8.GetString(original).Split('\n');
            var b = Encoding.UTF8.GetString(after).Split('\n');
            Assert.Equal(a.Length, b.Length);
            Assert.Single(a.Zip(b), p => p.First != p.Second);
        }
    }


    [Fact]
    public void Finds_installed_mastercam_versions()
    {
        var found = BridgeSetup.Detect();
        // on the test PC: 2025 (.NET Framework) and 2027 (.NET); 2020 has no bridge build
        Assert.All(found, f => Assert.True(f.Year >= 2025));
        if (Directory.Exists(@"C:\Program Files\Mastercam2027"))
            Assert.Contains(found, f => f.Year == 2027 && f.Flavor == "net10");
        if (File.Exists(@"C:\Program Files\Mastercam\Mastercam.exe"))
            Assert.Contains(found, f => f.Year == 2025 && f.Flavor == "net48");
    }
}
