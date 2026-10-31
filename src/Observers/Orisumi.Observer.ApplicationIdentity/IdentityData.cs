using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.ApplicationIdentity;

internal sealed record ApplicationIdentifier(string Scheme, string Value);
internal sealed record Product(Guid EntityId, string? Name);
internal sealed record Membership(EntityVersion Version, Guid IdentifierId, Guid ProductId);
internal sealed record DeniedConclusion(Guid ProductId, string? Name, string? Text);
internal sealed record Denial(EntityVersion Version, Guid IdentifierId, DeniedConclusion Conclusion);
internal sealed record MembershipContent(Guid IdentifierId, object ProductId);
internal sealed record IdentificationInput(string Platform, ApplicationIdentifier Identifier,
    ApplicationIdentifier[][] Applications, string[] Names, ProductCandidate[] Products, DeniedConclusion[] Denied);
internal sealed record ProductCandidate(Guid EntityId, string? Name, ApplicationIdentifier[] Identifiers);
internal sealed record IdentificationDecision(string Kind, Guid? ProductId = null, string? Name = null);

internal interface IApplicationModel
{
    bool IsConfigured { get; }
    Task<IdentificationDecision> IdentifyAsync(IdentificationInput input, CancellationToken token);
}

internal static class IdentityData
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static readonly EntityId ProductSchemaId = new(Guid.Parse("019e1bbb-ec00-7000-8000-000000000004"));
    internal static readonly EntityId ObserverSchemaId = new(Guid.Parse("019e1bbb-ec00-7000-8000-000000000005"));
    internal static readonly EntityId PeriodSchemaId = new(Guid.Parse("019e1bbb-ec00-7000-8000-000000000001"));
    internal static readonly Guid MembershipSchemaId = Guid.Parse("019e1bbb-ec00-7000-8000-00000000000c");
    internal static JsonElement Fields(EntityVersion version) => ((Observation)version.Content).Fields;
    internal static Guid[] Identifiers(EntityVersion version) => Fields(version).GetProperty("identifierIds").EnumerateArray().Select(v => v.GetGuid()).ToArray();
    internal static readonly SchemaDefinition[] Definitions = SchemaDefinition.Load(typeof(IdentityData).Assembly,
        "Orisumi.Observer.ApplicationIdentity.schemas.json");
}
