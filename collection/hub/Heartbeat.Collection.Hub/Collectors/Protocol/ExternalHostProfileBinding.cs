using System.Text.Json;
using System.Text.RegularExpressions;

namespace Heartbeat.Collection.Hub.Collectors.Protocol;

/// <summary>A local capability for one Desktop Profile; never a public Package reference.</summary>
public sealed record ExternalHostProfileBinding(string ProfileId, string Token, int Port)
{
    public const string FileName = "external-host-binding.json";
    public string RoutePrefix => $"/v1/collector-bindings/{ProfileId}/{Token}";

    public void Validate()
    {
        if (!Regex.IsMatch(ProfileId, "\\A[0-9a-f]{32}\\z") ||
            !Regex.IsMatch(Token, "\\A[0-9a-f]{64}\\z") || Port is < 1024 or > 65535)
            throw new InvalidDataException("The ExternalHost Profile binding is invalid; refusing to replace its identity.");
    }

    public static ExternalHostProfileBinding Read(string directory)
    {
        var result = JsonSerializer.Deserialize<ExternalHostProfileBinding>(
            File.ReadAllText(Path.Combine(directory, FileName)), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("The ExternalHost Profile binding is missing.");
        result.Validate();
        return result;
    }
}
