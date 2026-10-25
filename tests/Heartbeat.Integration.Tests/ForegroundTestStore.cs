using System.Net;
using Heartbeat.Core;
using Heartbeat.Observer.Sdk;
using Heartbeat.Observers.ForegroundState;

namespace Heartbeat.Integration.Tests;

internal sealed class ForegroundTestStore : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("heartbeat-sdk-");
    private readonly HttpClient _http = new(new MissingBackend()) { BaseAddress = new("http://localhost/") };
    internal string Path => System.IO.Path.Combine(_directory.FullName, "state.sqlite");
    internal ObserverClient Sdk { get; }
    internal ForegroundRegistration Registration { get; }
    internal ForegroundStore Store { get; }
    internal ForegroundTestStore(EntityId observerId)
    {
        Sdk = new(Path, observerId, _http);
        Registration = ForegroundRegistration.RegisterAsync(Sdk, observerId).GetAwaiter().GetResult();
        Store = new(Sdk, observerId, Registration);
    }
    internal IReadOnlyList<LocalVersion> Observations() => Sdk.Execute(local => local.Versions().Where(version => version.Version.Content is Observation).ToArray());
    internal IReadOnlyList<LocalVersion> Pending() => Observations().Where(version => version.State == SubmissionState.Frozen).ToArray();
    public void Dispose() { Sdk.Dispose(); _http.Dispose(); _directory.Delete(recursive: true); }
    private sealed class MissingBackend : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
