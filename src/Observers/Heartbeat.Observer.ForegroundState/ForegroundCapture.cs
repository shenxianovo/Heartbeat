using System.Text.Json;
using Heartbeat.Core;
using ObserverEntity = Heartbeat.Core.Observer;

namespace Heartbeat.Observers.ForegroundState;

internal sealed record ForegroundCapture(
    ObserverEntity Observer,
    LocalApplication Application,
    ForegroundApplicationContent Content,
    Observation Observation)
{
    internal static EntitySchema Schema { get; } = new()
    {
        Id = new EntityId(Guid.Parse("019e1bbb-ec00-7000-8000-000000000001")),
        Name = "foreground application period",
        ResourceName = "foreground-application-periods",
        Fields = JsonSerializer.Deserialize<JsonElement>("""
            {
              "localApplicationId": { "type": "reference", "required": true, "nullable": false },
              "name": { "type": "string", "required": true, "nullable": true }
            }
            """),
    };

    internal static EntitySchema ApplicationSchema { get; } = new()
    {
        Id = new EntityId(Guid.Parse("019e1bbb-ec00-7000-8000-000000000002")),
        Name = "local application",
        ResourceName = "local-applications",
        Fields = JsonSerializer.Deserialize<JsonElement>("""
            {
              "identifiers": { "type": "array", "required": true, "nullable": false,
                "items": { "type": "object", "nullable": false, "fields": {
                  "scheme": { "type": "string", "required": true, "nullable": false },
                  "value": { "type": "string", "required": true, "nullable": false }
                }}
              }
            }
            """),
    };

    internal static ForegroundCapture Create(
        EntityId observerId, LocalApplication application, ForegroundApplicationReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        var content = new ForegroundApplicationContent
        {
            Id = EntityId.New(),
            LocalApplicationId = application.Id,
            Name = reading.Name,
        };
        var time = Microseconds(reading.ObservedAt);
        return new ForegroundCapture(
            new ObserverEntity { Id = observerId, Name = "macOS foreground state observer" },
            application,
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

    internal ForegroundCapture Through(DateTimeOffset time) => this with
    {
        Observation = new Observation
        {
            Id = Observation.Id,
            ObserverId = Observation.ObserverId,
            ContentId = Observation.ContentId,
            SchemaId = Observation.SchemaId,
            StartAt = Observation.StartAt,
            EndAt = Microseconds(time),
            TimeZone = Observation.TimeZone,
        },
    };

    private static DateTimeOffset Microseconds(DateTimeOffset time) =>
        new(time.UtcTicks - time.UtcTicks % 10, TimeSpan.Zero);

    internal EntitySubmission[] Submissions() =>
        [
            new EntitySubmission(Observer.Id.Value, "observer", "entities/observers", Body(new { references = new { }, properties = new { Observer.Name } })),
            new EntitySubmission(Schema.Id.Value, "entity_schema", "entities/schemas",
                Body(new { references = new { }, properties = new { Schema.Name, Schema.ResourceName, Schema.Fields } })),
            new EntitySubmission(ApplicationSchema.Id.Value, "entity_schema", "entities/schemas",
                Body(new { references = new { }, properties = new
                    { ApplicationSchema.Name, ApplicationSchema.ResourceName, ApplicationSchema.Fields } })),
            new EntitySubmission(Application.Id.Value, "entity", $"entities/{ApplicationSchema.ResourceName}",
                Body(new { references = new { }, properties = new { Application.Identifiers } })),
            new EntitySubmission(Content.Id.Value, "entity", $"entities/{Schema.ResourceName}",
                Body(new { references = new { localApplicationId = Content.LocalApplicationId.Value }, properties = new { Content.Name } })),
            new EntitySubmission(Observation.Id.Value, "observation", "entities/observations", Body(new
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
        ];

    private static JsonElement Body<T>(T value) =>
        JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
}
