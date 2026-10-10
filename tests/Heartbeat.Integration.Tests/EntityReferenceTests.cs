using System.Text.Json;
using Heartbeat.Core;
using Heartbeat.Observers.ForegroundState;

namespace Heartbeat.Integration.Tests;

public sealed class EntityReferenceTests
{
    [Test]
    public async Task ObservationEnumeratesItsTargetsWithoutIncludingItsOwnIdentity()
    {
        var observerId = EntityId.New();
        var contentId = EntityId.New();
        var schemaId = EntityId.New();
        var entity = new Observation
        {
            Id = EntityId.New(),
            ObserverId = observerId,
            ContentId = contentId,
            SchemaId = schemaId,
        };

        var references = entity.GetReferences().ToArray();

        await Assert.That(references.Length).IsEqualTo(3);
        await Assert.That(references.ToHashSet().SetEquals([observerId, contentId, schemaId])).IsTrue();
        await Assert.That(references.Contains(entity.Id)).IsFalse();
    }

    [Test]
    public async Task EntitiesWithoutReferencesDoNotInterpretContentAsEntityIds()
    {
        var value = EntityId.New().Value.ToString();
        IEntity[] entities =
        [
            new Observer { Id = EntityId.New(), Name = value },
            new EntitySchema
            {
                Id = EntityId.New(),
                Name = value,
                ResourceName = "reference-tests",
                Fields = JsonSerializer.SerializeToElement(new { }),
            },
            new ForegroundApplicationContent
            {
                Id = EntityId.New(),
                BundleIdentifier = value,
                Name = value,
                ExecutablePath = value,
            },
        ];

        foreach (var entity in entities)
            await Assert.That(entity.GetReferences().Any()).IsFalse();
    }
}
