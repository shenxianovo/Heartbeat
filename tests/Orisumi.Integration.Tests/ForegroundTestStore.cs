using System.Net;
using Orisumi.Core;
using Orisumi.Observer.Runtime.Store;
using Orisumi.Observers.ForegroundState;

namespace Orisumi.Integration.Tests;

internal sealed class ForegroundTestStore : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("orisumi-runtime-store-");
    private readonly HttpClient _http = new(new MissingBackend()) { BaseAddress = new("http://localhost/") };
    internal string Path => System.IO.Path.Combine(_directory.FullName, "state.sqlite");
    internal ObserverStore RuntimeStore { get; }
    internal ForegroundRegistration Registration { get; }
    internal ForegroundStore Store { get; }
    internal ForegroundTestStore(EntityId observerId)
    {
        RuntimeStore = new(Path, observerId, _http);
        Registration = ForegroundRegistration.RegisterAsync(new LocalObserverSession(RuntimeStore), observerId).GetAwaiter().GetResult();
        Store = new(new LocalObserverSession(RuntimeStore), observerId, Registration);
    }
    internal IReadOnlyList<LocalVersion> Observations() => RuntimeStore.Execute(local => local.Versions().Where(version => version.Version.Content is Observation).ToArray());
    internal IReadOnlyList<LocalVersion> Pending() => Observations().Where(version => version.State == SubmissionState.Frozen).ToArray();
    public void Dispose() { RuntimeStore.Dispose(); _http.Dispose(); _directory.Delete(recursive: true); }
    private sealed class MissingBackend : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
