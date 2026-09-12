namespace Heartbeat.Hub.Host;

public sealed record HubSettings(
    string DatabasePath,
    DeliveryDestination Destination,
    string BackendToken,
    string AccessToken,
    int MaximumRecords,
    TimeSpan UploadInterval)
{
    public static HubSettings Read(IConfiguration configuration)
    {
        var section = configuration.GetSection("Hub");
        var destination = new DeliveryDestination(
            new Uri(Required("BackendUrl"), UriKind.Absolute), Guid.Parse(Required("OwnerId")));
        var backendToken = Required("BackendToken");
        destination.CheckTokenOwner(backendToken);
        var accessToken = Required("AccessToken");
        if (accessToken.Length < 32 || accessToken == backendToken)
        {
            throw new ArgumentException("Hub:AccessToken must be a separate secret of at least 32 characters.");
        }

        var capacity = section.GetValue("MaximumRecords", 10000);
        var seconds = section.GetValue("UploadIntervalSeconds", 5);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        if (seconds is < 1 or > 3600)
        {
            throw new ArgumentOutOfRangeException(nameof(configuration), "Upload interval must be 1 to 3600 seconds.");
        }

        return new HubSettings(Required("DatabasePath"), destination, backendToken, accessToken,
            capacity, TimeSpan.FromSeconds(seconds));

        string Required(string name) => !string.IsNullOrWhiteSpace(section[name])
            ? section[name]!.Trim()
            : throw new ArgumentException($"Missing Hub:{name}.");
    }
}
