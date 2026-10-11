using System.Net;
using System.Net.Http.Json;
using Heartbeat.Core;
using Heartbeat.Observers.ForegroundState;

namespace Heartbeat.Integration.Tests;

public sealed class MacObserverReadBackTests
{
    [Test]
    [Property("caseId", "foreground-outbox-recovery")]
    [Arguments("observer")]
    [Arguments("entity")]
    public async Task WrongReadBackContentOrCategoryCannotReportSuccess(string category)
    {
        using var handler = new WrongReadBackHandler(category);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/api/") };
        var capture = ForegroundCapture.Create(EntityId.New(), new LocalApplication(EntityId.New(), []),
            new ForegroundApplicationReading(null, "Application", null, DateTimeOffset.UtcNow, null));
        using var local = new ForegroundTestStore(capture.Observer.Id);
        local.Store.Checkpoint(capture, null);
        local.Store.FinishActive();
        await new OutboxSender(local.Store, client, TextWriter.Null).SynchronizeAsync(CancellationToken.None);
        await Assert.That(local.Store.Pending().Single().LastError).Contains("different content");
    }

    private sealed class WrongReadBackHandler(string category) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(
                request.Method == HttpMethod.Put
                    ? new HttpResponseMessage(HttpStatusCode.Created)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new { category, references = new { }, properties = new { name = "Wrong content" } }),
                    });
    }
}
