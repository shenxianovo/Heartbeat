using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Heartbeat.Core;
using ObserverEntity = Heartbeat.Core.Observer;

namespace Heartbeat.Observers.ForegroundState;

internal sealed record ForegroundCapture(
    ObserverEntity Observer,
    ForegroundApplicationData Data,
    Observation Observation)
{
    internal static ObservationSchema Schema { get; } = new()
    {
        Id = new EntityId(Guid.Parse("01a114c5-6282-7385-ab65-b93517e16b0f")),
        Name = "foreground application reading",
        Schema = JsonSerializer.Deserialize<JsonElement>("""
            {
              "type": "object",
              "description": "A single foreground application reading. Observation startAt and endAt are equal to the reading time, at microsecond precision; timeZone is the local IANA time zone when known. Each reading has its own data identity. Missing application properties are null. Failure to obtain an application is a diagnostic, not a reading.",
              "properties": {
                "bundleIdentifier": {
                  "type": ["string", "null"],
                  "description": "The application's bundle identifier, or null when unavailable. Other kinds of application identifiers are not stored in this field."
                },
                "name": { "type": ["string", "null"] },
                "executablePath": { "type": ["string", "null"] }
              },
              "required": ["bundleIdentifier", "name", "executablePath"],
              "additionalProperties": false
            }
            """),
    };

    internal static ForegroundCapture Create(EntityId observerId, ForegroundApplicationReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        var data = new ForegroundApplicationData
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
            data,
            new Observation
            {
                Id = EntityId.New(),
                ObserverId = observerId,
                DataId = data.Id,
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
            new Submission(Observer.Id, "observer", "entities/observers", Body(new { Observer.Name })),
            new Submission(Schema.Id, "observation_schema", "entities/observation-schemas",
                Body(new { Schema.Name, Schema.Schema })),
            new Submission(Data.Id, "entity", "entities",
                Body(new { Data.BundleIdentifier, Data.Name, Data.ExecutablePath })),
            new Submission(Observation.Id, "observation", "entities/observations", Body(new
            {
                observerId = Observation.ObserverId.Value,
                dataId = Observation.DataId.Value,
                schemaId = Observation.SchemaId.Value,
                startAt = Observation.StartAt?.UtcDateTime,
                endAt = Observation.EndAt?.UtcDateTime,
                Observation.TimeZone,
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
                || !JsonElement.DeepEquals(actual.GetProperty("entity"), entity.Body))
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
