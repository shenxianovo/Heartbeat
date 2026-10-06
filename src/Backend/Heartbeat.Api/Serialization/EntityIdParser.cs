using Heartbeat.Core;
using System.Text.RegularExpressions;

namespace Heartbeat.Api.Serialization;

internal static partial class EntityIdParser
{
    internal const string Pattern = "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-7[0-9a-fA-F]{3}-[89aAbB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$";

    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant)]
    private static partial Regex UuidPattern();

    internal static bool TryParse(string? text, out EntityId entityId)
    {
        entityId = default;
        if (text is not { Length: 36 } || !UuidPattern().IsMatch(text)
            || !Guid.TryParseExact(text, "D", out var id))
        {
            return false;
        }

        entityId = new EntityId(id);
        return true;
    }
}
