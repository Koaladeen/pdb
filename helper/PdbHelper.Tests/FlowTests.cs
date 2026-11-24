using System.Net.Http.Json;
using System.Text.Json;
using PdbHelper;

namespace PdbHelper.Tests;

/// <summary>Whole flow against a running dev server (http://127.0.0.1:8000) filled with the sample
/// data of server/scripts/demo_sample.py (users anna/tom without password, machines DMU50 and
/// Haas_VF2). Admin password: PDB_FLOW_ADMIN_PASSWORD (default "devadmin"). Fails when no server
/// answers; set PDB_FLOW_TEST=0 to skip. How to start it: server/README.md, "Dev server".</summary>
public class FlowTests
{
    const string Server = "http://127.0.0.1:8000";
    static string AdminPassword => Environment.GetEnvironmentVariable("PDB_FLOW_ADMIN_PASSWORD") ?? "devadmin";

    /// <summary>Stands in for Mastercam with the bridge: one open file, one machine definition.</summary>
    sealed class FakeMastercam : IMastercam
    {
        public volatile bool Open = true;
        public string? Current;
        public string Machine = "";
        public List<string> Opened { get; } = new();
        void IMastercam.Open(string file) { Opened.Add(file); Current = file; }
        public bool StartEmpty() => true;
        public bool IsOpen(string file) => Open && (Current is null || Same(Current, file));
        public string? MachineOf(string file) => Open && Current is not null && Same(Current, file) ? Machine : null;
        public string? SetMachine(string file, string mmd) { Machine = mmd; return null; }
        public string? SaveAs(string file, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            File.AppendAllText(target, "+saved"); // content changes like a real save
            Current = target;
            return null;
        }
        public List<(string Target, string Mmd)> Created { get; } = new();
        public string? CreateNew(string target, string mmd)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "new file");
            Created.Add((target, mmd));
            Current = target;
            Machine = mmd;
            return null;
        }
        static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    }

    static async Task<bool> ServerUp()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            return (await http.GetAsync($"{Server}/health")).IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    static async Task<T> Until<T>(Func<Task<T?>> probe, string what, int seconds = 20) where T : class
    {
        for (var i = 0; i < seconds * 4; i++)
        {
            if (await probe() is { } v) return v;
            await Task.Delay(250);
        }
        throw new TimeoutException($"Timed out waiting for: {what}");
    }

    static SessionManager Manager(ServerApi api, FakeMastercam mc, List<string> notes)
    {
        var config = new HelperConfig { HeartbeatSeconds = 1, CloseCheckSeconds = 1, StartupGraceSeconds = 0, CloseAfterMisses = 2 };
        return new SessionManager(config, (text, _) => { lock (notes) notes.Add(text); }, mc) { Api = api };
    }

    [Fact]
    public async Task Open_save_post_close()
    {
        if (Environment.GetEnvironmentVariable("PDB_FLOW_TEST") == "0") return;
        Assert.True(await ServerUp(), $"Demo server not reachable at {Server}. Start it (see server/README.md) or set PDB_FLOW_TEST=0.");

        // Demo users from scripts/demo_data.py
        var (adminToken, _) = await ServerApi.Login(Server, "admin", AdminPassword);
        var (annaToken, _) = await ServerApi.Login(Server, "anna", "");
        var (tomToken, _) = await ServerApi.Login(Server, "tom", "");

        using var http = new HttpClient { BaseAddress = new Uri(Server + "/") };
        http.DefaultRequestHeaders.Authorization = new("Bearer", adminToken);
        var machines = await http.GetFromJsonAsync<JsonElement>("machines");
        var dmu = machines.EnumerateArray().First(m => m.GetProperty("mcam_folder").GetString() == "DMU50").GetProperty("id").GetInt32();
        var part = await (await http.PostAsJsonAsync("articles", new { data = new { teilename = "Helper Test", zeichnungsnummer = "HT-1" } })).Content.ReadFromJsonAsync<JsonElement>();
        var opRes = await http.PostAsJsonAsync($"articles/{part.GetProperty("id").GetInt32()}/operations",
            new { machine_id = dmu, data = new { prozessbeschreibung = "1 OP" } });
        var op = await opRes.Content.ReadFromJsonAsync<JsonElement>();
        var opId = op.GetProperty("id").GetInt32();
        var paths = op.GetProperty("paths");

        var notes = new List<string>();
        var mc = new FakeMastercam();
        var anna = new ServerApi(Server, annaToken);
        using var manager = Manager(anna, mc, notes);

        // open: checked out, folder created, Mastercam asked to start (no file yet)
        var opened = await manager.Open(opId, readOnly: false);
        Assert.Equal(200, opened.Status);
        Assert.True(Directory.Exists(paths.GetProperty("folder").GetString()));

        // someone else is refused with the holder's name
        using var other = Manager(new ServerApi(Server, tomToken), new FakeMastercam(), new());
        var refused = await other.Open(opId, readOnly: false);
        Assert.Equal(409, refused.Status);
        Assert.Equal("Anna Programmer", refused.Lock!.User);

        // "save" in Mastercam -> archived and checked in as revision 1
        var mcam = paths.GetProperty("mcam").GetString()!;
        await File.WriteAllBytesAsync(mcam, [1, 2, 3]);
        await Until(async () => (await anna.Operation(opId)).CurrentRevision == 1 ? "ok" : null, "check-in r1");
        Assert.True(File.Exists(Path.Combine(paths.GetProperty("folder").GetString()!, "Revisionen", $"{op.GetProperty("mdm_number_text").GetString()}_r0001.mcam")));

        // "post" in Mastercam -> header filled, copied to the NC folder with info file
        var posted = paths.GetProperty("nc_posted").GetString()!;
        await File.WriteAllTextAsync(posted, "0 BEGIN PGM #MDM_FN MM\r\n1 ; TEILENAME : #MDM_Partname\r\n2 END PGM #MDM_FN MM\r\n", NcHeader.FileEncoding);
        var target = paths.GetProperty("nc_export").GetString()!;
        var exported = await Until(async () => File.Exists(target) ? await File.ReadAllTextAsync(target, NcHeader.FileEncoding) : null, "NC export");
        var num = op.GetProperty("mdm_number_text").GetString();
        Assert.Equal($"0 BEGIN PGM {num} MM\r\n1 ; TEILENAME : Helper Test\r\n2 END PGM {num} MM\r\n", exported);
        Assert.Contains("Drawing No  : HT-1", await File.ReadAllTextAsync(paths.GetProperty("nc_info").GetString()!, NcHeader.FileEncoding));
        Assert.False(NcHeader.HasPlaceholders(await File.ReadAllTextAsync(posted, NcHeader.FileEncoding)));

        // Mastercam writes the operation picture -> uploaded to the server
        var png = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(Path.Combine(paths.GetProperty("folder").GetString()!, $"Pic{num}.png.cam"), png);
        await Until(async () => (await anna.Operation(opId)).PictureAt, "picture upload");
        using (var pic = await http.GetAsync($"operations/{opId}/picture"))
            Assert.Equal(png, await pic.Content.ReadAsByteArrayAsync());

        // after "post" etc. the fake has this file open (it was created after opening)
        mc.Current = mcam;

        // close in Mastercam -> lock released, other user can open
        mc.Open = false;
        await Until(() => Task.FromResult(manager.All.Count == 0 ? "closed" : null), "release after close");
        Assert.Equal(200, (await other.Open(opId, readOnly: false)).Status);
        await other.CloseAll("test end");
    }

    [Fact]
    public async Task New_operation_gets_its_file_created_on_open()
    {
        if (Environment.GetEnvironmentVariable("PDB_FLOW_TEST") == "0") return;
        Assert.True(await ServerUp(), $"Demo server not reachable at {Server}. Start it (see server/README.md) or set PDB_FLOW_TEST=0.");
        var (adminToken, _) = await ServerApi.Login(Server, "admin", AdminPassword);
        var (annaToken, _) = await ServerApi.Login(Server, "anna", "");
        using var http = new HttpClient { BaseAddress = new Uri(Server + "/") };
        http.DefaultRequestHeaders.Authorization = new("Bearer", adminToken);
        var machines = (await http.GetFromJsonAsync<JsonElement>("admin/machines")).EnumerateArray().ToList();
        var dmu = machines.First(m => m.GetProperty("mcam_folder").GetString() == "DMU50");
        var mmd = Path.Combine(Path.GetTempPath(), $"pdbtest-{Guid.NewGuid():N}.mcam-mmd");
        await File.WriteAllTextAsync(mmd, "machine");
        var dmuId = dmu.GetProperty("id").GetInt32();
        var before = dmu.GetProperty("machine_definition").GetString();
        await http.PatchAsJsonAsync($"admin/machines/{dmuId}", new { machine_definition = mmd });
        try
        {
            var part = await (await http.PostAsJsonAsync("articles", new { data = new { teilename = "Neue Datei Test" } })).Content.ReadFromJsonAsync<JsonElement>();
            var op = await (await http.PostAsJsonAsync($"articles/{part.GetProperty("id").GetInt32()}/operations", new { machine_id = dmuId })).Content.ReadFromJsonAsync<JsonElement>();
            var opId = op.GetProperty("id").GetInt32();
            var mcam = op.GetProperty("paths").GetProperty("mcam").GetString()!;
            Assert.False(File.Exists(mcam));

            var mc = new FakeMastercam();
            var anna = new ServerApi(Server, annaToken);
            using var manager = Manager(anna, mc, new());
            Assert.Equal(200, (await manager.Open(opId, readOnly: false)).Status);

            await Until(() => Task.FromResult(File.Exists(mcam) ? "created" : null), "file created");
            Assert.Equal((mcam, mmd), mc.Created.Single()); // with the machine's definition
            await Until(async () => (await anna.Operation(opId)).CurrentRevision == 1 ? "ok" : null, "checked in as version 1");
            await manager.CloseAll("test end");
        }
        finally
        {
            await http.PatchAsJsonAsync($"admin/machines/{dmuId}", new { machine_definition = before ?? "" });
            File.Delete(mmd);
        }
    }

    [Fact]
    public async Task Machine_change_open_and_closed()
    {
        if (Environment.GetEnvironmentVariable("PDB_FLOW_TEST") == "0") return;
        Assert.True(await ServerUp(), $"Demo server not reachable at {Server}. Start it (see server/README.md) or set PDB_FLOW_TEST=0.");

        var (adminToken, _) = await ServerApi.Login(Server, "admin", AdminPassword);
        var (annaToken, _) = await ServerApi.Login(Server, "anna", "");
        using var http = new HttpClient { BaseAddress = new Uri(Server + "/") };
        http.DefaultRequestHeaders.Authorization = new("Bearer", adminToken);
        var machines = (await http.GetFromJsonAsync<JsonElement>("admin/machines")).EnumerateArray().ToList();
        var dmu = machines.First(m => m.GetProperty("mcam_folder").GetString() == "DMU50");
        var haas = machines.First(m => m.GetProperty("mcam_folder").GetString() == "Haas_VF2");
        // a machine definition file for the target machine (temporary)
        var mmd = Path.Combine(Path.GetTempPath(), $"pdbtest-{Guid.NewGuid():N}.mcam-mmd");
        await File.WriteAllTextAsync(mmd, "machine");
        var haasId = haas.GetProperty("id").GetInt32();
        await http.PatchAsJsonAsync($"admin/machines/{haasId}", new { machine_definition = mmd });
        try
        {
            var part = await (await http.PostAsJsonAsync("articles", new { data = new { teilename = "Machine Change Test" } })).Content.ReadFromJsonAsync<JsonElement>();
            var partId = part.GetProperty("id").GetInt32();
            async Task<JsonElement> NewOp() => await (await http.PostAsJsonAsync($"articles/{partId}/operations", new { machine_id = dmu.GetProperty("id").GetInt32() })).Content.ReadFromJsonAsync<JsonElement>();

            // --- open in Mastercam: switch definition, save into the new folder, keep the lock
            var op = await NewOp();
            var opId = op.GetProperty("id").GetInt32();
            var oldFolder = op.GetProperty("paths").GetProperty("folder").GetString()!;
            Directory.CreateDirectory(oldFolder);
            var oldMcam = op.GetProperty("paths").GetProperty("mcam").GetString()!;
            await File.WriteAllTextAsync(oldMcam, "part");
            await File.WriteAllTextAsync(Path.Combine(oldFolder, "extra.txt"), "x");

            var mc = new FakeMastercam();
            using var manager = Manager(new ServerApi(Server, annaToken), mc, new());
            Assert.Equal(200, (await manager.Open(opId, readOnly: false)).Status);
            var changed = await manager.ChangeMachine(opId, haasId);
            Assert.Equal(200, changed.Status);

            var now = await new ServerApi(Server, adminToken).Operation(opId);
            var newMcam = now.Paths["mcam"];
            Assert.Contains("Haas_VF2", newMcam);
            Assert.True(File.Exists(newMcam));
            Assert.True(File.Exists(Path.Combine(now.Paths["folder"], "extra.txt")));
            Assert.False(Directory.Exists(oldFolder));
            Assert.Equal(mmd, mc.Machine);
            Assert.Equal(newMcam, manager.All.Single().McamPath);
            Assert.Equal(1, now.CurrentRevision); // saved file checked in
            Assert.NotNull(now.Lock); // still checked out
            await manager.CloseAll("test end");

            // --- not open anywhere: the folder is moved
            var op2 = await NewOp();
            var op2Id = op2.GetProperty("id").GetInt32();
            var folder2 = op2.GetProperty("paths").GetProperty("folder").GetString()!;
            Directory.CreateDirectory(folder2);
            await File.WriteAllTextAsync(op2.GetProperty("paths").GetProperty("mcam").GetString()!, "part2");
            using var manager2 = Manager(new ServerApi(Server, annaToken), new FakeMastercam(), new());
            Assert.Equal(200, (await manager2.ChangeMachine(op2Id, haasId)).Status);
            var now2 = await new ServerApi(Server, adminToken).Operation(op2Id);
            Assert.True(File.Exists(now2.Paths["mcam"]));
            Assert.False(Directory.Exists(folder2));
        }
        finally
        {
            await http.PatchAsJsonAsync($"admin/machines/{haasId}", new { machine_definition = "" });
            File.Delete(mmd);
        }
    }
}
