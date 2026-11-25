using PdbHelper;

namespace PdbHelper.Tests;

public class NcHeaderTests
{
    // Header as the post writes it today (from 00091303-Autofill upon export.nc, TNC640).
    const string Posted =
        "0 BEGIN PGM #MDM_FN MM\r\n" +
        "1 ; DB-NR.        : MDM#MDM_FN\r\n" +
        "2 ; TEILENAME     : #MDM_Partname\r\n" +
        "3 ; ZCH-NR        : #MDM_DrawingNumber\r\n" +
        "4 ; OP-NAME       : #MDM_OperationTitle\r\n" +
        "5 ; PROGRAMMIERER : #MDM_Programmer\r\n" +
        "6 ; MASCHINE      : TNC640-AC\r\n" +
        "6307 END PGM #MDM_FN MM\r\n";

    static readonly Dictionary<string, string> Values = new()
    {
        ["#MDM_FN"] = "00084197",
        ["#MDM_Partname"] = "Flat Grid",
        ["#MDM_DrawingNumber"] = "NR-20431",
        ["#MDM_OperationTitle"] = "1 OP",
        ["#MDM_Programmer"] = "Anna Muster",
    };

    [Fact]
    public void Fills_every_placeholder_and_keeps_begin_and_end_in_step()
    {
        var filled = NcHeader.Fill(Posted, Values);
        Assert.False(NcHeader.HasPlaceholders(filled));
        Assert.StartsWith("0 BEGIN PGM 00084197 MM\r\n", filled);
        Assert.Contains("DB-NR.        : MDM00084197\r\n", filled);
        Assert.Contains("TEILENAME     : Flat Grid\r\n", filled);
        Assert.Contains("PROGRAMMIERER : Anna Muster\r\n", filled);
        Assert.EndsWith("END PGM 00084197 MM\r\n", filled);
        Assert.Contains("MASCHINE      : TNC640-AC", filled); // untouched
    }

    [Fact]
    public void Longer_names_win_over_shorter_prefixes()
    {
        var values = new Dictionary<string, string> { ["#MDM_FN"] = "1", ["#MDM_FNX"] = "2" };
        Assert.Equal("2 1", NcHeader.Fill("#MDM_FNX #MDM_FN", values));
    }

    [Fact]
    public void Reports_placeholders_nobody_filled()
    {
        var filled = NcHeader.Fill(Posted + "; #MDM_Customer\r\n", Values);
        Assert.Equal(new[] { "#MDM_Customer" }, NcHeader.Unknown(filled));
    }

    [Fact]
    public void Adds_program_name_for_fanuc_posts()
    {
        var export = new NcExportResult(new(), "1101", new(Values), "");
        Assert.Equal("O1101 (Flat Grid)", NcHeader.Fill("O#MDM_PGM (#MDM_Partname)", NcHeader.AllValues(export)));
    }

    [Fact]
    public void Uses_the_placeholder_prefix_of_the_server()
    {
        var values = new Dictionary<string, string> { ["#PDB_FN"] = "00001001", ["#PDB_Partname"] = "Flat Grid" };
        var export = new NcExportResult(new(), "1101", values, "", PlaceholderPrefix: "PDB_");
        const string posted = "BEGIN PGM #PDB_FN MM\r\n; #PDB_Partname #PDB_Customer O#PDB_PGM\r\n";
        Assert.True(NcHeader.HasPlaceholders(posted)); // recognised before the server is asked
        var filled = NcHeader.Fill(posted, NcHeader.AllValues(export));
        Assert.Equal("BEGIN PGM 00001001 MM\r\n; Flat Grid #PDB_Customer O1101\r\n", filled);
        Assert.Equal(new[] { "#PDB_Customer" }, NcHeader.Unknown(filled, NcHeader.PrefixOf(export)));
        Assert.False(NcHeader.HasPlaceholders("O1101 (#100=5)")); // FANUC variables are no placeholders

        NcHeader.Remember("XY_"); // custom prefix reported by the server
        Assert.True(NcHeader.HasPlaceholders("; #XY_FN"));
    }

    [Fact]
    public void Keeps_bytes_of_the_real_sample_file()
    {
        var sample = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\00091303-Autofill upon export.nc"));
        if (!File.Exists(sample)) return; // sample lives next to the plan document
        var bytes = File.ReadAllBytes(sample);
        var text = NcHeader.FileEncoding.GetString(bytes);
        Assert.True(NcHeader.HasPlaceholders(text));
        var filled = NcHeader.Fill(text, Values);
        Assert.False(NcHeader.HasPlaceholders(filled));
        // everything after the header is byte-identical
        var tail = text[text.IndexOf("; MASCHINE", StringComparison.Ordinal)..text.LastIndexOf("END PGM", StringComparison.Ordinal)];
        Assert.Contains(tail, filled);
    }
}

public class LinkTests
{
    [Theory]
    [InlineData("pdb://open/12", 12, false)]
    [InlineData("pdb://open/12/", 12, false)]
    [InlineData("PDB://open/7/readonly", 7, true)]
    public void Parses_links(string link, int id, bool readOnly)
    {
        Assert.Equal((id, readOnly), WindowsIntegration.ParseLink(link));
    }

    [Theory]
    [InlineData("pdb://delete/12")]
    [InlineData("pdb://open/abc")]
    [InlineData("http://open/12")]
    [InlineData("pdb://open/12 & calc")]
    public void Rejects_anything_else(string link)
    {
        Assert.Null(WindowsIntegration.ParseLink(link));
    }
}

public class PictureNameTests
{
    static FileSession Session() => new()
    {
        OperationId = 1, MdmText = "00084197", PartName = "", McamPath = @"C:\x\00084197.mcam",
        NcPostedPath = @"C:\x\00084197.nc", ReadOnly = false,
    };

    [Theory]
    [InlineData("Pic00084197.png.cam", true)]
    [InlineData("PIC00084197.png", true)]
    [InlineData("pic00084197.PNG.CAM", true)]
    [InlineData("Pic-00084197.png", true)]
    [InlineData("Pic00084198.png.cam", false)]
    [InlineData("Pic00084197.png.cam.tmp", false)]
    [InlineData("00084197.png", false)]
    public void Recognises_mdm_picture_names(string name, bool expected)
    {
        Assert.Equal(expected, Session().IsPicture(name));
    }
}

public class OriginTests
{
    [Theory]
    [InlineData("http://pdb-server:8000", "http://pdb-server:8000", true)]
    [InlineData("http://localhost:8000", "http://127.0.0.1:8000", true)]
    [InlineData("http://pdb-server:8001", "http://pdb-server:8000", false)]
    [InlineData("https://evil.example", "http://pdb-server:8000", false)]
    [InlineData("http://pdb-server.evil.example:8000", "http://pdb-server:8000", false)]
    public void Only_the_pdb_page_may_call_the_helper(string origin, string server, bool allowed)
    {
        Assert.Equal(allowed, LocalApi.SameOrigin(origin, server));
    }
}

public class FileOpsTests
{
    [Fact]
    public void Atomic_write_replaces_and_leaves_no_temp_file()
    {
        var dir = Directory.CreateTempSubdirectory("pdbtest").FullName;
        var file = Path.Combine(dir, "sub", "a.nc");
        FileOps.WriteAtomic(file, [1, 2, 3]);
        FileOps.WriteAtomic(file, [4]);
        Assert.Equal(new byte[] { 4 }, File.ReadAllBytes(file));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(file)!));
        Directory.Delete(dir, true);
    }
}
