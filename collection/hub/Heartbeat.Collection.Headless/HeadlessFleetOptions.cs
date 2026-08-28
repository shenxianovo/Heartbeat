using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Heartbeat.Collection.Hub.Collectors.Runtime;

namespace Heartbeat.Collection.Headless;

public sealed class HeadlessFleetOptions
{
    public required string ApiKey { get; init; }
    public required string DataDirectory { get; init; }
    public int UploadIntervalSeconds { get; init; } = 60;
    public string ListenUrl { get; init; } = "http://0.0.0.0:8080";
    public required HeadlessManagementOptions Management { get; init; }
    public required IReadOnlyList<HeadlessManagedInstanceOptions> Instances { get; init; }

    public static HeadlessFleetOptions Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var rootNode = JsonNode.Parse(File.ReadAllBytes(fullPath)) as JsonObject
                       ?? throw new JsonException("Headless Hub configuration must be a JSON object.");
        if (rootNode["instances"] is JsonArray instances)
        {
            foreach (var instance in instances.OfType<JsonObject>())
            {
                if (!instance.TryGetPropertyValue("configSchemaVersion", out var legacyVersion))
                    continue;
                if (instance.ContainsKey("configVersion"))
                    throw new JsonException(
                        "Headless Hub configuration contains both configSchemaVersion and configVersion.");
                instance.Remove("configSchemaVersion");
                instance["configVersion"] = legacyVersion?.DeepClone();
            }
        }
        var options = rootNode.Deserialize<HeadlessFleetOptions>(new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters = { new JsonStringEnumConverter() }
        }) ?? throw new JsonException("Headless Hub configuration is null.");
        var root = Path.GetDirectoryName(fullPath)!;
        return options.WithResolvedPaths(
            Resolve(root, options.DataDirectory),
            options.Instances.Select(instance => instance with
            {
                PackageDirectory = Resolve(root, instance.PackageDirectory)
            }).ToArray());
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApiKey)) throw new InvalidOperationException("apiKey is required.");
        if (string.IsNullOrWhiteSpace(DataDirectory)) throw new InvalidOperationException("dataDirectory is required.");
        if (UploadIntervalSeconds <= 0) throw new InvalidOperationException("uploadIntervalSeconds must be positive.");
        if (!Uri.TryCreate(ListenUrl, UriKind.Absolute, out _)) throw new InvalidOperationException("listenUrl must be absolute.");
        ArgumentNullException.ThrowIfNull(Management);
        Management.Validate();
        if (Instances is null || Instances.Count == 0) throw new InvalidOperationException("instances must not be empty.");
        foreach (var instance in Instances) instance.Validate();
        var duplicate = Instances.GroupBy(instance => instance.InstanceKey, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"instanceKey '{duplicate.Key}' is configured more than once.");
    }

    private HeadlessFleetOptions WithResolvedPaths(
        string dataDirectory,
        IReadOnlyList<HeadlessManagedInstanceOptions> instances) => new()
        {
            ApiKey = ApiKey,
            DataDirectory = dataDirectory,
            UploadIntervalSeconds = UploadIntervalSeconds,
            ListenUrl = ListenUrl,
            Management = Management,
            Instances = instances
        };

    private static string Resolve(string root, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
}

public sealed record HeadlessManagedInstanceOptions
{
    public required string InstanceKey { get; init; }
    public required string PackageDirectory { get; init; }
    public required Guid SubjectId { get; init; }
    public SubjectKind SubjectKind { get; init; } = SubjectKind.Account;
    public string SubjectName { get; init; } = "Managed account";
    public int ConfigVersion { get; init; } = 1;
    public JsonElement Config { get; init; } = JsonDocument.Parse("{}").RootElement.Clone();
    public int StartupTimeoutSeconds { get; init; } = 30;
    public int DrainGraceSeconds { get; init; } = 10;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(InstanceKey) ||
            InstanceKey.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
            throw new InvalidOperationException(
                "instanceKey must contain only ASCII letters, digits, '.', '_' or '-'.");
        if (string.IsNullOrWhiteSpace(PackageDirectory)) throw new InvalidOperationException("packageDirectory is required.");
        if (SubjectId == Guid.Empty) throw new InvalidOperationException("subjectId must not be empty.");
        if (!Enum.IsDefined(SubjectKind)) throw new InvalidOperationException("subjectKind is invalid.");
        if (string.IsNullOrWhiteSpace(SubjectName)) throw new InvalidOperationException("subjectName is required.");
        if (ConfigVersion <= 0 || StartupTimeoutSeconds <= 0 || DrainGraceSeconds <= 0)
            throw new InvalidOperationException("Instance numeric settings must be positive.");
        if (Config.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("config must contain a JSON value.");
    }
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
