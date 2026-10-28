using System.Text.Json;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observer.Runtime;

internal sealed class RuntimeHost : IAsyncDisposable
{
    private readonly PackageStore _packages;
    private readonly HttpClient _http;
    private readonly RuntimeReader _reader;
    private readonly Dictionary<string, ManagedObserver> _observers = new(StringComparer.Ordinal);

    public RuntimeHost(PackageStore packages, HttpClient http)
    {
        _packages = packages;
        _http = http;
        _reader = new(http);
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
            var observer = new ManagedObserver(_packages, registration, _http);
            _observers.Add(registration.PackageId, observer);
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
        switch (method)
        {
            case "list": return JsonLines.Json(new { observers = _observers.Values.Select(observer =>
            {
                var item = observer.Registration;
                var delivery = observer.Sdk.Status;
                return new
                {
                    item.PackageId, item.Name, item.Version, item.InstanceId, item.Installed, item.Enabled,
                    observer.State, observer.Error, observer.Diagnostic,
                    delivery = new { delivery.Pending, delivery.Drafts, delivery.Saved, delivery.Error,
                        rejection = delivery.Rejection is { } rejected ? new { rejected.Version.Rule, rejected.Version.Detail } : null },
                };
            }).ToArray() });
            case "catalog":
                var catalog = await _packages.CatalogAsync(token);
                return JsonLines.Json(new { packages = catalog.Select(package => new
                {
                    package.Id, package.Name, package.Description, package.Version, package.Target, package.Format, package.Protocol,
                    installedVersion = _observers.TryGetValue(package.Id, out var observer) && observer.Registration.Installed ? observer.Registration.Version : null,
                    compatible = package.Target == PackageStore.Target && package.Protocol == 1 && package.Format == 1,
                }) });
            case "install": case "update":
                await InstallAsync(parameters.GetProperty("packageId").GetString()!, method == "update", token);
                break;
            case "setEnabled": case "retry":
            {
                var observer = Find(parameters);
                if (!observer.Registration.Installed) throw new InvalidOperationException("Install the Observer first.");
                var enabled = method == "retry" || parameters.GetProperty("enabled").GetBoolean();
                var item = observer.Registration with { Enabled = enabled };
                _packages.Save(item);
                observer.SetRegistration(item);
                if (!enabled || method == "retry") await observer.StopAsync();
                if (enabled) observer.Start();
                break;
            }
            case "uninstall":
            {
                var observer = Find(parameters);
                var item = observer.Registration with { Enabled = false, Installed = false };
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

    private ManagedObserver Find(JsonElement parameters) => _observers.GetValueOrDefault(parameters.GetProperty("packageId").GetString()!)
        ?? throw new KeyNotFoundException("Observer is not installed.");
    private ManagedObserver FindInstance(JsonElement parameters) => _observers.Values.FirstOrDefault(observer => observer.Registration.InstanceId == parameters.GetProperty("instanceId").GetGuid())
        ?? throw new KeyNotFoundException("Unknown program instance.");

    private async Task InstallAsync(string id, bool updating, CancellationToken token)
    {
        _observers.TryGetValue(id, out var existing);
        if (!updating && existing?.Registration.Installed == true) return;
        if (updating && existing?.Registration.Installed != true) throw new InvalidOperationException("Install the Observer first.");
        var package = (await _packages.CatalogAsync(token)).FirstOrDefault(package => package.Id == id)
            ?? throw new KeyNotFoundException("This Observer is not in the official catalog.");
        if (existing?.Registration.Installed == true && Version.Parse(package.Version) <= Version.Parse(existing.Registration.Version)) return;
        var manifest = await _packages.PrepareAsync(package, token);
        if (existing is not null) await existing.StopAsync();
        var enabled = updating ? existing!.Registration.Enabled : true;
        var registration = _packages.Register(manifest, enabled);
        if (existing is null)
        {
            existing = new(_packages, registration, _http);
            _observers.Add(id, existing);
        }
        else existing.SetRegistration(registration);
        existing.Start();
    }

    public Task StopAsync() => Task.WhenAll(_observers.Values.Select(observer => observer.StopAsync()));
    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(_observers.Values.Select(observer => observer.DisposeAsync().AsTask()));
    }
}
