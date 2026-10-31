using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orisumi.Observer.Runtime;

internal sealed record HostIdentity(Guid HostId, string Credential, string Api);

// Credentials and the connection lifecycle belong to the Runtime, shared by every package.
internal sealed class RuntimeConnection : IDisposable
{
    private readonly string _path;
    private readonly HttpClient _transport;
    public HttpClient Client { get; }
    private readonly string _kind;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public HostIdentity? Identity { get; private set; }
    public string State { get; private set; } = "unpaired";
    public bool CanConnect => Identity is not null && State != "revoked";
    public RuntimeConnection(string root, HttpClient http, bool service)
    {
        _path = Path.Combine(root, "host.json"); _transport = http; _kind = service ? "server" : "device";
        if (File.Exists(_path))
        {
            Identity = JsonSerializer.Deserialize<HostIdentity>(File.ReadAllText(_path), Json)!;
            Identity = Identity with { Api = _transport.BaseAddress!.AbsoluteUri };
            State = "offline";
        }
        Client = new HttpClient(new CredentialHandler(this)) { BaseAddress = _transport.BaseAddress, Timeout = _transport.Timeout };
    }
    private sealed class CredentialHandler(RuntimeConnection connection) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (!connection.CanConnect) throw new HttpRequestException("Runtime is " + connection.State + ".");
            using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri) { Content = request.Content };
            foreach (var header in request.Headers) forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            forwarded.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.Identity!.Credential);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (!request.RequestUri!.AbsolutePath.EndsWith("/runtime/poll", StringComparison.Ordinal)) timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var response = await connection._transport.SendAsync(forwarded, timeout.Token);
            if (response.StatusCode == HttpStatusCode.Unauthorized) connection.State = "revoked";
            return response;
        }
    }
    public async Task<Guid> PairAsync(string code, string name, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "runtime/pair")
        { Content = JsonContent.Create(new { code, name, kind = _kind, hostId = Identity?.HostId }, options: Json) };
        // Pairing has no Authorization header, including when the old credential was revoked.
        using var response = await _transport.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        var identity = new HostIdentity(result.GetProperty("hostId").GetGuid(), result.GetProperty("credential").GetString()!, _transport.BaseAddress!.AbsoluteUri);
        var temporary = _path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var file = new FileStream(temporary, options)) { JsonSerializer.Serialize(file, identity, Json); file.Flush(true); }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, _path, true);
        Identity = identity; State = "offline";
        return identity.HostId;
    }
    public async Task<JsonElement> SendAsync(HttpMethod method, string path, object body, CancellationToken token)
    {
        if (!CanConnect) throw new HttpRequestException("Runtime is " + State + ".");
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body, options: Json) };
        using var response = await Client.SendAsync(request, token);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            State = "revoked";
            throw new HttpRequestException("Runtime credentials are no longer valid.", null, response.StatusCode);
        }
        // A proxy answers 5xx in its own format while the backend restarts.
        if ((int)response.StatusCode >= 500) throw new HttpRequestException($"Backend answered {(int)response.StatusCode}.", null, response.StatusCode);
        var payload = response.StatusCode == HttpStatusCode.NoContent ? default : await response.Content.ReadFromJsonAsync<JsonElement>(token);
        if (!response.IsSuccessStatusCode)
            throw new ManagementFailure(payload.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString()! : response.StatusCode == HttpStatusCode.NotFound ? "not_found" : response.StatusCode.ToString(), payload);
        return payload;
    }
    public void Dispose() => Client.Dispose();
    public void Connected() { if (CanConnect) State = "connected"; }
    public void Offline() { if (CanConnect) State = "offline"; }
}
internal sealed class ManagementFailure(string code, JsonElement payload) : Exception(code)
{
    public string Code { get; } = code;
    public JsonElement Payload { get; } = payload;
}
