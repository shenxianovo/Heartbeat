using System.Text.Json;

namespace Heartbeat.Core.DTOs.Facts;

/// <summary>Offline device/platform identity pair; product resolution belongs to Analytics.</summary>
public sealed record ApplicationContextReference(string DeviceReference, string AppIdentityKey)
{
    public FactTarget ToTarget() => new("application-context", JsonSerializer.Serialize(new[] { DeviceReference, AppIdentityKey }));

    public static ApplicationContextReference Parse(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 8192)
            throw new ArgumentException("Invalid application context reference.");
        try
        {
            var parts = JsonSerializer.Deserialize<string[]>(reference);
            if (parts is not { Length: 2 } || string.IsNullOrWhiteSpace(parts[0]) || parts[0].Length > 256 ||
                string.IsNullOrWhiteSpace(parts[1]) || parts[1].Length > 512)
                throw new ArgumentException("Application context requires device and platform App identity.");
            var key = AppIdentityKeys.Normalize(parts[1]);
            if (!key.StartsWith("mac:", StringComparison.Ordinal) && !key.StartsWith("win:", StringComparison.Ordinal))
                throw new ArgumentException("Application context requires a platform App identity.");
            return new(Guid.TryParse(parts[0], out var device) ? device.ToString("D") : parts[0], key);
        }
        catch (JsonException ex) { throw new ArgumentException("Invalid application context reference.", ex); }
    }
}
