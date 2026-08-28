using System.Text.Json;

namespace Heartbeat.Collection.Hub.Collectors.Packages;

internal static class FactSchemaContent
{
    public static bool SemanticallyEquals(
        ReadOnlySpan<byte> left,
        ReadOnlySpan<byte> right)
    {
        using var leftDocument = JsonDocument.Parse(left.ToArray());
        using var rightDocument = JsonDocument.Parse(right.ToArray());
        return JsonElement.DeepEquals(
            leftDocument.RootElement,
            rightDocument.RootElement);
    }
}
