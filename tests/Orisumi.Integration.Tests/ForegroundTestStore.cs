using Orisumi.Core;
using Orisumi.Observer.Runtime;
using Orisumi.Observers.ForegroundState;

namespace Orisumi.Integration.Tests;

internal sealed class ForegroundTestStore : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("orisumi-foreground-");
    private readonly HttpClient _http;
    internal MemoryBackend Backend { get; } = new();
    internal string Path => System.IO.Path.Combine(_directory.FullName, "state");
    internal ForegroundRegistration Registration { get; }
    internal ForegroundStore Store { get; }
    internal List<string> Diagnostics { get; } = [];
    internal ForegroundTestStore(EntityId observerId)
    {
        _http = Backend.CreateClient();
        var session = new RuntimeObserverSession(_http, observerId);
        Registration = ForegroundRegistration.RegisterAsync(session, observerId).GetAwaiter().GetResult();
        Store = new(session, observerId, Registration, Diagnostics.Add);
    }
    internal IReadOnlyList<StoredVersion> Observations() => Backend.Snapshot().Where(version => version.Version.Content is Observation).ToArray();
    public void Dispose() { _http.Dispose(); Backend.Dispose(); _directory.Delete(recursive: true); }
}
