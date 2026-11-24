using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using PdbHelper;

namespace PdbHelper.Tests;

/// <summary>The helper side of the Mastercam bridge, against a fake bridge (same pipe name and
/// line protocol as helper/PdbBridge, but without Mastercam).</summary>
public class BridgeTests
{
    [Fact]
    public async Task Sends_one_line_and_reads_the_answer()
    {
        var pid = Environment.ProcessId; // the fake "Mastercam" is this test process
        var received = new List<string>();
        var server = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                await using var pipe = new NamedPipeServerStream($"PdbBridge-{pid}", PipeDirection.InOut, 4);
                await pipe.WaitForConnectionAsync();
                var reader = new StreamReader(pipe, new UTF8Encoding(false));
                var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                var line = await reader.ReadLineAsync() ?? "";
                lock (received) received.Add(line);
                await writer.WriteLineAsync(line.StartsWith("OPEN") ? "OK\t" + line.Split('\t')[1] : "OK\t0.1.0\tC:\\a.mcam");
            }
        });

        Assert.Equal("OK\t0.1.0\tC:\\a.mcam", Bridge.Send(pid, "PING"));
        Assert.Equal("OK\tC:\\x y\\00091704.mcam", Bridge.Send(pid, "OPEN\tC:\\x y\\00091704.mcam"));
        await server;
        Assert.Equal(new[] { "PING", "OPEN\tC:\\x y\\00091704.mcam" }, received);
    }

    [Fact]
    public void No_bridge_means_null_quickly()
    {
        var watch = Stopwatch.StartNew();
        Assert.Null(Bridge.Send(999_999, "PING", connectTimeoutMs: 300));
        Assert.True(watch.ElapsedMilliseconds < 3000);
    }
}
