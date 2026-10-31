using System.Text.Json;
using Orisumi.Observer.Runtime.Forwarding;

namespace Orisumi.Observer.Runtime;

internal sealed partial class RuntimeHost : IAsyncDisposable
{
    private readonly PackageStore _packages;
    private readonly HttpClient _http;
    private readonly ForwardingServer _forwarding = new();
    private readonly string? _device;
    private readonly Dictionary<Guid, ManagedObserver> _observers = new();

    public RuntimeHost(PackageStore packages, HttpClient http, bool service = false, string? device = null)
    {
        _packages = packages;
        _connection = new(packages.Root, http, service);
        _http = _connection.Client;
        _device = device;
    }

    public async Task InitializeAsync(CancellationToken token, string? runPackage = null)
    {
        await _forwarding.StartAsync(token);
        foreach (var package in _packages.Bundled())
            if (!_packages.Observers.Any(observer => observer.PackageId == package.Id))
            {
                try { _packages.Register(await _packages.PrepareAsync(package, token), true); }
                catch (Exception exception) when (exception is IOException or ArgumentException)
                { await Console.Error.WriteLineAsync(package.Id + ": " + exception.Message); }
            }
        foreach (var registration in _packages.Observers)
        {
            var observer = new ManagedObserver(_packages, registration, _connection, _forwarding, _device);
            _observers.Add(registration.InstanceId, observer);
            if (registration.Removed) { _forwarding.Remove(registration.InstanceId); await observer.Writes.DeleteAsync(); }
            if (runPackage is null || registration.PackageId == runPackage) observer.Start();
        }
    }

