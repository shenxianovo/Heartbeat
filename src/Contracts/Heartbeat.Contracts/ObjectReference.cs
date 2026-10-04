using System.Text.Json.Serialization;

namespace Heartbeat.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ObjectScope(string Namespace, string Key);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ObjectReference(string Role, string? Namespace = null, string? Key = null,
    string? Name = null, Guid? Id = null, ObjectScope? Scope = null)
{
    public static ObjectReference[] Normalize(IEnumerable<ObjectReference>? references)
    {
        ArgumentNullException.ThrowIfNull(references);
        var items = references.Select(NormalizeOne)
            .OrderBy(item => item.Scope?.Namespace, StringComparer.Ordinal)
            .ThenBy(item => item.Scope?.Key, StringComparer.Ordinal)
            .ThenBy(item => item.Namespace, StringComparer.Ordinal)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .ThenBy(item => item.Id).ThenBy(item => item.Role, StringComparer.Ordinal).ToArray();
        if (items.Length > 64) throw new ArgumentException("A Record can reference at most 64 objects.");
        if (items.Select(item => (item.Role, item.Id, item.Scope, item.Namespace, item.Key)).Distinct().Count() != items.Length)
            throw new ArgumentException("Duplicate object references are not allowed.");
        ValidateNames(items);
        ValidateScopes(items);
        return items;
    }

    private static void ValidateNames(ObjectReference[] items)
    {
        foreach (var group in items.GroupBy(item => (item.Id, item.Scope, item.Namespace, item.Key)))
            if (group.Select(item => item.Name).Distinct().Count() > 1)
                throw new ArgumentException("An object's name must agree across roles within a Record.");
    }

    private static void ValidateScopes(ObjectReference[] items)
    {
        foreach (var scope in items.Select(item => item.Scope).OfType<ObjectScope>().Distinct())
            if (!items.Any(item => item.Scope is null && item.Namespace == scope.Namespace && item.Key == scope.Key))
                throw new ArgumentException("An identification scope must also be referenced in the Record.");
    }

    private static ObjectReference NormalizeOne(ObjectReference item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var role = Token(item.Role, 64);
        if (item.Id is { Version: not 7 }) throw new ArgumentException("An object ID must be a UUID v7.");
        var space = item.Namespace is null ? null : Token(item.Namespace, 128);
        var key = item.Key is null ? null : Text(item.Key, 512);
        if ((space is null) != (key is null) || (item.Id is null && space is null))
            throw new ArgumentException("An object reference requires an ID or a complete native identifier.");
        var scope = NormalizeScope(item.Scope, space is not null);
        var name = string.IsNullOrWhiteSpace(item.Name) ? null : Text(item.Name, 512);
        return new ObjectReference(role, space, key, name, item.Id, scope);
    }

    private static ObjectScope? NormalizeScope(ObjectScope? scope, bool hasIdentifier)
    {
        if (scope is null) return null;
        if (!hasIdentifier) throw new ArgumentException("An identification scope requires a native identifier.");
        return new ObjectScope(Token(scope.Namespace, 128), Text(scope.Key, 512));
    }

    private static string Token(string value, int maximum)
    {
        var text = Text(value, maximum);
        if (text.Any(c => c is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '.' and not '-' and not '_'))
            throw new ArgumentException("Object roles and namespaces must be lowercase identifiers.");
        return text;
    }

    private static string Text(string value, int maximum)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var text = value.Trim();
        if (text.Length > maximum) throw new ArgumentException($"Object field exceeds {maximum} characters.");
        return text;
    }
}
