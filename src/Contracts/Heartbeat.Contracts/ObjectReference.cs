using System.Text.Json.Serialization;

namespace Heartbeat.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ObjectReference(string Role, string Namespace, string Key, string? Name = null)
{
    public static ObjectReference[] Normalize(IEnumerable<ObjectReference>? references)
    {
        ArgumentNullException.ThrowIfNull(references);
        var items = references.Select(item =>
        {
            ArgumentNullException.ThrowIfNull(item);
            var role = Token(item.Role, 64);
            var space = Token(item.Namespace, 128);
            var key = Text(item.Key, 512);
            var name = string.IsNullOrWhiteSpace(item.Name) ? null : Text(item.Name, 512);
            return new ObjectReference(role, space, key, name);
        }).OrderBy(item => item.Namespace, StringComparer.Ordinal)
          .ThenBy(item => item.Key, StringComparer.Ordinal)
          .ThenBy(item => item.Role, StringComparer.Ordinal).ToArray();
        if (items.Length > 64) throw new ArgumentException("A Record can reference at most 64 objects.");
        if (items.Select(item => (item.Role, item.Namespace, item.Key)).Distinct().Count() != items.Length)
            throw new ArgumentException("Duplicate object references are not allowed.");
        foreach (var group in items.GroupBy(item => (item.Namespace, item.Key)))
            if (group.Select(item => item.Name).Distinct().Count() > 1)
                throw new ArgumentException("An object's name must agree across roles within a Record.");
        return items;
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
