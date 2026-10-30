using System.Text.Json;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observer.Runtime;

internal sealed partial class RuntimeHost : IAsyncDisposable
{
    private readonly PackageStore _packages;
    private readonly HttpClient _http;
    private readonly RuntimeReader _reader;
    private readonly Dictionary<Guid, ManagedObserver> _observers = new();

    public RuntimeHost(PackageStore packages, HttpClient http, bool service = false)
    {
        _packages = packages;
        _connection = new(packages.Root, http, service);
        _http = _connection.Client;
        _reader = new(_http, () => _connection.CanConnect);
    }

    public async Task InitializeAsync(CancellationToken token, string? runPackage = null)
    {
        foreach (var package in _packages.Bundled())
            if (!_packages.Observers.Any(observer => observer.PackageId == package.Id))
            {
                try { _packages.Register(await _packages.PrepareAsync(package, token), true); }
                catch (Exception exception) when (exception is IOException or ArgumentException)
                { await Console.Error.WriteLineAsync(package.Id + ": " + exception.Message); }
            }
        foreach (var registration in _packages.Observers)
        {
            var observer = new ManagedObserver(_packages, registration, _http, () => _connection.CanConnect && registration.Revision > 0);
            _observers.Add(registration.InstanceId, observer);
            observer.Store.CanSend = () => _connection.CanConnect && observer.Registration.Revision > 0;
            if (runPackage is null || registration.PackageId == runPackage) observer.Start();
        }
    }

    public async Task ServeAsync(TextReader input, TextWriter output, CancellationToken token)
    {
        while (!token.IsCancellationRequested && await JsonLines.ReadAsync(input, token) is { } line)
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
            await JsonLines.WriteAsync(output, JsonSerializer.Serialize(response, JsonSerializerOptions.Web), token);
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
            case "pair": return JsonLines.Json(new { hostId = await _connection.PairAsync(parameters.GetProperty("code").GetString()!, parameters.GetProperty("name").GetString()!, token) });
            case "list": return JsonLines.Json(new { host = new { hostId = _connection.Identity?.HostId, connection = _connection.State },
                observers = _observers.Values.Select(Status).ToArray() });
            case "catalog":
                var catalog = await _packages.CatalogAsync(token);
                return JsonLines.Json(new { packages = catalog.Select(package => new
                {
                    package.Id, package.Name, package.Description, package.Version, package.Target, package.Format, package.Protocol,
                    installedVersion = Active(package.Id) is { Registration.Installed: true } observer ? observer.Registration.Version : null,
                    compatible = package.Target == PackageStore.Target && package.Protocol == 1 && package.Format == 1,
                }) });
            case "install": case "update":
                if (method == "update") _ = Find(parameters);
                await InstallAsync(parameters.GetProperty("packageId").GetString()!, method == "update", parameters.TryGetProperty("newInstance", out var fresh) && fresh.GetBoolean(), token);
                break;
            case "setEnabled": case "retry": case "setSettings":
            {
                var observer = Find(parameters);
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
                break;
            }
            case "read": return await _reader.ReadAsync(FindInstance(parameters), parameters, token);
            case "versions": return await RuntimeReader.VersionsAsync(FindInstance(parameters), parameters, token);
            case "shutdown": await StopAsync(); break;
            default: throw new ArgumentException("Unknown Client method: " + method);
        }
        return JsonLines.Json(new { });
    }

    private ManagedObserver? Active(string id) => _observers.Values.LastOrDefault(observer => observer.Registration.PackageId == id && !observer.Registration.Removed)
        ?? _observers.Values.LastOrDefault(observer => observer.Registration.PackageId == id);
    private ManagedObserver Find(JsonElement parameters)
    {
        var observer = Active(parameters.GetProperty("packageId").GetString()!) ?? throw new KeyNotFoundException("Observer is not installed.");
        if (parameters.TryGetProperty("expectedInstanceId", out var expected) && expected.ValueKind != JsonValueKind.Null
            && observer.Registration.InstanceId != expected.GetGuid()) throw new InvalidOperationException("The target instance was replaced.");
        return observer;
    }
    private ManagedObserver FindInstance(JsonElement parameters) => _observers.Values.FirstOrDefault(observer => observer.Registration.InstanceId == parameters.GetProperty("instanceId").GetGuid())
        ?? throw new KeyNotFoundException("Unknown program instance.");

    private async Task InstallAsync(string id, bool updating, bool newInstance, CancellationToken token)
    {
        var existing = Active(id);
        if (existing?.Registration.Removed == true && (!newInstance || updating)) throw new InvalidOperationException("The previous instance was deleted; explicitly create a new instance.");
        if (existing?.Registration.Removed == true) existing = null;
        if (!updating && existing?.Registration.Installed == true) return;
        if (updating && existing?.Registration.Installed != true) throw new InvalidOperationException("Install the Observer first.");
        var package = (await _packages.CatalogAsync(token)).FirstOrDefault(package => package.Id == id)
            ?? throw new KeyNotFoundException("This Observer is not in the official catalog.");
        if (existing?.Registration.Installed == true && Version.Parse(package.Version) <= Version.Parse(existing.Registration.Version)) return;
        var manifest = await _packages.PrepareAsync(package, token);
        if (existing is not null) await existing.StopAsync();
        var enabled = updating ? existing!.Registration.Enabled : true;
        var registration = _packages.Register(manifest, enabled, newInstance, updating);
        if (existing is null)
        {
            existing = new(_packages, registration, _http);
            var created = existing;
            created.Store.CanSend = () => _connection.CanConnect && created.Registration.Revision > 0;
            _observers.Add(registration.InstanceId, existing);
        }
        else existing.SetRegistration(registration);
        existing.Start();
    }

    public Task StopAsync() => Task.WhenAll(_observers.Values.Select(observer => observer.StopAsync()));
    public async ValueTask DisposeAsync()
    {
        await _syncStop.CancelAsync();
        if (_sync is not null) await _sync;
        await Task.WhenAll(_observers.Values.Select(observer => observer.DisposeAsync().AsTask()));
        _syncStop.Dispose(); _gate.Dispose(); _connection.Dispose();
    }
}
