using System.Net;
using System.Text;
using System.Text.Json;

namespace PdbHelper;

/// <summary>What the local endpoint needs from the tray app.</summary>
public interface IHelperHost
{
    UserInfo? User { get; }

    /// <summary>Last checked connection to server and Mastercam bridge.</summary>
    HelperStatus Status { get; }

    /// <summary>A page from an address the helper does not know yet wants to use it: ask the
    /// person once. True = switch to that server.</summary>
    Task<bool> ApproveServer(string origin);

    /// <summary>Use the login of the web page (same user, no second login).</summary>
    Task AdoptToken(string token);
}

/// <summary>Small HTTP endpoint on 127.0.0.1 so the PDB web page can ask the helper to open
/// files. Only this PC can reach it, and only the approved PDB page may use it (Origin check);
/// a new address must be approved once by the person at the PC, so other websites cannot
/// open files.</summary>
public sealed class LocalApi : IDisposable
{
    readonly HttpListener listener = new();
    readonly HelperConfig config;
    readonly SessionManager sessions;
    readonly IHelperHost host;

    public LocalApi(HelperConfig config, SessionManager sessions, IHelperHost host)
    {
        this.config = config;
        this.sessions = sessions;
        this.host = host;
        listener.Prefixes.Add($"http://127.0.0.1:{config.LocalPort}/");
    }

    public void Start()
    {
        listener.Start();
        _ = Task.Run(Loop);
        Log.Write($"Local API on 127.0.0.1:{config.LocalPort}");
    }

    async Task Loop()
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch (Exception) { return; } // stopped
            _ = Task.Run(() => Handle(ctx));
        }
    }

    async Task Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            var origin = req.Headers["Origin"];
            // Browser calls carry an Origin; local programs (pdb:// forwarding) do not.
            var approved = origin is null || SameOrigin(origin, config.ServerOrigin);
            if (origin is not null)
            {
                // CORS headers also for not-yet-approved pages, so they can at least ask;
                // /open still requires approval below.
                res.Headers["Access-Control-Allow-Origin"] = origin;
                res.Headers["Vary"] = "Origin";
                res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
                res.Headers["Access-Control-Allow-Headers"] = "Content-Type";
                res.Headers["Access-Control-Allow-Private-Network"] = "true";
            }
            if (req.HttpMethod == "OPTIONS")
            {
                res.StatusCode = 204;
                res.Close();
                return;
            }

            var path = req.Url!.AbsolutePath.TrimEnd('/');
            switch (req.HttpMethod, path)
            {
                case ("GET", "/status"):
                    if (!approved)
                    {
                        // running, but nothing about user or files for an unknown page
                        await Reply(res, 200, new { ok = true, logged_in = false, needs_approval = true });
                        return;
                    }
                    var user = host.User;
                    var st = host.Status;
                    await Reply(res, 200, new
                    {
                        ok = true,
                        version = typeof(LocalApi).Assembly.GetName().Version?.ToString(3),
                        server = config.ServerUrl,
                        user = user?.DisplayName,
                        logged_in = user is not null,
                        server_ok = st.ServerOk,
                        mastercam_running = st.MastercamRunning,
                        mastercam_bridge = st.MastercamWithBridge,
                        mastercam_text = st.MastercamText,
                        open = sessions.All.Select(s => new { operation_id = s.OperationId, mdm = s.MdmText, read_only = s.ReadOnly, lock_lost = s.LockLost }),
                    });
                    return;

                case ("POST", "/machine"):
                {
                    if (await Admit(req, res, origin, approved) is not { } mbody) return;
                    var mid = mbody.GetProperty("operation_id").GetInt32();
                    var machineId = mbody.GetProperty("machine_id").GetInt32();
                    var changed = await sessions.ChangeMachine(mid, machineId);
                    await Reply(res, changed.Status, new { message = changed.Message, @lock = changed.Lock });
                    return;
                }

                case ("POST", "/open"):
                    if (await Admit(req, res, origin, approved) is not { } body) return;
                    var id = body.GetProperty("operation_id").GetInt32();
                    var readOnly = body.TryGetProperty("read_only", out var ro) && ro.GetBoolean();
                    var outcome = await sessions.Open(id, readOnly);
                    await Reply(res, outcome.Status, new { message = outcome.Message, mode = outcome.Mode, @lock = outcome.Lock });
                    return;

                default:
                    await Reply(res, 404, new { message = "Unbekannt" });
                    return;
            }
        }
        catch (ApiException ex)
        {
            await Reply(res, ex.Status == 0 ? 502 : (int)ex.Status, new { message = ex.Message });
        }
        catch (Exception ex)
        {
            Log.Write($"Local API error: {ex}");
            await Reply(res, 500, new { message = ex.Message });
        }
    }

    /// <summary>Common gate for actions: page approved (asks the PC once for a new address), login
    /// taken over from the page, user known. Returns the JSON body, or null after replying.</summary>
    async Task<JsonElement?> Admit(HttpListenerRequest req, HttpListenerResponse res, string? origin, bool approved)
    {
        if (!approved && !await host.ApproveServer(origin!))
        {
            await Reply(res, 403, new { message = "Am PC nicht erlaubt: der PDB Helper arbeitet nur mit der freigegebenen PDB-Seite." });
            return null;
        }
        using var doc = await JsonDocument.ParseAsync(req.InputStream);
        var body = doc.RootElement.Clone();
        if (body.TryGetProperty("token", out var tok) && tok.ValueKind == JsonValueKind.String && origin is not null)
            await host.AdoptToken(tok.GetString()!);
        if (host.User is null)
        {
            await Reply(res, 401, new { message = "PDB Helper ist nicht angemeldet. Bitte auf der PDB-Seite neu anmelden oder über das Tray-Symbol." });
            return null;
        }
        return body;
    }

    /// <summary>Exact match, except that localhost and 127.0.0.1 count as the same host.</summary>
    internal static bool SameOrigin(string origin, string server)
    {
        static string Norm(string o)
        {
            if (!Uri.TryCreate(o, UriKind.Absolute, out var u)) return o.ToLowerInvariant();
            var host = u.IsLoopback ? "loopback" : u.Host.ToLowerInvariant();
            return $"{u.Scheme}://{host}:{u.Port}";
        }
        return Norm(origin) == Norm(server);
    }

    static async Task Reply(HttpListenerResponse res, int status, object body)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, ServerApi.Json));
            res.StatusCode = status;
            res.ContentType = "application/json; charset=utf-8";
            res.ContentLength64 = bytes.Length;
            await res.OutputStream.WriteAsync(bytes);
            res.Close();
        }
        catch (Exception)
        {
            // browser went away
        }
    }

    public void Dispose()
    {
        try { listener.Stop(); listener.Close(); } catch (Exception) { }
    }
}
