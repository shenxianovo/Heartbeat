using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Orisumi.Core;

namespace Orisumi.Integration.Tests;

public sealed class ObservationPreviewApiTests
{
    [Test]
    [Property("caseId", "api-observation-previews")]
    public async Task PreviewUpdatesStayTransientAndSavedVersionsRejectLateWrites()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        var clock = new Clock();
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(clock)));
        using var http = configured.CreateClient();
        var schema = EntityApiTests.Schema();
        var content = EntityApiTests.Business(schema);
        using var setup = await EntityApiTests.Submit(http, schema, content);
        setup.EnsureSuccessStatusCode();
        var id = EntityId.New();
        var observation = new Observation(content.VersionId, content.VersionId, null, clock.Now, clock.Now, null);
        using var first = await Put(http, id, null, 1, observation);
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var original = await EntityApiTests.Json(first);
        await Assert.That(original.TryGetProperty("versionId", out _)).IsFalse();
        using var absent = await http.GetAsync($"/entities/{id.Value}");
        await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        clock.Now += TimeSpan.FromSeconds(10);
        observation = observation with { EndAt = clock.Now };
        using var updated = await Put(http, id, null, 3, observation);
        updated.EnsureSuccessStatusCode();
        using var older = await Put(http, id, null, 2, observation with { EndAt = observation.StartAt });
        var retained = await EntityApiTests.Json(older);
        await Assert.That(retained.GetProperty("sequence").GetInt64()).IsEqualTo(3);
        await Assert.That(retained.GetProperty("content").GetProperty("endAt").GetDateTimeOffset()).IsEqualTo(clock.Now);
        var final = new VersionSubmission(id, VersionId.New(), null, null, observation);
        using var saved = await EntityApiTests.Submit(http, final);
        saved.EnsureSuccessStatusCode();
        using var removed = await http.GetAsync($"/observation-previews/{id.Value}");
        await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var late = await Put(http, id, null, 4, observation);
        await Rule(late, HttpStatusCode.Conflict, "VERSION-6");
        using var next = await Put(http, id, final.VersionId, 5, observation);
        next.EnsureSuccessStatusCode();
        using var retry = await EntityApiTests.Submit(http, final);
        await Assert.That((await EntityApiTests.Json(retry)).GetProperty("versions")[0].GetProperty("recordedAt").GetString())
            .IsEqualTo((await EntityApiTests.Json(saved)).GetProperty("versions")[0].GetProperty("recordedAt").GetString());
        using var stillPresent = await http.GetAsync($"/observation-previews/{id.Value}");
        stillPresent.EnsureSuccessStatusCode();
        clock.Now += TimeSpan.FromMinutes(2) + TimeSpan.FromTicks(1);
        using var expired = await http.GetAsync($"/observation-previews/{id.Value}");
        await Assert.That(expired.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var restored = await Put(http, id, final.VersionId, 6, observation);
        restored.EnsureSuccessStatusCode();
        using var deleted = await http.DeleteAsync($"/entities/{id.Value}");
        deleted.EnsureSuccessStatusCode();
        using var deletedPreview = await http.GetAsync($"/observation-previews/{id.Value}");
        await Assert.That(deletedPreview.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var resurrect = await Put(http, id, final.VersionId, 7, observation);
        await Rule(resurrect, HttpStatusCode.Conflict, "HISTORY-7");
    }

    [Test]
    [Property("caseId", "api-observation-previews")]
    public async Task PreviewsValidateReferencesAndFilterByContentType()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        var a = EntityApiTests.Schema("a");
        var b = EntityApiTests.Schema("b");
        var one = EntityApiTests.Business(a);
        var two = EntityApiTests.Business(b);
        using var setup = await EntityApiTests.Submit(http, a, b, one, two);
        setup.EnsureSuccessStatusCode();
        var id = EntityId.New();
        var observation = new Observation(one.VersionId, one.VersionId, null, null, null, null);
        using var invalid = await Put(http, id, null, 1, observation with { ContentVersionId = VersionId.New() });
        await Rule(invalid, HttpStatusCode.BadRequest, "ENTITY-6");
        using var first = await Put(http, id, null, 1, observation);
        first.EnsureSuccessStatusCode();
        using var second = await Put(http, EntityId.New(), null, 1, observation with { ContentVersionId = two.VersionId });
        second.EnsureSuccessStatusCode();
        using var filtered = await http.GetAsync("/observation-previews?resourceName=a");
        var previews = (await EntityApiTests.Json(filtered)).GetProperty("previews");
        await Assert.That(previews.GetArrayLength()).IsEqualTo(1);
        await Assert.That(previews[0].GetProperty("entityId").GetGuid()).IsEqualTo(id.Value);
        using var wrongType = await Put(http, one.EntityId, one.VersionId, 1, observation);
        await Rule(wrongType, HttpStatusCode.BadRequest, "ENTITY-3");
        var saved = new VersionSubmission(id, VersionId.New(), null, null, observation);
        using var final = await EntityApiTests.Submit(http, saved);
        final.EnsureSuccessStatusCode();
        using var wrongObserver = await Put(http, id, saved.VersionId, 2, observation with { ObserverVersionId = two.VersionId });
        await Rule(wrongObserver, HttpStatusCode.BadRequest, "OBSERVATION-10");
    }

    internal static Task<HttpResponseMessage> Put(HttpClient http, EntityId id, VersionId? previous, long sequence, Observation content) =>
        http.PutAsJsonAsync($"/observation-previews/{id.Value}", new { previousVersionId = previous?.Value, sequence, content = VersionJson.Content(content) });

    private static async Task Rule(HttpResponseMessage response, HttpStatusCode status, string rule)
    {
        await Assert.That(response.StatusCode).IsEqualTo(status);
        var body = await EntityApiTests.Json(response);
        await Assert.That(body.GetProperty("rule").GetString()).IsEqualTo(rule);
        await Assert.That(body.TryGetProperty("index", out _)).IsFalse();
        await Assert.That(body.TryGetProperty("versionId", out _)).IsFalse();
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
