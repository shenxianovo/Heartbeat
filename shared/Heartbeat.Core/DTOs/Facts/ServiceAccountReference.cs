using System.Text.Json;
using System.Text.RegularExpressions;

namespace Heartbeat.Core.DTOs.Facts;

/// <summary>Observed service identity, independent of authentication and collector installation.</summary>
public sealed record ServiceAccountReference(string ServiceKey, string ServiceAccountId)
{
    public FactTarget ToTarget() => new("account", JsonSerializer.Serialize(new[] { ServiceKey, ServiceAccountId }));

    public static ServiceAccountReference Parse(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 512)
            throw new ArgumentException("Invalid service account reference.");
        try
        {
            var parts = JsonSerializer.Deserialize<string[]>(reference);
            if (parts is not { Length: 2 } || parts[0] != "vrchat" || !IsVRChatAccountId(parts[1]))
                throw new ArgumentException("Account requires a supported service and observed service account ID.");
            return new(parts[0], parts[1]);
        }
        catch (JsonException ex) { throw new ArgumentException("Invalid service account reference.", ex); }
    }

    public static bool IsVRChatAccountId(string? value) => value is { Length: 40 } &&
        Regex.IsMatch(value, "^usr_[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$");
}
