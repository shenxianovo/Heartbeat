using System.Text.Json;
using System.Text.Json.Nodes;
using Heartbeat.Core.DTOs.Facts;

namespace Heartbeat.Core.Facts;

/// <summary>Converts pre-object envelopes once at old protocol/cache read boundaries.</summary>
public static class ObservationCompatibility
{
    public static (Guid? CollectorId, ObservationObjectReference? Foi, List<FactRelationSnapshot> Relations) Convert(
        Guid? observer, FactTarget? target, JsonElement payload, IReadOnlyDictionary<string, string>? dimensions = null)
    {
        ObservationObjectReference? foi = null, machine = null, app = null;
        switch (target?.Kind)
        {
            case "device": foi = machine = new("machine", ObservationObjectScopes.Machine, target.Reference); break;
            case "application-context":
                var context = ApplicationContextReference.Parse(target.Reference);
                machine = new("machine", ObservationObjectScopes.Machine, context.DeviceReference);
                foi = app = new("app", ObservationObjectScopes.AppIdentity, context.AppIdentityKey);
                break;
            case "account":
                var account = ServiceAccountReference.Parse(target.Reference);
                foi = new("account", account.ServiceKey, account.ServiceAccountId); break;
            case "person": foi = new("person", ObservationObjectScopes.Person, PersonReference.Parse(target.Reference).Id.ToString("D")); break;
            case not null: throw new ArgumentException("Unknown historical Fact Target.");
        }
        if (machine is not null && app is null)
        {
            var key = dimensions?.GetValueOrDefault("appIdentityKey");
            if (key is null && payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("appIdentityKey", out var property) && property.ValueKind == JsonValueKind.String)
                key = property.GetString();
            if (key is not null) app = new("app", ObservationObjectScopes.AppIdentity, key);
        }
        return (observer, foi, machine is not null && app is not null ? [ObservedOn(machine, app)] : []);
    }

    public static (Guid? CollectorId, ObservationObjectReference? Foi, List<FactRelationSnapshot> Relations) FromStream(
        string source, string subjectKind, string subjectReference, Guid instanceId, JsonElement payload,
        IReadOnlyDictionary<string, string>? dimensions = null)
    {
        if (source == "vrchat.account" && subjectKind == "account")
            return (instanceId, new("account", "vrchat", subjectReference), []);
        if (subjectKind != "machine") return (null, null, []);
        if (source == "browser" && dimensions?.GetValueOrDefault("appIdentityKey") is { } appKey)
        {
            var observer = BrowserFactAttribution.Observer(dimensions.GetValueOrDefault("externalHostIdentity"));
            return Convert(observer, new ApplicationContextReference(subjectReference, appKey).ToTarget(), payload, dimensions);
        }
        return Convert(source == "system" ? instanceId : null, new FactTarget("device", subjectReference), payload, dimensions);
    }

    public static FactRelationSnapshot ObservedOn(ObservationObjectReference machine, ObservationObjectReference app) =>
        new("observed-on", [new("device", machine), new("app", app)]);

    public static void ReadOldEnvelope(JsonObject fact, bool camelCase, IReadOnlyDictionary<string, string>? dimensions = null)
    {
        string Name(string value) => camelCase ? char.ToLowerInvariant(value[0]) + value[1..] : value;
        if (fact.ContainsKey(Name("Relations"))) return;
        var targetNode = fact[Name("Target")];
        var observerNode = fact[Name("ObserverId")];
        if (targetNode is null && observerNode is null) { fact.Remove(Name("ObserverId")); fact.Remove(Name("Target")); return; }
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = camelCase ? JsonNamingPolicy.CamelCase : null };
        var converted = Convert(observerNode?.GetValue<Guid>(), targetNode?.Deserialize<FactTarget>(options),
            fact[Name("Payload")] is { } payload ? JsonSerializer.SerializeToElement(payload) : default, dimensions);
        fact.Remove(Name("ObserverId")); fact.Remove(Name("Target"));
        fact[Name("CollectorId")] = converted.CollectorId is { } id ? JsonValue.Create(id) : null;
        fact[Name("Foi")] = JsonSerializer.SerializeToNode(converted.Foi, options);
        fact[Name("Relations")] = JsonSerializer.SerializeToNode(converted.Relations, options);
    }
}

public static class ObservationContent
{
    public static bool Equal(List<FactRelationSnapshot>? first, List<FactRelationSnapshot>? second) =>
        JsonElement.DeepEquals(JsonSerializer.SerializeToElement(first), JsonSerializer.SerializeToElement(second));

    public static List<FactRelationSnapshot> Copy(List<FactRelationSnapshot>? relations) =>
        relations?.Select(relation => new FactRelationSnapshot(relation.Kind, [.. relation.Members])).ToList() ?? [];

    public static bool Valid(Guid? collector, ObservationObjectReference? foi, List<FactRelationSnapshot>? relations) =>
        collector != Guid.Empty && (foi is null || ValidObject(foi)) && relations is not null &&
        relations.All(r => !string.IsNullOrWhiteSpace(r.Kind) && r.Kind.Length <= 128 && r.Members is { Count: >= 2 } &&
            r.Members.All(m => !string.IsNullOrWhiteSpace(m.Role) && m.Role.Length <= 128 && ValidObject(m.Object)) &&
            r.Members.Select(m => m.Role).Distinct(StringComparer.Ordinal).Count() == r.Members.Count);

    private static bool ValidObject(ObservationObjectReference? value) => value is not null &&
        value.Kind is "machine" or "app" or "account" or "person" &&
        !string.IsNullOrWhiteSpace(value.Scope) && value.Scope.Length <= 128 &&
        !string.IsNullOrWhiteSpace(value.Key) && value.Key.Length <= 8192;
}
