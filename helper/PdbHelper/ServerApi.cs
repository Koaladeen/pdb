using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PdbHelper;

// Shapes of the server responses (snake_case JSON). Only the fields the helper needs.
public sealed record LockInfo(int UserId, string User, string PcName, string LockedAt, bool Stale);
public sealed record ArticleInfo(int Id, Dictionary<string, JsonElement> Data);
public sealed record OperationInfo(
    int Id,
    int MdmNumber,
    string MdmNumberText,
    int? NcNumber,
    string Machine,
    int CurrentRevision,
    Dictionary<string, JsonElement> Data,
    ArticleInfo Article,
    Dictionary<string, string> Paths,
    LockInfo? Lock,
    string? PictureAt = null,
    string? MachineDefinition = null,
    string? ImportSource = null,
    string? PlaceholderPrefix = null)
{
    public string PartName => Article.Data.TryGetValue("teilename", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
}
public sealed record MachineChangeResult(
    int Id,
    string Machine,
    int? NcNumber,
    int CurrentRevision,
    Dictionary<string, string> Paths,
    Dictionary<string, string> OldPaths,
    string? MachineDefinition);
public sealed record CheckoutResult(bool Locked, LockInfo Lock, Dictionary<string, string> Paths, int CurrentRevision);
public sealed record CheckinResult(int Revision, string ArchivePath, bool Locked, Dictionary<string, string> Paths);
public sealed record NcExportResult(Dictionary<string, string> Paths, string ProgramName, Dictionary<string, string> HeaderValues, string InfoFileText,
    string? PlaceholderPrefix = null);
public sealed record UserInfo(int Id, string Username, string DisplayName, string Role);
public sealed record LoginUser(int Id, string Username, string DisplayName, bool HasPassword)
{
    public override string ToString() => DisplayName; // shown in the login dropdown
}
sealed record LoginResult(string Token, UserInfo User);

public sealed class ApiException(HttpStatusCode status, string message, JsonElement? detail) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
    public JsonElement? Detail { get; } = detail;

    /// <summary>Lock holder from a 409 answer, if the server sent one.</summary>
    public LockInfo? Lock =>
        Detail is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("lock", out var l) && l.ValueKind == JsonValueKind.Object
            ? l.Deserialize<LockInfo>(ServerApi.Json)
            : null;
}

/// <summary>Calls to the PDB server. One instance per login.</summary>
public sealed class ServerApi
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    readonly HttpClient http;
    public string PcName { get; } = Environment.MachineName;

    public ServerApi(string baseUrl, string? token)
    {
        http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(15) };
        if (token is not null) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public static async Task<(string Token, UserInfo User)> Login(string baseUrl, string username, string password)
    {
        var api = new ServerApi(baseUrl, null);
        var res = await api.Send<LoginResult>(HttpMethod.Post, "auth/login", new { username, password, pc_name = Environment.MachineName });
        return (res.Token, res.User);
    }

    /// <summary>Names for the login dropdown (no login needed).</summary>
    public static Task<List<LoginUser>> LoginUsers(string baseUrl) =>
        new ServerApi(baseUrl, null).Send<List<LoginUser>>(HttpMethod.Get, "auth/users");

    public Task<UserInfo> Me() => Send<UserInfo>(HttpMethod.Get, "auth/me");
    public Task Logout() => Send<JsonElement>(HttpMethod.Post, "auth/logout", new { });
    public Task<OperationInfo> Operation(int id) => Send<OperationInfo>(HttpMethod.Get, $"operations/{id}");
    public Task<CheckoutResult> Checkout(int id) => Send<CheckoutResult>(HttpMethod.Post, $"operations/{id}/checkout", new { pc_name = PcName });
    public Task Heartbeat(int id) => Send<JsonElement>(HttpMethod.Post, $"operations/{id}/heartbeat", new { pc_name = PcName });
    public Task Release(int id) => Send<JsonElement>(HttpMethod.Post, $"operations/{id}/release", new { pc_name = PcName });
    public Task ImportCopied(int id) => Send<JsonElement>(HttpMethod.Post, $"operations/{id}/import-copied", new { pc_name = PcName });
    public Task<MachineChangeResult> ChangeMachine(int id, int machineId) =>
        Send<MachineChangeResult>(HttpMethod.Post, $"operations/{id}/machine", new { machine_id = machineId, pc_name = PcName });

    public Task<NcExportResult> NcExport(int id) => Send<NcExportResult>(HttpMethod.Post, $"operations/{id}/nc-export", new { });

    /// <summary>Uploads the operation picture (PNG/JPEG bytes). Returns the server's new timestamp.</summary>
    public async Task<string?> UploadPicture(int id, byte[] image)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, $"operations/{id}/picture") { Content = new ByteArrayContent(image) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        HttpResponseMessage res;
        try { res = await http.SendAsync(req); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ApiException(0, $"Server nicht erreichbar: {ex.Message}", null);
        }
        using (res)
        {
            var text = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) throw new ApiException(res.StatusCode, $"Bild-Upload: {(int)res.StatusCode} {text}", null);
            return JsonDocument.Parse(text).RootElement.GetProperty("picture_at").GetString();
        }
    }

    public Task<CheckinResult> Checkin(int id, int revision, string comment, bool keepLock) =>
        Send<CheckinResult>(HttpMethod.Post, $"operations/{id}/checkin", new { pc_name = PcName, revision, comment, keep_lock = keepLock });

    async Task<T> Send<T>(HttpMethod method, string path, object? body = null)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null) req.Content = JsonContent.Create(body, options: Json);
        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(req);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ApiException(0, $"Server nicht erreichbar ({http.BaseAddress}): {ex.Message}", null);
        }
        using (res)
        {
            var text = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                JsonElement? detail = null;
                var message = $"{(int)res.StatusCode} {res.ReasonPhrase}";
                try
                {
                    var doc = JsonDocument.Parse(text).RootElement;
                    if (doc.TryGetProperty("detail", out var d))
                    {
                        detail = d.Clone();
                        message = d.ValueKind switch
                        {
                            JsonValueKind.String => d.GetString()!,
                            JsonValueKind.Object when d.TryGetProperty("message", out var m) => m.GetString()!,
                            _ => d.ToString(),
                        };
                    }
                }
                catch (JsonException)
                {
                    // not JSON: keep the status text
                }
                throw new ApiException(res.StatusCode, message, detail);
            }
            return JsonSerializer.Deserialize<T>(text, Json)!;
        }
    }
}
