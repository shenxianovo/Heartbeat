using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Heartbeat.Core;
using ObserverEntity = Heartbeat.Core.Observer;

namespace Heartbeat.Observers.ForegroundState;

internal sealed record ForegroundCapture(
    ObserverEntity Observer,
    ForegroundApplicationContent Content,
    Observation Observation)
{
    internal static EntitySchema Schema { get; } = new()
    {
        Id = new EntityId(Guid.Parse("01a114c5-6282-7385-ab65-b93517e16b0f")),
        Name = "foreground application reading",
        ResourceName = "foreground-application-readings",
        Fields = JsonSerializer.Deserialize<JsonElement>("""
            {
              "bundleIdentifier": { "type": "string", "required": true, "nullable": true },
              "name": { "type": "string", "required": true, "nullable": true },
              "executablePath": { "type": "string", "required": true, "nullable": true }
            }
            """),
    };

    internal static ForegroundCapture Create(EntityId observerId, ForegroundApplicationReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        var content = new ForegroundApplicationContent
        {
            Id = EntityId.New(),
            BundleIdentifier = reading.BundleIdentifier,
            Name = reading.Name,
            ExecutablePath = reading.ExecutablePath,
        };
        var utc = reading.ObservedAt.UtcDateTime;
        var time = new DateTimeOffset(utc.AddTicks(-(utc.Ticks % 10)));
        return new ForegroundCapture(
            new ObserverEntity { Id = observerId, Name = "macOS foreground state observer" },
            content,
            new Observation
            {
                Id = EntityId.New(),
                ObserverId = observerId,
                ContentId = content.Id,
                SchemaId = Schema.Id,
                StartAt = time,
                EndAt = time,
                TimeZone = reading.TimeZone,
            });
    }

    internal async Task SubmitAndVerifyAsync(
        HttpClient client, TextWriter progress, CancellationToken cancellationToken)
    {
        var entities = new[]
        {
            new Submission(Observer.Id, "observer", "entities/observers", Body(new { references = new { }, properties = new { Observer.Name } })),
            new Submission(Schema.Id, "entity_schema", "entities/schemas",
                Body(new { references = new { }, properties = new { Schema.Name, Schema.ResourceName, Schema.Fields } })),
            new Submission(Content.Id, "entity", $"entities/{Schema.ResourceName}",
                Body(new { references = new { }, properties = new { Content.BundleIdentifier, Content.Name, Content.ExecutablePath } })),
            new Submission(Observation.Id, "observation", "entities/observations", Body(new
            {
                references = new
                {
                    observerId = Observation.ObserverId.Value,
                    contentId = Observation.ContentId.Value,
                    schemaId = Observation.SchemaId.Value,
                },
                properties = new
                {
                    startAt = Observation.StartAt?.UtcDateTime,
                    endAt = Observation.EndAt?.UtcDateTime,
                    Observation.TimeZone,
                },
            })),
        };
        foreach (var entity in entities)
        {
            var path = $"{entity.SavePath}/{entity.Id.Value}";
            await progress.WriteLineAsync($"PUT {path}");
            using var response = await client.PutAsJsonAsync(path, entity.Body, cancellationToken);
            if (response.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.NoContent))
                throw await RequestFailureAsync(response, path, cancellationToken);
        }
        foreach (var entity in entities)
        {
            var path = $"entities/{entity.Id.Value}";
            await progress.WriteLineAsync($"GET {path}");
            using var response = await client.GetAsync(path, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
                throw await RequestFailureAsync(response, path, cancellationToken);
            var actual = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            if (actual.GetProperty("category").GetString() != entity.Category
                || (!JsonElement.DeepEquals(actual.GetProperty("references"), entity.Body.GetProperty("references"))
                    || !JsonElement.DeepEquals(actual.GetProperty("properties"), entity.Body.GetProperty("properties"))))
                throw new InvalidDataException($"Read-back verification failed for {path}.");
        }
    }

    private static JsonElement Body<T>(T value) =>
        JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);

    private static async Task<HttpRequestException> RequestFailureAsync(
        HttpResponseMessage response, string path, CancellationToken cancellationToken)
    {
        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        return new HttpRequestException($"{path}: HTTP {(int)response.StatusCode}: {detail}",
            null, response.StatusCode);
    }

    private sealed record Submission(EntityId Id, string Category, string SavePath, JsonElement Body);
}
