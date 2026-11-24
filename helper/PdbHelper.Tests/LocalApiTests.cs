using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PdbHelper;

namespace PdbHelper.Tests;

/// <summary>The 127.0.0.1 endpoint: only the approved PDB page may open files; a new address
/// has to be approved at the PC, and its status answer reveals nothing.</summary>
public class LocalApiTests
{
    sealed class FakeHost : IHelperHost
    {
        public bool Approve;
        public HelperStatus Status { get; set; } = new(true, "http://pdb-server:8000", "Anna Muster", 1, 1, DateTime.Now);
        public List<string> Asked { get; } = new();
        public List<string> Tokens { get; } = new();
        public UserInfo? User { get; set; } = new(1, "anna", "Anna Muster", "user");
        public Task<bool> ApproveServer(string origin) { Asked.Add(origin); return Task.FromResult(Approve); }
        public Task AdoptToken(string token) { Tokens.Add(token); return Task.CompletedTask; }
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    static async Task<(HttpStatusCode, JsonElement)> Call(int port, HttpMethod method, string path, string? origin, object? body = null)
    {
        using var http = new HttpClient();
        using var req = new HttpRequestMessage(method, $"http://127.0.0.1:{port}{path}");
        if (origin is not null) req.Headers.Add("Origin", origin);
        if (body is not null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var res = await http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, text.Length > 0 ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    [Fact]
    public async Task Unknown_page_needs_approval_and_learns_nothing()
    {
        var config = new HelperConfig { ServerUrl = "http://pdb-server:8000", LocalPort = FreePort() };
        var host = new FakeHost();
        using var sessions = new SessionManager(config, (_, _) => { });
        using var api = new LocalApi(config, sessions, host);
        api.Start();

        // approved page sees the user and the connection state
        var (_, status) = await Call(config.LocalPort, HttpMethod.Get, "/status", "http://pdb-server:8000");
        Assert.Equal("Anna Muster", status.GetProperty("user").GetString());
        Assert.True(status.GetProperty("server_ok").GetBoolean());
        Assert.Equal("Mastercam mit Bridge verbunden", status.GetProperty("mastercam_text").GetString());

        // other page: running, but no user, no open files
        var (code, other) = await Call(config.LocalPort, HttpMethod.Get, "/status", "https://evil.example");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.True(other.GetProperty("needs_approval").GetBoolean());
        Assert.False(other.TryGetProperty("user", out _));

        // other page tries to open: the PC is asked, "no" -> refused, token not taken
        var (openCode, _) = await Call(config.LocalPort, HttpMethod.Post, "/open", "https://evil.example", new { operation_id = 1, token = "stolen" });
        Assert.Equal(HttpStatusCode.Forbidden, openCode);
        Assert.Equal(new[] { "https://evil.example" }, host.Asked);
        Assert.Empty(host.Tokens);
    }
}

public class StatusTests
{
    static HelperStatus S(bool server, string? user, int running, int bridged) => new(server, "http://pdb-server:8000", user, running, bridged, DateTime.Now);

    [Fact]
    public void Traffic_light()
    {
        Assert.Equal(HelperStatus.Level.Good, S(true, "Anna", 1, 1).Overall);
        Assert.Equal(HelperStatus.Level.Good, S(true, "Anna", 0, 0).Overall);     // Mastercam simply not started
        Assert.Equal(HelperStatus.Level.Warning, S(true, "Anna", 1, 0).Overall);  // Mastercam without bridge
        Assert.Equal(HelperStatus.Level.Warning, S(true, null, 1, 1).Overall);      // not logged in yet
        Assert.Equal(HelperStatus.Level.Bad, S(false, "Anna", 1, 1).Overall);     // server away
        Assert.Equal("1 von 2 Mastercam mit Bridge", S(true, "P", 2, 1).MastercamText);
    }
}
