using System.Net;
using System.Net.Http.Json;
using Heartbeat.Core;
using Heartbeat.Observers.ForegroundState;

namespace Heartbeat.Integration.Tests;

public sealed class MacObserverReadBackTests
{
    [Test]
    [Arguments("observer")]
    [Arguments("entity")]
    public async Task WrongReadBackContentOrCategoryCannotReportSuccess(string category)
    {
        using var handler = new WrongReadBackHandler(category);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/api/") };
        var capture = ForegroundCapture.Create(EntityId.New(),
            new ForegroundApplicationReading(null, "Application", null, DateTimeOffset.UtcNow, null));
        await Assert.That(() => capture.SubmitAndVerifyAsync(client, TextWriter.Null, CancellationToken.None))
            .Throws<InvalidDataException>();
    }

    private sealed class WrongReadBackHandler(string category) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(
                request.Method == HttpMethod.Put
                    ? new HttpResponseMessage(HttpStatusCode.Created)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new { category, entity = new { name = "Wrong content" } }),
                    });
    }
}