    public async Task ServeAsync(TextReader input, TextWriter output, CancellationToken token)
    {
        while (!token.IsCancellationRequested && await ClientProtocol.ReadAsync(input, token) is { } line)
        {
            string? id = null;
            object response;
            var shutdown = false;
            try
            {
                using var json = JsonDocument.Parse(line);
                var request = json.RootElement;
                id = request.GetProperty("id").GetString();
                if (string.IsNullOrWhiteSpace(id) || id.Length > 128) throw new ArgumentException("Invalid request id.");
                var method = request.GetProperty("method").GetString()!;
                var result = await RequestAsync(method, request.GetProperty("params"), token);
                shutdown = method == "shutdown";
                response = new { id, result };
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
            {
                var code = exception switch
                {
                    KeyNotFoundException => "not_found",
                    InvalidOperationException => "conflict",
                    HttpRequestException or IOException or OperationCanceledException => "unavailable",
                    JsonException or ArgumentException or FormatException => "invalid_request",
                    _ => "internal",
                };
                response = new { id, error = new { code, message = exception.Message } };
            }
            await ClientProtocol.WriteAsync(output, JsonSerializer.Serialize(response, JsonSerializerOptions.Web), token);
            if (shutdown) return;
        }
    }

    internal async Task<JsonElement> RequestAsync(string method, JsonElement parameters, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { return await HandleAsync(method, parameters, token); }
        finally { _gate.Release(); }
    }
    private async Task<JsonElement> HandleAsync(string method, JsonElement parameters, CancellationToken token)
    {
        switch (method)
        {
            case "pair": return ClientProtocol.Json(new { hostId = await PairAndRestartAsync(parameters.GetProperty("code").GetString()!, parameters.GetProperty("name").GetString()!, token) });
            case "list": return ClientProtocol.Json(new { host = new { hostId = _connection.Identity?.HostId, connection = _connection.State },
                observers = _observers.Values.Select(Status).ToArray() });
            case "catalog":
                var catalog = await _packages.CatalogAsync(token);
                return ClientProtocol.Json(new { packages = catalog.Select(package => new
                {
                    package.Id, package.Name, package.Description, package.Version, package.Target, package.Format, package.Protocol,
                    installedVersion = Active(package.Id) is { Registration.Installed: true } observer ? observer.Registration.Version : null,
                    compatible = package.Target == PackageStore.Target && package.Protocol == 1 && package.Format == 1,
                }) });
            case "install": case "update":
                return await InstallAsync(parameters, method == "update", token);
            case "setEnabled": case "retry": case "setSettings":
            {
                var observer = FindInstance(parameters);
                if (!observer.Registration.Installed) throw new InvalidOperationException("Install the Observer first.");
                if (observer.Registration.Removed) throw new InvalidOperationException("This instance was deleted.");
                if (method == "setSettings" && parameters.GetProperty("settings").ValueKind != JsonValueKind.Object) throw new ArgumentException("settings must be an object.");
                var item = method == "setSettings" ? observer.Registration.Change(settings: parameters.GetProperty("settings").Clone())
                    : observer.Registration.Change(enabled: method == "retry" || parameters.GetProperty("enabled").GetBoolean());
                var enabled = item.Enabled;
                _packages.Save(item);
                observer.SetRegistration(item);
                if (!enabled || method is "retry" or "setSettings") await observer.StopAsync();
                if (enabled) observer.Start();
                break;
            }
            case "uninstall":
            {
                var observer = Find(parameters);
                var item = observer.Registration with { Installed = false };
                _packages.Save(item);
                observer.SetRegistration(item);
                await observer.StopAsync();
                var directory = Path.GetDirectoryName(_packages.PackageDirectory(item.PackageId, item.Version))!;
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
                return ClientProtocol.Json(new { packageId = item.PackageId, instanceId = item.InstanceId });
            }
            case "connection": return ClientProtocol.Json(new { hostId = _connection.Identity?.HostId,
                api = new { url = _http.BaseAddress!.AbsoluteUri, credential = _connection.Identity?.Credential }, connection = _connection.State });
            case "resendRejected": return ClientProtocol.Json(new { count = await FindInstance(parameters).Writes.ResendRejectedAsync(token) });
            case "shutdown": await StopAsync(); break;
            default: throw new ArgumentException("Unknown Client method: " + method);
        }
        return ClientProtocol.Json(new { });
    }

    private ManagedObserver? Active(string id) => _observers.Values.LastOrDefault(observer => observer.Registration.PackageId == id && !observer.Registration.Removed)
        ?? _observers.Values.LastOrDefault(observer => observer.Registration.PackageId == id);
    private ManagedObserver Find(JsonElement parameters)
    {
        var observer = Active(parameters.GetProperty("packageId").GetString()!) ?? throw new KeyNotFoundException("Observer is not installed.");
        return observer;
    }
    private ManagedObserver FindInstance(JsonElement parameters) => _observers.Values.FirstOrDefault(observer => observer.Registration.InstanceId == parameters.GetProperty("instanceId").GetGuid())
        ?? throw new KeyNotFoundException("Unknown program instance.");

    private async Task<JsonElement> InstallAsync(JsonElement parameters, bool updating, CancellationToken token)
    {
        var id = parameters.TryGetProperty("packageId", out var packageId) && packageId.ValueKind == JsonValueKind.String ? packageId.GetString() : null;
        PackageManifest manifest;
        if (parameters.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
            manifest = await _packages.PrepareUrlAsync(url.GetString()!, parameters.TryGetProperty("sha256", out var hash) ? hash.GetString() : null, token);
        else
        {
            var package = (await _packages.CatalogAsync(token)).FirstOrDefault(package => package.Id == id)
                ?? throw new KeyNotFoundException("This Observer is not in the official catalog.");
            manifest = await _packages.PrepareAsync(package, token);
        }
        var existing = Active(manifest.Id);
        if (existing?.Registration.Removed == true) existing = null;
        if (updating && existing?.Registration.Installed != true) throw new InvalidOperationException("Install the Observer first.");
        if (existing?.Registration.Installed == true && Version.Parse(manifest.Version) <= Version.Parse(existing.Registration.Version))
            return ClientProtocol.Json(new { packageId = manifest.Id, instanceId = existing.Registration.InstanceId });
        if (existing is not null) await existing.StopAsync();
        var registration = _packages.Register(manifest, updating ? existing!.Registration.Enabled : true, updating);
        if (existing is null)
        {
            existing = new(_packages, registration, _connection, _forwarding, _device);
            _observers.Add(registration.InstanceId, existing);
        }
        else existing.SetRegistration(registration);
        existing.Start();
        return ClientProtocol.Json(new { packageId = registration.PackageId, instanceId = registration.InstanceId });
    }

    public Task StopAsync() => Task.WhenAll(_observers.Values.Select(observer => observer.StopAsync()));
    public async ValueTask DisposeAsync()
    {
        await _syncStop.CancelAsync();
        if (_sync is not null) await _sync;
        await Task.WhenAll(_observers.Values.Select(observer => observer.DisposeAsync().AsTask()));
        await _forwarding.DisposeAsync();
        _syncStop.Dispose(); _gate.Dispose(); _connection.Dispose();
    }
}
