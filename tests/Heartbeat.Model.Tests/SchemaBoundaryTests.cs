using System.Globalization;
using System.Text.Json;
using Heartbeat.Application.Entities;
using Heartbeat.Core;

namespace Heartbeat.Model.Tests;

public sealed class SchemaBoundaryTests
{
    [Test]
    [Arguments("9223372036854775807", true)]
    [Arguments("-9223372036854775808", true)]
    [Arguments("9223372036854775808", false)]
    [Arguments("-9223372036854775809", false)]
    [Arguments("1.000e3", true)]
    [Arguments("1000e-3", true)]
    [Arguments("1001e-3", false)]
    [Arguments("0e99999999999999999999", true)]
    [Arguments("1e99999999999999999999", false)]
    public async Task IntegersFollowMathematicalValueWithinInt64(string json, bool valid)
    {
        using var document = JsonDocument.Parse(json);
        await Assert.That(EntitySchemaRules.TryInteger(document.RootElement, out _)).IsEqualTo(valid);
    }

    [Test]
    public async Task EquivalentIntegerFormsAgreeAcrossDeterministicSamples()
    {
        for (var value = -1000; value <= 1000; value++)
        {
            var plain = value.ToString(CultureInfo.InvariantCulture);
            foreach (var raw in new[] { plain, plain + ".0", plain + "e0", plain + "00e-2" })
            {
                // JSON does not permit a second leading zero for zero.
                var text = value == 0 && raw == "000e-2" ? "0e-2" : raw;
                using var json = JsonDocument.Parse(text);
                await Assert.That(EntitySchemaRules.TryInteger(json.RootElement, out var parsed)).IsTrue();
                await Assert.That(parsed).IsEqualTo((long)value);
            }
        }
    }

    [Test]
    [Arguments(false, false, "{}", true)]
    [Arguments(false, false, "{\"v\":null}", false)]
    [Arguments(false, true, "{\"v\":null}", true)]
    [Arguments(true, true, "{}", false)]
    [Arguments(true, true, "{\"v\":null}", true)]
    public async Task MissingAndNullAreIndependent(bool required, bool nullable, string payload, bool expected)
    {
        using var fields = JsonDocument.Parse(JsonSerializer.Serialize(new { v = new { type = "string", required, nullable } }));
        using var refs = JsonDocument.Parse("{}");
        using var properties = JsonDocument.Parse(payload);
        await Assert.That(EntitySchemaRules.ValidEntity(fields.RootElement, refs.RootElement, properties.RootElement)).IsEqualTo(expected);
    }

    [Test]
    public async Task IndependentObservationsMayShareContentWithoutSharingIdentity()
    {
        var observer = EntityId.New();
        var content = EntityId.New();
        var schema = EntityId.New();
        var first = new Observation { Id = EntityId.New(), ObserverId = observer, ContentId = content, SchemaId = schema };
        var second = new Observation { Id = EntityId.New(), ObserverId = observer, ContentId = content, SchemaId = schema };
        await Assert.That(first.Id == second.Id).IsFalse();
        await Assert.That(first.GetReferences().SequenceEqual(second.GetReferences())).IsTrue();
        await Assert.That(first.StartAt is null && first.EndAt is null && first.TimeZone is null).IsTrue();
    }
}
