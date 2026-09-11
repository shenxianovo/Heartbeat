using System.Text.Json;
using Heartbeat.Core.DTOs.Facts;

namespace Heartbeat.Core.Facts;

/// <summary>Native shape validation shared by offline custody and Analytics.</summary>
public static class ObservationValidation
{
    public static string? Validate(ObservationSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot is null || snapshot.Id == Guid.Empty || snapshot.Kind is not ("segment" or "event") ||
            snapshot.CollectorId == Guid.Empty || snapshot.Foi is null || snapshot.Aspect is null || !FactAspects.IsValid(snapshot.Aspect) ||
            snapshot.Revision is <= 0 or > 9_007_199_254_740_991 || snapshot.Relations is null ||
            snapshot.Source is { } source && (string.IsNullOrWhiteSpace(source) || source.Length > 64 || source != source.Trim()))
            return "Observation requires valid Id, Kind, Collector, FOI, Aspect and Revision.";
        if (ValidateReferences(snapshot) is { } referenceError) return referenceError;
        if (snapshot.Kind == "segment")
        {
            if (snapshot.Start is not { } start || snapshot.End is not { } end || snapshot.OccurredAt is not null ||
                start == DateTimeOffset.MinValue || end == DateTimeOffset.MinValue ||
                start.Offset != TimeSpan.Zero || end.Offset != TimeSpan.Zero || start > end || end > now.AddMinutes(5))
                return "Segment requires a valid UTC start/end interval.";
        }
        else if (snapshot.OccurredAt is not { } at || snapshot.Start is not null || snapshot.End is not null ||
            at == DateTimeOffset.MinValue || at.Offset != TimeSpan.Zero || at > now.AddMinutes(5))
            return "Event requires a valid UTC occurredAt time.";
        if (snapshot.Result is not { } result || result.ValueKind == JsonValueKind.Null)
            return "Observation requires Result.";
        return FactJson.Validate(result);
    }

    private static string? ValidateReferences(ObservationSnapshot snapshot)
    {
        if (snapshot.Relations.Count > 8) return "Too many observation relations.";
        var references = new List<ObservationObjectReference> { snapshot.Foi! };
        var relationKinds = new HashSet<string>(StringComparer.Ordinal);
        var objectsByRole = new Dictionary<string, ObservationObjectReference>(StringComparer.Ordinal);
        foreach (var relation in snapshot.Relations)
        {
            if (relation?.Members is null || relation.Kind is null || !relationKinds.Add(relation.Kind))
                return "Invalid or duplicate observation relation.";
            var roles = relation.Kind switch
            {
                "observed-on" or "installed-on" => new[] { "app", "device" },
                "application-account-use" => new[] { "account", "app", "device" },
                _ => []
            };
            if (roles.Length == 0 || relation.Members.Count != roles.Length ||
                !relation.Members.Select(member => member?.Role).Order().SequenceEqual(roles))
                return "Relation members do not match its contract.";
            foreach (var member in relation.Members)
            {
                if (member.Object is null || member.Object.Kind != (member.Role == "device" ? "machine" : member.Role))
                    return "Relation role does not match the Object kind.";
                // App aliases are resolved only by Analytics; offline validation must not guess identity.
                if (objectsByRole.TryGetValue(member.Role, out var previous) && previous != member.Object && member.Role != "app")
                    return "One Fact cannot claim conflicting Objects for the same relation role.";
                objectsByRole[member.Role] = member.Object;
                references.Add(member.Object);
            }
            if (!relation.Members.Any(member => member.Object == snapshot.Foi) && snapshot.Foi!.Kind != "app")
                return "A Fact relation must include its directly observed Object.";
        }
        foreach (var reference in references)
        {
            if (reference is null || reference.Kind is not ("machine" or "app" or "account" or "person") ||
                string.IsNullOrWhiteSpace(reference.Scope) || reference.Scope.Length > 128 || reference.Scope != reference.Scope.Trim() ||
                string.IsNullOrWhiteSpace(reference.Key) || reference.Key.Length > 1024 || reference.Key != reference.Key.Trim())
                return "Invalid Object reference.";
            switch (reference.Kind)
            {
                case "machine":
                    if (reference.Scope != ObservationObjectScopes.Machine || reference.Key.Length > 256 || reference.Key.StartsWith("subject:", StringComparison.Ordinal))
                        return "A machine requires an observed device identity.";
                    break;
                case "app":
                    if (reference.Scope == ObservationObjectScopes.AppIdentity)
                    {
                        try { _ = AppIdentityKeys.Normalize(reference.Key); }
                        catch (ArgumentException error) { return error.Message; }
                    }
                    else if (reference.Scope != ObservationObjectScopes.App || reference.Key.Length > 128)
                        return "Invalid App identity scope or product key.";
                    break;
                case "person":
                    if (reference.Scope != ObservationObjectScopes.Person || !Guid.TryParse(reference.Key, out var person) || person == Guid.Empty)
                        return "Person requires its established reference.";
                    break;
                case "account":
                    if (reference.Scope == "vrchat" && !ServiceAccountReference.IsVRChatAccountId(reference.Key))
                        return "A native VRChat observation requires its real account identity.";
                    break;
            }
        }
        return null;
    }
}
