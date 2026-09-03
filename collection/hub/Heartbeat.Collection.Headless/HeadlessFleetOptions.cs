using System.Text.Json;
using System.Text.Json.Serialization;

namespace Heartbeat.Collection.Headless;

/// <summary>Infrastructure-only Hub configuration. Collector Instances live in CollectorRuntime.</summary>
public sealed class HeadlessFleetOptions
{
    public required string ApiKey { get; init; }
    public required string DataDirectory { get; init; }
    public int UploadIntervalSeconds { get; init; } = 60;
    public string ListenUrl { get; init; } = "http://0.0.0.0:8080";
    public string CollectorRegistryUrl { get; init; } =
        "https://heartbeat.shenxianovo.com/collector-registry/v1/";
    public int CollectorStartupTimeoutSeconds { get; init; } = 30;
    public int CollectorDrainGraceSeconds { get; init; } = 10;
    public required HeadlessManagementOptions Management { get; init; }

    public static HeadlessFleetOptions Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var options = JsonSerializer.Deserialize<HeadlessFleetOptions>(
            File.ReadAllBytes(fullPath),
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = false,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
            }) ?? throw new JsonException("Headless Hub configuration is null.");
        var root = Path.GetDirectoryName(fullPath)!;
        return options.WithResolvedDataDirectory(
            Path.GetFullPath(Path.IsPathRooted(options.DataDirectory)
                ? options.DataDirectory
                : Path.Combine(root, options.DataDirectory)));
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApiKey)) throw new InvalidOperationException("apiKey is required.");
        if (string.IsNullOrWhiteSpace(DataDirectory)) throw new InvalidOperationException("dataDirectory is required.");
        if (UploadIntervalSeconds <= 0) throw new InvalidOperationException("uploadIntervalSeconds must be positive.");
        if (!Uri.TryCreate(ListenUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException("listenUrl must be absolute.");
        if (!Uri.TryCreate(CollectorRegistryUrl, UriKind.Absolute, out var registry) ||
            registry.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("collectorRegistryUrl must be an absolute HTTPS URL.");
        if (CollectorStartupTimeoutSeconds <= 0 || CollectorDrainGraceSeconds <= 0)
            throw new InvalidOperationException("Collector timeout settings must be positive.");
        ArgumentNullException.ThrowIfNull(Management);
        Management.Validate();
    }

    private HeadlessFleetOptions WithResolvedDataDirectory(string dataDirectory) => new()
    {
        ApiKey = ApiKey,
        DataDirectory = dataDirectory,
        UploadIntervalSeconds = UploadIntervalSeconds,
        ListenUrl = ListenUrl,
        CollectorRegistryUrl = CollectorRegistryUrl,
        CollectorStartupTimeoutSeconds = CollectorStartupTimeoutSeconds,
        CollectorDrainGraceSeconds = CollectorDrainGraceSeconds,
        Management = Management
    };
}

public sealed class HeadlessManagementOptions
{
    public required string OwnerSubject { get; init; }
    public required string Authority { get; init; }
    public required string Issuer { get; init; }
    public required string ClientId { get; init; }
    public string? Audience { get; init; }
    public bool RequireHttpsMetadata { get; init; } = true;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(OwnerSubject)) throw new InvalidOperationException("management.ownerSubject is required.");
        if (!Uri.TryCreate(Authority, UriKind.Absolute, out _)) throw new InvalidOperationException("management.authority must be absolute.");
        if (!Uri.TryCreate(Issuer, UriKind.Absolute, out _)) throw new InvalidOperationException("management.issuer must be absolute.");
        if (string.IsNullOrWhiteSpace(ClientId)) throw new InvalidOperationException("management.clientId is required.");
    }
}
