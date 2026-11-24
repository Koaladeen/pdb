namespace PdbHelper;

/// <summary>Connection state shown in the tray (icon dot, tooltip, menu) and given to the web
/// page via /status. Checked every few seconds by <see cref="StatusMonitor"/>.</summary>
public sealed record HelperStatus(
    bool ServerOk,
    string Server,
    string? User,
    int MastercamRunning,
    int MastercamWithBridge,
    DateTime CheckedAt)
{
    public static readonly HelperStatus Unknown = new(false, "", null, 0, 0, DateTime.MinValue);

    public enum Level { Good, Warning, Bad }

    /// <summary>Green: server reachable, logged in, and any running Mastercam has the bridge.
    /// Red: server not reachable. Orange: everything in between.</summary>
    public Level Overall =>
        !ServerOk ? Level.Bad
        : User is null || (MastercamRunning > 0 && MastercamWithBridge < MastercamRunning) ? Level.Warning
        : Level.Good;

    public string ServerText => ServerOk
        ? $"Server verbunden ({new Uri(Server).Host}){(User is null ? ", nicht angemeldet" : $", {User}")}"
        : $"Server nicht erreichbar ({(Server.Length > 0 ? new Uri(Server).Host : "?")})";

    public string MastercamText => MastercamRunning switch
    {
        0 => "Mastercam nicht gestartet",
        _ when MastercamWithBridge == MastercamRunning => MastercamRunning == 1 ? "Mastercam mit Bridge verbunden" : $"{MastercamRunning} Mastercam mit Bridge verbunden",
        _ when MastercamWithBridge == 0 => MastercamRunning == 1 ? "Mastercam ohne Bridge (öffnet neue Fenster)" : $"{MastercamRunning} Mastercam ohne Bridge",
        _ => $"{MastercamWithBridge} von {MastercamRunning} Mastercam mit Bridge",
    };
}

/// <summary>Checks server and Mastercam bridge regularly and reports changes.</summary>
public sealed class StatusMonitor : IDisposable
{
    readonly HelperConfig config;
    readonly Func<UserInfo?> user;
    readonly System.Threading.Timer timer;
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(4) };
    int running;

    public HelperStatus Current { get; private set; } = HelperStatus.Unknown;
    public event Action<HelperStatus>? Changed;

    public StatusMonitor(HelperConfig config, Func<UserInfo?> user, TimeSpan every)
    {
        this.config = config;
        this.user = user;
        timer = new System.Threading.Timer(_ => _ = Check(), null, TimeSpan.Zero, every);
    }

    public async Task<HelperStatus> Check()
    {
        if (Interlocked.Exchange(ref running, 1) == 1) return Current;
        try
        {
            var server = config.ServerUrl.TrimEnd('/');
            bool serverOk;
            try { serverOk = (await http.GetAsync(server + "/health")).IsSuccessStatusCode; }
            catch (Exception) { serverOk = false; }

            var pids = Bridge.Candidates();
            var bridged = pids.Count(pid => Bridge.Send(pid, "PING", connectTimeoutMs: 300, readTimeoutMs: 3000) is { } a && a.StartsWith("OK"));

            var next = new HelperStatus(serverOk, server, user()?.DisplayName, pids.Count, bridged, DateTime.Now);
            var changed = next with { CheckedAt = Current.CheckedAt } != Current;
            Current = next;
            if (changed) Changed?.Invoke(next);
            return next;
        }
        finally
        {
            Interlocked.Exchange(ref running, 0);
        }
    }

    public void Dispose()
    {
        timer.Dispose();
        http.Dispose();
    }
}
