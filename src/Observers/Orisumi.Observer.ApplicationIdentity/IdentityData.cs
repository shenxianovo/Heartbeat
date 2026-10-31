using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.ApplicationIdentity;

internal sealed record ApplicationIdentifier(string Scheme, string Value)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsShared => Scheme is "macos.bundle-id" or "android.package-name";
}

internal sealed record Product(EntityVersion Version, string Name);
internal sealed record Membership(EntityVersion Version, ApplicationIdentifier Identifier, EntityId? ProductId);
internal sealed record ProductContent(string Name);
internal sealed record MembershipContent(ApplicationIdentifier Identifier, Ref ProductVersionId);
internal sealed record IdentificationInput(string Platform, ApplicationIdentifier Identifier,
    ApplicationIdentifier[][] Applications, string[] Names, ProductCandidate[] Products);
internal sealed record ProductCandidate(Guid EntityId, string Name, ApplicationIdentifier[] Identifiers);
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
    internal static ApplicationIdentifier[] Identifiers(EntityVersion version) =>
        ((BusinessContent)version.Content).Fields.GetProperty("identifiers").Deserialize<ApplicationIdentifier[]>(Json)!;
    internal static Product Product(EntityVersion version) => new(version,
        ((BusinessContent)version.Content).Fields.GetProperty("name").GetString()!);
    internal static readonly Guid MembershipSchemaId = Guid.Parse("019e1bbb-ec00-7000-8000-00000000000c");
    internal static readonly SchemaDefinition[] Definitions = SchemaDefinition.Load(typeof(IdentityData).Assembly,
        "Orisumi.Observer.ApplicationIdentity.schemas.json");
}
