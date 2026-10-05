namespace Heartbeat.Hub.Host;

public sealed record HubSettings(
    string DataDirectory,
    DeliveryDestination Destination,
    Uri AuthUrl,
    string ApiKey,
    string AccessToken,
    int MaximumRecords)
{
    public static HubSettings Read(IConfiguration configuration)
    {
        var section = configuration.GetSection("Hub");
        var destination = new DeliveryDestination(
            new Uri(Required("BackendUrl"), UriKind.Absolute), Guid.Parse(Required("OwnerId")));
        var authUrl = new Uri(section["AuthUrl"]?.Trim() ?? "https://auth.shenxianovo.com", UriKind.Absolute);
        var apiKey = Required("ApiKey");
        var accessToken = Required("AccessToken");
        if (accessToken.Length < 32 || accessToken == apiKey)
        {
            throw new ArgumentException("Hub:AccessToken must be a separate secret of at least 32 characters.");
        }

        var capacity = section.GetValue("MaximumRecords", 10000);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        return new HubSettings(Required("DataDirectory"), destination, authUrl, apiKey, accessToken,
            capacity);

        string Required(string name) => !string.IsNullOrWhiteSpace(section[name])
            ? section[name]!.Trim()
            : throw new ArgumentException($"Missing Hub:{name}.");
    }
}
